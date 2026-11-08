using System;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// GPU (DirectComposition) tabanlı akıcı 60-240Hz arayüz animasyon motoru:
    /// - XAML Measure/Arrange tetiklemez (%0 CPU yükü)
    /// - Tüm butonlarda, kartlarda, pencere geçişlerinde ve modal açılış/kapanışlarında yumuşak cubic-bezier geçişler sağlar
    /// </summary>
    public static class UiAnimationHelper
    {
        public static Action<UiSoundEffect>? UiSoundPlayer { get; set; }
        private static long _lastHoverSoundTick = 0;

        private static readonly PropertyInfo? ProtectedCursorProp =
            typeof(UIElement).GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly InputCursor? HandCursor = CreateHandCursorSafe();

        private static InputCursor? CreateHandCursorSafe()
        {
            try { return InputSystemCursor.Create(InputSystemCursorShape.Hand); }
            catch { return null; }
        }

        public static void ApplyHandCursor(UIElement element)
        {
            if (HandCursor == null) return;
            try { ProtectedCursorProp?.SetValue(element, HandCursor); }
            catch { }
        }

        private static Visual PrepareVisual(FrameworkElement element)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);

            void SyncCenter()
            {
                if (element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    visual.CenterPoint = new Vector3((float)(element.ActualWidth * 0.5), (float)(element.ActualHeight * 0.5), 0f);
                }
            }

            SyncCenter();
            element.SizeChanged -= OnElementSizeChanged;
            element.SizeChanged += OnElementSizeChanged;
            return visual;
        }

        private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement fe && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                var v = ElementCompositionPreview.GetElementVisual(fe);
                v.CenterPoint = new Vector3((float)(e.NewSize.Width * 0.5), (float)(e.NewSize.Height * 0.5), 0f);
            }
        }

        public static void AnimateScaleAndTranslate(
            FrameworkElement element,
            float scale,
            float translateX = 0f,
            float translateY = 0f,
            int durationMs = 200)
        {
            try
            {
                var visual = PrepareVisual(element);
                var comp = visual.Compositor;
                var easing = comp.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f));
                var dur = TimeSpan.FromMilliseconds(Math.Max(16, durationMs));

                var scaleAnim = comp.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
                scaleAnim.Duration = dur;
                visual.StartAnimation("Scale", scaleAnim);

                var transAnim = comp.CreateVector3KeyFrameAnimation();
                transAnim.InsertKeyFrame(1f, new Vector3(translateX, translateY, 0f), easing);
                transAnim.Duration = dur;
                visual.StartAnimation("Translation", transAnim);
            }
            catch { }
        }

        /// <summary>
        /// Standart butonlara tatlı hover (büyüme + hafif yükselme) ve tıklama (press) geri bildirimi ekler.
        /// </summary>
        public static void AttachInteractiveHover(
            FrameworkElement element,
            float hoverScale = 1.045f,
            float hoverLiftY = -1.5f,
            float pressScale = 0.955f)
        {
            PrepareVisual(element);
            ApplyHandCursor(element);

            element.PointerEntered += (_, _) =>
            {
                AnimateScaleAndTranslate(element, hoverScale, 0f, hoverLiftY, 190);
                long now = Environment.TickCount64;
                if (now - _lastHoverSoundTick > 55)
                {
                    _lastHoverSoundTick = now;
                    UiSoundPlayer?.Invoke(UiSoundEffect.ButtonHover);
                }
            };

            element.PointerExited += (_, _) =>
            {
                AnimateScaleAndTranslate(element, 1.0f, 0f, 0f, 210);
            };

            element.PointerPressed += (_, _) =>
            {
                AnimateScaleAndTranslate(element, pressScale, 0f, 0f, 95);
                UiSoundPlayer?.Invoke(UiSoundEffect.ButtonClick);
            };

            element.PointerReleased += (_, _) =>
            {
                AnimateScaleAndTranslate(element, hoverScale, 0f, hoverLiftY, 150);
            };
        }

        /// <summary>
        /// Ana sayfadaki iki büyük hap karta ("Yeni Oda Başlat" ve "Odaya Katıl") özel koordine hover animasyonu ekler:
        /// - Kart yukarı süzülür (-3.5px) ve kenarlığı parlar
        /// - Sol dairesel ikon %8 büyür
        /// - Sağ ok dairesi +4px sağa kayar ve aydınlanır
        /// </summary>
        public static void AttachHeroPillCardAnimation(
            Border card,
            FrameworkElement leftIconCircle,
            Border rightArrowCircle,
            Brush restBackground,
            Brush hoverBackground,
            Color restBorderColor,
            Color hoverBorderColor)
        {
            PrepareVisual(card);
            PrepareVisual(leftIconCircle);
            PrepareVisual(rightArrowCircle);
            ApplyHandCursor(card);

            var restBorderBrush = new SolidColorBrush(restBorderColor);
            var hoverBorderBrush = new SolidColorBrush(hoverBorderColor);
            var restArrowBg = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            var hoverArrowBg = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255));

            card.Background = restBackground;
            card.BorderBrush = restBorderBrush;
            rightArrowCircle.Background = restArrowBg;

            card.PointerEntered += (_, _) =>
            {
                card.Background = hoverBackground;
                card.BorderBrush = hoverBorderBrush;
                rightArrowCircle.Background = hoverArrowBg;

                AnimateScaleAndTranslate(card, 1.012f, 0f, -3.5f, 240);
                AnimateScaleAndTranslate(leftIconCircle, 1.085f, 0f, 0f, 220);
                AnimateScaleAndTranslate(rightArrowCircle, 1.04f, 4.0f, 0f, 220);
            };

            card.PointerExited += (_, _) =>
            {
                card.Background = restBackground;
                card.BorderBrush = restBorderBrush;
                rightArrowCircle.Background = restArrowBg;

                AnimateScaleAndTranslate(card, 1.0f, 0f, 0f, 240);
                AnimateScaleAndTranslate(leftIconCircle, 1.0f, 0f, 0f, 220);
                AnimateScaleAndTranslate(rightArrowCircle, 1.0f, 0f, 0f, 220);
            };

            card.PointerPressed += (_, _) =>
            {
                AnimateScaleAndTranslate(card, 0.985f, 0f, -1.0f, 100);
            };

            card.PointerReleased += (_, _) =>
            {
                AnimateScaleAndTranslate(card, 1.012f, 0f, -3.5f, 160);
            };
        }

        /// <summary>
        /// Ana sayfadaki 3D Runa logosuna sonsuz yumuşak süzülme (runaLogoFloat 4s) animasyonu uygular.
        /// GPU üzerinde çalışır, UI thread'ini sıfır meşgul eder.
        /// </summary>
        public static void StartHeroLogoFloat(FrameworkElement logoContainer, FrameworkElement? glowElement = null)
        {
            try
            {
                var visual = PrepareVisual(logoContainer);
                var comp = visual.Compositor;
                var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0f), new Vector2(0.58f, 1f));

                var floatAnim = comp.CreateVector3KeyFrameAnimation();
                floatAnim.InsertKeyFrame(0.0f, new Vector3(0f, 0f, 0f), ease);
                floatAnim.InsertKeyFrame(0.5f, new Vector3(0f, -5.5f, 0f), ease);
                floatAnim.InsertKeyFrame(1.0f, new Vector3(0f, 0f, 0f), ease);
                floatAnim.Duration = TimeSpan.FromSeconds(5.6);
                floatAnim.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation("Translation", floatAnim);

                var scaleAnim = comp.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0.0f, new Vector3(1.0f, 1.0f, 1f), ease);
                scaleAnim.InsertKeyFrame(0.5f, new Vector3(1.022f, 1.022f, 1f), ease);
                scaleAnim.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1f), ease);
                scaleAnim.Duration = TimeSpan.FromSeconds(5.6);
                scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation("Scale", scaleAnim);

                if (glowElement != null)
                {
                    var glowVis = PrepareVisual(glowElement);
                    var glowPulse = comp.CreateScalarKeyFrameAnimation();
                    glowPulse.InsertKeyFrame(0.0f, 0.72f, ease);
                    glowPulse.InsertKeyFrame(0.5f, 1.0f, ease);
                    glowPulse.InsertKeyFrame(1.0f, 0.72f, ease);
                    glowPulse.Duration = TimeSpan.FromSeconds(5.6);
                    glowPulse.IterationBehavior = AnimationIterationBehavior.Forever;
                    glowVis.StartAnimation("Opacity", glowPulse);
                }
            }
            catch { }
        }

        /// <summary>
        /// Modal pencereyi yumuşak arka plan kararması + aşağıdan yukarı kayma ve ölçeklenme (0.95 -> 1.0) ile açar.
        /// </summary>
        public static void ShowModalAnimated(FrameworkElement overlay, FrameworkElement? dialogCard = null)
        {
            overlay.Visibility = Visibility.Visible;
            try
            {
                var overlayVis = PrepareVisual(overlay);
                var comp = overlayVis.Compositor;
                var easeOut = comp.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f));

                overlayVis.Opacity = 0f;
                var fadeAnim = comp.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(0f, 0f);
                fadeAnim.InsertKeyFrame(1f, 1f, easeOut);
                fadeAnim.Duration = TimeSpan.FromMilliseconds(180);
                overlayVis.StartAnimation("Opacity", fadeAnim);

                if (dialogCard != null)
                {
                    var cardVis = PrepareVisual(dialogCard);
                    var slideAnim = comp.CreateVector3KeyFrameAnimation();
                    slideAnim.InsertKeyFrame(0f, new Vector3(0f, 16f, 0f));
                    slideAnim.InsertKeyFrame(1f, Vector3.Zero, easeOut);
                    slideAnim.Duration = TimeSpan.FromMilliseconds(240);
                    cardVis.StartAnimation("Translation", slideAnim);

                    var scaleAnim = comp.CreateVector3KeyFrameAnimation();
                    scaleAnim.InsertKeyFrame(0f, new Vector3(0.955f, 0.955f, 1f));
                    scaleAnim.InsertKeyFrame(1f, Vector3.One, easeOut);
                    scaleAnim.Duration = TimeSpan.FromMilliseconds(240);
                    cardVis.StartAnimation("Scale", scaleAnim);
                }
            }
            catch { }
        }

        /// <summary>
        /// Modal pencereyi yumuşak küçülme ve solma animasyonuyla kapatır.
        /// </summary>
        public static async Task HideModalAnimatedAsync(FrameworkElement overlay, FrameworkElement? dialogCard = null)
        {
            if (overlay.Visibility != Visibility.Visible) return;

            try
            {
                var overlayVis = PrepareVisual(overlay);
                var comp = overlayVis.Compositor;
                var easeIn = comp.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(1.0f, 1.0f));

                var fadeAnim = comp.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(1f, 0f, easeIn);
                fadeAnim.Duration = TimeSpan.FromMilliseconds(155);
                overlayVis.StartAnimation("Opacity", fadeAnim);

                if (dialogCard != null)
                {
                    var cardVis = PrepareVisual(dialogCard);
                    var slideAnim = comp.CreateVector3KeyFrameAnimation();
                    slideAnim.InsertKeyFrame(1f, new Vector3(0f, 10f, 0f), easeIn);
                    slideAnim.Duration = TimeSpan.FromMilliseconds(155);
                    cardVis.StartAnimation("Translation", slideAnim);

                    var scaleAnim = comp.CreateVector3KeyFrameAnimation();
                    scaleAnim.InsertKeyFrame(1f, new Vector3(0.965f, 0.965f, 1f), easeIn);
                    scaleAnim.Duration = TimeSpan.FromMilliseconds(155);
                    cardVis.StartAnimation("Scale", scaleAnim);
                }

                await Task.Delay(155);
            }
            catch { }

            overlay.Visibility = Visibility.Collapsed;
            try
            {
                ElementCompositionPreview.GetElementVisual(overlay).Opacity = 1f;
            }
            catch { }
        }

        /// <summary>
        /// Lobi <-> Sesli Oda veya Normal Sahne <-> Ekran Yayını geçişlerinde pürüzsüz sahne geçişi uygular.
        /// </summary>
        public static void AnimateViewEntrance(FrameworkElement enteringView)
        {
            enteringView.Visibility = Visibility.Visible;
            try
            {
                var vis = PrepareVisual(enteringView);
                var comp = vis.Compositor;
                var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f));

                vis.Opacity = 0f;
                var fade = comp.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, ease);
                fade.Duration = TimeSpan.FromMilliseconds(260);
                vis.StartAnimation("Opacity", fade);

                var scale = comp.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0f, new Vector3(0.985f, 0.985f, 1f));
                scale.InsertKeyFrame(1f, Vector3.One, ease);
                scale.Duration = TimeSpan.FromMilliseconds(280);
                vis.StartAnimation("Scale", scale);
            }
            catch { }
        }

        /// <summary>
        /// Hatalı oda kodu girildiğinde uyarı kutusunu yatay olarak titretir (runaAlertShake).
        /// </summary>
        public static void ShakeElement(FrameworkElement element)
        {
            element.Visibility = Visibility.Visible;
            try
            {
                var vis = PrepareVisual(element);
                var comp = vis.Compositor;
                var shake = comp.CreateVector3KeyFrameAnimation();
                shake.InsertKeyFrame(0.00f, Vector3.Zero);
                shake.InsertKeyFrame(0.20f, new Vector3(-5f, 0f, 0f));
                shake.InsertKeyFrame(0.40f, new Vector3(5f, 0f, 0f));
                shake.InsertKeyFrame(0.60f, new Vector3(-4f, 0f, 0f));
                shake.InsertKeyFrame(0.80f, new Vector3(4f, 0f, 0f));
                shake.InsertKeyFrame(1.00f, Vector3.Zero);
                shake.Duration = TimeSpan.FromMilliseconds(340);
                vis.StartAnimation("Translation", shake);
            }
            catch { }
        }

        /// <summary>
        /// Sesli oda merkezindeki 3 dalga halkasını GPU üzerinde canlandırır (0% CPU, XAML layout tetiklemez!).
        /// Sadece konuşma durumu (speaking <-> idle) değiştiğinde çağrılır.
        /// </summary>
        public static void SetVoiceStageRipplesState(
            Microsoft.UI.Xaml.Shapes.Shape ring1,
            Microsoft.UI.Xaml.Shapes.Shape ring2,
            Microsoft.UI.Xaml.Shapes.Shape ring3,
            bool animationsEnabled,
            bool speaking)
        {
            try
            {
                var v1 = PrepareVisual(ring1);
                var v2 = PrepareVisual(ring2);
                var v3 = PrepareVisual(ring3);

                if (!animationsEnabled)
                {
                    v1.StopAnimation("Scale");
                    v2.StopAnimation("Scale");
                    v3.StopAnimation("Scale");
                    v1.StopAnimation("Opacity");
                    v2.StopAnimation("Opacity");
                    v3.StopAnimation("Opacity");
                    v1.Scale = Vector3.One;
                    v2.Scale = Vector3.One;
                    v3.Scale = Vector3.One;
                    v1.Opacity = 0.25f;
                    v2.Opacity = 0.15f;
                    v3.Opacity = 0.08f;
                    return;
                }

                var strokeColor = speaking
                    ? Color.FromArgb(255, 52, 211, 153)
                    : Color.FromArgb(255, 168, 85, 247);
                var brush = new SolidColorBrush(strokeColor);
                ring1.Stroke = brush;
                ring2.Stroke = brush;
                ring3.Stroke = brush;

                StartSingleRippleLoop(v1, speaking ? 1.11f : 1.03f, speaking ? 0.85f : 0.34f, speaking ? 0.38f : 0.22f, speaking ? 950 : 2800, 0);
                StartSingleRippleLoop(v2, speaking ? 1.13f : 1.035f, speaking ? 0.60f : 0.22f, speaking ? 0.22f : 0.12f, speaking ? 950 : 2800, 140);
                StartSingleRippleLoop(v3, speaking ? 1.15f : 1.04f, speaking ? 0.40f : 0.13f, speaking ? 0.10f : 0.06f, speaking ? 950 : 2800, 280);
            }
            catch { }
        }

        private static void StartSingleRippleLoop(Visual vis, float peakScale, float peakOpacity, float minOpacity, int periodMs, int delayMs)
        {
            var comp = vis.Compositor;
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));

            var sAnim = comp.CreateVector3KeyFrameAnimation();
            sAnim.InsertKeyFrame(0.0f, Vector3.One, ease);
            sAnim.InsertKeyFrame(0.5f, new Vector3(peakScale, peakScale, 1f), ease);
            sAnim.InsertKeyFrame(1.0f, Vector3.One, ease);
            sAnim.Duration = TimeSpan.FromMilliseconds(periodMs);
            sAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            sAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            vis.StartAnimation("Scale", sAnim);

            var oAnim = comp.CreateScalarKeyFrameAnimation();
            oAnim.InsertKeyFrame(0.0f, minOpacity, ease);
            oAnim.InsertKeyFrame(0.5f, peakOpacity, ease);
            oAnim.InsertKeyFrame(1.0f, minOpacity, ease);
            oAnim.Duration = TimeSpan.FromMilliseconds(periodMs);
            oAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            oAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            vis.StartAnimation("Opacity", oAnim);
        }
    }
}
