using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Testler için sahte Moonlight istemcisi (PDF Bölüm 3, 14)
    /// </summary>
    public class MockStreamClient : IStreamClient
    {
        private CancellationTokenSource? _streamCts;
        public bool MockCanPlay { get; set; } = true;
        public bool IsRunning { get; private set; }

        public event Action<int, string>? ProcessExited;

        public Task<GameStreamCapabilities> DetectAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new GameStreamCapabilities
            {
                CanHost = false,
                CanPlay = MockCanPlay,
                PlayReason = MockCanPlay ? null : "Moonlight bulunamadı (Mock)",
                MoonlightVersion = "6.0.1-mock",
                Codecs = new() { "AV1", "HEVC", "H264" }
            });
        }

        public Task<bool> PairAsync(string hostIp, string pin, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> StartStreamAsync(string hostIp, string appName, int width, int height, int fps, int bitrateKbps, string codec, bool fullscreen, CancellationToken ct = default)
        {
            IsRunning = true;
            _streamCts?.Cancel();
            _streamCts = new CancellationTokenSource();

            // Tünel testi için 127.0.0.77 UDP portlarına periyodik paket gönderen test döngüsü başlatabilir
            _ = Task.Run(async () =>
            {
                try
                {
                    using var udp = new UdpClient();
                    var endPoint = new IPEndPoint(IPAddress.Parse("127.0.0.77"), 47998);
                    byte[] dummyPacket = new byte[1000];
                    Random.Shared.NextBytes(dummyPacket);

                    while (!_streamCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(16, _streamCts.Token); // ~60 fps
                        try
                        {
                            await udp.SendAsync(dummyPacket, dummyPacket.Length, endPoint);
                        }
                        catch
                        {
                            // Tünel henüz hazır olmayabilir
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }, _streamCts.Token);

            return Task.FromResult(true);
        }

        public Task<bool> StopStreamAsync(string hostIp, CancellationToken ct = default)
        {
            if (IsRunning)
            {
                IsRunning = false;
                _streamCts?.Cancel();
                _streamCts?.Dispose();
                _streamCts = null;
                ProcessExited?.Invoke(0, "user");
            }
            return Task.FromResult(true);
        }

        /// <summary>
        /// Test senaryoları için Moonlight'ın oyuncu tarafından kapatılmasını simüle eder
        /// </summary>
        public void SimulatePlayerExit(int exitCode = 0, string reason = "user")
        {
            if (IsRunning)
            {
                IsRunning = false;
                _streamCts?.Cancel();
                _streamCts?.Dispose();
                _streamCts = null;
                ProcessExited?.Invoke(exitCode, reason);
            }
        }
    }
}
