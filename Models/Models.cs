using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace RunaVoice_WinUI.Models
{
    public enum PeerPingStatus
    {
        Connecting,
        NoP2p,
        Measuring,
        Measured,
        Timeout
    }

    /// <summary>
    /// Bir eşle ilgili gecikme ölçümleri.
    /// NetworkRttMs: ses/görüntüyle aynı UDP/SRTP yolundan giden ölçüm paketinin gidiş-dönüşü (yalnızca ağ).
    /// AudioOneWayMs: o eşin sesinin bize ulaşma süresi (tek yön): mikrofon tamponu + paketleme + Opus kodlama
    /// (algoritmik gecikme dahil) + ağ (RTT/2) + alış tamponu + Opus çözme + hoparlör tamponu.
    /// Ölçülemeyen parçalar -1'dir ve toplama katılmaz (IsAudioComplete=false olur).
    /// </summary>
    public sealed class PeerLatencyInfo
    {
        public double NetworkRttMs { get; init; } = -1;
        public double NetworkJitterMs { get; init; } = -1;
        public bool IsUdpPath { get; init; }

        public double PeerCaptureBufferMs { get; init; } = -1;
        public double PeerFrameMs { get; init; } = -1;
        public double PeerEncodeMs { get; init; } = -1;     // CPU'da kodlama süresi (ölçülen)
        public double PeerLookaheadMs { get; init; } = -1;  // Opus algoritmik gecikmesi
        public double JitterBufferMs { get; init; } = -1;
        public double LocalDecodeMs { get; init; } = -1;
        public double PlaybackBufferMs { get; init; } = -1;
        public double JitterTargetMs { get; init; } = -1;   // alış tamponunun uyarlanabilir güvenlik payı
        public int Underruns { get; init; } = 0;             // bu eşin sesinde tampon boşalması (kesinti) sayısı
        public string PlaybackMode { get; init; } = "";      // hoparlör çıkış yolu (IAudioClient3 / standart)
        public int PlaybackGlitches { get; init; } = -1;     // düşük gecikmeli çıkışta ses kartı kuyruğu boşalması

        public double NetworkOneWayMs => NetworkRttMs >= 0 ? NetworkRttMs / 2.0 : -1;
        public double CodecTotalMs =>
            Sum(PeerFrameMs, PeerLookaheadMs, PeerEncodeMs, LocalDecodeMs);

        public bool IsAudioComplete =>
            NetworkRttMs >= 0 && PeerCaptureBufferMs >= 0 && PeerFrameMs >= 0 && PeerEncodeMs >= 0 &&
            PeerLookaheadMs >= 0 && JitterBufferMs >= 0 && LocalDecodeMs >= 0 && PlaybackBufferMs >= 0;

        public double AudioOneWayMs => IsAudioComplete
            ? PeerCaptureBufferMs + PeerFrameMs + PeerLookaheadMs + PeerEncodeMs + NetworkOneWayMs +
              JitterBufferMs + LocalDecodeMs + PlaybackBufferMs
            : -1;

        private static double Sum(params double[] parts)
        {
            double s = 0;
            foreach (var p in parts) { if (p < 0) return -1; s += p; }
            return s;
        }

        /// <summary>Küçük değerlerde ondalık gösterir ("0,4 ms"); büyüklerde tam sayı.</summary>
        public static string FormatMs(double ms)
        {
            if (ms < 0) return "—";
            if (ms > 999) return ">999 ms";
            if (ms < 0.1) return $"<{0.1:0.0} ms";
            if (ms < 10) return $"{ms:0.0} ms";
            return $"{Math.Round(ms):0} ms";
        }
    }


    public static class MqttKeyHelper
    {
        public static string SanitizeTopicSegment(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "peer";
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_')
                    sb.Append(ch);
                else if (ch == 'ç') sb.Append('c');
                else if (ch == 'ğ') sb.Append('g');
                else if (ch == 'ı') sb.Append('i');
                else if (ch == 'ö') sb.Append('o');
                else if (ch == 'ş') sb.Append('s');
                else if (ch == 'ü') sb.Append('u');
                else if (ch == '#' || ch == '+' || ch == '/' || char.IsWhiteSpace(ch))
                    sb.Append('_');
            }
            return sb.Length > 0 ? sb.ToString() : "peer";
        }
    }

    public static class AvatarHelper
    {
        private static readonly string AppBaseDir = AppContext.BaseDirectory;

        public static BitmapImage? CreateBitmapFromBytes(byte[]? bytes, int decodeWidth = 0)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                    writer.DetachStream();
                }
                stream.Seek(0);

                var bmp = new BitmapImage();
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.SetSource(stream);
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // Ağdan gelen data URI avatarlar için üst sınır (72x72 JPEG ~4 KB; büyük resimler bellek tüketimi için kullanılamasın)
        private const int MaxAvatarDataUriChars = 512 * 1024;

        private static readonly string LocalAppDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>
        /// Avatar/kapak olarak açılmasına izin verilen yerel dosya mı? Uzak katılımcılar avatar alanına keyfi
        /// yol yazabildiği için yalnızca uygulama klasörü ve RUNA'nın kendi veri klasörleri kabul edilir.
        /// Ağ (UNC) yolları reddedilir: \\sunucu\pay yoluna erişim Windows'un NTLM kimlik özetini sızdırabilir.
        /// </summary>
        public static bool TryResolveSafeLocalImagePath(string? spec, out string fullPath)
        {
            fullPath = "";
            if (string.IsNullOrWhiteSpace(spec) || spec.Length > 1024) return false;
            try
            {
                string candidate = Path.IsPathRooted(spec)
                    ? spec
                    : Path.Combine(AppBaseDir, spec.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, '\\'));

                // UNC, cihaz (\\?\, \\.\) ve tek ters eğik çizgiyle başlayan göreli-kök yolları reddet
                if (candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal))
                    return false;

                string full = Path.GetFullPath(candidate);
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;

                bool insideApp = full.StartsWith(Path.GetFullPath(AppBaseDir), StringComparison.OrdinalIgnoreCase);
                bool insideRunaData = full.StartsWith(Path.Combine(LocalAppDataDir, "RunaVoice"), StringComparison.OrdinalIgnoreCase);
                if (!insideApp && !insideRunaData) return false;

                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) return false;

                if (!File.Exists(full)) return false;
                fullPath = full;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static ImageSource LoadAvatarSource(string? avatarSpec, int decodeWidth = 96)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(avatarSpec))
                {
                    // 1. Data URI (Base64 encoded avatar sent over P2P presence)
                    if (avatarSpec.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        int commaIdx = avatarSpec.IndexOf(',');
                        if (commaIdx > 0 && commaIdx + 1 < avatarSpec.Length && avatarSpec.Length <= MaxAvatarDataUriChars)
                        {
                            byte[] bytes = Convert.FromBase64String(avatarSpec[(commaIdx + 1)..]);
                            var bmp = CreateBitmapFromBytes(bytes, decodeWidth > 0 ? decodeWidth : 96);
                            if (bmp != null) return bmp;
                        }
                    }
                    // 2. Yalnızca güvenli yerel dosyalar (uygulama klasörü veya RUNA veri klasörü)
                    else if (TryResolveSafeLocalImagePath(avatarSpec, out string safePath))
                    {
                        return new BitmapImage(new Uri(safePath)) { DecodePixelWidth = decodeWidth };
                    }
                }

                // Fallback to bundled default-avatar.png, then kaan.jpg or logo.jpg
                string defaultFallback = Path.Combine(AppBaseDir, "Assets", "avatars", "default-avatar.png");
                if (File.Exists(defaultFallback))
                {
                    return new BitmapImage(new Uri(defaultFallback)) { DecodePixelWidth = decodeWidth };
                }

                string fallback = Path.Combine(AppBaseDir, "Assets", "avatars", "kaan.jpg");
                if (File.Exists(fallback))
                {
                    return new BitmapImage(new Uri(fallback)) { DecodePixelWidth = decodeWidth };
                }

                string logoFallback = Path.Combine(AppBaseDir, "Assets", "logo.jpg");
                if (File.Exists(logoFallback))
                {
                    return new BitmapImage(new Uri(logoFallback)) { DecodePixelWidth = decodeWidth };
                }
            }
            catch { }

            return new BitmapImage();
        }

        public static ImageSource LoadCoverSource(string? coverSpec, int decodeWidth = 180)
        {
            try
            {
                string target = string.IsNullOrWhiteSpace(coverSpec) ? "Assets/covers/cover_moon.png" : coverSpec;
                if (TryResolveSafeLocalImagePath(target, out string safePath))
                {
                    return new BitmapImage(new Uri(safePath)) { DecodePixelWidth = decodeWidth };
                }

                string fallback = Path.Combine(AppBaseDir, "Assets", "covers", "cover_moon.png");
                if (File.Exists(fallback))
                {
                    return new BitmapImage(new Uri(fallback)) { DecodePixelWidth = decodeWidth };
                }
            }
            catch { }

            return new BitmapImage();
        }
    }

    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        public void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AppSettings : ObservableObject
    {
        // Profile
        public string UserName { get; set; } = Environment.UserName;
        public string AvatarPath { get; set; } = "Assets/avatars/default-avatar.png";
        public string AvatarDataUri { get; set; } = "";

        // Audio Devices & Volumes
        public string InputDeviceId { get; set; } = "";
        public string OutputDeviceId { get; set; } = "";
        public float InputGainPercent { get; set; } = 100f;
        public float OutputMasterPercent { get; set; } = 100f;

        // Noise Suppression & DSP
        public string NoiseEngine { get; set; } = "rnnoise"; // rnnoise | deep | speex | off
        public int NoiseSuppressionLevel { get; set; } = 3;   // 1..5
        public bool ImpactGuardEnabled { get; set; } = true;
        public bool HeadphoneAecEnabled { get; set; } = true;

        // Voice Profile & 5-Band EQ
        public string SoundProfile { get; set; } = "clean";  // clean | podcast | clarity | deep | flat | custom
        public float Eq80Hz { get; set; } = -2.0f;
        public float Eq300Hz { get; set; } = -1.0f;
        public float Eq1kHz { get; set; } = 1.5f;
        public float Eq3_5kHz { get; set; } = 3.0f;
        public float Eq8kHz { get; set; } = 2.0f;

        // Ear Protection Max dB Ceiling (-24 dB .. 0 dB)
        public float MaxOutputCeilingDb { get; set; } = -2.0f;

        // Input Mode (VAD vs PTT)
        public string InputMode { get; set; } = "vad"; // vad | ptt
        public float VadThresholdDb { get; set; } = -45.0f;
        public string PttKeyName { get; set; } = "V";
        public int PttVirtualKey { get; set; } = 86; // VirtualKey.V

        // Opus Codec & Transport
        public int OpusBitrateKbps { get; set; } = 96; // 32 | 64 | 96 | 128 | 256
        public float OpusFrameMs { get; set; } = 10f;  // 20 | 10 | 5 | 2.5

        // Screen Share Defaults
        public int DefaultStreamHeight { get; set; } = 1080; // 720 | 1080 | 1440 | 2160
        public int DefaultStreamFps { get; set; } = 30;      // 30 | 60
        public string PreferredVideoCodec { get; set; } = "auto"; // auto | av1 | h265 | h264
        public bool DataSaverMode { get; set; } = false;
        public int StreamSharpnessPercent { get; set; } = 0;  // 0..100 (0 = donanım ivmeli sıfır CPU yükü)
        public int P2PRelayMaxChildren { get; set; } = 2;     // 1..4

        // Appearance & UX
        public double UiScale { get; set; } = 1.0;
        public bool RippleAnimationsEnabled { get; set; } = true;
        public bool UiSoundsEnabled { get; set; } = true;
        public float SoundpadVolumePercent { get; set; } = 85f;
        public bool SoundpadBroadcastToRoom { get; set; } = true;

        // Screen Annotations
        public bool AllowViewerAnnotations { get; set; } = false;
        public int AnnotationFadeSeconds { get; set; } = 8; // 0 = kalıcı; izinli değerler: 0, 4, 8, 15, 30
        public bool ShowAnnotationNames { get; set; } = true;

        // Ses ve Yayın Geliştirmeleri (Plan Bölüm 7)
        public string VoiceLevelingMode { get; set; } = "normal"; // off | light | normal | strong
        public float VoiceLevelingTargetDb { get; set; } = -22f; // -26 | -22 | -18
        public bool SpatialAudioEnabled { get; set; } = false;
        public string SpatialAudioMode { get; set; } = "auto"; // auto | manual
        public bool SpatialSpeakerMode { get; set; } = false;
        public bool AdaptiveStreamEnabled { get; set; } = true;
        public string AdaptiveStreamPriority { get; set; } = "clarity"; // clarity | motion
        public int LastAdaptiveLevel { get; set; } = 1;

        // Saved Per-Peer Audio Configs (key: peerNameLower)
        public Dictionary<string, PeerAudioConfig> PeerAudioConfigs { get; set; } = new();
        public List<CustomSoundItem> CustomSounds { get; set; } = new();

        // GameStream Ayarları (PDF Bölüm 12)
        public string SunshineCredsProtected { get; set; } = ""; // DPAPI ile şifreli "kullanıcı:parola"
        public int GameStreamMaxBitrateKbps { get; set; } = 0; // 0 = otomatik
        public string GameStreamPreferredCodec { get; set; } = "auto"; // auto | av1 | hevc | h264
        public string GameStreamResolution { get; set; } = "auto"; // auto | 1080p | 720p
        public int GameStreamFps { get; set; } = 0; // 0 = otomatik | 30 | 60
        public List<GameStreamGame> GameStreamGames { get; set; } = new();
        public List<string> GameStreamTrustedPlayers { get; set; } = new(); // "her zaman izin ver" (ad, küçük harf)

        // Gerçek Zamanlı Motor Seçimi (Plan Bölüm 3, 5.5, Ek-2: legacy | rust)
        public string RealtimeEngine { get; set; } = "legacy";
    }

    public class GameStreamGame
    {
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string SunshineAppName { get; set; } = "";
        public List<string> ExtraAllowedProcesses { get; set; } = new(); // perde için
    }

    public class PeerAudioConfig
    {
        public int VolumePercent { get; set; } = 100; // 0..200
        public bool LocalMuted { get; set; } = false;
        public bool ClarityBoost { get; set; } = false;

        // Ses ve Yayın Geliştirmeleri (Plan Bölüm 7)
        public bool ExcludeFromLeveling { get; set; } = false;
        public float SpatialAzimuthDeg { get; set; } = 0f; // -180..180
        public float SpatialDistance { get; set; } = 0.2f; // 0..1
        public bool HasManualSpatialPosition { get; set; } = false;
    }

    /// <summary>
    /// Ekran Paylaşımı Dinamik Çözünürlük Alıcı Raporu (VQ_REPORT - Özellik 4)
    /// </summary>
    public sealed class VideoQualityReport
    {
        public int Fr { get; set; }
        public int Fd { get; set; }
        public int Gap { get; set; }
        public int Kbps { get; set; }
        public int Jit { get; set; }
        public int Rd { get; set; }
        public int Lvl { get; set; }
        public double LossPct { get; set; } = 0.0;
        public string Via { get; set; } = "direct";

        public static bool TryParse(System.Text.Json.JsonElement root, out VideoQualityReport r)
        {
            r = new VideoQualityReport();
            try
            {
                r.Fr = Math.Clamp(root.TryGetProperty("fr", out var el) && el.TryGetInt32(out int fr) ? fr : 0, 0, 240);
                r.Fd = Math.Clamp(root.TryGetProperty("fd", out el) && el.TryGetInt32(out int fd) ? fd : 0, 0, 1000);
                r.Gap = Math.Clamp(root.TryGetProperty("gap", out el) && el.TryGetInt32(out int gap) ? gap : 0, 0, 1000);
                r.Kbps = Math.Clamp(root.TryGetProperty("kbps", out el) && el.TryGetInt32(out int kbps) ? kbps : 0, 0, 200000);
                r.Jit = Math.Clamp(root.TryGetProperty("jit", out el) && el.TryGetInt32(out int jit) ? jit : 0, 0, 5000);
                r.Rd = Math.Clamp(root.TryGetProperty("rd", out el) && el.TryGetInt32(out int rd) ? rd : 0, 0, 1000);
                r.Lvl = Math.Clamp(root.TryGetProperty("lvl", out el) && el.TryGetInt32(out int lvl) ? lvl : 0, 0, 6);
                r.LossPct = root.TryGetProperty("loss", out el) && el.TryGetDouble(out double loss) ? Math.Clamp(loss, 0.0, 100.0) : 0.0;
                r.Via = root.TryGetProperty("via", out el) ? el.GetString() ?? "direct" : "direct";
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string ToJson()
        {
            string lossStr = LossPct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return $"{{\"t\":\"VQ_REPORT\",\"v\":1,\"fr\":{Fr},\"fd\":{Fd},\"gap\":{Gap},\"kbps\":{Kbps},\"jit\":{Jit},\"rd\":{Rd},\"lvl\":{Lvl},\"loss\":{lossStr},\"via\":\"{Via}\"}}";
        }
    }

    /// <summary>
    /// Ekran Paylaşımı Dinamik Çözünürlük Yayıncı Durum Mesajı (VQ_STATE - Özellik 4)
    /// </summary>
    public sealed class VideoQualityState
    {
        public int Lvl { get; set; } = 0;
        public int H { get; set; } = 1080;
        public int Fps { get; set; } = 60;
        public int Q { get; set; } = 80;
        public bool Adaptive { get; set; } = true;
        public string Reason { get; set; } = "init";

        public static bool TryParse(System.Text.Json.JsonElement root, out VideoQualityState s)
        {
            s = new VideoQualityState();
            try
            {
                s.Lvl = Math.Clamp(root.TryGetProperty("lvl", out var el) && el.TryGetInt32(out int lvl) ? lvl : 0, 0, 6);
                s.H = Math.Clamp(root.TryGetProperty("h", out el) && el.TryGetInt32(out int h) ? h : 720, 240, 2160);
                s.Fps = Math.Clamp(root.TryGetProperty("fps", out el) && el.TryGetInt32(out int fps) ? fps : 30, 5, 120);
                s.Q = Math.Clamp(root.TryGetProperty("q", out el) && el.TryGetInt32(out int q) ? q : 75, 30, 100);
                s.Adaptive = !root.TryGetProperty("adaptive", out el) || el.GetBoolean();
                s.Reason = root.TryGetProperty("reason", out el) ? el.GetString() ?? "" : "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string ToJson()
        {
            return $"{{\"t\":\"VQ_STATE\",\"lvl\":{Lvl},\"h\":{H},\"fps\":{Fps},\"q\":{Q},\"adaptive\":{(Adaptive ? "true" : "false")},\"reason\":\"{Reason}\"}}";
        }
    }

    public class CustomSoundItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Title { get; set; } = "";
        public string Emoji { get; set; } = "🎵";
        public string FilePath { get; set; } = "";
        public bool IsBuiltIn { get; set; } = false;
        public string BuiltInSynthId { get; set; } = "";
    }

    public class UserProfile : ObservableObject
    {
        private string _name = Environment.UserName;
        private string _avatarPath = "Assets/avatars/default-avatar.png";
        private string _avatarDataUri = "";
        private string _voiceSessionId = Guid.NewGuid().ToString("N")[..10];
        private bool _isMuted = false;
        private bool _isDeafened = false;
        private bool _isScreenSharing = false;

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string AvatarPath { get => _avatarPath; set => SetProperty(ref _avatarPath, value); }
        public string AvatarDataUri { get => _avatarDataUri; set => SetProperty(ref _avatarDataUri, value); }
        public string VoiceSessionId { get => _voiceSessionId; set => SetProperty(ref _voiceSessionId, value); }
        public bool IsMuted { get => _isMuted; set => SetProperty(ref _isMuted, value); }
        public bool IsDeafened { get => _isDeafened; set => SetProperty(ref _isDeafened, value); }
        public bool IsScreenSharing { get => _isScreenSharing; set => SetProperty(ref _isScreenSharing, value); }

        public string PeerKey => $"{MqttKeyHelper.SanitizeTopicSegment(Name)}__{VoiceSessionId}";
    }

    public sealed class PeerPresenceSnapshot
    {
        public string PeerKey { get; set; } = "";
        public string Name { get; set; } = "Misafir";
        public string Avatar { get; set; } = "Assets/avatars/kaan.jpg";
        public string VoiceSessionId { get; set; } = "";
        public bool IsMuted { get; set; }
        public bool IsDeafened { get; set; }
        public bool IsScreenSharing { get; set; }
        public long LastSeen { get; set; }
        public string Engine { get; set; } = "legacy";
        public int Proto { get; set; } = 1;
    }

    public class PersistentRoom : ObservableObject
    {
        private string _code = "";
        private string _title = "";
        private string _coverImage = "Assets/covers/cover_moon.png";
        private bool _isOwner = false;
        private int _onlineCount = 0;

        [JsonPropertyName("code")]
        public string Code { get => _code; set => SetProperty(ref _code, value); }

        [JsonPropertyName("title")]
        public string Title { get => _title; set => SetProperty(ref _title, value); }

        [JsonPropertyName("coverImage")]
        public string CoverImage
        {
            get => _coverImage;
            set
            {
                if (SetProperty(ref _coverImage, value))
                {
                    RaisePropertyChanged(nameof(CoverBitmap));
                }
            }
        }

        [JsonPropertyName("isOwner")]
        public bool IsOwner { get => _isOwner; set => SetProperty(ref _isOwner, value); }

        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [JsonPropertyName("lastJoinedAt")]
        public long LastJoinedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [JsonIgnore]
        public int OnlineCount
        {
            get => _onlineCount;
            set
            {
                if (SetProperty(ref _onlineCount, value))
                {
                    RaisePropertyChanged(nameof(OnlineStatusText));
                    RaisePropertyChanged(nameof(OnlineDotBrush));
                    RaisePropertyChanged(nameof(OnlineTextBrush));
                }
            }
        }

        private static readonly SolidColorBrush RoomLiveDotBrush = new(Windows.UI.Color.FromArgb(255, 168, 85, 247));
        private static readonly SolidColorBrush RoomIdleDotBrush = new(Windows.UI.Color.FromArgb(255, 100, 116, 139));
        private static readonly SolidColorBrush RoomLiveTxtBrush = new(Windows.UI.Color.FromArgb(255, 192, 132, 252));
        private static readonly SolidColorBrush RoomIdleTxtBrush = new(Windows.UI.Color.FromArgb(255, 148, 163, 184));

        [JsonIgnore]
        public string OnlineStatusText => _onlineCount > 0 ? $"{_onlineCount} Çevrimiçi" : "Oda Boş";

        [JsonIgnore]
        public Brush OnlineDotBrush => _onlineCount > 0 ? RoomLiveDotBrush : RoomIdleDotBrush;

        [JsonIgnore]
        public Brush OnlineTextBrush => _onlineCount > 0 ? RoomLiveTxtBrush : RoomIdleTxtBrush;

        [JsonIgnore]
        public ImageSource CoverBitmap => AvatarHelper.LoadCoverSource(_coverImage, 160);
    }

    public class RoomParticipant : ObservableObject
    {
        private string _peerKey = "";
        private string _name = "Misafir";
        private string _avatar = "Assets/avatars/kaan.jpg";
        private string _voiceSessionId = "";
        private bool _isSelf = false;
        private bool _isMuted = false;
        private bool _isLocalMuted = false;
        private bool _isDeafened = false;
        private bool _isScreenSharing = false;
        private bool _isSpeaking = false;
        private int _latencyRttMs = 0;
        private int _volumePercent = 100;
        private bool _clarityBoost = false;
        private int _treeDepth = 0; // 0 = root/normal, 1 = direct viewer, 2 = relayed viewer
        private string _relayParentName = "";
        private bool _isGameStreaming = false;
        private string _gameStreamStatusText = "";
        private ImageSource? _cachedAvatarBitmap;
        private PeerPingStatus _pingStatus = PeerPingStatus.Connecting;
        private int _jitterMs = -1;
        private int _selfAvgRtt = 0;
        private int _selfPeerCount = 0;
        private string _engine = "legacy";
        private int _proto = 1;
        private bool _isLegacyMismatch = false;

        public string Engine { get => _engine; set => SetProperty(ref _engine, value); }
        public int Proto { get => _proto; set => SetProperty(ref _proto, value); }
        public bool IsLegacyMismatch { get => _isLegacyMismatch; set => SetProperty(ref _isLegacyMismatch, value); }

        public string PeerKey { get => _peerKey; set => SetProperty(ref _peerKey, value); }
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    RaisePropertyChanged(nameof(DisplayName));
            }
        }

        public string Avatar
        {
            get => _avatar;
            set
            {
                if (SetProperty(ref _avatar, value))
                {
                    _cachedAvatarBitmap = null;
                    RaisePropertyChanged(nameof(AvatarBitmap));
                }
            }
        }

        public string VoiceSessionId { get => _voiceSessionId; set => SetProperty(ref _voiceSessionId, value); }
        public bool IsSelf
        {
            get => _isSelf;
            set
            {
                if (SetProperty(ref _isSelf, value))
                {
                    RaisePropertyChanged(nameof(DisplayName));
                    RaisePropertyChanged(nameof(SubtitleText));
                    RaisePropertyChanged(nameof(PingDotBrush));
                    RaisePropertyChanged(nameof(PingText));
                    RaisePropertyChanged(nameof(WifiBarsVisibility));
                    RaisePropertyChanged(nameof(SelfBadgeVisibility));
                    RaisePropertyChanged(nameof(PingToolTip));
                }
            }
        }

        public PeerPingStatus PingStatus
        {
            get => _pingStatus;
            set
            {
                if (SetProperty(ref _pingStatus, value))
                {
                    RaisePropertyChanged(nameof(PingText));
                    RaisePropertyChanged(nameof(PingToolTip));
                    RaisePropertyChanged(nameof(PingTextBrush));
                    RaisePropertyChanged(nameof(PingDotBrush));
                    RaisePropertyChanged(nameof(WifiBar1Brush));
                    RaisePropertyChanged(nameof(WifiBar2Brush));
                    RaisePropertyChanged(nameof(WifiBar3Brush));
                    RaisePropertyChanged(nameof(SubtitleText));
                }
            }
        }

        public int JitterMs
        {
            get => _jitterMs;
            set
            {
                if (SetProperty(ref _jitterMs, value))
                {
                    RaisePropertyChanged(nameof(PingToolTip));
                }
            }
        }

        public void UpdateSelfPeerStats(int avgRtt, int peerCount)
        {
            _selfAvgRtt = avgRtt;
            _selfPeerCount = peerCount;
            RaisePropertyChanged(nameof(SelfBadgeVisibility));
            RaisePropertyChanged(nameof(PingToolTip));
        }

        public Visibility WifiBarsVisibility => _isSelf ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SelfBadgeVisibility => (_isSelf && _selfPeerCount > 0) ? Visibility.Visible : Visibility.Collapsed;

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    RaisePropertyChanged(nameof(EffectiveMutedVisibility));
                    RaisePropertyChanged(nameof(OpenMicVisibility));
                    RaisePropertyChanged(nameof(MicStatusBrush));
                    RaisePropertyChanged(nameof(MicStatusPath));
                }
            }
        }

        public bool IsLocalMuted
        {
            get => _isLocalMuted;
            set
            {
                if (SetProperty(ref _isLocalMuted, value))
                {
                    RaisePropertyChanged(nameof(EffectiveMutedVisibility));
                    RaisePropertyChanged(nameof(OpenMicVisibility));
                    RaisePropertyChanged(nameof(MicStatusBrush));
                    RaisePropertyChanged(nameof(MicStatusPath));
                    RaisePropertyChanged(nameof(VolumeBadgeVisibility));
                    RaisePropertyChanged(nameof(VolumeBadgeText));
                }
            }
        }

        public bool IsDeafened
        {
            get => _isDeafened;
            set
            {
                if (SetProperty(ref _isDeafened, value))
                {
                    RaisePropertyChanged(nameof(DeafenedVisibility));
                    RaisePropertyChanged(nameof(OpenHeadphoneVisibility));
                    RaisePropertyChanged(nameof(DeafenStatusBrush));
                    RaisePropertyChanged(nameof(DeafenStatusPath));
                }
            }
        }

        public bool IsScreenSharing
        {
            get => _isScreenSharing;
            set
            {
                if (SetProperty(ref _isScreenSharing, value))
                {
                    RaisePropertyChanged(nameof(ScreenShareBadgeVisibility));
                    RaisePropertyChanged(nameof(WatchStreamButtonVisibility));
                }
            }
        }

        public bool IsGameStreaming
        {
            get => _isGameStreaming;
            set
            {
                if (SetProperty(ref _isGameStreaming, value))
                {
                    RaisePropertyChanged(nameof(GameStreamBadgeVisibility));
                }
            }
        }

        public string GameStreamStatusText
        {
            get => _gameStreamStatusText;
            set => SetProperty(ref _gameStreamStatusText, value);
        }

        public Visibility GameStreamBadgeVisibility => _isGameStreaming ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSpeaking
        {
            get => _isSpeaking;
            set
            {
                if (SetProperty(ref _isSpeaking, value))
                {
                    RaisePropertyChanged(nameof(SpeakingRingBrush));
                    RaisePropertyChanged(nameof(BubbleRingBrush));
                    RaisePropertyChanged(nameof(SpeakingBorderBrush));
                }
            }
        }

        public int LatencyRttMs
        {
            get => _latencyRttMs;
            set
            {
                if (SetProperty(ref _latencyRttMs, value))
                {
                    RaisePropertyChanged(nameof(SubtitleText));
                    RaisePropertyChanged(nameof(PingDotBrush));
                    RaisePropertyChanged(nameof(PingText));
                    RaisePropertyChanged(nameof(PingToolTip));
                    RaisePropertyChanged(nameof(WifiBar1Brush));
                    RaisePropertyChanged(nameof(WifiBar2Brush));
                    RaisePropertyChanged(nameof(WifiBar3Brush));
                }
            }
        }

        private double _networkRttExactMs = -1;
        private PeerLatencyInfo? _latencyInfo;

        /// <summary>UDP/SRTP yolundan ölçülen ağ gidiş-dönüşü (ondalıklı). Yoksa -1.</summary>
        public double NetworkRttExactMs
        {
            get => _networkRttExactMs;
            set
            {
                if (SetProperty(ref _networkRttExactMs, value))
                {
                    RaisePropertyChanged(nameof(PingText));
                    RaisePropertyChanged(nameof(PingToolTip));
                    RaisePropertyChanged(nameof(SubtitleText));
                }
            }
        }

        /// <summary>Son gecikme ölçümü (kullanıcı penceresinde gösterilir).</summary>
        public PeerLatencyInfo? LatencyInfo
        {
            get => _latencyInfo;
            set => SetProperty(ref _latencyInfo, value);
        }

        private string RttDisplay => _networkRttExactMs >= 0
            ? PeerLatencyInfo.FormatMs(_networkRttExactMs)
            : (_latencyRttMs > 999 ? ">999 ms" : $"{_latencyRttMs} ms");

        public int VolumePercent
        {
            get => _volumePercent;
            set
            {
                if (SetProperty(ref _volumePercent, value))
                {
                    RaisePropertyChanged(nameof(VolumeBadgeVisibility));
                    RaisePropertyChanged(nameof(VolumeBadgeText));
                }
            }
        }

        public bool ClarityBoost
        {
            get => _clarityBoost;
            set
            {
                if (SetProperty(ref _clarityBoost, value))
                {
                    RaisePropertyChanged(nameof(VolumeBadgeVisibility));
                    RaisePropertyChanged(nameof(VolumeBadgeText));
                }
            }
        }

        public int TreeDepth
        {
            get => _treeDepth;
            set
            {
                if (SetProperty(ref _treeDepth, value))
                {
                    RaisePropertyChanged(nameof(RowIndentMargin));
                    RaisePropertyChanged(nameof(TreeConnectorVisibility));
                    RaisePropertyChanged(nameof(SubtitleText));
                }
            }
        }

        public string RelayParentName
        {
            get => _relayParentName;
            set
            {
                if (SetProperty(ref _relayParentName, value))
                    RaisePropertyChanged(nameof(SubtitleText));
            }
        }

        public long LastSeen { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Computed UI Helpers
        public string DisplayName => _name;

        public ImageSource AvatarBitmap => _cachedAvatarBitmap ??= AvatarHelper.LoadAvatarSource(_avatar, 96);

        public string SubtitleText
        {
            get
            {
                if (_treeDepth >= 2 && !string.IsNullOrEmpty(_relayParentName))
                    return $"⚡ Röle ({_relayParentName}) • {(_latencyRttMs > 0 ? RttDisplay : "P2P")}";
                if (_isSelf)
                    return "";
                if (_pingStatus == PeerPingStatus.Connecting)
                    return "Bağlanıyor…";
                if (_pingStatus == PeerPingStatus.NoP2p)
                    return "P2P yok";
                if (_pingStatus == PeerPingStatus.Timeout)
                    return "Zaman aşımı";
                if (_latencyRttMs <= 0)
                    return "Ölçülüyor…";
                return $"Ping: {RttDisplay}";
            }
        }

        public string PingText
        {
            get
            {
                if (_isSelf) return "";
                return _pingStatus switch
                {
                    PeerPingStatus.Connecting => "Bağlanıyor…",
                    PeerPingStatus.NoP2p => "P2P yok",
                    PeerPingStatus.Timeout => "Zaman aşımı",
                    PeerPingStatus.Measuring => "Ölçülüyor…",
                    PeerPingStatus.Measured => RttDisplay,
                    _ => "Ölçülüyor…"
                };
            }
        }

        public object? PingToolTip
        {
            get
            {
                if (_isSelf)
                {
                    if (_selfPeerCount > 0)
                        return $"Odadakilere ortalama gecikme: {_selfAvgRtt} ms ({_selfPeerCount} kişi)";
                    return null;
                }

                return _pingStatus switch
                {
                    PeerPingStatus.Connecting => "P2P bağlantısı kuruluyor…",
                    PeerPingStatus.NoP2p => "Ses/yayın bağlantısı kurulamadı; ağ/güvenlik duvarı engelliyor olabilir",
                    PeerPingStatus.Timeout => "Zaman aşımı (yanıt alınamadı)",
                    PeerPingStatus.Measuring => "Ping ölçülüyor…",
                    PeerPingStatus.Measured => _jitterMs >= 0
                        ? $"Ağ gecikmesi (gidiş-dönüş): {RttDisplay} (Jitter: {_jitterMs} ms)"
                        : $"Ağ gecikmesi (gidiş-dönüş): {RttDisplay}",
                    _ => null
                };
            }
        }

        private static readonly SolidColorBrush BrushOrange = new(Windows.UI.Color.FromArgb(255, 245, 158, 11)); // #F59E0B

        public Brush PingTextBrush
        {
            get
            {
                if (_pingStatus == PeerPingStatus.NoP2p) return BrushOrange;
                return BrushSlate;
            }
        }

        // Önbellekli fırçalar (UI thread GC yükünü sıfırlar)
        private static readonly SolidColorBrush BrushGreen = new(Windows.UI.Color.FromArgb(255, 34, 197, 94));
        private static readonly SolidColorBrush BrushSlate = new(Windows.UI.Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush BrushYellow = new(Windows.UI.Color.FromArgb(255, 234, 179, 8));
        private static readonly SolidColorBrush BrushRed = new(Windows.UI.Color.FromArgb(255, 239, 68, 68));
        private static readonly SolidColorBrush BrushRedMuted = new(Windows.UI.Color.FromArgb(255, 255, 92, 110)); // #FF5C6E
        private static readonly SolidColorBrush BrushMutedIconGray = new(Windows.UI.Color.FromArgb(255, 203, 213, 225)); // #CBD5E1
        private static readonly SolidColorBrush BrushPurpleSoft = new(Windows.UI.Color.FromArgb(60, 139, 92, 246));
        private static readonly SolidColorBrush BrushPurpleBright = new(Windows.UI.Color.FromArgb(255, 168, 85, 247));
        private static readonly SolidColorBrush BrushWhiteSoft = new(Windows.UI.Color.FromArgb(80, 255, 255, 255));
        private static readonly SolidColorBrush BrushDimBar = new(Windows.UI.Color.FromArgb(50, 255, 255, 255));

        public const string PathMicNormal = "M12,1 A3,3 0 0,0 9,4 V12 A3,3 0 0,0 15,12 V4 A3,3 0 0,0 12,1 Z M19,10 V12 A7,7 0 0,1 5,12 V10 M12,19 V23 M8,23 H16";
        public const string PathMicSlashed = "M1,1 L23,23 M9,9 V12 A3,3 0 0,0 14.12,14.12 M15,9.34 V4 A3,3 0 0,0 9.06,3.4 M17,16.95 A7,7 0 0,1 5,12 V10 M19,10 V12 M12,19 V23 M8,23 H16";

        public const string PathHeadphoneNormal = "M3,18 V12 A9,9 0 0,1 21,12 V18 M21,19 A2,2 0 0,1 19,21 H18 A2,2 0 0,1 16,19 V16 A2,2 0 0,1 18,14 H21 Z M3,19 A2,2 0 0,0 5,21 H6 A2,2 0 0,0 8,19 V16 A2,2 0 0,0 6,14 H3 Z";
        public const string PathHeadphoneSlashed = "M1,1 L23,23 M3,18 V12 A9,9 0 0,1 6.4,5 M10.5,3.2 A9,9 0 0,1 21,12 V16 M21,19 A2,2 0 0,1 19,21 H18 A2,2 0 0,1 16,19 V16 M3,19 A2,2 0 0,0 5,21 H6 A2,2 0 0,0 8,19 V16 A2,2 0 0,0 6,14 H3 Z";

        public Brush MicStatusBrush => (_isMuted || _isLocalMuted) ? BrushRedMuted : BrushMutedIconGray;
        public Brush DeafenStatusBrush => _isDeafened ? BrushRedMuted : BrushMutedIconGray;
        public string MicStatusPath => (_isMuted || _isLocalMuted) ? PathMicSlashed : PathMicNormal;
        public string DeafenStatusPath => _isDeafened ? PathHeadphoneSlashed : PathHeadphoneNormal;

        public Brush PingDotBrush
        {
            get
            {
                if (_isSelf) return BrushGreen;
                if (_pingStatus == PeerPingStatus.NoP2p) return BrushOrange;
                if (_pingStatus == PeerPingStatus.Timeout || _latencyRttMs <= 0) return BrushSlate;
                if (_latencyRttMs < 80) return BrushGreen;
                if (_latencyRttMs <= 150) return BrushYellow;
                if (_latencyRttMs <= 300) return BrushOrange;
                return BrushRed;
            }
        }

        public Brush WifiBar1Brush
        {
            get
            {
                if (_isSelf) return BrushDimBar;
                if (_pingStatus == PeerPingStatus.Timeout) return BrushSlate; // Gri çubuk
                if (_pingStatus != PeerPingStatus.Measured) return BrushDimBar;
                int val = _latencyRttMs;
                if (val < 80) return BrushGreen;
                if (val <= 150) return BrushYellow;
                if (val <= 300) return BrushOrange;
                return BrushRed;
            }
        }

        public Brush WifiBar2Brush
        {
            get
            {
                if (_isSelf || _pingStatus != PeerPingStatus.Measured) return BrushDimBar;
                int val = _latencyRttMs;
                if (val < 80) return BrushGreen;
                if (val <= 150) return BrushYellow;
                return BrushDimBar;
            }
        }

        public Brush WifiBar3Brush
        {
            get
            {
                if (_isSelf || _pingStatus != PeerPingStatus.Measured) return BrushDimBar;
                int val = _latencyRttMs;
                if (val < 80) return BrushGreen;
                return BrushDimBar;
            }
        }

        public Brush SpeakingRingBrush => _isSpeaking ? BrushGreen : BrushPurpleSoft;

        public Brush SpeakingBorderBrush => _isSpeaking ? BrushGreen : BrushPurpleBright;

        public Brush BubbleRingBrush => _isSpeaking
            ? BrushGreen
            : (_isScreenSharing ? BrushPurpleBright : BrushWhiteSoft);

        public Visibility EffectiveMutedVisibility => (_isMuted || _isLocalMuted) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpenMicVisibility => (_isMuted || _isLocalMuted) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DeafenedVisibility => _isDeafened ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpenHeadphoneVisibility => _isDeafened ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ScreenShareBadgeVisibility => _isScreenSharing ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WatchStreamButtonVisibility => (!_isSelf && _isScreenSharing) ? Visibility.Visible : Visibility.Collapsed;

        public Visibility VolumeBadgeVisibility =>
            (!_isSelf && (_isLocalMuted || _volumePercent != 100 || _clarityBoost)) ? Visibility.Visible : Visibility.Collapsed;

        public string VolumeBadgeText
        {
            get
            {
                if (_isLocalMuted) return "SESSİZ";
                string prefix = _volumePercent > 100 ? "⚡" : "";
                string suffix = _clarityBoost ? " • HD" : "";
                return $"{prefix}%{_volumePercent}{suffix}";
            }
        }

        public Thickness RowIndentMargin => new Thickness(_treeDepth * 16, 3, 0, 3);
        public Visibility TreeConnectorVisibility => _treeDepth > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public class ChatFileAttachment : ObservableObject
    {
        private string _fileName = "";
        private string _fileSizeString = "";
        private string _fileExtension = "";
        private string _filePath = "";
        private string _fileDataUri = "";
        private bool _isDownloaded = false;

        public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
        public string FileSizeString { get => _fileSizeString; set => SetProperty(ref _fileSizeString, value); }
        public string FileExtension { get => _fileExtension; set => SetProperty(ref _fileExtension, value); }
        public string FilePath { get => _filePath; set => SetProperty(ref _filePath, value); }
        public string FileDataUri { get => _fileDataUri; set => SetProperty(ref _fileDataUri, value); }
        public bool IsDownloaded { get => _isDownloaded; set => SetProperty(ref _isDownloaded, value); }

        public bool IsImage => FileExtension is "png" or "jpg" or "jpeg" or "webp" or "gif";
        public bool IsPdf => FileExtension is "pdf";
        public string DisplaySizeAndExt => $"{FileSizeString} • {FileExtension.ToUpperInvariant()}";
    }

    public class ChatPollOption : ObservableObject
    {
        private string _text = "";
        private int _votes = 0;
        private double _votePercent = 0.0;
        private bool _isVotedByMe = false;

        public string Text { get => _text; set => SetProperty(ref _text, value); }
        public int Votes { get => _votes; set => SetProperty(ref _votes, value); }
        public double VotePercent { get => _votePercent; set => SetProperty(ref _votePercent, value); }
        public bool IsVotedByMe { get => _isVotedByMe; set => SetProperty(ref _isVotedByMe, value); }

        public string PercentString => $"%{Math.Round(_votePercent)}";
        public double Percent => VotePercent;
    }

    public class ChatPollData : ObservableObject
    {
        private string _pollId = Guid.NewGuid().ToString("N")[..8];
        private string _question = "";
        private string _expirationText = "2 saat içinde sona erecek";
        private int _totalVotes = 0;
        private int _userVotedIndex = -1;

        public string PollId { get => _pollId; set => SetProperty(ref _pollId, value); }
        public string Question { get => _question; set => SetProperty(ref _question, value); }
        public string ExpirationText { get => _expirationText; set => SetProperty(ref _expirationText, value); }
        public string DurationLabel { get => _expirationText; set => SetProperty(ref _expirationText, value); }
        public int TotalVotes { get => _totalVotes; set => SetProperty(ref _totalVotes, value); }
        public int UserVotedIndex { get => _userVotedIndex; set => SetProperty(ref _userVotedIndex, value); }

        public List<ChatPollOption> Options { get; set; } = new();

        public ChatPollOption Option1
        {
            get
            {
                if (Options.Count == 0) Options.Add(new ChatPollOption { Text = "Seçenek 1" });
                return Options[0];
            }
            set
            {
                if (Options.Count == 0) Options.Add(value);
                else Options[0] = value;
            }
        }

        public ChatPollOption Option2
        {
            get
            {
                while (Options.Count < 2) Options.Add(new ChatPollOption { Text = "Seçenek 2" });
                return Options[1];
            }
            set
            {
                while (Options.Count < 2) Options.Add(value);
                Options[1] = value;
            }
        }

        public void RecalculatePercents()
        {
            int total = 0;
            foreach (var opt in Options) total += opt.Votes;
            TotalVotes = total;
            foreach (var opt in Options)
            {
                opt.VotePercent = total > 0 ? (opt.Votes * 100.0 / total) : 0.0;
                opt.RaisePropertyChanged(nameof(opt.PercentString));
            }
            RaisePropertyChanged(nameof(TotalVotes));
        }
    }

    public class LinkPreviewData
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string SiteName { get; set; } = "";
        public string ThumbJpegBase64 { get; set; } = "";
        public bool IsYouTube { get; set; } = false;

        [JsonIgnore]
        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbJpegBase64);

        [JsonIgnore]
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        [JsonIgnore]
        public bool HasSiteName => !string.IsNullOrWhiteSpace(SiteName);

        private ImageSource? _cachedThumbSource;
        [JsonIgnore]
        public ImageSource? ThumbnailBitmap
        {
            get
            {
                if (_cachedThumbSource != null) return _cachedThumbSource;
                if (string.IsNullOrEmpty(ThumbJpegBase64)) return null;
                try
                {
                    byte[] bytes = Convert.FromBase64String(ThumbJpegBase64);
                    _cachedThumbSource = AvatarHelper.CreateBitmapFromBytes(bytes, 320);
                    return _cachedThumbSource;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    public class ChatMessage : ObservableObject
    {
        private string _text = "";
        private bool _isMe = false;
        private ChatFileAttachment? _fileAttachment;
        private ChatPollData? _pollData;
        private LinkPreviewData? _linkPreview;
        private string _messageType = "text"; // text | file | poll

        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string SenderName { get; set; } = "";
        public string SenderAvatar { get; set; } = "Assets/avatars/kaan.jpg";
        public string Text { get => _text; set => SetProperty(ref _text, value); }
        public string TimeString { get; set; } = DateTime.Now.ToString("HH:mm");
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public LinkPreviewData? LinkPreview
        {
            get => _linkPreview;
            set
            {
                if (SetProperty(ref _linkPreview, value))
                {
                    RaisePropertyChanged(nameof(HasLinkPreview));
                }
            }
        }

        [JsonIgnore]
        public bool HasLinkPreview => LinkPreview != null;

        public bool IsMe
        {
            get => _isMe;
            set
            {
                if (SetProperty(ref _isMe, value))
                {
                    RaisePropertyChanged(nameof(OtherUserVisibility));
                    RaisePropertyChanged(nameof(OwnMessageVisibility));
                    RaisePropertyChanged(nameof(BubbleHorizontalAlignment));
                }
            }
        }

        public string MessageType
        {
            get => _messageType;
            set
            {
                if (SetProperty(ref _messageType, value))
                {
                    RaisePropertyChanged(nameof(HasFile));
                    RaisePropertyChanged(nameof(HasPoll));
                    RaisePropertyChanged(nameof(HasText));
                }
            }
        }

        public ChatFileAttachment? FileAttachment
        {
            get => _fileAttachment;
            set
            {
                if (SetProperty(ref _fileAttachment, value))
                {
                    RaisePropertyChanged(nameof(HasFile));
                    RaisePropertyChanged(nameof(HasText));
                }
            }
        }

        public ChatPollData? PollData
        {
            get => _pollData;
            set
            {
                if (SetProperty(ref _pollData, value))
                {
                    RaisePropertyChanged(nameof(HasPoll));
                    RaisePropertyChanged(nameof(HasText));
                }
            }
        }

        [JsonIgnore]
        public bool HasFile => MessageType == "file" && FileAttachment != null;

        [JsonIgnore]
        public bool HasPoll => MessageType == "poll" && PollData != null;

        [JsonIgnore]
        public bool HasText => MessageType != "file" && MessageType != "poll" && !string.IsNullOrWhiteSpace(Text);

        [JsonIgnore]
        public Visibility OtherUserVisibility => IsMe ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public Visibility OwnMessageVisibility => IsMe ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public HorizontalAlignment BubbleHorizontalAlignment => IsMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        private ImageSource? _cachedSenderAvatar;

        // Liste kaydırıldıkça her yeniden oluşturmada base64 avatarı tekrar çözmemek için önbelleklenir
        [JsonIgnore]
        public ImageSource SenderAvatarBitmap => _cachedSenderAvatar ??= AvatarHelper.LoadAvatarSource(SenderAvatar, 64);

        public const int MaxWireTextLength = 4000;

        /// <summary>
        /// Ağa gönderilecek kopya: dosya ekinin yerel yolu ve içeriği ASLA gönderilmez
        /// (gönderenin kullanıcı adı / klasör yapısı sızmasın ve alıcı tarafta yol olarak kullanılamasın).
        /// </summary>
        public ChatFileAttachment? CreateWireAttachment()
        {
            if (FileAttachment == null) return null;
            return new ChatFileAttachment
            {
                FileName = FileAttachment.FileName,
                FileSizeString = FileAttachment.FileSizeString,
                FileExtension = FileAttachment.FileExtension,
                FilePath = "",
                FileDataUri = "",
                IsDownloaded = false
            };
        }

        /// <summary>
        /// Ağdan gelen mesajı güvenli hâle getirir: yerel dosya yolu/içerik alanları atılır, uzunluklar sınırlanır.
        /// Uzak bir katılımcı FilePath alanına keyfi bir yol (örn. \\sunucu\pay\x.exe) yazıp
        /// alıcının tıklamasıyla dosya çalıştıramasın.
        /// </summary>
        public void SanitizeIncoming()
        {
            IsMe = false;
            if (Text.Length > MaxWireTextLength) Text = Text[..MaxWireTextLength];
            if (SenderName.Length > 64) SenderName = SenderName[..64];
            if (Id.Length > 64) Id = Id[..64];
            if (MessageType is not ("text" or "file" or "poll")) MessageType = "text";

            if (FileAttachment != null)
            {
                FileAttachment.FilePath = "";
                FileAttachment.FileDataUri = "";
                FileAttachment.IsDownloaded = false;
                if (FileAttachment.FileName.Length > 160) FileAttachment.FileName = FileAttachment.FileName[..160];
                if (FileAttachment.FileExtension.Length > 16) FileAttachment.FileExtension = FileAttachment.FileExtension[..16];
                if (FileAttachment.FileSizeString.Length > 24) FileAttachment.FileSizeString = FileAttachment.FileSizeString[..24];
            }

            if (PollData != null)
            {
                if (PollData.Options.Count > 2) PollData.Options.RemoveRange(2, PollData.Options.Count - 2);
                if (PollData.Question.Length > 300) PollData.Question = PollData.Question[..300];
                foreach (var opt in PollData.Options)
                {
                    if (opt.Text.Length > 120) opt.Text = opt.Text[..120];
                    opt.Votes = Math.Clamp(opt.Votes, 0, 1000);
                    opt.IsVotedByMe = false;
                }
                PollData.UserVotedIndex = -1;
                PollData.RecalculatePercents();
            }

            if (LinkPreview != null)
            {
                // 1. URL geçerli http/https olmalıdır
                if (!Uri.TryCreate(LinkPreview.Url, UriKind.Absolute, out var previewUri) ||
                    (previewUri.Scheme != Uri.UriSchemeHttp && previewUri.Scheme != Uri.UriSchemeHttps))
                {
                    LinkPreview = null;
                }
                else
                {
                    // 2. Phishing önizleme enjeksiyonuna karşı: LinkPreview.Url sunucusu,
                    // mesaj metnindeki URL'lerden birinin alan adıyla eşleşmelidir.
                    var textHosts = RunaVoice_WinUI.Services.LinkPreviewHelper.ExtractHostsFromText(Text);
                    bool matched = false;
                    foreach (var h in textHosts)
                    {
                        if (string.Equals(previewUri.Host, h, StringComparison.OrdinalIgnoreCase) ||
                            previewUri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = true;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        LinkPreview = null;
                    }
                    else
                    {
                        // 3. Alan uzunluklarını sınırla
                        if (LinkPreview.Url.Length > 1024) LinkPreview.Url = LinkPreview.Url[..1024];
                        if (LinkPreview.Title.Length > 200) LinkPreview.Title = LinkPreview.Title[..200];
                        if (LinkPreview.Description.Length > 300) LinkPreview.Description = LinkPreview.Description[..300];
                        if (LinkPreview.SiteName.Length > 60) LinkPreview.SiteName = LinkPreview.SiteName[..60];

                        // 4. Küçük resim doğrulama: geçerli JPEG ve boyutu en fazla 640x640
                        if (!string.IsNullOrEmpty(LinkPreview.ThumbJpegBase64))
                        {
                            if (LinkPreview.ThumbJpegBase64.Length > 57344) // max ~56 KB base64
                            {
                                LinkPreview.ThumbJpegBase64 = "";
                            }
                            else
                            {
                                try
                                {
                                    byte[] thumbBytes = Convert.FromBase64String(LinkPreview.ThumbJpegBase64);
                                    if (!RunaVoice_WinUI.Services.ScreenCaptureService.TryReadJpegSize(thumbBytes, out int w, out int h) ||
                                        w <= 0 || h <= 0 || w > 640 || h > 640)
                                    {
                                        LinkPreview.ThumbJpegBase64 = "";
                                    }
                                }
                                catch
                                {
                                    LinkPreview.ThumbJpegBase64 = "";
                                }
                            }
                        }
                    }
                }
            }
        }

        [JsonIgnore]
        public Brush NameForeground => IsMe
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 192, 132, 252))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 168, 85, 247));

        [JsonIgnore]
        public Brush BubbleBackground => IsMe
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(230, 124, 58, 237))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(230, 22, 16, 42));

        [JsonIgnore]
        public Brush BubbleBorder => IsMe
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 168, 85, 247))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(51, 139, 92, 246));

        [JsonIgnore]
        public CornerRadius BubbleCornerRadius => IsMe
            ? new CornerRadius(16, 16, 4, 16)
            : new CornerRadius(16, 16, 16, 4);
    }

    public class RoomCheckResult
    {
        public bool Exists { get; set; } = false;
        public string PeerName { get; set; } = "";
        public string RoomTitle { get; set; } = "";
        public string RoomActivity { get; set; } = "Sohbet ediliyor";
        public bool IsPersistent { get; set; } = false;
        public int OnlineCount { get; set; } = 0;
        public string Reason { get; set; } = "";
        public int BrokerIndex { get; set; } = -1; // Odanın bulunduğu sinyalleşme sunucusu (katılırken aynı sunucu kullanılır)
    }

    public class AudioDeviceOption
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }

    public class ScreenCaptureSourceItem : ObservableObject
    {
        public string Id { get; set; } = "";

        private string _title = "";
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private string _subtitle = "";
        public string Subtitle
        {
            get => _subtitle;
            set => SetProperty(ref _subtitle, value);
        }

        public bool IsMonitor { get; set; } = true;
        public IntPtr Handle { get; set; } = IntPtr.Zero;
        public int BoundsX { get; set; }
        public int BoundsY { get; set; }
        public int BoundsWidth { get; set; } = 1920;
        public int BoundsHeight { get; set; } = 1080;

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        private ImageSource? _appIcon;
        public ImageSource? AppIcon
        {
            get => _appIcon;
            set
            {
                if (SetProperty(ref _appIcon, value))
                {
                    RaisePropertyChanged(nameof(HasAppIcon));
                }
            }
        }

        public bool HasAppIcon => AppIcon != null;
        public Stretch ThumbnailStretch => IsMonitor ? Stretch.Uniform : Stretch.UniformToFill;

        private bool _isMinimized = false;
        public bool IsMinimized
        {
            get => _isMinimized;
            set => SetProperty(ref _isMinimized, value);
        }

        private bool _isOtherDesktop = false;
        public bool IsOtherDesktop
        {
            get => _isOtherDesktop;
            set => SetProperty(ref _isOtherDesktop, value);
        }

        private bool _isLoadingThumbnail = true;
        public bool IsLoadingThumbnail
        {
            get => _isLoadingThumbnail;
            set => SetProperty(ref _isLoadingThumbnail, value);
        }
    }
}
