using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using RunaVoice_WinUI.Services.Engine;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace RunaVoice_WinUI
{
    public sealed partial class MainPage
    {
        private WriteableBitmap? _videoWriteableBitmap;
        private int _isRenderingVideoFrame = 0;
        private volatile bool _isAppWindowVisible = true;
        private int _lastRenderedVideoW = 0;
        private int _lastRenderedVideoH = 0;
        private bool _isWatchingRemoteStream = false;
        private bool _suppressSettingsEvents = false;
        private bool _isCapturingPttKey = false;
        private List<ScreenCaptureSourceItem> _allSources = new();
        private DispatcherTimer? _fullscreenIdleTimer;
        private DispatcherTimer? _streamToastTimer;
        private AdaptiveStreamController? _adaptiveStream;
        private DispatcherTimer? _adaptiveLoopTimer;

        // =====================================================================
        // CANLI EKRAN PAYLAŞIMI, P2P HİYERARŞİK AĞAÇ VE ÖZEL EKRAN SEÇİCİ
        // =====================================================================

        private void WireScreenShareEvents()
        {
            // Ayarlar penceresinin akordiyon kategori menüsünü kur ve kontrol butonlarına GPU hover animasyonları ekle
            BuildSettingsNav();
            UiAnimationHelper.AttachInteractiveHover(BtnRecordMicTest, 1.03f, -1f, 0.96f);
            UiAnimationHelper.AttachInteractiveHover(BtnAssignPttKey, 1.03f, -1f, 0.96f);

            _adaptiveStream = new AdaptiveStreamController(_screenCapture, _webrtc, _storage);
            _adaptiveStream.OnStateUpdated += info =>
            {
                DispatcherQueue.TryEnqueue(() => UpdateAdaptiveStreamUI(info));
            };

            _adaptiveLoopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _adaptiveLoopTimer.Tick += (_, _) =>
            {
                if (_screenCapture.IsSharing)
                {
                    _adaptiveStream?.TickOneSecondOuterLoop();
                }
            };

            _webrtc.OnVideoQualityState += state =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isWatchingRemoteStream)
                    {
                        TxtStreamSpecBadge.Text = state.Adaptive
                            ? $"{state.H}p • {state.Fps} FPS · Otomatik"
                            : $"{state.H}p • {state.Fps} FPS";
                    }
                });
            };

            // Ağ geri-basınç kapısı: WebRTC önceki kareyi hâlâ gönderiyorsa yeni kare yakalayıp CPU harcama!
            _screenCapture.ShouldSkipFrameGate = () => _webrtc.IsVideoSenderBusy;
            // Bağlı izleyici yokken tam hızda yakalama/kodlama yapma (yalnızca saniyede bir kare)
            _screenCapture.HasAudience = () => _webrtc.HasConnectedPeers;

            // Pencere simge durumundayken izlenen yayının karelerini çözmek boşa CPU harcar
            if (MainWindow.Current != null)
            {
                MainWindow.Current.VisibilityChanged += (_, args) => _isAppWindowVisible = args.Visible;
            }

            // Yerel yakalama başladığında
            _screenCapture.OnScreenShareStarted += (targetTitle, w, h, fps) =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    _isWatchingRemoteStream = false;
                    _storage.CurrentUser.IsScreenSharing = true;

                    var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
                    if (me != null) me.IsScreenSharing = true;

                    DefaultVoiceStage.Visibility = Visibility.Collapsed;
                    UiAnimationHelper.AnimateViewEntrance(ScreenShareStage);
                    BroadcasterActiveCard.Visibility = Visibility.Visible;
                    StreamWatchCard.Visibility = Visibility.Collapsed;

                    TxtBroadcasterActiveTitle.Text = $"Ekranınız Aktarılıyor: {targetTitle}";
                    TxtBroadcasterSpec.Text = $"{h}p • {fps} FPS";
                    TxtBroadcasterBitrate.Text = $"{_screenCapture.CurrentTargetBitrateKbps / 1000.0:F1} Mbps";
                    TxtScreenShareTitle.Text = $"Yayında: {targetTitle}";
                    TxtStreamSpecBadge.Text = $"{h}p • {fps} FPS";

                    BtnScreenShareToggle.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 124, 58, 237));
                    ToolTipService.SetToolTip(BtnScreenShareToggle, "Yayını Düzenle");
                    RefreshP2PRelayTreeLayout();
                    EnsureTelemetryTimerState();

                    _webrtc.BroadcastStreamStarted(targetTitle);
                    StartBroadcasterAnnotations();
                    if (_isInVoiceRoom) await _signaling.PublishPeerUpdateAsync();
                });
            };

            // Yerel yakalama durduğunda
            _screenCapture.OnScreenShareStopped += () =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    _storage.CurrentUser.IsScreenSharing = false;
                    var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
                    if (me != null) me.IsScreenSharing = false;

                    StopBroadcasterAnnotations();
                    _adaptiveLoopTimer?.Stop();
                    _adaptiveStream?.Stop();
                    BadgeBroadcasterAdaptive.Visibility = Visibility.Collapsed;
                    BroadcasterActiveCard.Visibility = Visibility.Collapsed;
                    BtnScreenShareToggle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    ToolTipService.SetToolTip(BtnScreenShareToggle, "Ekran veya Uygulama Penceresi Paylaş");

                    if (MainWindow.Current?.IsFullscreen == true) ExitStreamFullscreenMode();

                    if (!_isWatchingRemoteStream)
                    {
                        ScreenShareStage.Visibility = Visibility.Collapsed;
                        UiAnimationHelper.AnimateViewEntrance(DefaultVoiceStage);
                    }

                    BtnScreenShareToggle.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 255, 255));
                    RefreshP2PRelayTreeLayout();
                    EnsureTelemetryTimerState();

                    _webrtc.BroadcastStreamStopped();
                    if (_isInVoiceRoom) await _signaling.PublishPeerUpdateAsync();
                });
            };

            // Yerel kodlanmış kare hazır olduğunda -> WebRTC UDP/SRTP ile tam FPS gönder; yayıncı CPU tasarrufu için yerel çözme yapma!
            _screenCapture.FrameIdGenerator = () => _webrtc.GetNextVideoFrameId();
            _screenCapture.OnEncodedFrameReadyWithId += (jpegBytes, w, h, frameId) =>
            {
                _webrtc.BroadcastEncodedVideoFrame(jpegBytes, w, h, frameId);
            };

            // Küçük kapak resmi hazır olduğunda -> odadaki izleyicilerin "Yayını İzle" kartına gönder
            _screenCapture.OnPreviewThumbnailReady += thumbBytes =>
            {
                _webrtc.BroadcastStreamThumbnail(thumbBytes);
            };

            // Karşı tarafta bir kullanıcı yayını başlattığında / durdurduğunda
            _webrtc.OnWatcherStateChanged += (peerKey, isWatching) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RefreshP2PRelayTreeLayout();
                });
            };

            _webrtc.OnRemoteStreamStateChanged += (broadcasterKey, broadcasterName, thumbBytes) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isInVoiceRoom) return;

                    if (string.IsNullOrEmpty(broadcasterKey))
                    {
                        RemoteStreamPreviewCard.Visibility = Visibility.Collapsed;
                        foreach (var p in ActiveParticipants)
                        {
                            p.IsScreenSharing = false;
                        }

                        if (_isWatchingRemoteStream)
                        {
                            _isWatchingRemoteStream = false;
                            ExitAnnotationMode();
                            ClearViewerCanvasAndStore();
                            _annRemotePermOn = false;
                            UpdateAnnotateToggleButtonState();

                            LiveStreamVideoImage.Source = null;
                            BtnExitStreamInBottomBar.Visibility = Visibility.Collapsed;
                            StreamWatchCard.Visibility = Visibility.Collapsed;

                            if (MainWindow.Current?.IsFullscreen == true) ExitStreamFullscreenMode();
                            ScreenShareStage.Visibility = Visibility.Collapsed;
                            UiAnimationHelper.AnimateViewEntrance(DefaultVoiceStage);
                            EnsureTelemetryTimerState();
                            ShowToast("Canlı ekran yayını sona erdi");
                        }
                        RefreshP2PRelayTreeLayout();
                        return;
                    }

                    // Uzak yayıncıyı bul ve kutusunda "Yayını İzle" butonunu aç
                    foreach (var p in ActiveParticipants)
                    {
                        if (p.PeerKey == broadcasterKey || (!string.IsNullOrEmpty(broadcasterName) && p.Name == broadcasterName))
                        {
                            p.IsScreenSharing = true;
                        }
                    }

                    RefreshP2PRelayTreeLayout();
                });
            };

            // Karşı taraftan canlı video karesi geldiğinde -> tam kare hızında (30/60 FPS) anında sahneye çiz
            _webrtc.OnRemoteVideoFrameReceivedWithId += (broadcasterKey, jpegBytes, w, h, frameId) =>
            {
                if (_isWatchingRemoteStream && !_screenCapture.IsSharing)
                {
                    OnViewerVideoFrameRendered(frameId);
                    // Pencere simge durumundayken kare çözülmez; geri açılınca bir sonraki kare hemen çizilir
                    if (_isAppWindowVisible) RenderIncomingJpegFrameToStage(jpegBytes, w, h);
                }
            };

            WireAnnotationEvents();
        }

        private void RenderIncomingJpegFrameToStage(byte[] jpegBytes, int packetW, int packetH)
        {
            if (Interlocked.CompareExchange(ref _isRenderingVideoFrame, 1, 0) != 0)
            {
                _webrtc.IncrementRenderDelayDroppedFrames();
                return; // Bir önceki kare hâlâ çiziliyorsa atla (sıfır kuyruk gecikmesi!)
            }

            int sharpness = _storage.Settings.StreamSharpnessPercent;

            // Arka plan iş parçacığında donanım hızlandırmalı JPEG -> BGRA çözme + sıfır UI thread gecikmesi (WriteableBitmap Invalidate: 0.3ms)
            _ = Task.Run(() =>
            {
                try
                {
                    byte[]? bgra = ScreenCaptureService.DecodeJpegToBgra(jpegBytes, sharpness, out int w, out int h, out int validByteLen);
                    if (bgra == null || w <= 0 || h <= 0 || validByteLen <= 0)
                    {
                        return;
                    }

                    bool queued = DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            if (_videoWriteableBitmap == null || _videoWriteableBitmap.PixelWidth != w || _videoWriteableBitmap.PixelHeight != h)
                            {
                                _videoWriteableBitmap = new WriteableBitmap(w, h);
                                LiveStreamVideoImage.Source = _videoWriteableBitmap;
                            }

                            using var bufStream = _videoWriteableBitmap.PixelBuffer.AsStream();
                            bufStream.Position = 0;
                            bufStream.Write(bgra, 0, validByteLen);
                            _videoWriteableBitmap.Invalidate();
                            _lastRenderedVideoW = w;
                            _lastRenderedVideoH = h;
                        }
                        catch { }
                        finally
                        {
                            Interlocked.Exchange(ref _isRenderingVideoFrame, 0);
                        }
                    });

                    if (!queued)
                    {
                        Interlocked.Exchange(ref _isRenderingVideoFrame, 0);
                    }
                }
                catch
                {
                    Interlocked.Exchange(ref _isRenderingVideoFrame, 0);
                }
            });
        }

        private void UpdateLiveStreamHud()
        {
            UpdateTelemetryModalData();
            if (_screenCapture.IsSharing)
            {
                TxtStreamSpecBadge.Text = $"{_screenCapture.CurrentHeight}p • {_screenCapture.MeasuredFps} FPS";
            }
            else if (_isWatchingRemoteStream && _lastRenderedVideoH > 0)
            {
                TxtStreamSpecBadge.Text = $"{_lastRenderedVideoH}p • {_webrtc.RemoteStreamFps} FPS";
            }
        }

        /// <summary>
        /// Ekran paylaşımı açıkken sol katılımcı listesini Electron'daki P2P Hiyerarşik Röle Ağacı (Sarı L-dirsek) düzenine sokar.
        /// </summary>
        private void RefreshP2PRelayTreeLayout()
        {
            var broadcaster = ActiveParticipants.FirstOrDefault(p => p.IsScreenSharing);
            if (broadcaster == null || ActiveParticipants.Count <= 1)
            {
                P2PTreeModeBadge.Visibility = Visibility.Collapsed;
                foreach (var p in ActiveParticipants)
                {
                    p.TreeDepth = 0;
                    p.RelayParentName = "";
                }
                return;
            }

            P2PTreeModeBadge.Visibility = Visibility.Visible;
            broadcaster.TreeDepth = 0;
            broadcaster.RelayParentName = "";

            int maxDirect = Math.Clamp(_storage.Settings.P2PRelayMaxChildren, 1, 8);
            var activeWatchers = _webrtc.GetActiveWatcherKeys();
            var viewers = ActiveParticipants
                .Where(p => p != broadcaster && (activeWatchers.Contains(p.PeerKey) || activeWatchers.Count == 0))
                .OrderBy(p => p.PeerKey, StringComparer.Ordinal)
                .ToList();

            for (int i = 0; i < viewers.Count; i++)
            {
                if (i < maxDirect)
                {
                    viewers[i].TreeDepth = 1;
                    viewers[i].RelayParentName = broadcaster.Name;
                }
                else
                {
                    var relayParent = viewers[i % maxDirect];
                    viewers[i].TreeDepth = 2;
                    viewers[i].RelayParentName = relayParent.Name;
                }
            }
        }

        private void BtnScreenShareToggle_Click(object sender, RoutedEventArgs e)
        {
            // Yayın açıkken de seçiciyi düzenleme modunda açar; yayını aniden kapatmaz
            OpenScreenPickerModal();
        }

        private void BtnEditLiveStream_Click(object sender, RoutedEventArgs e)
        {
            OpenScreenPickerModal();
        }

        private async void BtnPickerStopStream_Click(object sender, RoutedEventArgs e)
        {
            await UiAnimationHelper.HideModalAnimatedAsync(ScreenPickerModalOverlay, ScreenPickerModalCard);
            BtnStopScreenShare_Click(sender, e);
        }

        private DispatcherTimer? _pickerSilentRefreshTimer;
        private CancellationTokenSource? _pickerCts;

        // Madde 2: Sekme durumu, geçiş animasyon sayacı ve katlanabilir kalite kartı
        private enum PickerTab { Screens, Windows }
        private PickerTab _activePickerTab = PickerTab.Screens;
        private int _pickerTabTransitionId = 0;

        private void PickerTabContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && PickerTabIndicator != null)
            {
                PickerTabIndicator.Width = e.NewSize.Width / 2;
                float targetX = _activePickerTab == PickerTab.Screens ? 0f : (float)(e.NewSize.Width / 2);
                UiAnimationHelper.AnimateScaleAndTranslate(PickerTabIndicator, 1.0f, targetX, 0f, 1);
            }
        }

        private void ScreenPickerModalCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ScreenSourcesGridView?.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                // Genişlik < 940 ise 3 sütuna düşer, >= 940 ise 4 sütun
                wrapGrid.MaximumRowsOrColumns = e.NewSize.Width < 940 ? 3 : 4;
            }
        }

        private void StartPickerSilentRefresh()
        {
            StopPickerSilentRefresh();
            _pickerSilentRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _pickerSilentRefreshTimer.Tick += OnPickerSilentRefreshTick;
            _pickerSilentRefreshTimer.Start();
        }

        private void StopPickerSilentRefresh()
        {
            _pickerSilentRefreshTimer?.Stop();
            _pickerSilentRefreshTimer = null;
        }

        private void OnPickerSilentRefreshTick(object? sender, object e)
        {
            if (ScreenPickerModalOverlay.Visibility != Visibility.Visible)
            {
                StopPickerSilentRefresh();
                return;
            }
            SilentRefreshSources();
        }

        private void SilentRefreshSources()
        {
            IntPtr selfHwnd = MainWindow.Current != null ? WindowNative.GetWindowHandle(MainWindow.Current) : IntPtr.Zero;
            var newSources = _screenCapture.GetAvailableSourcesQuick(selfHwnd);

            var oldKeys = _allSources.Select(s => s.Id).ToList();
            var newKeys = newSources.Select(s => s.Id).ToList();
            if (oldKeys.SequenceEqual(newKeys))
            {
                return;
            }

            var selectedItem = ScreenSourcesGridView.SelectedItem as ScreenCaptureSourceItem;
            string? selectedId = selectedItem?.Id;

            var oldDict = _allSources.ToDictionary(s => s.Id, s => s);
            var updatedList = new List<ScreenCaptureSourceItem>();
            var needVisuals = new List<ScreenCaptureSourceItem>();

            foreach (var src in newSources)
            {
                if (oldDict.TryGetValue(src.Id, out var existing) && existing.Thumbnail != null)
                {
                    src.Thumbnail = existing.Thumbnail;
                    src.AppIcon = existing.AppIcon;
                    src.Subtitle = existing.Subtitle;
                    src.IsLoadingThumbnail = existing.IsLoadingThumbnail;
                    updatedList.Add(src);
                }
                else
                {
                    updatedList.Add(src);
                    needVisuals.Add(src);
                }
            }

            _allSources = updatedList;
            TxtTabScreensCount.Text = _allSources.Count(s => s.IsMonitor).ToString();
            TxtTabWindowsCount.Text = _allSources.Count(s => !s.IsMonitor).ToString();

            // Aktif sekmedeki listeyi güncelle (arka plan rengine değil _activePickerTab'a göre)
            if (_activePickerTab == PickerTab.Windows)
            {
                var windows = _allSources.Where(s => !s.IsMonitor).ToList();
                ScreenSourcesGridView.ItemsSource = windows;
                if (!string.IsNullOrEmpty(selectedId))
                {
                    var match = windows.FirstOrDefault(w => w.Id == selectedId);
                    if (match != null) ScreenSourcesGridView.SelectedItem = match;
                }
            }
            else
            {
                var screens = _allSources.Where(s => s.IsMonitor).ToList();
                ScreenSourcesGridView.ItemsSource = screens;
                if (!string.IsNullOrEmpty(selectedId))
                {
                    var match = screens.FirstOrDefault(m => m.Id == selectedId);
                    if (match != null) ScreenSourcesGridView.SelectedItem = match;
                }
            }

            if (needVisuals.Count > 0 && _pickerCts != null && !_pickerCts.IsCancellationRequested)
            {
                _screenCapture.PopulateVisualsAsync(needVisuals, DispatcherQueue, _pickerCts.Token);
            }
        }

        private void BtnToggleQualityCard_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = QualitySettingsPanel.Visibility == Visibility.Visible;
            QualitySettingsPanel.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            TxtQualityChevron.Text = isVisible ? "▾" : "▴";
        }

        private void UpdateQualitySummaryText()
        {
            if (!_isUiReady || TxtQualitySummary == null) return;
            string res = GetComboSelectedTag(CmbPickerResolution) ?? "1080";
            string fps = GetComboSelectedTag(CmbPickerFps) ?? "60";
            bool adaptive = TglPickerAdaptive?.IsOn ?? true;
            TxtQualitySummary.Text = $"{res}p · {fps} FPS · Dinamik: {(adaptive ? "Açık" : "Kapalı")}";
        }

        private void PickerSetting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady) return;
            UpdateQualitySummaryText();
        }

        private async void BtnTabScreens_Click(object sender, RoutedEventArgs? e)
        {
            await SwitchPickerTabAsync(PickerTab.Screens);
        }

        private async void BtnTabWindows_Click(object sender, RoutedEventArgs? e)
        {
            await SwitchPickerTabAsync(PickerTab.Windows);
        }

        private async Task SwitchPickerTabAsync(PickerTab newTab)
        {
            bool isSame = _activePickerTab == newTab;
            _activePickerTab = newTab;

            // Kayan göstergeyi Composition ile kaydır (220 ms)
            double containerWidth = PickerTabContainer != null && PickerTabContainer.ActualWidth > 0 ? PickerTabContainer.ActualWidth : 900;
            float tabWidth = (float)(containerWidth / 2);
            if (PickerTabIndicator != null)
            {
                PickerTabIndicator.Width = tabWidth;
                float targetX = newTab == PickerTab.Screens ? 0f : tabWidth;
                UiAnimationHelper.AnimateScaleAndTranslate(PickerTabIndicator, 1.0f, targetX, 0f, 220);
            }

            // Buton metin renkleri ve ağırlıkları
            if (newTab == PickerTab.Screens)
            {
                BtnTabScreens.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                BtnTabScreens.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                BtnTabWindows.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
                BtnTabWindows.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                BtnTabWindows.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                BtnTabWindows.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                BtnTabScreens.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
                BtnTabScreens.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            if (isSame && ScreenSourcesGridView.ItemsSource != null) return;

            int transitionId = ++_pickerTabTransitionId;
            float exitOffset = newTab == PickerTab.Windows ? -16f : 16f;
            float enterOffset = -exitOffset;

            // Izgara içeriği animasyonu: çıkan liste 120 ms'de opaklık 1->0 ve X ofseti 0->exitOffset
            UiAnimationHelper.AnimateScaleAndTranslate(ScreenSourcesGridView, 1.0f, exitOffset, 0f, 120);
            try
            {
                var vis = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ScreenSourcesGridView);
                var comp = vis.Compositor;
                var fadeOut = comp.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(120);
                vis.StartAnimation("Opacity", fadeOut);
            }
            catch { }

            await Task.Delay(120);
            if (transitionId != _pickerTabTransitionId) return;

            // Filtreleme verisi animasyon ortasında değiştirilir
            if (newTab == PickerTab.Screens)
            {
                var screens = _allSources.Where(s => s.IsMonitor).ToList();
                ScreenSourcesGridView.ItemsSource = screens;
                if (screens.Count > 0 && ScreenSourcesGridView.SelectedItem == null)
                    ScreenSourcesGridView.SelectedIndex = 0;
            }
            else
            {
                var windows = _allSources.Where(s => !s.IsMonitor).ToList();
                ScreenSourcesGridView.ItemsSource = windows;
                if (windows.Count > 0 && ScreenSourcesGridView.SelectedItem == null)
                    ScreenSourcesGridView.SelectedIndex = 0;
            }

            // Yeni liste 180 ms'de 0->1 ve enterOffset->0
            UiAnimationHelper.AnimateScaleAndTranslate(ScreenSourcesGridView, 1.0f, enterOffset, 0f, 1);
            try
            {
                var vis = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ScreenSourcesGridView);
                var comp = vis.Compositor;
                var fadeIn = comp.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(180);
                vis.StartAnimation("Opacity", fadeIn);
            }
            catch { }
            UiAnimationHelper.AnimateScaleAndTranslate(ScreenSourcesGridView, 1.0f, 0f, 0f, 180);
        }

        private void OpenScreenPickerModal()
        {
            _pickerCts?.Cancel();
            _pickerCts = new CancellationTokenSource();

            IntPtr selfHwnd = MainWindow.Current != null ? WindowNative.GetWindowHandle(MainWindow.Current) : IntPtr.Zero;
            _allSources = _screenCapture.GetAvailableSourcesQuick(selfHwnd);

            int screenCount = _allSources.Count(s => s.IsMonitor);
            int winCount = _allSources.Count(s => !s.IsMonitor);
            TxtTabScreensCount.Text = screenCount.ToString();
            TxtTabWindowsCount.Text = winCount.ToString();

            bool isSharing = _screenCapture.IsSharing;
            var currentSrc = _screenCapture.ActiveSource;

            // Kalite kartı varsayılan kapalı
            QualitySettingsPanel.Visibility = Visibility.Collapsed;
            TxtQualityChevron.Text = "▾";

            if (isSharing && currentSrc != null)
            {
                TxtScreenPickerTitle.Text = "Yayını Düzenle";
                BtnConfirmStartStream.Content = "✓ Değişiklikleri Uygula";
                BtnPickerStopStream.Visibility = Visibility.Visible;

                SelectComboByTag(CmbPickerResolution, _screenCapture.CurrentHeight.ToString());
                SelectComboByTag(CmbPickerFps, _screenCapture.MeasuredFps > 0 ? _screenCapture.MeasuredFps.ToString() : _storage.Settings.DefaultStreamFps.ToString());
                SelectComboByTag(CmbPickerCodec, _storage.Settings.PreferredVideoCodec);
                ChkPickerDataSaver.IsChecked = _storage.Settings.DataSaverMode;

                if (currentSrc.IsMonitor)
                {
                    _activePickerTab = PickerTab.Screens;
                    BtnTabScreens_Click(this, null);
                    var match = _allSources.FirstOrDefault(s => s.IsMonitor && s.Id == currentSrc.Id);
                    if (match != null) ScreenSourcesGridView.SelectedItem = match;
                    else if (ScreenSourcesGridView.Items.Count > 0) ScreenSourcesGridView.SelectedIndex = 0;
                }
                else
                {
                    _activePickerTab = PickerTab.Windows;
                    BtnTabWindows_Click(this, null);
                    var match = _allSources.FirstOrDefault(s => !s.IsMonitor && s.Handle == currentSrc.Handle);
                    if (match != null) ScreenSourcesGridView.SelectedItem = match;
                    else if (ScreenSourcesGridView.Items.Count > 0) ScreenSourcesGridView.SelectedIndex = 0;
                }
            }
            else
            {
                TxtScreenPickerTitle.Text = "Ekran veya Uygulama Penceresi Paylaş";
                BtnConfirmStartStream.Content = "🔴 Yayını Başlat";
                BtnPickerStopStream.Visibility = Visibility.Collapsed;

                SelectComboByTag(CmbPickerResolution, _storage.Settings.DefaultStreamHeight.ToString());
                SelectComboByTag(CmbPickerFps, _storage.Settings.DefaultStreamFps.ToString());
                SelectComboByTag(CmbPickerCodec, _storage.Settings.PreferredVideoCodec);
                ChkPickerDataSaver.IsChecked = _storage.Settings.DataSaverMode;

                // Varsayılan olarak tüm ekranlar sekmesini aç
                _activePickerTab = PickerTab.Screens;
                BtnTabScreens_Click(this, null);
            }

            TglPickerAdaptive.IsOn = _storage.Settings.AdaptiveStreamEnabled;
            SelectComboByTag(CmbPickerAdaptivePriority, _storage.Settings.AdaptiveStreamPriority);
            UpdatePickerAdaptiveLabels(TglPickerAdaptive.IsOn);
            UpdateQualitySummaryText();

            // Modal kart yüksekliği ve duyarlılık
            if (this.ActualHeight > 0)
            {
                ScreenPickerModalCard.MaxHeight = Math.Max(500, this.ActualHeight - 40);
            }

            UiAnimationHelper.ShowModalAnimated(ScreenPickerModalOverlay, ScreenPickerModalCard);

            // Arka planda küçük resimleri doldur ve 3 sn'de bir sessizce yenile
            _screenCapture.PopulateVisualsAsync(_allSources, DispatcherQueue, _pickerCts.Token);
            StartPickerSilentRefresh();
        }

        private void BtnRefreshCaptureSources_Click(object sender, RoutedEventArgs e)
        {
            _pickerCts?.Cancel();
            _pickerCts = new CancellationTokenSource();

            IntPtr selfHwnd = MainWindow.Current != null ? WindowNative.GetWindowHandle(MainWindow.Current) : IntPtr.Zero;
            _allSources = _screenCapture.GetAvailableSourcesQuick(selfHwnd);
            TxtTabScreensCount.Text = _allSources.Count(s => s.IsMonitor).ToString();
            TxtTabWindowsCount.Text = _allSources.Count(s => !s.IsMonitor).ToString();

            // Aktif sekmedeki filtreyi koru
            if (_activePickerTab == PickerTab.Windows)
            {
                BtnTabWindows_Click(sender, e);
            }
            else
            {
                BtnTabScreens_Click(sender, e);
            }

            _screenCapture.PopulateVisualsAsync(_allSources, DispatcherQueue, _pickerCts.Token);
        }

        private void ScreenSourcesGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || TxtSelectedSourceHint == null) return;
            if (ScreenSourcesGridView?.SelectedItem is ScreenCaptureSourceItem item)
            {
                TxtSelectedSourceHint.Text = $"Seçilen: {item.Title} ({item.Subtitle})";
            }
        }

        private async void BtnConfirmStartStream_Click(object sender, RoutedEventArgs e)
        {
            if (ScreenSourcesGridView.SelectedItem is not ScreenCaptureSourceItem chosen)
            {
                ShowToast("Lütfen paylaşılacak bir ekran veya pencere seçin.");
                return;
            }

            int resH = int.TryParse(GetComboSelectedTag(CmbPickerResolution), out int rh) ? rh : 1080;
            int fps = int.TryParse(GetComboSelectedTag(CmbPickerFps), out int rf) ? rf : 60;
            string codec = GetComboSelectedTag(CmbPickerCodec) ?? "auto";
            bool saver = ChkPickerDataSaver.IsChecked == true;
            bool isAdaptive = TglPickerAdaptive.IsOn;
            string priority = GetComboSelectedTag(CmbPickerAdaptivePriority) ?? "clarity";

            _storage.Settings.DefaultStreamHeight = resH;
            _storage.Settings.DefaultStreamFps = fps;
            _storage.Settings.PreferredVideoCodec = codec;
            _storage.Settings.DataSaverMode = saver;
            _storage.Settings.AdaptiveStreamEnabled = isAdaptive;
            _storage.Settings.AdaptiveStreamPriority = priority;
            _storage.SaveSettings();

            StopPickerSilentRefresh();
            _pickerCts?.Cancel();

            // Madde 1: Küçültülmüş pencere seçilmişse odağı çalmadan geri yükle
            if (!chosen.IsMonitor && chosen.Handle != IntPtr.Zero)
            {
                if (ScreenCaptureService.IsWindowMinimized(chosen.Handle))
                {
                    ScreenCaptureService.RestoreWindowNoActivate(chosen.Handle);
                    ShowToast("Pencere paylaşım için geri yüklendi");
                }
            }

            await UiAnimationHelper.HideModalAnimatedAsync(ScreenPickerModalOverlay, ScreenPickerModalCard);
            bool wasAlreadySharing = _engine.IsSharing;
            var activeSrc = _engine.ActiveSource;
            bool sourceChanged = !wasAlreadySharing || activeSrc == null || activeSrc.IsMonitor != chosen.IsMonitor ||
                (chosen.IsMonitor ? (activeSrc.Id != chosen.Id) : (activeSrc.Handle != chosen.Handle));

            _engine.StartOrUpdateCapture(chosen, resH, fps, codec, saver);

            if (!wasAlreadySharing)
            {
                _adaptiveStream?.Start(resH, fps, saver, isAdaptive, priority, _storage.Settings.LastAdaptiveLevel);
                _adaptiveLoopTimer?.Start();
            }
            else
            {
                _adaptiveStream?.UpdateCeiling(resH, fps, saver, isAdaptive, priority);
            }

            TxtBroadcasterActiveTitle.Text = $"Ekranınız Aktarılıyor: {chosen.Title}";
            TxtScreenShareTitle.Text = $"Yayında: {chosen.Title}";
            TxtStreamSpecBadge.Text = $"{resH}p • {fps} FPS";
            TxtBroadcasterSpec.Text = $"{resH}p • {fps} FPS";
            ShowStreamSettingsToast($"⚙️ Yayın Ayarları Güncellendi: {resH}p @ {fps} FPS • Codec: {codec.ToUpper()}");

            if (!wasAlreadySharing)
            {
                _soundpad.PlayUiEffect(UiSoundEffect.ScreenShareStart);
            }
            else if (sourceChanged)
            {
                // Yayın sürerken kaynak değiştiğinde çizimleri temizle ve katmanı yeniden konumlandır
                _contentTracker?.Reset();
                _annBroadcasterStore.ClearAll();
                _annOverlay?.Render(_annBroadcasterStore.Snapshot());
                _webrtc.BroadcastAnnotation(new { t = "ANN_CLEAR", scope = "all" });

                if (chosen.IsMonitor)
                {
                    _annOverlay?.SetBounds(chosen.BoundsX, chosen.BoundsY, chosen.BoundsWidth, chosen.BoundsHeight);
                    _annOverlay?.SetVisible(true);
                    TglAllowAnnotations.IsEnabled = true;
                    TxtAnnotationHint.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _annOverlay?.SetVisible(false);
                    TglAllowAnnotations.IsEnabled = false;
                    TxtAnnotationHint.Text = "Çizim şu an yalnızca tüm ekran paylaşımında kullanılabilir.";
                    TxtAnnotationHint.Visibility = Visibility.Visible;
                }
                BroadcastPerm();
            }
            ShowToast($"🔴 {chosen.Title} yayını aktif ({resH}p @ {fps} FPS)");
        }

        public void ShowStreamSettingsToast(string msg)
        {
            TxtStreamSettingsToastMsg.Text = msg;
            StreamSettingsToast.Visibility = Visibility.Visible;
            _streamToastTimer?.Stop();
            _streamToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _streamToastTimer.Tick += (s, e) =>
            {
                _streamToastTimer?.Stop();
                StreamSettingsToast.Visibility = Visibility.Collapsed;
            };
            _streamToastTimer.Start();
        }

        private void UpdateAdaptiveStreamUI(AdaptiveStreamStateInfo info)
        {
            if (info == null) return;

            if (!info.IsAdaptive)
            {
                BadgeBroadcasterAdaptive.Visibility = Visibility.Collapsed;
                return;
            }

            BadgeBroadcasterAdaptive.Visibility = Visibility.Visible;
            TxtBroadcasterAdaptiveIndicator.Text = $"OTOMATİK {info.Direction}";
            TxtBroadcasterSpec.Text = $"{info.TargetHeight}p • {info.TargetFps} FPS";
            TxtStreamSpecBadge.Text = $"{info.TargetHeight}p • {info.TargetFps} FPS";
            ToolTipService.SetToolTip(BadgeBroadcasterAdaptive, $"Dinamik Kalite: Basamak {info.Level}/6 ({info.TargetHeight}p @ {info.TargetFps} FPS, {info.JpegQuality}q)\nNeden: {info.Reason}\nDoğrudan Alıcı Sayısı: {info.DirectReceiverCount}");
        }

        private void TglPickerAdaptive_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || TglPickerAdaptive == null) return;
            UpdatePickerAdaptiveLabels(TglPickerAdaptive.IsOn);
            UpdateQualitySummaryText();
        }

        private void UpdatePickerAdaptiveLabels(bool isAdaptive)
        {
            if (TxtPickerResLabel != null)
                TxtPickerResLabel.Text = isAdaptive ? "MAKSİMUM ÇÖZÜNÜRLÜK" : "ÇÖZÜNÜRLÜK";
            if (TxtPickerFpsLabel != null)
                TxtPickerFpsLabel.Text = isAdaptive ? "MAKSİMUM KARE HIZI (FPS)" : "KARE HIZI (FPS)";
            if (CmbPickerAdaptivePriority != null)
                CmbPickerAdaptivePriority.IsEnabled = isAdaptive;
        }

        private void CmbPickerAdaptivePriority_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || CmbPickerAdaptivePriority == null) return;
            string p = GetComboSelectedTag(CmbPickerAdaptivePriority) ?? "clarity";
            _storage.Settings.AdaptiveStreamPriority = p;
            _storage.SaveSettings();
        }

        private void TglSettingsAdaptiveStream_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents || TglSettingsAdaptiveStream == null) return;
            _storage.Settings.AdaptiveStreamEnabled = TglSettingsAdaptiveStream.IsOn;
            _storage.SaveSettings();
            if (TglPickerAdaptive != null)
            {
                TglPickerAdaptive.IsOn = TglSettingsAdaptiveStream.IsOn;
            }
        }

        private void BtnWatchRemoteStreamFromCard_Click(object sender, RoutedEventArgs e)
        {
            string broadcasterKey = _engine.RemoteBroadcasterKey;
            if (string.IsNullOrEmpty(broadcasterKey))
            {
                ShowToast("Aktif canlı yayın bulunamadı.");
                return;
            }

            _isWatchingRemoteStream = true;
            _engine.SendWatchStart(broadcasterKey);
            DefaultVoiceStage.Visibility = Visibility.Collapsed;
            UiAnimationHelper.AnimateViewEntrance(ScreenShareStage);
            BroadcasterActiveCard.Visibility = Visibility.Collapsed;
            StreamWatchCard.Visibility = Visibility.Visible;

            if (_engine is RustRealtimeEngine)
            {
                LiveStreamSwapChainPanel.Visibility = Visibility.Visible;
                LiveStreamVideoImage.Visibility = Visibility.Collapsed;
                try
                {
                    IntPtr unk = ((WinRT.IWinRTObject)LiveStreamSwapChainPanel).NativeObject.ThisPtr;
                    Guid iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
                    if (Marshal.QueryInterface(unk, ref iid, out IntPtr panelNative) == 0 && panelNative != IntPtr.Zero)
                    {
                        _engine.SetVideoSurface("stream_main", panelNative);
                        Marshal.Release(panelNative);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SwapChainPanel QI fallback] {ex.Message}");
                    LiveStreamSwapChainPanel.Visibility = Visibility.Collapsed;
                    LiveStreamVideoImage.Visibility = Visibility.Visible;
                }
            }
            else
            {
                LiveStreamSwapChainPanel.Visibility = Visibility.Collapsed;
                LiveStreamVideoImage.Visibility = Visibility.Visible;
            }

            if (_isTelemetryPanelOpen && PersistentTelemetryPanel != null)
            {
                PersistentTelemetryPanel.Visibility = Visibility.Visible;
                if (MenuToggleTelemetryPanel != null) MenuToggleTelemetryPanel.IsChecked = true;
            }
            BtnExitStreamInBottomBar.Visibility = Visibility.Visible;

            TxtScreenShareTitle.Text = $"{_engine.RemoteBroadcasterName} — {_webrtc.RemoteStreamTitle}";
            TxtStreamSpecBadge.Text = $"{_webrtc.RemoteStreamHeight}p • {_webrtc.RemoteStreamFps} FPS";
            EnsureTelemetryTimerState();
            UpdateAnnotateToggleButtonState();
        }

        private void BtnExitStreamInBottomBar_Click(object sender, RoutedEventArgs e)
        {
            string broadcasterKey = _engine.RemoteBroadcasterKey;
            if (_isWatchingRemoteStream && !string.IsNullOrEmpty(broadcasterKey))
            {
                _engine.SendWatchStop(broadcasterKey);
            }
            _isWatchingRemoteStream = false;
            ExitAnnotationMode();
            ClearViewerCanvasAndStore();
            _annRemotePermOn = false;
            UpdateAnnotateToggleButtonState();

            LiveStreamSwapChainPanel.Visibility = Visibility.Collapsed;
            LiveStreamVideoImage.Source = null;
            BtnExitStreamInBottomBar.Visibility = Visibility.Collapsed;
            StreamWatchCard.Visibility = Visibility.Collapsed;

            if (MainWindow.Current?.IsFullscreen == true) ExitStreamFullscreenMode();
            ScreenShareStage.Visibility = Visibility.Collapsed;
            UiAnimationHelper.AnimateViewEntrance(DefaultVoiceStage);
            EnsureTelemetryTimerState();
        }

        private void BtnWatchRemoteStream_Click(object sender, RoutedEventArgs e)
        {
            BtnWatchRemoteStreamFromCard_Click(sender, e);
        }

        private void BtnStopScreenShare_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsSharing)
            {
                _engine.StopCapture();
                _soundpad.PlayUiEffect(UiSoundEffect.ScreenShareStop);
            }
            else if (_isWatchingRemoteStream)
            {
                BtnExitStreamInBottomBar_Click(sender, e);
            }
        }

        // Madde 10: Tek keskinleştirme paneli ve sağ üstte sabit telemetri paneli
        private bool _isTelemetryPanelOpen = false;

        private void MenuToggleTelemetryPanel_Click(object sender, RoutedEventArgs e)
        {
            SetTelemetryPanelVisible(!_isTelemetryPanelOpen);
        }

        private void BtnClosePersistentTelemetry_Click(object sender, RoutedEventArgs e)
        {
            SetTelemetryPanelVisible(false);
        }

        public void SetTelemetryPanelVisible(bool visible)
        {
            _isTelemetryPanelOpen = visible;
            if (MenuToggleTelemetryPanel != null)
            {
                MenuToggleTelemetryPanel.IsChecked = visible;
            }
            if (PersistentTelemetryPanel != null)
            {
                PersistentTelemetryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible)
                {
                    UpdateTelemetryModalData();
                }
            }
            EnsureTelemetryTimerState();
        }

        private void MenuStreamSharpness_Click(object sender, RoutedEventArgs e)
        {
            int current = _storage.Settings.StreamSharpnessPercent;
            if (SliderSharpnessPopup != null)
            {
                SliderSharpnessPopup.Value = current;
            }
            if (TxtSharpnessPopupValue != null)
            {
                TxtSharpnessPopupValue.Text = $"%{current}";
            }
            UpdateSharpnessChipHighlights(current);
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(BtnStreamOptions);
        }

        private void SliderSharpnessPopup_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady) return;
            int val = (int)Math.Round(e.NewValue);
            ApplySharpnessValue(val, updateSlider: false);
        }

        private void SharpnessChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tagStr && int.TryParse(tagStr, out int val))
            {
                ApplySharpnessValue(val, updateSlider: true);
            }
        }

        private void ApplySharpnessValue(int val, bool updateSlider)
        {
            _storage.Settings.StreamSharpnessPercent = val;
            _storage.SaveSettings();

            if (updateSlider && SliderSharpnessPopup != null)
            {
                SliderSharpnessPopup.Value = val;
            }
            if (TxtSharpnessPopupValue != null)
            {
                TxtSharpnessPopupValue.Text = $"%{val}";
            }
            if (MenuStreamSharpnessItem != null)
            {
                MenuStreamSharpnessItem.Text = $"✨  Görüntü Keskinliği… (%{val})";
            }

            UpdateSharpnessChipHighlights(val);
        }

        private void UpdateSharpnessChipHighlights(int val)
        {
            SetChipState(ChipSharpness0, val == 0);
            SetChipState(ChipSharpness30, val == 30);
            SetChipState(ChipSharpness60, val == 60);
            SetChipState(ChipSharpness100, val == 100);
        }

        private void SetChipState(Button? chip, bool isSelected)
        {
            if (chip == null) return;
            if (isSelected)
            {
                chip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 139, 92, 246));
                chip.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                chip.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 139, 92, 246));
                chip.BorderThickness = new Thickness(1);
            }
            else
            {
                chip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
                chip.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
                chip.BorderThickness = new Thickness(0);
            }
        }

        private void UpdateTelemetryModalData()
        {
            if (_screenCapture.IsSharing)
            {
                TxtTelemetryCodec.Text = "MJPEG (JPEG kareleri)";
                TxtTelemetryResFps.Text = $"{_screenCapture.CurrentWidth}×{_screenCapture.CurrentHeight} @ {_screenCapture.MeasuredFps} FPS";
                
                double encOutputMbps = _screenCapture.MeasuredBitrateKbps / 1000.0;
                double netSentMbps = _webrtc.SentNetworkBitrateKbps / 1000.0;
                double targetMbps = _screenCapture.CurrentTargetBitrateKbps / 1000.0;
                var (avgLoss, minRxKbps, avgRxKbps, totalDropped) = _webrtc.GetBroadcasterReceiverStats();

                if (minRxKbps > 0)
                {
                    TxtTelemetryBitrate.Text = $"Ağ: {netSentMbps:F2} Mbps • Çıkış: {encOutputMbps:F2} (Hedef: {targetMbps:F1})\nAlıcılar: {minRxKbps / 1000.0:F2}–{avgRxKbps / 1000.0:F2} Mbps";
                }
                else
                {
                    TxtTelemetryBitrate.Text = $"Ağ: {netSentMbps:F2} Mbps • Çıkış: {encOutputMbps:F2} Mbps (Hedef: {targetMbps:F1})";
                }

                int bRtt = _webrtc.BroadcasterAverageRttMs;
                TxtTelemetryRtt.Text = bRtt > 0 ? $"UDP/SRTP • {bRtt} ms RTT (ortalama)" : "UDP/SRTP • —";
                
                if (avgRxKbps > 0 || totalDropped > 0)
                {
                    TxtTelemetryLoss.Text = $"{avgLoss:F1}% Alıcı Kaybı • {totalDropped} Düşen Kare";
                }
                else
                {
                    TxtTelemetryLoss.Text = "Alıcı Raporu Bekleniyor • —";
                }
                TxtTelemetryFrames.Text = $"{_screenCapture.TotalFramesCaptured} Kare Kodlandı";

                if (_adaptiveStream != null && _adaptiveStream.CurrentState != null)
                {
                    var st = _adaptiveStream.CurrentState;
                    TxtTelemetryAdaptiveStep.Text = st.IsAdaptive
                        ? $"Dinamik Mod: Aktif • Basamak {st.Level}/6 • Kalite: {st.JpegQuality}q • Hedef: {st.TargetBitrateKbps} kbps ({st.TargetHeight}p @ {st.TargetFps} FPS)"
                        : "Dinamik Mod: Kapalı (Sabit Kalite)";
                    TxtTelemetryAdaptiveDecision.Text = $"Yön: {st.Direction} • Neden: {st.Reason} • Doğrudan Alıcı Sayısı: {st.DirectReceiverCount}";
                }
                else
                {
                    TxtTelemetryAdaptiveStep.Text = "Dinamik Mod: Hazırlanıyor...";
                    TxtTelemetryAdaptiveDecision.Text = "Yayın parametreleri ölçülüyor.";
                }
            }
            else if (_isWatchingRemoteStream)
            {
                int w = _lastRenderedVideoW > 0 ? _lastRenderedVideoW : 1920;
                int h = _lastRenderedVideoH > 0 ? _lastRenderedVideoH : 1080;
                TxtTelemetryCodec.Text = "MJPEG (JPEG kareleri) • P2P UDP/SRTP";
                TxtTelemetryResFps.Text = $"{w}×{h} @ {_webrtc.RemoteStreamFps} FPS";
                
                double rxMbps = _webrtc.ReceivedVideoBitrateKbps / 1000.0;
                double usableMbps = _webrtc.UsableVideoBitrateKbps / 1000.0;
                TxtTelemetryBitrate.Text = $"Alınan: {rxMbps:F2} Mbps • Kullanılabilir: {usableMbps:F2} Mbps";
                
                int vRtt = _webrtc.RemoteStreamRttMs;
                TxtTelemetryRtt.Text = vRtt > 0 ? $"UDP/SRTP • {vRtt} ms RTT" : "UDP/SRTP • —";
                
                double lossPct = _webrtc.RemoteStreamPacketLossPct;
                int droppedFrames = _webrtc.RenderDelayDroppedFramesCount;
                TxtTelemetryLoss.Text = $"{lossPct:F1}% Paket Kaybı • {droppedFrames} Düşen Kare";
                TxtTelemetryFrames.Text = $"{_webrtc.TotalFramesDecoded} Kare Çözüldü";

                var vq = _webrtc.LastBroadcasterVqState;
                if (vq != null)
                {
                    TxtTelemetryAdaptiveStep.Text = vq.Adaptive
                        ? $"Yayıncı Dinamik: Aktif • Basamak {vq.Lvl}/6 • Kalite: {vq.Q}q • {vq.H}p @ {vq.Fps} FPS"
                        : "Yayıncı Dinamik: Kapalı (Sabit Kalite)";
                    TxtTelemetryAdaptiveDecision.Text = $"Neden: {vq.Reason}";
                }
                else
                {
                    TxtTelemetryAdaptiveStep.Text = "Yayıncı Dinamik Kalite Bilgisi Bekleniyor...";
                    TxtTelemetryAdaptiveDecision.Text = "Henüz VQ_STATE paketi alınmadı.";
                }
            }
        }

        private void BtnCopyTelemetryReport_Click(object sender, RoutedEventArgs e)
        {
            UpdateTelemetryModalData();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== RUNA WEBRTC GERÇEK RTP TEŞHİS RAPORU ===");
            sb.AppendLine($"Rapor Zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("---------------------------------------------------------");
            sb.AppendLine($"• Aktif Rol: {(_screenCapture.IsSharing ? "YAYINCI (GÖNDEREN)" : "İZLEYİCİ (ALICI)")}");
            sb.AppendLine($"• RTP Codec: {TxtTelemetryCodec.Text}");
            sb.AppendLine($"• Çözünürlük & FPS: {TxtTelemetryResFps.Text}");
            sb.AppendLine($"• Bit Hızı: {TxtTelemetryBitrate.Text.Replace("\n", " ")}");
            sb.AppendLine($"• Ağ Yolu & RTT: {TxtTelemetryRtt.Text}");
            sb.AppendLine($"• Kararlılık: {TxtTelemetryLoss.Text}");
            sb.AppendLine($"• Toplam Kare: {TxtTelemetryFrames.Text}");
            sb.AppendLine($"• Dinamik Çözünürlük: {TxtTelemetryAdaptiveStep.Text}");
            sb.AppendLine($"• Karar / Durum: {TxtTelemetryAdaptiveDecision.Text}");
            sb.AppendLine("=========================================================");

            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(sb.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ShowToast("📋 Telemetri raporu panoya kopyalandı!");
        }

        private void VoiceCenterPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (MainWindow.Current?.IsFullscreen == true)
            {
                BottomVoiceControlBar.Opacity = 1.0;
                BottomVoiceControlBar.IsHitTestVisible = true;
                ResetFullscreenIdleTimer();
            }
        }

        private void ResetFullscreenIdleTimer()
        {
            _fullscreenIdleTimer?.Stop();
            _fullscreenIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
            _fullscreenIdleTimer.Tick += (s, e) =>
            {
                _fullscreenIdleTimer?.Stop();
                if (MainWindow.Current?.IsFullscreen == true)
                {
                    BottomVoiceControlBar.Opacity = 0.0;
                    BottomVoiceControlBar.IsHitTestVisible = false;
                }
            };
            _fullscreenIdleTimer.Start();
        }

        private void BtnStreamFullscreen_Click(object sender, RoutedEventArgs e) => ToggleStreamFullscreenMode();
        private void LiveStreamVideoImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_annDrawMode) return;
            ToggleStreamFullscreenMode();
        }

        private void ToggleStreamFullscreenMode()
        {
            if (MainWindow.Current == null) return;
            if (MainWindow.Current.IsFullscreen)
                ExitStreamFullscreenMode();
            else
                EnterStreamFullscreenMode();
        }

        private void EnterStreamFullscreenMode()
        {
            VoiceLeftColDef.Width = new GridLength(0);
            VoiceRightColDef.Width = new GridLength(0);
            VoiceLeftPanel.Visibility = Visibility.Collapsed;
            VoiceRightChatPanel.Visibility = Visibility.Collapsed;
            ScreenShareStage.Margin = new Thickness(0);

            StreamWatchCard.MaxWidth = double.PositiveInfinity;
            StreamWatchCard.MaxHeight = double.PositiveInfinity;

            MainWindow.Current?.SetFullscreenMode(true);
            ResetFullscreenIdleTimer();
            ShowToast("Tam ekran modu (Çıkmak için Esc veya çift tık)");
        }

        private void ExitStreamFullscreenMode()
        {
            _fullscreenIdleTimer?.Stop();
            BottomVoiceControlBar.Opacity = 1.0;
            BottomVoiceControlBar.IsHitTestVisible = true;

            VoiceLeftColDef.Width = new GridLength(355);
            VoiceRightColDef.Width = new GridLength(395);
            VoiceLeftPanel.Visibility = Visibility.Visible;
            VoiceRightChatPanel.Visibility = Visibility.Visible;
            ScreenShareStage.Margin = new Thickness(14, 14, 14, 80);

            StreamWatchCard.MaxWidth = 880;
            StreamWatchCard.MaxHeight = 520;

            MainWindow.Current?.SetFullscreenMode(false);
        }

        private async Task CloseScreenPickerModalAsync()
        {
            StopPickerSilentRefresh();
            _pickerCts?.Cancel();
            await UiAnimationHelper.HideModalAnimatedAsync(ScreenPickerModalOverlay, ScreenPickerModalCard);
        }

        private async void BtnCloseScreenPicker_Click(object sender, RoutedEventArgs e) =>
            await CloseScreenPickerModalAsync();

        private async void BackdropCloseScreenPicker_Tapped(object sender, TappedRoutedEventArgs e) =>
            await CloseScreenPickerModalAsync();

        // =====================================================================
        // ANKET VE DOSYA PAYLAŞIM ETKİLEŞİMLERİ (sesli-oda-sag-panel.png)
        // =====================================================================

        private void BtnOpenCreatePoll_Click(object sender, RoutedEventArgs e)
        {
            TxtPollQuestion.Text = "";
            TxtPollOption1.Text = "";
            TxtPollOption2.Text = "";
            CreatePollModalOverlay.Visibility = Visibility.Visible;
        }

        private void BtnCloseCreatePoll_Click(object sender, RoutedEventArgs e)
        {
            CreatePollModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void BackdropCloseCreatePoll_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CreatePollModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnSubmitCreatePoll_Click(object sender, RoutedEventArgs e)
        {
            string q = TxtPollQuestion.Text.Trim();
            string o1 = TxtPollOption1.Text.Trim();
            string o2 = TxtPollOption2.Text.Trim();

            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(o1) || string.IsNullOrEmpty(o2))
            {
                ShowToast("Lütfen soru ve her iki seçeneği de doldurun.");
                return;
            }

            CreatePollModalOverlay.Visibility = Visibility.Collapsed;

            string duration = (CmbPollDuration.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "2 saat içinde sona erecek";

            var pollData = new ChatPollData
            {
                Question = q,
                DurationLabel = duration,
                Option1 = new ChatPollOption { Text = o1, Votes = 0 },
                Option2 = new ChatPollOption { Text = o2, Votes = 0 }
            };
            pollData.RecalculatePercents();

            var pollMsg = new ChatMessage
            {
                SenderName = _storage.CurrentUser.Name,
                SenderAvatar = !string.IsNullOrEmpty(_storage.CurrentUser.AvatarDataUri) ? _storage.CurrentUser.AvatarDataUri : _storage.CurrentUser.AvatarPath,
                TimeString = DateTime.Now.ToString("HH:mm"),
                IsMe = true,
                MessageType = "poll",
                Text = $"📊 {q}",
                PollData = pollData
            };

            AddChatMessageBounded(pollMsg);
            RoomChatEmptyHint.Visibility = Visibility.Collapsed;
            ChatMessagesList.ScrollIntoView(pollMsg);
            _soundpad.PlayUiEffect(UiSoundEffect.MessageSent);

            if (_isInVoiceRoom)
            {
                await _signaling.SendChatMessageAsync(pollMsg);
            }
        }

        private async void BtnVotePollOption1_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ChatMessage msg && msg.PollData != null)
            {
                msg.PollData.Option1.Votes++;
                msg.PollData.RecalculatePercents();
                _soundpad.PlayUiEffect(UiSoundEffect.ButtonClick);
                if (_isInVoiceRoom) await _signaling.SendPollVoteAsync(msg.Id, 1);
            }
        }

        private async void BtnVotePollOption2_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ChatMessage msg && msg.PollData != null)
            {
                msg.PollData.Option2.Votes++;
                msg.PollData.RecalculatePercents();
                _soundpad.PlayUiEffect(UiSoundEffect.ButtonClick);
                if (_isInVoiceRoom) await _signaling.SendPollVoteAsync(msg.Id, 2);
            }
        }

        private async void BtnAttachFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                IntPtr hwnd = MainWindow.Current != null ? WindowNative.GetWindowHandle(MainWindow.Current) : IntPtr.Zero;
                InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                var props = await file.GetBasicPropertiesAsync();
                string sizeStr = $"{props.Size / 1024.0 / 1024.0:F1} MB";
                if (props.Size < 1024 * 1024) sizeStr = $"{props.Size / 1024.0:F0} KB";

                var attachment = new ChatFileAttachment
                {
                    FileName = file.Name,
                    FileSizeString = sizeStr,
                    FileExtension = file.FileType,
                    FilePath = file.Path,
                    IsDownloaded = true
                };

                var fileMsg = new ChatMessage
                {
                    SenderName = _storage.CurrentUser.Name,
                    SenderAvatar = !string.IsNullOrEmpty(_storage.CurrentUser.AvatarDataUri) ? _storage.CurrentUser.AvatarDataUri : _storage.CurrentUser.AvatarPath,
                    TimeString = DateTime.Now.ToString("HH:mm"),
                    IsMe = true,
                    MessageType = "file",
                    Text = $"📁 {file.Name}",
                    FileAttachment = attachment
                };

                AddChatMessageBounded(fileMsg);
                RoomChatEmptyHint.Visibility = Visibility.Collapsed;
                ChatMessagesList.ScrollIntoView(fileMsg);
                _soundpad.PlayUiEffect(UiSoundEffect.MessageSent);

                if (_isInVoiceRoom)
                {
                    await _signaling.SendChatMessageAsync(fileMsg);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileAttach] Hata: {ex.Message}");
            }
        }

        private async void BtnDownloadOrOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ChatMessage msg && msg.FileAttachment != null)
            {
                try
                {
                    // Yalnızca BU cihazdan gönderilmiş dosyalar açılabilir. Uzaktan gelen mesajın yolu asla kullanılmaz:
                    // aksi hâlde bir katılımcı \\sunucu\pay\x.exe gibi bir yol yazıp tıklayana dosya çalıştırtabilirdi.
                    if (msg.IsMe && !string.IsNullOrEmpty(msg.FileAttachment.FilePath) &&
                        !msg.FileAttachment.FilePath.StartsWith(@"\\", StringComparison.Ordinal) &&
                        File.Exists(msg.FileAttachment.FilePath))
                    {
                        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(msg.FileAttachment.FilePath);
                        await Launcher.LaunchFileAsync(file);
                    }
                    else
                    {
                        ShowToast($"Dosya aktarımı henüz desteklenmiyor: {msg.FileAttachment.FileName} dosyasını gönderen kişiden isteyin.");
                    }
                }
                catch (Exception ex)
                {
                    ShowToast($"Dosya açılamadı: {ex.Message}");
                }
            }
        }

        private void EmojiItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string emoji)
            {
                TxtChatInput.Text += emoji;
                TxtChatInput.SelectionStart = TxtChatInput.Text.Length;
            }
        }

        // =====================================================================
        // KAPSAMLI 5 SEKMELİ AYARLAR PENCERESİ VE 30SN MİKROFON TESTİ
        // =====================================================================

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsModal();

        private void BtnQuickMicTest_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsModal();
            SwitchSettingsTab("mictest");
            BtnRecordMicTest_Click(sender, e);
        }

        private void OpenSettingsModal()
        {
            _suppressSettingsEvents = true;
            var s = _storage.Settings;

            // Cihazları doldur
            var inDevs = _audio.GetInputDevices();
            CmbInputDevices.ItemsSource = inDevs;
            CmbInputDevices.SelectedItem = inDevs.FirstOrDefault(d => d.Id == s.InputDeviceId) ?? inDevs[0];

            var outDevs = _audio.GetOutputDevices();
            CmbOutputDevices.ItemsSource = outDevs;
            CmbOutputDevices.SelectedItem = outDevs.FirstOrDefault(d => d.Id == s.OutputDeviceId) ?? outDevs[0];

            SliderInputGain.Value = s.InputGainPercent;
            TxtInputGainVal.Text = $"%{(int)s.InputGainPercent}";
            SliderOutputGain.Value = s.OutputMasterPercent;
            TxtOutputGainVal.Text = $"%{(int)s.OutputMasterPercent}";

            // RNNoise durumu
            TxtRnnoiseEngineHealth.Text = _audio.IsRNNoiseAvailable
                ? "● RNNoise.dll Aktif (C Native 48kHz)"
                : "● DSP Gürültü Motoru Aktif";

            SelectComboByTag(CmbNoiseEngine, s.NoiseEngine);
            SliderNoiseLevel.Value = s.NoiseSuppressionLevel;
            UpdateNoiseLevelLabel(s.NoiseSuppressionLevel);

            TglImpactGuard.IsOn = s.ImpactGuardEnabled;
            TglHeadphoneAec.IsOn = s.HeadphoneAecEnabled;

            // VAD / PTT / Opus
            SelectComboByTag(CmbInputMode, s.InputMode);
            PanelVadThreshold.Visibility = s.InputMode == "ptt" ? Visibility.Collapsed : Visibility.Visible;
            PanelPttKeybind.Visibility = s.InputMode == "ptt" ? Visibility.Visible : Visibility.Collapsed;
            SliderVadThreshold.Value = s.VadThresholdDb;
            TxtVadThreshLabel.Text = $"Ses Açılma Eşiği: {(int)s.VadThresholdDb} dB";
            BtnAssignPttKey.Content = $"Tuş: {s.PttKeyName} (Değiştir)";

            SelectComboByTag(CmbOpusBitrate, s.OpusBitrateKbps.ToString());
            SelectComboByTag(CmbOpusFrameMs, s.OpusFrameMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // 5-Bant EQ & Limiter
            SyncEqSlidersFromSettings();
            UpdateSoundProfileUi();
            SliderCeilingLimiter.Value = s.MaxOutputCeilingDb;
            TxtCeilingLimiterVal.Text = $"{s.MaxOutputCeilingDb:0.0} dB";

            // Ses Dengeleme (Leveling)
            SelectComboByTag(CmbVoiceLevelingMode, s.VoiceLevelingMode);
            UpdateVoiceLevelingDesc(s.VoiceLevelingMode);
            SelectComboByTag(CmbVoiceLevelingTarget, ((int)s.VoiceLevelingTargetDb).ToString());

            // Video & P2P Röle
            SelectComboByTag(CmbDefaultRes, s.DefaultStreamHeight.ToString());
            SelectComboByTag(CmbDefaultFps, s.DefaultStreamFps.ToString());
            TglSettingsAdaptiveStream.IsOn = s.AdaptiveStreamEnabled;
            SliderRelayChildren.Value = s.P2PRelayMaxChildren;
            TxtRelayChildrenLabel.Text = $"P2P Hiyerarşik Ağaç Doğrudan Yük Limiti: Maksimum {s.P2PRelayMaxChildren} Kişi";

            // Ekran Üstü Çizim (Annotation)
            TglSetAllowAnnotations.IsOn = s.AllowViewerAnnotations;
            SelectComboByTag(CmbSetAnnotationFade, s.AnnotationFadeSeconds.ToString());
            TglSetShowAnnotationNames.IsOn = s.ShowAnnotationNames;

            // Profil & Görünüm
            TxtSettingsProfileName.Text = _storage.CurrentUser.Name;
            RefreshModalAvatars();
            RefreshSettingsSidebarProfile();
            TglSettingRipples.IsOn = s.RippleAnimationsEnabled;
            TglSettingUiSounds.IsOn = s.UiSoundsEnabled;

            // Ağ durumu
            TxtNetworkBrokerStatus.Text = $"Aktif Sinyalleşme Sunucusu: {_signaling.ActiveBrokerUrl} (3'lü Yedekli)";
            TxtSessionIdInfo.Text = $"P2P Oturum Anahtarı: {_storage.CurrentUser.PeerKey}";

            // Gerçek Zamanlı Motor Seçici
            SelectComboByTag(CmbRealtimeEngine, s.RealtimeEngine);
            UpdateActiveEngineDisplay();

            _suppressSettingsEvents = false;
            BuildSettingsNav();
            if (_activeSettingsCategory == null) SwitchSettingsTab("voice");
            UiAnimationHelper.ShowModalAnimated(SettingsModalOverlay, SettingsModalCard);
            EnsureTelemetryTimerState();
        }

        private async void CloseSettingsModal()
        {
            _isCapturingPttKey = false;
            if (TglListenMyVoice.IsChecked == true)
            {
                TglListenMyVoice.IsChecked = false;
                _audio.SetMicMonitor(false);
            }
            _audio.StopMicTest();
            MicTestProgressBar.Visibility = Visibility.Collapsed;
            BtnRecordMicTest.Content = "⏺ 30 Sn Kayıt Testi Başlat";

            _storage.SaveSettings();
            if (_isInVoiceRoom) UpdateStageSubtitleText();
            await UiAnimationHelper.HideModalAnimatedAsync(SettingsModalOverlay, SettingsModalCard);
            EnsureTelemetryTimerState();
        }

        private void BtnCloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettingsModal();
        private void BackdropCloseSettings_Tapped(object sender, TappedRoutedEventArgs e) => CloseSettingsModal();

        private void UpdateSettingsLiveTelemetry()
        {
            TxtLiveMicDbTelemetry.Text = $"{_audio.CurrentInputDb:0} dB";
            TxtLiveVadTelemetry.Text = $"%{(int)Math.Round(_audio.CurrentVadProbability * 100)}";
            SettingsLiveMicBar.Value = _audio.CurrentVuLevel * 100.0;
            UpdateVoiceLevelingLiveTelemetry();
        }

        private void UpdateVoiceLevelingLiveTelemetry()
        {
            if (ListVoiceLevelingPeers == null || TxtVoiceLevelingLiveEmpty == null) return;

            if (!_isInVoiceRoom)
            {
                TxtVoiceLevelingLiveEmpty.Visibility = Visibility.Visible;
                TxtVoiceLevelingLiveEmpty.Text = "Odaya katıldığınızda katılımcıların dengeleme durumu burada görünür.";
                ListVoiceLevelingPeers.Visibility = Visibility.Collapsed;
                ListVoiceLevelingPeers.Children.Clear();
                return;
            }

            var stats = _audio.GetActivePeerLevelingStats();
            if (stats.Count == 0)
            {
                TxtVoiceLevelingLiveEmpty.Visibility = Visibility.Visible;
                TxtVoiceLevelingLiveEmpty.Text = "Odada henüz başka katılımcı bulunmuyor.";
                ListVoiceLevelingPeers.Visibility = Visibility.Collapsed;
                ListVoiceLevelingPeers.Children.Clear();
                return;
            }

            TxtVoiceLevelingLiveEmpty.Visibility = Visibility.Collapsed;
            ListVoiceLevelingPeers.Visibility = Visibility.Visible;

            var items = stats.Take(6).ToList();

            if (ListVoiceLevelingPeers.Children.Count != items.Count)
            {
                ListVoiceLevelingPeers.Children.Clear();
                foreach (var item in items)
                {
                    var row = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        Margin = new Thickness(0, 2, 0, 2)
                    };

                    var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184)), VerticalAlignment = VerticalAlignment.Center };
                    var txtName = new TextBlock { Text = item.Value.Name, FontSize = 11.5, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 232, 240)), VerticalAlignment = VerticalAlignment.Center };
                    namePanel.Children.Add(dot);
                    namePanel.Children.Add(txtName);

                    var badge = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var txtGain = new TextBlock { FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    badge.Child = txtGain;

                    row.Children.Add(namePanel);
                    Grid.SetColumn(namePanel, 0);
                    row.Children.Add(badge);
                    Grid.SetColumn(badge, 1);

                    ListVoiceLevelingPeers.Children.Add(row);
                }
            }

            for (int i = 0; i < items.Count && i < ListVoiceLevelingPeers.Children.Count; i++)
            {
                if (ListVoiceLevelingPeers.Children[i] is Grid row &&
                    row.Children.Count >= 2 &&
                    row.Children[0] is StackPanel sp &&
                    row.Children[1] is Border badge &&
                    badge.Child is TextBlock txtGain)
                {
                    var item = items[i];
                    if (sp.Children.Count >= 2 && sp.Children[0] is Microsoft.UI.Xaml.Shapes.Ellipse dot && sp.Children[1] is TextBlock txtName)
                    {
                        txtName.Text = item.Value.Name;
                        dot.Fill = item.Value.Speaking
                            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94))
                            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 116, 139));
                    }

                    float gain = item.Value.GainDb;
                    if (gain >= 0.5f)
                    {
                        badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 34, 197, 94));
                        txtGain.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 74, 222, 128));
                        txtGain.Text = $"+{gain:F1} dB";
                    }
                    else if (gain <= -0.5f)
                    {
                        badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 245, 158, 11));
                        txtGain.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 191, 36));
                        txtGain.Text = $"{gain:F1} dB";
                    }
                    else
                    {
                        badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));
                        txtGain.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
                        txtGain.Text = "0.0 dB";
                    }
                }
            }
        }

        private void CmbInputDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents) return;
            if (CmbInputDevices?.SelectedItem is AudioDeviceOption opt)
            {
                _storage.Settings.InputDeviceId = opt.Id;
                _audio.RestartDevices();
                _storage.SaveSettings();
            }
        }

        private void CmbOutputDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents) return;
            if (CmbOutputDevices?.SelectedItem is AudioDeviceOption opt)
            {
                _storage.Settings.OutputDeviceId = opt.Id;
                _audio.RestartDevices();
                _storage.SaveSettings();
            }
        }

        private void SliderInputGain_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtInputGainVal == null) return;
            _storage.Settings.InputGainPercent = (float)Math.Round(e.NewValue);
            TxtInputGainVal.Text = $"%{(int)_storage.Settings.InputGainPercent}";
        }

        private void SliderOutputGain_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtOutputGainVal == null) return;
            _storage.Settings.OutputMasterPercent = (float)Math.Round(e.NewValue);
            TxtOutputGainVal.Text = $"%{(int)_storage.Settings.OutputMasterPercent}";
        }

        private void CmbNoiseEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbNoiseEngine == null) return;
            _storage.Settings.NoiseEngine = GetComboSelectedTag(CmbNoiseEngine) ?? "rnnoise";
            _audio.ApplySettingsFromStorage();
            _storage.SaveSettings();
            if (_isInVoiceRoom) UpdateStageSubtitleText();
        }

        private void SliderNoiseLevel_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtNoiseLevelLabel == null) return;
            int lvl = (int)Math.Round(e.NewValue);
            _storage.Settings.NoiseSuppressionLevel = lvl;
            UpdateNoiseLevelLabel(lvl);
            _audio.ApplySettingsFromStorage();
        }

        private void UpdateNoiseLevelLabel(int lvl)
        {
            if (TxtNoiseLevelLabel == null) return;
            string desc = lvl switch
            {
                1 => "Çok Hafif",
                2 => "Yumuşak",
                3 => "Dengeli",
                4 => "Güçlü",
                _ => "Maksimum Agresif"
            };
            TxtNoiseLevelLabel.Text = $"{lvl}/5 · {desc}";
        }

        private void TglAudioOption_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TglImpactGuard == null || TglHeadphoneAec == null) return;
            _storage.Settings.ImpactGuardEnabled = TglImpactGuard.IsOn;
            _storage.Settings.HeadphoneAecEnabled = TglHeadphoneAec.IsOn;
            _audio.ApplySettingsFromStorage();
        }

        private void CmbVoiceLevelingMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbVoiceLevelingMode == null) return;
            string mode = GetComboSelectedTag(CmbVoiceLevelingMode) ?? "normal";
            _storage.Settings.VoiceLevelingMode = mode;
            _storage.SaveSettings();
            UpdateVoiceLevelingDesc(mode);
            _audio.ApplySettingsFromStorage();
        }

        private void CmbVoiceLevelingTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbVoiceLevelingTarget == null) return;
            if (float.TryParse(GetComboSelectedTag(CmbVoiceLevelingTarget), out float tgt))
            {
                _storage.Settings.VoiceLevelingTargetDb = tgt;
                _storage.SaveSettings();
                _audio.ApplySettingsFromStorage();
            }
        }

        private void UpdateVoiceLevelingDesc(string mode)
        {
            if (TxtVoiceLevelingDesc == null) return;
            TxtVoiceLevelingDesc.Text = mode switch
            {
                "off" => "Herkes kendi gönderdiği seviyede duyulur.",
                "light" => "Küçük farkları yumuşatır; doğal dinamiği korur.",
                "strong" => "Çok farklı mikrofonlar için; arka plan sesi de bir miktar artabilir.",
                _ => "Kısık ve yüksek konuşanları belirgin şekilde eşitler."
            };
        }

        private void BtnOpenSpatialAudio_Click(object sender, RoutedEventArgs e)
        {
            OpenSpatialAudioModal();
        }

        private void BtnRecordMicTest_Click(object sender, RoutedEventArgs e)
        {
            if (!_audio.IsRecordingMicTest)
            {
                MicTestProgressBar.Value = 0;
                MicTestProgressBar.Visibility = Visibility.Visible;
                BtnRecordMicTest.Content = "⏹ Kaydı Bitir ve Dinle";
                TxtMicTestStatus.Text = "Konuşun... İşlenmiş sesiniz kaydediliyor (Maks. 30 sn)";

                _audio.OnMicTestProgress -= HandleMicTestProgress;
                _audio.OnMicTestProgress += HandleMicTestProgress;
                _audio.StartMicTestRecording();
            }
            else
            {
                _audio.OnMicTestProgress -= HandleMicTestProgress;
                bool ok = _audio.StopAndPlayMicTestRecording();
                MicTestProgressBar.Visibility = Visibility.Collapsed;
                BtnRecordMicTest.Content = "⏺ 30 Sn Kayıt Testi Başlat";
                TxtMicTestStatus.Text = ok ? "🔊 Kaydınız şu an kulaklığınıza oynatılıyor!" : "Kayıt çok kısa sürdü.";
            }
        }

        private void HandleMicTestProgress(float p)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                MicTestProgressBar.Value = p * 100.0;
                if (p >= 0.999f)
                {
                    BtnRecordMicTest.Content = "⏺ 30 Sn Kayıt Testi Başlat";
                    TxtMicTestStatus.Text = "🔊 30 saniyelik kaydınız oynatılıyor...";
                }
            });
        }

        private void TglListenMyVoice_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TglListenMyVoice == null || TxtMicTestStatus == null) return;
            bool listen = TglListenMyVoice.IsChecked == true;
            _audio.SetMicMonitor(listen);
            TxtMicTestStatus.Text = listen ? "🎧 Canlı mikrofon monitörü açık (Kendi sesinizi duyuyorsunuz)" : "Canlı dinleme kapatıldı.";
        }

        private void CmbInputMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbInputMode == null) return;
            string mode = GetComboSelectedTag(CmbInputMode) ?? "vad";
            _storage.Settings.InputMode = mode;
            if (PanelVadThreshold != null) PanelVadThreshold.Visibility = mode == "ptt" ? Visibility.Collapsed : Visibility.Visible;
            if (PanelPttKeybind != null) PanelPttKeybind.Visibility = mode == "ptt" ? Visibility.Visible : Visibility.Collapsed;
            _storage.SaveSettings();
        }

        private void SliderVadThreshold_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtVadThreshLabel == null) return;
            _storage.Settings.VadThresholdDb = (float)Math.Round(e.NewValue);
            TxtVadThreshLabel.Text = $"Ses Açılma Eşiği: {(int)_storage.Settings.VadThresholdDb} dB";
        }

        private void BtnAssignPttKey_Click(object sender, RoutedEventArgs e)
        {
            _isCapturingPttKey = true;
            BtnAssignPttKey.Content = "Bir tuşa basın...";
        }

        private void CaptureNewPttKey(VirtualKey key)
        {
            _isCapturingPttKey = false;
            _storage.Settings.PttVirtualKey = (int)key;
            _storage.Settings.PttKeyName = key.ToString();
            BtnAssignPttKey.Content = $"Tuş: {key} (Değiştir)";
            _storage.SaveSettings();
        }

        private void CmbOpusConfig_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbOpusBitrate == null || CmbOpusFrameMs == null) return;
            if (int.TryParse(GetComboSelectedTag(CmbOpusBitrate), out int kbps))
                _storage.Settings.OpusBitrateKbps = kbps;
            if (float.TryParse(GetComboSelectedTag(CmbOpusFrameMs), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fms))
                _storage.Settings.OpusFrameMs = fms;

            _audio.ApplySettingsFromStorage();
            _webrtc.RefreshCodecSettings();
            _storage.SaveSettings();
        }

        private void BtnEqPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string preset)
            {
                _storage.Settings.SoundProfile = preset;
                _audio.ApplySettingsFromStorage();
                _suppressSettingsEvents = true;
                SyncEqSlidersFromSettings();
                _suppressSettingsEvents = false;
                UpdateSoundProfileUi();
                _storage.SaveSettings();
                ShowToast($"🎚️ Ses profili uygulandı: {b.Content}");
            }
        }

        private void SyncEqSlidersFromSettings()
        {
            var s = _storage.Settings;
            SlEq80.Value = s.Eq80Hz;
            SlEq300.Value = s.Eq300Hz;
            SlEq1k.Value = s.Eq1kHz;
            SlEq35k.Value = s.Eq3_5kHz;
            SlEq8k.Value = s.Eq8kHz;
            UpdateEqLabels();
        }

        private void UpdateEqLabels()
        {
            var s = _storage.Settings;
            TxtEq80.Text = $"80 Hz\n{s.Eq80Hz:+0.0;-0.0;0.0} dB";
            TxtEq300.Text = $"300 Hz\n{s.Eq300Hz:+0.0;-0.0;0.0} dB";
            TxtEq1k.Text = $"1 kHz\n{s.Eq1kHz:+0.0;-0.0;0.0} dB";
            TxtEq35k.Text = $"3.5 kHz\n{s.Eq3_5kHz:+0.0;-0.0;0.0} dB";
            TxtEq8k.Text = $"8 kHz\n{s.Eq8kHz:+0.0;-0.0;0.0} dB";
        }

        private void SliderEqBand_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || SlEq80 == null || SlEq300 == null || SlEq1k == null || SlEq35k == null || SlEq8k == null) return;
            var s = _storage.Settings;
            s.SoundProfile = "custom";
            s.Eq80Hz = (float)SlEq80.Value;
            s.Eq300Hz = (float)SlEq300.Value;
            s.Eq1kHz = (float)SlEq1k.Value;
            s.Eq3_5kHz = (float)SlEq35k.Value;
            s.Eq8kHz = (float)SlEq8k.Value;
            UpdateEqLabels();
            UpdateSoundProfileUi();
            _audio.ApplySettingsFromStorage();
        }

        private void SliderCeilingLimiter_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtCeilingLimiterVal == null) return;
            _storage.Settings.MaxOutputCeilingDb = (float)Math.Round(e.NewValue, 1);
            TxtCeilingLimiterVal.Text = $"{_storage.Settings.MaxOutputCeilingDb:0.0} dB";
            _audio.ApplySettingsFromStorage();
        }

        private void CmbVideoDefaults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbDefaultRes == null || CmbDefaultFps == null) return;
            if (int.TryParse(GetComboSelectedTag(CmbDefaultRes), out int r)) _storage.Settings.DefaultStreamHeight = r;
            if (int.TryParse(GetComboSelectedTag(CmbDefaultFps), out int f)) _storage.Settings.DefaultStreamFps = f;
            _storage.SaveSettings();
        }

        private void SliderRelayChildren_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtRelayChildrenLabel == null) return;
            int v = (int)Math.Round(e.NewValue);
            _storage.Settings.P2PRelayMaxChildren = v;
            TxtRelayChildrenLabel.Text = $"P2P Hiyerarşik Ağaç Doğrudan Yük Limiti: Maksimum {v} Kişi";
            RefreshP2PRelayTreeLayout();
        }

        private void TglSetAllowAnnotations_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents) return;
            _storage.Settings.AllowViewerAnnotations = TglSetAllowAnnotations.IsOn;
            _storage.SaveSettings();

            if (TglAllowAnnotations != null && TglAllowAnnotations.IsOn != TglSetAllowAnnotations.IsOn)
            {
                TglAllowAnnotations.IsOn = TglSetAllowAnnotations.IsOn;
            }

            if (_screenCapture.IsSharing)
            {
                BroadcastPerm();
            }
        }

        private void CmbSetAnnotationFade_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents) return;
            string tag = GetComboSelectedTag(CmbSetAnnotationFade) ?? "8";
            if (int.TryParse(tag, out int fadeSec))
            {
                _storage.Settings.AnnotationFadeSeconds = fadeSec;
                _storage.SaveSettings();
                if (_screenCapture.IsSharing)
                {
                    BroadcastPerm();
                }
            }
        }

        private void TglSetShowAnnotationNames_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents) return;
            _storage.Settings.ShowAnnotationNames = TglSetShowAnnotationNames.IsOn;
            _storage.SaveSettings();
        }

        private void TxtSettingsProfileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TxtSettingsProfileName == null) return;
            string n = TxtSettingsProfileName.Text.Trim();
            if (!string.IsNullOrEmpty(n))
            {
                _storage.CurrentUser.Name = n;
                var me = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
                if (me != null) me.Name = n;
                RefreshSettingsSidebarProfile();
            }
        }

        private void TglAppearance_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || TglSettingRipples == null || TglSettingUiSounds == null) return;
            _storage.Settings.RippleAnimationsEnabled = TglSettingRipples.IsOn;
            _storage.Settings.UiSoundsEnabled = TglSettingUiSounds.IsOn;
            _storage.SaveSettings();
            RefreshStageRippleState(force: true);
        }

        private void CmbRealtimeEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || _suppressSettingsEvents || CmbRealtimeEngine == null) return;
            string? choice = GetComboSelectedTag(CmbRealtimeEngine);
            if (!string.IsNullOrEmpty(choice) && choice != _storage.Settings.RealtimeEngine)
            {
                _storage.Settings.RealtimeEngine = choice;
                _storage.SaveSettings();
                ShowToast($"Motor tercihi kaydedildi ({choice}). Değişikliğin etkili olması için uygulamayı yeniden başlatın.");
            }
        }

        private void BtnClearSavedRooms_Click(object sender, RoutedEventArgs e)
        {
            _storage.ClearAllPersistentRooms();
            LoadPersistentRooms();
            ShowToast("Tüm kayıtlı kalıcı odalar temizlendi");
        }

        private static string? GetComboSelectedTag(ComboBox? cmb)
        {
            if (cmb == null) return null;
            return (cmb.SelectedItem as ComboBoxItem)?.Tag as string;
        }

        private static void SelectComboByTag(ComboBox? cmb, string? wantedTag)
        {
            if (cmb == null || string.IsNullOrEmpty(wantedTag)) return;
            foreach (var obj in cmb.Items)
            {
                if (obj is ComboBoxItem cbi && string.Equals(cbi.Tag as string, wantedTag, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void UpdateActiveEngineDisplay()
        {
            if (TxtActiveEngine == null) return;

            if (_engine is RustRealtimeEngine)
            {
                uint abi = EngineNative.RUNA_ENGINE_ABI_VERSION;
                TxtActiveEngine.Text = $"Etkin motor: Rust (sürüm 0.1.0, ABI {abi})";
                TxtActiveEngine.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153));
                if (TxtActiveEngineDetails != null)
                {
                    TxtActiveEngineDetails.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(RustRealtimeEngine.LastInitErrorSummary))
                {
                    TxtActiveEngine.Text = $"Etkin motor: Standart (Rust başlatılamadı: {RustRealtimeEngine.LastInitErrorSummary})";
                    TxtActiveEngine.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 191, 36));
                    if (TxtActiveEngineDetails != null && !string.IsNullOrEmpty(RustRealtimeEngine.LastInitErrorDetails))
                    {
                        TxtActiveEngineDetails.Text = $"Hata Ayrıntısı:\n{RustRealtimeEngine.LastInitErrorDetails}";
                        TxtActiveEngineDetails.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    TxtActiveEngine.Text = "Etkin motor: Standart";
                    TxtActiveEngine.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 203, 213, 225));
                    if (TxtActiveEngineDetails != null)
                    {
                        TxtActiveEngineDetails.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private void BtnOpenEngineLogDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logDir = System.IO.Path.Combine(_storage.AppDataFolder, "logs");
                if (!System.IO.Directory.Exists(logDir))
                {
                    System.IO.Directory.CreateDirectory(logDir);
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast($"Klasör açılamadı: {ex.Message}");
            }
        }
    }
}
