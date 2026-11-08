using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RunaVoice_WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace RunaVoice_WinUI
{
    public sealed partial class MainWindow : Window
    {
        public static new MainWindow? Current { get; private set; }
        private readonly bool _isTestAccount;

        public event Action? OnOpenSettingsRequested;
        public event Action? OnTogglePinCurrentRoomRequested;

        public MainWindow()
        {
            InitializeComponent();
            Current = this;

            try
            {
                foreach (var arg in Environment.GetCommandLineArgs())
                {
                    if (arg.Equals("--profile-2", StringComparison.OrdinalIgnoreCase) ||
                        arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        _isTestAccount = true;
                        break;
                    }
                }
            }
            catch { }

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragArea);

            try
            {
                AppWindow.Title = _isTestAccount ? "RUNA — TestAccount" : "RUNA";
                var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var workArea = displayArea.WorkArea;
                int winW = Math.Clamp((int)(workArea.Width * 0.90), 1360, Math.Max(1360, workArea.Width - 32));
                int winH = Math.Clamp((int)(workArea.Height * 0.91), 840, Math.Max(840, workArea.Height - 24));
                int posX = workArea.X + Math.Max(0, (workArea.Width - winW) / 2);
                int posY = workArea.Y + Math.Max(0, (workArea.Height - winH) / 2);
                AppWindow.MoveAndResize(new RectInt32(posX, posY, winW, winH));
                string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(icoPath)) AppWindow.SetIcon(icoPath);

                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var tb = AppWindow.TitleBar;
                    tb.ButtonBackgroundColor = Colors.Transparent;
                    tb.ButtonInactiveBackgroundColor = Colors.Transparent;
                    tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 148, 163, 184);
                    tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 100, 116, 139);
                    tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(26, 255, 255, 255);
                    tb.ButtonHoverForegroundColor = Colors.White;
                    tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(45, 139, 92, 246);
                }
            }
            catch { }

            UiAnimationHelper.AttachInteractiveHover(BtnTitleBarSettings, 1.12f, 0f, 0.92f);
            UiAnimationHelper.AttachInteractiveHover(BtnTitleBarCopy, 1.04f, -0.5f, 0.95f);
            UiAnimationHelper.AttachInteractiveHover(BtnTitleBarPin, 1.04f, -0.5f, 0.95f);

            Closed += MainWindow_Closed;
            RootFrame.Navigate(typeof(MainPage));
            UpdateTitleBarRoomState(null, false);
        }

        private async void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                if (RootFrame.Content is MainPage page)
                {
                    await page.ShutdownAllServicesAsync();
                }
            }
            catch { }
            Environment.Exit(0);
        }

        private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTitleBarInteractivePassthrough();
        }

        public void UpdateTitleBarInteractivePassthrough()
        {
            try
            {
                if (AppTitleBar.XamlRoot == null) return;
                double scale = AppTitleBar.XamlRoot.RasterizationScale;
                var rects = new List<RectInt32>();

                // Sadece odadayken Kopyala/Sabitle butonları için tıklama geçirgenliği açılır; Lobide rozet üzerinden pencere sürüklenebilir
                if (BtnTitleBarCopy.Visibility == Visibility.Visible && RoomCodeBadge.ActualWidth > 0)
                {
                    var t = RoomCodeBadge.TransformToVisual(null);
                    var pt = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    rects.Add(new RectInt32(
                        (int)(pt.X * scale),
                        (int)(pt.Y * scale),
                        (int)(RoomCodeBadge.ActualWidth * scale),
                        (int)(RoomCodeBadge.ActualHeight * scale)));
                }

                if (BtnTitleBarSettings.ActualWidth > 0)
                {
                    var t2 = BtnTitleBarSettings.TransformToVisual(null);
                    var pt2 = t2.TransformPoint(new Windows.Foundation.Point(0, 0));
                    rects.Add(new RectInt32(
                        (int)(pt2.X * scale),
                        (int)(pt2.Y * scale),
                        (int)(BtnTitleBarSettings.ActualWidth * scale),
                        (int)(BtnTitleBarSettings.ActualHeight * scale)));
                }

                var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
                nonClient.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
            }
            catch { }
        }

        public void SetFullscreenMode(bool fullscreen)
        {
            try
            {
                if (fullscreen)
                {
                    TitleBarRow.Height = new GridLength(0);
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                else
                {
                    TitleBarRow.Height = new GridLength(38);
                    AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    UpdateTitleBarInteractivePassthrough();
                }
            }
            catch { }
        }

        public bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

        public void UpdateTitleBarRoomState(string? roomCode, bool isPinned = false)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (string.IsNullOrEmpty(roomCode))
                {
                    // LOBİDE: Ortada [● ODA | Lobi] rozeti görünür, Kopyala/Sabitle gizlidir, durum noktası gri (Madde 8)
                    TxtTitleBarRoomCode.Text = "Lobi";
                    TitleBarStatusHalo.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 148, 163, 184));
                    TitleBarStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 148, 163, 184));
                    RoomCodeBadge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 255, 255, 255));
                    BtnTitleBarCopy.Visibility = Visibility.Collapsed;
                    BtnTitleBarPin.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // SESLİ ODADA: Ortada [● ODA | RUNA-XXXX | Kopyala | Sabitle] görünür
                    TxtTitleBarRoomCode.Text = roomCode;
                    TitleBarStatusHalo.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0x22, 0xC5, 0x5E));
                    TitleBarStatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));
                    RoomCodeBadge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6));
                    UpdatePinVisualState(isPinned);
                    BtnTitleBarCopy.Visibility = Visibility.Visible;
                    BtnTitleBarPin.Visibility = Visibility.Visible;
                }

                await Task.Delay(40);
                UpdateTitleBarInteractivePassthrough();
            });
        }

        public void UpdatePinVisualState(bool isPinned)
        {
            TxtPinIcon.Text = isPinned ? "★" : "☆";
            TxtPinLabel.Text = isPinned ? "Sabitlendi" : "Sabitle";
            BtnTitleBarPin.Background = isPinned
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(55, 251, 191, 36))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255));
        }

        private void BtnTitleBarSettings_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var white = new SolidColorBrush(Colors.White);
            TitleSettingsGearInner.Stroke = white;
            TitleSettingsGearOuter.Stroke = white;
            BtnTitleBarSettings.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255));
        }

        private void BtnTitleBarSettings_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            var muted = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
            TitleSettingsGearInner.Stroke = muted;
            TitleSettingsGearOuter.Stroke = muted;
            BtnTitleBarSettings.Background = new SolidColorBrush(Colors.Transparent);
        }

        private async void BtnTitleBarCopy_Click(object sender, RoutedEventArgs e)
        {
            var code = TxtTitleBarRoomCode.Text;
            if (string.IsNullOrEmpty(code) || code == "Lobi") return;

            try
            {
                var package = new DataPackage();
                package.SetText(code);
                Clipboard.SetContent(package);

                IconCopyNormal.Visibility = Visibility.Collapsed;
                IconCopyCheck.Visibility = Visibility.Visible;
                TxtCopyBtnLabel.Text = "Kopyalandı ✓";
                TxtCopyBtnLabel.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 134, 239, 172));
                await Task.Delay(1500);
                IconCopyCheck.Visibility = Visibility.Collapsed;
                IconCopyNormal.Visibility = Visibility.Visible;
                TxtCopyBtnLabel.Text = "Kopyala";
                TxtCopyBtnLabel.Foreground = new SolidColorBrush(Colors.White);
            }
            catch { }
        }

        private void BtnTitleBarPin_Click(object sender, RoutedEventArgs e)
        {
            OnTogglePinCurrentRoomRequested?.Invoke();
        }

        private void BtnTitleBarSettings_Click(object sender, RoutedEventArgs e)
        {
            OnOpenSettingsRequested?.Invoke();
        }
    }
}
