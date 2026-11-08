using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using RunaVoice_WinUI.Services.GameStream;

namespace RunaVoice_WinUI.Services.Engine;

public sealed unsafe class RustRealtimeEngine : IRealtimeEngine
{
    private IntPtr _nativeEngine = IntPtr.Zero;
    private readonly EngineEventPump _eventPump;
    private readonly LegacyRealtimeEngine _fallbackDelegate;
    private readonly StorageService _storage;
    private readonly SignalingService _signaling;
    private bool _disposed;
    private bool _pongReceived;

    private bool _isMuted;
    private bool _isDeafened;
    private bool _isPttHeld;
    private float _currentInputDb = -65f;
    private float _currentVadProbability;
    private double _currentVuLevel;
    private bool _isRecordingMicTest;
    private bool _prevLocalSpeaking;
    private readonly Dictionary<string, (string Name, float GainDb, bool Speaking)> _peerLevelStats = new(StringComparer.OrdinalIgnoreCase);

    public string EngineName => "rust";
    public bool IsRustEngine => true;

    public event Action<string>? OnEngineFallback;
    public event Action<string>? OnDiagnosticLog;

    public static string? LastInitErrorDetails { get; private set; }
    public static string? LastInitErrorSummary { get; private set; }

    public static bool TryCreate(
        StorageService storage,
        SignalingService signaling,
        AudioEngine audio,
        WebRtcService webrtc,
        ScreenCaptureService screenCapture,
        out IRealtimeEngine engine,
        out string? errorMessage)
    {
        errorMessage = null;
        LastInitErrorDetails = null;
        LastInitErrorSummary = null;
        try
        {
            if (!EngineNative.IsDllAvailable())
            {
                LastInitErrorSummary = "runa_engine.dll bulunamadı";
                LastInitErrorDetails = "runa_engine.dll veya SHA-256 doğrulama dosyası uygulama dizininde bulunamadı.";
                errorMessage = "Yeni ses motoru başlatılamadı, standart motor kullanılıyor. (Ayrıntı: Ayarlar > Gelişmiş)";
                engine = new LegacyRealtimeEngine(audio, webrtc, screenCapture);
                return false;
            }

            uint abi = EngineNative.RunaEngineAbiVersion();
            if (abi != EngineNative.RUNA_ENGINE_ABI_VERSION)
            {
                LastInitErrorSummary = $"ABI uyuşmazlığı (Beklenen: {EngineNative.RUNA_ENGINE_ABI_VERSION}, Mevcut: {abi})";
                LastInitErrorDetails = $"Uygulama ABI sürümü {EngineNative.RUNA_ENGINE_ABI_VERSION} bekliyor ancak yüklü DLL {abi} döndürdü. DLL ve uygulama sürümlerinin eşleştiğinden emin olun.";
                errorMessage = "Yeni ses motoru başlatılamadı, standart motor kullanılıyor. (Ayrıntı: Ayarlar > Gelişmiş)";
                engine = new LegacyRealtimeEngine(audio, webrtc, screenCapture);
                return false;
            }

            var rustEngine = new RustRealtimeEngine(storage, signaling, audio, webrtc, screenCapture);
            engine = rustEngine;
            return true;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(LastInitErrorSummary))
            {
                string createErr = EngineNative.GetLastCreateError();
                if (!string.IsNullOrWhiteSpace(createErr))
                {
                    LastInitErrorDetails = createErr;
                    LastInitErrorSummary = createErr.Split('\n')[0].Trim();
                }
                else
                {
                    LastInitErrorDetails = ex.ToString();
                    LastInitErrorSummary = ex.Message;
                }
            }
            errorMessage = "Yeni ses motoru başlatılamadı, standart motor kullanılıyor. (Ayrıntı: Ayarlar > Gelişmiş)";
            engine = new LegacyRealtimeEngine(audio, webrtc, screenCapture);
            return false;
        }
    }

    private RustRealtimeEngine(
        StorageService storage,
        SignalingService signaling,
        AudioEngine audio,
        WebRtcService webrtc,
        ScreenCaptureService screenCapture)
    {
        _storage = storage;
        _signaling = signaling;
        _fallbackDelegate = new LegacyRealtimeEngine(audio, webrtc, screenCapture);
        _eventPump = new EngineEventPump();

        // Forward fallback / diagnostics
        _eventPump.OnEngineFault += fault =>
        {
            OnDiagnosticLog?.Invoke($"[Rust Engine Fault] {fault}");
            OnEngineFallback?.Invoke(fault);
        };

        _eventPump.OnPongReceived += json =>
        {
            _pongReceived = true;
            OnDiagnosticLog?.Invoke($"[Rust Engine Pong] pongReceived={_pongReceived}: {json}");
        };

        // Wire 20 Hz audio levels event pump
        _eventPump.OnAudioLevelsReceived += HandleAudioLevelsJson;

        // Wire Stage 3: Signaling & WebRTC Events
        _eventPump.OnSignalLocal += (peerKey, sigType, data) =>
        {
            _ = _signaling.SendSignalAsync(peerKey, sigType, data);
        };

        _eventPump.OnPeerStateChanged += (peerKey, state) =>
        {
            if (state == "connected")
            {
                OnPeerPingStatusChanged?.Invoke(peerKey, PeerPingStatus.Measuring);
            }
            else if (state == "disconnected")
            {
                OnPeerPingStatusChanged?.Invoke(peerKey, PeerPingStatus.Connecting);
            }
            else if (state == "failed")
            {
                OnPeerPingStatusChanged?.Invoke(peerKey, PeerPingStatus.Timeout);
            }
        };

        _eventPump.OnControlChannelOpened += peerKey =>
        {
            OnControlChannelOpened?.Invoke(peerKey);
        };

        // Tüm değerler motorun gerçek ölçümlerinden gelir (sabit sayı yok; ölçülemeyen alan -1 → arayüzde "—")
        _eventPump.OnLatencyReportReceived += (peerKey, info, lossPct) =>
        {
            if (info.NetworkRttMs >= 0)
            {
                OnPeerPingUpdated?.Invoke(peerKey, info.NetworkRttMs, info.NetworkJitterMs >= 0 ? info.NetworkJitterMs : 0);
            }
            OnPeerLatencyUpdated?.Invoke(peerKey, info);
        };

        _eventPump.OnDataChannelMessage += (peerKey, channel, data) =>
        {
            if (channel == "runa-control")
            {
                HandleControlMessage(peerKey, data);
            }
        };

        _eventPump.OnShareStateChanged += (state, w, h, fps) =>
        {
            if (state == "started" || state == "updated")
            {
                _rustIsSharing = true;
                OnScreenShareStarted?.Invoke(_rustActiveSource?.Id ?? "screen", w, h, fps);
            }
            else if (state == "stopped")
            {
                _rustIsSharing = false;
                _rustActiveSource = null;
                OnScreenShareStopped?.Invoke();
            }
        };

        _eventPump.OnRemoteStreamReceived += (peerKey, state, title, thumbB64) =>
        {
            byte[]? thumbBytes = null;
            if (!string.IsNullOrEmpty(thumbB64))
            {
                try { thumbBytes = Convert.FromBase64String(thumbB64); } catch { }
            }
            OnRemoteStreamStateChanged?.Invoke(peerKey, state, thumbBytes);
        };

        _eventPump.OnGameStreamStateChanged += (pk, st) =>
        {
            _isGsTunnelActive = (st == "started");
            OnGameStreamStateChanged?.Invoke(pk, st);
        };

        _eventPump.OnGameStreamStatsReceived += (json) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var d))
                {
                    var stats = new GameStreamTunnelStats
                    {
                        BytesSent = d.TryGetProperty("bytes_sent", out var bs) ? bs.GetInt64() : 0,
                        BytesReceived = d.TryGetProperty("bytes_received", out var br) ? br.GetInt64() : 0,
                        PacketsSent = d.TryGetProperty("packets_sent", out var ps) ? ps.GetInt64() : 0,
                        PacketsReceived = d.TryGetProperty("packets_received", out var pr) ? pr.GetInt64() : 0,
                        PacketsLost = d.TryGetProperty("packets_lost", out var pl) ? pl.GetInt64() : 0,
                        LossRatePercent = d.TryGetProperty("loss_rate_percent", out var lr) ? lr.GetDouble() : 0.0,
                        RttMs = d.TryGetProperty("rtt_ms", out var rt) ? (int)rt.GetDouble() : 0,
                    };
                    _lastGsStats = stats;
                    OnGameStreamStatsUpdated?.Invoke(stats);
                }
            }
            catch { }
        };

        _eventPump.OnGameStreamHighLoss += (rate) =>
        {
            OnGameStreamHighLossWarning?.Invoke();
        };

        _eventPump.OnGameStreamProbeResult += (pk, cap, profileJson) =>
        {
            try
            {
                var profile = JsonSerializer.Deserialize<GameStreamQualityProfile>(profileJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (profile != null)
                {
                    OnGameStreamProbeCompleted?.Invoke(pk, cap, profile);
                }
            }
            catch { }
        };

        _signaling.OnSignalReceived += HandleRemoteSignalFromMqtt;

        _fallbackDelegate.OnAnnotationMessage += (fromKey, fromName, msg) => OnAnnotationMessage?.Invoke(fromKey, fromName, msg);
        _fallbackDelegate.OnControlChannelOpened += peerKey => OnControlChannelOpened?.Invoke(peerKey);
        _fallbackDelegate.OnVideoQualityState += state => OnVideoQualityState?.Invoke(state);
        _fallbackDelegate.OnWatcherStateChanged += (peerKey, isWatching) => OnWatcherStateChanged?.Invoke(peerKey, isWatching);
        _fallbackDelegate.OnRemoteStreamStateChanged += (bKey, bName, thumb) => OnRemoteStreamStateChanged?.Invoke(bKey, bName, thumb);
        _fallbackDelegate.OnRemoteVideoFrameReceivedWithId += (bKey, jpeg, w, h, fId) => OnRemoteVideoFrameReceivedWithId?.Invoke(bKey, jpeg, w, h, fId);
        _fallbackDelegate.OnPeerPingUpdated += (peerKey, rtt, jitter) => OnPeerPingUpdated?.Invoke(peerKey, rtt, jitter);
        _fallbackDelegate.OnPeerLatencyUpdated += (peerKey, info) => OnPeerLatencyUpdated?.Invoke(peerKey, info);
        _fallbackDelegate.OnPeerPingStatusChanged += (peerKey, status) => OnPeerPingStatusChanged?.Invoke(peerKey, status);
        _fallbackDelegate.OnPeerRttReset += peerKey => OnPeerRttReset?.Invoke(peerKey);

        _fallbackDelegate.OnCaptureRectChanged += (x, y, w, h) => OnCaptureRectChanged?.Invoke(x, y, w, h);
        _fallbackDelegate.OnTrackingFrameReady += (gray, w, h, fId) => OnTrackingFrameReady?.Invoke(gray, w, h, fId);
        _fallbackDelegate.OnScreenShareStarted += (title, w, h, fps) => OnScreenShareStarted?.Invoke(title, w, h, fps);
        _fallbackDelegate.OnScreenShareStopped += () => OnScreenShareStopped?.Invoke();
        _fallbackDelegate.OnEncodedFrameReadyWithId += (jpeg, w, h, fId) => OnEncodedFrameReadyWithId?.Invoke(jpeg, w, h, fId);
        _fallbackDelegate.OnPreviewThumbnailReady += thumb => OnPreviewThumbnailReady?.Invoke(thumb);

        // Initialize Native Rust Engine
        string logDir = Path.Combine(storage.AppDataFolder, "logs");

        var config = new
        {
            log_dir = logDir,
            log_level = "info"
        };

        byte[] configBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config));
        fixed (byte* pConfig = configBytes)
        {
            int createRes = EngineNative.RunaEngineCreate(
                pConfig,
                (nuint)configBytes.Length,
                &EngineEventPump.NativeCallback,
                _eventPump.NativeUserPointer,
                out _nativeEngine);

            if (createRes != EngineNative.RUNA_OK || _nativeEngine == IntPtr.Zero)
            {
                string createErr = EngineNative.GetLastCreateError();
                string summary = createRes switch
                {
                    EngineNative.RUNA_ERR_GENERIC => "Genel motor hatası (1)",
                    EngineNative.RUNA_ERR_INVALID_ARG => "Geçersiz yapılandırma parametresi (2)",
                    EngineNative.RUNA_ERR_PANIC => "Rust motoru panic verdi (3)",
                    EngineNative.RUNA_ERR_BUFFER_TOO_SMALL => "Bellek tamponu yetersiz (4)",
                    _ => $"Hata kodu {createRes}"
                };

                if (!string.IsNullOrWhiteSpace(createErr))
                {
                    LastInitErrorDetails = createErr;
                    LastInitErrorSummary = $"{summary}: {createErr.Split('\n')[0].Trim()}";
                }
                else
                {
                    LastInitErrorDetails = summary;
                    LastInitErrorSummary = summary;
                }
                throw new InvalidOperationException(LastInitErrorSummary);
            }
        }

        // Send initial ping to verify FFI communication
        SendCommand(new { cmd = "ping", data = "init_handshake" });
        OnDiagnosticLog?.Invoke("[RustEngine] Native engine initialized and ping dispatched.");
    }

    public int SendCommand(object commandObj)
    {
        if (_nativeEngine == IntPtr.Zero) return EngineNative.RUNA_ERR_INVALID_ARG;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(commandObj));
        fixed (byte* pBytes = bytes)
        {
            return EngineNative.RunaEngineCommand(_nativeEngine, pBytes, (nuint)bytes.Length);
        }
    }

    public bool TestPanicSafety()
    {
        if (_nativeEngine == IntPtr.Zero) return false;
        // Kasıtlı panic tetikleme komutu: FFI catch_unwind ile yakalanmalı ve RUNA_ERR_PANIC (3) dönmeli
        int res = SendCommand(new { cmd = "diag.panic" });
        return res == EngineNative.RUNA_ERR_PANIC;
    }

    public string? Query(object queryObj)
    {
        if (_nativeEngine == IntPtr.Zero) return null;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(queryObj));
        byte[] outBuf = new byte[65536];
        fixed (byte* pBytes = bytes)
        fixed (byte* pOut = outBuf)
        {
            int res = EngineNative.RunaEngineQuery(_nativeEngine, pBytes, (nuint)bytes.Length, pOut, (nuint)outBuf.Length, out nuint outLen);
            if (res == EngineNative.RUNA_OK && outLen > 0)
            {
                return Encoding.UTF8.GetString(outBuf, 0, (int)outLen);
            }
        }
        return null;
    }

    private void HandleAudioLevelsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("vu", out var vuProp))
                    _currentVuLevel = vuProp.GetDouble();
                if (data.TryGetProperty("db", out var dbProp))
                    _currentInputDb = dbProp.GetSingle();
                if (data.TryGetProperty("vad_prob", out var vadProp))
                    _currentVadProbability = vadProp.GetSingle();
                if (data.TryGetProperty("is_recording_mic_test", out var micTestProp))
                    _isRecordingMicTest = micTestProp.GetBoolean();
                if (data.TryGetProperty("mic_test_progress", out var progProp) && _isRecordingMicTest)
                    OnMicTestProgress?.Invoke(progProp.GetSingle());

                if (data.TryGetProperty("is_speaking", out var spProp))
                {
                    bool isSpeaking = spProp.GetBoolean();
                    if (isSpeaking != _prevLocalSpeaking)
                    {
                        _prevLocalSpeaking = isSpeaking;
                        OnLocalSpeakingChanged?.Invoke(isSpeaking);
                    }
                }

                if (data.TryGetProperty("peers", out var peersProp) && peersProp.ValueKind == JsonValueKind.Array)
                {
                    lock (_peerLevelStats)
                    {
                        foreach (var item in peersProp.EnumerateArray())
                        {
                            string key = item.GetProperty("key").GetString() ?? "";
                            string name = item.GetProperty("name").GetString() ?? key;
                            float gain = item.GetProperty("gain_db").GetSingle();
                            bool speaking = item.GetProperty("is_speaking").GetBoolean();

                            bool prevSpeaking = false;
                            if (_peerLevelStats.TryGetValue(key, out var old))
                            {
                                prevSpeaking = old.Speaking;
                            }

                            _peerLevelStats[key] = (name, gain, speaking);

                            if (speaking != prevSpeaking)
                            {
                                OnPeerSpeakingChanged?.Invoke(key, speaking);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void HandleControlMessage(string peerKey, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string t = root.TryGetProperty("t", out var tp) ? tp.GetString() ?? "" : "";

            if (t == "WATCH_START")
            {
                OnWatcherStateChanged?.Invoke(peerKey, true);
                return;
            }
            if (t == "WATCH_STOP")
            {
                OnWatcherStateChanged?.Invoke(peerKey, false);
                return;
            }
            if (t.StartsWith("ANN_", StringComparison.Ordinal))
            {
                if (AnnotationProtocol.TryParse(root, out var annMsg))
                {
                    string name = _signaling.Participants.TryGetValue(peerKey, out var p) ? p.Name : peerKey;
                    OnAnnotationMessage?.Invoke(peerKey, name, annMsg);
                }
                return;
            }
            if (t == "STREAM_START")
            {
                string name = _signaling.Participants.TryGetValue(peerKey, out var p) ? p.Name : peerKey;
                OnRemoteStreamStateChanged?.Invoke(peerKey, name, null);
            }
            else if (t == "STREAM_STOP")
            {
                string name = _signaling.Participants.TryGetValue(peerKey, out var p) ? p.Name : peerKey;
                OnRemoteStreamStateChanged?.Invoke(peerKey, name, null);
            }
        }
        catch { }
    }

    private void HandleRemoteSignalFromMqtt(string fromPeerKey, string fromName, string sigType, string data)
    {
        if (_nativeEngine == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(fromPeerKey) || fromPeerKey.Length > 160) return;
        if (data == null || data.Length > 65536) return;
        if (sigType is not ("sdp-offer" or "sdp-answer" or "ice-candidate")) return;

        string myKey = _storage.CurrentUser?.PeerKey ?? "";
        bool isPolite = string.CompareOrdinal(myKey, fromPeerKey) < 0;

        SendCommand(new
        {
            cmd = "signal.remote",
            peer_key = fromPeerKey,
            peer_name = fromName,
            sig_type = sigType,
            data,
            is_polite = isPolite
        });
    }

    // Audio Engine (Yerel Rust motoru üzerinden)
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            SendCommand(new { cmd = "audio.set_mute", muted = value });
        }
    }

    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            _isDeafened = value;
            SendCommand(new { cmd = "audio.set_deafen", deafened = value });
        }
    }

    public bool IsPttKeyHeld
    {
        get => _isPttHeld;
        set
        {
            _isPttHeld = value;
            SendCommand(new { cmd = "audio.set_ptt", held = value });
        }
    }

    public bool IsRNNoiseAvailable => true;
    public float CurrentInputDb => _currentInputDb;
    public float CurrentVadProbability => _currentVadProbability;
    public double CurrentVuLevel => _currentVuLevel;
    public bool IsRecordingMicTest => _isRecordingMicTest;
    public bool IsOutputDeviceMono => false;

    public List<AudioDeviceOption> GetInputDevices()
    {
        var list = new List<AudioDeviceOption>
        {
            new AudioDeviceOption { Id = "", Name = "Varsayılan Sistem Mikrofonu" }
        };
        try
        {
            string? json = Query(new { query = "audio.devices" });
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dev in inputs.EnumerateArray())
                    {
                        string id = dev.GetProperty("id").GetString() ?? "";
                        string name = dev.GetProperty("name").GetString() ?? id;
                        list.Add(new AudioDeviceOption { Id = id, Name = name });
                    }
                }
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
            string? json = Query(new { query = "audio.devices" });
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dev in outputs.EnumerateArray())
                    {
                        string id = dev.GetProperty("id").GetString() ?? "";
                        string name = dev.GetProperty("name").GetString() ?? id;
                        list.Add(new AudioDeviceOption { Id = id, Name = name });
                    }
                }
            }
        }
        catch { }
        return list;
    }

    public void ApplySettingsFromStorage()
    {
        var s = _storage.Settings;

        float b80 = s.Eq80Hz, b300 = s.Eq300Hz, b1k = s.Eq1kHz, b35k = s.Eq3_5kHz, b8k = s.Eq8kHz;
        if (!string.Equals(s.SoundProfile, "custom", StringComparison.OrdinalIgnoreCase))
        {
            (b80, b300, b1k, b35k, b8k) = BiquadFilter.GetPresetBands(s.SoundProfile);
        }

        var settingsPayload = new
        {
            input_gain = s.InputGainPercent / 100f,
            output_master = s.OutputMasterPercent / 100f,
            impact_guard = s.ImpactGuardEnabled,
            eq_profile = s.SoundProfile ?? "clean",
            eq_bands = new[] { b80, b300, b1k, b35k, b8k },
            ns_engine = s.NoiseEngine ?? "rnnoise",
            ns_level = s.NoiseSuppressionLevel,
            ceiling_db = s.MaxOutputCeilingDb,
            leveling_mode = s.VoiceLevelingMode ?? "normal",
            leveling_target_db = s.VoiceLevelingTargetDb,
            spatial_enabled = s.SpatialAudioEnabled,
            spatial_speaker_mode = s.SpatialSpeakerMode,
            vad_threshold_db = s.VadThresholdDb,
            gate_mode = s.InputMode ?? "vad"
        };

        SendCommand(new { cmd = "audio.apply_settings", settings = settingsPayload });
        SendCommand(new { cmd = "audio.set_devices", input_id = s.InputDeviceId ?? "", output_id = s.OutputDeviceId ?? "" });
    }

    public void RestartDevices() => SendCommand(new { cmd = "audio.restart_devices" });
    public void StartRoomAudio() => SendCommand(new { cmd = "audio.start_room" });
    public void StopRoomAudio() => SendCommand(new { cmd = "audio.stop_room" });
    public void SetMicMonitor(bool enabled) => SendCommand(new { cmd = "audio.mic_monitor", enabled });
    public void StartMicTestRecording() => SendCommand(new { cmd = "audio.mic_test.start" });
    public bool StopAndPlayMicTestRecording()
    {
        SendCommand(new { cmd = "audio.mic_test.stop_and_play" });
        return true;
    }
    public void StopMicTest() => SendCommand(new { cmd = "audio.mic_test.stop" });
    public void PlaySpatialTestTone(float azimuthDeg, float distance) =>
        SendCommand(new { cmd = "audio.spatial.test_tone", azimuth = azimuthDeg, distance });

    public void UpdatePeerAudioSettings(string peerName, PeerAudioConfig cfg)
    {
        SendCommand(new
        {
            cmd = "audio.peer.config",
            peer_key = peerName,
            config = new
            {
                volume_percent = cfg.VolumePercent,
                spatial_azimuth_deg = cfg.SpatialAzimuthDeg,
                spatial_distance = cfg.SpatialDistance,
                clarity_boost = cfg.ClarityBoost,
                exclude_from_leveling = cfg.ExcludeFromLeveling,
                local_muted = cfg.LocalMuted
            }
        });
    }

    public float GetPeerLevelingGainDb(string voiceSessionId)
    {
        lock (_peerLevelStats)
        {
            return _peerLevelStats.TryGetValue(voiceSessionId, out var stat) ? stat.GainDb : 0f;
        }
    }

    public IReadOnlyDictionary<string, (string Name, float GainDb, bool Speaking)> GetActivePeerLevelingStats()
    {
        lock (_peerLevelStats)
        {
            return new Dictionary<string, (string Name, float GainDb, bool Speaking)>(_peerLevelStats);
        }
    }

    public event Action<bool>? OnLocalSpeakingChanged;
    public event Action<string, bool>? OnPeerSpeakingChanged;
    public event Action<float>? OnMicTestProgress;

    // WebRTC (Native Rust motoru üzerinden)
    public bool HasConnectedPeers
    {
        get
        {
            var resp = Query(new { query = "net.has_connected_peers" });
            if (resp != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(resp);
                    if (doc.RootElement.TryGetProperty("has_connected_peers", out var p))
                        return p.GetBoolean();
                }
                catch { }
            }
            return _fallbackDelegate.HasConnectedPeers;
        }
    }

    public bool IsVideoSenderBusy => _fallbackDelegate.IsVideoSenderBusy;
    public string RemoteBroadcasterKey => _fallbackDelegate.RemoteBroadcasterKey;
    public string RemoteBroadcasterName => _fallbackDelegate.RemoteBroadcasterName;
    public string RemoteStreamTitle => _fallbackDelegate.RemoteStreamTitle;
    public int RemoteStreamHeight => _fallbackDelegate.RemoteStreamHeight;
    public int RemoteStreamFps => _fallbackDelegate.RemoteStreamFps;
    public double SentNetworkBitrateKbps => _fallbackDelegate.SentNetworkBitrateKbps;
    public double ReceivedVideoBitrateKbps => _fallbackDelegate.ReceivedVideoBitrateKbps;
    public double UsableVideoBitrateKbps => _fallbackDelegate.UsableVideoBitrateKbps;
    public int BroadcasterAverageRttMs => _fallbackDelegate.BroadcasterAverageRttMs;
    public int RemoteStreamRttMs => _fallbackDelegate.RemoteStreamRttMs;
    public double RemoteStreamPacketLossPct => _fallbackDelegate.RemoteStreamPacketLossPct;
    public int RenderDelayDroppedFramesCount => _fallbackDelegate.RenderDelayDroppedFramesCount;
    public long TotalFramesDecoded => _fallbackDelegate.TotalFramesDecoded;
    public VideoQualityState LastBroadcasterVqState => _fallbackDelegate.LastBroadcasterVqState;

    public Task EnsurePeerConnectionAsync(string myPeerKey, RoomParticipant remotePeer)
    {
        if (string.IsNullOrEmpty(remotePeer.PeerKey) || remotePeer.PeerKey == myPeerKey) return Task.CompletedTask;

        // Bölüm 12: Karışık oda ve eski sürüm kontrolü
        if (remotePeer.Proto != 2 && remotePeer.Engine != "rust")
        {
            remotePeer.IsLegacyMismatch = true;
            OnDiagnosticLog?.Invoke($"[Karışık Motor] {remotePeer.Name} eski motoru kullanıyor. Sesli bağlantı kurulamadı.");
            return Task.CompletedTask;
        }

        bool isPolite = string.CompareOrdinal(myPeerKey, remotePeer.PeerKey) < 0;
        SendCommand(new
        {
            cmd = "peer.ensure",
            peer_key = remotePeer.PeerKey,
            peer_name = remotePeer.Name,
            is_polite = isPolite
        });
        return Task.CompletedTask;
    }

    public void RefreshCodecSettings() => _fallbackDelegate.RefreshCodecSettings();

    public void CloseAll()
    {
        SendCommand(new { cmd = "room.leave" });
        _fallbackDelegate.CloseAll();
    }

    public void BroadcastP2PChatMessage(ChatMessage msg)
    {
        string json = JsonSerializer.Serialize(new { type = "P2P_CHAT", data = msg });
        SendCommand(new { cmd = "dc.broadcast", channel = "runa-control", data = json });
    }

    public void BroadcastStreamStarted(string title)
    {
        string json = JsonSerializer.Serialize(new { t = "STREAM_START", title });
        SendCommand(new { cmd = "dc.broadcast", channel = "runa-control", data = json });
        _fallbackDelegate.BroadcastStreamStarted(title);
    }

    public void BroadcastStreamStopped()
    {
        string json = JsonSerializer.Serialize(new { t = "STREAM_STOP" });
        SendCommand(new { cmd = "dc.broadcast", channel = "runa-control", data = json });
        _fallbackDelegate.BroadcastStreamStopped();
    }

    public void BroadcastStreamThumbnail(byte[] thumbBytes)
    {
        string b64 = Convert.ToBase64String(thumbBytes);
        string json = JsonSerializer.Serialize(new { t = "STREAM_THUMB", jpg = b64 });
        SendCommand(new { cmd = "dc.broadcast", channel = "runa-control", data = json });
        _fallbackDelegate.BroadcastStreamThumbnail(thumbBytes);
    }

    public void BroadcastEncodedVideoFrame(byte[] jpegBytes, int width, int height, uint frameId) =>
        _fallbackDelegate.BroadcastEncodedVideoFrame(jpegBytes, width, height, frameId);

    public void BroadcastAnnotation(object payload, string? exceptPeerKey = null)
    {
        string json = JsonSerializer.Serialize(payload);
        SendCommand(new { cmd = "dc.broadcast", channel = "runa-control", data = json, except_peer = exceptPeerKey });
    }

    public void SendAnnotationTo(string peerKey, object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        SendCommand(new { cmd = "dc.send", peer_key = peerKey, channel = "runa-control", data = json });
    }

    public bool IsControlChannelOpen(string peerKey)
    {
        var resp = Query(new { query = "net.is_control_open", peer_key = peerKey });
        if (resp != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("is_control_open", out var p))
                    return p.GetBoolean();
            }
            catch { }
        }
        return false;
    }

    public void SendWatchStart(string broadcasterKey)
    {
        string json = JsonSerializer.Serialize(new { t = "WATCH_START" });
        SendCommand(new { cmd = "dc.send", peer_key = broadcasterKey, channel = "runa-control", data = json });
        SendCommand(new { cmd = "watch.start", peer_key = broadcasterKey });
        _fallbackDelegate.SendWatchStart(broadcasterKey);
    }

    public void SendWatchStop(string broadcasterKey)
    {
        string json = JsonSerializer.Serialize(new { t = "WATCH_STOP" });
        SendCommand(new { cmd = "dc.send", peer_key = broadcasterKey, channel = "runa-control", data = json });
        SendCommand(new { cmd = "watch.stop", peer_key = broadcasterKey });
        _fallbackDelegate.SendWatchStop(broadcasterKey);
    }

    public uint GetNextVideoFrameId() => _fallbackDelegate.GetNextVideoFrameId();
    public void IncrementRenderDelayDroppedFrames() => _fallbackDelegate.IncrementRenderDelayDroppedFrames();
    public HashSet<string> GetActiveWatcherKeys() => _fallbackDelegate.GetActiveWatcherKeys();
    public (double AvgLoss, double MinRxKbps, double AvgRxKbps, int TotalDropped) GetBroadcasterReceiverStats() => _fallbackDelegate.GetBroadcasterReceiverStats();

    public event Action<string, string, AnnotationMessage>? OnAnnotationMessage;
    public event Action<string>? OnControlChannelOpened;
    public event Action<VideoQualityState>? OnVideoQualityState;
    public event Action<string, bool>? OnWatcherStateChanged;
    public event Action<string, string, byte[]?>? OnRemoteStreamStateChanged;
    public event Action<string, byte[], int, int, uint>? OnRemoteVideoFrameReceivedWithId;
    public event Action<string, double, double>? OnPeerPingUpdated;
    public event Action<string, PeerLatencyInfo>? OnPeerLatencyUpdated;
    public event Action<string, PeerPingStatus>? OnPeerPingStatusChanged;
    public event Action<string>? OnPeerRttReset;

    private ScreenCaptureSourceItem? _rustActiveSource;
    private bool _rustIsSharing;

    public bool IsSharing => _rustIsSharing;
    public ScreenCaptureSourceItem? ActiveSource => _rustActiveSource;
    public int CurrentWidth => _rustActiveSource?.BoundsWidth ?? 1920;
    public int CurrentHeight => _rustActiveSource?.BoundsHeight ?? 1080;
    public int MeasuredFps => 30;
    public long TotalFramesCaptured => 0;
    public double MeasuredBitrateKbps => 2500;
    public double CurrentTargetBitrateKbps => 3000;

    public Func<bool>? NeedsTrackingFrames { get => _fallbackDelegate.NeedsTrackingFrames; set => _fallbackDelegate.NeedsTrackingFrames = value; }
    public Func<bool>? ShouldSkipFrameGate { get => _fallbackDelegate.ShouldSkipFrameGate; set => _fallbackDelegate.ShouldSkipFrameGate = value; }
    public Func<bool>? HasAudience { get => _fallbackDelegate.HasAudience; set => _fallbackDelegate.HasAudience = value; }
    public Func<uint>? FrameIdGenerator { get => _fallbackDelegate.FrameIdGenerator; set => _fallbackDelegate.FrameIdGenerator = value; }

    public List<ScreenCaptureSourceItem> GetAvailableSourcesQuick(IntPtr selfHwnd) => _fallbackDelegate.GetAvailableSourcesQuick(selfHwnd);
    public void PopulateVisualsAsync(IEnumerable<ScreenCaptureSourceItem> items, DispatcherQueue dq, CancellationToken ct) => _fallbackDelegate.PopulateVisualsAsync(items, dq, ct);
    
    public void StartOrUpdateCapture(ScreenCaptureSourceItem source, int targetH, int targetFps, string codec, bool dataSaver)
    {
        _rustActiveSource = source;
        _rustIsSharing = true;
        SendCommand(new
        {
            cmd = "share.start",
            source_type = source.IsMonitor ? "monitor" : "window",
            handle = (long)source.Handle,
            max_height = targetH,
            max_fps = targetFps,
            priority = _storage.Settings.AdaptiveStreamPriority == "Fluidity" ? "fluidity" : "clarity",
            data_saver = dataSaver
        });
        OnScreenShareStarted?.Invoke(source.Id, source.BoundsWidth, source.BoundsHeight, targetFps);
    }

    public void StopCapture()
    {
        _rustIsSharing = false;
        _rustActiveSource = null;
        SendCommand(new { cmd = "share.stop" });
        OnScreenShareStopped?.Invoke();
    }

    public bool SetVideoSurface(string streamId, IntPtr swapChainPanelNative)
    {
        if (_nativeEngine == IntPtr.Zero || swapChainPanelNative == IntPtr.Zero) return false;
        return EngineNative.RunaEngineSetVideoSurface(_nativeEngine, streamId, swapChainPanelNative) == EngineNative.RUNA_OK;
    }

    public void ResizeSurface(int width, int height)
    {
        SendCommand(new { cmd = "share.surface_resize", width, height });
    }

    // GameStream Tunnel
    private bool _isGsTunnelActive = false;
    private GameStreamTunnelStats? _lastGsStats = null;

    public bool IsGameStreamTunnelActive => _isGsTunnelActive;

    public void StartGameStreamTunnel(string peerKey, bool isHost, string bindIp = "127.0.0.77")
    {
        _isGsTunnelActive = true;
        SendCommand(new
        {
            cmd = "gs.tunnel.start",
            peer_key = peerKey,
            role = isHost ? "host" : "client",
            bind_ip = bindIp
        });
    }

    public void StopGameStreamTunnel()
    {
        _isGsTunnelActive = false;
        SendCommand(new { cmd = "gs.tunnel.stop" });
    }

    public void RunGameStreamProbe(string peerKey, string sessionId, int maxBitrateKbps, List<string> codecs)
    {
        SendCommand(new
        {
            cmd = "gs.probe.run",
            peer_key = peerKey,
            session_id = sessionId,
            max_bitrate_kbps = maxBitrateKbps,
            codecs = codecs
        });
    }

    public GameStreamTunnelStats? GetGameStreamStats()
    {
        return _lastGsStats;
    }

    public event Action<string, string>? OnGameStreamStateChanged;
    public event Action<GameStreamTunnelStats>? OnGameStreamStatsUpdated;
    public event Action? OnGameStreamHighLossWarning;
    public event Action<string, int, GameStreamQualityProfile>? OnGameStreamProbeCompleted;

    public event Action<int, int, int, int>? OnCaptureRectChanged;
    public event Action<byte[], int, int, uint>? OnTrackingFrameReady;
    public event Action<string, int, int, int>? OnScreenShareStarted;
    public event Action? OnScreenShareStopped;
    public event Action<byte[], int, int, uint>? OnEncodedFrameReadyWithId;
    public event Action<byte[]>? OnPreviewThumbnailReady;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _signaling.OnSignalReceived -= HandleRemoteSignalFromMqtt;

        if (_nativeEngine != IntPtr.Zero)
        {
            EngineNative.RunaEngineDestroy(_nativeEngine);
            _nativeEngine = IntPtr.Zero;
        }

        _eventPump.Dispose();
        _fallbackDelegate.Dispose();
    }
}
