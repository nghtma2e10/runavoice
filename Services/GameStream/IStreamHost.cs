using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Host tarafındaki Sunshine motorunu yönetmek için soyutlama (PDF Bölüm 3, 5, 14)
    /// </summary>
    public interface IStreamHost
    {
        Task<GameStreamCapabilities> DetectAsync(CancellationToken ct = default);
        Task<List<GameStreamAppInfo>> GetAppsAsync(CancellationToken ct = default);
        Task<bool> AddAppAsync(string name, string exePath, string workingDir, List<string>? extraProcesses, CancellationToken ct = default);
        Task<bool> PairPinAsync(string pin, string clientName, CancellationToken ct = default);
        Task<bool> UnpairClientAsync(string clientUuidOrName, CancellationToken ct = default);
        Task<bool> EnsureRunningAsync(CancellationToken ct = default);
        Task<bool> IsSessionActiveAsync(CancellationToken ct = default);
        Task<bool> SetResolutionAsync(int width, int height, int fps, CancellationToken ct = default);
        Task<bool> RestoreResolutionAsync(CancellationToken ct = default);
        Task CleanupStaleClientsAsync(List<string> trustedPlayers, CancellationToken ct = default);
    }
}
