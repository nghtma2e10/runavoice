using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Resmî Moonlight istemci sürecini yöneten servis (PDF Bölüm 5.5, 7, 11)
    /// </summary>
    public class MoonlightManager : IStreamClient
    {
        public static string? DetectedExePath { get; private set; }
        public static string? DetectedVersion { get; private set; }

        private Process? _moonlightProcess;
        public bool IsRunning => _moonlightProcess != null && !_moonlightProcess.HasExited;

        public event Action<int, string>? ProcessExited;

        [SupportedOSPlatform("windows")]
        public Task<GameStreamCapabilities> DetectAsync(CancellationToken ct = default)
        {
            var caps = new GameStreamCapabilities();
            string? exePath = FindMoonlightExe();
            DetectedExePath = exePath;

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                caps.CanPlay = true;
                caps.PlayReason = null;
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exePath);
                    DetectedVersion = vi.ProductVersion ?? vi.FileVersion ?? "6.0.0";
                    caps.MoonlightVersion = DetectedVersion;
                }
                catch
                {
                    DetectedVersion = "6.0.0";
                    caps.MoonlightVersion = DetectedVersion;
                }
                caps.Codecs = new() { "AV1", "HEVC", "H264" };
            }
            else
            {
                caps.CanPlay = false;
                caps.PlayReason = "Moonlight kurulu değil";
            }

            return Task.FromResult(caps);
        }

        [SupportedOSPlatform("windows")]
        private static string? FindMoonlightExe()
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
                            if (dispName != null && dispName.Contains("Moonlight", StringComparison.OrdinalIgnoreCase))
                            {
                                var installLoc = subkey?.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrEmpty(installLoc))
                                {
                                    string exe = Path.Combine(installLoc, "Moonlight.exe");
                                    if (File.Exists(exe)) return exe;
                                }
                                var uninstallStr = subkey?.GetValue("UninstallString") as string;
                                if (!string.IsNullOrEmpty(uninstallStr))
                                {
                                    string? dir = Path.GetDirectoryName(uninstallStr.Trim('"', ' '));
                                    if (dir != null)
                                    {
                                        string exe = Path.Combine(dir, "Moonlight.exe");
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
            string standardPath = Path.Combine(pf, "Moonlight Game Streaming", "Moonlight.exe");
            if (File.Exists(standardPath)) return standardPath;

            string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string standardX86 = Path.Combine(pfX86, "Moonlight Game Streaming", "Moonlight.exe");
            if (File.Exists(standardX86)) return standardX86;

            return null;
        }

        // =====================================================================
        // MOONLIGHT KOMUT SATIRI İŞLEMLERİ (PDF Bölüm 5.5)
        // =====================================================================

        public async Task<bool> PairAsync(string hostIp, string pin, CancellationToken ct = default)
        {
            string? exe = DetectedExePath ?? FindMoonlightExe();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"pair {hostIp} --pin {pin}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var p = Process.Start(psi);
                if (p == null) return false;

                await p.WaitForExitAsync(ct);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public Task<bool> StartStreamAsync(string hostIp, string appName, int width, int height, int fps, int bitrateKbps, string codec, bool fullscreen, CancellationToken ct = default)
        {
            string? exe = DetectedExePath ?? FindMoonlightExe();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return Task.FromResult(false);

            try
            {
                StopExistingProcess();

                string displayMode = fullscreen ? "fullscreen" : "windowed";
                string args = $"stream {hostIp} \"{appName}\" --resolution {width}x{height} --fps {fps} --bitrate {bitrateKbps} --video-codec {codec} --display-mode {displayMode}";

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                _moonlightProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _moonlightProcess.Exited += (s, e) =>
                {
                    int exitCode = _moonlightProcess?.ExitCode ?? 0;
                    _moonlightProcess = null;
                    ProcessExited?.Invoke(exitCode, "user");
                };

                bool started = _moonlightProcess.Start();
                return Task.FromResult(started);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public async Task<bool> StopStreamAsync(string hostIp, CancellationToken ct = default)
        {
            string? exe = DetectedExePath ?? FindMoonlightExe();
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = $"quit {hostIp}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync(ct);
                }
                catch { }
            }

            StopExistingProcess();
            return true;
        }

        private void StopExistingProcess()
        {
            if (_moonlightProcess != null)
            {
                try
                {
                    if (!_moonlightProcess.HasExited)
                    {
                        _moonlightProcess.Kill();
                        _moonlightProcess.WaitForExit(1000);
                    }
                }
                catch { }
                finally
                {
                    _moonlightProcess = null;
                }
            }
        }
    }
}
