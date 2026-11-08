using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RunaVoice_WinUI.Services;
using Windows.Foundation;

namespace RunaVoice_WinUI
{
    public sealed partial class MainPage
    {
        // =====================================================================
        // EKRAN ÜSTÜ ÇİZİM (ANNOTATION) SİSTEMİ — İZLEYİCİ VE YAYINCI MANTIĞI
        // =====================================================================

        private readonly AnnotationStore _annViewerStore = new();
        private readonly AnnotationStore _annBroadcasterStore = new();
        private AnnotationOverlayWindow? _annOverlay;
        private AnnotationContentTracker? _contentTracker;
        private readonly Queue<(uint frameId, long receivedTick, List<AnnotationTransformItem> transforms)> _pendingTransforms = new();
        private uint _lastViewerRenderedFrameId = 0;

        private bool _annDrawMode = false;
        private bool _annRemotePermOn = false;
        private bool _annRemoteInVideo = false;
        private int _annRemoteFade = 8;

        private int _annLocalSid = 0;
        private int _annColorIndex = 0;
        private int _annWidthIndex = 1; // 0: İnce (3‰), 1: Orta (5‰), 2: Kalın (9‰)

        private readonly List<int> _annSendBuffer = new();
        private DispatcherTimer? _annSendTimer;
        private DispatcherTimer? _annViewerFadeTimer;
        private DispatcherTimer? _annBroadcasterFadeTimer;

        private readonly ConcurrentDictionary<string, AnnotationRateLimiter> _annRateLimiters = new();
        private int _lastSentU = -1;
        private int _lastSentV = -1;
        private bool _isPointerDrawing = false;
        private bool _hasAnnInited = false;
        private bool _hasShownFirstAnnotationToast = false;

        private Polyline? _activeLocalPolyline;
        private readonly List<Point> _activeLocalPoints = new();
        private readonly Dictionary<string, (Border badge, DispatcherTimer timer)> _activeNameBadges = new();

        /// <summary>
        /// WireScreenShareEvents sonunda çağrılır; olaylara abone olur ve zamanlayıcıları başlatır.
        /// </summary>
        private void WireAnnotationEvents()
        {
            if (_hasAnnInited) return;
            _hasAnnInited = true;

            // Varsayılan renk: peerKey özetinden
            _annColorIndex = AnnotationProtocol.DefaultColorIndexFor(_storage.CurrentUser.VoiceSessionId);
            UpdateToolbarColorSelection();
            UpdateToolbarWidthSelection();

            // Yayıncı izin anahtarı başlangıç değeri
            TglAllowAnnotations.IsOn = _storage.Settings.AllowViewerAnnotations;

            // 33 ms gönderim tamponlama zamanlayıcısı
            _annSendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _annSendTimer.Tick += (s, e) => FlushSendBuffer();

            // İzleyici solma zamanlayıcısı (yalnızca çizgi varken çalışır)
            _annViewerFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _annViewerFadeTimer.Tick += (s, e) =>
            {
                bool changed = _annViewerStore.Tick(Environment.TickCount64, _annRemoteFade);
                if (changed)
                {
                    RedrawViewerCanvas();
                }
                if (_annViewerStore.Snapshot().Count == 0)
                {
                    _annViewerFadeTimer.Stop();
                }
            };

            _annViewerStore.Changed += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RedrawViewerCanvas();
                    if (_annViewerStore.Snapshot().Count > 0 && !_annViewerFadeTimer.IsEnabled)
                    {
                        _annViewerFadeTimer.Start();
                    }
                });
            };

            // Yayıncı solma zamanlayıcısı (en fazla 30-60 Hz)
            _annBroadcasterFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _annBroadcasterFadeTimer.Tick += (s, e) =>
            {
                int fadeSec = _storage.Settings.AnnotationFadeSeconds;
                bool changed = _annBroadcasterStore.Tick(Environment.TickCount64, fadeSec);
                if (changed && _annOverlay != null)
                {
                    _annOverlay.Render(_annBroadcasterStore.Snapshot());
                }
                if (_annBroadcasterStore.Snapshot().Count == 0)
                {
                    _annBroadcasterFadeTimer.Stop();
                }
            };

            _annBroadcasterStore.Changed += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_annOverlay != null)
                    {
                        _annOverlay.Render(_annBroadcasterStore.Snapshot());
                    }
                    if (_annBroadcasterStore.Snapshot().Count > 0 && !_annBroadcasterFadeTimer.IsEnabled)
                    {
                        _annBroadcasterFadeTimer.Start();
                    }
                });
            };

            // WebRTC gelen çizim mesajı olayı
            _engine.OnAnnotationMessage += (fromKey, fromName, msg) =>
            {
                DispatcherQueue.TryEnqueue(() => HandleAnnotationMessage(fromKey, fromName, msg));
            };

            // WebRTC kontrol kanalı açıldı olayı
            _engine.OnControlChannelOpened += peerKey =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_screenCapture.IsSharing)
                    {
                        SendPermToPeer(peerKey);
                    }

                    if (_isWatchingRemoteStream && peerKey == _webrtc.RemoteBroadcasterKey)
                    {
                        UpdateAnnotateToggleButtonState();
                    }
                });
            };

            // Yakalanan ekran dikdörtgeni değiştiğinde
            _screenCapture.OnCaptureRectChanged += (x, y, w, h) =>
            {
                _annOverlay?.SetBounds(x, y, w, h);
            };

            // Görev 1: İçerik Takipçisi başlat ve bağla
            _contentTracker = new AnnotationContentTracker(
                _webrtc,
                _annBroadcasterStore,
                () => _annOverlay?.Render(_annBroadcasterStore.Snapshot()));

            _screenCapture.OnTrackingFrameReady += (gray, w, h, fId) =>
            {
                _contentTracker?.ProcessTrackingFrame(gray, w, h, fId);
            };
            // Çizgi yalnızca izin açıkken ve bağlı izleyici varken gelebilir; aksi hâlde takip karesi üretilmez
            _screenCapture.NeedsTrackingFrames = () => _storage.Settings.AllowViewerAnnotations && _webrtc.HasConnectedPeers;

            _annBroadcasterStore.StrokeEnded += stroke =>
            {
                _contentTracker?.OnStrokeEnded(stroke);
            };

            _annBroadcasterStore.StrokeRemoved += (ownerKey, strokeId) =>
            {
                _contentTracker?.RemoveStroke(ownerKey, strokeId);
            };
        }

        #region Mesaj İşleme (Yayıncı & İzleyici Yolu)

        private void HandleAnnotationMessage(string fromKey, string fromName, AnnotationMessage msg)
        {
            if (_screenCapture.IsSharing)
            {
                // YAYINCI YOLU
                var src = _screenCapture.ActiveSource;
                if (src == null || !src.IsMonitor) return; // v1: Yalnızca monitör kaynağında kabul edilir
                if (!_storage.Settings.AllowViewerAnnotations) return; // İzin kapalıysa reddet

                // scope:"all" izleyiciden gelirse yok sayılır (yalnızca yayıncı tümünü temizleyebilir)
                if (msg.Kind == AnnotationKind.Clear && msg.ClearAll) return;

                // Hız sınırı (kayan 1 sn: 900 nokta/sn, 20 çizgi/sn)
                var limiter = _annRateLimiters.GetOrAdd(fromKey, _ => new AnnotationRateLimiter());
                int pointCount = (msg.Points != null) ? msg.Points.Length / 2 : 0;
                if (!limiter.CheckAndRecord(msg.Kind, pointCount)) return;

                // Bilgilendirme: İlk çizim geldiğinde yayıncıya bir kez toast
                if (!_hasShownFirstAnnotationToast && (msg.Kind == AnnotationKind.Begin || msg.Kind == AnnotationKind.Points))
                {
                    _hasShownFirstAnnotationToast = true;
                    ShowToast($"{fromName} ekranınıza çiziyor — temizlemek için yayın kartındaki düğmeyi kullanın");
                }

                // İzleyiciden gelen o/n alanları yok sayılır; yayıncı kendi oturum bilgisini (fromKey, fromName) uygular
                _annBroadcasterStore.Apply(fromKey, fromName, msg);

                // Diğer izleyicilere röle et (çizen eş hariç)
                msg.OriginKey = fromKey;
                msg.OriginName = fromName;
                object wirePayload = AnnotationProtocol.ToWire(msg, includeOrigin: true);
                _engine.BroadcastAnnotation(wirePayload, exceptPeerKey: fromKey);

                // Katmanı yeniden çiz
                if (_annOverlay != null)
                {
                    _annOverlay.Render(_annBroadcasterStore.Snapshot());
                    if (!_annBroadcasterFadeTimer!.IsEnabled)
                    {
                        _annBroadcasterFadeTimer.Start();
                    }
                }
            }
            else if (fromKey == _webrtc.RemoteBroadcasterKey)
            {
                // İZLEYİCİ YOLU (yalnızca aktif yayıncıdan gelen mesajlar kabul edilir)
                if (msg.OriginKey == _storage.CurrentUser.VoiceSessionId && msg.Kind != AnnotationKind.Xform)
                {
                    // Kendi çizgimizin yankısı: yok say (ANN_XFORM hariç!)
                    return;
                }

                if (msg.Kind == AnnotationKind.Xform)
                {
                    if (msg.Transforms != null && msg.Transforms.Count > 0)
                    {
                        long now = Environment.TickCount64;
                        if (_lastViewerRenderedFrameId >= msg.FrameId)
                        {
                            _annViewerStore.ApplyTransforms(msg.Transforms);
                            RedrawViewerCanvas();
                        }
                        else
                        {
                            _pendingTransforms.Enqueue((msg.FrameId, now, msg.Transforms));
                        }
                    }
                    return;
                }

                if (msg.Kind == AnnotationKind.Perm)
                {
                    _annRemotePermOn = msg.PermOn;
                    _annRemoteFade = msg.FadeSeconds;
                    _annRemoteInVideo = msg.InVideo;

                    if (!_annRemotePermOn)
                    {
                        if (_annDrawMode)
                        {
                            ExitAnnotationMode();
                            ShowToast("Yayıncı çizim iznini kapattı");
                        }
                    }

                    UpdateAnnotateToggleButtonState();
                    return;
                }

                if (msg.Kind == AnnotationKind.Clear)
                {
                    if (msg.ClearAll)
                    {
                        _annViewerStore.ClearAll();
                    }
                    else if (!string.IsNullOrEmpty(msg.OriginKey))
                    {
                        _annViewerStore.ClearOwner(msg.OriginKey);
                    }
                    RedrawViewerCanvas();
                    return;
                }

                // Geri dönüş (fallback): Eğer inv (in video) true ise başkalarının çizgilerini yerel olarak çizme
                if (_annRemoteInVideo)
                {
                    return;
                }

                string strokeOwnerKey = msg.OriginKey ?? fromKey;
                string strokeOwnerName = msg.OriginName ?? fromName;

                _annViewerStore.Apply(strokeOwnerKey, strokeOwnerName, msg);
                RedrawViewerCanvas();

                // Ad etiketi (başkalarının aktif çizgisinin son noktasında küçük rozet)
                if (_storage.Settings.ShowAnnotationNames && (msg.Kind == AnnotationKind.Begin || msg.Kind == AnnotationKind.Points))
                {
                    UpdateRemoteNameBadge(strokeOwnerKey, strokeOwnerName, msg);
                }
            }
        }

        private void OnViewerVideoFrameRendered(uint frameId)
        {
            _lastViewerRenderedFrameId = frameId;
            if (_pendingTransforms.Count == 0) return;

            long now = Environment.TickCount64;
            bool hasTransforms = false;
            while (_pendingTransforms.Count > 0)
            {
                var peek = _pendingTransforms.Peek();
                if (frameId >= peek.frameId || (now - peek.receivedTick >= 1000))
                {
                    _pendingTransforms.Dequeue();
                    _annViewerStore.ApplyTransforms(peek.transforms);
                    hasTransforms = true;
                }
                else
                {
                    break;
                }
            }

            if (hasTransforms)
            {
                DispatcherQueue.TryEnqueue(RedrawViewerCanvas);
            }
        }

        #endregion

        #region İzleyici Arayüzü ve Tuval Çizimi

        public void UpdateAnnotateToggleButtonState()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                bool hasPerm = _annRemotePermOn;

                if (!_isWatchingRemoteStream || !hasPerm)
                {
                    BtnAnnotateToggle.Visibility = Visibility.Collapsed;
                    if (_annDrawMode) ExitAnnotationMode();
                    return;
                }

                BtnAnnotateToggle.Visibility = Visibility.Visible;
                bool channelOpen = _webrtc.IsControlChannelOpen(_webrtc.RemoteBroadcasterKey);

                if (channelOpen)
                {
                    BtnAnnotateToggle.IsEnabled = true;
                    ToolTipService.SetToolTip(BtnAnnotateToggle, "Yayın Üzerine Çiz (Kalem)");
                }
                else
                {
                    BtnAnnotateToggle.IsEnabled = false;
                    ToolTipService.SetToolTip(BtnAnnotateToggle, "Yayıncıyla doğrudan bağlantı kuruluyor…");
                }
            });
        }

        private void BtnAnnotateToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_annDrawMode)
            {
                ExitAnnotationMode();
            }
            else
            {
                EnterAnnotationMode();
            }
        }

        private void EnterAnnotationMode()
        {
            _annDrawMode = true;
            AnnotationToolbar.Visibility = Visibility.Visible;
            AnnotationCanvas.IsHitTestVisible = true;
            AnnotationCanvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
            BtnAnnotateToggle.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 124, 58, 237));
        }

        private void ExitAnnotationMode()
        {
            _annDrawMode = false;
            EndCurrentPointerStroke();

            AnnotationToolbar.Visibility = Visibility.Collapsed;
            AnnotationCanvas.IsHitTestVisible = false;
            AnnotationCanvas.Background = null;
            ProtectedCursor = null;
            BtnAnnotateToggle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private void BtnExitAnnotationMode_Click(object sender, RoutedEventArgs e)
        {
            ExitAnnotationMode();
        }

        private void BtnClearMyAnnotations_Click(object sender, RoutedEventArgs e)
        {
            string myKey = _storage.CurrentUser.VoiceSessionId;
            _annViewerStore.ClearOwner(myKey);
            RedrawViewerCanvas();

            var clearMsg = new AnnotationMessage { Kind = AnnotationKind.Clear, ClearAll = false };
            _engine.SendAnnotationTo(_webrtc.RemoteBroadcasterKey, AnnotationProtocol.ToWire(clearMsg, false));
        }

        private void AnnColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tagStr && int.TryParse(tagStr, out int ci))
            {
                _annColorIndex = Math.Clamp(ci, 0, 7);
                UpdateToolbarColorSelection();
            }
        }

        private void UpdateToolbarColorSelection()
        {
            if (AnnColorPanel == null) return;
            for (int i = 0; i < AnnColorPanel.Children.Count; i++)
            {
                if (AnnColorPanel.Children[i] is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int ci))
                {
                    if (ci == _annColorIndex)
                    {
                        btn.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                        btn.BorderThickness = new Thickness(2);
                    }
                    else
                    {
                        btn.BorderThickness = (ci == 6 || ci == 7) ? new Thickness(1) : new Thickness(0);
                    }
                }
            }
        }

        private void AnnWidthButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tagStr && int.TryParse(tagStr, out int wi))
            {
                _annWidthIndex = Math.Clamp(wi, 0, 2);
                UpdateToolbarWidthSelection();
            }
        }

        private void UpdateToolbarWidthSelection()
        {
            if (AnnWidthPanel == null) return;
            var activeBg = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 124, 58, 237));
            var inactiveBg = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255));
            var activeBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 168, 85, 247));
            var inactiveBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(64, 255, 255, 255));

            BtnAnnWidthThin.Background = (_annWidthIndex == 0) ? activeBg : inactiveBg;
            BtnAnnWidthThin.BorderBrush = (_annWidthIndex == 0) ? activeBorder : inactiveBorder;
            BtnAnnWidthThin.FontWeight = (_annWidthIndex == 0) ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

            BtnAnnWidthMedium.Background = (_annWidthIndex == 1) ? activeBg : inactiveBg;
            BtnAnnWidthMedium.BorderBrush = (_annWidthIndex == 1) ? activeBorder : inactiveBorder;
            BtnAnnWidthMedium.FontWeight = (_annWidthIndex == 1) ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

            BtnAnnWidthThick.Background = (_annWidthIndex == 2) ? activeBg : inactiveBg;
            BtnAnnWidthThick.BorderBrush = (_annWidthIndex == 2) ? activeBorder : inactiveBorder;
            BtnAnnWidthThick.FontWeight = (_annWidthIndex == 2) ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        }

        private void LiveStreamVideoImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (LiveStreamVideoImage.ActualWidth > 0 && LiveStreamVideoImage.ActualHeight > 0)
            {
                AnnotationCanvas.Width = LiveStreamVideoImage.ActualWidth;
                AnnotationCanvas.Height = LiveStreamVideoImage.ActualHeight;
                RedrawViewerCanvas();
            }
        }

        private void LiveStreamSwapChainPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (LiveStreamSwapChainPanel.ActualWidth > 0 && LiveStreamSwapChainPanel.ActualHeight > 0)
            {
                AnnotationCanvas.Width = LiveStreamSwapChainPanel.ActualWidth;
                AnnotationCanvas.Height = LiveStreamSwapChainPanel.ActualHeight;
                RedrawViewerCanvas();
                _engine?.ResizeSurface((int)LiveStreamSwapChainPanel.ActualWidth, (int)LiveStreamSwapChainPanel.ActualHeight);
            }
        }

        private void AnnotationCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_annDrawMode) return;

            var pt = e.GetCurrentPoint(AnnotationCanvas);
            if (!pt.Properties.IsLeftButtonPressed && pt.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen && pt.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
                return;

            AnnotationCanvas.CapturePointer(e.Pointer);
            _isPointerDrawing = true;
            _annLocalSid++;

            double cw = Math.Max(1, AnnotationCanvas.ActualWidth);
            double ch = Math.Max(1, AnnotationCanvas.ActualHeight);

            int u = Math.Clamp((int)Math.Round(pt.Position.X / cw * AnnotationProtocol.Scale), 0, AnnotationProtocol.Scale);
            int v = Math.Clamp((int)Math.Round(pt.Position.Y / ch * AnnotationProtocol.Scale), 0, AnnotationProtocol.Scale);

            _lastSentU = u;
            _lastSentV = v;

            string myKey = _storage.CurrentUser.VoiceSessionId;
            string myName = _storage.CurrentUser.Name;
            int widthPermille = AnnotationProtocol.Widths[_annWidthIndex];

            // Yerel görsel Polyline oluştur
            uint colorVal = AnnotationProtocol.Palette[_annColorIndex];
            var strokeBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)((colorVal >> 24) & 0xFF),
                (byte)((colorVal >> 16) & 0xFF),
                (byte)((colorVal >> 8) & 0xFF),
                (byte)(colorVal & 0xFF)));

            _activeLocalPolyline = new Polyline
            {
                Stroke = strokeBrush,
                StrokeThickness = Math.Max(1.5, widthPermille / 1000.0 * ch),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 1.0
            };

            _activeLocalPoints.Clear();
            _activeLocalPoints.Add(pt.Position);
            _activeLocalPoints.Add(new Point(pt.Position.X + 0.1, pt.Position.Y + 0.1)); // Tek nokta için dolu daire etkisi
            foreach (var p in _activeLocalPoints)
                _activeLocalPolyline.Points.Add(p);

            AnnotationCanvas.Children.Add(_activeLocalPolyline);

            // Depoya ekle
            var beginMsg = new AnnotationMessage
            {
                Kind = AnnotationKind.Begin,
                StrokeId = _annLocalSid,
                ColorIndex = _annColorIndex,
                Width = widthPermille,
                Points = new[] { u, v }
            };
            _annViewerStore.Apply(myKey, myName, beginMsg);

            // Yayıncıya ANN_BEGIN gönder
            _annSendBuffer.Clear();
            object wire = AnnotationProtocol.ToWire(beginMsg, false);
            _engine.SendAnnotationTo(_webrtc.RemoteBroadcasterKey, wire);

            _annSendTimer?.Start();
            e.Handled = true;
        }

        private void AnnotationCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_annDrawMode || !_isPointerDrawing || _activeLocalPolyline == null) return;

            double cw = Math.Max(1, AnnotationCanvas.ActualWidth);
            double ch = Math.Max(1, AnnotationCanvas.ActualHeight);

            var interPts = e.GetIntermediatePoints(AnnotationCanvas);

            // Ara noktaları kronolojik sırada işle (GetIntermediatePoints ters sırada döner)
            for (int i = interPts.Count - 1; i >= 0; i--)
            {
                var p = interPts[i];
                int u = Math.Clamp((int)Math.Round(p.Position.X / cw * AnnotationProtocol.Scale), 0, AnnotationProtocol.Scale);
                int v = Math.Clamp((int)Math.Round(p.Position.Y / ch * AnnotationProtocol.Scale), 0, AnnotationProtocol.Scale);

                int du = u - _lastSentU;
                int dv = v - _lastSentV;

                // Seyreltme eşiği: 12 birim (~ %0.12)
                if (du * du + dv * dv >= 144)
                {
                    _lastSentU = u;
                    _lastSentV = v;

                    _activeLocalPoints.Add(p.Position);
                    _activeLocalPolyline.Points.Add(p.Position);

                    string myKey = _storage.CurrentUser.VoiceSessionId;
                    string myName = _storage.CurrentUser.Name;
                    _annViewerStore.Apply(myKey, myName, new AnnotationMessage
                    {
                        Kind = AnnotationKind.Points,
                        StrokeId = _annLocalSid,
                        Points = new[] { u, v }
                    });

                    _annSendBuffer.Add(u);
                    _annSendBuffer.Add(v);

                    if (_annSendBuffer.Count >= 128) // 64 nokta
                    {
                        FlushSendBuffer();
                    }
                }
            }

            e.Handled = true;
        }

        private void AnnotationCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndCurrentPointerStroke();
            try { AnnotationCanvas.ReleasePointerCapture(e.Pointer); } catch { }
            e.Handled = true;
        }

        private void AnnotationCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            EndCurrentPointerStroke();
        }

        private void EndCurrentPointerStroke()
        {
            if (!_isPointerDrawing) return;
            _isPointerDrawing = false;

            FlushSendBuffer();
            _annSendTimer?.Stop();

            string myKey = _storage.CurrentUser.VoiceSessionId;
            string myName = _storage.CurrentUser.Name;

            var endMsg = new AnnotationMessage { Kind = AnnotationKind.End, StrokeId = _annLocalSid };
            _annViewerStore.Apply(myKey, myName, endMsg);

            _engine.SendAnnotationTo(_webrtc.RemoteBroadcasterKey, AnnotationProtocol.ToWire(endMsg, false));
            _activeLocalPolyline = null;
            _activeLocalPoints.Clear();
        }

        private void FlushSendBuffer()
        {
            if (_annSendBuffer.Count == 0) return;

            var ptsMsg = new AnnotationMessage
            {
                Kind = AnnotationKind.Points,
                StrokeId = _annLocalSid,
                Points = _annSendBuffer.ToArray()
            };
            _annSendBuffer.Clear();

            _engine.SendAnnotationTo(_webrtc.RemoteBroadcasterKey, AnnotationProtocol.ToWire(ptsMsg, false));
        }

        private void RedrawViewerCanvas()
        {
            double cw = AnnotationCanvas.ActualWidth;
            double ch = AnnotationCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            // Yerel çizim devam ediyorsa tuvali tamamen silmeyip korumak için
            AnnotationCanvas.Children.Clear();

            var strokes = _annViewerStore.Snapshot();
            for (int i = 0; i < strokes.Count; i++)
            {
                var s = strokes[i];
                if (s.Points == null || s.Points.Count < 2 || s.Opacity <= 0.01 || s.Hidden) continue;

                uint colorVal = AnnotationProtocol.Palette[Math.Clamp(s.ColorIndex, 0, 7)];
                byte a = (byte)Math.Clamp((int)Math.Round(((colorVal >> 24) & 0xFF) * s.Opacity), 0, 255);
                var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    a,
                    (byte)((colorVal >> 16) & 0xFF),
                    (byte)((colorVal >> 8) & 0xFF),
                    (byte)(colorVal & 0xFF)));

                var polyline = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = Math.Max(1.5, s.Width / 1000.0 * ch),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = s.Opacity
                };

                int ptCount = s.Points.Count / 2;
                if (ptCount == 1)
                {
                    double px = (s.Points[0] + s.OffsetU) / 10000.0 * cw;
                    double py = (s.Points[1] + s.OffsetV) / 10000.0 * ch;
                    polyline.Points.Add(new Point(px, py));
                    polyline.Points.Add(new Point(px + 0.1, py + 0.1));
                }
                else
                {
                    for (int p = 0; p < ptCount; p++)
                    {
                        double px = (s.Points[p * 2] + s.OffsetU) / 10000.0 * cw;
                        double py = (s.Points[p * 2 + 1] + s.OffsetV) / 10000.0 * ch;
                        polyline.Points.Add(new Point(px, py));
                    }
                }

                AnnotationCanvas.Children.Add(polyline);
            }

            // Aktif ad etiketlerini yeniden tuvale ekle
            foreach (var kvp in _activeNameBadges)
            {
                if (!AnnotationCanvas.Children.Contains(kvp.Value.badge))
                {
                    AnnotationCanvas.Children.Add(kvp.Value.badge);
                }
            }
        }

        private void UpdateRemoteNameBadge(string ownerKey, string ownerName, AnnotationMessage msg)
        {
            if (msg.Points == null || msg.Points.Length < 2) return;

            double cw = Math.Max(1, AnnotationCanvas.ActualWidth);
            double ch = Math.Max(1, AnnotationCanvas.ActualHeight);

            int lastIdx = msg.Points.Length - 2;
            double px = msg.Points[lastIdx] / 10000.0 * cw;
            double py = msg.Points[lastIdx + 1] / 10000.0 * ch;

            uint colorVal = AnnotationProtocol.Palette[Math.Clamp(msg.ColorIndex, 0, 7)];
            var bgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
                220,
                (byte)((colorVal >> 16) & 0xFF),
                (byte)((colorVal >> 8) & 0xFF),
                (byte)(colorVal & 0xFF)));

            double lum = 0.299 * ((colorVal >> 16) & 0xFF) + 0.587 * ((colorVal >> 8) & 0xFF) + 0.114 * (colorVal & 0xFF);
            var textBrush = lum > 140
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(240, 17, 24, 39))
                : new SolidColorBrush(Microsoft.UI.Colors.White);

            if (_activeNameBadges.TryGetValue(ownerKey, out var existing))
            {
                existing.timer.Stop();
                Canvas.SetLeft(existing.badge, px + 4);
                Canvas.SetTop(existing.badge, py + 4);
                existing.badge.Opacity = 1.0;
                existing.timer.Start();
            }
            else
            {
                var badge = new Border
                {
                    Background = bgBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = new TextBlock
                    {
                        Text = ownerName,
                        FontSize = 10,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = textBrush
                    }
                };

                Canvas.SetLeft(badge, px + 4);
                Canvas.SetTop(badge, py + 4);
                AnnotationCanvas.Children.Add(badge);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    AnnotationCanvas.Children.Remove(badge);
                    _activeNameBadges.Remove(ownerKey);
                };
                timer.Start();

                _activeNameBadges[ownerKey] = (badge, timer);
            }
        }

        private void ClearViewerCanvasAndStore()
        {
            _annViewerStore.ClearAll();
            AnnotationCanvas.Children.Clear();
            _activeNameBadges.Clear();
            _activeLocalPolyline = null;
            _activeLocalPoints.Clear();
        }

        #endregion

        #region Yayıncı Katmanı ve İzin Yönetimi

        private void StartBroadcasterAnnotations()
        {
            _contentTracker?.Reset();
            if (_annOverlay == null)
            {
                _annOverlay = new AnnotationOverlayWindow();
                _annOverlay.Start();
            }

            var src = _screenCapture.ActiveSource;
            if (src != null && src.IsMonitor)
            {
                _annOverlay.SetBounds(src.BoundsX, src.BoundsY, src.BoundsWidth, src.BoundsHeight);
                _annOverlay.SetVisible(true);

                TglAllowAnnotations.IsEnabled = true;
                TxtAnnotationHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                _annOverlay.SetVisible(false);
                TglAllowAnnotations.IsEnabled = false;
                TxtAnnotationHint.Text = "Çizim şu an yalnızca tüm ekran paylaşımında kullanılabilir.";
                TxtAnnotationHint.Visibility = Visibility.Visible;
            }

            if (!_annOverlay.IsExcludedFromCapture)
            {
                TxtAnnotationHint.Text = "Bu Windows sürümünde çizimler yayın görüntüsüne de yansır.";
                TxtAnnotationHint.Visibility = Visibility.Visible;
            }

            BroadcastPerm();
        }

        private void StopBroadcasterAnnotations()
        {
            _contentTracker?.Reset();
            _annBroadcasterStore.ClearAll();
            _engine.BroadcastAnnotation(new { t = "ANN_CLEAR", scope = "all" });
            _engine.BroadcastAnnotation(new
            {
                t = "ANN_PERM",
                on = false,
                fade = _storage.Settings.AnnotationFadeSeconds,
                inv = false
            });

            _annBroadcasterFadeTimer?.Stop();
            _annOverlay?.Dispose();
            _annOverlay = null;

            _hasShownFirstAnnotationToast = false;
            _annRateLimiters.Clear();
        }

        private void TglAllowAnnotations_Toggled(object sender, RoutedEventArgs e)
        {
            _storage.Settings.AllowViewerAnnotations = TglAllowAnnotations.IsOn;
            _storage.SaveSettings();

            if (TglSetAllowAnnotations != null && TglSetAllowAnnotations.IsOn != TglAllowAnnotations.IsOn)
            {
                _suppressSettingsEvents = true;
                TglSetAllowAnnotations.IsOn = TglAllowAnnotations.IsOn;
                _suppressSettingsEvents = false;
            }

            BroadcastPerm();
        }

        private void BtnClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            _contentTracker?.Reset();
            _annBroadcasterStore.ClearAll();
            _annOverlay?.Render(_annBroadcasterStore.Snapshot());
            _engine.BroadcastAnnotation(new { t = "ANN_CLEAR", scope = "all" });
        }

        private void BroadcastPerm()
        {
            var src = _screenCapture.ActiveSource;
            bool isMon = src?.IsMonitor == true;
            bool permOn = _storage.Settings.AllowViewerAnnotations && isMon;
            bool inv = !(_annOverlay?.IsExcludedFromCapture ?? true);

            var permMsg = new AnnotationMessage
            {
                Kind = AnnotationKind.Perm,
                PermOn = permOn,
                FadeSeconds = _storage.Settings.AnnotationFadeSeconds,
                InVideo = inv
            };

            _engine.BroadcastAnnotation(AnnotationProtocol.ToWire(permMsg, false));
        }

        private void SendPermToPeer(string peerKey)
        {
            var src = _screenCapture.ActiveSource;
            bool isMon = src?.IsMonitor == true;
            bool permOn = _storage.Settings.AllowViewerAnnotations && isMon;
            bool inv = !(_annOverlay?.IsExcludedFromCapture ?? true);

            var permMsg = new AnnotationMessage
            {
                Kind = AnnotationKind.Perm,
                PermOn = permOn,
                FadeSeconds = _storage.Settings.AnnotationFadeSeconds,
                InVideo = inv
            };

            _engine.SendAnnotationTo(peerKey, AnnotationProtocol.ToWire(permMsg, false));
        }

        #endregion
    }
}
