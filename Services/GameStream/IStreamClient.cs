using System;
using System.Threading;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Oyuncu tarafındaki Moonlight istemcisini yönetmek için soyutlama (PDF Bölüm 3, 5, 14)
    /// </summary>
    public interface IStreamClient
    {
        Task<GameStreamCapabilities> DetectAsync(CancellationToken ct = default);
        Task<bool> PairAsync(string hostIp, string pin, CancellationToken ct = default);
        Task<bool> StartStreamAsync(string hostIp, string appName, int width, int height, int fps, int bitrateKbps, string codec, bool fullscreen, CancellationToken ct = default);
        Task<bool> StopStreamAsync(string hostIp, CancellationToken ct = default);
        
        event Action<int, string>? ProcessExited;
        bool IsRunning { get; }
    }
}
