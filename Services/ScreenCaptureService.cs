using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using RunaVoice_WinUI.Models;

namespace RunaVoice_WinUI.Services
{
    /// <summary>
    /// Yüksek Performanslı Sıfır-Tahsis (Zero-GC-Churn) Ekran & Pencere Yakalama Motoru:
    /// - Tekrar kullanılan kalıcı GDI DIB yüzeyleri (her karede new Bitmap tahsisi YOK!)
    /// - Ağ geri-basınç kapısı (ShouldSkipFrameGate) ile gereksiz CPU kodlamasını %100 engeller
    /// - Ön plandaki pencereler için doğrudan donanım BitBlt hızlandırması + arka plandakiler için PrintWindow
    /// </summary>
    public sealed class ScreenCaptureService : IDisposable
    {
        #region Win32 Interop
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const int DWMWA_CLOAKED = 14;
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest, IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr hdc, int iStretchMode);
        private const int COLORONCOLOR = 3;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_APPWINDOW = 0x00040000;
        private const uint GW_OWNER = 4;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int DWM_CLOAKED_APP = 1;
        private const int DWM_CLOAKED_SHELL = 2;
        private const int DWM_CLOAKED_INHERITED = 4;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }


        private const int GCLP_HICON = -14;
        private const uint WM_GETICON = 0x007F;
        private const int ICON_BIG = 1;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        #endregion

        public static bool IsWindowMinimized(IntPtr hWnd) => hWnd != IntPtr.Zero && IsIconic(hWnd);

        public static void RestoreWindowNoActivate(IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero && IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            }
        }

        private CancellationTokenSource? _captureCts;
        private Task? _captureTask;
        private readonly object _configLock = new();

        private ScreenCaptureSourceItem? _activeSource;
        private int _targetHeight = 1080;
        private int _targetFps = 30;
        private string _codecName = "MJPEG (JPEG kareleri)";
        private bool _dataSaver = false;

        // Canlı istatistikler
        private int _framesInLastSec = 0;
        private long _bytesInLastSec = 0;
        private long _lastStatsTick = Environment.TickCount64;

        public Func<bool>? ShouldSkipFrameGate { get; set; }

        /// <summary>Yayını alabilecek bağlı bir eş var mı? Yoksa yakalama saniyede bir kareye düşer.</summary>
        public Func<bool>? HasAudience { get; set; }

        /// <summary>
        /// Çizim takibi için gri takip karesi gerekiyor mu? (Çizim izni kapalıysa veya izleyici yoksa
        /// çizgi gelemez; 15 Hz'lik küçültme + gri dönüşüm boşa yapılmaz.)
        /// </summary>
        public Func<bool>? NeedsTrackingFrames { get; set; }
        private const long IdleFrameIntervalMs = 1000;

        public bool IsSharing => _captureCts != null && _activeSource != null;
        public string ActiveTargetName => _activeSource?.Title ?? "";
        public ScreenCaptureSourceItem? ActiveSource
        {
            get
            {
                lock (_configLock)
                {
                    return _activeSource;
                }
            }
        }
        public int CurrentWidth { get; private set; } = 1920;
        public int CurrentHeight { get; private set; } = 1080;
        private int _targetBitrateKbps = 4500;
        public int MeasuredFps { get; private set; } = 0;
        public int MeasuredBitrateKbps { get; private set; } = 0;
        public int TargetBitrateKbps => _targetBitrateKbps;
        public int CurrentTargetBitrateKbps => _targetBitrateKbps;
        public long TotalFramesCaptured { get; set; } = 0;
        public string ActiveCodecLabel => _codecName;

        // Olaylar
        public event Action<string, int, int, int>? OnScreenShareStarted; // title, width, height, fps
        public event Action<string, int, int, int>? OnScreenShareUpdated; // title, width, height, fps
        public event Action? OnScreenShareStopped;
        public event Action<byte[], int, int>? OnEncodedFrameReady;       // jpegBytes, width, height
        public event Action<byte[], int, int, uint>? OnEncodedFrameReadyWithId; // jpegBytes, width, height, frameId
        public event Action<byte[], int, int, uint>? OnTrackingFrameReady; // gray, width, height, frameId
        public event Action<int, int, int, long>? OnFrameEncoded; // bytes, width, height, timestampMs
        public Func<uint>? FrameIdGenerator { get; set; }
        public event Action<byte[]>? OnPreviewThumbnailReady;
        public event Action<int, int, int, int>? OnCaptureRectChanged;   // x, y, w, h (fiziksel px)

        // Dinamik Çözünürlük Uyarlanabilir Hedefleri (Özellik 4)
        private int _adaptiveTargetHeight = 0;
        private int _adaptiveTargetFps = 0;
        private int _adaptiveJpegQuality = 0;
        private int _skippedFramesInLastSec = 0;
        public int SkippedFramesCount { get; private set; } = 0;
        public int SkippedFramesInLastSec => _skippedFramesInLastSec;

        public void SetAdaptiveTarget(int maxHeight, int fps, int jpegQuality)
        {
            lock (_configLock)
            {
                _adaptiveTargetHeight = maxHeight;
                _adaptiveTargetFps = fps;
                _adaptiveJpegQuality = Math.Clamp(jpegQuality, 45, 88);
            }
        }

        public void ClearAdaptiveTarget()
        {
            lock (_configLock)
            {
                _adaptiveTargetHeight = 0;
                _adaptiveTargetFps = 0;
                _adaptiveJpegQuality = 0;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (WindowVisual Visual, long Timestamp)> _windowVisualCache = new();

        /// <summary>
        /// Madde 1: Hızlı numaralandırma (UI iş parçacığını dondurmadan <15ms içinde döner).
        /// Küçültülmüş pencereleri ve diğer masaüstü pencerelerini dahil eder.
        /// </summary>
        public List<ScreenCaptureSourceItem> GetAvailableSourcesQuick(IntPtr excludeSelfHwnd)
        {
            var results = new List<ScreenCaptureSourceItem>();

            // 1. Fiziksel Ekranlar (Monitörler)
            int monIndex = 1;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdcMon, ref RECT rc, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref info))
                {
                    int w = Math.Max(1, info.rcMonitor.Right - info.rcMonitor.Left);
                    int h = Math.Max(1, info.rcMonitor.Bottom - info.rcMonitor.Top);
                    bool isPrimary = (info.dwFlags & 1) != 0;

                    var item = new ScreenCaptureSourceItem
                    {
                        Id = $"mon_{monIndex}",
                        Title = isPrimary ? $"Ekran {monIndex} (Birincil)" : $"Ekran {monIndex}",
                        Subtitle = $"{w} × {h} Tam Ekran",
                        IsMonitor = true,
                        Handle = hMon,
                        BoundsX = info.rcMonitor.Left,
                        BoundsY = info.rcMonitor.Top,
                        BoundsWidth = w,
                        BoundsHeight = h,
                        IsLoadingThumbnail = true
                    };
                    results.Add(item);
                    monIndex++;
                }
                return true;
            }, IntPtr.Zero);

            // 2. Açık ve Küçültülmüş Uygulama Pencereleri
            EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == excludeSelfHwnd) return true;

                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title) || title is "Program Manager" or "Windows Input Experience")
                    return true;

                bool isIconic = IsIconic(hWnd);
                bool isVisible = IsWindowVisible(hWnd);
                if (!isVisible && !isIconic) return true;

                long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
                if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                IntPtr owner = GetWindow(hWnd, GW_OWNER);
                if (owner != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0)
                    return true;

                bool isOtherDesktop = false;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    if ((cloaked & DWM_CLOAKED_SHELL) != 0)
                    {
                        isOtherDesktop = true;
                    }
                    else
                    {
                        return true;
                    }
                }

                int w = 0, h = 0, left = 0, top = 0;
                if (isIconic)
                {
                    var wp = new WINDOWPLACEMENT();
                    wp.length = Marshal.SizeOf(wp);
                    if (GetWindowPlacement(hWnd, ref wp))
                    {
                        w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                        h = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                        left = wp.rcNormalPosition.Left;
                        top = wp.rcNormalPosition.Top;
                    }
                }
                else
                {
                    if (TryGetVisibleWindowRect(hWnd, out RECT rc))
                    {
                        w = rc.Right - rc.Left;
                        h = rc.Bottom - rc.Top;
                        left = rc.Left;
                        top = rc.Top;
                    }
                }

                if (w < 160 || h < 100) return true;

                string subtitle = isOtherDesktop
                    ? $"Diğer masaüstü • {w} × {h}"
                    : (isIconic ? $"Simge durumunda • {w} × {h}" : $"Uygulama Penceresi • {w} × {h}");

                results.Add(new ScreenCaptureSourceItem
                {
                    Id = $"win_{hWnd}",
                    Title = title.Length > 38 ? title[..38] + "…" : title,
                    Subtitle = subtitle,
                    IsMonitor = false,
                    Handle = hWnd,
                    BoundsX = left,
                    BoundsY = top,
                    BoundsWidth = w,
                    BoundsHeight = h,
                    IsMinimized = isIconic,
                    IsOtherDesktop = isOtherDesktop,
                    IsLoadingThumbnail = true
                });

                return true;
            }, IntPtr.Zero);

            return results;
        }

        public List<ScreenCaptureSourceItem> GetAvailableSources(IntPtr excludeSelfHwnd)
        {
            var results = GetAvailableSourcesQuick(excludeSelfHwnd);
            foreach (var item in results)
            {
                if (item.IsMonitor)
                {
                    item.Thumbnail = CaptureSourceThumbnail(item, 320, 180);
                    item.IsLoadingThumbnail = false;
                }
                else
                {
                    var v = BuildWindowVisual(item, 320, 180);
                    if (!string.IsNullOrEmpty(v.AppName))
                    {
                        string prefix = item.IsOtherDesktop ? "Diğer masaüstü" : (item.IsMinimized ? "Simge durumunda" : v.AppName);
                        item.Subtitle = $"{prefix} • {item.BoundsWidth} × {item.BoundsHeight}";
                    }
                    item.Thumbnail = AvatarHelper.CreateBitmapFromBytes(v.ThumbJpeg, 320);
                    item.AppIcon = AvatarHelper.CreateBitmapFromBytes(v.IconPng, 32);
                    item.IsLoadingThumbnail = false;
                }
            }
            return results;
        }

        /// <summary>
        /// Madde 1: Küçük resimleri ve simgeleri arka planda Task.Run ile üretip DispatcherQueue ile tek tek işler.
        /// </summary>
        public void PopulateVisualsAsync(IEnumerable<ScreenCaptureSourceItem> items, Microsoft.UI.Dispatching.DispatcherQueue dq, CancellationToken ct)
        {
            var list = items.ToList();
            _ = Task.Run(() =>
            {
                long now = Environment.TickCount64;

                Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, item =>
                {
                    if (ct.IsCancellationRequested) return;

                    if (item.IsMonitor)
                    {
                        var thumb = CaptureSourceThumbnail(item, 320, 180);
                        dq.TryEnqueue(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            item.Thumbnail = thumb;
                            item.IsLoadingThumbnail = false;
                        });
                        return;
                    }

                    string cacheKey = $"{item.Handle}_{item.BoundsWidth}_{item.BoundsHeight}_{item.IsMinimized}";
                    WindowVisual visual;
                    if (_windowVisualCache.TryGetValue(cacheKey, out var entry) && (now - entry.Timestamp < 8000))
                    {
                        visual = entry.Visual;
                    }
                    else
                    {
                        visual = BuildWindowVisual(item, 320, 180);
                        _windowVisualCache[cacheKey] = (visual, now);
                    }

                    dq.TryEnqueue(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        if (!string.IsNullOrEmpty(visual.AppName))
                        {
                            string prefix = item.IsOtherDesktop ? "Diğer masaüstü" : (item.IsMinimized ? "Simge durumunda" : visual.AppName);
                            item.Subtitle = $"{prefix} • {item.BoundsWidth} × {item.BoundsHeight}";
                        }
                        item.Thumbnail = AvatarHelper.CreateBitmapFromBytes(visual.ThumbJpeg, 320);
                        item.AppIcon = AvatarHelper.CreateBitmapFromBytes(visual.IconPng, 32);
                        item.IsLoadingThumbnail = false;
                    });
                });
            }, ct);
        }

        private readonly record struct WindowVisual(byte[]? ThumbJpeg, byte[]? IconPng, string AppName);

        /// <summary>
        /// Pencerenin kendi içeriğinden (PrintWindow, PW_RENDERFULLCONTENT) küçük resim, uygulama simgesi ve
        /// uygulama adı üretir. İçerik alınamazsa (siyah/boş) küçük resim simgeli bir kart olarak çizilir.
        /// </summary>
        private static WindowVisual BuildWindowVisual(ScreenCaptureSourceItem item, int thumbW, int thumbH)
        {
            string exePath = GetProcessImagePath(item.Handle);
            string appName = GetAppDisplayName(exePath);
            using Bitmap? icon = GetWindowIconBitmap(item.Handle, exePath);

            byte[]? iconPng = null;
            if (icon != null)
            {
                using var ims = new MemoryStream();
                icon.Save(ims, ImageFormat.Png);
                iconPng = ims.ToArray();
            }

            byte[]? thumbJpeg = null;
            try
            {
                using var thumb = new Bitmap(thumbW, thumbH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.Clear(Color.FromArgb(12, 10, 23));
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;

                    using Bitmap? raw = (IsHungAppWindow(item.Handle) || item.IsMinimized) ? null : PrintWindowToBitmap(item.Handle);
                    if (raw != null && !IsMostlyBlank(raw))
                    {
                        float scale = Math.Min((float)thumbW / raw.Width, (float)thumbH / raw.Height);
                        int dw = Math.Max(1, (int)(raw.Width * scale));
                        int dh = Math.Max(1, (int)(raw.Height * scale));
                        g.DrawImage(raw, (thumbW - dw) / 2, (thumbH - dh) / 2, dw, dh);
                    }
                    else if (icon != null)
                    {
                        // İçerik alınamadı: ortada büyük uygulama simgesi
                        using var bg = new LinearGradientBrush(new Rectangle(0, 0, thumbW, thumbH),
                            Color.FromArgb(30, 22, 58), Color.FromArgb(12, 10, 23), 45f);
                        g.FillRectangle(bg, 0, 0, thumbW, thumbH);
                        int s = 64;
                        g.DrawImage(icon, (thumbW - s) / 2, (thumbH - s) / 2, s, s);
                    }
                }

                using var ms = new MemoryStream();
                thumb.Save(ms, ImageFormat.Jpeg);
                thumbJpeg = ms.ToArray();
            }
            catch { }

            return new WindowVisual(thumbJpeg, iconPng, appName);
        }

        /// <summary>Pencereyi, üstünde başka pencere olsa bile kendi içeriğiyle bir bitmap'e çizer.</summary>
        private static Bitmap? PrintWindowToBitmap(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT wr)) return null;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w < 2 || h < 2 || w > 16384 || h > 16384) return null;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (!ok) { bmp.Dispose(); return null; }

            // DPI farkında olmayan (eski) uygulamalar PrintWindow ile mantıksal boyutta (ör. %200 ekranda yarı boyutta)
            // çizilir; geri kalan alan siyah kalır. Fiziksel koordinatları pencerenin DPI oranına indir.
            double dpiScale = 1.0;
            uint winDpi = GetDpiForWindow(hwnd);
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (winDpi > 0 && mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint monDpi, out _) == 0 && monDpi > winDpi)
                dpiScale = (double)winDpi / monDpi;

            // Görünmez yeniden boyutlandırma kenarlıklarını kırp (görünür pencere sınırları)
            if (TryGetVisibleWindowRect(hwnd, out RECT vr))
            {
                var crop = Rectangle.Intersect(new Rectangle((int)((vr.Left - wr.Left) * dpiScale), (int)((vr.Top - wr.Top) * dpiScale),
                                                             (int)((vr.Right - vr.Left) * dpiScale), (int)((vr.Bottom - vr.Top) * dpiScale)),
                                               new Rectangle(0, 0, w, h));
                if (crop.Width >= 2 && crop.Height >= 2 && (crop.Width != w || crop.Height != h))
                {
                    var cropped = bmp.Clone(crop, PixelFormat.Format32bppRgb);
                    bmp.Dispose();
                    return cropped;
                }
            }
            return bmp;
        }

        /// <summary>Örnek piksellerin neredeyse tamamı siyahsa (içerik çizilememiş) true.</summary>
        private static bool IsMostlyBlank(Bitmap bmp)
        {
            int dark = 0, total = 0;
            for (int y = 1; y < 8; y++)
            {
                for (int x = 1; x < 8; x++)
                {
                    var c = bmp.GetPixel(bmp.Width * x / 8, bmp.Height * y / 8);
                    total++;
                    if (c.R + c.G + c.B < 12) dark++;
                }
            }
            return dark >= total * 0.95;
        }

        /// <summary>DWM'nin çizdiği görünür pencere sınırları (görünmez yeniden boyutlandırma kenarlıkları hariç).</summary>
        private static bool TryGetVisibleWindowRect(IntPtr hwnd, out RECT rc)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rc, Marshal.SizeOf<RECT>()) == 0 &&
                rc.Right > rc.Left && rc.Bottom > rc.Top)
                return true;
            return GetWindowRect(hwnd, out rc);
        }

        private static string GetProcessImagePath(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(hProc, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private static string GetAppDisplayName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                string name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription
                            : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName
                            : Path.GetFileNameWithoutExtension(exePath);
                name = name.Trim();
                return name.Length > 28 ? name[..28] + "…" : name;
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(exePath);
            }
        }

        /// <summary>Pencerenin büyük simgesi; yoksa sınıf simgesi; o da yoksa çalıştırılabilir dosyanın simgesi.</summary>
        private static Bitmap? GetWindowIconBitmap(IntPtr hwnd, string exePath)
        {
            try
            {
                IntPtr hIcon = IntPtr.Zero;
                if (SendMessageTimeout(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out IntPtr res) != IntPtr.Zero)
                    hIcon = res;
                if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GCLP_HICON);
                if (hIcon != IntPtr.Zero)
                {
                    using var icon = Icon.FromHandle(hIcon);
                    return icon.ToBitmap();
                }
                if (!string.IsNullOrEmpty(exePath))
                {
                    using var exeIcon = Icon.ExtractAssociatedIcon(exePath);
                    return exeIcon?.ToBitmap();
                }
            }
            catch { }
            return null;
        }

        public void StartOrUpdateCapture(ScreenCaptureSourceItem source, int targetHeight, int targetFps, string codec, bool dataSaver)
        {
            bool isNew = false;
            lock (_configLock)
            {
                _activeSource = source;
                _targetHeight = dataSaver ? Math.Min(720, targetHeight) : Math.Clamp(targetHeight, 480, 2160);
                _targetFps = dataSaver ? Math.Min(20, targetFps) : Math.Clamp(targetFps, 15, 60);
                _dataSaver = dataSaver;
                _targetBitrateKbps = _targetHeight >= 1440 ? 8000 : (_targetHeight >= 1080 ? (_targetFps >= 60 ? 5500 : 3800) : 2200);
                _codecName = "MJPEG (JPEG kareleri)";

                if (_captureCts == null)
                {
                    isNew = true;
                    _captureCts = new CancellationTokenSource();
                    var token = _captureCts.Token;
                    _captureTask = Task.Run(() => CaptureLoopAsync(token), token);
                }
            }

            if (isNew)
            {
                OnScreenShareStarted?.Invoke(source.Title, CurrentWidth, CurrentHeight, _targetFps);
            }
            else
            {
                OnScreenShareUpdated?.Invoke(source.Title, CurrentWidth, CurrentHeight, _targetFps);
            }
        }

        public void StopCapture()
        {
            CancellationTokenSource? cts;
            lock (_configLock)
            {
                cts = _captureCts;
                _captureCts = null;
                _activeSource = null;
                MeasuredFps = 0;
                MeasuredBitrateKbps = 0;
            }

            if (cts != null)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
                OnScreenShareStopped?.Invoke();
            }
        }

        private async Task CaptureLoopAsync(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            long lastThumbSentMs = -5000;
            var jpegEncoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            Bitmap? reusableEncodeBmp = null;
            Bitmap? reusableTrackingBmp = null;
            Graphics? trackingGraphics = null;
            byte[]? reusableTrackingGrayBuffer = null;
            long lastTrackingTick = 0;
            using var encodeStream = new MemoryStream(98304);
            using var thumbBmp = new Bitmap(320, 180, PixelFormat.Format24bppRgb);
            using var thumbStream = new MemoryStream(16384);

            int lastSrcX = int.MinValue;
            int lastSrcY = int.MinValue;
            int lastRawW = -1;
            int lastRawH = -1;

            long lastIdleFrameMs = -IdleFrameIntervalMs;

            // Pencere yakalama durumu (yalnızca bu iş parçacığında kullanılır)
            WindowGraphicsCapture? wgc = null;
            IntPtr wgcHandle = IntPtr.Zero;
            Bitmap? windowScratch = null;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    long frameStartMs = sw.ElapsedMilliseconds;

                    ScreenCaptureSourceItem? src;
                    int targetH, targetFps;
                    bool saver;
                    lock (_configLock)
                    {
                        src = _activeSource;
                        targetH = _targetHeight;
                        targetFps = _targetFps;
                        saver = _dataSaver;
                    }

                    if (src == null) break;

                    // İzleyen kimse yokken tam hızda yakalayıp JPEG kodlamak boşa CPU harcar
                    // (4K kaynakta kare başına ~10-20 ms). Bu durumda yalnızca saniyede bir kare üretilir:
                    // önizleme kapağı ve ilk bağlanan izleyiciye hemen bir görüntü gitmesi için yeterli.
                    if (HasAudience != null && !HasAudience())
                    {
                        if (frameStartMs - lastIdleFrameMs < IdleFrameIntervalMs)
                        {
                            try { await Task.Delay(100, token).ConfigureAwait(false); } catch { break; }
                            continue;
                        }
                        lastIdleFrameMs = frameStartMs;
                    }

                    // Geri-basınç kapısı: Önceki kare hâlâ gönderiliyorsa CPU'yu hiç yorma!
                    if (ShouldSkipFrameGate?.Invoke() == true)
                    {
                        SkippedFramesCount++;
                        _skippedFramesInLastSec++;
                        try { await Task.Delay(6, token).ConfigureAwait(false); } catch { break; }
                        continue;
                    }

                    int effTargetH = _adaptiveTargetHeight > 0 ? Math.Min(targetH, _adaptiveTargetHeight) : targetH;
                    int effTargetFps = _adaptiveTargetFps > 0 ? Math.Min(targetFps, _adaptiveTargetFps) : targetFps;

                    try
                    {
                        int srcX = src.BoundsX;
                        int srcY = src.BoundsY;
                        int rawW = Math.Max(2, src.BoundsWidth);
                        int rawH = Math.Max(2, src.BoundsHeight);
                        Bitmap? windowFrame = null;

                        if (!src.IsMonitor)
                        {
                            // Paylaşılan pencere kapandıysa yayını düzgünce sonlandır
                            if (!IsWindow(src.Handle) || (wgc != null && wgc.IsClosed))
                            {
                                _ = Task.Run(StopCapture);
                                break;
                            }

                            if (!TryGetVisibleWindowRect(src.Handle, out RECT wrc))
                            {
                                await Task.Delay(50, token).ConfigureAwait(false);
                                continue;
                            }
                            srcX = wrc.Left;
                            srcY = wrc.Top;

                            // Pencere içeriği: WGC (üstü kapalı/arkadaki pencerede de doğru), olmazsa PrintWindow
                            if (wgcHandle != src.Handle)
                            {
                                wgc?.Dispose();
                                wgc = WindowGraphicsCapture.TryCreate(src.Handle);
                                wgcHandle = src.Handle;
                            }

                            windowFrame = GrabWindowFrame(src.Handle, wgc, ref windowScratch);
                            if (windowFrame == null)
                            {
                                // Henüz kare yok (WGC ilk kareyi bekliyor veya pencere küçültülmüş)
                                await Task.Delay(15, token).ConfigureAwait(false);
                                continue;
                            }
                            rawW = Math.Max(2, windowFrame.Width);
                            rawH = Math.Max(2, windowFrame.Height);
                        }
                        else if (wgc != null)
                        {
                            // Yayın sırasında pencereden monitöre geçildi: pencere oturumunu kapat
                            wgc.Dispose();
                            wgc = null;
                            wgcHandle = IntPtr.Zero;
                        }

                        if (srcX != lastSrcX || srcY != lastSrcY || rawW != lastRawW || rawH != lastRawH)
                        {
                            lastSrcX = srcX;
                            lastSrcY = srcY;
                            lastRawW = rawW;
                            lastRawH = rawH;
                            OnCaptureRectChanged?.Invoke(srcX, srcY, rawW, rawH);
                        }

                        float aspect = (float)rawW / Math.Max(1, rawH);
                        int cappedH = Math.Min(rawH, effTargetH);
                        int maxRealtimeH = effTargetFps >= 60 ? Math.Min(cappedH, 1080) : Math.Min(cappedH, 1080);
                        int outW = Math.Max(2, ((int)Math.Round(maxRealtimeH * aspect) / 2) * 2);
                        int outH = Math.Max(2, (maxRealtimeH / 2) * 2);

                        CurrentWidth = outW;
                        CurrentHeight = outH;

                        if (reusableEncodeBmp == null || reusableEncodeBmp.Width != outW || reusableEncodeBmp.Height != outH)
                        {
                            reusableEncodeBmp?.Dispose();
                            reusableEncodeBmp = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
                        }

                        // Monitör: donanım hızlandırmalı StretchBlt/BitBlt. Pencere: kendi içeriğinden ölçekle.
                        bool capturedOk = windowFrame != null
                            ? ScaleWindowFrameInto(windowFrame, reusableEncodeBmp)
                            : CaptureIntoBitmap(src, srcX, srcY, rawW, rawH, outW, outH, reusableEncodeBmp);

                        if (capturedOk)
                        {
                            uint frameId = FrameIdGenerator != null ? FrameIdGenerator() : (uint)(TotalFramesCaptured + 1);
                            long nowTick = Environment.TickCount64;

                            // Takip karesi (en fazla 15 Hz ve yalnızca abone varsa)
                            if (OnTrackingFrameReady != null && (nowTick - lastTrackingTick >= 66) &&
                                (NeedsTrackingFrames == null || NeedsTrackingFrames()))
                            {
                                lastTrackingTick = nowTick;
                                int trackW = 480;
                                int trackH = Math.Max(120, (int)Math.Round((double)outH * 480.0 / outW));
                                trackH = (trackH / 2) * 2;

                                if (reusableTrackingBmp == null || reusableTrackingBmp.Width != trackW || reusableTrackingBmp.Height != trackH)
                                {
                                    reusableTrackingBmp?.Dispose();
                                    trackingGraphics?.Dispose();
                                    reusableTrackingBmp = new Bitmap(trackW, trackH, PixelFormat.Format24bppRgb);
                                    trackingGraphics = Graphics.FromImage(reusableTrackingBmp);
                                    trackingGraphics.CompositingMode = CompositingMode.SourceCopy;
                                    trackingGraphics.InterpolationMode = InterpolationMode.Bilinear;
                                    trackingGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                                }

                                trackingGraphics?.DrawImage(reusableEncodeBmp, 0, 0, trackW, trackH);

                                int reqLen = trackW * trackH;
                                if (reusableTrackingGrayBuffer == null || reusableTrackingGrayBuffer.Length < reqLen)
                                {
                                    reusableTrackingGrayBuffer = new byte[reqLen];
                                }

                                BitmapData bData = reusableTrackingBmp.LockBits(new Rectangle(0, 0, trackW, trackH), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                                try
                                {
                                    unsafe
                                    {
                                        byte* pScan0 = (byte*)bData.Scan0;
                                        int stride = bData.Stride;
                                        fixed (byte* pDest = reusableTrackingGrayBuffer)
                                        {
                                            for (int y = 0; y < trackH; y++)
                                            {
                                                byte* pRow = pScan0 + (y * stride);
                                                byte* pOut = pDest + (y * trackW);
                                                for (int x = 0; x < trackW; x++)
                                                {
                                                    pOut[x] = (byte)((pRow[2] * 77 + pRow[1] * 150 + pRow[0] * 29) >> 8);
                                                    pRow += 3;
                                                }
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    reusableTrackingBmp.UnlockBits(bData);
                                }

                                OnTrackingFrameReady?.Invoke(reusableTrackingGrayBuffer, trackW, trackH, frameId);
                            }

                            long quality = _adaptiveJpegQuality > 0
                                ? (long)_adaptiveJpegQuality
                                : (saver ? 55L : (effTargetFps >= 60 ? 70L : 80L));
                            encodeStream.SetLength(0);

                            if (jpegEncoder != null)
                            {
                                using var ep = new EncoderParameters(1);
                                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                                reusableEncodeBmp.Save(encodeStream, jpegEncoder, ep);
                            }
                            else
                            {
                                reusableEncodeBmp.Save(encodeStream, ImageFormat.Jpeg);
                            }

                            byte[] encodedBytes = encodeStream.ToArray();
                            OnFrameEncoded?.Invoke(encodedBytes.Length, outW, outH, frameStartMs);

                            // Her 4 saniyede bir "Yayını İzle" önizleme kartı için 320x180 küçük kapak üret
                            if (frameStartMs - lastThumbSentMs > 4000)
                            {
                                lastThumbSentMs = frameStartMs;
                                using (var tg = Graphics.FromImage(thumbBmp))
                                {
                                    tg.CompositingMode = CompositingMode.SourceCopy;
                                    tg.InterpolationMode = InterpolationMode.Low;
                                    tg.DrawImage(reusableEncodeBmp, 0, 0, 320, 180);
                                }
                                thumbStream.SetLength(0);
                                if (jpegEncoder != null)
                                {
                                    using var tep = new EncoderParameters(1);
                                    tep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 52L);
                                    thumbBmp.Save(thumbStream, jpegEncoder, tep);
                                }
                                else thumbBmp.Save(thumbStream, ImageFormat.Jpeg);

                                OnPreviewThumbnailReady?.Invoke(thumbStream.ToArray());
                            }

                            // Telemetri sayacı
                            _framesInLastSec++;
                            _bytesInLastSec += encodedBytes.Length;
                            if (nowTick - _lastStatsTick >= 1000)
                            {
                                double elapsedSec = (nowTick - _lastStatsTick) / 1000.0;
                                MeasuredFps = (int)Math.Round(_framesInLastSec / elapsedSec);
                                MeasuredBitrateKbps = (int)Math.Round((_bytesInLastSec * 8.0 / 1000.0) / elapsedSec);
                                _framesInLastSec = 0;
                                _bytesInLastSec = 0;
                                _skippedFramesInLastSec = 0;
                                _lastStatsTick = nowTick;
                            }

                            TotalFramesCaptured++;
                            OnEncodedFrameReady?.Invoke(encodedBytes, outW, outH);
                            OnEncodedFrameReadyWithId?.Invoke(encodedBytes, outW, outH, frameId);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenCapture] Frame error: {ex.Message}");
                    }

                    double targetIntervalMs = 1000.0 / Math.Clamp(effTargetFps, 15, 60);
                    double nextFrameMs = frameStartMs + targetIntervalMs;
                    double remainingMs = nextFrameMs - sw.ElapsedMilliseconds;
                    if (remainingMs > 2.0)
                    {
                        try { await Task.Delay((int)Math.Floor(remainingMs), token).ConfigureAwait(false); } catch { break; }
                    }
                    while (sw.ElapsedMilliseconds < nextFrameMs)
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
            finally
            {
                reusableEncodeBmp?.Dispose();
                reusableTrackingBmp?.Dispose();
                trackingGraphics?.Dispose();
                windowScratch?.Dispose();
                wgc?.Dispose();
            }
        }

        /// <summary>
        /// Pencerenin güncel karesini <paramref name="scratch"/> bitmap'ine alır (yeniden kullanılır).
        /// WGC varsa onu, yoksa PrintWindow'u kullanır. Kare yoksa null.
        /// </summary>
        private static Bitmap? GrabWindowFrame(IntPtr hwnd, WindowGraphicsCapture? wgc, ref Bitmap? scratch)
        {
            if (wgc != null)
            {
                if (!wgc.TryUpdateFrame() || !wgc.HasFrame) return null;
                int w = wgc.Width, h = wgc.Height;
                if (scratch == null || scratch.Width != w || scratch.Height != h || scratch.PixelFormat != PixelFormat.Format32bppPArgb)
                {
                    scratch?.Dispose();
                    scratch = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                }
                var data = scratch.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int rowBytes = w * 4;
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(wgc.Pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
                finally
                {
                    scratch.UnlockBits(data);
                }
                return scratch;
            }

            // Yedek yol: PrintWindow (WGC kullanılamayan sistemler). Küçültülmüş pencerede içerik yoktur.
            if (IsIconic(hwnd) || IsHungAppWindow(hwnd)) return scratch;
            var printed = PrintWindowToBitmap(hwnd);
            if (printed == null) return scratch;
            scratch?.Dispose();
            scratch = printed;
            return scratch;
        }

        private static bool ScaleWindowFrameInto(Bitmap frame, Bitmap dest)
        {
            try
            {
                using var g = Graphics.FromImage(dest);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                bool sameSize = frame.Width == dest.Width && frame.Height == dest.Height;
                g.InterpolationMode = sameSize ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                g.Clear(Color.Black);
                g.DrawImage(frame, new Rectangle(0, 0, dest.Width, dest.Height), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool CaptureIntoBitmap(ScreenCaptureSourceItem src, int srcX, int srcY, int srcW, int srcH, int destW, int destH, Bitmap destBmp)
        {
            using var g = Graphics.FromImage(destBmp);
            IntPtr hdcDest = g.GetHdc();
            try
            {
                if (destW != srcW || destH != srcH)
                {
                    SetStretchBltMode(hdcDest, COLORONCOLOR);
                }

                IntPtr desktopHwnd = GetDesktopWindow();
                IntPtr hdcDesktop = GetWindowDC(desktopHwnd);
                try
                {
                    if (destW == srcW && destH == srcH)
                    {
                        BitBlt(hdcDest, 0, 0, destW, destH, hdcDesktop, srcX, srcY, SRCCOPY);
                    }
                    else
                    {
                        StretchBlt(hdcDest, 0, 0, destW, destH, hdcDesktop, srcX, srcY, srcW, srcH, SRCCOPY);
                    }
                    DrawCursorOntoDc(hdcDest, srcX, srcY, (float)destW / srcW, (float)destH / srcH);
                }
                finally
                {
                    ReleaseDC(desktopHwnd, hdcDesktop);
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                g.ReleaseHdc(hdcDest);
            }
        }

        private struct CachedCursorMetrics
        {
            public int HotspotX;
            public int HotspotY;
            public int Width;
            public int Height;
        }

        private static IntPtr _lastCursorHandle = IntPtr.Zero;
        private static CachedCursorMetrics _lastCursorMetrics;

        private static void DrawCursorOntoDc(IntPtr hdc, int originX, int originY, float scaleX = 1f, float scaleY = 1f)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
            {
                CachedCursorMetrics metrics;
                if (ci.hCursor == _lastCursorHandle)
                {
                    metrics = _lastCursorMetrics;
                }
                else
                {
                    metrics = new CachedCursorMetrics { HotspotX = 0, HotspotY = 0, Width = 32, Height = 32 };
                    if (GetIconInfo(ci.hCursor, out ICONINFO ii))
                    {
                        metrics.HotspotX = ii.xHotspot;
                        metrics.HotspotY = ii.yHotspot;

                        IntPtr hTargetBm = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
                        if (hTargetBm != IntPtr.Zero && GetObject(hTargetBm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) > 0)
                        {
                            metrics.Width = bm.bmWidth;
                            metrics.Height = (ii.hbmColor == IntPtr.Zero) ? (bm.bmHeight / 2) : bm.bmHeight;
                        }

                        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    }
                    _lastCursorHandle = ci.hCursor;
                    _lastCursorMetrics = metrics;
                }

                int cx = (int)Math.Round((ci.ptScreenPos.x - metrics.HotspotX - originX) * scaleX);
                int cy = (int)Math.Round((ci.ptScreenPos.y - metrics.HotspotY - originY) * scaleY);
                int cw = (int)Math.Max(8, Math.Round(metrics.Width * scaleX));
                int ch = (int)Math.Max(8, Math.Round(metrics.Height * scaleY));

                DrawIconEx(hdc, cx, cy, ci.hCursor, cw, ch, 0, IntPtr.Zero, DI_NORMAL);
            }
        }

        private static BitmapImage? CaptureSourceThumbnail(ScreenCaptureSourceItem item, int thumbW, int thumbH)
        {
            try
            {
                int w = Math.Max(2, item.BoundsWidth);
                int h = Math.Max(2, item.BoundsHeight);
                using var raw = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                if (!CaptureIntoBitmap(item, item.BoundsX, item.BoundsY, w, h, w, h, raw)) return null;

                using var thumb = new Bitmap(thumbW, thumbH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.Clear(Color.FromArgb(12, 10, 23));
                    g.InterpolationMode = InterpolationMode.Bilinear;

                    float scale = Math.Min((float)thumbW / raw.Width, (float)thumbH / raw.Height);
                    int dw = (int)(raw.Width * scale);
                    int dh = (int)(raw.Height * scale);
                    int dx = (thumbW - dw) / 2;
                    int dy = (thumbH - dh) / 2;
                    g.DrawImage(raw, dx, dy, dw, dh);
                }

                using var ms = new MemoryStream();
                thumb.Save(ms, ImageFormat.Jpeg);
                return AvatarHelper.CreateBitmapFromBytes(ms.ToArray(), thumbW);
            }
            catch
            {
                return null;
            }
        }

        private const int MaxRemoteFrameWidth = 3840;
        private const int MaxRemoteFrameHeight = 2400;

        /// <summary>
        /// JPEG başlığındaki (SOFn) genişlik/yüksekliği resmi çözmeden okur. JPEG değilse false.
        /// </summary>
        public static bool TryReadJpegSize(byte[]? data, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

            int i = 2;
            while (i + 3 < data.Length)
            {
                if (data[i] != 0xFF) return false;
                byte marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }              // dolgu
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { i += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) return false; // SOF bulunmadan görüntü verisi/son

                int segLen = (data[i + 2] << 8) | data[i + 3];
                if (segLen < 2 || i + 2 + segLen > data.Length) return false;

                bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    if (segLen < 7) return false;
                    height = (data[i + 5] << 8) | data[i + 6];
                    width = (data[i + 7] << 8) | data[i + 8];
                    return true;
                }
                i += 2 + segLen;
            }
            return false;
        }

        private static readonly object SharpBufferLock = new();
        private static byte[]? _pooledBgra;
        private static byte[]? _pooledSrcCopy;

        /// <summary>
        /// Yalnızca kullanıcı manuel olarak Keskinlik (%8-%100) açtığında kullanılır.
        /// Havuzlanmış (pooled) bellek kullanarak GC baskısını sıfırlar.
        /// </summary>
        public static unsafe byte[]? DecodeJpegToBgra(byte[] jpegBytes, int sharpnessPercent, out int width, out int height, out int validByteLen)
        {
            width = 0;
            height = 0;
            validByteLen = 0;
            try
            {
                // Ağdan gelen veri: çözmeden önce gerçekten JPEG olduğunu ve boyutunun makul olduğunu doğrula.
                // (GDI+ PNG/TIFF/BMP gibi başka biçimleri de çözer; başlığında 65535x65535 yazan bir kare
                // tek başına GB'larca bellek ayırtıp uygulamayı çökertebilir.)
                if (!TryReadJpegSize(jpegBytes, out int hdrW, out int hdrH) ||
                    hdrW <= 0 || hdrH <= 0 || hdrW > MaxRemoteFrameWidth || hdrH > MaxRemoteFrameHeight)
                {
                    return null;
                }

                using var ms = new MemoryStream(jpegBytes);
                using var bmp = new Bitmap(ms);
                width = bmp.Width;
                height = bmp.Height;

                var rect = new Rectangle(0, 0, width, height);
                var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int byteLen = width * height * 4;
                validByteLen = byteLen;

                lock (SharpBufferLock)
                {
                    if (_pooledBgra == null || _pooledBgra.Length < byteLen)
                        _pooledBgra = new byte[byteLen];

                    try
                    {
                        if (data.Stride == width * 4)
                        {
                            Marshal.Copy(data.Scan0, _pooledBgra, 0, byteLen);
                        }
                        else
                        {
                            for (int y = 0; y < height; y++)
                            {
                                Marshal.Copy(data.Scan0 + y * data.Stride, _pooledBgra, y * width * 4, width * 4);
                            }
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }

                    if (sharpnessPercent >= 8 && width > 4 && height > 4)
                    {
                        if (_pooledSrcCopy == null || _pooledSrcCopy.Length < byteLen)
                            _pooledSrcCopy = new byte[byteLen];

                        Buffer.BlockCopy(_pooledBgra, 0, _pooledSrcCopy, 0, byteLen);
                        float strength = Math.Clamp(sharpnessPercent / 100f, 0f, 1.0f) * 0.65f;
                        int stride = width * 4;

                        fixed (byte* src = _pooledSrcCopy)
                        fixed (byte* dst = _pooledBgra)
                        {
                            for (int y = 1; y < height - 1; y++)
                            {
                                int rowOffset = y * stride;
                                for (int x = 1; x < width - 1; x++)
                                {
                                    int p = rowOffset + x * 4;
                                    for (int c = 0; c < 3; c++)
                                    {
                                        int center = src[p + c];
                                        int neighbors = src[p - 4 + c] + src[p + 4 + c] + src[p - stride + c] + src[p + stride + c];
                                        float detail = center - (neighbors * 0.25f);
                                        int boosted = (int)(center + detail * (strength * 2.4f));
                                        dst[p + c] = (byte)(boosted < 0 ? 0 : (boosted > 255 ? 255 : boosted));
                                    }
                                }
                            }
                        }
                    }

                    return _pooledBgra;
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
