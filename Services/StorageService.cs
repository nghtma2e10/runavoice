using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services
{
    public class StorageService
    {
        private readonly string _appDataFolder;
        private readonly string _avatarsFolder;
        private readonly string _soundsFolder;
        private readonly string _settingsFile;
        private readonly string _roomsFile;

        public AppSettings Settings { get; private set; } = new();
        public UserProfile CurrentUser { get; private set; } = new();
        public bool IsTestProfile { get; private set; } = false;
        public string AppDataFolder => _appDataFolder;

        private static readonly string[] CoverPool =
        {
            "Assets/covers/cover_moon.png",
            "Assets/covers/cover_nebula.png",
            "Assets/covers/cover_music.png",
            "Assets/covers/cover_code.png"
        };

        public StorageService()
        {
            string profileSuffix = "";
            try
            {
                var args = Environment.GetCommandLineArgs();
                foreach (var arg in args)
                {
                    if (arg.Equals("--profile-2", StringComparison.OrdinalIgnoreCase))
                    {
                        profileSuffix = "_Profile2";
                        IsTestProfile = true;
                    }
                    else if (arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        profileSuffix = "_" + Path.GetFileNameWithoutExtension(arg["--profile=".Length..]);
                        IsTestProfile = true;
                    }
                    else if (arg.Equals("--engine=rust", StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.RealtimeEngine = "rust";
                    }
                    else if (arg.Equals("--engine=legacy", StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.RealtimeEngine = "legacy";
                    }
                }
            }
            catch { }

            _appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RunaVoice" + profileSuffix);

            _avatarsFolder = Path.Combine(_appDataFolder, "avatars");
            _soundsFolder = Path.Combine(_appDataFolder, "soundpad");
            _settingsFile = Path.Combine(_appDataFolder, "runa_settings_v3.json");
            _roomsFile = Path.Combine(_appDataFolder, "runa_persistent_rooms.json");

            try
            {
                Directory.CreateDirectory(_appDataFolder);
                Directory.CreateDirectory(_avatarsFolder);
                Directory.CreateDirectory(_soundsFolder);
                LoadSettings();
            }
            catch { }
        }

        public void LoadSettings()
        {
            bool hasSavedFile = false;
            try
            {
                if (File.Exists(_settingsFile))
                {
                    hasSavedFile = true;
                    var json = File.ReadAllText(_settingsFile);
                    Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    // Eski v2 dosyası varsa cihaz ve ses tercihlerini taşı, ancak eski yapay varsayılanları temizle
                    string legacyV2 = Path.Combine(_appDataFolder, "runa_settings_v2.json");
                    if (File.Exists(legacyV2))
                    {
                        var legacy = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacyV2));
                        if (legacy != null)
                        {
                            Settings = legacy;
                            if (Settings.UserName == "Kaan")
                                Settings.UserName = Environment.UserName;
                            if (Settings.AvatarPath == "Assets/avatars/kaan.jpg" || Settings.AvatarPath == "Assets/avatars/arda.jpg")
                            {
                                Settings.AvatarPath = "Assets/avatars/default-avatar.png";
                                Settings.AvatarDataUri = "";
                            }
                            if (Settings.StreamSharpnessPercent == 20)
                                Settings.StreamSharpnessPercent = 0;
                        }
                    }
                }
            }
            catch
            {
                Settings = new AppSettings();
            }

            // Komut satırı (--engine=rust|legacy) yalnızca bu oturum için kayıtlı tercihi geçersiz kılar.
            // (Önceden burada kayıtlı "rust" tercihi her açılışta "legacy"ye çevrilip kaydediliyordu;
            // Ayarlar'dan Rust seçilse bile yeniden başlatınca C#'a dönüyordu. Başlatma hatası giderildiği için kaldırıldı.)
            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg.Equals("--engine=rust", StringComparison.OrdinalIgnoreCase))
                        Settings.RealtimeEngine = "rust";
                    else if (arg.Equals("--engine=legacy", StringComparison.OrdinalIgnoreCase))
                        Settings.RealtimeEngine = "legacy";
                }
            }
            catch { }
            if (Settings.RealtimeEngine is not ("rust" or "legacy")) Settings.RealtimeEngine = "legacy";

            string defaultName = IsTestProfile
                ? "TestHesap"
                : (string.IsNullOrWhiteSpace(Environment.UserName) ? "Kullanıcı" : Environment.UserName);

            if (!hasSavedFile && IsTestProfile)
            {
                Settings.UserName = "TestHesap";
                Settings.AvatarPath = "Assets/avatars/default-avatar.png";
                Settings.AvatarDataUri = "";
            }

            if (string.IsNullOrWhiteSpace(Settings.AvatarPath))
            {
                Settings.AvatarPath = "Assets/avatars/default-avatar.png";
            }

            if (string.IsNullOrWhiteSpace(Settings.AvatarDataUri))
            {
                Settings.AvatarDataUri = BuildAvatarDataUri(ResolveFullPath(Settings.AvatarPath));
            }

            CurrentUser.Name = string.IsNullOrWhiteSpace(Settings.UserName) ? defaultName : Settings.UserName;
            CurrentUser.AvatarPath = Settings.AvatarPath;
            CurrentUser.AvatarDataUri = Settings.AvatarDataUri;

            if (!hasSavedFile)
            {
                SaveSettings();
            }
        }

        public void SaveSettings()
        {
            try
            {
                Settings.UserName = CurrentUser.Name;
                Settings.AvatarPath = CurrentUser.AvatarPath;
                Settings.AvatarDataUri = CurrentUser.AvatarDataUri;

                var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFile, json);
            }
            catch { }
        }

        public string ImportAvatarFile(string sourceFilePath)
        {
            try
            {
                if (!File.Exists(sourceFilePath)) return CurrentUser.AvatarPath;

                string ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".png";

                string destFile = Path.Combine(_avatarsFolder, $"avatar_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}");
                File.Copy(sourceFilePath, destFile, true);

                CurrentUser.AvatarPath = destFile;
                CurrentUser.AvatarDataUri = BuildAvatarDataUri(destFile);
                SaveSettings();
                return destFile;
            }
            catch
            {
                return CurrentUser.AvatarPath;
            }
        }

        public void SetPresetAvatar(string relativeAssetPath)
        {
            CurrentUser.AvatarPath = relativeAssetPath;
            CurrentUser.AvatarDataUri = BuildAvatarDataUri(ResolveFullPath(relativeAssetPath));
            SaveSettings();
        }

        public CustomSoundItem? ImportSoundpadClip(string sourceFilePath)
        {
            try
            {
                if (!File.Exists(sourceFilePath)) return null;
                string ext = Path.GetExtension(sourceFilePath);
                string baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
                string destFile = Path.Combine(_soundsFolder, $"{Guid.NewGuid():N}{ext}");
                File.Copy(sourceFilePath, destFile, true);

                var item = new CustomSoundItem
                {
                    Title = baseName.Length > 22 ? baseName[..22] : baseName,
                    Emoji = "🔊",
                    FilePath = destFile,
                    IsBuiltIn = false
                };

                Settings.CustomSounds.Add(item);
                SaveSettings();
                return item;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveFullPath(string pathSpec)
        {
            if (string.IsNullOrWhiteSpace(pathSpec)) return "";
            if (Path.IsPathRooted(pathSpec)) return pathSpec;
            return Path.Combine(AppContext.BaseDirectory, pathSpec.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string BuildAvatarDataUri(string fullFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullFilePath) || !File.Exists(fullFilePath)) return "";

                using var srcImg = Image.FromFile(fullFilePath);
                const int size = 72;
                using var thumb = new Bitmap(size, size, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    // Center-crop square
                    int minSide = Math.Min(srcImg.Width, srcImg.Height);
                    int srcX = (srcImg.Width - minSide) / 2;
                    int srcY = (srcImg.Height - minSide) / 2;
                    g.DrawImage(srcImg, new Rectangle(0, 0, size, size), new Rectangle(srcX, srcY, minSide, minSide), GraphicsUnit.Pixel);
                }

                using var ms = new MemoryStream();
                var jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegCodec != null)
                {
                    var encParams = new EncoderParameters(1);
                    encParams.Param[0] = new EncoderParameter(Encoder.Quality, 76L);
                    thumb.Save(ms, jpegCodec, encParams);
                }
                else
                {
                    thumb.Save(ms, ImageFormat.Jpeg);
                }

                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                return "";
            }
        }

        public PeerAudioConfig GetPeerAudioConfig(string peerName)
        {
            string key = (peerName ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key)) return new PeerAudioConfig();

            if (!Settings.PeerAudioConfigs.TryGetValue(key, out var cfg))
            {
                cfg = new PeerAudioConfig();
                Settings.PeerAudioConfigs[key] = cfg;
            }
            return cfg;
        }

        public void SavePeerAudioConfig(string peerName, PeerAudioConfig config)
        {
            string key = (peerName ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key)) return;
            Settings.PeerAudioConfigs[key] = config;
            SaveSettings();
        }

        public List<PersistentRoom> GetPersistentRooms()
        {
            try
            {
                if (File.Exists(_roomsFile))
                {
                    var json = File.ReadAllText(_roomsFile);
                    var rooms = JsonSerializer.Deserialize<List<PersistentRoom>>(json) ?? new List<PersistentRoom>();
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        if (string.IsNullOrWhiteSpace(rooms[i].CoverImage) || rooms[i].CoverImage.Contains("banner.jpg"))
                        {
                            rooms[i].CoverImage = CoverPool[Math.Abs(rooms[i].Code.GetHashCode()) % CoverPool.Length];
                        }
                    }
                    return rooms;
                }
            }
            catch { }
            return new List<PersistentRoom>();
        }

        public void SavePersistentRoom(string code, string title, bool isOwner, string? coverImage = null)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var cleanCode = NormalizeRoomCode(code);
            var list = GetPersistentRooms();

            var existing = list.FirstOrDefault(r => r.Code.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string chosenCover = !string.IsNullOrWhiteSpace(coverImage)
                ? coverImage
                : CoverPool[Math.Abs(cleanCode.GetHashCode()) % CoverPool.Length];

            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(title) && !title.Equals(cleanCode, StringComparison.OrdinalIgnoreCase))
                    existing.Title = title.Trim();
                existing.IsOwner = isOwner || existing.IsOwner;
                if (!string.IsNullOrWhiteSpace(coverImage))
                    existing.CoverImage = coverImage;
                existing.LastJoinedAt = now;
            }
            else
            {
                list.Insert(0, new PersistentRoom
                {
                    Code = cleanCode,
                    Title = string.IsNullOrWhiteSpace(title) ? cleanCode : title.Trim(),
                    CoverImage = chosenCover,
                    IsOwner = isOwner,
                    CreatedAt = now,
                    LastJoinedAt = now
                });
            }

            try
            {
                var json = JsonSerializer.Serialize(list.Take(24).ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_roomsFile, json);
            }
            catch { }
        }

        public void RemovePersistentRoom(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var cleanCode = NormalizeRoomCode(code);
            var list = GetPersistentRooms();
            list.RemoveAll(r => r.Code.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

            try
            {
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_roomsFile, json);
            }
            catch { }
        }

        public void ClearAllPersistentRooms()
        {
            try
            {
                if (File.Exists(_roomsFile)) File.Delete(_roomsFile);
            }
            catch { }
        }

        public bool IsRoomPersistent(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            var cleanCode = NormalizeRoomCode(code);
            return GetPersistentRooms().Any(r => r.Code.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
        }

        public static string NormalizeRoomCode(string rawCode)
        {
            if (string.IsNullOrWhiteSpace(rawCode)) return "";
            string trimmed = rawCode.Trim();
            int hashIdx = trimmed.LastIndexOf('#');
            if (hashIdx >= 0 && hashIdx + 1 < trimmed.Length)
                trimmed = trimmed[(hashIdx + 1)..];
            int slashIdx = trimmed.LastIndexOf('/');
            if (slashIdx >= 0 && slashIdx + 1 < trimmed.Length)
                trimmed = trimmed[(slashIdx + 1)..];

            string clean = MqttKeyHelper.SanitizeTopicSegment(trimmed).ToUpperInvariant();
            if (clean == "PEER") return "";
            if (clean.Length == 4 && clean.All(char.IsDigit))
                return $"RUNA-{clean}";
            return clean;
        }
    }
}
