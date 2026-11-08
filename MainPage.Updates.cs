using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RunaVoice_WinUI.Services;

namespace RunaVoice_WinUI
{
    // Otomatik guncelleme (Velopack + GitHub Releases) - arayuz tarafi
    public sealed partial class MainPage
    {
        /// <summary>Acilistan kisa sure sonra sessizce kontrol eder; sesli odadayken kurup uygulamayi kapatmaz.</summary>
        private async Task StartupUpdateCheckAsync()
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                    TxtSettingsAppVersion.Text = $"Sürüm {UpdateService.CurrentVersion}");

                if (!UpdateService.IsInstalled) return;
                await Task.Delay(5000);

                string? version = await UpdateService.CheckAndDownloadAsync();
                if (version == null) return;

                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_isInVoiceRoom)
                    {
                        ShowToast($"✨ Yeni sürüm {version} hazır. Odadan çıkınca Ayarlar'dan güncelleyebilirsiniz.");
                        BtnCheckUpdate.Content = $"Güncelle ve yeniden başlat ({version})";
                    }
                    else
                    {
                        ShowToast($"✨ Sürüm {version} kuruluyor, uygulama yeniden başlatılacak…");
                        _ = ApplyAfterDelayAsync();
                    }
                });
            }
            catch { }
        }

        private static async Task ApplyAfterDelayAsync()
        {
            await Task.Delay(1800);
            UpdateService.ApplyAndRestart();
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (UpdateService.HasPendingUpdate)
                {
                    UpdateService.ApplyAndRestart();
                    return;
                }

                if (!UpdateService.IsConfigured)
                {
                    ShowToast("Güncelleme adresi henüz ayarlanmadı.");
                    return;
                }

                if (!UpdateService.IsInstalled)
                {
                    ShowToast("Güncelleme yalnızca kurulum dosyasıyla (Setup.exe) kurulan sürümde çalışır.");
                    return;
                }

                BtnCheckUpdate.IsEnabled = false;
                BtnCheckUpdate.Content = "Denetleniyor…";

                string? version = await UpdateService.CheckAndDownloadAsync();
                if (version == null)
                {
                    ShowToast("✅ Uygulamanız güncel.");
                    BtnCheckUpdate.Content = "Güncellemeleri denetle";
                }
                else
                {
                    ShowToast($"✨ Sürüm {version} indirildi.");
                    BtnCheckUpdate.Content = $"Güncelle ve yeniden başlat ({version})";
                }
            }
            catch
            {
                BtnCheckUpdate.Content = "Güncellemeleri denetle";
            }
            finally
            {
                BtnCheckUpdate.IsEnabled = true;
            }
        }
    }
}
