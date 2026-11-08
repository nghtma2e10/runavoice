using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using RunaVoice_WinUI.Services.GameStream;

namespace RunaVoice_WinUI.Services.Engine;

public interface IRealtimeEngine : IDisposable
{
    // Engine Meta
    string EngineName { get; }
    bool IsRustEngine { get; }
    event Action<string>? OnEngineFallback;
    event Action<string>? OnDiagnosticLog;

    // Audio Engine
    bool IsMuted { get; set; }
    bool IsDeafened { get; set; }
    bool IsPttKeyHeld { get; set; }
    bool IsRNNoiseAvailable { get; }
    float CurrentInputDb { get; }
    float CurrentVadProbability { get; }
    double CurrentVuLevel { get; }
    bool IsRecordingMicTest { get; }
    bool IsOutputDeviceMono { get; }

    List<AudioDeviceOption> GetInputDevices();
    List<AudioDeviceOption> GetOutputDevices();
    void ApplySettingsFromStorage();
    void RestartDevices();
    void StartRoomAudio();
    void StopRoomAudio();
    void SetMicMonitor(bool enabled);
    void StartMicTestRecording();
    bool StopAndPlayMicTestRecording();
    void StopMicTest();
    void PlaySpatialTestTone(float azimuthDeg, float distance);
    void UpdatePeerAudioSettings(string peerName, PeerAudioConfig cfg);
    float GetPeerLevelingGainDb(string voiceSessionId);
    IReadOnlyDictionary<string, (string Name, float GainDb, bool Speaking)> GetActivePeerLevelingStats();

    event Action<bool>? OnLocalSpeakingChanged;
    event Action<string, bool>? OnPeerSpeakingChanged;
    event Action<float>? OnMicTestProgress;

    // WebRTC / Network
    bool HasConnectedPeers { get; }
    bool IsVideoSenderBusy { get; }
    string RemoteBroadcasterKey { get; }
    string RemoteBroadcasterName { get; }
    string RemoteStreamTitle { get; }
    int RemoteStreamHeight { get; }
    int RemoteStreamFps { get; }
    double SentNetworkBitrateKbps { get; }
    double ReceivedVideoBitrateKbps { get; }
    double UsableVideoBitrateKbps { get; }
    int BroadcasterAverageRttMs { get; }
    int RemoteStreamRttMs { get; }
    double RemoteStreamPacketLossPct { get; }
    int RenderDelayDroppedFramesCount { get; }
    long TotalFramesDecoded { get; }
    VideoQualityState LastBroadcasterVqState { get; }

    Task EnsurePeerConnectionAsync(string myPeerKey, RoomParticipant remotePeer);
    void RefreshCodecSettings();
    void CloseAll();
    void BroadcastP2PChatMessage(ChatMessage msg);
    void BroadcastStreamStarted(string title);
    void BroadcastStreamStopped();
    void BroadcastStreamThumbnail(byte[] thumbBytes);
    void BroadcastEncodedVideoFrame(byte[] jpegBytes, int width, int height, uint frameId);
    void BroadcastAnnotation(object payload, string? exceptPeerKey = null);
    void SendAnnotationTo(string peerKey, object payload);
    bool IsControlChannelOpen(string peerKey);
    void SendWatchStart(string broadcasterKey);
    void SendWatchStop(string broadcasterKey);
    uint GetNextVideoFrameId();
    void IncrementRenderDelayDroppedFrames();
    HashSet<string> GetActiveWatcherKeys();
    (double AvgLoss, double MinRxKbps, double AvgRxKbps, int TotalDropped) GetBroadcasterReceiverStats();

    event Action<string, string, AnnotationMessage>? OnAnnotationMessage;
    event Action<string>? OnControlChannelOpened;
    event Action<VideoQualityState>? OnVideoQualityState;
    event Action<string, bool>? OnWatcherStateChanged;
    event Action<string, string, byte[]?>? OnRemoteStreamStateChanged;
    event Action<string, byte[], int, int, uint>? OnRemoteVideoFrameReceivedWithId;
    event Action<string, double, double>? OnPeerPingUpdated;
    event Action<string, PeerLatencyInfo>? OnPeerLatencyUpdated;
    event Action<string, PeerPingStatus>? OnPeerPingStatusChanged;
    event Action<string>? OnPeerRttReset;

    // Screen Capture
    bool IsSharing { get; }
    ScreenCaptureSourceItem? ActiveSource { get; }
    int CurrentWidth { get; }
    int CurrentHeight { get; }
    int MeasuredFps { get; }
    long TotalFramesCaptured { get; }
    double MeasuredBitrateKbps { get; }
    double CurrentTargetBitrateKbps { get; }
    Func<bool>? NeedsTrackingFrames { get; set; }
    Func<bool>? ShouldSkipFrameGate { get; set; }
    Func<bool>? HasAudience { get; set; }
    Func<uint>? FrameIdGenerator { get; set; }

    List<ScreenCaptureSourceItem> GetAvailableSourcesQuick(IntPtr selfHwnd);
    void PopulateVisualsAsync(IEnumerable<ScreenCaptureSourceItem> items, DispatcherQueue dq, CancellationToken ct);
    void StartOrUpdateCapture(ScreenCaptureSourceItem source, int targetH, int targetFps, string codec, bool dataSaver);
    void StopCapture();
    bool SetVideoSurface(string streamId, IntPtr swapChainPanelNative);
    void ResizeSurface(int width, int height);

    event Action<int, int, int, int>? OnCaptureRectChanged;
    event Action<byte[], int, int, uint>? OnTrackingFrameReady;
    event Action<string, int, int, int>? OnScreenShareStarted;
    event Action? OnScreenShareStopped;
    event Action<byte[], int, int, uint>? OnEncodedFrameReadyWithId;
    event Action<byte[]>? OnPreviewThumbnailReady;

    // GameStream Tunnel
    bool IsGameStreamTunnelActive { get; }
    void StartGameStreamTunnel(string peerKey, bool isHost, string bindIp = "127.0.0.77");
    void StopGameStreamTunnel();
    void RunGameStreamProbe(string peerKey, string sessionId, int maxBitrateKbps, List<string> codecs);
    GameStreamTunnelStats? GetGameStreamStats();

    event Action<string, string>? OnGameStreamStateChanged;
    event Action<GameStreamTunnelStats>? OnGameStreamStatsUpdated;
    event Action? OnGameStreamHighLossWarning;
    event Action<string, int, GameStreamQualityProfile>? OnGameStreamProbeCompleted;
}
