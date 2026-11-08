using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace RunaVoice_WinUI
{
    public sealed partial class MainPage
    {
        // =====================================================================
        // AYARLAR PENCERESİ: ELECTRON "YENİ NESİL AYARLAR" AKORDİYON MENÜSÜ
        // Kategori butonu → paneli açar ve alt bölüm çekmecesini genişletir,
        // alt bölüm butonu → panel içindeki çapa bölüme pürüzsüz kaydırır.
        // =====================================================================

        private sealed class SettingsNavSub
        {
            public string Title = "";
            public FrameworkElement Section = null!;
            public Button Button = null!;
            public Rectangle Indicator = null!;
        }

        private sealed class SettingsNavCategory
        {
            public string Id = "";
            public string Title = "";
            public string SearchText = "";
            public FrameworkElement Pane = null!;
            public TextBlock GroupHeader = null!;
            public StackPanel Root = null!;
            public Button Button = null!;
            public FontIcon Icon = null!;
            public TextBlock Label = null!;
            public FontIcon Chevron = null!;
            public Border Drawer = null!;
            public List<SettingsNavSub> Subs = new();
        }

        private readonly List<SettingsNavCategory> _settingsNav = new();
        private SettingsNavCategory? _activeSettingsCategory;
        private SettingsNavSub? _activeSettingsSub;
        private TextBlock? _settingsNoResultText;

        private static SolidColorBrush NavBrush(uint argb) =>
            new(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        private void BuildSettingsNav()
        {
            if (_settingsNav.Count > 0) return;

            const string appGroup = "UYGULAMA AYARLARI";
            const string devGroup = "GELİŞTİRİCİ";

            var defs = new (string Group, string Id, string Title, string Glyph, FrameworkElement Pane, string Keywords, (string Title, FrameworkElement Section)[] Subs)[]
            {
                (appGroup, "appearance", "Görünüm", "", PaneSettingsAppearance, "profil avatar fotoğraf ad animasyon dalga ses efekti",
                    new[] { ("Kullanıcı Profili", (FrameworkElement)SecAppProfile), ("Arayüz & Animasyonlar", SecAppUi) }),
                (appGroup, "voice", "Ses Ayarları", "", PaneSettingsVoice, "mikrofon hoparlör kulaklık rnnoise eq ekolayzır limiter vad ptt opus bitrate gecikme dengeleme normalizasyon",
                    new[] { ("Cihazlar", (FrameworkElement)SecVoiceDevices), ("Gürültü Engelleme", SecVoiceNoise),
                            ("Ses Profili", SecVoiceProfile), ("Ses Dengeleme", SecVoiceLeveling),
                            ("Giriş Modu", SecVoiceInput), ("Mikrofon Testi", SecVoiceTest) }),
                (appGroup, "video", "Görüntü ve Yayın", "", PaneSettingsVideo, "ekran paylaşımı çözünürlük fps röle çizim işaretleme kalem annotation",
                    new[] { ("Yayın Kalitesi & FPS", (FrameworkElement)SecStreamQuality), ("P2P Hibrit Dağıtım", SecStreamRelay), ("Ekran Üstü Çizim", SecStreamAnnotations) }),
                (appGroup, "network", "Ağ ve Bağlantı", "", PaneSettingsNetwork, "mqtt broker stun turn oturum sinyal",
                    new[] { ("P2P Sinyalleşme & Ağ", (FrameworkElement)SecNetStatus) }),
                (appGroup, "hotkeys", "Kısayollar", "", PaneSettingsHotkeys, "klavye tuş",
                    new[] { ("Hızlı Tuşlar", (FrameworkElement)SecHotkeyList) }),
                (devGroup, "advanced", "Gelişmiş", "", PaneSettingsAdvanced, "motor rust c# deneysel veri oda temizle sil",
                    new[] {
                        ("Gerçek Zamanlı Motor", (FrameworkElement)SecAdvEngine),
                        ("Önbellek & Yerel Veri", (FrameworkElement)SecAdvData)
                    }),
            };

            TextBlock? header = null;
            string? lastGroup = null;
            foreach (var d in defs)
            {
                if (d.Group != lastGroup)
                {
                    header = new TextBlock
                    {
                        Text = d.Group,
                        FontSize = 10.5,
                        FontWeight = FontWeights.ExtraBold,
                        CharacterSpacing = 60,
                        Foreground = NavBrush(0xFF80848E),
                        Margin = new Thickness(8, lastGroup == null ? 6 : 14, 8, 4)
                    };
                    SettingsNavHost.Children.Add(header);
                    lastGroup = d.Group;
                }

                var cat = CreateSettingsNavCategory(d.Id, d.Title, d.Glyph, d.Pane, header!, d.Subs);
                cat.SearchText = string.Join(" ", new[] { d.Title, d.Keywords }.Concat(d.Subs.Select(s => s.Title)));
                _settingsNav.Add(cat);
                SettingsNavHost.Children.Add(cat.Root);
            }

            _settingsNoResultText = new TextBlock
            {
                Text = "Eşleşen ayar bulunamadı.",
                FontSize = 12,
                Foreground = NavBrush(0xFF80848E),
                Margin = new Thickness(10, 8, 10, 0),
                Visibility = Visibility.Collapsed
            };
            SettingsNavHost.Children.Add(_settingsNoResultText);

            var v = typeof(App).Assembly.GetName().Version;
            if (v != null) TxtSettingsAppVersion.Text = $"Sürüm {v.Major}.{v.Minor}.{v.Build} (WinUI 3 Native)";

            BtnSettingsClose.PointerEntered += (_, _) =>
            {
                BtnSettingsClose.Background = NavBrush(0x14FFFFFF);
                BtnSettingsClose.Foreground = NavBrush(0xFFFFFFFF);
            };
            BtnSettingsClose.PointerExited += (_, _) =>
            {
                BtnSettingsClose.Background = NavBrush(0x00000000);
                BtnSettingsClose.Foreground = NavBrush(0xFFB5BAC1);
            };
        }

        private SettingsNavCategory CreateSettingsNavCategory(string id, string title, string glyph, FrameworkElement pane,
            TextBlock groupHeader, (string Title, FrameworkElement Section)[] subs)
        {
            var cat = new SettingsNavCategory { Id = id, Title = title, Pane = pane, GroupHeader = groupHeader };

            cat.Icon = new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            cat.Label = new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            cat.Chevron = new FontIcon
            {
                Glyph = "",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform()
            };

            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            left.Children.Add(cat.Icon);
            left.Children.Add(cat.Label);

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(left);
            Grid.SetColumn(cat.Chevron, 1);
            content.Children.Add(cat.Chevron);

            cat.Button = CreateSettingsNavButton(content, new Thickness(10, 8, 10, 8));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cat.Button, title);
            cat.Button.Click += (_, _) => SelectSettingsCategory(cat, null);
            cat.Button.PointerEntered += (_, _) => ApplySettingsCategoryVisual(cat, hover: true);
            cat.Button.PointerExited += (_, _) => ApplySettingsCategoryVisual(cat, hover: false);

            // Alt bölüm çekmecesi: sol ince dikey çizgi + alt butonlar (aktif olanın üstünde beyaz gösterge)
            var subsPanel = new StackPanel { Spacing = 2 };
            foreach (var (subTitle, section) in subs)
            {
                var sub = new SettingsNavSub { Title = subTitle, Section = section };
                sub.Button = CreateSettingsNavButton(subTitle, new Thickness(10, 6, 10, 6));
                sub.Button.FontSize = 13;
                sub.Button.HorizontalContentAlignment = HorizontalAlignment.Left;
                sub.Indicator = new Rectangle
                {
                    Width = 2.5,
                    RadiusX = 1.25,
                    RadiusY = 1.25,
                    Fill = NavBrush(0xFFFFFFFF),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(-13.5, 3, 0, 3),
                    Visibility = Visibility.Collapsed
                };
                sub.Button.Click += (_, _) => SelectSettingsCategory(cat, sub);
                sub.Button.PointerEntered += (_, _) => ApplySettingsSubVisual(sub, hover: true);
                sub.Button.PointerExited += (_, _) => ApplySettingsSubVisual(sub, hover: false);

                var holder = new Grid();
                holder.Children.Add(sub.Button);
                holder.Children.Add(sub.Indicator);
                subsPanel.Children.Add(holder);

                cat.Subs.Add(sub);
                ApplySettingsSubVisual(sub, hover: false);
            }

            cat.Drawer = new Border
            {
                Margin = new Thickness(14, 3, 0, 6),
                Padding = new Thickness(12, 0, 0, 0),
                BorderBrush = NavBrush(0x24FFFFFF),
                BorderThickness = new Thickness(1.5, 0, 0, 0),
                Child = subsPanel,
                Visibility = Visibility.Collapsed
            };

            cat.Root = new StackPanel();
            cat.Root.Children.Add(cat.Button);
            cat.Root.Children.Add(cat.Drawer);

            ApplySettingsCategoryVisual(cat, hover: false);
            return cat;
        }

        private static Button CreateSettingsNavButton(object content, Thickness padding) => new()
        {
            Style = (Style)Application.Current.Resources["RunaCleanButtonStyle"],
            Content = content,
            Padding = padding,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = NavBrush(0x00000000)
        };

        private void ApplySettingsCategoryVisual(SettingsNavCategory cat, bool hover)
        {
            bool active = cat == _activeSettingsCategory;
            cat.Button.Background = NavBrush(active ? 0xFF35373Cu : hover ? 0xFF2B2D31u : 0x00000000u);
            cat.Button.Foreground = NavBrush(active ? 0xFFFFFFFFu : hover ? 0xFFDBDEE1u : 0xFF949BA4u);
            cat.Icon.Foreground = NavBrush(active ? 0xFFFFFFFFu : hover ? 0xFFDBDEE1u : 0xFF80848Eu);
            cat.Chevron.Foreground = NavBrush(active ? 0xFFFFFFFFu : 0xFF949BA4u);
            cat.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            cat.Chevron.Opacity = active || hover ? 1 : 0;
            ((RotateTransform)cat.Chevron.RenderTransform).Angle = active ? 90 : 0;
        }

        private void ApplySettingsSubVisual(SettingsNavSub sub, bool hover)
        {
            bool active = sub == _activeSettingsSub;
            sub.Button.Background = NavBrush(active ? 0xFF3E4046u : hover ? 0x0DFFFFFFu : 0x00000000u);
            sub.Button.Foreground = NavBrush(active ? 0xFFFFFFFFu : hover ? 0xFFDBDEE1u : 0xFF949BA4u);
            sub.Button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            sub.Indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Eski sekme kimliklerini ("voice", "eq", "video", "appearance", "network") ve
        /// yeni kategori/bölüm kısayollarını ("hotkeys", "advanced", "mictest") karşılar.
        /// </summary>
        private void SwitchSettingsTab(string tab)
        {
            BuildSettingsNav();
            string catId = tab switch
            {
                "eq" or "mictest" => "voice",
                _ => tab
            };
            var cat = _settingsNav.FirstOrDefault(c => c.Id == catId) ?? _settingsNav[0];
            FrameworkElement? targetSection = tab switch
            {
                "eq" => SecVoiceProfile,
                "mictest" => SecVoiceTest,
                _ => null
            };
            if (tab == "eq" && ExpanderVoiceEq != null)
            {
                ExpanderVoiceEq.IsExpanded = true;
            }
            SelectSettingsCategory(cat, cat.Subs.FirstOrDefault(s => s.Section == targetSection));
        }

        private void SelectSettingsCategory(SettingsNavCategory cat, SettingsNavSub? sub)
        {
            _activeSettingsCategory = cat;
            _activeSettingsSub = sub ?? cat.Subs.FirstOrDefault();

            foreach (var c in _settingsNav)
            {
                c.Pane.Visibility = c == cat ? Visibility.Visible : Visibility.Collapsed;
                ApplySettingsCategoryVisual(c, hover: false);
                foreach (var s in c.Subs) ApplySettingsSubVisual(s, hover: false);
            }
            TxtSettingsPaneTitle.Text = cat.Title;
            RefreshSettingsNavFilter();

            if (sub == null) SettingsContentScroller.ChangeView(null, 0, null, true);
            else ScrollToSettingsSection(sub.Section);
        }

        private void ScrollToSettingsSection(FrameworkElement section)
        {
            // Panel az önce görünür olduysa konumların hesaplanması için yerleşimi tamamla
            SettingsContentScroller.UpdateLayout();
            if (SettingsContentScroller.Content is not UIElement content) return;
            var pos = section.TransformToVisual(content).TransformPoint(new Point(0, 0));
            SettingsContentScroller.ChangeView(null, Math.Max(0, pos.Y), null, false);
        }

        private void TxtSettingsSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshSettingsNavFilter();

        private void RefreshSettingsNavFilter()
        {
            string q = TxtSettingsSearch?.Text.Trim() ?? "";
            bool filtering = q.Length > 0;
            bool anyMatch = false;

            foreach (var c in _settingsNav)
            {
                bool match = !filtering || c.SearchText.Contains(q, StringComparison.CurrentCultureIgnoreCase);
                anyMatch |= match;
                c.Root.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                c.Drawer.Visibility = match && (c == _activeSettingsCategory || filtering) ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var header in _settingsNav.Select(c => c.GroupHeader).Distinct())
            {
                bool visible = _settingsNav.Any(c => c.GroupHeader == header && c.Root.Visibility == Visibility.Visible);
                header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_settingsNoResultText != null)
                _settingsNoResultText.Visibility = anyMatch ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshSettingsSidebarProfile()
        {
            var user = _storage.CurrentUser;
            TxtSettingsSidebarName.Text = user.Name;
            string sid = user.VoiceSessionId ?? "";
            TxtSettingsSidebarSub.Text = sid.Length >= 4 ? $"#{sid[..4].ToUpperInvariant()} • Çevrimiçi" : "Çevrimiçi";
        }

        private void UpdateSoundProfileUi()
        {
            string profile = _storage.Settings.SoundProfile ?? "custom";

            foreach (var child in EqPresetPillGrid.Children)
            {
                if (child is not Button pill) continue;
                bool active = string.Equals(pill.Tag as string, profile, StringComparison.OrdinalIgnoreCase);
                if (active)
                {
                    var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                    grad.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 124, 58, 237), Offset = 0 });
                    grad.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 168, 85, 247), Offset = 1 });
                    pill.Background = grad;
                    pill.Foreground = NavBrush(0xFFFFFFFF);
                }
                else
                {
                    pill.Background = NavBrush(0x00000000);
                    pill.Foreground = NavBrush(0xFF94A3B8);
                }
            }

            (TxtSoundProfileBadge.Text, TxtSoundProfileDesc.Text) = profile switch
            {
                "clean" => ("TEMİZ VOKAL • ÖNERİLEN", "Dip gürültü ve masa titreşimleri kesilir, konuşma anlaşılırlığı belirginleşir; günlük sohbet için en dengeli profil."),
                "podcast" => ("RADYO & YAYINCI", "Tok radyo spikeri tınısı ve hava parlaklığı; yayın ve podcast için dolgun, dengeli vokal."),
                "clarity" => ("VOKAL PARLATICI", "Koyu veya boğuk mikrofonlara orta-tiz bölgede kristal netlik kazandırır."),
                "deep" => ("BAS & TOKLUK", "Alt frekanslarda zengin, sıcak ve tok bir bas gövdesi verir."),
                "flat" => ("SAF HAM SES", "Ekolayzır eğrisi düz tutulur; mikrofonun kendi doğal sesi iletilir."),
                _ => ("ÖZEL EQ", "Bantları elle ayarladınız; kişisel ekolayzır eğriniz kullanılıyor.")
            };
        }
    }
}
