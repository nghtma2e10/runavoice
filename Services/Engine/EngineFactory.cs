using System;
using RunaVoice_WinUI.Services;

namespace RunaVoice_WinUI.Services.Engine;

public static class EngineFactory
{
    public static IRealtimeEngine CreateEngine(
        StorageService storage,
        SignalingService signaling,
        AudioEngine audio,
        WebRtcService webrtc,
        ScreenCaptureService screenCapture,
        out string? fallbackNotice)
    {
        fallbackNotice = null;

        string selectedEngine = storage.Settings.RealtimeEngine;
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--engine=", StringComparison.OrdinalIgnoreCase))
            {
                selectedEngine = arg["--engine=".Length..].Trim();
                break;
            }
        }

        if (selectedEngine.Equals("rust", StringComparison.OrdinalIgnoreCase))
        {
            if (RustRealtimeEngine.TryCreate(storage, signaling, audio, webrtc, screenCapture, out var rustEngine, out var error))
            {
                signaling.ActiveEngineName = "rust";
                return rustEngine;
            }

            signaling.ActiveEngineName = "legacy";
            fallbackNotice = error ?? "Rust motoru yüklenemedi, eski motora dönüldü.";
            return new LegacyRealtimeEngine(audio, webrtc, screenCapture);
        }

        signaling.ActiveEngineName = "legacy";
        return new LegacyRealtimeEngine(audio, webrtc, screenCapture);
    }
}
