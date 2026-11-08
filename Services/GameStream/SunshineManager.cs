using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Resmî Sunshine sürecini ve yerel HTTPS API'sini yöneten servis (PDF Bölüm 5, 7, 11)
    /// </summary>
    public class SunshineManager : IStreamHost
    {
        public const int SunshinePort = 47990;
        public static string? DetectedExePath { get; private set; }
        public static string? DetectedVersion { get; private set; }
        public static bool IsServiceRunning { get; private set; }
        public static string? GpuVendorAndModel { get; private set; }
        public static List<string> SupportedCodecs { get; private set; } = new();

        private static DEVMODE? _originalDevMode;

        [SupportedOSPlatform("windows")]
        public Task<GameStreamCapabilities> DetectAsync(CancellationToken ct = default)
        {
            var caps = new GameStreamCapabilities();
            string? exePath = FindSunshineExe();
            DetectedExePath = exePath;

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exePath);
                    DetectedVersion = vi.ProductVersion ?? vi.FileVersion ?? "0.23.0";
                    caps.SunshineVersion = DetectedVersion;
                }
                catch
                {
                    DetectedVersion = "0.23.0";
                    caps.SunshineVersion = DetectedVersion;
                }
            }

            CheckSunshineService();
            DetectHardwareEncoder(caps);

            if (string.IsNullOrEmpty(exePath))
            {
                caps.CanHost = false;
                caps.HostReason = "Sunshine kurulu değil";
            }
            else if (!caps.CanHost && string.IsNullOrEmpty(caps.HostReason))
            {
                caps.HostReason = "Donanım kodlayıcı bulunamadı";
            }

            return Task.FromResult(caps);
        }

        [SupportedOSPlatform("windows")]
        private static string? FindSunshineExe()
        {
            string[] registryPaths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var regPath in registryPaths)
                {
                    try
                    {
                        using var key = root.OpenSubKey(regPath);
                        if (key == null) continue;
                        foreach (var subkeyName in key.GetSubKeyNames())
                        {
                            using var subkey = key.OpenSubKey(subkeyName);
                            var dispName = subkey?.GetValue("DisplayName") as string;
                            if (dispName != null && dispName.Contains("Sunshine", StringComparison.OrdinalIgnoreCase))
                            {
                                var installLoc = subkey?.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrEmpty(installLoc))
                                {
                                    string exe = Path.Combine(installLoc, "sunshine.exe");
                                    if (File.Exists(exe)) return exe;
                                }
                                var uninstallStr = subkey?.GetValue("UninstallString") as string;
                                if (!string.IsNullOrEmpty(uninstallStr))
                                {
                                    string? dir = Path.GetDirectoryName(uninstallStr.Trim('"', ' '));
                                    if (dir != null)
                                    {
                                        string exe = Path.Combine(dir, "sunshine.exe");
                                        if (File.Exists(exe)) return exe;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string standardPath = Path.Combine(pf, "Sunshine", "sunshine.exe");
            if (File.Exists(standardPath)) return standardPath;

            string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string standardX86 = Path.Combine(pfX86, "Sunshine", "sunshine.exe");
            if (File.Exists(standardX86)) return standardX86;

            return null;
        }

        private static void CheckSunshineService()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query SunshineService",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(1000);
                    IsServiceRunning = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                IsServiceRunning = false;
            }
        }

        [SupportedOSPlatform("windows")]
        private static void DetectHardwareEncoder(GameStreamCapabilities caps)
        {
            var codecs = new List<string>();
            bool hasNvenc = false;
            bool hasAmf = false;
            bool hasQsv = false;
            string gpuName = "";

            try
            {
                const string displayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using var key = Registry.LocalMachine.OpenSubKey(displayClassKey);
                if (key != null)
                {
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        if (subName.Length != 4 || !char.IsDigit(subName[0])) continue;
                        using var sub = key.OpenSubKey(subName);
                        if (sub == null) continue;

                        string driverDesc = sub.GetValue("DriverDesc") as string ?? "";
                        string provider = sub.GetValue("ProviderName") as string ?? "";
                        string combined = $"{provider} {driverDesc}".Trim();

                        if (string.IsNullOrWhiteSpace(combined) || combined.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (string.IsNullOrWhiteSpace(gpuName))
                        {
                            gpuName = driverDesc;
                        }

                        if (combined.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || combined.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || combined.Contains("RTX", StringComparison.OrdinalIgnoreCase) || combined.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                        {
                            hasNvenc = true;
                            if (combined.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) || combined.Contains("RTX 50", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!codecs.Contains("AV1")) codecs.Add("AV1");
                            }
                        }
                        else if (combined.Contains("AMD", StringComparison.OrdinalIgnoreCase) || combined.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || combined.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
                        {
                            hasAmf = true;
                            if (combined.Contains("RX 7", StringComparison.OrdinalIgnoreCase) || combined.Contains("RX 8", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!codecs.Contains("AV1")) codecs.Add("AV1");
                            }
                        }
                        else if (combined.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                        {
                            hasQsv = true;
                            if (combined.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!codecs.Contains("AV1")) codecs.Add("AV1");
                            }
                        }
                    }
                }
            }
            catch { }

            if (hasNvenc || hasAmf || hasQsv)
            {
                if (!codecs.Contains("HEVC")) codecs.Add("HEVC");
                if (!codecs.Contains("H264")) codecs.Add("H264");
                caps.CanHost = true;
                caps.HostReason = null;
            }
            else
            {
                caps.CanHost = false;
                caps.HostReason = "Donanım kodlayıcı bulunamadı (NVIDIA/AMD/Intel GPU gereklidir)";
            }

            GpuVendorAndModel = gpuName;
            SupportedCodecs = codecs;
            caps.Codecs = new List<string>(codecs);
        }

        // =====================================================================
        // SUNSHINE REST API İŞLEMLERİ (PDF Bölüm 5.3 & 5.4)
        // =====================================================================

        private HttpClient CreateApiClient(string? credsProtected = null)
        {
            var handler = new HttpClientHandler
            {
                // Yalnızca localhost için sertifika doğrulaması atlanır; başka adrese asla! (PDF 5.3)
                ServerCertificateCustomValidationCallback = (req, cert, chain, err) =>
                {
                    return req.RequestUri != null && (req.RequestUri.Host == "localhost" || req.RequestUri.Host == "127.0.0.1");
                }
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://localhost:{SunshinePort}"),
                Timeout = TimeSpan.FromSeconds(5)
            };

            if (!string.IsNullOrEmpty(credsProtected))
            {
                string creds = DpapiHelper.UnprotectString(credsProtected);
                if (!string.IsNullOrEmpty(creds))
                {
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", b64);
                }
            }

            return client;
        }

        public async Task<List<GameStreamAppInfo>> GetAppsAsync(CancellationToken ct = default)
        {
            var list = new List<GameStreamAppInfo>();
            try
            {
                using var client = CreateApiClient();
                var resp = await client.GetAsync("/api/apps", ct);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("apps", out var appsEl) && appsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var app in appsEl.EnumerateArray())
                        {
                            string name = app.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            string output = app.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                            string cmd = app.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                list.Add(new GameStreamAppInfo
                                {
                                    Id = name,
                                    Name = name,
                                    ExePath = !string.IsNullOrWhiteSpace(cmd) ? cmd : output
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            // Sunshine boş dönerse veya ulaşılamazsa temel varsayılan ekle
            if (list.Count == 0)
            {
                list.Add(new GameStreamAppInfo { Id = "steam", Name = "Steam Big Picture", ExePath = "steam.exe" });
            }

            return list;
        }

        public async Task<bool> AddAppAsync(string name, string exePath, string workingDir, List<string>? extraProcesses, CancellationToken ct = default)
        {
            try
            {
                using var client = CreateApiClient();
                var payload = new
                {
                    name = name,
                    cmd = exePath,
                    output = "",
                    detached = new string[0],
                    image_path = "",
                    working_dir = workingDir ?? ""
                };

                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync("/api/apps", content, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> PairPinAsync(string pin, string clientName, CancellationToken ct = default)
        {
            try
            {
                using var client = CreateApiClient();
                var payload = new
                {
                    pin = pin,
                    name = clientName
                };

                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync("/api/pin", content, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> UnpairClientAsync(string clientUuidOrName, CancellationToken ct = default)
        {
            try
            {
                using var client = CreateApiClient();
                // Önce istemci listesini çek
                var resp = await client.GetAsync("/api/clients/list", ct);
                if (!resp.IsSuccessStatusCode) return false;

                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                string? targetUuid = null;

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var clientEl in doc.RootElement.EnumerateArray())
                    {
                        string uuid = clientEl.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
                        string name = clientEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                        if (uuid.Equals(clientUuidOrName, StringComparison.OrdinalIgnoreCase) || name.Contains(clientUuidOrName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetUuid = uuid;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(targetUuid))
                {
                    var unpairPayload = new { uuid = targetUuid };
                    using var content = new StringContent(JsonSerializer.Serialize(unpairPayload), Encoding.UTF8, "application/json");
                    var unpairResp = await client.PostAsync("/api/clients/unpair", content, ct);
                    return unpairResp.IsSuccessStatusCode;
                }
            }
            catch { }

            return false;
        }

        public async Task CleanupStaleClientsAsync(List<string> trustedPlayers, CancellationToken ct = default)
        {
            try
            {
                using var client = CreateApiClient();
                var resp = await client.GetAsync("/api/clients/list", ct);
                if (!resp.IsSuccessStatusCode) return;

                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var clientEl in doc.RootElement.EnumerateArray())
                    {
                        string uuid = clientEl.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
                        string name = clientEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                        // RUNA kaynaklı geçici istemciler ("RUNA-") ve güvenilenler listesinde olmayanlar (PDF Bölüm 11)
                        if (name.StartsWith("RUNA-", StringComparison.OrdinalIgnoreCase))
                        {
                            string rawName = name.Substring(5).ToLowerInvariant();
                            if (!trustedPlayers.Contains(rawName))
                            {
                                var unpairPayload = new { uuid };
                                using var content = new StringContent(JsonSerializer.Serialize(unpairPayload), Encoding.UTF8, "application/json");
                                await client.PostAsync("/api/clients/unpair", content, ct);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public Task<bool> EnsureRunningAsync(CancellationToken ct = default)
        {
            CheckSunshineService();
            if (IsServiceRunning) return Task.FromResult(true);

            // Servisi başlatmayı dene
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "start SunshineService",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                var p = Process.Start(psi);
                p?.WaitForExit(3000);
                CheckSunshineService();
                return Task.FromResult(IsServiceRunning);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public async Task<bool> IsSessionActiveAsync(CancellationToken ct = default)
        {
            try
            {
                using var client = CreateApiClient();
                var resp = await client.GetAsync("/api/status", ct);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    return json.Contains("\"streaming\":true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            return false;
        }

        // =====================================================================
        // ÇÖZÜNÜRLÜK YÖNETİMİ (PDF Bölüm 5.4: ChangeDisplaySettingsEx)
        // =====================================================================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
        }

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int CDS_UPDATEREGISTRY = 0x01;
        private const int CDS_TEST = 0x02;
        private const int DISP_CHANGE_SUCCESSFUL = 0;

        [DllImport("user32.dll")]
        private static extern int EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll")]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        public Task<bool> SetResolutionAsync(int width, int height, int fps, CancellationToken ct = default)
        {
            try
            {
                var currentMode = new DEVMODE();
                currentMode.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));

                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref currentMode) != 0)
                {
                    if (_originalDevMode == null)
                    {
                        _originalDevMode = currentMode;
                    }

                    if (currentMode.dmPelsWidth == width && currentMode.dmPelsHeight == height)
                    {
                        return Task.FromResult(true); // Zaten istenen çözünürlükte
                    }

                    var newMode = currentMode;
                    newMode.dmPelsWidth = (uint)width;
                    newMode.dmPelsHeight = (uint)height;
                    newMode.dmFields = 0x00080000 | 0x00100000; // DM_PELSWIDTH | DM_PELSHEIGHT

                    int result = ChangeDisplaySettingsEx(null, ref newMode, IntPtr.Zero, 0, IntPtr.Zero);
                    return Task.FromResult(result == DISP_CHANGE_SUCCESSFUL);
                }
            }
            catch { }

            return Task.FromResult(false);
        }

        public Task<bool> RestoreResolutionAsync(CancellationToken ct = default)
        {
            try
            {
                if (_originalDevMode.HasValue)
                {
                    var mode = _originalDevMode.Value;
                    ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, 0, IntPtr.Zero);
                    _originalDevMode = null;
                    return Task.FromResult(true);
                }
                else
                {
                    // Varsayılan ayarlara sıfırla
                    ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                    return Task.FromResult(true);
                }
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }
}
