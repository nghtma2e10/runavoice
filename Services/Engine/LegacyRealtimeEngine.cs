using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using RunaVoice_WinUI.Services.GameStream;

namespace RunaVoice_WinUI.Services.Engine;

public sealed class LegacyRealtimeEngine : IRealtimeEngine
{
    private readonly AudioEngine _audio;
    private readonly WebRtcService _webrtc;
    private readonly ScreenCaptureService _screenCapture;
    private bool _disposed;

    public string EngineName => "legacy";
    public bool IsRustEngine => false;

#pragma warning disable CS0067
    public event Action<string>? OnEngineFallback;
    public event Action<string>? OnDiagnosticLog;
#pragma warning restore CS0067

    public LegacyRealtimeEngine(AudioEngine audio, WebRtcService webrtc, ScreenCaptureService screenCapture)
    {
        _audio = audio;
        _webrtc = webrtc;
        _screenCapture = screenCapture;

        // Wire Audio Events
        _audio.OnLocalSpeakingChanged += isSpeaking => OnLocalSpeakingChanged?.Invoke(isSpeaking);
        _audio.OnPeerSpeakingChanged += (peerKey, isSpeaking) => OnPeerSpeakingChanged?.Invoke(peerKey, isSpeaking);
        _audio.OnMicTestProgress += progress => OnMicTestProgress?.Invoke(progress);

        // Wire WebRTC Events
        _webrtc.OnAnnotationMessage += (fromKey, fromName, msg) => OnAnnotationMessage?.Invoke(fromKey, fromName, msg);
        _webrtc.OnControlChannelOpened += peerKey => OnControlChannelOpened?.Invoke(peerKey);
        _webrtc.OnVideoQualityState += state => OnVideoQualityState?.Invoke(state);
        _webrtc.OnWatcherStateChanged += (peerKey, isWatching) => OnWatcherStateChanged?.Invoke(peerKey, isWatching);
        _webrtc.OnRemoteStreamStateChanged += (bKey, bName, thumb) => OnRemoteStreamStateChanged?.Invoke(bKey, bName, thumb);
        _webrtc.OnRemoteVideoFrameReceivedWithId += (bKey, jpeg, w, h, fId) => OnRemoteVideoFrameReceivedWithId?.Invoke(bKey, jpeg, w, h, fId);
        _webrtc.OnPeerPingUpdated += (peerKey, rtt, jitter) => OnPeerPingUpdated?.Invoke(peerKey, rtt, jitter);
        _webrtc.OnPeerLatencyUpdated += (peerKey, info) => OnPeerLatencyUpdated?.Invoke(peerKey, info);
        _webrtc.OnPeerPingStatusChanged += (peerKey, status) => OnPeerPingStatusChanged?.Invoke(peerKey, status);
        _webrtc.OnPeerRttReset += peerKey => OnPeerRttReset?.Invoke(peerKey);

        // Wire ScreenCapture Events
        _screenCapture.OnCaptureRectChanged += (x, y, w, h) => OnCaptureRectChanged?.Invoke(x, y, w, h);
        _screenCapture.OnTrackingFrameReady += (gray, w, h, fId) => OnTrackingFrameReady?.Invoke(gray, w, h, fId);
        _screenCapture.OnScreenShareStarted += (title, w, h, fps) => OnScreenShareStarted?.Invoke(title, w, h, fps);
        _screenCapture.OnScreenShareStopped += () => OnScreenShareStopped?.Invoke();
        _screenCapture.OnEncodedFrameReadyWithId += (jpeg, w, h, fId) => OnEncodedFrameReadyWithId?.Invoke(jpeg, w, h, fId);
        _screenCapture.OnPreviewThumbnailReady += thumb => OnPreviewThumbnailReady?.Invoke(thumb);
    }

    // Audio Properties
    public bool IsMuted
    {
        get => _audio.IsMuted;
        set => _audio.IsMuted = value;
    }

    public bool IsDeafened
    {
        get => _audio.IsDeafened;
        set => _audio.IsDeafened = value;
    }

    public bool IsPttKeyHeld
    {
        get => _audio.IsPttKeyHeld;
        set => _audio.IsPttKeyHeld = value;
    }

    public bool IsRNNoiseAvailable => _audio.IsRNNoiseAvailable;
    public float CurrentInputDb => _audio.CurrentInputDb;
    public float CurrentVadProbability => _audio.CurrentVadProbability;
    public double CurrentVuLevel => _audio.CurrentVuLevel;
    public bool IsRecordingMicTest => _audio.IsRecordingMicTest;
    public bool IsOutputDeviceMono => _audio.IsOutputDeviceMono;

    // Audio Methods
    public List<AudioDeviceOption> GetInputDevices() => _audio.GetInputDevices();
    public List<AudioDeviceOption> GetOutputDevices() => _audio.GetOutputDevices();
    public void ApplySettingsFromStorage() => _audio.ApplySettingsFromStorage();
    public void RestartDevices() => _audio.RestartDevices();
    public void StartRoomAudio() => _audio.StartRoomAudio();
    public void StopRoomAudio() => _audio.StopRoomAudio();
    public void SetMicMonitor(bool enabled) => _audio.SetMicMonitor(enabled);
    public void StartMicTestRecording() => _audio.StartMicTestRecording();
    public bool StopAndPlayMicTestRecording() => _audio.StopAndPlayMicTestRecording();
    public void StopMicTest() => _audio.StopMicTest();
    public void PlaySpatialTestTone(float azimuthDeg, float distance) => _audio.PlaySpatialTestTone(azimuthDeg, distance);
    public void UpdatePeerAudioSettings(string peerName, PeerAudioConfig cfg) => _audio.UpdatePeerAudioSettings(peerName, cfg);
    public float GetPeerLevelingGainDb(string voiceSessionId) => _audio.GetPeerLevelingGainDb(voiceSessionId);
    public IReadOnlyDictionary<string, (string Name, float GainDb, bool Speaking)> GetActivePeerLevelingStats() => _audio.GetActivePeerLevelingStats();

    public event Action<bool>? OnLocalSpeakingChanged;
    public event Action<string, bool>? OnPeerSpeakingChanged;
    public event Action<float>? OnMicTestProgress;

    // WebRTC Properties
    public bool HasConnectedPeers => _webrtc.HasConnectedPeers;
    public bool IsVideoSenderBusy => _webrtc.IsVideoSenderBusy;
    public string RemoteBroadcasterKey => _webrtc.RemoteBroadcasterKey;
    public string RemoteBroadcasterName => _webrtc.RemoteBroadcasterName;
    public string RemoteStreamTitle => _webrtc.RemoteStreamTitle;
    public int RemoteStreamHeight => _webrtc.RemoteStreamHeight;
    public int RemoteStreamFps => _webrtc.RemoteStreamFps;
    public double SentNetworkBitrateKbps => _webrtc.SentNetworkBitrateKbps;
    public double ReceivedVideoBitrateKbps => _webrtc.ReceivedVideoBitrateKbps;
    public double UsableVideoBitrateKbps => _webrtc.UsableVideoBitrateKbps;
    public int BroadcasterAverageRttMs => _webrtc.BroadcasterAverageRttMs;
    public int RemoteStreamRttMs => _webrtc.RemoteStreamRttMs;
    public double RemoteStreamPacketLossPct => _webrtc.RemoteStreamPacketLossPct;
    public int RenderDelayDroppedFramesCount => _webrtc.RenderDelayDroppedFramesCount;
    public long TotalFramesDecoded => _webrtc.TotalFramesDecoded;
    public VideoQualityState LastBroadcasterVqState => _webrtc.LastBroadcasterVqState ?? new VideoQualityState();

    // WebRTC Methods
    public Task EnsurePeerConnectionAsync(string myPeerKey, RoomParticipant remotePeer) => _webrtc.EnsurePeerConnectionAsync(myPeerKey, remotePeer);
    public void RefreshCodecSettings() => _webrtc.RefreshCodecSettings();
    public void CloseAll() => _webrtc.CloseAll();
    public void BroadcastP2PChatMessage(ChatMessage msg) => _webrtc.BroadcastP2PChatMessage(msg);
    public void BroadcastStreamStarted(string title) => _webrtc.BroadcastStreamStarted(title);
    public void BroadcastStreamStopped() => _webrtc.BroadcastStreamStopped();
    public void BroadcastStreamThumbnail(byte[] thumbBytes) => _webrtc.BroadcastStreamThumbnail(thumbBytes);
    public void BroadcastEncodedVideoFrame(byte[] jpegBytes, int width, int height, uint frameId) => _webrtc.BroadcastEncodedVideoFrame(jpegBytes, width, height, frameId);
    public void BroadcastAnnotation(object payload, string? exceptPeerKey = null) => _webrtc.BroadcastAnnotation(payload, exceptPeerKey);
    public void SendAnnotationTo(string peerKey, object payload) => _webrtc.SendAnnotationTo(peerKey, payload);
    public bool IsControlChannelOpen(string peerKey) => _webrtc.IsControlChannelOpen(peerKey);
    public void SendWatchStart(string broadcasterKey) => _webrtc.SendWatchStart(broadcasterKey);
    public void SendWatchStop(string broadcasterKey) => _webrtc.SendWatchStop(broadcasterKey);
    public uint GetNextVideoFrameId() => _webrtc.GetNextVideoFrameId();
    public void IncrementRenderDelayDroppedFrames() => _webrtc.IncrementRenderDelayDroppedFrames();
    public HashSet<string> GetActiveWatcherKeys() => _webrtc.GetActiveWatcherKeys();
    public (double AvgLoss, double MinRxKbps, double AvgRxKbps, int TotalDropped) GetBroadcasterReceiverStats() => _webrtc.GetBroadcasterReceiverStats();

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

    // Screen Capture Properties
    public bool IsSharing => _screenCapture.IsSharing;
    public ScreenCaptureSourceItem? ActiveSource => _screenCapture.ActiveSource;
    public int CurrentWidth => _screenCapture.CurrentWidth;
    public int CurrentHeight => _screenCapture.CurrentHeight;
    public int MeasuredFps => _screenCapture.MeasuredFps;
    public long TotalFramesCaptured => _screenCapture.TotalFramesCaptured;
    public double MeasuredBitrateKbps => _screenCapture.MeasuredBitrateKbps;
    public double CurrentTargetBitrateKbps => _screenCapture.CurrentTargetBitrateKbps;

    public Func<bool>? NeedsTrackingFrames
    {
        get => _screenCapture.NeedsTrackingFrames;
        set => _screenCapture.NeedsTrackingFrames = value;
    }

    public Func<bool>? ShouldSkipFrameGate
    {
        get => _screenCapture.ShouldSkipFrameGate;
        set => _screenCapture.ShouldSkipFrameGate = value;
    }

    public Func<bool>? HasAudience
    {
        get => _screenCapture.HasAudience;
        set => _screenCapture.HasAudience = value;
    }

    public Func<uint>? FrameIdGenerator
    {
        get => _screenCapture.FrameIdGenerator;
        set => _screenCapture.FrameIdGenerator = value;
    }

    // Screen Capture Methods
    public List<ScreenCaptureSourceItem> GetAvailableSourcesQuick(IntPtr selfHwnd) => _screenCapture.GetAvailableSourcesQuick(selfHwnd);
    public void PopulateVisualsAsync(IEnumerable<ScreenCaptureSourceItem> items, DispatcherQueue dq, CancellationToken ct) => _screenCapture.PopualteVisualsSafe(items, dq, ct);
    public void StartOrUpdateCapture(ScreenCaptureSourceItem source, int targetH, int targetFps, string codec, bool dataSaver) => _screenCapture.StartOrUpdateCapture(source, targetH, targetFps, codec, dataSaver);
    public void StopCapture() => _screenCapture.StopCapture();
    public bool SetVideoSurface(string streamId, IntPtr swapChainPanelNative) => false;
    public void ResizeSurface(int width, int height) { }

#pragma warning disable CS0067
    public event Action<int, int, int, int>? OnCaptureRectChanged;
    public event Action<byte[], int, int, uint>? OnTrackingFrameReady;
    public event Action<string, int, int, int>? OnScreenShareStarted;
    public event Action? OnScreenShareStopped;
    public event Action<byte[], int, int, uint>? OnEncodedFrameReadyWithId;
    public event Action<byte[]>? OnPreviewThumbnailReady;

    public bool IsGameStreamTunnelActive => false;
    public void StartGameStreamTunnel(string peerKey, bool isHost, string bindIp = "127.0.0.77") { }
    public void StopGameStreamTunnel() { }
    public void RunGameStreamProbe(string peerKey, string sessionId, int maxBitrateKbps, List<string> codecs) { }
    public GameStreamTunnelStats? GetGameStreamStats() => null;

    public event Action<string, string>? OnGameStreamStateChanged;
    public event Action<GameStreamTunnelStats>? OnGameStreamStatsUpdated;
    public event Action? OnGameStreamHighLossWarning;
    public event Action<string, int, GameStreamQualityProfile>? OnGameStreamProbeCompleted;
#pragma warning restore CS0067

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _screenCapture.Dispose();
        _webrtc.Dispose();
        _audio.Dispose();
    }
}

internal static class ScreenCaptureExtensions
{
    public static void PopualteVisualsSafe(this ScreenCaptureService svc, IEnumerable<ScreenCaptureSourceItem> items, DispatcherQueue dq, CancellationToken ct)
    {
        svc.PopulateVisualsAsync(items, dq, ct);
    }
}
