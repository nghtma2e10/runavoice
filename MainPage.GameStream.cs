using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using RunaVoice_WinUI.Services.GameStream;
using Windows.System;

namespace RunaVoice_WinUI
{
    /// <summary>
    /// Odadaki bir kullanıcı ile Oyun Akışı (Sunshine & Moonlight) etkileşimleri (PDF Bölüm 1-15)
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly ObservableCollection<GameStreamPeerViewModel> _gameStreamPeersVm = new();
        private readonly ObservableCollection<GameStreamAppInfo> _selectableGames = new();
        private GameStreamPeerViewModel? _selectedTargetPeer;
        private GsInviteMessage? _currentIncomingInvite;
        private DispatcherTimer? _inviteCountdownTimer;
        private DispatcherTimer? _sessionTelemetryTimer;
        private int _inviteRemainingSeconds = 60;
        private bool _isSuppressingPrivacyToggle = true;

        private void InitGameStreamUi()
        {
            LvGameStreamPeers.ItemsSource = _gameStreamPeersVm;
            LvSelectableGames.ItemsSource = _selectableGames;

            _isSuppressingPrivacyToggle = true;
            TglGameStreamPrivacyCurtain.IsOn = true;
            _isSuppressingPrivacyToggle = false;

            _sessionTelemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTelemetryTimer.Tick += (s, e) =>
            {
                if (_gameStream.Phase == GameStreamPhase.Playing)
                {
                    var st = _engine.IsRustEngine ? _engine.GetGameStreamStats() : _gameStream.ActiveTunnel?.Stats;
                    if (st != null)
                    {
                        TxtSessionTelemetry.Text = $"Bağlantı: Tünel (P2P) · Gecikme: {st.RttMs} ms · Gönderilen: {(st.BytesSent / 1024.0 / 1024.0):0.0} MB · Kayıp: %{st.LossRatePercent:0.0}";
                    }
                }
            };

            _gameStream.HighLossWarning += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_gameStream.Role == GameStreamRole.Host)
                    {
                        ShowToast("⚠️ Bağlantı yetersiz (kayıp > %5). Bit hızı otomatik dengeleniyor.");
                    }
                });
            };

            _engine.OnGameStreamHighLossWarning += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_gameStream.Role == GameStreamRole.Host)
                    {
                        ShowToast("⚠️ Bağlantı yetersiz (kayıp > %5). Bit hızı otomatik dengeleniyor.");
                    }
                });
            };

            _gameStream.PresenceUpdated += () =>
            {
                DispatcherQueue.TryEnqueue(RefreshGameStreamPeersUi);
            };

            _gameStream.PhaseChanged += (phase, info) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateGameStreamSessionUi(phase, info);
                    UpdateGameStreamMenuTitle();

                    var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
                    if (me != null)
                    {
                        if (phase == GameStreamPhase.Playing)
                        {
                            me.IsGameStreaming = true;
                            me.GameStreamStatusText = $"{me.Name} → {_gameStream.PlayingWithPeerName} oynuyor";
                            if (_gameStream.Role == GameStreamRole.Host)
                            {
                                ShowToast("🛡️ Bildirimlerin yayına düşmemesi için Windows Rahatsız Etmeyin modunu açabilirsiniz.");
                            }
                        }
                        else if (phase == GameStreamPhase.Idle)
                        {
                            me.IsGameStreaming = false;
                            me.GameStreamStatusText = "";
                        }
                    }
                });
            };

            _gameStream.PeerGamingStatusChanged += (peerKey, isBusy, playingWith) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var p = ActiveParticipants.FirstOrDefault(x => x.PeerKey == peerKey);
                    if (p != null)
                    {
                        p.IsGameStreaming = isBusy;
                        p.GameStreamStatusText = isBusy
                            ? (!string.IsNullOrEmpty(playingWith) ? $"{p.Name} → {playingWith} oynuyor" : "Oyun oynuyor")
                            : "";
                    }
                });
            };

            _gameStream.RemoteStateReceived += (phase, info) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CardGameStreamActiveSession.Visibility == Visibility.Visible && _gameStream.Phase != GameStreamPhase.Idle)
                    {
                        TxtSessionStepIndicator.Text = $"Uzak durum: {info}";
                    }
                });
            };

            _gameStream.InviteReceived += (invite, fromPeerKey, fromPeerName) =>
            {
                DispatcherQueue.TryEnqueue(() => ShowIncomingInviteUi(invite, fromPeerKey, fromPeerName));
            };

            _gameStream.InviteCancelled += (sid) =>
            {
                DispatcherQueue.TryEnqueue(() => HideIncomingInviteUi("Host daveti iptal etti."));
            };

            _gameStream.ResponseReceived += (resp) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleInviteResponseUi(resp));
            };

            _gameStream.CurtainChanged += (on) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ShowToast(on ? "🛡️ Host gizlilik perdesini indirdi." : "🛡️ Host oyuna döndü.");
                });
            };

            _gameStream.SessionEnded += (reason) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    string msg = reason switch
                    {
                        "host_hotkey" => "Host acil kısayolla (Ctrl+Alt+End) oturumu durdurdu.",
                        "game_exit" => "Oyun kapandığı için oturum bitti.",
                        "left_room" => "Kullanıcı odadan ayrıldığı için oturum bitti.",
                        "timeout" => "Bağlantı zaman aşımına uğradı.",
                        _ => "Oyun Akışı oturumu sonlandırıldı."
                    };
                    ShowToast(msg);
                    CardGameStreamActiveSession.Visibility = Visibility.Collapsed;
                    UpdateGameStreamMenuTitle();
                });
            };
        }

        private async void BtnOpenGameStream_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInVoiceRoom)
            {
                ShowToast("Oyun Akışını kullanabilmek için önce bir sesli odaya katılmalısınız.");
                return;
            }

            _isSuppressingPrivacyToggle = true;
            TglGameStreamPrivacyCurtain.IsOn = _gameStream.IsPrivacyCurtainEnabled;
            BadgePrivacyCurtainOff.Visibility = _gameStream.IsPrivacyCurtainEnabled ? Visibility.Collapsed : Visibility.Visible;
            _isSuppressingPrivacyToggle = false;

            UiAnimationHelper.ShowModalAnimated(GameStreamModalOverlay, GameStreamModalCard);
            _gameStream.SetModalOpen(true);

            await _gameStream.RefreshCapabilitiesAsync();
            UpdateGameStreamStatusCardUi();
            RefreshGameStreamPeersUi();
            UpdateGameStreamMenuTitle();
        }

        private async void BtnCloseGameStream_Click(object sender, RoutedEventArgs e)
        {
            await CloseGameStreamModalAsync();
        }

        private async void BackdropCloseGameStream_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await CloseGameStreamModalAsync();
        }

        private async Task CloseGameStreamModalAsync()
        {
            if (GameStreamModalOverlay.Visibility != Visibility.Visible) return;
            await UiAnimationHelper.HideModalAnimatedAsync(GameStreamModalOverlay, GameStreamModalCard);
            _gameStream.SetModalOpen(false);
            UpdateGameStreamMenuTitle();
        }

        private void UpdateGameStreamMenuTitle()
        {
            bool isActive = _gameStream.Phase == GameStreamPhase.Playing || _gameStream.Phase == GameStreamPhase.Launching;
            BtnMenuGameStream.Text = isActive ? "🎮  Oyun Akışı (Aktif)" : "🎮  Oyun Akışı";
        }

        private void UpdateGameStreamStatusCardUi()
        {
            var caps = _gameStream.LocalCapabilities;
            string gpuInfo = SunshineManager.GpuVendorAndModel ?? "GPU";
            string codecsInfo = string.Join("/", caps.Codecs);

            if (caps.CanHost && caps.CanPlay)
            {
                TxtGameStreamStatusLine.Text = $"Bu bilgisayar: Host olabilir ✓ ({gpuInfo} · {codecsInfo}) · Oynayabilir ✓";
                TxtGameStreamStatusLine.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimary"] ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 250, 252));
            }
            else if (caps.CanHost)
            {
                TxtGameStreamStatusLine.Text = $"Bu bilgisayar: Host olabilir ✓ ({gpuInfo} · {codecsInfo}) · Oynayamaz (Moonlight yok)";
            }
            else if (caps.CanPlay)
            {
                TxtGameStreamStatusLine.Text = $"Bu bilgisayar: Host olamaz ({caps.HostReason ?? "Sunshine yok"}) · Oynayabilir ✓";
            }
            else
            {
                TxtGameStreamStatusLine.Text = "Bu bilgisayar: Host veya Oyuncu olmak için gerekli bileşenler eksik";
            }

            string sunshineVer = !string.IsNullOrEmpty(caps.SunshineVersion) ? $"Sunshine v{caps.SunshineVersion} ✓" : "Sunshine: kurulu değil";
            string moonlightVer = !string.IsNullOrEmpty(caps.MoonlightVersion) ? $"Moonlight v{caps.MoonlightVersion} ✓" : "Moonlight: kurulu değil";
            TxtGameStreamComponentsLine.Text = $"{sunshineVer} · {moonlightVer}";

            BtnInstallSunshine.Visibility = string.IsNullOrEmpty(caps.SunshineVersion) ? Visibility.Visible : Visibility.Collapsed;
            BtnInstallMoonlight.Visibility = string.IsNullOrEmpty(caps.MoonlightVersion) ? Visibility.Visible : Visibility.Collapsed;
            PnlGameStreamPrivacyRow.Visibility = caps.CanHost ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshGameStreamPeersUi()
        {
            var peers = _gameStream.GetActivePeers();
            string myPeerKey = _storage.CurrentUser.PeerKey;
            bool iCanHost = _gameStream.LocalCapabilities.CanHost;

            _gameStreamPeersVm.Clear();

            foreach (var p in peers)
            {
                if (p.PeerKey == myPeerKey) continue;

                string status;
                if (!string.IsNullOrEmpty(p.PlayingWith))
                {
                    status = $"{p.PlayingWith} ile oynuyor";
                }
                else if (p.IsBusy)
                {
                    status = "Meşgul";
                }
                else
                {
                    status = "Müsait";
                }

                bool canInvite = iCanHost && p.CanPlay && !p.IsBusy && _gameStream.Phase == GameStreamPhase.Idle;

                _gameStreamPeersVm.Add(new GameStreamPeerViewModel
                {
                    PeerKey = p.PeerKey,
                    PeerName = p.PeerName,
                    StatusText = status,
                    CanHostVisibility = p.CanHost ? Visibility.Visible : Visibility.Collapsed,
                    CanPlayVisibility = p.CanPlay ? Visibility.Visible : Visibility.Collapsed,
                    CanInvite = canInvite
                });
            }

            TxtGameStreamEmptyPeers.Visibility = _gameStreamPeersVm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LvGameStreamPeers.Visibility = _gameStreamPeersVm.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // =====================================================================
        // GİZLİLİK PERDESİ VE UYARI DİYALOĞU (PDF Bölüm 9.2 & 10.4)
        // =====================================================================

        private void TglGameStreamPrivacyCurtain_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isSuppressingPrivacyToggle || BadgePrivacyCurtainOff == null || _gameStream == null) return;

            if (!TglGameStreamPrivacyCurtain.IsOn)
            {
                // Kullanıcı kapatmak istediğinde risk uyarısı penceresi çıkar (PDF 9.2)
                UiAnimationHelper.ShowModalAnimated(PrivacyOffWarningOverlay, PrivacyOffWarningCard);
                BtnPrivacyKeepOn.Focus(FocusState.Programmatic); // Varsayılan odak: Açık bırak
            }
            else
            {
                _gameStream.IsPrivacyCurtainEnabled = true;
                BadgePrivacyCurtainOff.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnPrivacyKeepOn_Click(object sender, RoutedEventArgs e)
        {
            _isSuppressingPrivacyToggle = true;
            TglGameStreamPrivacyCurtain.IsOn = true;
            _gameStream.IsPrivacyCurtainEnabled = true;
            BadgePrivacyCurtainOff.Visibility = Visibility.Collapsed;
            _isSuppressingPrivacyToggle = false;

            await UiAnimationHelper.HideModalAnimatedAsync(PrivacyOffWarningOverlay, PrivacyOffWarningCard);
        }

        private async void BtnPrivacyConfirmOff_Click(object sender, RoutedEventArgs e)
        {
            _gameStream.IsPrivacyCurtainEnabled = false;
            BadgePrivacyCurtainOff.Visibility = Visibility.Visible;
            await UiAnimationHelper.HideModalAnimatedAsync(PrivacyOffWarningOverlay, PrivacyOffWarningCard);
        }

        private void BackdropClosePrivacyWarning_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Esc veya dışarı tıklama = Açık bırak seçilmiş gibi davranır (PDF Bölüm 9.2)
            BtnPrivacyKeepOn_Click(sender, e);
        }

        // =====================================================================
        // KURULUM YÖNLENDİRMESİ VE LİSANS ATIFI (PDF Bölüm 2 & 5.2)
        // =====================================================================

        private void BtnInstallSunshine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Winget ile kullanıcı onaylı kurulum yönlendirmesi
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -Command \"Write-Host 'Sunshine Kurulumu Baslatiliyor...' -ForegroundColor Cyan; winget install -e --id LizardByte.Sunshine\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Web fallback
                _ = Launcher.LaunchUriAsync(new Uri("https://app.lizardbyte.dev/Sunshine"));
            }
        }

        private void BtnInstallMoonlight_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -Command \"Write-Host 'Moonlight Kurulumu Baslatiliyor...' -ForegroundColor Cyan; winget install -e --id MoonlightGameStreamingProject.Moonlight\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                _ = Launcher.LaunchUriAsync(new Uri("https://moonlight-stream.org"));
            }
        }

        private void BtnThirdPartyLicenses_Click(object sender, RoutedEventArgs e)
        {
            UiAnimationHelper.ShowModalAnimated(ThirdPartyLicensesModalOverlay, ThirdPartyLicensesCard);
        }

        private async void BtnCloseLicenses_Click(object sender, RoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(ThirdPartyLicensesModalOverlay, ThirdPartyLicensesCard);
        }

        private async void BackdropCloseLicenses_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(ThirdPartyLicensesModalOverlay, ThirdPartyLicensesCard);
        }

        // =====================================================================
        // DAVET VE OYUN SEÇİM AKIŞI (PDF Bölüm 10.3 & Faz 2)
        // =====================================================================

        private async void BtnSendPeerInvite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string peerKey) return;
            var target = _gameStreamPeersVm.FirstOrDefault(p => p.PeerKey == peerKey);
            if (target == null) return;

            _selectedTargetPeer = target;
            TxtGameSelectTitle.Text = $"{target.PeerName} için oyun seçin";

            // Sunshine veya mock oyun listesini doldur
            _selectableGames.Clear();
            var apps = await _gameStream.StreamHost.GetAppsAsync();
            foreach (var a in apps)
            {
                // Masaüstü girdisi gizlenir (PDF 10.3)
                if (a.Name.Contains("Desktop", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Masaüstü", StringComparison.OrdinalIgnoreCase))
                    continue;
                _selectableGames.Add(a);
            }

            // Kayıtlı oyunlar varsa ekle
            foreach (var g in _storage.Settings.GameStreamGames)
            {
                if (!_selectableGames.Any(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _selectableGames.Add(new GameStreamAppInfo
                    {
                        Id = g.SunshineAppName,
                        Name = g.Name,
                        ExePath = g.ExePath,
                        ExtraAllowedProcesses = g.ExtraAllowedProcesses
                    });
                }
            }

            if (_selectableGames.Count > 0)
            {
                LvSelectableGames.SelectedIndex = 0;
            }

            BtnConfirmSendInvite.IsEnabled = LvSelectableGames.SelectedItem != null;
            UiAnimationHelper.ShowModalAnimated(GameSelectModalOverlay, GameSelectModalCard);
        }

        private void LvSelectableGames_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnConfirmSendInvite.IsEnabled = LvSelectableGames.SelectedItem != null;
        }

        private async void BtnCancelGameSelect_Click(object sender, RoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(GameSelectModalOverlay, GameSelectModalCard);
            _selectedTargetPeer = null;
        }

        private async void BackdropCloseGameSelect_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(GameSelectModalOverlay, GameSelectModalCard);
            _selectedTargetPeer = null;
        }

        private async void BtnConfirmSendInvite_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTargetPeer == null || LvSelectableGames.SelectedItem is not GameStreamAppInfo selectedGame)
                return;

            await UiAnimationHelper.HideModalAnimatedAsync(GameSelectModalOverlay, GameSelectModalCard);

            if (_gameStream.SendInvite(
                _selectedTargetPeer.PeerKey,
                _selectedTargetPeer.PeerName,
                selectedGame.Name,
                selectedGame.ExePath,
                selectedGame.ExtraAllowedProcesses,
                "1080p",
                60,
                "HEVC",
                3800,
                out string? error))
            {
                ShowToast($"🎮 {_selectedTargetPeer.PeerName} oyuncusuna {selectedGame.Name} daveti gönderildi.");
            }
            else
            {
                ShowToast(error ?? "Davet gönderilemedi.");
            }
        }

        private async void BtnAddCustomGame_Click(object sender, RoutedEventArgs e)
        {
            // Windows OpenFileDialog ile exe seçici
            if (MainWindow.Current == null) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current));

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                string gameName = Path.GetFileNameWithoutExtension(file.Name);
                var newGame = new GameStreamGame
                {
                    Name = gameName,
                    ExePath = file.Path,
                    SunshineAppName = gameName
                };

                _storage.Settings.GameStreamGames.Add(newGame);
                _storage.SaveSettings();

                await _gameStream.StreamHost.AddAppAsync(gameName, file.Path, Path.GetDirectoryName(file.Path) ?? "", null);

                _selectableGames.Add(new GameStreamAppInfo
                {
                    Id = gameName,
                    Name = gameName,
                    ExePath = file.Path
                });
                LvSelectableGames.SelectedItem = _selectableGames.Last();
                ShowToast($"🎮 '{gameName}' oyunu eklendi.");
            }
        }

        // =====================================================================
        // OYUNCU İSTEK KARTI VE YANITLARI (PDF Bölüm 10.2 & Faz 2)
        // =====================================================================

        private void ShowIncomingInviteUi(GsInviteMessage invite, string fromPeerKey, string fromPeerName)
        {
            _currentIncomingInvite = invite;
            _selectedTargetPeer = new GameStreamPeerViewModel
            {
                PeerKey = fromPeerKey,
                PeerName = fromPeerName
            };

            TxtInviteTitle.Text = $"{fromPeerName} sizi {invite.Game} oynamaya davet ediyor";
            TxtInviteQuality.Text = $"{invite.Resolution} · {invite.Fps} FPS · {invite.Codec} · ~{(invite.BitrateKbps / 1000.0):0.0} Mbps";
            TxtInvitePrivacyNote.Text = invite.Privacy ? "🛡️ Gizlilik koruması aktif" : "⚠️ Gizlilik koruması kapalı";

            _inviteRemainingSeconds = invite.ExpireSeconds > 0 ? invite.ExpireSeconds : 60;
            TxtInviteTimer.Text = $"{_inviteRemainingSeconds} sn";

            _inviteCountdownTimer?.Stop();
            _inviteCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _inviteCountdownTimer.Tick += (s, e) =>
            {
                _inviteRemainingSeconds--;
                if (_inviteRemainingSeconds <= 0)
                {
                    _inviteCountdownTimer.Stop();
                    BtnRejectInvite_Click(this, new RoutedEventArgs());
                }
                else
                {
                    TxtInviteTimer.Text = $"{_inviteRemainingSeconds} sn";
                }
            };
            _inviteCountdownTimer.Start();

            CardGameStreamIncomingInvite.Visibility = Visibility.Visible;
        }

        private void HideIncomingInviteUi(string? toastMsg = null)
        {
            _inviteCountdownTimer?.Stop();
            _inviteCountdownTimer = null;
            _currentIncomingInvite = null;
            CardGameStreamIncomingInvite.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(toastMsg)) ShowToast(toastMsg);
        }

        private void BtnAcceptInvite_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIncomingInvite == null || _selectedTargetPeer == null) return;

            string sid = _currentIncomingInvite.SessionId;
            string hostPeerKey = _selectedTargetPeer.PeerKey;
            string hostPeerName = _selectedTargetPeer.PeerName;

            HideIncomingInviteUi();
            _gameStream.AcceptInvite(sid, hostPeerKey, hostPeerName);
            ShowToast("🎮 Oyun daveti kabul edildi! Bağlantı hazırlanıyor...");
        }

        private void BtnRejectInvite_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIncomingInvite != null && _selectedTargetPeer != null)
            {
                _gameStream.RejectInvite(_currentIncomingInvite.SessionId, _selectedTargetPeer.PeerKey, "declined");
            }

            HideIncomingInviteUi("Davet reddedildi.");
        }

        private void HandleInviteResponseUi(GsResponseMessage resp)
        {
            if (resp.Accept)
            {
                _gameStream.SetPhase(GameStreamPhase.Pairing, "Eşleştiriliyor...");
                ShowToast("🎮 Davet kabul edildi! Oturum hazırlanıyor...");
            }
            else
            {
                string info = resp.Reason switch
                {
                    "busy" => "Arkadaşınız şu an başka bir oturumda meşgul.",
                    "timeout" => "Davet zaman aşımına uğradı.",
                    "no_moonlight" => "Arkadaşınızda Moonlight kurulu değil.",
                    _ => "Arkadaşınız daveti reddetti."
                };
                ShowToast(info);
                _gameStream.SetPhase(GameStreamPhase.Idle, "Boşta");
            }
        }

        private void UpdateGameStreamSessionUi(GameStreamPhase phase, string info)
        {
            if (phase == GameStreamPhase.Idle)
            {
                _sessionTelemetryTimer?.Stop();
                CardGameStreamActiveSession.Visibility = Visibility.Collapsed;
                return;
            }

            if (phase == GameStreamPhase.Playing)
            {
                _sessionTelemetryTimer?.Start();
            }
            else
            {
                _sessionTelemetryTimer?.Stop();
            }

            CardGameStreamActiveSession.Visibility = Visibility.Visible;

            string stepText = phase switch
            {
                GameStreamPhase.Pairing => "1/5 Eşleştiriliyor...",
                GameStreamPhase.Tunneling => "2/5 Bağlantı hazırlanıyor...",
                GameStreamPhase.Probing => "3/5 Hız ölçülüyor...",
                GameStreamPhase.Launching => "4/5 Oyun başlatılıyor...",
                GameStreamPhase.Playing => $"5/5 Oynanıyor: {_gameStream.ActiveGameName ?? "Oyun"}",
                GameStreamPhase.Ending => "Oturum sonlandırılıyor...",
                _ => $"Durum: {info}"
            };

            TxtSessionStepIndicator.Text = stepText;
            TxtSessionHostNotice.Visibility = _gameStream.Role == GameStreamRole.Host ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnEndSession_Click(object sender, RoutedEventArgs e)
        {
            await _gameStream.CloseActiveSessionAsync("user");
        }
    }

    /// <summary>
    /// Odadaki bir kullanıcının Oyun Akışı UI görünüm modeli
    /// </summary>
    public class GameStreamPeerViewModel
    {
        public string PeerKey { get; set; } = "";
        public string PeerName { get; set; } = "";
        public string StatusText { get; set; } = "Müsait";
        public Visibility CanHostVisibility { get; set; } = Visibility.Collapsed;
        public Visibility CanPlayVisibility { get; set; } = Visibility.Collapsed;
        public bool CanInvite { get; set; }
    }
}
