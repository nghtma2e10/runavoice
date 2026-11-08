using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Oyun Akışı oturum durumları (PDF Bölüm 11)
    /// </summary>
    public enum GameStreamPhase
    {
        Idle,
        Inviting,
        Invited,
        Pairing,
        Tunneling,
        Probing,
        Launching,
        Playing,
        Ending
    }

    /// <summary>
    /// Kullanıcının bu oturumdaki rolü
    /// </summary>
    public enum GameStreamRole
    {
        None,
        Host,
        Player
    }

    /// <summary>
    /// Bilgisayarın Oyun Akışı yetenekleri (PDF Bölüm 5.1)
    /// </summary>
    public class GameStreamCapabilities
    {
        public bool CanHost { get; set; }
        public bool CanPlay { get; set; }
        public string? HostReason { get; set; }
        public string? PlayReason { get; set; }
        public string? SunshineVersion { get; set; }
        public string? MoonlightVersion { get; set; }
        public List<string> Codecs { get; set; } = new();
    }

    /// <summary>
    /// Sunshine veya kayıtlı oyun bilgisi
    /// </summary>
    public class GameStreamAppInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string WorkingDir { get; set; } = "";
        public List<string> ExtraAllowedProcesses { get; set; } = new();
    }

    /// <summary>
    /// Kalite ve kodlayıcı profili (PDF Bölüm 8)
    /// </summary>
    public class GameStreamQualityProfile
    {
        public string Resolution { get; set; } = "1080p";
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int Fps { get; set; } = 60;
        public string Codec { get; set; } = "HEVC";
        public int BitrateKbps { get; set; } = 3800;

        public string SummaryText => $"{Resolution} · {Fps} FPS · {Codec} · ~{(BitrateKbps / 1000.0):0.0} Mbps";
    }

    /// <summary>
    /// Tünel canlı telemetri ve sayaçları (PDF Bölüm 7.3 & 8)
    /// </summary>
    public class GameStreamTunnelStats
    {
        public long PacketsSent { get; set; }
        public long PacketsReceived { get; set; }
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public long PacketsLost { get; set; }
        public int RttMs { get; set; }
        public int BitrateKbps { get; set; }
        public double LossRatePercent { get; set; }
    }

    /// <summary>
    /// Odadaki bir kullanıcının Oyun Akışı durumu (GS_PRESENCE tabanlı)
    /// </summary>
    public class GameStreamPeerPresence
    {
        public string PeerKey { get; set; } = "";
        public string PeerName { get; set; } = "";
        public string AvatarPath { get; set; } = "";
        public bool IsOpen { get; set; }
        public bool CanHost { get; set; }
        public bool CanPlay { get; set; }
        public bool IsBusy { get; set; }
        public string? PlayingWith { get; set; }
        public List<string> Codecs { get; set; } = new();
        public int ProtocolVersion { get; set; } = 1;
    }

    // =========================================================================
    // PROTOKOL MESAJLARI (GS_ önekli, PDF Bölüm 6)
    // =========================================================================

    public class GsMessageBase
    {
        [JsonPropertyName("t")]
        public string Type { get; set; } = "";
    }

    public class GsPresenceMessage : GsMessageBase
    {
        public const string MsgType = "GS_PRESENCE";

        [JsonPropertyName("open")]
        public bool Open { get; set; }

        [JsonPropertyName("canHost")]
        public bool CanHost { get; set; }

        [JsonPropertyName("canPlay")]
        public bool CanPlay { get; set; }

        [JsonPropertyName("busy")]
        public bool Busy { get; set; }

        [JsonPropertyName("codecs")]
        public List<string> Codecs { get; set; } = new();

        [JsonPropertyName("v")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("playingWith")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlayingWith { get; set; }

        public GsPresenceMessage() => Type = MsgType;
    }

    public class GsInviteMessage : GsMessageBase
    {
        public const string MsgType = "GS_INVITE";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("game")]
        public string Game { get; set; } = "";

        [JsonPropertyName("res")]
        public string Resolution { get; set; } = "1080p";

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 60;

        [JsonPropertyName("codec")]
        public string Codec { get; set; } = "HEVC";

        [JsonPropertyName("kbps")]
        public int BitrateKbps { get; set; } = 3800;

        [JsonPropertyName("privacy")]
        public bool Privacy { get; set; } = true;

        [JsonPropertyName("exp")]
        public int ExpireSeconds { get; set; } = 60;

        public GsInviteMessage() => Type = MsgType;
    }

    public class GsInviteCancelMessage : GsMessageBase
    {
        public const string MsgType = "GS_INVITE_CANCEL";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        public GsInviteCancelMessage() => Type = MsgType;
    }

    public class GsResponseMessage : GsMessageBase
    {
        public const string MsgType = "GS_RESPONSE";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("accept")]
        public bool Accept { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = ""; // "declined" | "busy" | "timeout" | "no_moonlight"

        public GsResponseMessage() => Type = MsgType;
    }

    public class GsPairPinMessage : GsMessageBase
    {
        public const string MsgType = "GS_PAIR_PIN";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("pin")]
        public string Pin { get; set; } = ""; // 4 haneli PIN

        public GsPairPinMessage() => Type = MsgType;
    }

    public class GsPairResultMessage : GsMessageBase
    {
        public const string MsgType = "GS_PAIR_RESULT";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }

        public GsPairResultMessage() => Type = MsgType;
    }

    public class GsProbeMessage : GsMessageBase
    {
        public const string MsgType = "GS_PROBE";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("phase")]
        public int Phase { get; set; }

        [JsonPropertyName("kbps")]
        public int Kbps { get; set; }

        public GsProbeMessage() => Type = MsgType;
    }

    public class GsProbeResultMessage : GsMessageBase
    {
        public const string MsgType = "GS_PROBE_RESULT";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("phase")]
        public int Phase { get; set; }

        [JsonPropertyName("kbps")]
        public int Kbps { get; set; }

        [JsonPropertyName("loss")]
        public double LossRate { get; set; }

        public GsProbeResultMessage() => Type = MsgType;
    }

    public class GsStartMessage : GsMessageBase
    {
        public const string MsgType = "GS_START";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("app")]
        public string App { get; set; } = "";

        [JsonPropertyName("res")]
        public string Resolution { get; set; } = "1080p";

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 60;

        [JsonPropertyName("codec")]
        public string Codec { get; set; } = "HEVC";

        [JsonPropertyName("kbps")]
        public int BitrateKbps { get; set; } = 3800;

        public GsStartMessage() => Type = MsgType;
    }

    public class GsStateMessage : GsMessageBase
    {
        public const string MsgType = "GS_STATE";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = ""; // pairing|tunnel|probing|launching|playing|ending

        [JsonPropertyName("info")]
        public string Info { get; set; } = "";

        public GsStateMessage() => Type = MsgType;
    }

    public class GsCurtainMessage : GsMessageBase
    {
        public const string MsgType = "GS_CURTAIN";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("on")]
        public bool On { get; set; }

        public GsCurtainMessage() => Type = MsgType;
    }

    public class GsEndMessage : GsMessageBase
    {
        public const string MsgType = "GS_END";

        [JsonPropertyName("sid")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = ""; // user|host_hotkey|game_exit|error|left_room|timeout

        public GsEndMessage() => Type = MsgType;
    }
}
