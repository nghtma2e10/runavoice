using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.Engine;

public sealed record EngineEvent(uint Kind, byte[] Data);

public sealed class EngineEventPump : IDisposable
{
    private readonly Channel<EngineEvent> _channel = Channel.CreateUnbounded<EngineEvent>(new UnboundedChannelOptions
    {
        SingleWriter = false,
        SingleReader = true
    });

    private readonly CancellationTokenSource _cts = new();
    private GCHandle _gcHandle;
    private readonly Task _pumpTask;

    public event Action<string>? OnJsonEvent;
    public event Action<string>? OnPongReceived;
    public event Action<string>? OnEngineFault;
    public event Action<string>? OnAudioLevelsReceived;
    public event Action<string, string, string>? OnSignalLocal; // peerKey, sigType, data
    public event Action<string, string>? OnPeerStateChanged;   // peerKey, state
    public event Action<string>? OnControlChannelOpened;       // peerKey
    public event Action<string, string, string>? OnDataChannelMessage; // peerKey, channel, data
    public event Action<string, RunaVoice_WinUI.Models.PeerLatencyInfo, double>? OnLatencyReportReceived; // peerKey, gerçek ölçümler, kayıp %
    public event Action<string, int, int, int>? OnShareStateChanged; // state, width, height, fps
    public event Action<string>? OnShareStatsReceived; // stats JSON
    public event Action<string, string, string, string>? OnRemoteStreamReceived; // peerKey, state, title, thumbB64
    public event Action<string, int, int, bool>? OnVideoFrameReceived; // peerKey, width, height, isKeyframe
    public event Action<string, string>? OnGameStreamStateChanged; // peerKey, state
    public event Action<string>? OnGameStreamStatsReceived; // json
    public event Action<double>? OnGameStreamHighLoss; // lossRate
    public event Action<string, int, string>? OnGameStreamProbeResult; // peerKey, capacityKbps, profileJson

    public EngineEventPump()
    {
        _gcHandle = GCHandle.Alloc(this);
        _pumpTask = Task.Run(PumpLoopAsync);
    }

    public IntPtr NativeUserPointer => GCHandle.ToIntPtr(_gcHandle);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe void NativeCallback(IntPtr user, uint kind, byte* data, nuint len)
    {
        try
        {
            if (user == IntPtr.Zero || data == null || len == 0) return;
            var handle = GCHandle.FromIntPtr(user);
            if (handle.Target is EngineEventPump pump)
            {
                byte[] copy = new byte[(int)len];
                Marshal.Copy((IntPtr)data, copy, 0, (int)len);
                pump._channel.Writer.TryWrite(new EngineEvent(kind, copy));
            }
        }
        catch { }
    }

    private async Task PumpLoopAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    ProcessEvent(evt);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnEngineFault?.Invoke($"Event pump error: {ex.Message}");
        }
    }

    private void ProcessEvent(EngineEvent evt)
    {
        if (evt.Kind == 0) // JSON event
        {
            try
            {
                string json = Encoding.UTF8.GetString(evt.Data);
                OnJsonEvent?.Invoke(json);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "pong")
                    {
                        OnPongReceived?.Invoke(json);
                    }
                    else if (type == "engine_fault")
                    {
                        string fault = doc.RootElement.TryGetProperty("error", out var err)
                            ? err.GetString() ?? "Unknown fault"
                            : "Unknown fault";
                        OnEngineFault?.Invoke(fault);
                    }
                    else if (type == "audio_levels")
                    {
                        OnAudioLevelsReceived?.Invoke(json);
                    }
                    else if (type == "signal.local")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        string sigType = doc.RootElement.TryGetProperty("sig_type", out var st) ? st.GetString() ?? "" : "";
                        string data = doc.RootElement.TryGetProperty("data", out var dt) ? dt.GetString() ?? "" : "";
                        OnSignalLocal?.Invoke(peerKey, sigType, data);
                    }
                    else if (type == "peer.state")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        string state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                        OnPeerStateChanged?.Invoke(peerKey, state);
                    }
                    else if (type == "dc.open")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        OnControlChannelOpened?.Invoke(peerKey);
                    }
                    else if (type == "dc.message")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        string channel = doc.RootElement.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "";
                        string data = doc.RootElement.TryGetProperty("data", out var dt) ? dt.GetString() ?? "" : "";
                        OnDataChannelMessage?.Invoke(peerKey, channel, data);
                    }
                    else if (type == "latency")
                    {
                        var r = doc.RootElement;
                        string peerKey = r.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        // Ölçülmemiş alanlar motorda -1; eksik alan da -1 kabul edilir (uydurma değer yok)
                        double D(string name) => r.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : -1;
                        var info = new RunaVoice_WinUI.Models.PeerLatencyInfo
                        {
                            NetworkRttMs = D("rtt_ms"),
                            NetworkJitterMs = D("jitter_ms"),
                            IsUdpPath = true,
                            PeerCaptureBufferMs = D("peer_capture_ms"),
                            PeerFrameMs = D("peer_frame_ms"),
                            PeerLookaheadMs = D("peer_lookahead_ms"),
                            PeerEncodeMs = D("peer_encode_ms"),
                            LocalDecodeMs = D("local_decode_ms"),
                            JitterBufferMs = D("jitter_buffer_ms"),
                            JitterTargetMs = D("jitter_target_ms"),
                            Underruns = r.TryGetProperty("underruns", out var u) && u.TryGetInt32(out int ui) ? ui : 0,
                            PlaybackBufferMs = D("playback_buffer_ms"),
                            PlaybackMode = "Rust motoru (WASAPI olay tabanlı)"
                        };
                        OnLatencyReportReceived?.Invoke(peerKey, info, D("loss_pct"));
                    }
                    else if (type == "share.state")
                    {
                        string state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                        int width = doc.RootElement.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        int height = doc.RootElement.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        int fps = doc.RootElement.TryGetProperty("fps", out var f) ? f.GetInt32() : 0;
                        OnShareStateChanged?.Invoke(state, width, height, fps);
                    }
                    else if (type == "share.stats")
                    {
                        OnShareStatsReceived?.Invoke(json);
                    }
                    else if (type == "remote_stream")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        string state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                        string title = doc.RootElement.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        string thumb = doc.RootElement.TryGetProperty("thumbnail_b64", out var tb) ? tb.GetString() ?? "" : "";
                        OnRemoteStreamReceived?.Invoke(peerKey, state, title, thumb);
                    }
                    else if (type == "video.frame")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        int width = doc.RootElement.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        int height = doc.RootElement.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        bool isKf = doc.RootElement.TryGetProperty("is_keyframe", out var kf) && kf.GetBoolean();
                        OnVideoFrameReceived?.Invoke(peerKey, width, height, isKf);
                    }
                    else if (type == "gs.state")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        string state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                        OnGameStreamStateChanged?.Invoke(peerKey, state);
                    }
                    else if (type == "gs.stats")
                    {
                        OnGameStreamStatsReceived?.Invoke(json);
                    }
                    else if (type == "gs.high_loss")
                    {
                        double lossRate = doc.RootElement.TryGetProperty("loss_rate", out var lr) ? lr.GetDouble() : 0.0;
                        OnGameStreamHighLoss?.Invoke(lossRate);
                    }
                    else if (type == "gs.probe_result")
                    {
                        string peerKey = doc.RootElement.TryGetProperty("peer_key", out var pk) ? pk.GetString() ?? "" : "";
                        int capacity = doc.RootElement.TryGetProperty("capacity_kbps", out var cap) ? cap.GetInt32() : 0;
                        string profile = doc.RootElement.TryGetProperty("profile", out var prof) ? prof.GetRawText() : "{}";
                        OnGameStreamProbeResult?.Invoke(peerKey, capacity, profile);
                    }
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_gcHandle.IsAllocated)
        {
            _gcHandle.Free();
        }
    }
}
