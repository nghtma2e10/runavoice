using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Yayıncının gerçek ekranında (tüm uygulamaların üzerinde) çalışan şeffaf,
    /// tıklamayı tamamen alta geçiren (WS_EX_TRANSPARENT), odak çalmayan (WS_EX_NOACTIVATE),
    /// Alt+Tab'da görünmeyen (WS_EX_TOOLWINDOW) ve BitBlt yakalamadan hariç tutulan (WDA_EXCLUDEFROMCAPTURE)
    /// saf Win32 layered pencere katmanı.
    /// </summary>
    public sealed class AnnotationOverlayWindow : IDisposable
    {
        #region Win32 P/Invoke & Sabitler

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        private const int WM_DESTROY = 0x0002;
        private const int WM_CLOSE = 0x0010;
        private const int WM_APP = 0x8000;
        private const int WM_APP_DISPATCH = WM_APP + 1;
        private const int WM_APP_RENDER = WM_APP + 2;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint ULW_ALPHA = 0x00000002;

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Windows 10 2004+

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            ref BLENDFUNCTION pblend,
            uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        #endregion

        private readonly ConcurrentQueue<Action> _cmdQueue = new();
        private readonly object _stateLock = new();

        private Thread? _thread;
        private IntPtr _hwnd = IntPtr.Zero;
        private WndProc? _wndProcDelegate; // GC'ye karşı alanda tutulur
        private readonly ManualResetEventSlim _initDone = new(false);

        private int _x = 0;
        private int _y = 0;
        private int _w = 1;
        private int _h = 1;
        private bool _visible = false;
        private bool _disposed = false;

        private Bitmap? _renderBitmap;
        private IReadOnlyList<AnnotationStroke>? _pendingStrokes;
        private long _lastRenderTick = 0;
        private bool _renderScheduled = false;

        public bool IsExcludedFromCapture { get; private set; } = true;

        public void Start()
        {
            if (_thread != null) return;

            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "RunaAnnotationOverlay"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            _initDone.Wait(2000);
        }

        private void ThreadProc()
        {
            string className = "RunaAnnotationOverlayClass_" + Guid.NewGuid().ToString("N");
            IntPtr hInstance = GetModuleHandle(null);

            _wndProcDelegate = OverlayWndProc;

            var wcx = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _wndProcDelegate,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = className,
                hIconSm = IntPtr.Zero
            };

            RegisterClassEx(ref wcx);

            int exStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            _hwnd = CreateWindowEx(
                exStyle,
                className,
                "RunaAnnotationOverlay",
                WS_POPUP,
                _x, _y, _w, _h,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                // Yakalamadan hariç tutma (Windows 10 2004+)
                try
                {
                    bool ok = SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
                    IsExcludedFromCapture = ok;
                }
                catch
                {
                    IsExcludedFromCapture = false;
                }
            }

            _initDone.Set();

            // Win32 Standart Mesaj Döngüsü
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            _renderBitmap?.Dispose();
            _renderBitmap = null;
        }

        private IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_APP_DISPATCH:
                    while (_cmdQueue.TryDequeue(out var act))
                    {
                        try { act(); } catch { }
                    }
                    return IntPtr.Zero;

                case WM_APP_RENDER:
                    _renderScheduled = false;
                    RenderInternal();
                    return IntPtr.Zero;

                case WM_CLOSE:
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                default:
                    return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        private void EnqueueCommand(Action action)
        {
            if (_disposed) return;
            _cmdQueue.Enqueue(action);
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_APP_DISPATCH, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void SetBounds(int x, int y, int w, int h)
        {
            EnqueueCommand(() =>
            {
                _x = x;
                _y = y;
                _w = Math.Max(1, w);
                _h = Math.Max(1, h);

                uint flags = SWP_NOACTIVATE | (_visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);
                SetWindowPos(_hwnd, HWND_TOPMOST, _x, _y, _w, _h, flags);

                // Bitmap boyutunu eşitle
                if (_renderBitmap == null || _renderBitmap.Width != _w || _renderBitmap.Height != _h)
                {
                    _renderBitmap?.Dispose();
                    _renderBitmap = new Bitmap(_w, _h, PixelFormat.Format32bppPArgb);
                }

                RenderInternal();
            });
        }

        public void SetVisible(bool visible)
        {
            EnqueueCommand(() =>
            {
                _visible = visible;
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
                if (visible)
                {
                    RenderInternal();
                }
            });
        }

        public void Render(IReadOnlyList<AnnotationStroke> strokes)
        {
            if (_disposed) return;

            lock (_stateLock)
            {
                _pendingStrokes = strokes;
            }

            long now = Environment.TickCount64;
            long elapsed = now - _lastRenderTick;

            // En fazla 60 Hz çizim tazeleme (>= 16 ms)
            if (elapsed >= 16)
            {
                if (_hwnd != IntPtr.Zero)
                {
                    PostMessage(_hwnd, WM_APP_RENDER, IntPtr.Zero, IntPtr.Zero);
                }
            }
            else if (!_renderScheduled)
            {
                _renderScheduled = true;
                int delay = (int)Math.Max(1, 16 - elapsed);
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    if (_hwnd != IntPtr.Zero && !_disposed)
                    {
                        PostMessage(_hwnd, WM_APP_RENDER, IntPtr.Zero, IntPtr.Zero);
                    }
                });
            }
        }

        private void RenderInternal()
        {
            if (_disposed || _hwnd == IntPtr.Zero || _w <= 0 || _h <= 0)
                return;

            _lastRenderTick = Environment.TickCount64;

            IReadOnlyList<AnnotationStroke>? strokes;
            lock (_stateLock)
            {
                strokes = _pendingStrokes;
            }

            if (_renderBitmap == null || _renderBitmap.Width != _w || _renderBitmap.Height != _h)
            {
                _renderBitmap?.Dispose();
                _renderBitmap = new Bitmap(_w, _h, PixelFormat.Format32bppPArgb);
            }

            using (var g = Graphics.FromImage(_renderBitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (strokes != null && strokes.Count > 0)
                {
                    float dpiScale = 1.0f;
                    try
                    {
                        uint dpi = GetDpiForWindow(_hwnd);
                        if (dpi > 0) dpiScale = dpi / 96.0f;
                    }
                    catch { }

                    using var font = new Font("Segoe UI", Math.Max(8.0f, 9.0f * dpiScale), FontStyle.Bold, GraphicsUnit.Pixel);

                    long now = Environment.TickCount64;

                    for (int i = 0; i < strokes.Count; i++)
                    {
                        var s = strokes[i];
                        if (s.Points == null || s.Points.Count < 2 || s.Opacity <= 0.01 || s.Hidden)
                            continue;

                        uint rawColor = AnnotationProtocol.Palette[Math.Clamp(s.ColorIndex, 0, 7)];
                        byte a = (byte)Math.Clamp((int)Math.Round(((rawColor >> 24) & 0xFF) * s.Opacity), 0, 255);
                        byte r = (byte)((rawColor >> 16) & 0xFF);
                        byte gVal = (byte)((rawColor >> 8) & 0xFF);
                        byte b = (byte)(rawColor & 0xFF);

                        var color = Color.FromArgb(a, r, gVal, b);
                        float strokeWidth = (float)Math.Max(1.5, s.Width / 1000.0 * _h);

                        int ptCount = s.Points.Count / 2;
                        var gdiPoints = new PointF[ptCount];
                        for (int p = 0; p < ptCount; p++)
                        {
                            float px = (float)((s.Points[p * 2] + s.OffsetU) / 10000.0 * _w);
                            float py = (float)((s.Points[p * 2 + 1] + s.OffsetV) / 10000.0 * _h);
                            gdiPoints[p] = new PointF(px, py);
                        }

                        if (ptCount == 1)
                        {
                            using var brush = new SolidBrush(color);
                            float radius = strokeWidth / 2.0f;
                            g.FillEllipse(brush, gdiPoints[0].X - radius, gdiPoints[0].Y - radius, strokeWidth, strokeWidth);
                        }
                        else
                        {
                            using var pen = new Pen(color, strokeWidth)
                            {
                                StartCap = LineCap.Round,
                                EndCap = LineCap.Round,
                                LineJoin = LineJoin.Round
                            };
                            g.DrawLines(pen, gdiPoints);
                        }

                        // Ad Etiketi: Son 1,5 saniyede güncellenen çizginin son noktasına rozet
                        if (!string.IsNullOrEmpty(s.OwnerName) && (now - s.LastUpdateTick <= 1500))
                        {
                            DrawNameBadge(g, s.OwnerName, gdiPoints[ptCount - 1], color, font, dpiScale);
                        }
                    }
                }
            }

            // GDI 32bpp premultiplied bitmap ekrana UpdateLayeredWindow ile basılır
            IntPtr screenDC = GetDC(IntPtr.Zero);
            IntPtr memDC = CreateCompatibleDC(screenDC);
            IntPtr hBitmap = _renderBitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = SelectObject(memDC, hBitmap);

            POINT ptSrc = new POINT { x = 0, y = 0 };
            POINT ptDst = new POINT { x = _x, y = _y };
            SIZE size = new SIZE { cx = _w, cy = _h };
            BLENDFUNCTION blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size, memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

            SelectObject(memDC, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
        }

        private static void DrawNameBadge(Graphics g, string name, PointF pos, Color strokeColor, Font font, float dpiScale)
        {
            var textSize = g.MeasureString(name, font);
            float padX = 6.0f * dpiScale;
            float padY = 2.0f * dpiScale;
            float boxW = textSize.Width + (padX * 2.0f);
            float boxH = textSize.Height + (padY * 2.0f);

            float bx = pos.X + (6.0f * dpiScale);
            float by = pos.Y + (6.0f * dpiScale);

            // Zemin: çizgi renginde, hafif şeffaf
            using var bgBrush = new SolidBrush(Color.FromArgb(220, strokeColor.R, strokeColor.G, strokeColor.B));
            using var path = GetRoundedRectPath(new RectangleF(bx, by, boxW, boxH), 4.0f * dpiScale);
            g.FillPath(bgBrush, path);

            // Kontrast metin rengi
            double lum = 0.299 * strokeColor.R + 0.587 * strokeColor.G + 0.114 * strokeColor.B;
            Color textCol = lum > 140 ? Color.FromArgb(240, 17, 24, 39) : Color.White;
            using var textBrush = new SolidBrush(textCol);
            g.DrawString(name, font, textBrush, bx + padX, by + padY);
        }

        private static GraphicsPath GetRoundedRectPath(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2.0f;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            _thread?.Join(1000);
            _initDone.Dispose();
        }
    }
}
