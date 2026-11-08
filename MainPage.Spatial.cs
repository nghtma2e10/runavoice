using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using RunaVoice_WinUI.Models;
using RunaVoice_WinUI.Services;
using Windows.Foundation;
using Windows.UI;

namespace RunaVoice_WinUI
{
    public sealed partial class MainPage
    {
        private const float RadarCenter = 180.0f;
        private const float RadarMinRadius = 45.0f;
        private const float RadarMaxRadius = 140.0f;

        private bool _isSpatialModalOpen = false;
        private bool _suppressSpatialEvents = false;

        private readonly List<SpatialParticipantItem> _spatialItems = new();
        private SpatialParticipantItem? _selectedSpatialItem;
        private SpatialParticipantItem? _draggingSpatialItem;

        private sealed class SpatialParticipantItem
        {
            public string Name { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public ImageSource? AvatarBitmap { get; set; }
            public float AzimuthDeg { get; set; } = 0f;
            public float Distance { get; set; } = 0.2f;
            public bool HasManualPosition { get; set; } = false;
            public bool IsSpeaking { get; set; } = false;
            public bool IsVirtual { get; set; } = false;
            public Border? VisualElement { get; set; }
            public Border? AvatarRingBorder { get; set; }
        }

        public void OpenSpatialAudioModal()
        {
            OpenSpatialAudioModalForPeer(null);
        }

        public void OpenSpatialAudioModalForPeer(string? selectPeerName)
        {
            _isSpatialModalOpen = true;
            _suppressSpatialEvents = true;

            var s = _storage.Settings;
            TglSpatialAudio.IsOn = s.SpatialAudioEnabled;
            TglSpatialSpeakerMode.IsOn = s.SpatialSpeakerMode;

            // Yerleşim modu (auto / manual)
            string mode = string.Equals(s.SpatialAudioMode, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto";
            foreach (ComboBoxItem item in CmbSpatialMode.Items)
            {
                if (string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
                {
                    CmbSpatialMode.SelectedItem = item;
                    break;
                }
            }

            // Mono çıkış cihazı uyarısı
            bool isMono = _audio.IsOutputDeviceMono;
            PnlSpatialMonoWarning.Visibility = isMono ? Visibility.Visible : Visibility.Collapsed;

            _suppressSpatialEvents = false;

            RefreshSpatialParticipants(selectPeerName);
            UpdateSpatialMenuBadge();

            UiAnimationHelper.ShowModalAnimated(SpatialAudioModalOverlay, SpatialAudioModalCard);
        }

        private async void CloseSpatialAudioModal()
        {
            _isSpatialModalOpen = false;
            _draggingSpatialItem = null;
            UpdateSpatialMenuBadge();
            await UiAnimationHelper.HideModalAnimatedAsync(SpatialAudioModalOverlay, SpatialAudioModalCard);
        }

        private void BtnCloseSpatialAudio_Click(object sender, RoutedEventArgs e) => CloseSpatialAudioModal();
        private void BtnSpatialClose_Click(object sender, RoutedEventArgs e) => CloseSpatialAudioModal();
        private void BackdropCloseSpatialAudio_Tapped(object sender, TappedRoutedEventArgs e) => CloseSpatialAudioModal();

        private void UpdateSpatialMenuBadge()
        {
            if (BtnMenuSpatialAudio == null) return;
            bool active = _storage.Settings.SpatialAudioEnabled;
            BtnMenuSpatialAudio.Text = active ? "🎧  3B Uzamsal Ses (Açık)" : "🎧  3B Uzamsal Ses";
        }

        private void TglSpatialAudio_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSpatialEvents) return;
            _storage.Settings.SpatialAudioEnabled = TglSpatialAudio.IsOn;
            _storage.SaveSettings();
            _audio.ApplySettingsFromStorage();
            UpdateSpatialMenuBadge();
            RedrawSpatialRadar();
        }

        private void CmbSpatialMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSpatialEvents || CmbSpatialMode.SelectedItem is not ComboBoxItem sel) return;
            string mode = (sel.Tag as string) ?? "auto";
            _storage.Settings.SpatialAudioMode = mode;
            _storage.SaveSettings();

            if (mode == "auto")
            {
                ComputeAutoPositions(_spatialItems);
                ApplySpatialPositionsToAudioEngine();
            }
            RedrawSpatialRadar();
        }

        private void TglSpatialSpeakerMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSpatialEvents) return;
            _storage.Settings.SpatialSpeakerMode = TglSpatialSpeakerMode.IsOn;
            _storage.SaveSettings();
            _audio.ApplySettingsFromStorage();
        }

        private void RefreshSpatialParticipants(string? preselectPeerName)
        {
            _spatialItems.Clear();

            // Odadaki diğer katılımcıları al
            var participants = ActiveParticipants.Where(p => !p.IsSelf).ToList();

            if (participants.Count > 0)
            {
                foreach (var p in participants)
                {
                    var cfg = _storage.GetPeerAudioConfig(p.Name);
                    var item = new SpatialParticipantItem
                    {
                        Name = p.Name,
                        DisplayName = p.Name,
                        AvatarBitmap = p.AvatarBitmap,
                        AzimuthDeg = cfg.SpatialAzimuthDeg,
                        Distance = cfg.SpatialDistance,
                        HasManualPosition = cfg.HasManualSpatialPosition,
                        IsSpeaking = p.IsSpeaking,
                        IsVirtual = false
                    };
                    _spatialItems.Add(item);
                }

                // Otomatik moddaysa veya elle konumu olmayanlar varsa otomatik yerleştir
                bool isManual = string.Equals(_storage.Settings.SpatialAudioMode, "manual", StringComparison.OrdinalIgnoreCase);
                if (!isManual)
                {
                    ComputeAutoPositions(_spatialItems);
                }
                else
                {
                    // Elle modda olup henüz elle ayarlanmamışları otomatik yayın boşluklarına yerleştir
                    var unset = _spatialItems.Where(x => !x.HasManualPosition).ToList();
                    if (unset.Count > 0)
                    {
                        ComputeAutoPositions(unset);
                    }
                }
            }
            else
            {
                // Odada kimse yoksa test amaçlı sanal katılımcı
                var sampleItem = new SpatialParticipantItem
                {
                    Name = "__sample_speaker__",
                    DisplayName = "Örnek Katılımcı",
                    AvatarBitmap = new BitmapImage(new Uri("ms-appx:///Assets/avatars/kaan.jpg")),
                    AzimuthDeg = 45f,
                    Distance = 0.2f,
                    HasManualPosition = false,
                    IsSpeaking = false,
                    IsVirtual = true
                };
                _spatialItems.Add(sampleItem);
            }

            // Seçili öğeyi belirle
            if (!string.IsNullOrEmpty(preselectPeerName))
            {
                _selectedSpatialItem = _spatialItems.FirstOrDefault(x => string.Equals(x.Name, preselectPeerName, StringComparison.OrdinalIgnoreCase));
            }
            _selectedSpatialItem ??= _spatialItems.FirstOrDefault();

            RedrawSpatialRadar();
            UpdateSelectedSpatialInspector();
            ApplySpatialPositionsToAudioEngine();
        }

        private void ComputeAutoPositions(List<SpatialParticipantItem> list)
        {
            int n = list.Count;
            if (n == 0) return;
            if (n == 1)
            {
                list[0].AzimuthDeg = 0f;
                list[0].Distance = 0.2f;
                return;
            }
            if (n == 2)
            {
                list[0].AzimuthDeg = -30f;
                list[0].Distance = 0.2f;
                list[1].AzimuthDeg = 30f;
                list[1].Distance = 0.2f;
                return;
            }

            float span = n >= 6 ? 150f : 120f;
            float step = span / (n - 1);
            float start = -span / 2.0f;
            for (int i = 0; i < n; i++)
            {
                list[i].AzimuthDeg = start + (i * step);
                list[i].Distance = 0.2f;
            }
        }

        private void RedrawSpatialRadar()
        {
            if (CanvasSpatialRadar == null) return;
            CanvasSpatialRadar.Children.Clear();

            // 1. Merkezde "Sen" avatarı ve göstergesi
            var selfBorder = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(21),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 6, 182, 212)), // Cyan
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(255, 14, 11, 29)),
                IsHitTestVisible = false
            };
            var selfEllipse = new Ellipse
            {
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var selfParticipant = ActiveParticipants.FirstOrDefault(p => p.IsSelf);
            if (selfParticipant?.AvatarBitmap != null)
            {
                selfEllipse.Fill = new ImageBrush { ImageSource = selfParticipant.AvatarBitmap, Stretch = Stretch.UniformToFill };
            }
            else
            {
                selfEllipse.Fill = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
            }
            selfBorder.Child = selfEllipse;
            Canvas.SetLeft(selfBorder, RadarCenter - 21);
            Canvas.SetTop(selfBorder, RadarCenter - 21);
            CanvasSpatialRadar.Children.Add(selfBorder);

            var selfBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 6, 182, 212)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            };
            selfBadge.Child = new TextBlock
            {
                Text = "SEN",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.Black)
            };
            Canvas.SetLeft(selfBadge, RadarCenter - 14);
            Canvas.SetTop(selfBadge, RadarCenter + 22);
            CanvasSpatialRadar.Children.Add(selfBadge);

            // 2. Katılımcı avatarları
            foreach (var item in _spatialItems)
            {
                CreateParticipantRadarVisual(item);
            }
        }

        private void CreateParticipantRadarVisual(SpatialParticipantItem item)
        {
            var root = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = item
            };

            bool isSelected = item == _selectedSpatialItem;

            var ringBorder = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(21),
                BorderThickness = new Thickness(isSelected ? 2.5 : 1.5),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromArgb(255, 168, 85, 247)) // Mor
                    : (item.IsSpeaking
                        ? new SolidColorBrush(Color.FromArgb(255, 34, 197, 94)) // Yeşil
                        : new SolidColorBrush(Color.FromArgb(140, 148, 163, 184))), // Gri
                Background = new SolidColorBrush(Color.FromArgb(255, 14, 11, 29))
            };

            var avatar = new Ellipse
            {
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (item.AvatarBitmap != null)
            {
                avatar.Fill = new ImageBrush { ImageSource = item.AvatarBitmap, Stretch = Stretch.UniformToFill };
            }
            else
            {
                avatar.Fill = new SolidColorBrush(Color.FromArgb(255, 139, 92, 246));
            }
            ringBorder.Child = avatar;
            root.Children.Add(ringBorder);

            var labelBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 10, 7, 22)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            labelBorder.Child = new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 9.5,
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = isSelected
                    ? new SolidColorBrush(Color.FromArgb(255, 233, 213, 255))
                    : new SolidColorBrush(Color.FromArgb(255, 203, 213, 225)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 70
            };
            root.Children.Add(labelBorder);

            item.VisualElement = ringBorder;
            item.AvatarRingBorder = ringBorder;

            PositionItemOnRadar(item, root);
            CanvasSpatialRadar.Children.Add(root);
        }

        private void PositionItemOnRadar(SpatialParticipantItem item, UIElement element)
        {
            float rad = item.AzimuthDeg * (MathF.PI / 180.0f);
            float r = RadarMinRadius + (Math.Clamp(item.Distance, 0f, 1f) * (RadarMaxRadius - RadarMinRadius));

            // θ=0° -> Ön (y < RadarCenter), θ=+90° -> Sağ (x > RadarCenter)
            float x = RadarCenter + (r * MathF.Sin(rad));
            float y = RadarCenter - (r * MathF.Cos(rad));

            Canvas.SetLeft(element, x - 21);
            Canvas.SetTop(element, y - 21);
        }

        private void CanvasSpatialRadar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(CanvasSpatialRadar).Position;
            var hitItem = FindItemNearPoint(pt);

            if (hitItem != null)
            {
                _selectedSpatialItem = hitItem;
                bool isManual = string.Equals(_storage.Settings.SpatialAudioMode, "manual", StringComparison.OrdinalIgnoreCase);
                if (isManual)
                {
                    _draggingSpatialItem = hitItem;
                    CanvasSpatialRadar.CapturePointer(e.Pointer);
                }
                UpdateSelectedSpatialInspector();
                RedrawSpatialRadar();
            }
        }

        private void CanvasSpatialRadar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_draggingSpatialItem == null) return;

            var pt = e.GetCurrentPoint(CanvasSpatialRadar).Position;
            float dx = (float)(pt.X - RadarCenter);
            float dy = (float)(RadarCenter - pt.Y); // dy > 0 -> Ön

            float r = MathF.Sqrt((dx * dx) + (dy * dy));
            float rClamped = Math.Clamp(r, RadarMinRadius, RadarMaxRadius);

            float deg = MathF.Atan2(dx, dy) * (180.0f / MathF.PI);
            float dist = (rClamped - RadarMinRadius) / (RadarMaxRadius - RadarMinRadius);

            _draggingSpatialItem.AzimuthDeg = deg;
            _draggingSpatialItem.Distance = dist;
            _draggingSpatialItem.HasManualPosition = true;

            UpdateSelectedSpatialInspector();
            RedrawSpatialRadar();

            // Canlı ses motorunu güncelle
            ApplyItemPositionToAudioEngine(_draggingSpatialItem);
        }

        private void CanvasSpatialRadar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_draggingSpatialItem != null)
            {
                CanvasSpatialRadar.ReleasePointerCapture(e.Pointer);
                SaveItemPosition(_draggingSpatialItem);
                _draggingSpatialItem = null;
            }
        }

        private SpatialParticipantItem? FindItemNearPoint(Point pt)
        {
            foreach (var item in _spatialItems)
            {
                float rad = item.AzimuthDeg * (MathF.PI / 180.0f);
                float r = RadarMinRadius + (Math.Clamp(item.Distance, 0f, 1f) * (RadarMaxRadius - RadarMinRadius));
                float x = RadarCenter + (r * MathF.Sin(rad));
                float y = RadarCenter - (r * MathF.Cos(rad));

                float distSq = (float)(((pt.X - x) * (pt.X - x)) + ((pt.Y - y) * (pt.Y - y)));
                if (distSq <= (26 * 26)) // 26px yarıçapında tıklama toleransı
                {
                    return item;
                }
            }
            return null;
        }

        private void SliderSpatialDistance_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSpatialEvents || _selectedSpatialItem == null) return;
            float newDist = (float)e.NewValue / 100.0f;
            _selectedSpatialItem.Distance = newDist;
            _selectedSpatialItem.HasManualPosition = true;

            UpdateSelectedSpatialInspector();
            RedrawSpatialRadar();
            ApplyItemPositionToAudioEngine(_selectedSpatialItem);
            SaveItemPosition(_selectedSpatialItem);
        }

        private void UpdateSelectedSpatialInspector()
        {
            if (_selectedSpatialItem == null)
            {
                TxtSpatialSelectedUser.Text = "Katılımcı Seçin";
                TxtSpatialAngleInfo.Text = "Yön: —";
                TxtSpatialDistanceVal.Text = "—";
                return;
            }

            TxtSpatialSelectedUser.Text = _selectedSpatialItem.DisplayName;

            float deg = _selectedSpatialItem.AzimuthDeg;
            string dirStr;
            if (Math.Abs(deg) < 5.0f) dirStr = "Ön (0°)";
            else if (Math.Abs(deg) > 175.0f) dirStr = "Arka (180°)";
            else if (deg > 0) dirStr = $"Sağ {(int)Math.Round(deg)}°";
            else dirStr = $"Sol {(int)Math.Round(-deg)}°";

            TxtSpatialAngleInfo.Text = $"Yön: {dirStr}";

            float dist = _selectedSpatialItem.Distance;
            TxtSpatialDistanceVal.Text = $"{dist:F2} m ({(dist < 0.35f ? "Yakın" : dist > 0.7f ? "Uzak" : "Orta")})";

            _suppressSpatialEvents = true;
            SliderSpatialDistance.Value = Math.Round(dist * 100);
            _suppressSpatialEvents = false;
        }

        private void BtnSpatialTestHear_Click(object sender, RoutedEventArgs e)
        {
            float az = _selectedSpatialItem?.AzimuthDeg ?? 0f;
            float dist = _selectedSpatialItem?.Distance ?? 0.2f;
            _audio.PlaySpatialTestTone(az, dist);
        }

        private void BtnSpatialResetDefault_Click(object sender, RoutedEventArgs e)
        {
            _storage.Settings.SpatialAudioMode = "auto";
            foreach (ComboBoxItem item in CmbSpatialMode.Items)
            {
                if (string.Equals(item.Tag as string, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    _suppressSpatialEvents = true;
                    CmbSpatialMode.SelectedItem = item;
                    _suppressSpatialEvents = false;
                    break;
                }
            }

            foreach (var item in _spatialItems)
            {
                item.HasManualPosition = false;
                if (!item.IsVirtual)
                {
                    var cfg = _storage.GetPeerAudioConfig(item.Name);
                    cfg.HasManualSpatialPosition = false;
                    _storage.SavePeerAudioConfig(item.Name, cfg);
                }
            }

            ComputeAutoPositions(_spatialItems);
            ApplySpatialPositionsToAudioEngine();
            RedrawSpatialRadar();
            UpdateSelectedSpatialInspector();
            ShowToast("🔄 3B uzamsal konumlar varsayılana sıfırlandı.");
        }

        private void ApplyItemPositionToAudioEngine(SpatialParticipantItem item)
        {
            if (item.IsVirtual) return;
            var cfg = _storage.GetPeerAudioConfig(item.Name);
            cfg.SpatialAzimuthDeg = item.AzimuthDeg;
            cfg.SpatialDistance = item.Distance;
            cfg.HasManualSpatialPosition = item.HasManualPosition;
            _audio.UpdatePeerAudioSettings(item.Name, cfg);
        }

        private void SaveItemPosition(SpatialParticipantItem item)
        {
            if (item.IsVirtual) return;
            var cfg = _storage.GetPeerAudioConfig(item.Name);
            cfg.SpatialAzimuthDeg = item.AzimuthDeg;
            cfg.SpatialDistance = item.Distance;
            cfg.HasManualSpatialPosition = item.HasManualPosition;
            _storage.SavePeerAudioConfig(item.Name, cfg);
        }

        private void ApplySpatialPositionsToAudioEngine()
        {
            foreach (var item in _spatialItems)
            {
                if (!item.IsVirtual)
                {
                    ApplyItemPositionToAudioEngine(item);
                }
            }
        }
    }
}
