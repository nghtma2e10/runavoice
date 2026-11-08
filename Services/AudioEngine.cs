using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// 48kHz Profesyonel Ses Motoru:
    /// - Evrensel WASAPI Shared Mode Yakalama & Yeniden Örnekleme (32-bit Float / 16-bit PCM / Stereo / 44.1-96kHz uyumlu)
    /// - Mozilla RNNoise C Native + Spektral VAD Gürültü Engelleme + Darbe/Nefes Kalkanı
    /// - Gerçek 5-Bant Biquad Parametrik Ekolayzır & 5 Ses Profili
    /// - VAD / Bas-Konuş (PTT), 30sn Mikrofon Kayıt/Dinleme Testi, "Sesimi Dinle" Canlı Monitör
    /// - Kullanıcı Başına Bağımsız Oynatma Kanalı (%0-%200 Ses Yükseltici + Clarity Boost + Yerel Susturma)
    /// - Kulak Koruyucu Max dB Limiter + Soundpad Karıştırıcı
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public const int SampleRate = 48000;

        // WASAPI paylaşımlı mod tamponları. Önce olay tabanlı (event-driven) düşük gecikmeli mod denenir:
        // cihaz kendi periyodunda (~10 ms) veri ister/verir. Sürücü desteklemezse eski zamanlayıcı moduna dönülür.
        private const int LowLatencyCaptureBufferMs = 20;
        private const int LowLatencyPlaybackBufferMs = 20;
        private const int FallbackCaptureBufferMs = 30;
        private const int FallbackPlaybackBufferMs = 50;

        // Ölçülen değerler (ses gecikmesi dökümünde kullanılır)
        private double _captureChunkMsAvg = -1;
        private volatile int _playbackBufferFrames = -1;
        private volatile int _playbackRate = SampleRate;

        /// <summary>Mikrofon tamponu: yakalama geri çağrısı başına gelen ses süresi (ölçülen ortalama). Yoksa -1.</summary>
        public double MeasuredCaptureBufferMs => _captureChunkMsAvg;

        /// <summary>Hoparlör tamponu: WASAPI'nin ilk doldurmada istediği toplam tampon süresi (ölçülen). Yoksa -1.</summary>
        public double MeasuredPlaybackBufferMs =>
            _lowLatencyOut is { } ll ? ll.BufferMs
            : _playbackBufferFrames > 0 ? _playbackBufferFrames * 1000.0 / Math.Max(8000, _playbackRate) : -1;

        /// <summary>Düşük gecikmeli çıkışta ses kartı kuyruğunun boşaldığı (takılma) sayısı; standart yolda -1.</summary>
        public int PlaybackGlitches => _lowLatencyOut?.Glitches ?? -1;

        /// <summary>Opus çerçeve süresi: kodlayıcı bir çerçeve dolana kadar bekler (paketleme gecikmesi).</summary>
        public double CurrentFrameMs => _targetFrameSamples * 1000.0 / SampleRate;

        /// <summary>Bu eşin sesi için alış tamponunda bekleme süresi (ms); ölçüm yoksa -1.</summary>
        public double GetPeerBufferedMs(string peerKey) =>
            _peerChannels.TryGetValue(peerKey, out var ch) ? ch.AverageBufferedMs : -1;

        public double GetPeerJitterTargetMs(string peerKey) =>
            _peerChannels.TryGetValue(peerKey, out var ch) ? ch.TargetSlackMs : -1;

        public int GetPeerUnderruns(string peerKey) =>
            _peerChannels.TryGetValue(peerKey, out var ch) ? ch.Underruns : 0;

        private readonly StorageService _storage;
        private readonly RnnoiseFilter _rnnoise = new();
        private readonly object _deviceLock = new();

        private WasapiCapture? _capture;
        private WasapiOut? _playback;
        private LowLatencyAudioOutput? _lowLatencyOut;
        // Düşük gecikmenin desteklenmediği veya çalışırken hata verdiği cihazlar (oturum boyunca tekrar denenmez)
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _lowLatencyDisabledForDevice = new();

        /// <summary>Etkin hoparlör çıkış yolu (tanılama/gösterim için).</summary>
        public string ActivePlaybackMode { get; private set; } = "";
        private string _lowLatencyUnavailableReason = "";
        private MasterNativeWaveProvider? _masterMixer;

        // 5-Bant Mikrofon Ekolayzır + Darbe Kalkanı
        private readonly BiquadFilter _hpImpactFilter = new();
        private readonly BiquadFilter _eq80 = new();
        private readonly BiquadFilter _eq300 = new();
        private readonly BiquadFilter _eq1k = new();
        private readonly BiquadFilter _eq35k = new();
        private readonly BiquadFilter _eq8k = new();
        private float _prevImpactSample = 0f;

        // Opus Çıkış Çerçeveleyici (120 / 240 / 480 / 960 örnek @ 48kHz)
        private readonly object _frameLock = new();
        private short[] _outgoingPcmFrame = new short[960];
        private float[] _captureMonoScratch = new float[4096];
        private float[] _captureResampleScratch = new float[4096];
        private int _outgoingFramePos = 0;
        private int _targetFrameSamples = 480; // 10ms default

        // Ses Dengeleme (Loudness Leveler)
        private volatile string _voiceLevelingMode = "normal";
        private volatile float _voiceLevelingTargetDb = -22.0f;

        // 3B Uzamsal Ses (Spatial Audio)
        private volatile bool _spatialAudioEnabled = false;
        private volatile bool _spatialSpeakerMode = false;
        public bool IsSpatialAudioActive => _spatialAudioEnabled;
        public bool IsOutputDeviceMono => _masterMixer?.IsMonoDevice ?? false;

        // Uzak Katılımcı Ses Kanalları (key: peerKey)
        private readonly ConcurrentDictionary<string, PeerAudioChannel> _peerChannels = new();

        // Soundpad / UI Ses Klipleri
        private readonly object _auxLock = new();
        private readonly List<ActiveAuxClip> _localAuxClips = new();
        private readonly List<ActiveAuxClip> _broadcastAuxClips = new();

        // Mikrofon Testi & Canlı Monitör
        private readonly object _micTestLock = new();
        private List<float>? _micTestBuffer;
        private bool _isRecordingMicTest = false;
        private bool _tempCaptureForTest = false;
        private ActiveAuxClip? _activeMicTestPlayback;

        // Durum Bayrakları
        private bool _isRoomActive = false;
        private int _vadHoldSamples = 0;
        private bool _lastLocalSpeaking = false;
        private double _resamplePhase = 0.0;

        public bool IsMuted { get; set; } = false;
        public bool IsDeafened { get; set; } = false;
        public bool IsPttKeyHeld { get; set; } = false;
        public bool IsMicMonitorEnabled { get; set; } = false;
        public bool IsRecordingMicTest => _isRecordingMicTest;

        public bool IsRNNoiseAvailable => _rnnoise.IsAvailable;
        public float CurrentVadProbability => _rnnoise.LastVadProbability;
        public float CurrentInputDb { get; private set; } = -60f;
        public float CurrentVuLevel { get; private set; } = 0f;
        public bool IsLocalSpeaking => _lastLocalSpeaking;

        // Olaylar
        public event Action<float>? OnLocalVuMeter;                  // 0.0 - 1.0
        public event Action<bool>? OnLocalSpeakingChanged;           // Yerel konuşma durumu
        public event Action<string, bool>? OnPeerSpeakingChanged;    // peerKey, isSpeaking
        public event Action<short[], int>? OnOutgoingPcmFrameReady;  // Opus kodlayıcıya gidecek 48kHz mono çerçeve
        public event Action<float>? OnMicTestProgress;               // 0.0 - 1.0 (30sn kayıt ilerlemesi)

        public AudioEngine(StorageService storage)
        {
            _storage = storage;
            ApplySettingsFromStorage();
        }

        public List<AudioDeviceOption> GetInputDevices()
        {
            var list = new List<AudioDeviceOption>
            {
                new AudioDeviceOption { Id = "", Name = "Varsayılan Sistem Mikrofonu" }
            };
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    list.Add(new AudioDeviceOption { Id = dev.ID, Name = dev.FriendlyName });
                }
            }
            catch { }
            return list;
        }

        public List<AudioDeviceOption> GetOutputDevices()
        {
            var list = new List<AudioDeviceOption>
            {
                new AudioDeviceOption { Id = "", Name = "Varsayılan Sistem Hoparlörü / Kulaklığı" }
            };
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    list.Add(new AudioDeviceOption { Id = dev.ID, Name = dev.FriendlyName });
                }
            }
            catch { }
            return list;
        }

        public void ApplySettingsFromStorage()
        {
            var s = _storage.Settings;

            _rnnoise.EngineMode = s.NoiseEngine;
            _rnnoise.SuppressionLevel = s.NoiseSuppressionLevel;

            if (!string.Equals(s.SoundProfile, "custom", StringComparison.OrdinalIgnoreCase))
            {
                var (b80, b300, b1k, b35k, b8k) = BiquadFilter.GetPresetBands(s.SoundProfile);
                s.Eq80Hz = b80;
                s.Eq300Hz = b300;
                s.Eq1kHz = b1k;
                s.Eq3_5kHz = b35k;
                s.Eq8kHz = b8k;
            }

            _hpImpactFilter.Configure(BiquadType.HighPass, SampleRate, s.ImpactGuardEnabled ? 85f : 35f, 0.707f, 0f);
            _eq80.Configure(BiquadType.LowShelf, SampleRate, 80f, 0.8f, s.Eq80Hz);
            _eq300.Configure(BiquadType.PeakingEQ, SampleRate, 300f, 1.0f, s.Eq300Hz);
            _eq1k.Configure(BiquadType.PeakingEQ, SampleRate, 1000f, 1.0f, s.Eq1kHz);
            _eq35k.Configure(BiquadType.PeakingEQ, SampleRate, 3500f, 1.2f, s.Eq3_5kHz);
            _eq8k.Configure(BiquadType.HighShelf, SampleRate, 8000f, 0.8f, s.Eq8kHz);

            int newFrameSamples = s.OpusFrameMs switch
            {
                <= 2.6f => 120, // 2.5 ms
                <= 5.5f => 240, // 5.0 ms
                <= 12f  => 480, // 10.0 ms
                _       => 960  // 20.0 ms
            };

            lock (_frameLock)
            {
                if (_targetFrameSamples != newFrameSamples)
                {
                    _targetFrameSamples = newFrameSamples;
                    _outgoingPcmFrame = new short[newFrameSamples];
                    _outgoingFramePos = 0;
                }
            }

            _masterMixer?.SetCeilingDb(s.MaxOutputCeilingDb);

            _voiceLevelingMode = s.VoiceLevelingMode ?? "normal";
            _voiceLevelingTargetDb = s.VoiceLevelingTargetDb;
            _spatialAudioEnabled = s.SpatialAudioEnabled;
            _spatialSpeakerMode = s.SpatialSpeakerMode;
            foreach (var kvp in _peerChannels)
            {
                var cfg = _storage.GetPeerAudioConfig(kvp.Value.PeerName);
                kvp.Value.ApplyConfig(cfg, _voiceLevelingMode, _voiceLevelingTargetDb, _spatialAudioEnabled, _spatialSpeakerMode);
            }
        }

        public void RestartDevices()
        {
            lock (_deviceLock)
            {
                bool wasCapturing = _capture != null;
                StopCaptureInternal();
                StopPlaybackInternal();
                EnsurePlaybackEngineRunning();
                if (wasCapturing || _isRoomActive || IsMicMonitorEnabled || _isRecordingMicTest)
                {
                    StartCaptureInternal();
                }
            }
        }

        public void StartRoomAudio()
        {
            lock (_deviceLock)
            {
                _isRoomActive = true;
                _tempCaptureForTest = false;
                EnsurePlaybackEngineRunning();
                StartCaptureInternal();
            }
        }

        public void StopRoomAudio()
        {
            lock (_deviceLock)
            {
                _isRoomActive = false;
                _peerChannels.Clear();
                ClearAuxClips();

                if (!IsMicMonitorEnabled && !_isRecordingMicTest)
                {
                    StopCaptureInternal();
                    StopPlaybackInternal();
                }
            }
            UpdateLocalSpeakingState(false);
            OnLocalVuMeter?.Invoke(0f);
        }

        public void SetMicMonitor(bool enabled)
        {
            IsMicMonitorEnabled = enabled;
            lock (_deviceLock)
            {
                if (enabled)
                {
                    EnsurePlaybackEngineRunning();
                    StartCaptureInternal();
                }
                else if (!_isRoomActive && !_isRecordingMicTest)
                {
                    StopCaptureInternal();
                }
            }
        }

        public void StartMicTestRecording()
        {
            lock (_micTestLock)
            {
                _micTestBuffer = new List<float>(SampleRate * 30);
                _isRecordingMicTest = true;
                _activeMicTestPlayback = null;
            }

            lock (_deviceLock)
            {
                EnsurePlaybackEngineRunning();
                if (_capture == null)
                {
                    _tempCaptureForTest = true;
                    StartCaptureInternal();
                }
            }
        }

        public bool StopAndPlayMicTestRecording()
        {
            float[]? recorded = null;
            lock (_micTestLock)
            {
                _isRecordingMicTest = false;
                if (_micTestBuffer != null && _micTestBuffer.Count > SampleRate / 4)
                {
                    recorded = _micTestBuffer.ToArray();
                }
                _micTestBuffer = null;
            }

            lock (_deviceLock)
            {
                if (_tempCaptureForTest && !_isRoomActive && !IsMicMonitorEnabled)
                {
                    _tempCaptureForTest = false;
                    StopCaptureInternal();
                }
            }

            if (recorded == null || recorded.Length == 0) return false;

            var clip = new ActiveAuxClip(recorded);
            lock (_auxLock)
            {
                if (_activeMicTestPlayback != null) _localAuxClips.Remove(_activeMicTestPlayback);
                _activeMicTestPlayback = clip;
                _localAuxClips.Add(clip);
            }
            return true;
        }

        public void StopMicTest()
        {
            lock (_micTestLock)
            {
                _isRecordingMicTest = false;
                _micTestBuffer = null;
            }
            lock (_auxLock)
            {
                if (_activeMicTestPlayback != null)
                {
                    _localAuxClips.Remove(_activeMicTestPlayback);
                    _activeMicTestPlayback = null;
                }
            }
            lock (_deviceLock)
            {
                if (_tempCaptureForTest && !_isRoomActive && !IsMicMonitorEnabled)
                {
                    _tempCaptureForTest = false;
                    StopCaptureInternal();
                }
            }
        }

        public void PlayAuxClip(float[] samples48kMono, bool broadcastToRoom)
        {
            if (samples48kMono == null || samples48kMono.Length == 0) return;
            EnsurePlaybackEngineRunning();

            lock (_auxLock)
            {
                _localAuxClips.Add(new ActiveAuxClip(samples48kMono));
                if (broadcastToRoom && _isRoomActive)
                {
                    _broadcastAuxClips.Add(new ActiveAuxClip(samples48kMono));
                }
            }
        }

        public void ClearAuxClips()
        {
            lock (_auxLock)
            {
                _localAuxClips.Clear();
                _broadcastAuxClips.Clear();
                _activeMicTestPlayback = null;
            }
        }

        public void PushRemotePeerPcm(string peerKey, string peerDisplayName, float[] pcm48kMono, int count)
        {
            if (IsDeafened || count <= 0) return;

            var channel = _peerChannels.GetOrAdd(peerKey, _ =>
            {
                var cfg = _storage.GetPeerAudioConfig(peerDisplayName);
                var ch = new PeerAudioChannel(peerKey, peerDisplayName);
                ch.ApplyConfig(cfg, _voiceLevelingMode, _voiceLevelingTargetDb, _spatialAudioEnabled, _spatialSpeakerMode);
                return ch;
            });

            channel.WriteSamples(pcm48kMono, count);
        }

        public void UpdatePeerAudioSettings(string peerName, PeerAudioConfig config)
        {
            string lower = (peerName ?? "").Trim().ToLowerInvariant();
            foreach (var kvp in _peerChannels)
            {
                if (kvp.Value.PeerName.Trim().ToLowerInvariant() == lower)
                {
                    kvp.Value.ApplyConfig(config, _voiceLevelingMode, _voiceLevelingTargetDb, _spatialAudioEnabled, _spatialSpeakerMode);
                }
            }
        }

        /// <summary>
        /// 3B Uzamsal Ses radarında "Buradan Dinle" testi: seçili açı ve mesafeden kısa, melodik bir ses çalar.
        /// </summary>
        public void PlaySpatialTestTone(float azimuthDeg, float distance)
        {
            const int durationSamples = SampleRate * 250 / 1000;
            float[] stereoSamples = new float[durationSamples * 2];
            var testPanner = new SpatialPanner();
            testPanner.Configure(true, azimuthDeg, distance, _spatialSpeakerMode);

            for (int i = 0; i < durationSamples; i++)
            {
                float t = (float)i / SampleRate;
                float freq = i < (durationSamples / 2) ? 880f : 1320f;
                float env = MathF.Sin(MathF.PI * (float)i / durationSamples); // Yumuşak çan zarfı
                float tone = MathF.Sin(2.0f * MathF.PI * freq * t) * 0.25f * env;
                testPanner.Process(tone, out float outL, out float outR);
                stereoSamples[i * 2] = outL;
                stereoSamples[(i * 2) + 1] = outR;
            }

            lock (_auxLock)
            {
                _localAuxClips.Add(new ActiveAuxClip(stereoSamples, isStereo: true));
            }
        }

        public float GetPeerLevelingGainDb(string peerKey)
        {
            if (string.IsNullOrEmpty(peerKey)) return 0f;
            if (_peerChannels.TryGetValue(peerKey, out var ch))
            {
                return ch.LevelingGainDb;
            }
            return 0f;
        }

        public IReadOnlyDictionary<string, (string Name, float GainDb, bool Speaking)> GetActivePeerLevelingStats()
        {
            var dict = new Dictionary<string, (string, float, bool)>();
            foreach (var kvp in _peerChannels)
            {
                if (kvp.Key == "__self_monitor__") continue;
                dict[kvp.Key] = (kvp.Value.PeerName, kvp.Value.LevelingGainDb, kvp.Value.IsSpeaking);
            }
            return dict;
        }

        public void RemovePeerChannel(string peerKey)
        {
            if (_peerChannels.TryRemove(peerKey, out _))
            {
                OnPeerSpeakingChanged?.Invoke(peerKey, false);
            }
        }

        private void EnsurePlaybackEngineRunning()
        {
            lock (_deviceLock)
            {
                if (_playback != null || _lowLatencyOut != null) return;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    MMDevice? outDev = null;
                    string wantedId = _storage.Settings.OutputDeviceId;
                    if (!string.IsNullOrWhiteSpace(wantedId))
                    {
                        try { outDev = enumerator.GetDevice(wantedId); } catch { outDev = null; }
                    }
                    outDev ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // KRİTİK: Cihazın doğal WASAPI MixFormat'ı (Stereo / 7.1, 44.1k/48k/96k, 32-bit IEEE Float) ile
                    // birebir eşleşen IWaveProvider kullanarak NAudio'nun STA/MTA uyumsuz COM ResamplerDmoStream'ini
                    // tamamen devre dışı bırakıyoruz (sıfır takılma, sıfır cızırtı).
                    WaveFormat nativeMixFmt = outDev.AudioClient.MixFormat;
                    _masterMixer = new MasterNativeWaveProvider(this, nativeMixFmt);
                    _masterMixer.SetCeilingDb(_storage.Settings.MaxOutputCeilingDb);

                    _playbackBufferFrames = -1;
                    _playbackRate = nativeMixFmt.SampleRate;

                    // 1) IAudioClient3 düşük gecikmeli paylaşımlı çıkış (~5 ms periyot, ~10 ms tampon).
                    //    Sürücü desteklemiyorsa aşağıdaki NAudio yoluna (şimdiki davranış) düşülür.
                    if (!_lowLatencyDisabledForDevice.Contains(outDev.ID))
                    {
                        var ll = new LowLatencyAudioOutput(outDev.ID, _masterMixer);
                        if (ll.TryStart(out string llReason))
                        {
                            string failedDeviceId = outDev.ID;
                            ll.Failed += ex =>
                            {
                                // Çalışırken cihaz hatası: bu cihaz için düşük gecikmeyi kapat ve eski yola geç
                                System.Diagnostics.Debug.WriteLine($"[AudioEngine] Low-latency output failed: {ex.Message}");
                                _lowLatencyDisabledForDevice.Add(failedDeviceId);
                                System.Threading.Tasks.Task.Run(() =>
                                {
                                    lock (_deviceLock) { StopPlaybackInternal(); }
                                    EnsurePlaybackEngineRunning();
                                });
                            };
                            _lowLatencyOut = ll;
                            ActivePlaybackMode = $"düşük gecikme modu ({ll.PeriodMs:0.0} ms periyot)";
                            return;
                        }
                        System.Diagnostics.Debug.WriteLine($"[AudioEngine] IAudioClient3 unavailable: {llReason}");
                        _lowLatencyUnavailableReason = llReason;
                        ll.Dispose();
                        _lowLatencyDisabledForDevice.Add(outDev.ID);
                        _masterMixer = new MasterNativeWaveProvider(this, nativeMixFmt);
                        _masterMixer.SetCeilingDb(_storage.Settings.MaxOutputCeilingDb);
                        _playbackBufferFrames = -1;
                    }

                    // 2) Şimdiki davranış: NAudio olay tabanlı paylaşımlı WASAPI (20 ms), o da olmazsa zamanlayıcı (50 ms)
                    ActivePlaybackMode = string.IsNullOrEmpty(_lowLatencyUnavailableReason)
                        ? "standart mod"
                        : _lowLatencyUnavailableReason.Contains("desteklemiyor", StringComparison.OrdinalIgnoreCase)
                            ? "standart mod (bu ses cihazı düşük gecikmeyi desteklemiyor)"
                            : "standart mod (düşük gecikme açılamadı)";
                    try
                    {
                        // Olay tabanlı düşük gecikme: cihaz her periyotta (~10 ms) yalnızca boşalan kısmı ister
                        _playback = new WasapiOut(outDev, AudioClientShareMode.Shared, true, LowLatencyPlaybackBufferMs);
                        _playback.Init(_masterMixer);
                    }
                    catch (Exception lowEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AudioEngine] Low-latency playback failed, fallback: {lowEx.Message}");
                        try { _playback?.Dispose(); } catch { }
                        _masterMixer = new MasterNativeWaveProvider(this, nativeMixFmt);
                        _masterMixer.SetCeilingDb(_storage.Settings.MaxOutputCeilingDb);
                        _playbackBufferFrames = -1;
                        _playback = new WasapiOut(outDev, AudioClientShareMode.Shared, false, FallbackPlaybackBufferMs);
                        _playback.Init(_masterMixer);
                    }
                    _playback.Play();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] Playback init error: {ex.Message}");
                    StopPlaybackInternal();
                }
            }
        }

        private void StartCaptureInternal()
        {
            if (_capture != null) return;
            try
            {
                MMDevice? inDev = null;
                string wantedId = _storage.Settings.InputDeviceId;
                if (!string.IsNullOrWhiteSpace(wantedId))
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        inDev = enumerator.GetDevice(wantedId);
                    }
                    catch { inDev = null; }
                }

                // Cihazın doğal WASAPI paylaşımlı formatında olay tabanlı (düşük gecikmeli) yakalama; sürücü
                // desteklemezse zamanlayıcı tabanlı moda dön. Mikrofonun sesi büyük parçalar hâlinde vermesi
                // karşı tarafta paketlerin toplu gelmesine ve alış tamponunun büyümesine yol açar.
                _captureChunkMsAvg = -1;
                _resamplePhase = 0.0;
                _rnnoise.ResetBuffers();
                try
                {
                    _capture = new WasapiCapture(inDev ?? WasapiCapture.GetDefaultCaptureDevice(), true, LowLatencyCaptureBufferMs);
                    _capture.DataAvailable += OnCaptureDataAvailable;
                    _capture.StartRecording();
                }
                catch (Exception lowEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioEngine] Low-latency capture failed, fallback: {lowEx.Message}");
                    try
                    {
                        if (_capture != null)
                        {
                            _capture.DataAvailable -= OnCaptureDataAvailable;
                            _capture.Dispose();
                        }
                    }
                    catch { }
                    _capture = new WasapiCapture(inDev ?? WasapiCapture.GetDefaultCaptureDevice(), false, FallbackCaptureBufferMs);
                    _capture.DataAvailable += OnCaptureDataAvailable;
                    _capture.StartRecording();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] StartCapture error: {ex.Message}");
            }
        }

        private void StopCaptureInternal()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnCaptureDataAvailable;
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch { _capture = null; }
        }

        private void StopPlaybackInternal()
        {
            var ll = _lowLatencyOut;
            _lowLatencyOut = null;
            if (ll != null) { try { ll.Dispose(); } catch { } }

            try
            {
                if (_playback != null)
                {
                    _playback.Stop();
                    _playback.Dispose();
                    _playback = null;
                }
            }
            catch { _playback = null; }
        }

        private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                if (e.BytesRecorded <= 0 || _capture == null) return;

                // 1. Cihazın doğal formatından (32f/16i, 1-8 kanal, 44.1k/48k/96k) -> 48kHz Mono Float'a dönüştür
                float[] mono48k = ConvertCaptureTo48kMono(e.Buffer, e.BytesRecorded, _capture.WaveFormat, out int count);
                if (count <= 0) return;

                double chunkMs = count * 1000.0 / SampleRate;
                _captureChunkMsAvg = _captureChunkMsAvg < 0 ? chunkMs : _captureChunkMsAvg * 0.95 + chunkMs * 0.05;

                var s = _storage.Settings;
                float inGain = Math.Clamp(s.InputGainPercent / 100f, 0f, 2.5f);

                // 2. Giriş kazancı + Darbe & Nefes Kalkanı (Impact/Breath Slew Limiter + Rumble HPF)
                for (int i = 0; i < count; i++)
                {
                    float x = mono48k[i] * inGain;
                    x = _hpImpactFilter.Process(x);

                    if (s.ImpactGuardEnabled)
                    {
                        float delta = x - _prevImpactSample;
                        const float maxSlew = 0.28f; // Ani masa vurma / klavye tıkırtısı tepe kırpıcı
                        if (delta > maxSlew) x = _prevImpactSample + maxSlew;
                        else if (delta < -maxSlew) x = _prevImpactSample - maxSlew;
                        _prevImpactSample = x;
                    }

                    mono48k[i] = Math.Clamp(x, -1.0f, 1.0f);
                }

                // 3. RNNoise C Native / Çoklu Gürültü Engelleme Motoru
                _rnnoise.ProcessInPlace(mono48k, count);

                // 4. Gerçek 5-Bant Biquad Parametrik Ekolayzır + Yumuşak Limiter
                float sumSq = 0f;
                float maxPeak = 0f;
                for (int i = 0; i < count; i++)
                {
                    float y = mono48k[i];
                    y = _eq80.Process(y);
                    y = _eq300.Process(y);
                    y = _eq1k.Process(y);
                    y = _eq35k.Process(y);
                    y = _eq8k.Process(y);

                    // Doğal doygunluk koruması
                    if (y > 0.96f) y = 0.96f;
                    else if (y < -0.96f) y = -0.96f;

                    mono48k[i] = y;
                    sumSq += y * y;
                    float abs = Math.Abs(y);
                    if (abs > maxPeak) maxPeak = abs;
                }

                float rms = MathF.Sqrt(sumSq / count);
                float db = rms > 1e-5f ? 20f * MathF.Log10(rms) : -65f;
                CurrentInputDb = Math.Clamp(db, -65f, 0f);
                CurrentVuLevel = Math.Clamp(maxPeak * 1.45f, 0f, 1f);

                // 5. 30sn Mikrofon Testi Kaydı (Susturma durumundan bağımsız çalışır ki ayarlarda test edilebilsin)
                if (_isRecordingMicTest)
                {
                    bool finished = false;
                    float progress = 0f;
                    lock (_micTestLock)
                    {
                        if (_micTestBuffer != null)
                        {
                            const int maxSamples = SampleRate * 30;
                            int space = maxSamples - _micTestBuffer.Count;
                            int toCopy = Math.Min(count, space);
                            for (int i = 0; i < toCopy; i++) _micTestBuffer.Add(mono48k[i]);
                            progress = (float)_micTestBuffer.Count / maxSamples;
                            if (_micTestBuffer.Count >= maxSamples) finished = true;
                        }
                    }
                    OnMicTestProgress?.Invoke(progress);
                    if (finished) StopAndPlayMicTestRecording();
                }

                // 6. "Sesimi Dinle" Canlı Kulaklık Monitörü
                if (IsMicMonitorEnabled && !IsDeafened)
                {
                    PushRemotePeerPcm("__self_monitor__", "Sesimi Dinle", mono48k, count);
                }

                // 7. Susturma, Sağırlaştırma ve VAD / PTT Kapısı
                bool gateOpen;
                if (IsMuted || IsDeafened)
                {
                    gateOpen = false;
                    CurrentVuLevel = 0f;
                }
                else if (string.Equals(s.InputMode, "ptt", StringComparison.OrdinalIgnoreCase))
                {
                    gateOpen = IsPttKeyHeld;
                }
                else
                {
                    // VAD eşiği + RNNoise olasılık desteği + 180ms tutma (hold) süresi
                    bool aboveThreshold = (db >= s.VadThresholdDb) &&
                                          (s.NoiseEngine == "off" || _rnnoise.LastVadProbability >= 0.08f || db >= s.VadThresholdDb + 8f);
                    if (aboveThreshold)
                    {
                        _vadHoldSamples = SampleRate * 180 / 1000; // 180ms hold
                        gateOpen = true;
                    }
                    else if (_vadHoldSamples > 0)
                    {
                        _vadHoldSamples = Math.Max(0, _vadHoldSamples - count);
                        gateOpen = true;
                    }
                    else
                    {
                        gateOpen = false;
                    }
                }

                OnLocalVuMeter?.Invoke(gateOpen ? CurrentVuLevel : (IsMuted ? 0f : CurrentVuLevel * 0.35f));
                UpdateLocalSpeakingState(gateOpen && rms > 0.004f);

                // 8. Odadaysak Giden Opus Çerçeveleyiciye + Soundpad Yayınına Karıştır
                if (_isRoomActive)
                {
                    lock (_frameLock)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            float voiceSample = gateOpen ? mono48k[i] : 0f;
                            float auxBroadcast = ReadBroadcastAuxSample();
                            float combined = Math.Clamp(voiceSample + auxBroadcast, -0.98f, 0.98f);

                            _outgoingPcmFrame[_outgoingFramePos++] = (short)(combined * 32767.0f);

                            if (_outgoingFramePos == _targetFrameSamples)
                            {
                                // WebRTC Opus kodlayıcıya tam çerçeveyi teslim et. Abone (EncodeAndBroadcastAudioFrame)
                                // çerçeveyi eşzamanlı olarak kodlayıp bırakır; bu yüzden her çerçevede yeni dizi ayrılmaz.
                                _outgoingFramePos = 0;
                                OnOutgoingPcmFrameReady?.Invoke(_outgoingPcmFrame, _targetFrameSamples);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine] Capture processing error: {ex.Message}");
            }
        }

        private float ReadBroadcastAuxSample()
        {
            lock (_auxLock)
            {
                if (_broadcastAuxClips.Count == 0) return 0f;
                float sum = 0f;
                for (int i = _broadcastAuxClips.Count - 1; i >= 0; i--)
                {
                    var clip = _broadcastAuxClips[i];
                    if (clip.TryReadNext(out float s))
                        sum += s;
                    else
                        _broadcastAuxClips.RemoveAt(i);
                }
                return sum;
            }
        }

        private void UpdateLocalSpeakingState(bool speaking)
        {
            if (_lastLocalSpeaking == speaking) return;
            _lastLocalSpeaking = speaking;
            OnLocalSpeakingChanged?.Invoke(speaking);
        }

        private float[] ConvertCaptureTo48kMono(byte[] buffer, int bytesRecorded, WaveFormat fmt, out int outCount)
        {
            int channels = Math.Max(1, fmt.Channels);
            int bytesPerSample = fmt.BitsPerSample / 8;
            int frameSize = channels * bytesPerSample;
            if (frameSize <= 0)
            {
                outCount = 0;
                return Array.Empty<float>();
            }

            int srcFrames = bytesRecorded / frameSize;
            // Yakalama geri çağrısı ~10 ms'de bir ve tek iş parçacığından gelir: tamponlar her çağrıda
            // yeniden ayrılmaz (saniyede yüzlerce küçük ayırma GC'yi sürekli çalıştırıyordu).
            if (_captureMonoScratch.Length < srcFrames) _captureMonoScratch = new float[srcFrames * 2];
            float[] monoSrc = _captureMonoScratch;

            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT))
            {
                for (int f = 0; f < srcFrames; f++)
                {
                    float sum = 0f;
                    int baseIdx = f * frameSize;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += BitConverter.ToSingle(buffer, baseIdx + c * 4);
                    }
                    monoSrc[f] = sum / channels;
                }
            }
            else if (fmt.BitsPerSample == 16)
            {
                for (int f = 0; f < srcFrames; f++)
                {
                    float sum = 0f;
                    int baseIdx = f * frameSize;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += BitConverter.ToInt16(buffer, baseIdx + c * 2) / 32768.0f;
                    }
                    monoSrc[f] = sum / channels;
                }
            }
            else if (fmt.BitsPerSample == 24)
            {
                for (int f = 0; f < srcFrames; f++)
                {
                    float sum = 0f;
                    int baseIdx = f * frameSize;
                    for (int c = 0; c < channels; c++)
                    {
                        int p = baseIdx + c * 3;
                        int sample24 = (buffer[p + 2] << 24) | (buffer[p + 1] << 16) | (buffer[p] << 8);
                        sum += (sample24 >> 8) / 8388608.0f;
                    }
                    monoSrc[f] = sum / channels;
                }
            }
            else if (fmt.BitsPerSample == 32)
            {
                for (int f = 0; f < srcFrames; f++)
                {
                    float sum = 0f;
                    int baseIdx = f * frameSize;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += BitConverter.ToInt32(buffer, baseIdx + c * 4) / 2147483648.0f;
                    }
                    monoSrc[f] = sum / channels;
                }
            }

            if (fmt.SampleRate == SampleRate)
            {
                outCount = srcFrames;
                return monoSrc;
            }

            // Doğrusal enterpolasyonlu 48kHz yeniden örnekleyici
            double step = (double)fmt.SampleRate / SampleRate;
            int estimatedOut = (int)Math.Ceiling(srcFrames / step) + 2;
            if (_captureResampleScratch.Length < estimatedOut) _captureResampleScratch = new float[estimatedOut * 2];
            float[] resampled = _captureResampleScratch;
            int written = 0;
            double pos = _resamplePhase;

            while (pos < srcFrames - 1 && written < estimatedOut)
            {
                int idx = (int)pos;
                float frac = (float)(pos - idx);
                resampled[written++] = monoSrc[idx] * (1f - frac) + monoSrc[idx + 1] * frac;
                pos += step;
            }
            while (pos < srcFrames && written < estimatedOut)
            {
                resampled[written++] = monoSrc[(int)pos];
                pos += step;
            }

            _resamplePhase = Math.Max(0.0, pos - srcFrames);
            outCount = written;
            return resampled;
        }

        internal void FillMasterPlaybackBuffer(float[] interleavedStereo, int offset, int frames)
        {
            Array.Clear(interleavedStereo, offset, frames * 2);

            if (IsDeafened) return;

            float masterGain = Math.Clamp(_storage.Settings.OutputMasterPercent / 100f, 0f, 2.0f);

            // 1. Tüm uzak katılımcı kanallarını karıştır (Stereo interleaved: L, R, L, R...)
            foreach (var kvp in _peerChannels)
            {
                var ch = kvp.Value;
                bool speakingChanged = ch.MixInto(interleavedStereo, offset, frames, out bool nowSpeaking);
                if (speakingChanged && ch.PeerKey != "__self_monitor__")
                {
                    OnPeerSpeakingChanged?.Invoke(ch.PeerKey, nowSpeaking);
                }
            }

            // 2. Yerel Soundpad, UI ses efektleri, uzamsal test ve Mikrofon Testi oynatımını ekle
            lock (_auxLock)
            {
                for (int c = _localAuxClips.Count - 1; c >= 0; c--)
                {
                    var clip = _localAuxClips[c];
                    for (int f = 0; f < frames; f++)
                    {
                        if (clip.TryReadNext(out float sL, out float sR))
                        {
                            interleavedStereo[offset + (f * 2)] += sL;
                            interleavedStereo[offset + (f * 2) + 1] += sR;
                        }
                        else
                        {
                            _localAuxClips.RemoveAt(c);
                            break;
                        }
                    }
                }
            }

            // 3. Ana Çıkış Kazancı (her iki kanala eşit)
            if (Math.Abs(masterGain - 1.0f) > 0.001f)
            {
                int total = frames * 2;
                for (int i = 0; i < total; i++) interleavedStereo[offset + i] *= masterGain;
            }
        }

        public void Dispose()
        {
            lock (_deviceLock)
            {
                StopCaptureInternal();
                StopPlaybackInternal();
            }
            _rnnoise.Dispose();
        }

        private sealed class ActiveAuxClip
        {
            private readonly float[] _data;
            private readonly bool _isStereo;
            private int _pos = 0;

            public ActiveAuxClip(float[] data, bool isStereo = false)
            {
                _data = data;
                _isStereo = isStereo;
            }

            public bool TryReadNext(out float sample)
            {
                if (_pos < _data.Length)
                {
                    sample = _data[_pos++];
                    if (_isStereo && _pos < _data.Length) _pos++;
                    return true;
                }
                sample = 0f;
                return false;
            }

            public bool TryReadNext(out float l, out float r)
            {
                if (_isStereo)
                {
                    if (_pos + 1 < _data.Length)
                    {
                        l = _data[_pos++];
                        r = _data[_pos++];
                        return true;
                    }
                }
                else
                {
                    if (_pos < _data.Length)
                    {
                        l = _data[_pos++];
                        r = l;
                        return true;
                    }
                }
                l = 0f;
                r = 0f;
                return false;
            }
        }

        private sealed class PeerAudioChannel
        {
            public string PeerKey { get; }
            public string PeerName { get; }

            private readonly object _lock = new();
            private readonly float[] _ring = new float[SampleRate]; // Max 1s tampon, jitter sönümlemeli
            private int _readPos = 0;
            private int _writePos = 0;
            private int _count = 0;

            private float _volumeGain = 1.0f;
            private bool _localMuted = false;
            private bool _clarityBoost = false;

            private readonly BiquadFilter _clarityHp = new();
            private readonly BiquadFilter _clarityShelf = new();
            private readonly LoudnessLeveler _leveler = new();
            private readonly SpatialPanner _panner = new();

            public float LevelingGainDb => _leveler.CurrentGainDb;
            public bool IsSpeaking => _isSpeaking;

            private bool _isSpeaking = false;
            private int _speakingHoldSamples = 0;

            public PeerAudioChannel(string peerKey, string peerName)
            {
                PeerKey = peerKey;
                PeerName = peerName;
                _clarityHp.Configure(BiquadType.HighPass, SampleRate, 125f, 0.707f, 0f);
                _clarityShelf.Configure(BiquadType.HighShelf, SampleRate, 3200f, 0.85f, 6.5f);
            }

            public void ApplyConfig(PeerAudioConfig cfg, string levelingMode, float levelingTargetDb, bool spatialAudioEnabled, bool spatialSpeakerMode)
            {
                _volumeGain = Math.Clamp(cfg.VolumePercent / 100f, 0f, 2.0f);
                _localMuted = cfg.LocalMuted;
                _clarityBoost = cfg.ClarityBoost;
                bool isSelf = PeerKey == "__self_monitor__";
                _leveler.Configure(isSelf ? "off" : levelingMode, levelingTargetDb, cfg.ExcludeFromLeveling || isSelf);
                _panner.Configure(spatialAudioEnabled && !isSelf, cfg.SpatialAzimuthDeg, cfg.SpatialDistance, spatialSpeakerMode);
            }

            public void WriteSamples(float[] src, int length)
            {
                lock (_lock)
                {
                    _lastWriteTick = Environment.TickCount64;
                    // Jitter birikmesini önle: tampon 120ms'yi (5760 örnek) aşarsa eski kısmı kırp
                    const int maxLatencySamples = SampleRate * 120 / 1000;
                    if (_count + length > maxLatencySamples)
                    {
                        int drop = (_count + length) - (SampleRate * 60 / 1000);
                        if (drop > 0 && drop <= _count)
                        {
                            _readPos = (_readPos + drop) % _ring.Length;
                            _count -= drop;
                        }
                    }

                    for (int i = 0; i < length; i++)
                    {
                        if (_count >= _ring.Length)
                        {
                            _readPos = (_readPos + 1) % _ring.Length;
                            _count--;
                        }
                        _ring[_writePos] = src[i];
                        _writePos = (_writePos + 1) % _ring.Length;
                        _count++;
                    }
                }
            }

            // Alış (jitter) tamponunda bekleyen sesin ortalama süresi. Yalnızca ses akarken güncellenir;
            // karşı taraf susturulmuşken boş tampon ortalamayı sıfıra çekmez.
            private double _avgBufferedSamples = -1;
            private long _lastWriteTick = 0;
            public double AverageBufferedMs => _avgBufferedSamples < 0 ? -1 : _avgBufferedSamples * 1000.0 / SampleRate;

            // ---- Uyarlanabilir alış tamponu ----
            // "Pay" (slack) = okuma anında, bu okumadan SONRA tamponda kalacak örnek sayısı. Hedef pay küçük başlar;
            // ses akarken tampon boşalırsa (gerçek kesinti) büyür, kesintisiz geçen her 5 sn'de biraz küçülür.
            // Son 500 ms'deki en düşük pay hedefin üzerindeyse fazlası yumuşak geçişle atılır: gecikme birikmez.
            private const int SlackMin = SampleRate / 1000;          // 1 ms
            private const int SlackMax = SampleRate * 80 / 1000;     // 80 ms
            private const int SlackStep = SampleRate * 25 / 10000;   // 2,5 ms
            private const int SlackDecay = SampleRate / 2000;        // 0,5 ms
            private const int CrossfadeSamples = 96;                 // 2 ms
            private bool _playing = false;
            private int _targetSlack = SampleRate * 10 / 1000;       // başlangıç 10 ms (kesintisiz geçtikçe küçülür)
            private int _floorSlack = SlackMin;                      // son kesintinin olduğu seviye: 5 dk altına inilmez
            private long _floorSetTick = 0;
            private int _windowMinSlack = int.MaxValue;
            private long _windowStartTick = 0;
            private long _lastUnderrunTick = 0;
            private long _lastDecayTick = 0;
            private int _underruns = 0;

            public double TargetSlackMs => _targetSlack * 1000.0 / SampleRate;
            public int Underruns => _underruns;

            /// <summary>Okuma konumundan itibaren <paramref name="drop"/> örneği atlar; 2 ms çapraz geçişle tıklama olmaz.</summary>
            private void DropWithCrossfade(int drop)
            {
                if (drop <= 0 || drop > _count) return;
                int n = _ring.Length;
                int fade = Math.Min(CrossfadeSamples, _count - drop);
                for (int k = 0; k < fade; k++)
                {
                    float w = (k + 1f) / (fade + 1f);
                    int a = (_readPos + k) % n;
                    int b = (_readPos + drop + k) % n;
                    _ring[b] = _ring[a] * (1f - w) + _ring[b] * w;
                }
                _readPos = (_readPos + drop) % n;
                _count -= drop;
            }

            public bool MixInto(float[] dest, int offset, int frames, out bool isSpeakingNow)
            {
                float sumSq = 0f;
                int actualRead = 0;

                lock (_lock)
                {
                    long now = Environment.TickCount64;
                    bool streamActive = now - _lastWriteTick < 80;

                    if (!_playing)
                    {
                        // Konuşma/akış başında hedef pay kadar ses birikene kadar bekle (ön tampon)
                        if (streamActive && _count >= frames + _targetSlack)
                        {
                            _playing = true;
                            _windowMinSlack = int.MaxValue;
                            _windowStartTick = now;
                        }
                    }

                    if (_playing && streamActive)
                    {
                        _avgBufferedSamples = _avgBufferedSamples < 0
                            ? _count
                            : _avgBufferedSamples * 0.97 + _count * 0.03;

                        int slack = _count - frames;
                        if (slack < _windowMinSlack) _windowMinSlack = slack;

                        if (now - _windowStartTick >= 500)
                        {
                            if (_windowMinSlack != int.MaxValue && _windowMinSlack > _targetSlack + CrossfadeSamples)
                            {
                                DropWithCrossfade(_windowMinSlack - _targetSlack);
                            }
                            _windowMinSlack = int.MaxValue;
                            _windowStartTick = now;
                        }

                        // Küçülme: son kesintiden 30 sn sonra, 5 sn'de bir 0,5 ms. Kesintinin yaşandığı seviyenin
                        // (taban) altına inilmez; taban 5 dk'da bir 0,5 ms gevşer. Aksi hâlde pay sürekli kesinti
                        // sınırında gidip gelir (her ~25 sn'de bir kesinti).
                        if (_floorSetTick > 0 && now - _floorSetTick > 300_000)
                        {
                            // Taban 5 dk'da bir yalnızca 0,5 ms gevşer: koşullar iyileştiyse yavaşça aşağı yoklanır
                            _floorSlack = Math.Max(SlackMin, _floorSlack - SlackDecay);
                            _floorSetTick = now;
                        }
                        if (now - _lastUnderrunTick > 30_000 && now - _lastDecayTick > 5000)
                        {
                            _targetSlack = Math.Max(Math.Max(SlackMin, _floorSlack), _targetSlack - SlackDecay);
                            _lastDecayTick = now;
                        }
                    }

                    for (int i = 0; i < frames && _playing; i++)
                    {
                        if (_count <= 0)
                        {
                            // Tampon boşaldı. Ses hâlâ akıyorsa bu gerçek bir kesintidir: hedef payı büyüt.
                            if (streamActive)
                            {
                                _underruns++;
                                _floorSlack = Math.Min(SlackMax, _targetSlack + SlackStep);
                                _floorSetTick = now;
                                _targetSlack = _floorSlack;
                                _lastUnderrunTick = now;
                            }
                            _playing = false;
                            break;
                        }
                        float s = _ring[_readPos];
                        _readPos = (_readPos + 1) % _ring.Length;
                        _count--;
                        actualRead++;

                        sumSq += s * s;

                        if (_localMuted || _volumeGain <= 0.0001f) continue;

                        if (_clarityBoost)
                        {
                            s = _clarityHp.Process(s);
                            s = _clarityShelf.Process(s);
                        }

                        // Ses Dengeleme (Loudness Leveler - Özellik 2)
                        s = _leveler.Process(s, _isSpeaking);

                        s *= _volumeGain;

                        // 3B Uzamsal Ses (Spatial Panner - Özellik 3)
                        _panner.Process(s, out float outL, out float outR);

                        int dstIdx = offset + (i * 2);
                        dest[dstIdx] += outL;
                        dest[dstIdx + 1] += outR;
                    }
                }

                float rms = actualRead > 0 ? MathF.Sqrt(sumSq / actualRead) : 0f;
                if (rms > 0.008f && !_localMuted)
                {
                    _speakingHoldSamples = SampleRate * 200 / 1000;
                }
                else if (_speakingHoldSamples > 0)
                {
                    _speakingHoldSamples = Math.Max(0, _speakingHoldSamples - frames);
                }

                bool newSpeak = _speakingHoldSamples > 0;
                bool changed = newSpeak != _isSpeaking;
                _isSpeaking = newSpeak;
                isSpeakingNow = newSpeak;
                return changed;
            }
        }

        private sealed class MasterNativeWaveProvider : IWaveProvider
        {
            private readonly AudioEngine _engine;
            private readonly EarProtectionLimiter _limiter = new();
            private readonly int _outRate;
            private readonly int _outChannels;
            private readonly int _bytesPerFrame;
            private readonly bool _isFloat32;

            private float[] _stereo48kScratch = new float[8192];
            private float[] _resampledStereoScratch = new float[8192];
            private double _srcPos = 0.0;
            private float _prevSampleL = 0f;
            private float _prevSampleR = 0f;

            public WaveFormat WaveFormat { get; }
            public bool IsMonoDevice => _outChannels == 1;

            public MasterNativeWaveProvider(AudioEngine engine, WaveFormat deviceMixFormat)
            {
                _engine = engine;
                WaveFormat = deviceMixFormat;
                _outRate = Math.Max(8000, deviceMixFormat.SampleRate);
                _outChannels = Math.Max(1, deviceMixFormat.Channels);
                _bytesPerFrame = deviceMixFormat.BlockAlign > 0 ? deviceMixFormat.BlockAlign : (_outChannels * (deviceMixFormat.BitsPerSample / 8));
                _isFloat32 = deviceMixFormat.BitsPerSample == 32;
            }

            public void SetCeilingDb(float db) => _limiter.SetCeilingDb(db);

            public int Read(byte[] buffer, int offset, int byteCount)
            {
                Array.Clear(buffer, offset, byteCount);
                if (_bytesPerFrame <= 0 || byteCount < _bytesPerFrame) return byteCount;

                int outFrames = byteCount / _bytesPerFrame;

                // NAudio oynatmaya başlamadan önce WASAPI tamponunun tamamını tek seferde doldurur:
                // ilk okuma boyutu = gerçek hoparlör tamponu (ses bu kadar süre cihaz kuyruğunda bekler).
                if (_engine._playbackBufferFrames < 0) _engine._playbackBufferFrames = outFrames;

                if (_outRate == SampleRate)
                {
                    int neededSamples = outFrames * 2;
                    if (_stereo48kScratch.Length < neededSamples)
                        _stereo48kScratch = new float[neededSamples * 2];

                    _engine.FillMasterPlaybackBuffer(_stereo48kScratch, 0, outFrames);
                    WriteStereoToDeviceBuffer(_stereo48kScratch, outFrames, buffer, offset);
                    return outFrames * _bytesPerFrame;
                }

                // Çıkış cihazı 44.1kHz / 96kHz / 192kHz ise pürüzsüz doğrusal yeniden örnekleme (L ve R ayrı)
                double step = (double)SampleRate / _outRate;
                double totalAdvance = _srcPos + (outFrames * step);
                int needed48k = Math.Max(1, (int)Math.Floor(totalAdvance));
                int neededScratchSamples = (needed48k + 4) * 2;

                if (_stereo48kScratch.Length < neededScratchSamples)
                    _stereo48kScratch = new float[neededScratchSamples * 2];

                _engine.FillMasterPlaybackBuffer(_stereo48kScratch, 0, needed48k);

                int resampledLen = outFrames * 2;
                if (_resampledStereoScratch.Length < resampledLen)
                    _resampledStereoScratch = new float[resampledLen * 2];

                double pos = _srcPos;
                for (int i = 0; i < outFrames; i++)
                {
                    int idx = (int)Math.Floor(pos);
                    float frac = (float)(pos - idx);

                    int idxPrev = Math.Min(idx - 1, needed48k - 1);
                    int idxCurr = Math.Min(Math.Max(0, idx), needed48k - 1);

                    float s0L = idx <= 0 ? _prevSampleL : _stereo48kScratch[idxPrev * 2];
                    float s1L = _stereo48kScratch[idxCurr * 2];
                    float s0R = idx <= 0 ? _prevSampleR : _stereo48kScratch[(idxPrev * 2) + 1];
                    float s1R = _stereo48kScratch[(idxCurr * 2) + 1];

                    _resampledStereoScratch[i * 2] = s0L + ((s1L - s0L) * frac);
                    _resampledStereoScratch[(i * 2) + 1] = s0R + ((s1R - s0R) * frac);
                    pos += step;
                }

                _prevSampleL = _stereo48kScratch[(needed48k - 1) * 2];
                _prevSampleR = _stereo48kScratch[((needed48k - 1) * 2) + 1];
                _srcPos = Math.Max(0.0, pos - needed48k);

                WriteStereoToDeviceBuffer(_resampledStereoScratch, outFrames, buffer, offset);
                return outFrames * _bytesPerFrame;
            }

            private void WriteStereoToDeviceBuffer(float[] stereoInterleaved, int frames, byte[] destBytes, int offset)
            {
                if (_isFloat32)
                {
                    var floatSpan = MemoryMarshal.Cast<byte, float>(destBytes.AsSpan(offset, frames * _outChannels * 4));
                    for (int f = 0; f < frames; f++)
                    {
                        float l = stereoInterleaved[f * 2];
                        float r = stereoInterleaved[(f * 2) + 1];
                        _limiter.ProcessStereo(ref l, ref r);

                        int baseIdx = f * _outChannels;
                        if (_outChannels == 1)
                        {
                            floatSpan[baseIdx] = (l + r) * 0.5f;
                        }
                        else
                        {
                            floatSpan[baseIdx] = l;
                            floatSpan[baseIdx + 1] = r;
                            for (int c = 2; c < _outChannels; c++)
                            {
                                floatSpan[baseIdx + c] = 0f;
                            }
                        }
                    }
                }
                else if (WaveFormat.BitsPerSample == 16)
                {
                    var shortSpan = MemoryMarshal.Cast<byte, short>(destBytes.AsSpan(offset, frames * _outChannels * 2));
                    for (int f = 0; f < frames; f++)
                    {
                        float l = stereoInterleaved[f * 2];
                        float r = stereoInterleaved[(f * 2) + 1];
                        _limiter.ProcessStereo(ref l, ref r);

                        int baseIdx = f * _outChannels;
                        if (_outChannels == 1)
                        {
                            float mono = Math.Clamp((l + r) * 0.5f, -1.0f, 1.0f);
                            shortSpan[baseIdx] = (short)(mono * 32767f);
                        }
                        else
                        {
                            shortSpan[baseIdx] = (short)(Math.Clamp(l, -1.0f, 1.0f) * 32767f);
                            shortSpan[baseIdx + 1] = (short)(Math.Clamp(r, -1.0f, 1.0f) * 32767f);
                            for (int c = 2; c < _outChannels; c++)
                            {
                                shortSpan[baseIdx + c] = 0;
                            }
                        }
                    }
                }
            }
        }
    }
}
