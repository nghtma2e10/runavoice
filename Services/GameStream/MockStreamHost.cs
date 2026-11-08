using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Testler için sahte Sunshine sunucusu (PDF Bölüm 3, 14)
    /// PIN'i kabul eder, sahte oyun listesi döner ve tünelin host ucunda TCP/UDP yankı dinleyicisi çalıştırabilir.
    /// </summary>
    public class MockStreamHost : IStreamHost
    {
        private readonly List<GameStreamAppInfo> _apps = new()
        {
            new GameStreamAppInfo { Id = "bf6", Name = "Battlefield 6", ExePath = @"C:\Games\BF6\bf6.exe" },
            new GameStreamAppInfo { Id = "testgame", Name = "Test Oyunu (Mock)", ExePath = @"C:\Games\Test\test.exe" }
        };

        private readonly List<string> _pairedPins = new();
        private readonly List<TcpListener> _tcpEchoListeners = new();
        private readonly List<UdpClient> _udpEchoClients = new();
        private CancellationTokenSource? _echoCts;

        public bool MockCanHost { get; set; } = true;
        public bool MockCanPlay { get; set; } = true;
        public bool SimulateActiveSession { get; set; } = false;

        public Task<GameStreamCapabilities> DetectAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new GameStreamCapabilities
            {
                CanHost = MockCanHost,
                CanPlay = MockCanPlay,
                HostReason = MockCanHost ? null : "Host olunamıyor (Mock)",
                PlayReason = MockCanPlay ? null : "Oynanamıyor (Mock)",
                SunshineVersion = "0.23.1-mock",
                MoonlightVersion = "6.0.1-mock",
                Codecs = new List<string> { "AV1", "HEVC", "H264" }
            });
        }

        public Task<List<GameStreamAppInfo>> GetAppsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new List<GameStreamAppInfo>(_apps));
        }

        public Task<bool> AddAppAsync(string name, string exePath, string workingDir, List<string>? extraProcesses, CancellationToken ct = default)
        {
            _apps.Add(new GameStreamAppInfo
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = name,
                ExePath = exePath,
                WorkingDir = workingDir,
                ExtraAllowedProcesses = extraProcesses ?? new()
            });
            return Task.FromResult(true);
        }

        public Task<bool> PairPinAsync(string pin, string clientName, CancellationToken ct = default)
        {
            _pairedPins.Add(pin);
            return Task.FromResult(true);
        }

        public Task<bool> UnpairClientAsync(string clientUuidOrName, CancellationToken ct = default)
        {
            _pairedPins.Clear();
            return Task.FromResult(true);
        }

        public Task<bool> EnsureRunningAsync(CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> IsSessionActiveAsync(CancellationToken ct = default)
        {
            return Task.FromResult(SimulateActiveSession);
        }

        public Task<bool> SetResolutionAsync(int width, int height, int fps, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> RestoreResolutionAsync(CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task CleanupStaleClientsAsync(List<string> trustedPlayers, CancellationToken ct = default)
        {
            _pairedPins.Clear();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Faz 4 tünel testi için 127.0.0.1 üzerinde Sunshine portlarında (47984, 47989, 48010 tcp; 47998-48000 udp) yankı sunucuları açar.
        /// </summary>
        public void StartEchoServers()
        {
            StopEchoServers();
            _echoCts = new CancellationTokenSource();
            int[] tcpPorts = { 47984, 47989, 48010 };
            int[] udpPorts = { 47998, 47999, 48000 };

            foreach (var port in tcpPorts)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    _tcpEchoListeners.Add(listener);
                    _ = RunTcpEchoLoopAsync(listener, _echoCts.Token);
                }
                catch
                {
                    // Port meşgulse testte devam et
                }
            }

            foreach (var port in udpPorts)
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                    _udpEchoClients.Add(udp);
                    _ = RunUdpEchoLoopAsync(udp, _echoCts.Token);
                }
                catch
                {
                    // Port meşgulse testte devam et
                }
            }
        }

        public void StopEchoServers()
        {
            _echoCts?.Cancel();
            _echoCts?.Dispose();
            _echoCts = null;

            foreach (var tcp in _tcpEchoListeners)
            {
                try { tcp.Stop(); } catch { }
            }
            _tcpEchoListeners.Clear();

            foreach (var udp in _udpEchoClients)
            {
                try { udp.Close(); udp.Dispose(); } catch { }
            }
            _udpEchoClients.Clear();
        }

        private static async Task RunTcpEchoLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            byte[] buffer = new byte[8192];
                            while (!ct.IsCancellationRequested)
                            {
                                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                                if (read <= 0) break;
                                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                            }
                        }
                    }, ct);
                }
                catch
                {
                    break;
                }
            }
        }

        private static async Task RunUdpEchoLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(ct);
                    await udp.SendAsync(result.Buffer, result.RemoteEndPoint, ct);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
