using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using RunaVoice_WinUI.Services.GameStream;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using RunaVoice_WinUI.Services.Engine;

namespace RunaVoice_WinUI
{
    public sealed partial class MainPage : Page
    {
        private readonly StorageService _storage = new();
        private readonly SignalingService _signaling = new();
        private readonly IRealtimeEngine _engine;
        private readonly AudioEngine _audio;
        private readonly SoundpadService _soundpad;
        private readonly WebRtcService _webrtc;
        private readonly ScreenCaptureService _screenCapture = new();
        private readonly GameStreamService _gameStream;

        public ObservableCollection<PersistentRoom> PinnedRooms { get; } = new();
        public ObservableCollection<RoomParticipant> ActiveParticipants { get; } = new();
        public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
        public ObservableCollection<CustomSoundItem> SoundpadItems { get; } = new();

        private readonly HashSet<int> _animatedElements = new();
        private DispatcherTimer? _telemetryTimer;
        private CancellationTokenSource? _toastCts;

        private bool _isInVoiceRoom = false;
        private string _activeRoomCode = "";
        private string _activeRoomTitle = "";
        private bool _showAllSavedRooms = false;
        private bool _isHostPermanentChecked = false;
        private bool? _lastRippleSpeakingState = null;

        // Kişiye özel ses menüsü durumu
        private RoomParticipant? _popoverTargetParticipant;
        private bool _suppressPopoverEvents = false;
        private bool _isUiReady = false;

        public MainPage()
        {
            _audio = new AudioEngine(_storage);
            _soundpad = new SoundpadService(_audio, _storage);
            _webrtc = new WebRtcService(_signaling, _audio, _storage);
            _gameStream = new GameStreamService(_webrtc, _storage, _screenCapture, _audio);
            _engine = EngineFactory.CreateEngine(_storage, _signaling, _audio, _webrtc, _screenCapture, out string? fallbackNotice);

            _engine.OnEngineFallback += notice => DispatcherQueue?.TryEnqueue(() => ShowToast(notice));
            if (!string.IsNullOrEmpty(fallbackNotice))
            {
                DispatcherQueue?.TryEnqueue(() => ShowToast(fallbackNotice));
            }

            _suppressSettingsEvents = true;
            _suppressPopoverEvents = true;
            InitializeComponent();
            UpdateActiveEngineDisplay();
            _suppressSettingsEvents = false;
            _suppressPopoverEvents = false;
            _isUiReady = true;

            InitializeStaticBitmaps();

            ParticipantsListView.ItemsSource = ActiveParticipants;
            StageHorizontalRepeater.ItemsSource = ActiveParticipants;
            ChatMessagesList.ItemsSource = ChatMessages;
            SoundpadRepeater.ItemsSource = SoundpadItems;

            SetupInteractiveAnimations();
            WireBackendEvents();
            RefreshModalAvatars();
            RefreshSoundpadList();
            LoadPersistentRooms();

            if (MainWindow.Current != null)
            {
                MainWindow.Current.OnOpenSettingsRequested += () => DispatcherQueue?.TryEnqueue(OpenSettingsModal);
                MainWindow.Current.OnTogglePinCurrentRoomRequested += () => DispatcherQueue?.TryEnqueue(TogglePinCurrentRoom);
                MainWindow.Current.UpdateTitleBarRoomState(null, false);
            }

            InitOnDemandTelemetryTimer();
            InitGameStreamUi();
            Loaded += MainPage_Loaded;
        }

        private void SetupInteractiveAnimations()
        {
            // Hero 3D logo süzülme animasyonu (GPU DirectComposition - 0% CPU)
            UiAnimationHelper.StartHeroLogoFloat(HeroLogoContainer, HeroLogoGlow);

            // Ana sayfa iki büyük hap kart koordine hover/press animasyonu
            UiAnimationHelper.AttachHeroPillCardAnimation(
                HostHeroCard,
                HostCardLeftCircle,
                HostCardRightArrow,
                (Brush)Resources["HostCardRestBrush"],
                (Brush)Resources["HostCardHoverBrush"],
                Color.FromArgb(89, 168, 85, 247),
                Color.FromArgb(185, 192, 132, 252));

            UiAnimationHelper.AttachHeroPillCardAnimation(
                JoinHeroCard,
                JoinCardLeftCircle,
                JoinCardRightArrow,
                (Brush)Resources["JoinCardRestBrush"],
                (Brush)Resources["JoinCardHoverBrush"],
                Color.FromArgb(77, 96, 165, 250),
                Color.FromArgb(175, 147, 197, 253));

            // Lobi ve Modal butonları
            UiAnimationHelper.AttachInteractiveHover(BtnToggleShowAllRooms, 1.04f, 0f, 0.96f);
            UiAnimationHelper.AttachInteractiveHover(BtnCloseHostModal, 1.10f, 0f, 0.92f);
            UiAnimationHelper.AttachInteractiveHover(BtnRefreshCode, 1.08f, -1f, 0.92f);
            UiAnimationHelper.AttachInteractiveHover(BtnCopyHostCode, 1.08f, -1f, 0.92f);
            UiAnimationHelper.AttachInteractiveHover(BtnTogglePermanentCheckbox, 1.015f, 0f, 0.98f);
            UiAnimationHelper.AttachInteractiveHover(BtnHostAvatarPicker, 1.07f, -1f, 0.94f);
            UiAnimationHelper.AttachInteractiveHover(BtnCancelHostModal, 1.03f, -1f, 0.96f);
            UiAnimationHelper.AttachInteractiveHover(BtnStartAndJoin, 1.03f, -1.5f, 0.96f);

            UiAnimationHelper.AttachInteractiveHover(BtnCloseJoinModal, 1.10f, 0f, 0.92f);
            UiAnimationHelper.AttachInteractiveHover(BtnJoinAvatarPicker, 1.07f, -1f, 0.94f);
            UiAnimationHelper.AttachInteractiveHover(BtnCancelJoinModal, 1.03f, -1f, 0.96f);
            UiAnimationHelper.AttachInteractiveHover(BtnSubmitJoin, 1.03f, -1.5f, 0.96f);

            // Sesli oda alt kontrol kapsülü & sahne butonları
            UiAnimationHelper.AttachInteractiveHover(BtnMuteToggle, 1.08f, -2f, 0.93f);
            UiAnimationHelper.AttachInteractiveHover(BtnDeafenToggle, 1.08f, -2f, 0.93f);
            UiAnimationHelper.AttachInteractiveHover(BtnScreenShareToggle, 1.08f, -2f, 0.93f);
            UiAnimationHelper.AttachInteractiveHover(BtnSoundpadToggle, 1.08f, -2f, 0.93f);
            UiAnimationHelper.AttachInteractiveHover(BtnStageMore, 1.08f, -2f, 0.93f);
            UiAnimationHelper.AttachInteractiveHover(BtnLeaveVoice, 1.07f, -2f, 0.93f);

            UiAnimationHelper.AttachInteractiveHover(BtnExitStreamInBottomBar, 1.05f, -1.5f, 0.95f);
            UiAnimationHelper.AttachInteractiveHover(BtnSendChat, 1.09f, -1f, 0.92f);
            UiAnimationHelper.AttachInteractiveHover(BtnChatPlusMenu, 1.08f, -1f, 0.93f);

            // Ekran paylaşımı ve yayın kontrol butonları
            UiAnimationHelper.AttachInteractiveHover(BtnStreamOptions, 1.05f, -1f, 0.95f);
            UiAnimationHelper.AttachInteractiveHover(BtnStreamFullscreen, 1.06f, -1f, 0.94f);
            UiAnimationHelper.AttachInteractiveHover(BtnCancelScreenPicker, 1.03f, -1f, 0.96f);
            UiAnimationHelper.AttachInteractiveHover(BtnConfirmStartStream, 1.03f, -1.5f, 0.96f);

            // Madde 9: Ayrılma onay penceresi buton hover animasyonları
            UiAnimationHelper.AttachInteractiveHover(BtnCancelLeaveRoom, 1.03f, -1f, 0.96f);
            UiAnimationHelper.AttachInteractiveHover(BtnConfirmLeaveRoom, 1.03f, -1.5f, 0.96f);
        }

        private void InteractiveCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fe);
                if (_animatedElements.Add(id))
                {
                    // Madde 4: Hover ofsetini tam piksele yuvarla (-2), ölçeği 1.015'e indir; hover'da kenarlık rengini #338B5CF6'ya geçir
                    UiAnimationHelper.AttachInteractiveHover(fe, 1.015f, -2f, 0.985f);
                    if (fe is Border b)
                    {
                        var origBrush = b.BorderBrush;
                        var hoverBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x8B, 0x5C, 0xF6));
                        fe.PointerEntered += (_, _) => b.BorderBrush = hoverBrush;
                        fe.PointerExited += (_, _) => b.BorderBrush = origBrush;
                    }
                }
            }
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainPage_Loaded;
            UiAnimationHelper.AnimateViewEntrance(LandingViewGrid);
            UpdateSpatialMenuBadge();
            _ = StartupUpdateCheckAsync();

            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg.StartsWith("--join=", StringComparison.OrdinalIgnoreCase))
                    {
                        string autoRoom = StorageService.NormalizeRoomCode(arg["--join=".Length..]);
                        if (!string.IsNullOrEmpty(autoRoom))
                        {
                            await Task.Delay(300);
                            await EnterVoiceRoomAsync(autoRoom, $"Oda {autoRoom}", false);
                        }
                        break;
                    }
                    else if (arg.Equals("--show-modal=host", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(200);
                        BtnOpenHostDialog_Tapped(this, null!);
                        break;
                    }
                    else if (arg.Equals("--show-modal=join", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(200);
                        BtnOpenJoinDialog_Tapped(this, null!);
                        break;
                    }
                    else if (arg.Equals("--show-modal=settings", StringComparison.OrdinalIgnoreCase) ||
                             arg.Equals("--open-settings", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(200);
                        OpenSettingsModal();
                        SwitchSettingsTab("advanced");
                        break;
                    }
                }
            }
            catch { }
        }

        private void WireBackendEvents()
        {
            // 1. Konuşma Halkaları (Sadece durum değiştiğinde GPU animasyonu güncellenir -> 0% CPU!)
            _audio.OnLocalSpeakingChanged += isSpeaking =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
                    if (me != null) me.IsSpeaking = isSpeaking;
                    RefreshStageRippleState();
                });
            };

            _audio.OnPeerSpeakingChanged += (peerKey, isSpeaking) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var peer = ActiveParticipants.FirstOrDefault(p => p.PeerKey == peerKey);
                    if (peer != null) peer.IsSpeaking = isSpeaking;
                    RefreshStageRippleState();

                    if (_isSpatialModalOpen)
                    {
                        var sItem = _spatialItems.FirstOrDefault(x => x.Name == peer?.Name);
                        if (sItem != null && sItem.AvatarRingBorder != null && sItem != _selectedSpatialItem)
                        {
                            sItem.IsSpeaking = isSpeaking;
                            sItem.AvatarRingBorder.BorderBrush = isSpeaking
                                ? new SolidColorBrush(Color.FromArgb(255, 34, 197, 94))
                                : new SolidColorBrush(Color.FromArgb(140, 148, 163, 184));
                        }
                    }
                });
            };

            // 2. Kalıcı Odalar Canlı Çevrimiçi Sayacı
            _signaling.OnWatchedRoomOnlineChanged += (roomCode, onlineCount) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var room = PinnedRooms.FirstOrDefault(r => r.Code.Equals(roomCode, StringComparison.OrdinalIgnoreCase));
                    if (room != null) room.OnlineCount = onlineCount;
                });
            };

            // 3. Odaya Katılan / Güncellenen / Ayrılan Kişiler
            _signaling.OnPeerJoined += snap =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (!_isInVoiceRoom) return;

                        var existing = ActiveParticipants.FirstOrDefault(p => p.PeerKey == snap.PeerKey);
                        if (existing == null)
                        {
                            existing = new RoomParticipant
                            {
                                PeerKey = snap.PeerKey,
                                Name = snap.Name,
                                VoiceSessionId = snap.VoiceSessionId,
                                Avatar = snap.Avatar,
                                IsMuted = snap.IsMuted,
                                IsDeafened = snap.IsDeafened,
                                IsScreenSharing = snap.IsScreenSharing,
                                LastSeen = snap.LastSeen,
                                // Motor/protokol bilgisi ilk eklemede de kopyalanmalı: aksi hâlde yeni gelen her eş
                                // varsayılan "legacy/1" görünür ve Rust motoru bağlantıyı hiç başlatmaz.
                                Engine = snap.Engine,
                                Proto = snap.Proto,
                                PingStatus = Models.PeerPingStatus.Connecting,
                                LatencyRttMs = -1
                            };
                            ApplySavedPeerAudioConfigToParticipant(existing);
                            ActiveParticipants.Add(existing);
                            _soundpad.PlayUiEffect(UiSoundEffect.PeerJoin);
                            ShowToast($"👋 {existing.Name} odaya katıldı");
                        }
                        else
                        {
                            existing.Name = snap.Name;
                            if (!string.IsNullOrEmpty(snap.Avatar)) existing.Avatar = snap.Avatar;
                            existing.IsMuted = snap.IsMuted;
                            existing.IsDeafened = snap.IsDeafened;
                            existing.IsScreenSharing = snap.IsScreenSharing;
                            existing.Engine = snap.Engine;
                            existing.Proto = snap.Proto;
                        }

                    UpdateParticipantCountsAndTree();
                        await _engine.EnsurePeerConnectionAsync(_storage.CurrentUser.PeerKey, existing);
                    }
                    catch { }
                });
            };

            _signaling.OnPeerUpdated += snap =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isInVoiceRoom) return;
                    var existing = ActiveParticipants.FirstOrDefault(p => p.PeerKey == snap.PeerKey);
                    if (existing != null)
                    {
                        existing.Name = snap.Name;
                        if (!string.IsNullOrEmpty(snap.Avatar)) existing.Avatar = snap.Avatar;
                        existing.IsMuted = snap.IsMuted;
                        existing.IsDeafened = snap.IsDeafened;
                        existing.IsScreenSharing = snap.IsScreenSharing;
                        bool engineChanged = existing.Engine != snap.Engine || existing.Proto != snap.Proto;
                        existing.Engine = snap.Engine;
                        existing.Proto = snap.Proto;
                        UpdateParticipantCountsAndTree();

                        // Karşı tarafın motoru sonradan uyumlu hâle geldiyse (ör. motor değiştirip yeniden yayın yaptı)
                        // daha önce "eski motor" diye atlanan bağlantıyı şimdi başlat.
                        if (engineChanged && existing.IsLegacyMismatch)
                        {
                            existing.IsLegacyMismatch = false;
                            _ = _engine.EnsurePeerConnectionAsync(_storage.CurrentUser.PeerKey, existing);
                        }
                    }
                });
            };

            _signaling.OnPeerLeft += (peerKey, peerName) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var existing = ActiveParticipants.FirstOrDefault(p => p.PeerKey == peerKey);
                    if (existing != null)
                    {
                        ActiveParticipants.Remove(existing);
                        _soundpad.PlayUiEffect(UiSoundEffect.PeerLeave);
                        ShowToast($"{peerName} odadan ayrıldı");
                    }
                    _gameStream.RemovePeer(peerKey);
                    UpdateParticipantCountsAndTree();
                    RefreshStageRippleState();
                });
            };

            // 4. Oda Başlığı ve Aktivite Senkronizasyonu
            _signaling.OnRoomMetadataReceived += (code, title, activity, isPersistent) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isInVoiceRoom) return;
                    if (!string.IsNullOrWhiteSpace(title) && !title.Equals(code, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeRoomTitle = title;
                        TxtStageRoomTitle.Text = title;
                    }
                    if (!string.IsNullOrWhiteSpace(activity))
                    {
                        TxtCurrentActivityPill.Text = activity;
                    }
                });
            };

            // 5. Gerçek WebRTC RTT Ping & Jitter Ölçümü (Madde 12)
            _engine.OnPeerPingUpdated += (peerKey, rttMs, jitterMs) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var peer = ActiveParticipants.FirstOrDefault(p => p.PeerKey == peerKey);
                    if (peer != null)
                    {
                        peer.LatencyRttMs = (int)Math.Round(rttMs);
                        peer.JitterMs = (int)Math.Round(jitterMs);
                        peer.PingStatus = Models.PeerPingStatus.Measured;
                    }

                    UpdateSelfRowPingSummary();
                });
            };

            // UDP/SRTP ağ gecikmesi + uçtan uca ses gecikmesi (kullanıcı penceresi)
            _engine.OnPeerLatencyUpdated += (peerKey, info) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var peer = ActiveParticipants.FirstOrDefault(p => p.PeerKey == peerKey);
                    if (peer == null) return;
                    peer.NetworkRttExactMs = info.NetworkRttMs;
                    peer.LatencyInfo = info;
                    if (ReferenceEquals(peer, _popoverTargetParticipant) && VoiceUserPopoverOverlay.Visibility == Visibility.Visible)
                    {
                        RefreshPopoverLatency(peer);
                    }
                });
            };

            _engine.OnPeerPingStatusChanged += (peerKey, status) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var peer = ActiveParticipants.FirstOrDefault(p => p.PeerKey == peerKey);
                    if (peer != null)
                    {
                        peer.PingStatus = status;
                        if (status == Models.PeerPingStatus.Timeout)
                        {
                            peer.LatencyRttMs = -1;
                            peer.NetworkRttExactMs = -1;
                            peer.LatencyInfo = null;
                        }
                        if (ReferenceEquals(peer, _popoverTargetParticipant) && VoiceUserPopoverOverlay.Visibility == Visibility.Visible)
                        {
                            RefreshPopoverLatency(peer);
                        }
                    }

                    UpdateSelfRowPingSummary();
                });
            };

            _engine.OnPeerRttReset += (peerKey) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var peer = ActiveParticipants.FirstOrDefault(p => p.PeerKey == peerKey);
                    if (peer != null)
                    {
                        peer.LatencyRttMs = -1;
                        peer.NetworkRttExactMs = -1;
                        peer.LatencyInfo = null;
                        peer.PingStatus = Models.PeerPingStatus.Connecting;
                    }

                    UpdateSelfRowPingSummary();
                });
            };

            // 6. P2P Geçici Sohbet
            _signaling.OnChatMessageReceived += msg =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isInVoiceRoom) return;
                    AddChatMessageBounded(msg);
                    RoomChatEmptyHint.Visibility = Visibility.Collapsed;
                    ChatMessagesList.ScrollIntoView(msg);
                    _soundpad.PlayUiEffect(UiSoundEffect.MessageReceived);
                });
            };

            _signaling.OnPollVoteReceived += (msgId, opt) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var targetMsg = ChatMessages.FirstOrDefault(m => m.Id == msgId);
                    if (targetMsg?.PollData != null)
                    {
                        if (opt == 1) targetMsg.PollData.Option1.Votes++;
                        else if (opt == 2) targetMsg.PollData.Option2.Votes++;
                        targetMsg.PollData.RecalculatePercents();
                    }
                });
            };

            // 7. Ekran Paylaşımı ve Canlı Video Olayları
            WireScreenShareEvents();
        }

        private void InitOnDemandTelemetryTimer()
        {
            // Sadece Ayarlar veya HUD açıkken 180ms'de bir çalışır; boşta ve normal sesli odada %0 CPU kullanır!
            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _telemetryTimer.Tick += (_, _) =>
            {
                bool settingsOpen = SettingsModalOverlay.Visibility == Visibility.Visible;
                bool hudOpen = _isInVoiceRoom && PersistentTelemetryPanel != null && PersistentTelemetryPanel.Visibility == Visibility.Visible;

                if (!settingsOpen && !hudOpen)
                {
                    _telemetryTimer?.Stop();
                    return;
                }

                if (settingsOpen) UpdateSettingsLiveTelemetry();
                if (hudOpen) UpdateTelemetryModalData();
            };
        }

        private void EnsureTelemetryTimerState()
        {
            bool needed = SettingsModalOverlay.Visibility == Visibility.Visible ||
                          (_isInVoiceRoom && PersistentTelemetryPanel?.Visibility == Visibility.Visible);
            if (needed) _telemetryTimer?.Start();
            else _telemetryTimer?.Stop();
        }

        private void RefreshStageRippleState(bool force = false)
        {
            if (!_isInVoiceRoom) return;
            bool anyoneSpeaking = ActiveParticipants.Any(p => p.IsSpeaking);
            if (!force && _lastRippleSpeakingState == anyoneSpeaking) return;

            _lastRippleSpeakingState = anyoneSpeaking;
            UiAnimationHelper.SetVoiceStageRipplesState(
                RippleRing1,
                RippleRing2,
                RippleRing3,
                _storage.Settings.RippleAnimationsEnabled,
                anyoneSpeaking);
        }

        // =====================================================================
        // LOBİ VE KALICI ODALAR YÖNETİMİ
        // =====================================================================

        private void LoadPersistentRooms()
        {
            var allSaved = _storage.GetPersistentRooms();
            TxtSavedRoomsCount.Text = allSaved.Count.ToString();
            SavedRoomsCountBadge.Visibility = allSaved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            TxtShowAllRoomsLabel.Text = allSaved.Count > 3
                ? (_showAllSavedRooms ? "Daha Az Göster" : $"Tümünü Gör ({allSaved.Count})")
                : "Tümünü Gör";

            var toShow = _showAllSavedRooms ? allSaved : allSaved.Take(3).ToList();

            PinnedRooms.Clear();
            foreach (var r in toShow) PinnedRooms.Add(r);

            if (PinnedRooms.Count == 0)
            {
                EmptyRoomsPlaceholder.Visibility = Visibility.Visible;
                RoomsRepeater.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyRoomsPlaceholder.Visibility = Visibility.Collapsed;
                RoomsRepeater.Visibility = Visibility.Visible;
                RoomsRepeater.ItemsSource = PinnedRooms;
            }

            if (!_isInVoiceRoom)
            {
                _ = _signaling.StartWatchingPersistentRoomsAsync(allSaved.Select(r => r.Code));
            }
        }

        private void BtnToggleShowAllRooms_Click(object sender, RoutedEventArgs e)
        {
            var allSaved = _storage.GetPersistentRooms();
            if (allSaved.Count == 0)
            {
                ShowToast("⭐ Henüz kayıtlı kalıcı odanız bulunmuyor");
                return;
            }
            if (allSaved.Count <= 3)
            {
                ShowToast($"Kayıtlı {allSaved.Count} kalıcı odanızın tamamı listeleniyor");
                return;
            }

            _showAllSavedRooms = !_showAllSavedRooms;
            LoadPersistentRooms();
        }

        private void BtnDeletePersistentRoom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string code)
            {
                _storage.RemovePersistentRoom(code);
                LoadPersistentRooms();
                ShowToast($"Kalıcı oda kaldırıldı ({code})");
            }
        }

        private void SavedRoomCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                // Sil butonuna basıldıysa kart tıklamasını yoksay
                DependencyObject? cur = dep;
                while (cur != null && !ReferenceEquals(cur, sender))
                {
                    if (cur is Button) return;
                    cur = VisualTreeHelper.GetParent(cur);
                }
            }

            if (sender is FrameworkElement fe && fe.Tag is string code)
            {
                _ = JoinSavedRoomByCodeAsync(code);
            }
        }

        // =====================================================================
        // YENİ ODA BAŞLAT VE ODAYA KATIL MODALLARI (p2p-ana-sayfa-create & join)
        // =====================================================================

        private void RefreshModalAvatars()
        {
            var src = AvatarHelper.LoadAvatarSource(_storage.CurrentUser.AvatarPath, 128);
            HostModalAvatarBrush.ImageSource = src;
            JoinModalAvatarBrush.ImageSource = src;
            SettingsAvatarBrush.ImageSource = src;
            SettingsSidebarAvatarBrush.ImageSource = src;

            var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
            if (me != null)
            {
                me.Avatar = !string.IsNullOrEmpty(_storage.CurrentUser.AvatarDataUri)
                    ? _storage.CurrentUser.AvatarDataUri
                    : _storage.CurrentUser.AvatarPath;
            }
        }

        private void SetPermanentCheckboxState(bool isChecked)
        {
            _isHostPermanentChecked = isChecked;
            if (isChecked)
            {
                PermanentCheckboxBox.Background = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
                PermanentCheckboxBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
                PermanentCheckboxTick.Visibility = Visibility.Visible;
                UiAnimationHelper.AnimateViewEntrance(TxtPermanentTitle);
            }
            else
            {
                PermanentCheckboxBox.Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255));
                PermanentCheckboxBox.BorderBrush = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255));
                PermanentCheckboxTick.Visibility = Visibility.Collapsed;
                TxtPermanentTitle.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnTogglePermanentCheckbox_Click(object sender, RoutedEventArgs e)
        {
            SetPermanentCheckboxState(!_isHostPermanentChecked);
        }

        private void BtnOpenHostDialog_Tapped(object sender, TappedRoutedEventArgs e)
        {
            TxtGeneratedCode.Text = $"RUNA-{Random.Shared.Next(1000, 9999)}";
            TxtHostNickname.Text = _storage.CurrentUser.Name;
            TxtPermanentTitle.Text = "";
            SetPermanentCheckboxState(false);
            IconHostCopyNormal.Visibility = Visibility.Visible;
            IconHostCopyCheck.Visibility = Visibility.Collapsed;
            RefreshModalAvatars();

            UiAnimationHelper.ShowModalAnimated(HostRoomModalOverlay, HostModalDialog);
        }

        private void BtnOpenJoinDialog_Tapped(object sender, TappedRoutedEventArgs e)
        {
            TxtJoinRoomInput.Text = "";
            JoinErrorBanner.Visibility = Visibility.Collapsed;
            TxtJoinNickname.Text = _storage.CurrentUser.Name;
            BtnSubmitJoin.IsEnabled = true;
            TxtSubmitJoinLabel.Text = "Odaya Katıl";
            RefreshModalAvatars();

            UiAnimationHelper.ShowModalAnimated(JoinRoomModalOverlay, JoinModalDialog);
            TxtJoinRoomInput.Focus(FocusState.Programmatic);
        }

        private async void BtnPickProfileAvatar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MainWindow.Current == null) return;
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.Thumbnail,
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary
                };
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");

                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(MainWindow.Current));
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _storage.ImportAvatarFile(file.Path);
                    RefreshModalAvatars();
                    if (_isInVoiceRoom) await _signaling.PublishPeerUpdateAsync();
                    ShowToast("Profil fotoğrafınız güncellendi");
                }
            }
            catch { }
        }

        private async void BtnCopyHostCode_Click(object sender, RoutedEventArgs e)
        {
            CopyTextToClipboard(TxtGeneratedCode.Text);
            IconHostCopyNormal.Visibility = Visibility.Collapsed;
            IconHostCopyCheck.Visibility = Visibility.Visible;
            ShowToast($"📋 Oda kodu kopyalandı: {TxtGeneratedCode.Text}");
            await Task.Delay(1400);
            IconHostCopyCheck.Visibility = Visibility.Collapsed;
            IconHostCopyNormal.Visibility = Visibility.Visible;
        }

        private void BtnRefreshCode_Click(object sender, RoutedEventArgs e)
        {
            TxtGeneratedCode.Text = $"RUNA-{Random.Shared.Next(1000, 9999)}";
            UiAnimationHelper.AnimateViewEntrance(TxtGeneratedCode);
        }

        private async void CloseModals_Click(object sender, RoutedEventArgs e)
        {
            await CloseHostAndJoinModalsAsync();
        }

        private async void BackdropCloseModals_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await CloseHostAndJoinModalsAsync();
        }

        private async Task CloseHostAndJoinModalsAsync()
        {
            if (HostRoomModalOverlay.Visibility == Visibility.Visible)
                await UiAnimationHelper.HideModalAnimatedAsync(HostRoomModalOverlay, HostModalDialog);
            if (JoinRoomModalOverlay.Visibility == Visibility.Visible)
                await UiAnimationHelper.HideModalAnimatedAsync(JoinRoomModalOverlay, JoinModalDialog);
        }

        private void StopPropagation_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void BtnStartAndJoin_Click(object sender, RoutedEventArgs e)
        {
            var code = StorageService.NormalizeRoomCode(TxtGeneratedCode.Text);
            var isPersistent = _isHostPermanentChecked;
            var title = string.IsNullOrWhiteSpace(TxtPermanentTitle.Text) ? $"Oda {code}" : TxtPermanentTitle.Text.Trim();
            var fallbackNick = _storage.IsTestProfile ? "TestHesap" : Environment.UserName;
            var nick = string.IsNullOrWhiteSpace(TxtHostNickname.Text) ? fallbackNick : TxtHostNickname.Text.Trim();

            _storage.CurrentUser.Name = nick;
            _storage.SaveSettings();

            if (isPersistent)
            {
                _storage.SavePersistentRoom(code, title, isOwner: true);
                LoadPersistentRooms();
            }

            await UiAnimationHelper.HideModalAnimatedAsync(HostRoomModalOverlay, HostModalDialog);
            await EnterVoiceRoomAsync(code, title, isPersistent);
        }

        private void TxtJoinRoomInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                BtnSubmitJoin_Click(sender, e);
            }
        }

        private async void BtnSubmitJoin_Click(object sender, RoutedEventArgs e)
        {
            var code = StorageService.NormalizeRoomCode(TxtJoinRoomInput.Text);
            if (string.IsNullOrWhiteSpace(code) || code.Length < 3)
            {
                TxtJoinError.Text = "Lütfen geçerli bir oda kodu girin (Örn: RUNA-3038)!";
                UiAnimationHelper.ShakeElement(JoinErrorBanner);
                return;
            }

            var fallbackNick = _storage.IsTestProfile ? "TestHesap" : Environment.UserName;
            var nick = string.IsNullOrWhiteSpace(TxtJoinNickname.Text) ? fallbackNick : TxtJoinNickname.Text.Trim();
            _storage.CurrentUser.Name = nick;
            _storage.SaveSettings();

            var mySaved = _storage.GetPersistentRooms().FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (mySaved != null && mySaved.IsOwner)
            {
                await UiAnimationHelper.HideModalAnimatedAsync(JoinRoomModalOverlay, JoinModalDialog);
                await EnterVoiceRoomAsync(code, mySaved.Title, true);
                return;
            }

            BtnSubmitJoin.IsEnabled = false;
            TxtSubmitJoinLabel.Text = "Kontrol Ediliyor...";
            JoinErrorBanner.Visibility = Visibility.Collapsed;

            var checkRes = await _signaling.CheckRoomExistsAsync(code);

            if (!checkRes.Exists)
            {
                TxtJoinError.Text = $"Böyle bir oda mevcut değil ({code}) veya odada şu an aktif kimse yok!";
                UiAnimationHelper.ShakeElement(JoinErrorBanner);
                BtnSubmitJoin.IsEnabled = true;
                TxtSubmitJoinLabel.Text = "Odaya Katıl";
                return;
            }

            await UiAnimationHelper.HideModalAnimatedAsync(JoinRoomModalOverlay, JoinModalDialog);
            BtnSubmitJoin.IsEnabled = true;
            TxtSubmitJoinLabel.Text = "Odaya Katıl";
            // Odayı kuran kişiyle aynı sinyalleşme sunucusunda buluş (yedek sunucuya düşmüş olabilir)
            await _signaling.UseBrokerAsync(checkRes.BrokerIndex);
            await EnterVoiceRoomAsync(code, checkRes.RoomTitle, checkRes.IsPersistent, checkRes.RoomActivity);
        }

        private async void BtnJoinFromCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string code)
            {
                await JoinSavedRoomByCodeAsync(code);
            }
        }

        private async Task JoinSavedRoomByCodeAsync(string code)
        {
            var mySaved = _storage.GetPersistentRooms().FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (mySaved == null) return;

            if (!mySaved.IsOwner)
            {
                ShowToast($"{code} odası kontrol ediliyor...");
                var check = await _signaling.CheckRoomExistsAsync(code);
                if (!check.Exists)
                {
                    ShowToast($"⚠️ {mySaved.Title} odasında şu an kimse aktif değil.");
                    return;
                }
                await _signaling.UseBrokerAsync(check.BrokerIndex);
            }

            await EnterVoiceRoomAsync(code, mySaved.Title, true);
        }

        // =====================================================================
        // SESLİ ODA YAŞAM DÖNGÜSÜ VE KONTROLLER
        // =====================================================================

        private async Task EnterVoiceRoomAsync(string roomCode, string roomTitle, bool isPersistent, string initialActivity = "💬 Sohbet ediliyor")
        {
            try
            {
                await _signaling.StopWatchingPersistentRoomsAsync();

                _isInVoiceRoom = true;
                App.SetHighResolutionTimer(true);
                _activeRoomCode = roomCode;
                _activeRoomTitle = string.IsNullOrWhiteSpace(roomTitle) ? roomCode : roomTitle;

                LandingViewGrid.Visibility = Visibility.Collapsed;
                ScreenShareStage.Visibility = Visibility.Collapsed;
                DefaultVoiceStage.Visibility = Visibility.Visible;
                RemoteStreamPreviewCard.Visibility = Visibility.Collapsed;
                UiAnimationHelper.AnimateViewEntrance(VoiceStageGrid);

                TxtStageRoomTitle.Text = _activeRoomTitle;
                TxtStageRoomCodePill.Text = $"{roomCode} • Kopyala";
                TxtCurrentActivityPill.Text = initialActivity;
                UpdateStageSubtitleText();

                bool isPinned = _storage.IsRoomPersistent(roomCode);
                MainWindow.Current?.UpdateTitleBarRoomState(roomCode, isPinned);

                ActiveParticipants.Clear();
                ChatMessages.Clear();
                RoomChatEmptyHint.Visibility = Visibility.Visible;

                var selfParticipant = new RoomParticipant
                {
                    PeerKey = _storage.CurrentUser.PeerKey,
                    Name = _storage.CurrentUser.Name,
                    Avatar = !string.IsNullOrEmpty(_storage.CurrentUser.AvatarDataUri) ? _storage.CurrentUser.AvatarDataUri : _storage.CurrentUser.AvatarPath,
                    IsSelf = true,
                    IsMuted = _audio.IsMuted,
                    IsDeafened = _audio.IsDeafened,
                    IsScreenSharing = false
                };
                ActiveParticipants.Add(selfParticipant);
                UpdateParticipantCountsAndTree();

                _engine.StartRoomAudio();
                RefreshStageRippleState(force: true);
                EnsureTelemetryTimerState();
                _soundpad.PlayUiEffect(UiSoundEffect.RoomJoin);

                await _signaling.JoinRoomAsync(roomCode, _storage.CurrentUser, _activeRoomTitle, isPersistent, initialActivity);
                ShowToast($"🎙️ {_activeRoomTitle} odasına bağlandınız");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EnterVoiceRoomAsync] Error: {ex.Message}");
                ShowToast("⚠️ Odaya bağlanırken ağ hatası oluştu, yeniden deneniyor...");
            }
        }

        private bool _isLeavingRoom = false;

        private void BtnLeaveVoice_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInVoiceRoom) return;

            // Madde 9: Modern ayrilma modali - oda kodu cipi, aciklama, yayin uyarisi ve ikon animasyonu
            TxtLeaveRoomCodeChip.Text = _activeRoomCode;
            bool hasDistinctTitle = !string.IsNullOrEmpty(_activeRoomTitle) &&
                                    !_activeRoomTitle.Equals(_activeRoomCode, StringComparison.OrdinalIgnoreCase);
            if (hasDistinctTitle)
            {
                TxtLeaveRoomTitleText.Text = $"· {_activeRoomTitle}";
                TxtLeaveRoomTitleText.Visibility = Visibility.Visible;
            }
            else
            {
                TxtLeaveRoomTitleText.Visibility = Visibility.Collapsed;
            }
            TxtLeaveRoomDesc.Text = "Sesli bağlantınız ve odadaki etkileşiminiz sonlandırılacak.";
            LeaveRoomStreamWarningBox.Visibility = _screenCapture.IsSharing ? Visibility.Visible : Visibility.Collapsed;

            UiAnimationHelper.ShowModalAnimated(LeaveRoomConfirmOverlay, LeaveRoomConfirmCard);
            UiAnimationHelper.ShakeElement(IconLeaveRoomExit);
            BtnCancelLeaveRoom.Focus(FocusState.Programmatic);
        }

        private void BtnConfirmLeaveRoom_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
            }
        }

        private void BtnConfirmLeaveRoom_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
            }
        }

        private async void BtnCancelLeaveRoom_Click(object sender, RoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(LeaveRoomConfirmOverlay, LeaveRoomConfirmCard);
        }

        private async void BackdropCloseLeaveConfirm_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(LeaveRoomConfirmOverlay, LeaveRoomConfirmCard);
        }

        private async void BtnConfirmLeaveRoom_Click(object sender, RoutedEventArgs e)
        {
            if (_isLeavingRoom) return;
            _isLeavingRoom = true;
            try
            {
                await UiAnimationHelper.HideModalAnimatedAsync(LeaveRoomConfirmOverlay, LeaveRoomConfirmCard);
                await LeaveVoiceRoomAsync();
            }
            finally
            {
                _isLeavingRoom = false;
            }
        }

        public async Task LeaveVoiceRoomAsync()
        {
            if (!_isInVoiceRoom) return;
            _isInVoiceRoom = false;
            _lastRippleSpeakingState = null;
            App.SetHighResolutionTimer(false);

            try
            {
                if (MainWindow.Current?.IsFullscreen == true)
                {
                    ExitStreamFullscreenMode();
                }

                await _gameStream.CloseActiveSessionAsync("left_room");
                _gameStream.ResetRoomPresence();
                _screenCapture.StopCapture();
                _engine.CloseAll();
                _soundpad.PlayUiEffect(UiSoundEffect.RoomLeave);

                await _signaling.LeaveRoomAsync();
                await Task.Delay(140);
                _engine.StopRoomAudio();
                _audio.StopRoomAudio();
            }
            catch { }

            _activeRoomCode = "";
            _activeRoomTitle = "";
            MainWindow.Current?.UpdateTitleBarRoomState(null, false);
            EnsureTelemetryTimerState();

            VoiceUserPopoverOverlay.Visibility = Visibility.Collapsed;
            SoundpadModalOverlay.Visibility = Visibility.Collapsed;
            VoiceStageGrid.Visibility = Visibility.Collapsed;
            UiAnimationHelper.AnimateViewEntrance(LandingViewGrid);

            LoadPersistentRooms();
        }

        private void UpdateStageSubtitleText()
        {
            string engineLabel = _storage.Settings.NoiseEngine switch
            {
                "rnnoise" => "RNNoise AI Aktif",
                "deep"    => "Derin Hibrit AI Aktif",
                "speex"   => "Hafif DSP Aktif",
                _         => "Saf Stüdyo Modu"
            };
            TxtStageRoomSubtitle.Text = $"Şifreli P2P Ses Kanalı • {engineLabel} • {_storage.Settings.OpusBitrateKbps} kbps Opus";
        }

        private void UpdateParticipantCountsAndTree()
        {
            int count = ActiveParticipants.Count;
            TxtParticipantsCount.Text = $"({count})";
            TxtStageCapacityPill.Text = $"{count} / 12";
            RefreshP2PRelayTreeLayout();
            UpdateSelfRowPingSummary();
        }

        private void UpdateSelfRowPingSummary()
        {
            var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
            if (me == null) return;

            var peersWithValidPing = ActiveParticipants
                .Where(p => !p.IsSelf && p.PingStatus == Models.PeerPingStatus.Measured && p.LatencyRttMs > 0)
                .ToList();

            if (peersWithValidPing.Count > 0)
            {
                int avg = (int)Math.Round(peersWithValidPing.Average(p => p.LatencyRttMs));
                me.UpdateSelfPeerStats(avg, peersWithValidPing.Count);
            }
            else
            {
                me.UpdateSelfPeerStats(0, 0);
            }
        }

        private async void BtnMuteToggle_Click(object sender, RoutedEventArgs e)
        {
            _audio.IsMuted = !_audio.IsMuted;
            _storage.CurrentUser.IsMuted = _audio.IsMuted;

            IconMicOpen.Visibility = _audio.IsMuted ? Visibility.Collapsed : Visibility.Visible;
            IconMicMuted.Visibility = _audio.IsMuted ? Visibility.Visible : Visibility.Collapsed;
            BtnMuteToggle.Background = _audio.IsMuted
                ? new SolidColorBrush(Color.FromArgb(255, 255, 92, 110))
                : new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

            var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
            if (me != null) me.IsMuted = _audio.IsMuted;

            _soundpad.PlayUiEffect(_audio.IsMuted ? UiSoundEffect.Mute : UiSoundEffect.Unmute);
            if (_isInVoiceRoom) await _signaling.PublishPeerUpdateAsync();
        }

        private async void BtnDeafenToggle_Click(object sender, RoutedEventArgs e)
        {
            _audio.IsDeafened = !_audio.IsDeafened;
            _storage.CurrentUser.IsDeafened = _audio.IsDeafened;

            var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);

            if (_audio.IsDeafened)
            {
                // Sağırlaştırıldığında mikrofon da otomatik kapatılır ve her iki ikon da kırmızıya döner
                _audio.IsMuted = true;
                _storage.CurrentUser.IsMuted = true;

                IconHeadphoneOpen.Visibility = Visibility.Collapsed;
                IconHeadphoneDeafened.Visibility = Visibility.Visible;
                BtnDeafenToggle.Background = new SolidColorBrush(Color.FromArgb(255, 255, 92, 110));

                IconMicOpen.Visibility = Visibility.Collapsed;
                IconMicMuted.Visibility = Visibility.Visible;
                BtnMuteToggle.Background = new SolidColorBrush(Color.FromArgb(255, 255, 92, 110));

                if (me != null)
                {
                    me.IsDeafened = true;
                    me.IsMuted = true;
                }

                _soundpad.PlayUiEffect(UiSoundEffect.Deafen);
            }
            else
            {
                // Sağırlaştırma kaldırıldığında mikrofon da açılır
                _audio.IsMuted = false;
                _storage.CurrentUser.IsMuted = false;

                IconHeadphoneOpen.Visibility = Visibility.Visible;
                IconHeadphoneDeafened.Visibility = Visibility.Collapsed;
                BtnDeafenToggle.Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

                IconMicOpen.Visibility = Visibility.Visible;
                IconMicMuted.Visibility = Visibility.Collapsed;
                BtnMuteToggle.Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

                if (me != null)
                {
                    me.IsDeafened = false;
                    me.IsMuted = false;
                }

                _soundpad.PlayUiEffect(UiSoundEffect.Undeafen);
            }

            if (_isInVoiceRoom) await _signaling.PublishPeerUpdateAsync();
        }

        private void BtnParticipantOptions_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is RoomParticipant p)
            {
                OpenUserAudioPopover(p);
            }
        }

        private async void ActivityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string activity)
            {
                TxtCurrentActivityPill.Text = activity;
                if (_isInVoiceRoom)
                {
                    await _signaling.SetRoomActivityAsync(activity);
                    ShowToast($"Oda durumu güncellendi: {activity}");
                }
            }
        }

        private void BtnStageCopyRoomCode_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeRoomCode)) return;
            CopyTextToClipboard(_activeRoomCode);
            ShowToast($"📋 Oda kodu kopyalandı: {_activeRoomCode}");
        }

        private void BtnStagePinRoom_Click(object sender, RoutedEventArgs e)
        {
            TogglePinCurrentRoom();
        }

        private void TogglePinCurrentRoom()
        {
            if (string.IsNullOrEmpty(_activeRoomCode)) return;

            if (_storage.IsRoomPersistent(_activeRoomCode))
            {
                _storage.RemovePersistentRoom(_activeRoomCode);
                MainWindow.Current?.UpdatePinVisualState(false);
                ShowToast("Oda kalıcı odalardan çıkarıldı");
            }
            else
            {
                _storage.SavePersistentRoom(_activeRoomCode, _activeRoomTitle, isOwner: true);
                MainWindow.Current?.UpdatePinVisualState(true);
                ShowToast($"⭐ {_activeRoomTitle} kalıcı odalara sabitlendi");
            }
        }

        // =====================================================================
        // KİŞİYE ÖZEL %0-%200 SES YÜKSELTİCİ (AUDIO BOOSTER) POP-UP
        // =====================================================================

        private void ApplySavedPeerAudioConfigToParticipant(RoomParticipant p)
        {
            if (p.IsSelf) return;
            var cfg = _storage.GetPeerAudioConfig(p.Name);
            p.VolumePercent = cfg.VolumePercent;
            p.IsLocalMuted = cfg.LocalMuted;
            p.ClarityBoost = cfg.ClarityBoost;
        }

        private static bool IsFromInteractiveChild(DependencyObject? src, DependencyObject? cardRoot)
        {
            var cur = src;
            while (cur != null && cur != cardRoot)
            {
                if (cur is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase ||
                    cur is ToggleSwitch ||
                    cur is Slider)
                {
                    return true;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void ParticipantCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                if (IsFromInteractiveChild(e.OriginalSource as DependencyObject, fe)) return;
                if (fe.Tag is RoomParticipant p)
                {
                    OpenUserAudioPopover(p);
                }
            }
        }

        private void ParticipantCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                if (IsFromInteractiveChild(e.OriginalSource as DependencyObject, fe)) return;
                if (fe.Tag is RoomParticipant p)
                {
                    e.Handled = true;
                    OpenUserAudioPopover(p);
                }
            }
        }

        private void OpenUserAudioPopover(RoomParticipant participant)
        {
            if (participant.IsSelf)
            {
                OpenSettingsModal();
                return;
            }

            _popoverTargetParticipant = participant;
            var cfg = _storage.GetPeerAudioConfig(participant.Name);

            _suppressPopoverEvents = true;
            TxtPopoverUserName.Text = participant.Name;
            PopoverAvatarBrush.ImageSource = participant.AvatarBitmap;
            SliderPopoverUserVolume.Value = cfg.VolumePercent;
            TglPopoverLocalMute.IsOn = cfg.LocalMuted;
            TglPopoverClarityBoost.IsOn = cfg.ClarityBoost;
            TglPopoverLevelingIncluded.IsOn = !cfg.ExcludeFromLeveling;
            float gain = _audio.GetPeerLevelingGainDb(participant.VoiceSessionId);
            TxtPopoverLevelingGain.Text = $"Dengeleme Kazancı: {(gain >= 0 ? "+" : "")}{gain:F1} dB";
            TxtPopoverSpatialInfo.Text = cfg.HasManualSpatialPosition
                ? $"{cfg.SpatialAzimuthDeg:0}° · {(cfg.SpatialDistance / 100f):0.0}m"
                : "Otomatik / Merkez";
            UpdatePopoverVolumeBadgeVisual(cfg.VolumePercent);
            RefreshPopoverLatency(participant);
            _suppressPopoverEvents = false;

            UiAnimationHelper.ShowModalAnimated(VoiceUserPopoverOverlay, VoiceUserPopoverCard);
        }

        // Ses gecikmesi dökümü renkleri (çubuk ve açıklama aynı sırada)
        private static readonly (string Label, Windows.UI.Color Color)[] LatencySegments =
        {
            ("Mikrofon tamponu", Color.FromArgb(255, 100, 116, 139)),
            ("Paketleme (çerçeve)", Color.FromArgb(255, 245, 158, 11)),
            ("Opus kodlama", Color.FromArgb(255, 168, 85, 247)),
            ("Ağ (tek yön)", Color.FromArgb(255, 56, 189, 248)),
            ("Alış tamponu", Color.FromArgb(255, 34, 197, 94)),
            ("Opus çözme", Color.FromArgb(255, 236, 72, 153)),
            ("Hoparlör tamponu", Color.FromArgb(255, 148, 163, 184)),
        };

        /// <summary>
        /// Kullanıcı penceresindeki ağ ve ses gecikmesi kartlarını ve dökümünü günceller.
        /// Ölçülemeyen parçalar "—" gösterilir; uydurma değer yazılmaz.
        /// </summary>
        private void RefreshPopoverLatency(RoomParticipant p)
        {
            var info = p.LatencyInfo;

            // Bağlantı çipi
            (string chip, Windows.UI.Color dot) = p.PingStatus switch
            {
                Models.PeerPingStatus.Measured when info?.IsUdpPath == true => ("P2P bağlı · UDP", Color.FromArgb(255, 34, 197, 94)),
                Models.PeerPingStatus.Measured => ("P2P bağlı · veri kanalı", Color.FromArgb(255, 34, 197, 94)),
                Models.PeerPingStatus.Measuring => ("Ölçülüyor…", Color.FromArgb(255, 148, 163, 184)),
                Models.PeerPingStatus.Timeout => ("Zaman aşımı", Color.FromArgb(255, 239, 68, 68)),
                Models.PeerPingStatus.NoP2p => ("P2P kurulamadı", Color.FromArgb(255, 245, 158, 11)),
                _ => ("Bağlanıyor…", Color.FromArgb(255, 148, 163, 184))
            };
            TxtPopoverConnChip.Text = chip;
            PopoverConnDot.Fill = new SolidColorBrush(dot);

            // Ağ gecikmesi kartı
            if (info != null)
            {
                TxtPopoverNetRtt.Text = Models.PeerLatencyInfo.FormatMs(info.NetworkRttMs);
                TxtPopoverNetJitter.Text = info.NetworkJitterMs >= 0 ? $"Dalgalanma ± {Models.PeerLatencyInfo.FormatMs(info.NetworkJitterMs)}" : "";
            }
            else if (p.PingStatus == Models.PeerPingStatus.Measured && p.LatencyRttMs > 0)
            {
                // Karşı taraf eski sürüm: yalnızca veri kanalı ölçümü var
                TxtPopoverNetRtt.Text = $"~{p.LatencyRttMs} ms";
                TxtPopoverNetJitter.Text = "Veri kanalı ölçümü (karşı taraf eski sürüm)";
            }
            else
            {
                TxtPopoverNetRtt.Text = "—";
                TxtPopoverNetJitter.Text = "";
            }

            // Ses gecikmesi kartı ve dökümü
            double[] parts = info == null
                ? new double[] { -1, -1, -1, -1, -1, -1, -1 }
                : new[]
                {
                    info.PeerCaptureBufferMs,
                    info.PeerFrameMs,
                    (info.PeerLookaheadMs >= 0 && info.PeerEncodeMs >= 0) ? info.PeerLookaheadMs + info.PeerEncodeMs : -1,
                    info.NetworkOneWayMs,
                    info.JitterBufferMs,
                    info.LocalDecodeMs,
                    info.PlaybackBufferMs
                };

            double total = info?.AudioOneWayMs ?? -1;
            TxtPopoverAudioLatency.Text = total >= 0 ? Models.PeerLatencyInfo.FormatMs(total) : "—";
            double codec = info?.CodecTotalMs ?? -1;
            TxtPopoverCodecShare.Text = codec >= 0 ? $"Sıkıştırma + çözme: {Models.PeerLatencyInfo.FormatMs(codec)}" : "";

            // Yığılmış çubuk
            PopoverLatencyBar.Children.Clear();
            PopoverLatencyBar.ColumnDefinitions.Clear();
            if (total > 0)
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    PopoverLatencyBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(parts[i], 0.0001), GridUnitType.Star) });
                    var seg = new Border { Background = new SolidColorBrush(LatencySegments[i].Color) };
                    Grid.SetColumn(seg, i);
                    PopoverLatencyBar.Children.Add(seg);
                }
            }

            // Açıklama: iki sütun
            PopoverLatencyLegend.Children.Clear();
            PopoverLatencyLegend.ColumnDefinitions.Clear();
            PopoverLatencyLegend.RowDefinitions.Clear();
            PopoverLatencyLegend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PopoverLatencyLegend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = (parts.Length + 1) / 2;
            for (int r = 0; r < rows; r++) PopoverLatencyLegend.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < parts.Length; i++)
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dotEl = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(LatencySegments[i].Color), VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock { Text = LatencySegments[i].Label, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)), TextTrimming = TextTrimming.CharacterEllipsis };
                var val = new TextBlock { Text = Models.PeerLatencyInfo.FormatMs(parts[i]), FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240)) };
                Grid.SetColumn(label, 1);
                Grid.SetColumn(val, 2);
                row.Children.Add(dotEl);
                row.Children.Add(label);
                row.Children.Add(val);
                Grid.SetRow(row, i / 2);
                Grid.SetColumn(row, i % 2);
                PopoverLatencyLegend.Children.Add(row);
            }

            // Eksik ölçüm açıklaması
            string note = "";
            if (info == null)
                note = p.PingStatus == Models.PeerPingStatus.Measured
                    ? "Karşı taraf eski bir RUNA sürümü kullanıyor; ses gecikmesi dökümü hesaplanamıyor."
                    : "Bağlantı kurulunca ölçüm başlar.";
            else if (info.JitterBufferMs < 0 || info.LocalDecodeMs < 0)
                note = "Alış tamponu ve çözme süresi, bu kişiden ses geldikçe ölçülür (konuşmaya başlayınca dolar).";
            else if (info.PeerEncodeMs < 0)
                note = "Kodlama süresi, karşı taraf mikrofonu açıp konuşunca ölçülür.";
            if (note.Length == 0 && info != null && info.JitterTargetMs >= 0)
                note = $"Alış tamponu güvenlik payı: {Models.PeerLatencyInfo.FormatMs(info.JitterTargetMs)} · kesinti: {info.Underruns}";
            if (info != null && !string.IsNullOrEmpty(info.PlaybackMode))
            {
                string pb = $"Hoparlör: {info.PlaybackMode}" + (info.PlaybackGlitches >= 0 ? $" · takılma: {info.PlaybackGlitches}" : "");
                note = note.Length > 0 ? note + "\n" + pb : pb;
            }
            TxtPopoverLatencyNote.Text = note;
            TxtPopoverLatencyNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (TxtPopoverEngineNote != null)
            {
                TxtPopoverEngineNote.Text = _engine is RustRealtimeEngine ? "Motor: Rust" : "Motor: Standart";
            }
        }

        private void UpdatePopoverVolumeBadgeVisual(int vol)
        {
            TxtPopoverVolumeBadge.Text = $"%{vol}";
            TxtPopoverBoostHint.Visibility = vol > 100 ? Visibility.Visible : Visibility.Collapsed;
            PopoverVolBadgeBorder.Background = vol > 100
                ? new SolidColorBrush(Color.FromArgb(65, 245, 158, 11))
                : new SolidColorBrush(Color.FromArgb(40, 139, 92, 246));
        }

        private void SliderPopoverUserVolume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressPopoverEvents || _popoverTargetParticipant == null) return;
            int vol = (int)Math.Round(e.NewValue);
            ApplyPopoverChanges(vol, vol == 0 ? TglPopoverLocalMute.IsOn : false, TglPopoverClarityBoost.IsOn);
            if (vol > 0 && TglPopoverLocalMute.IsOn)
            {
                _suppressPopoverEvents = true;
                TglPopoverLocalMute.IsOn = false;
                _suppressPopoverEvents = false;
            }
        }

        private void BtnVolumePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && int.TryParse(tag, out int presetVal))
            {
                SliderPopoverUserVolume.Value = presetVal;
            }
        }

        private void TglPopoverLocalMute_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressPopoverEvents || _popoverTargetParticipant == null) return;
            ApplyPopoverChanges((int)Math.Round(SliderPopoverUserVolume.Value), TglPopoverLocalMute.IsOn, TglPopoverClarityBoost.IsOn);
        }

        private void TglPopoverClarityBoost_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressPopoverEvents || _popoverTargetParticipant == null) return;
            ApplyPopoverChanges((int)Math.Round(SliderPopoverUserVolume.Value), TglPopoverLocalMute.IsOn, TglPopoverClarityBoost.IsOn);
        }

        private void TglPopoverLevelingIncluded_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressPopoverEvents || _popoverTargetParticipant == null) return;
            ApplyPopoverChanges((int)Math.Round(SliderPopoverUserVolume.Value), TglPopoverLocalMute.IsOn, TglPopoverClarityBoost.IsOn);
        }

        private async void BtnPopoverOpenSpatial_Click(object sender, RoutedEventArgs e)
        {
            if (_popoverTargetParticipant == null) return;
            string targetName = _popoverTargetParticipant.Name;
            await UiAnimationHelper.HideModalAnimatedAsync(VoiceUserPopoverOverlay, VoiceUserPopoverCard);
            OpenSpatialAudioModalForPeer(targetName);
        }

        private void ApplyPopoverChanges(int vol, bool localMuted, bool clarity)
        {
            if (_popoverTargetParticipant == null) return;

            var cfg = _storage.GetPeerAudioConfig(_popoverTargetParticipant.Name);
            cfg.VolumePercent = Math.Clamp(vol, 0, 200);
            cfg.LocalMuted = localMuted;
            cfg.ClarityBoost = clarity;
            cfg.ExcludeFromLeveling = !TglPopoverLevelingIncluded.IsOn;

            _popoverTargetParticipant.VolumePercent = cfg.VolumePercent;
            _popoverTargetParticipant.IsLocalMuted = cfg.LocalMuted;
            _popoverTargetParticipant.ClarityBoost = cfg.ClarityBoost;

            UpdatePopoverVolumeBadgeVisual(cfg.VolumePercent);
            _storage.SavePeerAudioConfig(_popoverTargetParticipant.Name, cfg);
            _audio.UpdatePeerAudioSettings(_popoverTargetParticipant.Name, cfg);
        }

        private async void BtnCloseUserPopover_Click(object sender, RoutedEventArgs e) =>
            await UiAnimationHelper.HideModalAnimatedAsync(VoiceUserPopoverOverlay, VoiceUserPopoverCard);

        private async void BackdropClosePopover_Tapped(object sender, TappedRoutedEventArgs e) =>
            await UiAnimationHelper.HideModalAnimatedAsync(VoiceUserPopoverOverlay, VoiceUserPopoverCard);

        // =====================================================================
        // SES PANELİ (SOUNDPAD)
        // =====================================================================

        private void RefreshSoundpadList(string? query = null)
        {
            SoundpadItems.Clear();
            foreach (var item in _soundpad.GetAllSounds(query))
            {
                SoundpadItems.Add(item);
            }
        }

        private async void BtnSoundpadToggle_Click(object sender, RoutedEventArgs e)
        {
            if (SoundpadModalOverlay.Visibility == Visibility.Visible)
            {
                await UiAnimationHelper.HideModalAnimatedAsync(SoundpadModalOverlay, SoundpadModalCard);
                return;
            }

            SliderSoundpadVol.Value = _storage.Settings.SoundpadVolumePercent;
            ChkSoundpadBroadcast.IsChecked = _storage.Settings.SoundpadBroadcastToRoom;
            RefreshSoundpadList(TxtSoundpadSearch.Text);
            UiAnimationHelper.ShowModalAnimated(SoundpadModalOverlay, SoundpadModalCard);
        }

        private void TxtSoundpadSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady || TxtSoundpadSearch == null) return;
            RefreshSoundpadList(TxtSoundpadSearch.Text);
        }

        private void BtnPlaySoundpadItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is CustomSoundItem item)
            {
                if (_soundpad.PlaySoundpadItem(item))
                {
                    ShowToast($"{item.Emoji} {item.Title} çalınıyor");
                }
            }
        }

        private async void BtnAddCustomSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MainWindow.Current == null) return;
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".ogg");
                picker.FileTypeFilter.Add(".m4a");

                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(MainWindow.Current));
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    var added = _storage.ImportSoundpadClip(file.Path);
                    if (added != null)
                    {
                        RefreshSoundpadList(TxtSoundpadSearch.Text);
                        ShowToast($"🔊 '{added.Title}' ses paneline eklendi");
                    }
                }
            }
            catch { }
        }

        private void BtnStopAllSounds_Click(object sender, RoutedEventArgs e) => _soundpad.StopAll();
        private async void BtnCloseSoundpad_Click(object sender, RoutedEventArgs e) =>
            await UiAnimationHelper.HideModalAnimatedAsync(SoundpadModalOverlay, SoundpadModalCard);
        private async void BackdropCloseSoundpad_Tapped(object sender, TappedRoutedEventArgs e) =>
            await UiAnimationHelper.HideModalAnimatedAsync(SoundpadModalOverlay, SoundpadModalCard);

        private void SliderSoundpadVol_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || TxtSoundpadVolLabel == null) return;
            int v = (int)Math.Round(e.NewValue);
            TxtSoundpadVolLabel.Text = $"Ses Paneli Düzeyi: %{v}";
            _storage.Settings.SoundpadVolumePercent = v;
        }

        private void ChkSoundpadBroadcast_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || ChkSoundpadBroadcast == null) return;
            _storage.Settings.SoundpadBroadcastToRoom = ChkSoundpadBroadcast.IsChecked == true;
        }

        // =====================================================================
        // P2P GEÇİCİ METİN SOHBETİ
        // =====================================================================

        private async void BtnSendChat_Click(object sender, RoutedEventArgs e)
        {
            var text = TxtChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text) || !_isInVoiceRoom) return;
            if (text.Length > ChatMessage.MaxWireTextLength) text = text[..ChatMessage.MaxWireTextLength];
            TxtChatInput.Text = "";

            // Madde 6: Gönderen tarafında önizleme üretimi (en fazla 1.5 sn zaman aşımı)
            LinkPreviewData? preview = null;
            try
            {
                preview = await LinkPreviewHelper.GeneratePreviewAsync(text);
            }
            catch { }

            var msg = new ChatMessage
            {
                SenderName = $"{_storage.CurrentUser.Name} (Sen)",
                SenderAvatar = _storage.CurrentUser.AvatarPath,
                Text = text,
                TimeString = DateTime.Now.ToString("HH:mm"),
                IsMe = true,
                LinkPreview = preview
            };

            AddChatMessageBounded(msg);
            RoomChatEmptyHint.Visibility = Visibility.Collapsed;
            ChatMessagesList.ScrollIntoView(msg);

            var wireCopy = new ChatMessage
            {
                Id = msg.Id,
                SenderName = _storage.CurrentUser.Name,
                SenderAvatar = SignalingService.GetShareableAvatar(_storage.CurrentUser),
                Text = text,
                TimeString = msg.TimeString,
                Timestamp = msg.Timestamp,
                IsMe = false,
                LinkPreview = preview
            };

            _engine.BroadcastP2PChatMessage(wireCopy);
            await _signaling.SendChatMessageAsync(msg);
        }

        private void BtnChatLinkPreviewCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ChatMessage msg && msg.LinkPreview != null)
            {
                _ = LinkPreviewHelper.SafeOpenUrlAsync(msg.LinkPreview.Url, msg.LinkPreview.Url, fe.XamlRoot);
            }
        }

        private const int MaxVisibleChatMessages = 400;

        /// <summary>
        /// Sohbet listesine ekler; uzun oturumlarda listenin (ve her öğenin avatar/fırça nesnelerinin)
        /// sınırsız büyümesini önlemek için en eski mesajlar atılır.
        /// </summary>
        private void AddChatMessageBounded(ChatMessage msg)
        {
            ChatMessages.Add(msg);
            while (ChatMessages.Count > MaxVisibleChatMessages)
            {
                ChatMessages.RemoveAt(0);
            }
        }

        private void TxtChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                BtnSendChat_Click(sender, e);
            }
        }

        // =====================================================================
        // KLAVYE KISAYOLLARI, BAS-KONUŞ (PTT) VE TOAST BİLDİRİMLERİ
        // =====================================================================

        private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_isCapturingPttKey)
            {
                CaptureNewPttKey(e.Key);
                e.Handled = true;
                return;
            }

            if ((int)e.Key == _storage.Settings.PttVirtualKey)
            {
                _audio.IsPttKeyHeld = true;
            }

            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrl && shift && e.Key == VirtualKey.M && _isInVoiceRoom)
            {
                BtnMuteToggle_Click(sender, e);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == VirtualKey.D && _isInVoiceRoom)
            {
                BtnDeafenToggle_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.F11 && ScreenShareStage.Visibility == Visibility.Visible)
            {
                ToggleStreamFullscreenMode();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter && PrivacyOffWarningOverlay.Visibility == Visibility.Visible)
            {
                BtnPrivacyKeepOn_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                if (LeaveRoomConfirmOverlay.Visibility == Visibility.Visible)
                {
                    await UiAnimationHelper.HideModalAnimatedAsync(LeaveRoomConfirmOverlay, LeaveRoomConfirmCard);
                    e.Handled = true;
                }
                else if (_annDrawMode)
                {
                    ExitAnnotationMode();
                    e.Handled = true;
                }
                else if (MainWindow.Current?.IsFullscreen == true) ExitStreamFullscreenMode();
                else if (PrivacyOffWarningOverlay.Visibility == Visibility.Visible)
                {
                    BtnPrivacyKeepOn_Click(sender, e);
                    e.Handled = true;
                }
                else if (GameSelectModalOverlay.Visibility == Visibility.Visible)
                {
                    await UiAnimationHelper.HideModalAnimatedAsync(GameSelectModalOverlay, GameSelectModalCard);
                    _selectedTargetPeer = null;
                    e.Handled = true;
                }
                else if (ThirdPartyLicensesModalOverlay.Visibility == Visibility.Visible)
                {
                    await UiAnimationHelper.HideModalAnimatedAsync(ThirdPartyLicensesModalOverlay, ThirdPartyLicensesCard);
                    e.Handled = true;
                }
                else if (GameStreamModalOverlay.Visibility == Visibility.Visible)
                {
                    await CloseGameStreamModalAsync();
                    e.Handled = true;
                }
                else if (SettingsModalOverlay.Visibility == Visibility.Visible) CloseSettingsModal();
                else if (ScreenPickerModalOverlay.Visibility == Visibility.Visible) await CloseScreenPickerModalAsync();
                else if (VoiceUserPopoverOverlay.Visibility == Visibility.Visible) await UiAnimationHelper.HideModalAnimatedAsync(VoiceUserPopoverOverlay, VoiceUserPopoverCard);
                else if (SoundpadModalOverlay.Visibility == Visibility.Visible) await UiAnimationHelper.HideModalAnimatedAsync(SoundpadModalOverlay, SoundpadModalCard);
                else await CloseHostAndJoinModalsAsync();
            }
        }

        private void Page_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if ((int)e.Key == _storage.Settings.PttVirtualKey)
            {
                _audio.IsPttKeyHeld = false;
            }
        }

        private static void CopyTextToClipboard(string text)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(text);
                Clipboard.SetContent(pkg);
            }
            catch { }
        }

        private void InitializeStaticBitmaps()
        {
            HeroRunaLogoBrush.ImageSource = AvatarHelper.LoadAvatarSource("Assets/runa-logo.png", 256);
            var spSrc = AvatarHelper.LoadAvatarSource("Assets/soundpad-icon.jpg", 96);
            SoundpadModalHeaderBrush.ImageSource = spSrc;
        }

        public async Task ShutdownAllServicesAsync()
        {
            try
            {
                _annOverlay?.Dispose();
                _annOverlay = null;
                _telemetryTimer?.Stop();
                _engine.Dispose();
                _gameStream.Dispose();
                _screenCapture.Dispose();
                _webrtc.Dispose();
                _audio.Dispose();
                await _signaling.LeaveRoomAsync();
                _signaling.Dispose();
            }
            catch { }
        }

        private async void ShowToast(string message)
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            TxtToastMessage.Text = message;
            UiAnimationHelper.AnimateViewEntrance(ToastBanner);

            try
            {
                await Task.Delay(2600, token);
                ToastBanner.Visibility = Visibility.Collapsed;
            }
            catch { }
        }
    }
}
