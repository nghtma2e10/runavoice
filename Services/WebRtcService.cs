using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Concentus;
using Concentus.Enums;
using RunaVoice_WinUI.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Gerçek WebRTC Opus (RFC 7587, 48kHz, FEC, 2.5ms-20ms) Ses İletimi,
    /// Bağımsız SRTP UDP Video İzi (SDPMediaTypesEnum.video - Sıfır SCTP Tıkanıklığı!),
    /// P2P Şifreli "runa-control" DataChannel Hassas RTT Ping Ölçümü ve Hiyerarşik Röle Servisi.
    /// </summary>
    /// <summary>
    /// 2 saniyelik kayan pencere (4 kova x 0.5 sn) ile bit hızı izleyici.
    /// </summary>
    public sealed class SlidingRateTracker
    {
        private readonly long[] _buckets = new long[4];
        private long _currentBucketTick = 0;
        private int _bucketIdx = 0;
        private readonly object _lock = new();

        public void AddBytes(long bytes)
        {
            lock (_lock)
            {
                Advance();
                _buckets[_bucketIdx] += bytes;
            }
        }

        private void Advance()
        {
            long now = Stopwatch.GetTimestamp();
            if (_currentBucketTick == 0)
            {
                _currentBucketTick = now;
                return;
            }

            double elapsedBuckets = (now - _currentBucketTick) * 2.0 / Stopwatch.Frequency;
            int steps = (int)elapsedBuckets;
            if (steps > 0)
            {
                int clearSteps = Math.Min(steps, 4);
                for (int i = 1; i <= clearSteps; i++)
                {
                    _buckets[(_bucketIdx + i) % 4] = 0;
                }
                _bucketIdx = (_bucketIdx + steps) % 4;
                _currentBucketTick = now;
            }
        }

        public int CurrentRateKbps
        {
            get
            {
                lock (_lock)
                {
                    Advance();
                    long sum = 0;
                    for (int i = 0; i < 4; i++) sum += _buckets[i];
                    return (int)Math.Round((sum * 8.0) / 2000.0);
                }
            }
        }
    }

    public sealed class WebRtcService : IDisposable
    {
        private const ushort ScreenMagic = 0x5256; // "RV"
        private const int VideoPayloadTypeId = 96;
        private const int MaxRtpChunkPayload = 1140; // Tekil 1200-bayt UDP/SRTP MTU dilimi (sıfır IP parçalanması)
        private const int MaxControlMessageBytes = 96 * 1024;
        private const int MaxPeerSessions = 24;        // Bilinmeyen eşlerden gelen sinyallerle sınırsız bağlantı açılamasın
        private const int MaxSdpChars = 64 * 1024;

        // Röle çocuk listesi önbelleği (her video paketinde LINQ sıralaması yapılmasın)
        private string[] _relayChildKeys = Array.Empty<string>();
        private long _relayCacheTick = 0;
        private string _relayCacheBroadcaster = "";
        private int _relayCacheParticipantCount = -1;

        private readonly SignalingService _signaling;
        private readonly AudioEngine _audio;
        private readonly StorageService _storage;

        private readonly ConcurrentDictionary<string, PeerRtcSession> _sessions = new();

        // Teklif (veya katılım bildirimi) gelmeden önce ulaşan ICE adayları. Başlatan taraf adaylarını
        // SDP teklifiyle neredeyse aynı anda yolladığından MQTT'de sıra karışabilir; bunlar atılırsa bağlantı
        // hiç kurulamayabilir. Bağlantı nesnesi oluşturmadan, sınırlı ve kısa ömürlü bir tamponda tutulur.
        private readonly ConcurrentDictionary<string, EarlyIceBuffer> _earlyIce = new();
        private const int MaxEarlyIcePeers = 24;
        private const int MaxEarlyIcePerPeer = 40;
        private const long EarlyIceTtlMs = 20000;

        private sealed class EarlyIceBuffer
        {
            public long CreatedTick = Environment.TickCount64;
            public readonly List<string> Candidates = new();
        }

        private readonly object _encoderLock = new();
        private IOpusEncoder _opusEncoder;
        private int _configuredBitrateKbps = 0;

        private Timer? _rttPingTimer;
        private uint _localVideoFrameSeq = 0;
        private int _sendingFramesCount = 0;

        // Uzak ekran yayını istatistikleri
        private int _rxFramesInSec = 0;
        private long _rxBytesInSec = 0;
        private long _lastRxStatsTick = Environment.TickCount64;

        public int RemoteStreamFps { get; private set; } = 0;
        public int RemoteStreamBitrateKbps { get; private set; } = 0;
        public int RemoteStreamHeight { get; set; } = 1080;
        public int RemoteStreamWidth { get; set; } = 1920;
        private readonly SlidingRateTracker _sentNetworkTracker = new();
        private readonly SlidingRateTracker _receivedVideoTracker = new();
        private readonly SlidingRateTracker _usableVideoTracker = new();
        private readonly ConcurrentDictionary<string, VideoQualityReport> _lastPeerVqReports = new();

        public int PacerTargetBitrateKbps { get; set; } = 4000;
        private int _pacerDroppedFramesCount = 0;
        public int PacerDroppedFramesCount => Volatile.Read(ref _pacerDroppedFramesCount);

        public int SentNetworkBitrateKbps => _sentNetworkTracker.CurrentRateKbps;
        public int ReceivedVideoBitrateKbps => _receivedVideoTracker.CurrentRateKbps;
        public int UsableVideoBitrateKbps => _usableVideoTracker.CurrentRateKbps;

        private readonly object _dedupLock = new();
        private readonly ulong[] _recentPacketsRing = new ulong[2048];
        private int _recentPacketsIdx = 0;

        private bool IsDuplicateVideoChunk(uint frameId, ushort chunkIdx)
        {
            ulong key = ((ulong)frameId << 16) | (ulong)chunkIdx;
            lock (_dedupLock)
            {
                for (int i = 0; i < 2048; i++)
                {
                    if (_recentPacketsRing[i] == key) return true;
                }
                _recentPacketsRing[_recentPacketsIdx] = key;
                _recentPacketsIdx = (_recentPacketsIdx + 1) % 2048;
                return false;
            }
        }

        public (double avgLoss, int minKbps, int avgKbps, int totalDropped) GetBroadcasterReceiverStats()
        {
            var reports = _lastPeerVqReports.Values.ToList();
            if (reports.Count == 0) return (0.0, 0, 0, 0);
            double avgLoss = reports.Average(r => r.LossPct);
            int minKbps = reports.Min(r => r.Kbps);
            int avgKbps = (int)Math.Round(reports.Average(r => r.Kbps));
            int totalDropped = reports.Sum(r => r.Fd + r.Gap);
            return (avgLoss, minKbps, avgKbps, totalDropped);
        }

        private readonly object _watchersLock = new();
        private readonly HashSet<string> _activeWatchers = new();
        private long _broadcastingStreamStartTick = 0;
        public event Action<string, bool>? OnWatcherStateChanged; // peerKey, isWatching

        public HashSet<string> GetActiveWatcherKeys()
        {
            lock (_watchersLock)
            {
                return new HashSet<string>(_activeWatchers);
            }
        }

        public void SendWatchStart(string broadcasterPeerKey)
        {
            if (string.IsNullOrEmpty(broadcasterPeerKey)) return;
            SendControlJsonToPeer(broadcasterPeerKey, "{\"t\":\"WATCH_START\"}");
        }

        public void SendWatchStop(string broadcasterPeerKey)
        {
            if (string.IsNullOrEmpty(broadcasterPeerKey)) return;
            SendControlJsonToPeer(broadcasterPeerKey, "{\"t\":\"WATCH_STOP\"}");
        }

        public int RemoteStreamRttMs
        {
            get
            {
                if (!string.IsNullOrEmpty(RemoteBroadcasterKey) && _sessions.TryGetValue(RemoteBroadcasterKey, out var s) && s.LastRttMs > 0)
                    return s.LastRttMs;
                return -1;
            }
        }

        public int BroadcasterAverageRttMs
        {
            get
            {
                var valid = _sessions.Values.Where(s => s.LastRttMs > 0).ToList();
                if (valid.Count == 0) return -1;
                return (int)Math.Round(valid.Average(s => s.LastRttMs));
            }
        }
        public double RemoteStreamPacketLossPct
        {
            get
            {
                if (!string.IsNullOrEmpty(RemoteBroadcasterKey) && _sessions.TryGetValue(RemoteBroadcasterKey, out var s))
                    return s.CurrentLossPct;
                return 0.0;
            }
        }
        public int RemoteStreamPacketLoss => (int)Math.Round(RemoteStreamPacketLossPct);
        public long TotalFramesDecoded { get; set; } = 0;
        public string RemoteStreamCodec { get; set; } = "MJPEG";
        public string RemoteBroadcasterKey { get; private set; } = "";
        public string RemoteBroadcasterName { get; private set; } = "";
        public string RemoteStreamTitle { get; private set; } = "";

        // Yakalama döngüsü bunu her karede sorar: .Values her çağrıda liste kopyası ayırdığı için doğrudan sözlük gezilir
        public bool HasConnectedPeers
        {
            get
            {
                foreach (var kv in _sessions)
                {
                    if (kv.Value.Connection.connectionState == RTCPeerConnectionState.connected) return true;
                }
                return false;
            }
        }
        public bool IsVideoSenderBusy => Volatile.Read(ref _sendingFramesCount) >= 2;

        // Olaylar
        public event Action<string, int>? OnPeerRttUpdated; // peerKey, rttMs
        public event Action<string, int, int>? OnPeerPingUpdated; // peerKey, rttMs, jitterMs
        public event Action<string, Models.PeerLatencyInfo>? OnPeerLatencyUpdated; // peerKey, ağ + uçtan uca ses gecikmesi

        // UDP/SRTP ağ ölçüm paketi ("TP"): video izinde, ses/görüntüyle aynı yol. Eski sürümler bu sihirli
        // sayıyı tanımadığı için (ekran paketi 0x5256 değil) paketi sessizce yok sayar.
        private const ushort NetPingMagic = 0x5054;
        private const int NetPingPacketSize = 20;
        private double _encodeUsAvg = -1;
        private int _encoderLookaheadSamples = -1;
        public event Action<string, Models.PeerPingStatus>? OnPeerPingStatusChanged; // peerKey, status
        public event Action<string>? OnPeerRttReset; // peerKey
        public event Action<string, string, byte[]?>? OnRemoteStreamStateChanged; // peerKey, peerName, optionalPreviewJpeg (empty key = stopped)
        public event Action<string, byte[], int, int>? OnRemoteVideoFrameReceived; // peerKey, jpegBytes, width, height
        public event Action<string, byte[], int, int, uint>? OnRemoteVideoFrameReceivedWithId; // peerKey, jpegBytes, width, height, frameId
        public uint LastRemoteVideoFrameId { get; private set; }
        public event Action<string, string, AnnotationMessage>? OnAnnotationMessage; // fromPeerKey, fromPeerName, msg
        public event Action<string>? OnControlChannelOpened; // peerKey

        // GameStream Kontrol ve Tünel Olayları (PDF Bölüm 4, 6, 7)
        public event Action<string, string, string, JsonElement>? OnGameStreamMessage; // fromPeerKey, fromPeerName, msgType, root
        public event Action<string, RTCDataChannel>? OnGameStreamTcpChannelOpened; // peerKey, channel
        public event Action<string, byte[]>? OnGameStreamUdpPacketReceived; // peerKey, rawBytes

        // Dinamik Çözünürlük (Özellik 4)
        public event Action<string, VideoQualityReport, bool>? OnVideoQualityReport; // peerKey, report, viaDirect
        public event Action<VideoQualityState>? OnVideoQualityState;
        public VideoQualityState? LastBroadcasterVqState => _lastBroadcasterVqState;
        private VideoQualityState? _lastBroadcasterVqState;

        private int _vqFr = 0;
        private int _vqFd = 0;
        private int _vqGap = 0;
        private int _vqRd = 0;
        private long _vqBytes = 0;
        private float _vqJitterMs = 0f;
        private long _vqLastFrameTick = 0;
        private uint _vqLastCompletedFrameId = 0;
        private readonly Timer _vqReportTimer;

        public int SentVideoBitrateKbps { get; private set; } = 0;
        private long _sentVideoBytesInSec = 0;
        private long _lastSentVideoStatsTick = 0;

        public void IncrementRenderDelayDroppedFrames() => Interlocked.Increment(ref _vqRd);
        public int RenderDelayDroppedFramesCount => _vqRd;

        public WebRtcService(SignalingService signaling, AudioEngine audio, StorageService storage)
        {
            _signaling = signaling;
            _audio = audio;
            _storage = storage;

            _opusEncoder = CreateConfiguredEncoder();

            _audio.OnOutgoingPcmFrameReady += EncodeAndBroadcastAudioFrame;
            _signaling.OnSignalReceived += HandleSignalingMessage;
            _signaling.OnPeerLeft += (peerKey, _) => RemovePeer(peerKey);

            _rttPingTimer = new Timer(_ => SendRttPingsToAll(), null, 1000, 1000);
            _vqReportTimer = new Timer(_ => SendVqReportTimerTick(), null, 1000, 1000);
        }

        private IOpusEncoder CreateConfiguredEncoder()
        {
            int kbps = Math.Clamp(_storage.Settings.OpusBitrateKbps, 24, 256);
            var app = kbps >= 128 ? OpusApplication.OPUS_APPLICATION_AUDIO : OpusApplication.OPUS_APPLICATION_VOIP;
            var enc = OpusCodecFactory.CreateEncoder(AudioEngine.SampleRate, 1, app);
            enc.Bitrate = kbps * 1000;
            enc.Complexity = 5;
            enc.UseInbandFEC = true;
            enc.PacketLossPercent = 8;
            enc.UseDTX = false;
            _configuredBitrateKbps = kbps;
            return enc;
        }

        public void RefreshCodecSettings()
        {
            lock (_encoderLock)
            {
                int wanted = Math.Clamp(_storage.Settings.OpusBitrateKbps, 24, 256);
                if (wanted != _configuredBitrateKbps)
                {
                    _opusEncoder = CreateConfiguredEncoder();
                }
            }
        }

        private static RTCConfiguration CreateConfiguration()
        {
            return new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                    new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },
                    new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" }
                }
            };
        }

        /// <summary>
        /// Deterministik başlatıcı (Caller/Callee) seçimi ile Glare (çift SDP teklif çakışması) oluşmasını kesin olarak engeller.
        /// </summary>
        public async Task EnsurePeerConnectionAsync(string myPeerKey, RoomParticipant remotePeer)
        {
            if (string.IsNullOrEmpty(remotePeer.PeerKey) || remotePeer.PeerKey == myPeerKey) return;
            if (_sessions.ContainsKey(remotePeer.PeerKey)) return;

            bool isInitiator = string.CompareOrdinal(myPeerKey, remotePeer.PeerKey) < 0;
            if (!isInitiator)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3500);
                    if (!_sessions.ContainsKey(remotePeer.PeerKey) && _signaling.Participants.ContainsKey(remotePeer.PeerKey))
                    {
                        await ConnectToPeerInternalAsync(remotePeer.PeerKey, remotePeer.Name, true);
                    }
                });
                return;
            }

            await ConnectToPeerInternalAsync(remotePeer.PeerKey, remotePeer.Name, true);
        }

        private async Task<PeerRtcSession> ConnectToPeerInternalAsync(string peerKey, string peerName, bool isInitiator)
        {
            if (_sessions.TryGetValue(peerKey, out var existing))
                return existing;

            var pc = new RTCPeerConnection(CreateConfiguration());

            // 1. Gerçek RFC 7587 Opus Ses İzi (48kHz, PayloadType 111)
            var opusFormat = new SDPAudioVideoMediaFormat(
                new AudioFormat(111, "opus", AudioEngine.SampleRate, 2, "minptime=10;useinbandfec=1"));
            var audioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat> { opusFormat },
                MediaStreamStatusEnum.SendRecv);
            pc.addTrack(audioTrack);

            // 2. Bağımsız Gerçek Zamanlı UDP/SRTP Ekran Yayını Video İzi (PayloadType 96)
            // SCTP DataChannel kuyruğunu %0 meşgul eder; böylece kontrol/ping gecikmesi asla yükselmez!
            var videoFormat = new SDPAudioVideoMediaFormat(
                new VideoFormat(VideoCodecsEnum.VP8, VideoPayloadTypeId, 90000, ""));
            var videoTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                false,
                new List<SDPAudioVideoMediaFormat> { videoFormat },
                MediaStreamStatusEnum.SendRecv);
            pc.addTrack(videoTrack);

            var session = new PeerRtcSession(peerKey, peerName, pc) { IsInitiator = isInitiator };
            if (!_sessions.TryAdd(peerKey, session))
            {
                try { pc.close(); } catch { }
                return _sessions[peerKey];
            }
            DrainEarlyIceCandidates(session);

            // 3. Gelen SRTP Ses ve Video Paketlerini İşle
            pc.OnRtpPacketReceived += (ipep, mediaType, rtpPacket) =>
            {
                if (rtpPacket?.Payload == null || rtpPacket.Payload.Length == 0)
                    return;

                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    try
                    {
                        int decodedSamples = session.DecodeOpusPacket(rtpPacket.Payload, out float[] pcmBuf);
                        if (decodedSamples > 0)
                        {
                            _audio.PushRemotePeerPcm(session.PeerKey, session.PeerName, pcmBuf, decodedSamples);
                        }
                    }
                    catch { }
                }
                else if (mediaType == SDPMediaTypesEnum.video)
                {
                    try
                    {
                        if (rtpPacket.Payload.Length >= 2)
                        {
                            ushort magic = BitConverter.ToUInt16(rtpPacket.Payload, 0);
                            if (magic == 0x5247) // "RG" GameStream UDP tünel paketi (PDF Bölüm 4 & 7.3)
                            {
                                OnGameStreamUdpPacketReceived?.Invoke(session.PeerKey, rtpPacket.Payload);
                                return;
                            }
                            if (magic == NetPingMagic)
                            {
                                HandleNetPingPacket(session, rtpPacket.Payload);
                                return;
                            }
                        }
                        HandleIncomingVideoChunkPacket(session, rtpPacket.Payload, rtpPacket.Header.Timestamp, rtpPacket.Header.MarkerBit);
                    }
                    catch { }
                }
            };

            pc.onicecandidate += async candidate =>
            {
                if (candidate != null)
                {
                    var candJson = JsonSerializer.Serialize(new RTCIceCandidateInit
                    {
                        candidate = candidate.candidate,
                        sdpMid = candidate.sdpMid ?? "0",
                        sdpMLineIndex = candidate.sdpMLineIndex
                    });
                    session.RememberLocalIce(candJson);
                    await _signaling.SendSignalAsync(peerKey, "ice-candidate", candJson);
                }
            };

            pc.onconnectionstatechange += state =>
            {
                Debug.WriteLine($"[WebRTC] {peerName} ({peerKey}) state -> {state}");
                if (state == RTCPeerConnectionState.connected)
                {
                    session.ConnectionEstablishedTicks = Stopwatch.GetTimestamp();
                    session.IsP2pFailed = false;
                    OnPeerPingStatusChanged?.Invoke(session.PeerKey, Models.PeerPingStatus.Measuring);
                }
                else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
                {
                    // Yalnızca bu bağlantı hâlâ geçerli oturumsa kaldır; çakışma çözümünde değiştirilen eski
                    // bağlantının "closed" olayı yerine geçen yeni oturumu silmesin.
                    if (_sessions.TryGetValue(peerKey, out var current) && ReferenceEquals(current, session))
                    {
                        RemovePeer(peerKey);
                    }
                }
                else if (state == RTCPeerConnectionState.disconnected)
                {
                    OnPeerPingStatusChanged?.Invoke(session.PeerKey, Models.PeerPingStatus.Connecting);
                }
            };

            pc.ondatachannel += dc => AttachDataChannelHandlers(session, dc);

            if (isInitiator)
            {
                var ctrlDc = await pc.createDataChannel("runa-control", new RTCDataChannelInit { ordered = true });
                if (ctrlDc != null) AttachDataChannelHandlers(session, ctrlDc);

                var pingDc = await pc.createDataChannel("runa-ping", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
                if (pingDc != null) AttachDataChannelHandlers(session, pingDc);

                var offer = pc.createOffer(null);
                await pc.setLocalDescription(offer);
                session.LocalOfferSdp = offer.sdp;
                await _signaling.SendSignalAsync(peerKey, "sdp-offer", offer.sdp);
                _ = ResendOfferUntilAnsweredAsync(session);
            }

            return session;
        }

        /// <summary>
        /// Açık MQTT sunucularında mesaj kaybolabilir (özellikle karşı taraf o anda sunucu değiştiriyorsa).
        /// Yanıt gelmezse aynı teklif ve yerel ICE adayları birkaç kez yeniden gönderilir; karşı taraf
        /// yinelenen teklifi tanıyıp kayıtlı yanıtını yeniden yollar.
        /// </summary>
        private async Task ResendOfferUntilAnsweredAsync(PeerRtcSession session)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(4000);
                if (!_sessions.TryGetValue(session.PeerKey, out var current) || !ReferenceEquals(current, session)) return;
                if (session.Connection.remoteDescription != null || string.IsNullOrEmpty(session.LocalOfferSdp)) return;

                await _signaling.SendSignalAsync(session.PeerKey, "sdp-offer", session.LocalOfferSdp);
                foreach (var ice in session.GetLocalIce())
                {
                    await _signaling.SendSignalAsync(session.PeerKey, "ice-candidate", ice);
                }
            }
        }

        private void AttachDataChannelHandlers(PeerRtcSession session, RTCDataChannel dc)
        {
            if (dc.label == "runa-control")
            {
                session.ControlChannel = dc;
                void OnControlOpen()
                {
                    if (dc.readyState != RTCDataChannelState.open) return;

                    // Kanal açılır açılmaz anında ilk RTT pingini gönder (2 sn beklemeden gerçek ms görünsün)
                    try { dc.send($"PING:{Stopwatch.GetTimestamp()}"); } catch { }

                    if (!string.IsNullOrEmpty(_localActiveStreamTitle))
                    {
                        SendControlJson(dc, new
                        {
                            t = "STREAM_START",
                            title = _localActiveStreamTitle
                        });
                    }

                    OnControlChannelOpened?.Invoke(session.PeerKey);
                }

                dc.onopen += OnControlOpen;
                OnControlOpen();

                dc.onmessage += (chan, proto, rawBytes) =>
                {
                    HandleControlChannelMessage(session, chan, rawBytes);
                };
            }
            else if (dc.label == "runa-ping")
            {
                session.PingChannel = dc;
                void OnPingOpen()
                {
                    if (dc.readyState != RTCDataChannelState.open) return;
                    try { dc.send($"PING:{Stopwatch.GetTimestamp()}"); } catch { }
                }
                dc.onopen += OnPingOpen;
                OnPingOpen();

                dc.onmessage += (chan, proto, rawBytes) =>
                {
                    HandlePingChannelMessage(session, chan, rawBytes);
                };
            }
            else if (dc.label == "runa-screen")
            {
                // Geriye dönük uyumluluk alıcısı
                session.ScreenChannel = dc;
                dc.onmessage += (chan, proto, rawBytes) =>
                {
                    HandleIncomingVideoChunkPacket(session, rawBytes, 0, 0);
                };
            }
            else if (dc.label == "runa-gs-tcp")
            {
                session.GameStreamTcpChannel = dc;
                OnGameStreamTcpChannelOpened?.Invoke(session.PeerKey, dc);
            }
        }

        private void HandleControlChannelMessage(PeerRtcSession session, RTCDataChannel dc, byte[] rawBytes)
        {
            try
            {
                // Yayın önizleme küçük resmi (STREAM_THUMB, base64 JPEG) 16 KB'ı aşabildiği için sınır 96 KB;
                // ANN_/VQ_ gibi küçük mesajların kendi sınırları kendi ayrıştırıcılarında uygulanır.
                if (rawBytes == null || rawBytes.Length == 0 || rawBytes.Length > MaxControlMessageBytes)
                    return;

                string text = Encoding.UTF8.GetString(rawBytes);
                if (text.StartsWith("PING:", StringComparison.Ordinal))
                {
                    dc.send("PONG:" + text.AsSpan(5).ToString());
                    return;
                }
                if (text.StartsWith("PONG:", StringComparison.Ordinal))
                {
                    if (long.TryParse(text.AsSpan(5), out long sentTicks))
                    {
                        double rttMs = (Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency;
                        // Veri kanalı (SCTP) ölçümü yalnızca karşı taraf UDP ölçüm paketini desteklemiyorsa
                        // (eski sürüm) yedek olarak kullanılır; SCTP katmanı kendi gecikmesini ekler.
                        if (!UseSctpPingFallback(session)) return;
                        int rawSample = Math.Max(1, (int)Math.Round(rttMs));
                        var (median, jitter) = session.RecordRttSample(rawSample);
                        OnPeerPingUpdated?.Invoke(session.PeerKey, median, jitter);
                        OnPeerRttUpdated?.Invoke(session.PeerKey, median);
                    }
                    return;
                }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                string t = root.TryGetProperty("t", out var tp) ? tp.GetString() ?? "" : "";

                if (t == "WATCH_START")
                {
                    lock (_watchersLock)
                    {
                        _activeWatchers.Add(session.PeerKey);
                    }
                    OnWatcherStateChanged?.Invoke(session.PeerKey, true);
                    return;
                }
                if (t == "WATCH_STOP")
                {
                    lock (_watchersLock)
                    {
                        _activeWatchers.Remove(session.PeerKey);
                    }
                    OnWatcherStateChanged?.Invoke(session.PeerKey, false);
                    return;
                }

                if (t.StartsWith("ANN_", StringComparison.Ordinal))
                {
                    if (AnnotationProtocol.TryParse(root, out var annMsg))
                    {
                        OnAnnotationMessage?.Invoke(session.PeerKey, session.PeerName, annMsg);
                    }
                    return;
                }

                if (t.StartsWith("GS_", StringComparison.Ordinal))
                {
                    OnGameStreamMessage?.Invoke(session.PeerKey, session.PeerName, t, root.Clone());
                    return;
                }

                if (t == "STREAM_START")
                {
                    RemoteBroadcasterKey = session.PeerKey;
                    RemoteBroadcasterName = session.PeerName;
                    RemoteStreamTitle = root.TryGetProperty("title", out var tl) ? tl.GetString() ?? "Ekran Yayını" : "Ekran Yayını";
                    OnRemoteStreamStateChanged?.Invoke(session.PeerKey, session.PeerName, null);
                }
                else if (t == "STREAM_THUMB")
                {
                    string b64 = root.TryGetProperty("jpg", out var j) ? j.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(b64) && b64.Length <= MaxControlMessageBytes)
                    {
                        RemoteBroadcasterKey = session.PeerKey;
                        RemoteBroadcasterName = session.PeerName;
                        byte[] thumbBytes = Convert.FromBase64String(b64);
                        OnRemoteStreamStateChanged?.Invoke(session.PeerKey, session.PeerName, thumbBytes);
                    }
                }
                else if (t == "STREAM_STOP")
                {
                    RemoteBroadcasterKey = "";
                    RemoteBroadcasterName = "";
                    RemoteStreamTitle = "";
                    RemoteStreamFps = 0;
                    RemoteStreamBitrateKbps = 0;
                    OnRemoteStreamStateChanged?.Invoke("", "", null);
                }
                else if (t == "P2P_CHAT" && root.TryGetProperty("msg", out var mEl))
                {
                    var chat = JsonSerializer.Deserialize<ChatMessage>(mEl.GetRawText());
                    if (chat != null)
                    {
                        // Gönderen adı mesajdan değil, şifreli P2P oturumun kimliğinden alınır (ad taklidi önlenir)
                        chat.SenderName = session.PeerName;
                        _signaling.DeliverUniqueChatMessage(chat);
                    }
                }
                else if (t == "VQ_REPORT")
                {
                    if (VideoQualityReport.TryParse(root, out var report))
                    {
                        _lastPeerVqReports[session.PeerKey] = report;
                        bool viaDirect = string.Equals(report.Via, "direct", StringComparison.OrdinalIgnoreCase);
                        OnVideoQualityReport?.Invoke(session.PeerKey, report, viaDirect);
                    }
                }
                else if (t == "VQ_STATE")
                {
                    if (VideoQualityState.TryParse(root, out var state))
                    {
                        _lastBroadcasterVqState = state;
                        OnVideoQualityState?.Invoke(state);
                    }
                }
            }
            catch { }
        }

        private void HandlePingChannelMessage(PeerRtcSession session, RTCDataChannel dc, byte[] rawBytes)
        {
            try
            {
                if (rawBytes == null || rawBytes.Length == 0 || rawBytes.Length > 256) return;
                string text = Encoding.UTF8.GetString(rawBytes);
                if (text.StartsWith("PING:", StringComparison.Ordinal))
                {
                    dc.send("PONG:" + text.AsSpan(5).ToString());
                    return;
                }
                if (text.StartsWith("PONG:", StringComparison.Ordinal))
                {
                    if (long.TryParse(text.AsSpan(5), out long sentTicks))
                    {
                        double rttMs = (Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency;
                        // Veri kanalı (SCTP) ölçümü yalnızca karşı taraf UDP ölçüm paketini desteklemiyorsa
                        // (eski sürüm) yedek olarak kullanılır; SCTP katmanı kendi gecikmesini ekler.
                        if (!UseSctpPingFallback(session)) return;
                        int rawSample = Math.Max(1, (int)Math.Round(rttMs));
                        var (median, jitter) = session.RecordRttSample(rawSample);
                        OnPeerPingUpdated?.Invoke(session.PeerKey, median, jitter);
                        OnPeerRttUpdated?.Invoke(session.PeerKey, median);
                    }
                }
            }
            catch { }
        }

        private void HandleIncomingVideoChunkPacket(PeerRtcSession session, byte[] packet, uint rtpTimestamp, int markerBit)
        {
            if (packet == null || packet.Length < 14) return;

            ushort magic = BitConverter.ToUInt16(packet, 0);
            if (magic != ScreenMagic) return;

            // P2P Hibrit Röle: Eğer bu düğüm röle ebeveyni ise alt izleyicilere SRTP üzerinden anında ilet
            RelayScreenPacketToChildrenIfNeeded(session.PeerKey, packet, rtpTimestamp, markerBit);

            uint frameId = BitConverter.ToUInt32(packet, 2);
            ushort chunkIdx = BitConverter.ToUInt16(packet, 6);
            ushort totalChunks = BitConverter.ToUInt16(packet, 8);
            ushort width = BitConverter.ToUInt16(packet, 10);
            ushort height = BitConverter.ToUInt16(packet, 12);

            if (totalChunks == 0 || totalChunks > 512 || chunkIdx >= totalChunks) return;

            bool isDup = IsDuplicateVideoChunk(frameId, chunkIdx);
            if (!isDup)
            {
                _receivedVideoTracker.AddBytes(packet.Length);
            }
            _vqBytes += packet.Length;

            byte[]? fullFrame = session.AssembleVideoChunk(frameId, chunkIdx, totalChunks, packet, 14, packet.Length - 14);
            if (fullFrame != null)
            {
                _usableVideoTracker.AddBytes(fullFrame.Length);
                _vqFr++;
                if (_vqLastCompletedFrameId > 0 && frameId > _vqLastCompletedFrameId + 1)
                {
                    _vqGap += (int)(frameId - _vqLastCompletedFrameId - 1);
                }
                _vqLastCompletedFrameId = frameId;

                long nowTick = Environment.TickCount64;
                if (_vqLastFrameTick > 0)
                {
                    long delta = nowTick - _vqLastFrameTick;
                    float diff = MathF.Abs(delta - 16.6f);
                    _vqJitterMs = (_vqJitterMs * 0.9f) + (diff * 0.1f);
                }
                _vqLastFrameTick = nowTick;
                _rxFramesInSec++;
                _rxBytesInSec += fullFrame.Length;
                long now = Environment.TickCount64;
                if (now - _lastRxStatsTick >= 1000)
                {
                    double sec = (now - _lastRxStatsTick) / 1000.0;
                    RemoteStreamFps = (int)Math.Round(_rxFramesInSec / sec);
                    RemoteStreamBitrateKbps = _receivedVideoTracker.CurrentRateKbps;
                    _rxFramesInSec = 0;
                    _rxBytesInSec = 0;
                    _lastRxStatsTick = now;
                }

                if (string.IsNullOrEmpty(RemoteBroadcasterKey))
                {
                    RemoteBroadcasterKey = session.PeerKey;
                    RemoteBroadcasterName = session.PeerName;
                    OnRemoteStreamStateChanged?.Invoke(session.PeerKey, session.PeerName, null);
                }

                TotalFramesDecoded++;
                if (width > 0) RemoteStreamWidth = width;
                if (height > 0) RemoteStreamHeight = height;
                LastRemoteVideoFrameId = frameId;
                OnRemoteVideoFrameReceived?.Invoke(session.PeerKey, fullFrame, width, height);
                OnRemoteVideoFrameReceivedWithId?.Invoke(session.PeerKey, fullFrame, width, height, frameId);
            }
        }

        private string _localActiveStreamTitle = "";

        public void BroadcastStreamStarted(string streamTitle)
        {
            _localActiveStreamTitle = streamTitle;
            _broadcastingStreamStartTick = Stopwatch.GetTimestamp();
            lock (_watchersLock)
            {
                _activeWatchers.Clear();
            }
            foreach (var kvp in _sessions)
            {
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, new { t = "STREAM_START", title = streamTitle });
                }
            }
        }

        public void BroadcastStreamThumbnail(byte[] thumbJpeg)
        {
            if (thumbJpeg == null || thumbJpeg.Length == 0) return;
            string b64 = Convert.ToBase64String(thumbJpeg);
            foreach (var kvp in _sessions)
            {
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, new { t = "STREAM_THUMB", jpg = b64 });
                }
            }
        }

        public void BroadcastStreamStopped()
        {
            _localActiveStreamTitle = "";
            foreach (var kvp in _sessions)
            {
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, new { t = "STREAM_STOP" });
                }
            }
        }

        public void BroadcastP2PChatMessage(ChatMessage msg)
        {
            foreach (var kvp in _sessions)
            {
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, new { t = "P2P_CHAT", msg });
                }
            }
        }

        public bool IsControlChannelOpen(string peerKey)
        {
            return _sessions.TryGetValue(peerKey, out var s) && s.ControlChannel?.readyState == RTCDataChannelState.open;
        }

        public void SendAnnotationTo(string peerKey, object wirePayload)
        {
            if (_sessions.TryGetValue(peerKey, out var s) && s.ControlChannel?.readyState == RTCDataChannelState.open)
            {
                SendControlJson(s.ControlChannel, wirePayload);
            }
        }

        public void BroadcastAnnotation(object wirePayload, string? exceptPeerKey = null)
        {
            foreach (var kvp in _sessions)
            {
                if (!string.IsNullOrEmpty(exceptPeerKey) && kvp.Key == exceptPeerKey) continue;
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, wirePayload);
                }
            }
        }

        public void SendGameStreamTo(string peerKey, object wirePayload)
        {
            if (_sessions.TryGetValue(peerKey, out var s) && s.ControlChannel?.readyState == RTCDataChannelState.open)
            {
                SendControlJson(s.ControlChannel, wirePayload);
            }
        }

        public void BroadcastGameStream(object wirePayload, string? exceptPeerKey = null)
        {
            foreach (var kvp in _sessions)
            {
                if (!string.IsNullOrEmpty(exceptPeerKey) && kvp.Key == exceptPeerKey) continue;
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, wirePayload);
                }
            }
        }

        public async Task<RTCDataChannel?> CreateGameStreamDataChannelAsync(string peerKey)
        {
            if (_sessions.TryGetValue(peerKey, out var s))
            {
                var dc = await s.Connection.createDataChannel("runa-gs-tcp", new RTCDataChannelInit { ordered = true });
                if (dc != null)
                {
                    AttachDataChannelHandlers(s, dc);
                    return dc;
                }
            }
            return null;
        }

        public bool SendGameStreamUdpRaw(string peerKey, byte[] packet)
        {
            if (_sessions.TryGetValue(peerKey, out var s) && s.Connection.connectionState == RTCPeerConnectionState.connected)
            {
                try
                {
                    s.Connection.SendRtpRaw(SDPMediaTypesEnum.video, packet, (uint)Environment.TickCount, 0, VideoPayloadTypeId);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        public uint GetNextVideoFrameId() => Interlocked.Increment(ref _localVideoFrameSeq);

        /// <summary>
        /// Kodlanmış JPEG karesini doğrudan SRTP Video RTP üzerinden sıfır kuyruk birikmesiyle gönderir.
        /// Bir önceki karenin gönderimi henüz bitmediyse yeni kareyi atlayarak ağ ve CPU tıkanmasını %100 önler.
        /// </summary>
        public void BroadcastEncodedVideoFrame(byte[] jpegBytes, int width, int height, uint explicitFrameId = 0)
        {
            if (jpegBytes == null || jpegBytes.Length == 0 || _sessions.IsEmpty) return;

            if (Interlocked.Increment(ref _sendingFramesCount) > 2)
            {
                Interlocked.Decrement(ref _sendingFramesCount);
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    int maxDirect = Math.Clamp(_storage.Settings.P2PRelayMaxChildren, 1, 8);
                    var allConnected = _sessions.Values
                        .Where(s => s.Connection.connectionState == RTCPeerConnectionState.connected)
                        .OrderBy(s => s.LastRttMs <= 0 ? 500 : s.LastRttMs)
                        .ToList();

                    double streamAgeSec = (Stopwatch.GetTimestamp() - _broadcastingStreamStartTick) / (double)Stopwatch.Frequency;

                    List<PeerRtcSession> candidateTargets;
                    lock (_watchersLock)
                    {
                        if (_activeWatchers.Count > 0)
                        {
                            candidateTargets = allConnected.Where(s => _activeWatchers.Contains(s.PeerKey)).ToList();
                        }
                        else if (streamAgeSec > 5.0)
                        {
                            // Geriye dönük uyumluluk: 5 sn boyunca hiçbir WATCH_START gelmediyse eski eşler olabilir
                            candidateTargets = allConnected;
                        }
                        else
                        {
                            // Henüz hiçbir izleyici yayını izlemeye başlamadı
                            candidateTargets = new List<PeerRtcSession>();
                        }
                    }

                    var openTargets = candidateTargets.Take(maxDirect).ToList();

                    if (openTargets.Count == 0) return;

                    uint frameId = explicitFrameId > 0 ? explicitFrameId : Interlocked.Increment(ref _localVideoFrameSeq);
                    uint rtpTimestamp = frameId * 3000u; // 90kHz video RTP clock
                    int totalChunks = (jpegBytes.Length + MaxRtpChunkPayload - 1) / MaxRtpChunkPayload;
                    if (totalChunks <= 0 || totalChunks > 512) return;

                    int targetKbps = PacerTargetBitrateKbps > 0 ? PacerTargetBitrateKbps : 4000;
                    long pacerBytesPerSec = (long)((targetKbps * 1000.0 / 8.0) * 1.15);
                    long bucketCapacityBytes = Math.Max(MaxRtpChunkPayload * 2, jpegBytes.Length);
                    double frameBudgetMs = 1000.0 / 60.0;
                    long frameStartTick = Stopwatch.GetTimestamp();

                    for (int c = 0; c < totalChunks; c++)
                    {
                        int offset = c * MaxRtpChunkPayload;
                        int len = Math.Min(MaxRtpChunkPayload, jpegBytes.Length - offset);
                        byte[] pkt = new byte[14 + len];

                        BitConverter.TryWriteBytes(pkt.AsSpan(0, 2), ScreenMagic);
                        BitConverter.TryWriteBytes(pkt.AsSpan(2, 4), frameId);
                        BitConverter.TryWriteBytes(pkt.AsSpan(6, 2), (ushort)c);
                        BitConverter.TryWriteBytes(pkt.AsSpan(8, 2), (ushort)totalChunks);
                        BitConverter.TryWriteBytes(pkt.AsSpan(10, 2), (ushort)width);
                        BitConverter.TryWriteBytes(pkt.AsSpan(12, 2), (ushort)height);
                        Buffer.BlockCopy(jpegBytes, offset, pkt, 14, len);

                        int markerBit = (c == totalChunks - 1) ? 1 : 0;
                        for (int t = 0; t < openTargets.Count; t++)
                        {
                            var target = openTargets[t];
                            long now = Stopwatch.GetTimestamp();
                            double elapsedSec = (now - target.LastPacerRefillTick) / (double)Stopwatch.Frequency;
                            target.LastPacerRefillTick = now;
                            target.PacerTokens = Math.Min(bucketCapacityBytes, target.PacerTokens + (long)(elapsedSec * pacerBytesPerSec));

                            if (target.PacerTokens < pkt.Length)
                            {
                                long needed = pkt.Length - target.PacerTokens;
                                double waitMs = (needed * 1000.0) / pacerBytesPerSec;
                                if (waitMs > 0 && waitMs <= 2.0)
                                {
                                    Thread.SpinWait(Math.Clamp((int)(waitMs * 800), 50, 1600));
                                    long nowAfter = Stopwatch.GetTimestamp();
                                    double elapsedAfter = (nowAfter - target.LastPacerRefillTick) / (double)Stopwatch.Frequency;
                                    target.LastPacerRefillTick = nowAfter;
                                    target.PacerTokens = Math.Min(bucketCapacityBytes, target.PacerTokens + (long)(elapsedAfter * pacerBytesPerSec));
                                }
                            }
                            target.PacerTokens = Math.Max(0, target.PacerTokens - pkt.Length);

                            try
                            {
                                target.Connection.SendRtpRaw(
                                    SDPMediaTypesEnum.video,
                                    pkt,
                                    rtpTimestamp,
                                    markerBit,
                                    VideoPayloadTypeId);
                                target.SessionSentTracker.AddBytes(pkt.Length);
                            }
                            catch { }
                        }
                        _sentNetworkTracker.AddBytes((long)pkt.Length * openTargets.Count);

                        double elapsedFrameMs = (Stopwatch.GetTimestamp() - frameStartTick) * 1000.0 / Stopwatch.Frequency;
                        if (elapsedFrameMs > frameBudgetMs * 1.8 && c < totalChunks - 1)
                        {
                            Interlocked.Increment(ref _pacerDroppedFramesCount);
                            break;
                        }
                    }

                    long nowTick = Environment.TickCount64;
                    _sentVideoBytesInSec += (long)jpegBytes.Length * openTargets.Count;
                    if (nowTick - _lastSentVideoStatsTick >= 1000)
                    {
                        double sec = (nowTick - _lastSentVideoStatsTick) / 1000.0;
                        SentVideoBitrateKbps = (int)Math.Round((_sentVideoBytesInSec * 8.0 / 1000.0) / sec);
                        _sentVideoBytesInSec = 0;
                        _lastSentVideoStatsTick = nowTick;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _sendingFramesCount);
                }
            });
        }

        public void BroadcastVideoQualityState(int lvl, int h, int fps, int q, bool adaptive, string reason)
        {
            var state = new VideoQualityState
            {
                Lvl = lvl,
                H = h,
                Fps = fps,
                Q = q,
                Adaptive = adaptive,
                Reason = reason
            };
            string json = state.ToJson();
            BroadcastControlJson(json);
        }

        public void BroadcastControlJson(string json)
        {
            foreach (var kvp in _sessions)
            {
                var dc = kvp.Value.ControlChannel;
                if (dc != null && dc.readyState == RTCDataChannelState.open)
                {
                    SendControlJson(dc, json);
                }
            }
        }

        public int GetPeerRtt(string peerKey)
        {
            if (_sessions.TryGetValue(peerKey, out var s))
            {
                return s.LastRttMs;
            }
            return 0;
        }

        public void SendControlJsonToPeer(string peerKey, string json)
        {
            if (_sessions.TryGetValue(peerKey, out var s) && s.ControlChannel?.readyState == RTCDataChannelState.open)
            {
                SendControlJson(s.ControlChannel, json);
            }
        }

        public int GetDirectVideoReceiversCount()
        {
            return _sessions.Values.Count(s => s.Connection.connectionState == RTCPeerConnectionState.connected);
        }

        private void SendVqReportTimerTick()
        {
            if (string.IsNullOrEmpty(RemoteBroadcasterKey))
            {
                _vqFr = 0;
                _vqFd = 0;
                _vqGap = 0;
                _vqRd = 0;
                _vqBytes = 0;
                return;
            }

            int fd = _vqFd;
            foreach (var s in _sessions.Values)
            {
                fd += s.ConsumeDroppedFrames();
            }

            int kbps = _receivedVideoTracker.CurrentRateKbps;
            int fr = _vqFr;
            int gap = _vqGap;
            int rd = _vqRd;
            int jit = (int)Math.Round(_vqJitterMs);
            int lvl = _lastBroadcasterVqState?.Lvl ?? 1;
            double lossPct = RemoteStreamPacketLossPct;

            bool isDirect = _sessions.TryGetValue(RemoteBroadcasterKey, out var sess) && sess.Connection.connectionState == RTCPeerConnectionState.connected;
            string via = isDirect ? "direct" : "relay";

            var report = new VideoQualityReport
            {
                Fr = fr,
                Fd = fd,
                Gap = gap,
                Kbps = kbps,
                Jit = jit,
                Rd = rd,
                Lvl = lvl,
                LossPct = lossPct,
                Via = via
            };

            _vqFr = 0;
            _vqFd = 0;
            _vqGap = 0;
            _vqRd = 0;
            _vqBytes = 0;

            string json = report.ToJson();
            SendControlJsonToPeer(RemoteBroadcasterKey, json);
        }

        // ---------------------------------------------------------------------
        // UDP/SRTP AĞ GECİKMESİ ÖLÇÜMÜ
        // Paket (20 bayt, little-endian): [0-1] sihirli 0x5054, [2] tür (1=ping, 2=pong), [3] ayrılmış,
        // [4-11] ping gönderenin Stopwatch zaman damgası (pong'da aynen geri döner),
        // [12-15] paketi gönderenin ortalama Opus kodlama süresi (µs, bilinmiyorsa -1),
        // [16-17] Opus algoritmik gecikmesi (10 µs birim), [18] Opus çerçeve süresi (0,5 ms birim), [19] mikrofon tamponu (ms).
        // Karşı taraf PING'i, SIPSorcery'nin RTP alma iş parçacığında gelir gelmez yanıtlar; SCTP/veri kanalı
        // katmanından geçmediği için ölçüm yalnızca ağ + SRTP şifreleme süresini içerir.
        // ---------------------------------------------------------------------

        private byte[] BuildNetPingPacket(byte type, long ticks)
        {
            var pkt = new byte[NetPingPacketSize];
            BitConverter.TryWriteBytes(pkt.AsSpan(0, 2), NetPingMagic);
            pkt[2] = type;
            BitConverter.TryWriteBytes(pkt.AsSpan(4, 8), ticks);

            int encodeUs = _encodeUsAvg < 0 ? -1 : (int)Math.Min(int.MaxValue, Math.Round(_encodeUsAvg));
            BitConverter.TryWriteBytes(pkt.AsSpan(12, 4), encodeUs);

            int la = _encoderLookaheadSamples;
            ushort lookaheadTenUs = la < 0 ? ushort.MaxValue
                : (ushort)Math.Min(ushort.MaxValue - 1, Math.Round(la * 100000.0 / AudioEngine.SampleRate));
            BitConverter.TryWriteBytes(pkt.AsSpan(16, 2), lookaheadTenUs);

            pkt[18] = (byte)Math.Clamp(Math.Round(_audio.CurrentFrameMs * 2), 0, 254); // yarım ms birim (2,5 ms çerçeve = 5)
            double cap = _audio.MeasuredCaptureBufferMs;
            pkt[19] = cap < 0 ? (byte)0 : (byte)Math.Clamp(Math.Round(cap), 1, 254); // 0 = henüz ölçülmedi
            return pkt;
        }

        private void SendNetPing(PeerRtcSession session)
        {
            try
            {
                var pkt = BuildNetPingPacket(1, Stopwatch.GetTimestamp());
                session.Connection.SendRtpRaw(SDPMediaTypesEnum.video, pkt, 0, 0, VideoPayloadTypeId);
            }
            catch { }
        }

        private void HandleNetPingPacket(PeerRtcSession session, byte[] payload)
        {
            if (payload.Length < NetPingPacketSize) return;
            byte type = payload[2];
            long ticks = BitConverter.ToInt64(payload, 4);

            // Paketi gönderenin ses kodlama bilgileri (ping ve pong ikisinde de gelir)
            int encUs = BitConverter.ToInt32(payload, 12);
            ushort laTenUs = BitConverter.ToUInt16(payload, 16);
            session.PeerEncodeMs = encUs < 0 ? -1 : Math.Min(encUs, 1_000_000) / 1000.0;
            session.PeerLookaheadMs = laTenUs == ushort.MaxValue ? -1 : laTenUs / 100.0;
            session.PeerFrameMs = payload[18] == 0 ? -1 : payload[18] / 2.0;
            session.PeerCaptureBufferMs = payload[19] == 0 ? -1 : payload[19];

            if (type == 1)
            {
                // Hemen yanıtla (aynı zaman damgası + kendi kodlama bilgilerimiz)
                try
                {
                    var pong = BuildNetPingPacket(2, ticks);
                    session.Connection.SendRtpRaw(SDPMediaTypesEnum.video, pong, 0, 0, VideoPayloadTypeId);
                }
                catch { }
                return;
            }

            if (type != 2) return;
            double rttMs = (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
            if (rttMs < 0 || rttMs > 10_000) return; // bizim göndermediğimiz/bozuk zaman damgası

            var (median, jitter) = session.RecordNetRttSample(rttMs);
            OnPeerPingUpdated?.Invoke(session.PeerKey, session.LastRttMs, (int)Math.Round(jitter));
            OnPeerLatencyUpdated?.Invoke(session.PeerKey, BuildLatencyInfo(session, median, jitter));
        }

        private Models.PeerLatencyInfo BuildLatencyInfo(PeerRtcSession session, double rttMs, double jitterMs)
        {
            return new Models.PeerLatencyInfo
            {
                NetworkRttMs = rttMs,
                NetworkJitterMs = jitterMs,
                IsUdpPath = true,
                PeerCaptureBufferMs = session.PeerCaptureBufferMs,
                PeerFrameMs = session.PeerFrameMs,
                PeerEncodeMs = session.PeerEncodeMs,
                PeerLookaheadMs = session.PeerLookaheadMs,
                JitterBufferMs = _audio.GetPeerBufferedMs(session.PeerKey),
                LocalDecodeMs = session.LocalDecodeMs,
                PlaybackBufferMs = _audio.MeasuredPlaybackBufferMs,
                JitterTargetMs = _audio.GetPeerJitterTargetMs(session.PeerKey),
                Underruns = _audio.GetPeerUnderruns(session.PeerKey),
                PlaybackMode = _audio.ActivePlaybackMode,
                PlaybackGlitches = _audio.PlaybackGlitches
            };
        }

        private void RelayScreenPacketToChildrenIfNeeded(string broadcasterPeerKey, byte[] packet, uint rtpTimestamp, int markerBit)
        {
            // Yalnızca gerçek yayıncıdan gelen paketler aktarılır; başka bir eş, röle üzerinden
            // diğer izleyicilere sahte kare enjekte edemesin.
            if (string.IsNullOrEmpty(RemoteBroadcasterKey) || broadcasterPeerKey != RemoteBroadcasterKey) return;

            string[] children = GetRelayChildKeys(broadcasterPeerKey);
            for (int i = 0; i < children.Length; i++)
            {
                string childKey = children[i];
                if (_sessions.TryGetValue(childKey, out var childSession) &&
                    childSession.Connection.connectionState == RTCPeerConnectionState.connected)
                {
                    try
                    {
                        childSession.Connection.SendRtpRaw(
                            SDPMediaTypesEnum.video,
                            packet,
                            rtpTimestamp,
                            markerBit,
                            VideoPayloadTypeId);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Bu düğümün röle ile beslemesi gereken izleyiciler. Video paketi başına değil, en fazla saniyede bir
        /// (veya katılımcı/yayıncı değişince) hesaplanır; 1080p60 yayında saniyede binlerce sıralama ve liste
        /// ayırmasından kaçınılır.
        /// </summary>
        private string[] GetRelayChildKeys(string broadcasterPeerKey)
        {
            long now = Environment.TickCount64;
            int participantCount = _signaling.Participants.Count;
            if (now - _relayCacheTick < 1000 &&
                participantCount == _relayCacheParticipantCount &&
                broadcasterPeerKey == _relayCacheBroadcaster)
            {
                return _relayChildKeys;
            }

            int maxChildren = Math.Clamp(_storage.Settings.P2PRelayMaxChildren, 1, 8);
            List<string> candidateKeys;
            lock (_watchersLock)
            {
                candidateKeys = _activeWatchers.Count > 0
                    ? _activeWatchers.Where(k => k != broadcasterPeerKey).ToList()
                    : _signaling.Participants.Keys.Where(k => k != broadcasterPeerKey).ToList();
            }

            var allPeerKeys = candidateKeys
                .Append(_storage.CurrentUser.PeerKey)
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            var result = new List<string>();
            int myIdx = allPeerKeys.IndexOf(_storage.CurrentUser.PeerKey);
            if (myIdx >= 0 && myIdx < maxChildren)
            {
                for (int childIdx = maxChildren + myIdx; childIdx < allPeerKeys.Count; childIdx += maxChildren)
                    result.Add(allPeerKeys[childIdx]);
            }

            _relayChildKeys = result.ToArray();
            _relayCacheTick = now;
            _relayCacheParticipantCount = participantCount;
            _relayCacheBroadcaster = broadcasterPeerKey;
            return _relayChildKeys;
        }

        private static void SendControlJson(RTCDataChannel dc, object payload)
        {
            try
            {
                string text = payload is string str ? str : JsonSerializer.Serialize(payload);
                dc.send(text);
            }
            catch { }
        }

        /// <summary>
        /// Veri kanalı (SCTP) ping'i yalnızca yedektir: karşı taraf UDP ölçüm paketine hiç yanıt vermediyse
        /// (eski sürüm) ve bağlantı kurulalı 4 sn geçtiyse kullanılır. Böylece ilk saniyede SCTP değeri
        /// görünüp sonra UDP değerine atlamaz.
        /// </summary>
        private static bool UseSctpPingFallback(PeerRtcSession session)
        {
            if (session.LastNetPongTicks > 0) return !session.HasRecentNetPong;
            long since = session.ConnectionEstablishedTicks > 0 ? session.ConnectionEstablishedTicks : session.CreatedTicks;
            return (Stopwatch.GetTimestamp() - since) > Stopwatch.Frequency * 4;
        }

        private void SendRttPingsToAll()
        {
            long nowTicks = Stopwatch.GetTimestamp();
            string msg = $"PING:{nowTicks}";

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                var connState = session.Connection.connectionState;
                double sessionAgeSec = (nowTicks - session.CreatedTicks) / (double)Stopwatch.Frequency;

                if (connState != RTCPeerConnectionState.connected)
                {
                    if (sessionAgeSec >= 15.0 && !session.IsP2pFailed)
                    {
                        session.IsP2pFailed = true;
                        OnPeerPingStatusChanged?.Invoke(session.PeerKey, Models.PeerPingStatus.NoP2p);
                    }
                    continue;
                }

                // Bağlantı bağlı; zaman aşımı kontrolü (6 sn PONG gelmezse)
                if (session.LastPongReceivedTicks > 0)
                {
                    double pongAgeSec = (nowTicks - session.LastPongReceivedTicks) / (double)Stopwatch.Frequency;
                    if (pongAgeSec >= 6.0)
                    {
                        if (!session.IsTimedOut)
                        {
                            session.IsTimedOut = true;
                            session.LastRttMs = -1;
                            OnPeerPingStatusChanged?.Invoke(session.PeerKey, Models.PeerPingStatus.Timeout);
                        }
                    }
                }
                else
                {
                    double establishedAgeSec = session.ConnectionEstablishedTicks > 0
                        ? (nowTicks - session.ConnectionEstablishedTicks) / (double)Stopwatch.Frequency
                        : sessionAgeSec;
                    if (establishedAgeSec >= 15.0 && !session.IsP2pFailed)
                    {
                        session.IsP2pFailed = true;
                        OnPeerPingStatusChanged?.Invoke(session.PeerKey, Models.PeerPingStatus.NoP2p);
                    }
                }

                // Asıl ölçüm: UDP/SRTP ağ ölçüm paketi (her saniye)
                SendNetPing(session);

                // Karşı taraf UDP ölçümünü yanıtlıyorsa veri kanalı ölçümüne gerek yok
                if (!UseSctpPingFallback(session)) continue;

                // Karşı taraf eski sürümse (runa-ping 5 sn içinde açılmadıysa kontrol kanalına geri dön)
                if ((session.PingChannel == null || session.PingChannel.readyState != RTCDataChannelState.open) && sessionAgeSec >= 5.0)
                {
                    session.PingFallbackToControl = true;
                }

                // Ping gönderimi
                RTCDataChannel? targetDc = null;
                if (session.PingChannel != null && session.PingChannel.readyState == RTCDataChannelState.open)
                {
                    targetDc = session.PingChannel;
                }
                else if (session.PingFallbackToControl && session.ControlChannel != null && session.ControlChannel.readyState == RTCDataChannelState.open)
                {
                    targetDc = session.ControlChannel;
                }

                if (targetDc != null)
                {
                    try { targetDc.send(msg); } catch { }
                }
            }
        }

        private void EncodeAndBroadcastAudioFrame(short[] pcm48kMono, int sampleCount)
        {
            if (_sessions.IsEmpty || sampleCount <= 0) return;

            Span<byte> opusOut = stackalloc byte[512];
            int encodedLen;

            lock (_encoderLock)
            {
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    encodedLen = _opusEncoder.Encode(pcm48kMono.AsSpan(0, sampleCount), sampleCount, opusOut, opusOut.Length);
                    double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
                    _encodeUsAvg = _encodeUsAvg < 0 ? us : _encodeUsAvg * 0.95 + us * 0.05;
                    _encoderLookaheadSamples = _opusEncoder.Lookahead;
                }
                catch
                {
                    return;
                }
            }

            if (encodedLen <= 0) return;

            byte[] packet = opusOut[..encodedLen].ToArray();
            uint durationRtpUnits = (uint)sampleCount;

            foreach (var kvp in _sessions)
            {
                var pc = kvp.Value.Connection;
                if (pc.connectionState == RTCPeerConnectionState.connected)
                {
                    try
                    {
                        pc.SendAudio(durationRtpUnits, packet);
                    }
                    catch { }
                }
            }
        }

        private async void HandleSignalingMessage(string fromPeerKey, string fromName, string sigType, string data)
        {
            try
            {
                if (_signaling.ActiveEngineName == "rust")
                {
                    // RustRealtimeEngine handles all WebRTC signaling natively via runa-net.
                    // WebRtcService must not process or respond to signaling in Rust engine mode.
                    return;
                }

                if (string.IsNullOrEmpty(fromPeerKey) || fromPeerKey.Length > 160) return;
                if (sigType is not ("sdp-offer" or "sdp-answer" or "ice-candidate")) return;
                if (data == null || data.Length > MaxSdpChars) return;

                // Bilinmeyen bir anahtardan gelen sinyal yalnızca kapasite varsa yeni bağlantı açabilir
                // (açık MQTT konusuna yazan biri sınırsız RTCPeerConnection oluşturup kaynak tüketemesin).
                if (!_sessions.ContainsKey(fromPeerKey) &&
                    (_sessions.Count >= MaxPeerSessions ||
                     (sigType != "sdp-offer" && !_signaling.Participants.ContainsKey(fromPeerKey))))
                {
                    if (sigType == "ice-candidate" && _sessions.Count < MaxPeerSessions)
                    {
                        BufferEarlyIceCandidate(fromPeerKey, data);
                    }
                    return;
                }

                // Teklif çakışması (glare): iki taraf da teklif göndermişse — örneğin MQTT gecikmesi yüzünden
                // asıl başlatıcının teklifi geç ulaştı ve karşı taraf 3,5 sn'lik yedek ile kendi teklifini yolladı —
                // anahtar sırasına göre asıl başlatıcı gelen teklifi yok sayar; diğer taraf kendi teklifinden
                // vazgeçip gelen teklife yanıt verir. Aksi hâlde iki oturum da yanıt beklerken sonsuza dek kilitlenir.
                RTCIceCandidateInit[]? carriedIce = null;
                if (sigType == "sdp-offer" &&
                    _sessions.TryGetValue(fromPeerKey, out var existingSession) &&
                    existingSession.LastRemoteOfferSdp != data)
                {
                    bool replace = false;
                    if (existingSession.IsInitiator && existingSession.Connection.remoteDescription == null)
                    {
                        bool iAmPreferredInitiator = string.CompareOrdinal(_storage.CurrentUser.PeerKey, fromPeerKey) < 0;
                        if (iAmPreferredInitiator) return;
                        carriedIce = existingSession.TakePendingIceCandidates();
                        replace = true;
                    }
                    else if (existingSession.IsInitiator)
                    {
                        // Yanıtı zaten almış başlatıcıya gecikmeli ulaşan eski bir çakışma teklifi: yok say
                        // (karşı taraf gerçekten yeniden başladıysa bu bağlantı düşünce yeniden gönderilen teklif kabul edilir).
                        return;
                    }
                    else if (existingSession.Connection.remoteDescription != null)
                    {
                        // Yanıtladığımız tarafın farklı yeni teklifi: uygulamayı yeniden başlatmış olabilir.
                        // Yeniden anlaşma desteklenmediği için eski bağlantı kapatılıp yenisi kurulur.
                        replace = true;
                    }

                    if (replace && _sessions.TryRemove(fromPeerKey, out var dropped))
                    {
                        _audio.RemovePeerChannel(fromPeerKey);
                        dropped.Dispose();
                    }
                }

                var session = await ConnectToPeerInternalAsync(fromPeerKey, fromName, isInitiator: false);
                var pc = session.Connection;
                if (carriedIce != null)
                {
                    foreach (var c in carriedIce) session.AddOrQueueIceCandidate(c);
                }

                if (sigType == "sdp-offer")
                {
                    // Yinelenen teklif: yanıtımız yolda kaybolmuş demektir; aynı yanıtı ve adaylarımızı tekrar yolla
                    if (session.LastRemoteOfferSdp == data && !string.IsNullOrEmpty(session.LocalAnswerSdp))
                    {
                        await _signaling.SendSignalAsync(fromPeerKey, "sdp-answer", session.LocalAnswerSdp);
                        foreach (var ice in session.GetLocalIce())
                        {
                            await _signaling.SendSignalAsync(fromPeerKey, "ice-candidate", ice);
                        }
                        return;
                    }

                    var init = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.offer,
                        sdp = data
                    };
                    var setRes = pc.setRemoteDescription(init);
                    if (setRes == SetDescriptionResultEnum.OK)
                    {
                        session.FlushPendingIceCandidates();
                        var answer = pc.createAnswer(null);
                        await pc.setLocalDescription(answer);
                        session.LastRemoteOfferSdp = data;
                        session.LocalAnswerSdp = answer.sdp;
                        await _signaling.SendSignalAsync(fromPeerKey, "sdp-answer", answer.sdp);
                    }
                }
                else if (sigType == "sdp-answer")
                {
                    var init = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = data
                    };
                    if (pc.setRemoteDescription(init) == SetDescriptionResultEnum.OK)
                    {
                        session.FlushPendingIceCandidates();
                    }
                }
                else if (sigType == "ice-candidate")
                {
                    var cand = JsonSerializer.Deserialize<RTCIceCandidateInit>(data);
                    if (cand != null && !string.IsNullOrWhiteSpace(cand.candidate))
                    {
                        session.AddOrQueueIceCandidate(cand);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebRTC Signaling Error] {ex.Message}");
            }
        }

        private void BufferEarlyIceCandidate(string peerKey, string candJson)
        {
            long now = Environment.TickCount64;
            foreach (var kv in _earlyIce)
            {
                if (now - kv.Value.CreatedTick > EarlyIceTtlMs) _earlyIce.TryRemove(kv.Key, out _);
            }

            if (!_earlyIce.TryGetValue(peerKey, out var buf))
            {
                if (_earlyIce.Count >= MaxEarlyIcePeers) return;
                buf = _earlyIce.GetOrAdd(peerKey, _ => new EarlyIceBuffer());
            }

            lock (buf)
            {
                if (buf.Candidates.Count < MaxEarlyIcePerPeer) buf.Candidates.Add(candJson);
            }
        }

        /// <summary>Oturum oluşturulunca, öncesinde gelmiş adayları uzak açıklama beklenen kuyruğa aktarır.</summary>
        private void DrainEarlyIceCandidates(PeerRtcSession session)
        {
            if (!_earlyIce.TryRemove(session.PeerKey, out var buf)) return;
            if (Environment.TickCount64 - buf.CreatedTick > EarlyIceTtlMs) return;

            string[] items;
            lock (buf) { items = buf.Candidates.ToArray(); }
            foreach (var json in items)
            {
                try
                {
                    var cand = JsonSerializer.Deserialize<RTCIceCandidateInit>(json);
                    if (cand != null && !string.IsNullOrWhiteSpace(cand.candidate))
                    {
                        session.AddOrQueueIceCandidate(cand);
                    }
                }
                catch { }
            }
        }

        public void RemovePeer(string peerKey)
        {
            if (_sessions.TryRemove(peerKey, out var session))
            {
                _audio.RemovePeerChannel(peerKey);
                if (RemoteBroadcasterKey == peerKey)
                {
                    RemoteBroadcasterKey = "";
                    RemoteBroadcasterName = "";
                    RemoteStreamTitle = "";
                    RemoteStreamFps = 0;
                    RemoteStreamBitrateKbps = 0;
                    OnRemoteStreamStateChanged?.Invoke("", "", null);
                }
                _lastPeerVqReports.TryRemove(peerKey, out _);
                lock (_watchersLock)
                {
                    _activeWatchers.Remove(peerKey);
                }
                session.Dispose();
                OnPeerRttReset?.Invoke(peerKey);
            }
        }

        public void ResetRemoteStream()
        {
            RemoteBroadcasterKey = "";
            RemoteBroadcasterName = "";
            RemoteStreamTitle = "";
            RemoteStreamFps = 0;
            RemoteStreamBitrateKbps = 0;
            OnRemoteStreamStateChanged?.Invoke("", "", null);
        }

        public void CloseAll()
        {
            foreach (var kvp in _sessions)
            {
                _audio.RemovePeerChannel(kvp.Key);
                kvp.Value.Dispose();
                OnPeerRttReset?.Invoke(kvp.Key);
            }
            _sessions.Clear();
            _earlyIce.Clear();
            _localActiveStreamTitle = "";
            RemoteBroadcasterKey = "";
            RemoteBroadcasterName = "";
            RemoteStreamTitle = "";
            RemoteStreamFps = 0;
            RemoteStreamBitrateKbps = 0;
        }

        public void Dispose()
        {
            _rttPingTimer?.Dispose();
            CloseAll();
            _audio.OnOutgoingPcmFrameReady -= EncodeAndBroadcastAudioFrame;
            _signaling.OnSignalReceived -= HandleSignalingMessage;
        }

        private sealed class PeerRtcSession : IDisposable
        {
            public string PeerKey { get; }
            public string PeerName { get; }
            public RTCPeerConnection Connection { get; }
            public RTCDataChannel? ControlChannel { get; set; }
            public RTCDataChannel? PingChannel { get; set; }
            public RTCDataChannel? ScreenChannel { get; set; }
            public RTCDataChannel? GameStreamTcpChannel { get; set; }
            public bool PingFallbackToControl { get; set; } = false;
            public long CreatedTicks { get; } = Stopwatch.GetTimestamp();
            public long ConnectionEstablishedTicks { get; set; } = 0;
            public long LastPongReceivedTicks { get; set; } = 0;
            public bool IsTimedOut { get; set; } = false;
            public bool IsP2pFailed { get; set; } = false;
            public int LastRttMs { get; set; } = 0;

            // --- UDP/SRTP ağ ölçümü (ses/görüntü paketleriyle aynı yol) ---
            private readonly List<double> _netRttSamples = new(10);
            public long LastNetPongTicks { get; set; } = 0;
            public double NetRttMedianMs { get; private set; } = -1;
            public double NetJitterMs { get; private set; } = -1;
            /// <summary>Karşı taraf son 5 sn içinde UDP ölçüm paketine yanıt verdiyse true (yeni sürüm).</summary>
            public bool HasRecentNetPong =>
                LastNetPongTicks > 0 && (Stopwatch.GetTimestamp() - LastNetPongTicks) < Stopwatch.Frequency * 5;

            // Karşı tarafın bildirdiği ses kodlama bilgileri
            public double PeerEncodeMs { get; set; } = -1;
            public double PeerLookaheadMs { get; set; } = -1;
            public double PeerFrameMs { get; set; } = -1;
            public double PeerCaptureBufferMs { get; set; } = -1;

            // Bu eşten gelen sesin bizdeki çözme süresi (üstel ortalama, µs)
            private double _decodeUsAvg = -1;
            public double LocalDecodeMs => _decodeUsAvg < 0 ? -1 : _decodeUsAvg / 1000.0;

            public (double median, double jitter) RecordNetRttSample(double rttMs)
            {
                lock (_netRttSamples)
                {
                    _netRttSamples.Add(rttMs);
                    if (_netRttSamples.Count > 10) _netRttSamples.RemoveAt(0);

                    int k = Math.Min(_netRttSamples.Count, 5);
                    var recent = _netRttSamples.TakeLast(k).OrderBy(x => x).ToList();
                    double median = recent[k / 2];

                    double jitter = 0;
                    if (_netRttSamples.Count >= 2)
                    {
                        double diffSum = 0;
                        for (int i = 1; i < _netRttSamples.Count; i++)
                            diffSum += Math.Abs(_netRttSamples[i] - _netRttSamples[i - 1]);
                        jitter = diffSum / (_netRttSamples.Count - 1);
                    }

                    NetRttMedianMs = median;
                    NetJitterMs = jitter;
                    LastNetPongTicks = Stopwatch.GetTimestamp();
                    // Zaman aşımı, röle sıralaması ve telemetri bu değeri kullanır
                    LastRttMs = Math.Max(1, (int)Math.Round(median));
                    LastMedianRttMs = LastRttMs;
                    LastJitterMs = (int)Math.Round(jitter);
                    LastPongReceivedTicks = LastNetPongTicks;
                    IsTimedOut = false;
                    return (median, jitter);
                }
            }

            public int LastMedianRttMs { get; set; } = -1;
            public int LastJitterMs { get; set; } = 0;
            private readonly List<int> _rttSamples = new(10);
            public SlidingRateTracker SessionSentTracker { get; } = new();
            public long PacerTokens { get; set; } = 65536;
            public long LastPacerRefillTick { get; set; } = Stopwatch.GetTimestamp();

            private long _totalExpectedChunksInLossWindow = 0;
            private long _totalMissingChunksInLossWindow = 0;
            private long _lastLossWindowTick = Stopwatch.GetTimestamp();
            public double CurrentLossPct { get; private set; } = 0.0;

            public void RecordChunkLossWindow(long expected, long missing)
            {
                _totalExpectedChunksInLossWindow += expected;
                _totalMissingChunksInLossWindow += missing;
                long now = Stopwatch.GetTimestamp();
                if ((now - _lastLossWindowTick) * 1000.0 / Stopwatch.Frequency >= 1000.0)
                {
                    if (_totalExpectedChunksInLossWindow > 0)
                    {
                        CurrentLossPct = Math.Clamp((_totalMissingChunksInLossWindow * 100.0) / _totalExpectedChunksInLossWindow, 0.0, 100.0);
                    }
                    else
                    {
                        CurrentLossPct = 0.0;
                    }
                    _totalExpectedChunksInLossWindow = 0;
                    _totalMissingChunksInLossWindow = 0;
                    _lastLossWindowTick = now;
                }
            }

            public (int median, int jitter) RecordRttSample(int rawSampleMs)
            {
                lock (_rttSamples)
                {
                    _rttSamples.Add(rawSampleMs);
                    if (_rttSamples.Count > 10) _rttSamples.RemoveAt(0);

                    int k = Math.Min(_rttSamples.Count, 5);
                    var recent = _rttSamples.TakeLast(k).OrderBy(x => x).ToList();
                    int median = recent[k / 2];

                    int jitter = 0;
                    if (_rttSamples.Count >= 2)
                    {
                        int diffSum = 0;
                        for (int i = 1; i < _rttSamples.Count; i++)
                        {
                            diffSum += Math.Abs(_rttSamples[i] - _rttSamples[i - 1]);
                        }
                        jitter = (int)Math.Round((double)diffSum / (_rttSamples.Count - 1));
                    }

                    LastMedianRttMs = median;
                    LastJitterMs = jitter;
                    LastRttMs = median;
                    LastPongReceivedTicks = Stopwatch.GetTimestamp();
                    IsTimedOut = false;
                    return (median, jitter);
                }
            }
            public bool IsInitiator { get; init; }

            // Kayıp MQTT mesajlarına karşı yeniden gönderim için yerel SDP ve ICE adayları
            public string? LocalOfferSdp { get; set; }
            public string? LocalAnswerSdp { get; set; }
            public string? LastRemoteOfferSdp { get; set; }
            private readonly List<string> _localIce = new();
            public void RememberLocalIce(string json) { lock (_localIce) { if (_localIce.Count < 40) _localIce.Add(json); } }
            public string[] GetLocalIce() { lock (_localIce) { return _localIce.ToArray(); } }

            private readonly IOpusDecoder _decoder;
            private readonly float[] _decodeBuffer = new float[2880]; // 60ms max @ 48kHz mono
            private readonly object _iceLock = new();
            private readonly List<RTCIceCandidateInit> _pendingIce = new();
            private bool _remoteDescSet = false;

            // Video çoklu-kare birleştirici ring buffer (UDP paket çakışmalarında %0 kare kaybı)
            private sealed class VideoFrameSlot
            {
                public uint FrameId;
                public byte[][]? Chunks;
                public int ReceivedCount;
                public int TotalChunks;
                public long CreatedTick;
            }

            private int _droppedIncompleteFrames = 0;
            public int ConsumeDroppedFrames()
            {
                return Interlocked.Exchange(ref _droppedIncompleteFrames, 0);
            }

            private readonly object _videoLock = new();
            private readonly VideoFrameSlot?[] _frameSlots = new VideoFrameSlot?[16];
            private uint _lastCompletedFrameId = 0;

            public PeerRtcSession(string peerKey, string peerName, RTCPeerConnection pc)
            {
                PeerKey = peerKey;
                PeerName = peerName;
                Connection = pc;
                _decoder = OpusCodecFactory.CreateDecoder(AudioEngine.SampleRate, 1);
            }

            public int DecodeOpusPacket(byte[] opusPayload, out float[] pcmMono)
            {
                pcmMono = _decodeBuffer;
                long t0 = Stopwatch.GetTimestamp();
                int n = _decoder.Decode(opusPayload.AsSpan(), _decodeBuffer.AsSpan(), _decodeBuffer.Length, false);
                double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
                _decodeUsAvg = _decodeUsAvg < 0 ? us : _decodeUsAvg * 0.95 + us * 0.05;
                return n;
            }

            public void AddOrQueueIceCandidate(RTCIceCandidateInit cand)
            {
                lock (_iceLock)
                {
                    if (_remoteDescSet || Connection.remoteDescription != null)
                    {
                        Connection.addIceCandidate(cand);
                    }
                    else
                    {
                        _pendingIce.Add(cand);
                    }
                }
            }

            public void FlushPendingIceCandidates()
            {
                lock (_iceLock)
                {
                    _remoteDescSet = true;
                    foreach (var c in _pendingIce)
                    {
                        try { Connection.addIceCandidate(c); } catch { }
                    }
                    _pendingIce.Clear();
                }
            }

            /// <summary>Uzak açıklama gelmeden kuyruğa alınmış adayları devralır (çakışmada oturum değişirken kaybolmasın).</summary>
            public RTCIceCandidateInit[] TakePendingIceCandidates()
            {
                lock (_iceLock)
                {
                    var items = _pendingIce.ToArray();
                    _pendingIce.Clear();
                    return items;
                }
            }

            public byte[]? AssembleVideoChunk(uint frameId, ushort chunkIdx, ushort totalChunks, byte[] src, int offset, int len)
            {
                if (totalChunks == 0 || totalChunks > 512 || chunkIdx >= totalChunks) return null;

                lock (_videoLock)
                {
                    if (frameId <= _lastCompletedFrameId && (_lastCompletedFrameId - frameId) < 500)
                        return null;

                    long now = Environment.TickCount64;

                    // 500 ms'den eski tamamlanmamış yuvaları düşür ve say
                    for (int s = 0; s < _frameSlots.Length; s++)
                    {
                        var sl = _frameSlots[s];
                        if (sl != null && sl.FrameId != frameId && sl.ReceivedCount < sl.TotalChunks && (now - sl.CreatedTick > 500))
                        {
                            Interlocked.Increment(ref _droppedIncompleteFrames);
                            int missing = sl.TotalChunks - sl.ReceivedCount;
                            RecordChunkLossWindow(sl.TotalChunks, missing);
                            _frameSlots[s] = null;
                        }
                    }

                    int slotIdx = (int)(frameId % (uint)_frameSlots.Length);
                    var slot = _frameSlots[slotIdx];

                    if (slot != null && (slot.FrameId != frameId || slot.TotalChunks != totalChunks || slot.Chunks == null))
                    {
                        if (slot.ReceivedCount < slot.TotalChunks)
                        {
                            Interlocked.Increment(ref _droppedIncompleteFrames);
                            int missing = slot.TotalChunks - slot.ReceivedCount;
                            RecordChunkLossWindow(slot.TotalChunks, missing);
                        }
                        slot = null;
                    }

                    if (slot == null)
                    {
                        slot = new VideoFrameSlot
                        {
                            FrameId = frameId,
                            TotalChunks = totalChunks,
                            Chunks = new byte[totalChunks][],
                            ReceivedCount = 0,
                            CreatedTick = now
                        };
                        _frameSlots[slotIdx] = slot;
                    }

                    var targetSlot = slot!;
                    if (targetSlot.Chunks == null) return null;

                    if (targetSlot.Chunks[chunkIdx] == null)
                    {
                        var slice = new byte[len];
                        Buffer.BlockCopy(src, offset, slice, 0, len);
                        targetSlot.Chunks[chunkIdx] = slice;
                        targetSlot.ReceivedCount++;
                    }

                    if (targetSlot.ReceivedCount == targetSlot.TotalChunks)
                    {
                        RecordChunkLossWindow(targetSlot.TotalChunks, 0);
                        _lastCompletedFrameId = frameId;
                        int totalLen = 0;
                        for (int i = 0; i < targetSlot.TotalChunks; i++)
                        {
                            var c = targetSlot.Chunks[i];
                            if (c != null) totalLen += c.Length;
                        }

                        byte[] assembled = new byte[totalLen];
                        int pos = 0;
                        for (int i = 0; i < targetSlot.TotalChunks; i++)
                        {
                            var c = targetSlot.Chunks[i];
                            if (c != null)
                            {
                                Buffer.BlockCopy(c, 0, assembled, pos, c.Length);
                                pos += c.Length;
                            }
                        }
                        _frameSlots[slotIdx] = null;
                        return assembled;
                    }
                    return null;
                }
            }

            public void Dispose()
            {
                try { PingChannel?.close(); } catch { }
                try { ControlChannel?.close(); } catch { }
                try { ScreenChannel?.close(); } catch { }
                try { GameStreamTcpChannel?.close(); } catch { }
                try { Connection.close(); } catch { }
            }
        }
    }
}
