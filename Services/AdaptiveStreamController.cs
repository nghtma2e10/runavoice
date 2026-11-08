using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services
{
    public sealed class AdaptiveStreamStateInfo
    {
        public int Level { get; set; } = 1;
        public int TargetHeight { get; set; } = 1080;
        public int TargetFps { get; set; } = 60;
        public int JpegQuality { get; set; } = 75;
        public int TargetBitrateKbps { get; set; } = 4000;
        public bool IsAdaptive { get; set; } = true;
        public string Priority { get; set; } = "clarity";
        public string Reason { get; set; } = "Başlatıldı";
        public string Direction { get; set; } = "="; // ↑, ↓, =
        public float WorstLossPct { get; set; } = 0f;
        public int WorstRttMs { get; set; } = 0;
        public int DirectReceiverCount { get; set; } = 0;
    }

    /// <summary>
    /// Ekran Paylaşımı Dinamik Çözünürlük Çift Döngülü Denetleyicisi (Özellik 4):
    /// - İç döngü: Her karede bayt bütçesine göre anlık JPEG kalitesi (q) optimizasyonu [45..88].
    /// - Dış döngü: 1 sn aralıkla doğrudan alıcıların VQ_REPORT raporları ve yerel geri-basınç sinyalleriyle
    ///   kapasite tahmini (B), basamak seçimi (0..6), histerezis (5s) ve basamak geçişleri.
    /// </summary>
    public sealed class AdaptiveStreamController
    {
        private readonly ScreenCaptureService _capture;
        private readonly WebRtcService _webrtc;
        private readonly StorageService _storage;

        private readonly object _stateLock = new();
        private bool _isRunning = false;

        // Tavanlar (Kullanıcının ekran seçicide belirlediği değerler)
        private int _ceilingHeight = 1080;
        private int _ceilingFps = 60;
        private bool _isDataSaver = false;
        private bool _isAdaptiveEnabled = true;
        private string _priority = "clarity"; // clarity | motion

        // Denetleyici Durumu
        private int _targetBitrateKbps = 4000;
        private int _currentLevel = 1;
        private int _currentJpegQuality = 75;
        private int _currentTargetHeight = 1080;
        private int _currentTargetFps = 60;
        private string _lastDecisionReason = "Başlatıldı";
        private string _lastDirection = "=";

        // İç döngü durumu
        private int _sceneChangeDropCount = 0;

        // Dış döngü durumu
        private int _lastDecreaseBitrate = 0;
        private long _freezeProbeUntilTick = 0;
        private long _lastLevelChangeTick = 0;
        private int _consecutiveBadSecs = 0;
        private int _consecutiveGoodSecs = 0;

        // Alıcı raporları (peerKey -> rapor listesi / son rapor)
        private sealed class PeerReportWindow
        {
            public string PeerKey { get; set; } = "";
            public bool ViaDirect { get; set; } = true;
            public VideoQualityReport? LastReport { get; set; }
            public long LastReportTick { get; set; } = 0;
            public readonly List<(long Tick, int Rtt)> RttHistory = new();
        }

        private readonly ConcurrentDictionary<string, PeerReportWindow> _peerReports = new();

        public event Action<AdaptiveStreamStateInfo>? OnStateUpdated;

        public AdaptiveStreamStateInfo CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return new AdaptiveStreamStateInfo
                    {
                        Level = _currentLevel,
                        TargetHeight = _currentTargetHeight,
                        TargetFps = _currentTargetFps,
                        JpegQuality = _currentJpegQuality,
                        TargetBitrateKbps = _targetBitrateKbps,
                        IsAdaptive = _isAdaptiveEnabled,
                        Priority = _priority,
                        Reason = _lastDecisionReason,
                        Direction = _lastDirection,
                        DirectReceiverCount = GetDirectReceiverCount()
                    };
                }
            }
        }

        public AdaptiveStreamController(ScreenCaptureService capture, WebRtcService webrtc, StorageService storage)
        {
            _capture = capture;
            _webrtc = webrtc;
            _storage = storage;

            _capture.OnFrameEncoded += HandleFrameEncoded;
            _webrtc.OnVideoQualityReport += HandleQualityReport;
        }

        public void Start(int ceilingH, int ceilingFps, bool dataSaver, bool adaptiveEnabled, string priority, int initialLevel)
        {
            lock (_stateLock)
            {
                _ceilingHeight = Math.Clamp(ceilingH, 480, 1080);
                _ceilingFps = Math.Clamp(ceilingFps, 15, 60);
                _isDataSaver = dataSaver;
                _isAdaptiveEnabled = adaptiveEnabled;
                _priority = string.Equals(priority, "motion", StringComparison.OrdinalIgnoreCase) ? "motion" : "clarity";

                if (_isDataSaver)
                {
                    _ceilingHeight = Math.Min(_ceilingHeight, 720);
                    _ceilingFps = Math.Min(_ceilingFps, 20);
                }

                _peerReports.Clear();
                _sceneChangeDropCount = 0;
                _consecutiveBadSecs = 0;
                _consecutiveGoodSecs = 0;
                _freezeProbeUntilTick = Environment.TickCount64 + 2000;
                _lastLevelChangeTick = Environment.TickCount64;

                // Başlangıç basamağı
                int startLvl = Math.Clamp(initialLevel, 0, 6);
                int minLvlForCeiling = GetMinLevelForHeight(_ceilingHeight);
                _currentLevel = Math.Max(startLvl, minLvlForCeiling);

                var spec = GetSpecForLevel(_currentLevel, _ceilingHeight, _ceilingFps, _priority);
                _currentTargetHeight = spec.H;
                _currentTargetFps = spec.Fps;
                _targetBitrateKbps = Math.Min(spec.Bitrate, 6000);
                _webrtc.PacerTargetBitrateKbps = _targetBitrateKbps;
                _currentJpegQuality = _currentLevel switch
                {
                    0 => 82,
                    1 => 72,
                    2 => 70,
                    3 => 65,
                    4 => 60,
                    5 => 55,
                    _ => 48
                };

                _lastDecisionReason = "Başlangıç";
                _lastDirection = "=";
                _isRunning = true;

                if (_isAdaptiveEnabled)
                {
                    _capture.SetAdaptiveTarget(_currentTargetHeight, _currentTargetFps, _currentJpegQuality);
                    BroadcastStateMessage("init");
                }
                else
                {
                    _capture.ClearAdaptiveTarget();
                }

                NotifyStateChanged();
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                _isRunning = false;
                _peerReports.Clear();
                _capture.ClearAdaptiveTarget();
            }
        }

        public void UpdateCeiling(int ceilingH, int ceilingFps, bool dataSaver, bool adaptiveEnabled, string priority)
        {
            lock (_stateLock)
            {
                _ceilingHeight = Math.Clamp(ceilingH, 480, 1080);
                _ceilingFps = Math.Clamp(ceilingFps, 15, 60);
                _isDataSaver = dataSaver;
                _isAdaptiveEnabled = adaptiveEnabled;
                _priority = string.Equals(priority, "motion", StringComparison.OrdinalIgnoreCase) ? "motion" : "clarity";

                if (_isDataSaver)
                {
                    _ceilingHeight = Math.Min(_ceilingHeight, 720);
                    _ceilingFps = Math.Min(_ceilingFps, 20);
                }

                if (!_isAdaptiveEnabled)
                {
                    _capture.ClearAdaptiveTarget();
                    NotifyStateChanged();
                    return;
                }

                // Tavan değişiminde anında sıkıştır
                int minLvl = GetMinLevelForHeight(_ceilingHeight);
                if (_currentLevel < minLvl)
                {
                    _currentLevel = minLvl;
                }

                ApplyCurrentLevelSpec("ceiling_update");
            }
        }

        // =====================================================================
        // İÇ DÖNGÜ (Kare Başına Hızlı Bayt Bütçesi)
        // =====================================================================
        private void HandleFrameEncoded(int encodedBytes, int width, int height, long timestampMs)
        {
            if (!_isRunning || !_isAdaptiveEnabled) return;

            int targetBitrate;
            int targetFps;
            lock (_stateLock)
            {
                targetBitrate = _targetBitrateKbps;
                targetFps = Math.Max(15, _currentTargetFps);
            }

            // Kare başına hedef bayt bütçesi = (B * 1000 / 8) / FPS
            int bytesPerFrame = (targetBitrate * 1000 / 8) / targetFps;

            lock (_stateLock)
            {
                if (encodedBytes > (bytesPerFrame * 3.0f))
                {
                    // Sahne değişimi veya ani karmaşık hareket
                    _currentJpegQuality = Math.Max(45, _currentJpegQuality - 8);
                    _sceneChangeDropCount = 2;
                }
                else if (_sceneChangeDropCount > 0)
                {
                    _sceneChangeDropCount--;
                    _currentJpegQuality = Math.Max(45, _currentJpegQuality - 4);
                }
                else if (encodedBytes > (bytesPerFrame * 1.15f))
                {
                    _currentJpegQuality = Math.Max(45, _currentJpegQuality - 4);
                }
                else if (encodedBytes < (bytesPerFrame * 0.75f))
                {
                    _currentJpegQuality = Math.Min(88, _currentJpegQuality + 2);
                }

                _capture.SetAdaptiveTarget(_currentTargetHeight, _currentTargetFps, _currentJpegQuality);
            }
        }

        // =====================================================================
        // DIŞ DÖNGÜ (Saniyelik Kapasite Tahmini ve Basamak Seçimi)
        // =====================================================================
        private void HandleQualityReport(string peerKey, VideoQualityReport report, bool viaDirect)
        {
            if (!_isRunning || !_isAdaptiveEnabled) return;

            var entry = _peerReports.GetOrAdd(peerKey, _ => new PeerReportWindow { PeerKey = peerKey });
            entry.ViaDirect = viaDirect;
            entry.LastReport = report;
            entry.LastReportTick = Environment.TickCount64;

            int currentRtt = _webrtc.GetPeerRtt(peerKey);
            if (currentRtt > 0)
            {
                lock (entry.RttHistory)
                {
                    entry.RttHistory.Add((Environment.TickCount64, currentRtt));
                    entry.RttHistory.RemoveAll(x => Environment.TickCount64 - x.Tick > 10000);
                }
            }
        }

        /// <summary>
        /// MainPage veya zamanlayıcı tarafından her 1000 ms'de bir çağrılır.
        /// </summary>
        public void TickOneSecondOuterLoop()
        {
            if (!_isRunning || !_isAdaptiveEnabled) return;

            lock (_stateLock)
            {
                long nowTick = Environment.TickCount64;

                // Eski raporları temizle (> 3 sn önce)
                foreach (var kvp in _peerReports)
                {
                    if (nowTick - kvp.Value.LastReportTick > 3000)
                    {
                        _peerReports.TryRemove(kvp.Key, out _);
                    }
                }

                var directReceivers = _peerReports.Values.Where(x => x.ViaDirect && x.LastReport != null).ToList();
                int directCount = directReceivers.Count;

                float worstLoss = 0f;
                int worstDeltaRtt = 0;
                float worstDelivery = 1.0f;
                int worstRtt = 0;

                if (directCount > 0)
                {
                    var sortedByLoss = directReceivers.OrderByDescending(r =>
                    {
                        var rep = r.LastReport!;
                        return rep.LossPct > 0 ? (float)(rep.LossPct / 100.0) : (float)(rep.Fd + rep.Gap) / Math.Max(1, rep.Fr + rep.Fd + rep.Gap);
                    }).ToList();

                    var targetReceiver = (directCount >= 3) ? sortedByLoss[1] : sortedByLoss[0];
                    var rep = targetReceiver.LastReport!;

                    worstLoss = rep.LossPct > 0
                        ? (float)(rep.LossPct / 100.0)
                        : (float)(rep.Fd + rep.Gap) / Math.Max(1, rep.Fr + rep.Fd + rep.Gap);

                    int curRtt = _webrtc.GetPeerRtt(targetReceiver.PeerKey);
                    worstRtt = curRtt;
                    int minRtt10s = curRtt;
                    lock (targetReceiver.RttHistory)
                    {
                        if (targetReceiver.RttHistory.Count > 0)
                            minRtt10s = targetReceiver.RttHistory.Min(x => x.Rtt);
                    }
                    worstDeltaRtt = Math.Max(0, curRtt - minRtt10s);

                    // Gerçek alıcı raporu bit hızı ile hedefin karşılaştırılması (Madde 11 Yapılacaklar 5)
                    worstDelivery = (_targetBitrateKbps > 300 && rep.Kbps > 0)
                        ? Math.Clamp((float)rep.Kbps / _targetBitrateKbps, 0.05f, 1.2f)
                        : 1.0f;
                }

                // Yerel geri-basınç
                float localSkipRatio = 0f;
                int totalCapturedInSec = _capture.MeasuredFps + _capture.SkippedFramesInLastSec;
                if (totalCapturedInSec > 0)
                {
                    localSkipRatio = (float)_capture.SkippedFramesInLastSec / totalCapturedInSec;
                }

                // Madde 11 Yapılacaklar 5:
                // kayıp > %3 veya ulaşan < hedefin %80'i 2 ardışık saniye -> bir basamak düş
                bool isBadCondition = directCount > 0 && (worstLoss > 0.03f || worstDelivery < 0.80f || localSkipRatio > 0.20f);
                if (isBadCondition)
                {
                    _consecutiveBadSecs++;
                }
                else
                {
                    _consecutiveBadSecs = 0;
                }

                bool shouldDecrease = _consecutiveBadSecs >= 2;

                // kayıp < %1 ve ulaşan >= hedefin %95'i 6 sn -> bir basamak çık
                bool isGoodCondition = directCount > 0 && worstLoss < 0.01f && worstDelivery >= 0.95f && localSkipRatio < 0.05f;
                if (isGoodCondition)
                {
                    _consecutiveGoodSecs++;
                }
                else
                {
                    _consecutiveGoodSecs = 0;
                }

                bool shouldIncrease = _consecutiveGoodSecs >= 6;

                if (shouldDecrease)
                {
                    _consecutiveBadSecs = 0;
                    _consecutiveGoodSecs = 0;

                    int prevB = _targetBitrateKbps;
                    _targetBitrateKbps = Math.Max(300, (int)(_targetBitrateKbps * 0.75f));
                    _webrtc.PacerTargetBitrateKbps = _targetBitrateKbps;
                    _lastDecreaseBitrate = prevB;
                    _freezeProbeUntilTick = nowTick + 3000;

                    _lastDecisionReason = worstLoss > 0.03f
                        ? $"Paket kaybı (%{(int)(worstLoss * 100)})"
                        : worstDelivery < 0.80f
                            ? $"Teslim oranı düşük (%{(int)(worstDelivery * 100)})"
                            : $"Yerel geri-basınç (%{(int)(localSkipRatio * 100)})";

                    _lastDirection = "↓";

                    // Basamak düşürme HEMEN uygulanır
                    int newLvl = Math.Min(6, _currentLevel + 1);
                    if (newLvl > _currentLevel)
                    {
                        _currentLevel = newLvl;
                        _lastLevelChangeTick = nowTick;
                        ApplyCurrentLevelSpec("congestion_drop");
                    }
                }
                else if (shouldIncrease && nowTick >= _freezeProbeUntilTick)
                {
                    _consecutiveGoodSecs = 0;
                    _consecutiveBadSecs = 0;

                    int targetLevel = Math.Max(GetMinLevelForHeight(_ceilingHeight), _currentLevel - 1);
                    if (targetLevel < _currentLevel)
                    {
                        _currentLevel = targetLevel;
                        _lastLevelChangeTick = nowTick;
                        _targetBitrateKbps = Math.Min(6000, (int)(_targetBitrateKbps * 1.15f) + 150);
                        _webrtc.PacerTargetBitrateKbps = _targetBitrateKbps;
                        _lastDecisionReason = "Kalite artırıldı (hat kararlı)";
                        _lastDirection = "↑";
                        ApplyCurrentLevelSpec("probe_upgrade");
                    }
                }
                else
                {
                    _lastDecisionReason = directCount > 0 ? "Hat kararlı" : "Alıcı bekleniyor";
                    _lastDirection = "=";
                }

                // 1 sn'de bir VQ_STATE yayınla
                BroadcastStateMessage(_lastDecisionReason);
                NotifyStateChanged(worstLoss, worstRtt);
            }
        }

        private void ApplyCurrentLevelSpec(string triggerReason)
        {
            var spec = GetSpecForLevel(_currentLevel, _ceilingHeight, _ceilingFps, _priority);
            _currentTargetHeight = spec.H;
            _currentTargetFps = spec.Fps;
            _targetBitrateKbps = Math.Min(spec.Bitrate, 6000);
            _webrtc.PacerTargetBitrateKbps = _targetBitrateKbps;

            _storage.Settings.LastAdaptiveLevel = _currentLevel;

            _capture.SetAdaptiveTarget(_currentTargetHeight, _currentTargetFps, _currentJpegQuality);
            BroadcastStateMessage(triggerReason);
            NotifyStateChanged();
        }

        private void BroadcastStateMessage(string reason)
        {
            _webrtc.BroadcastVideoQualityState(
                lvl: _currentLevel,
                h: _currentTargetHeight,
                fps: _currentTargetFps,
                q: _currentJpegQuality,
                adaptive: _isAdaptiveEnabled,
                reason: reason
            );
        }

        private void NotifyStateChanged(float worstLoss = 0f, int worstRtt = 0)
        {
            var info = new AdaptiveStreamStateInfo
            {
                Level = _currentLevel,
                TargetHeight = _currentTargetHeight,
                TargetFps = _currentTargetFps,
                JpegQuality = _currentJpegQuality,
                TargetBitrateKbps = _targetBitrateKbps,
                IsAdaptive = _isAdaptiveEnabled,
                Priority = _priority,
                Reason = _lastDecisionReason,
                Direction = _lastDirection,
                WorstLossPct = worstLoss * 100f,
                WorstRttMs = worstRtt,
                DirectReceiverCount = GetDirectReceiverCount()
            };
            OnStateUpdated?.Invoke(info);
        }

        private int GetDirectReceiverCount()
        {
            return _peerReports.Values.Count(x => x.ViaDirect);
        }

        // =====================================================================
        // BASAMAK TABLOSU MATEMATİĞİ (PDF Bölüm 6.3)
        // =====================================================================
        private static int GetMinLevelForHeight(int ceilingH)
        {
            return ceilingH switch
            {
                <= 480 => 6,
                <= 540 => 5,
                <= 720 => 3,
                <= 900 => 2,
                _ => 0
            };
        }

        private static (int H, int Fps, int Bitrate) GetSpecForLevel(int level, int ceilingH, int ceilingFps, string priority)
        {
            bool isMotion = string.Equals(priority, "motion", StringComparison.OrdinalIgnoreCase);

            return level switch
            {
                0 => (ceilingH, ceilingFps, 6000),
                1 => (ceilingH, ceilingFps, 4000),
                2 => (Math.Min(ceilingH, 900), ceilingFps, 2800),
                3 => (Math.Min(ceilingH, 720), ceilingFps, 1800),
                4 => isMotion
                    ? (Math.Min(ceilingH, 540), ceilingFps, 1100)
                    : (Math.Min(ceilingH, 720), Math.Min(ceilingFps, 30), 1100),
                5 => isMotion
                    ? (Math.Min(ceilingH, 480), ceilingFps, 700)
                    : (Math.Min(ceilingH, 540), Math.Min(ceilingFps, 30), 700),
                _ => (Math.Min(ceilingH, 480), Math.Min(ceilingFps, 20), 450)
            };
        }

        private static int SelectLevelForBitrate(int bitrateKbps, string priority)
        {
            if (bitrateKbps >= 6000) return 0;
            if (bitrateKbps >= 4000) return 1;
            if (bitrateKbps >= 2800) return 2;
            if (bitrateKbps >= 1800) return 3;
            if (bitrateKbps >= 1100) return 4;
            if (bitrateKbps >= 700) return 5;
            return 6;
        }
    }
}
