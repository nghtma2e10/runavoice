using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace RunaVoice_WinUI.Services;

/// <summary>
/// GitHub Releases uzerinden otomatik guncelleme (Velopack).
/// Repo herkese acik oldugu icin istemci tarafinda token GEREKMEZ.
/// </summary>
public static class UpdateService
{
    // TODO: GitHub'da repoyu actiktan sonra burayi kendi adresinizle degistirin.
    // Ornek: "https://github.com/kullaniciadi/runavoice"
    public const string RepoUrl = "https://github.com/nghtma2e10/runavoice";

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    public static bool IsConfigured => !RepoUrl.Contains("KULLANICI_ADI", StringComparison.Ordinal);

    private static UpdateManager Manager =>
        _manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));

    /// <summary>Uygulama Setup.exe ile kurulduysa true (Visual Studio'dan calisirken false).</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return IsConfigured && Manager.IsInstalled; }
            catch { return false; }
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try { if (IsConfigured && Manager.IsInstalled) return Manager.CurrentVersion?.ToString() ?? "?"; }
            catch { }
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static bool HasPendingUpdate => _pending != null;

    /// <summary>Yeni surum var mi? Varsa indirir ve hazir bekletir. Donen deger: yeni surum numarasi veya null.</summary>
    public static async Task<string?> CheckAndDownloadAsync()
    {
        try
        {
            if (!IsInstalled) return null;
            var update = await Manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update == null) return null;
            await Manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            _pending = update;
            return update.TargetFullRelease.Version.ToString();
        }
        catch
        {
            return null; // internet yok, GitHub'a ulasilamadi vb. -> sessizce gec
        }
    }

    /// <summary>Indirilmis guncellemeyi kurar ve uygulamayi yeniden baslatir.</summary>
    public static void ApplyAndRestart()
    {
        if (_pending == null) return;
        Manager.ApplyUpdatesAndRestart(_pending);
    }
}
