using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// Host tarafında oyun dışındaki pencereleri gizlemek için çalışan Win32 perde penceresi (PDF Bölüm 9)
    /// Ayrı iş parçacığında WS_POPUP, WS_EX_TOPMOST | WS_EX_TOOLWINDOW ile tam ekran koyu katman oluşturur.
    /// Yakalamadan hariç tutulmaz (SetWindowDisplayAffinity çağrılmaz); oyuncu perdeyi görür.
    /// </summary>
    public class PrivacyCurtain : IDisposable
    {
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        private const int WM_DESTROY = 0x0002;
        private const int WM_CLOSE = 0x0010;
        private const int WM_PAINT = 0x000F;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_USER_SHOW = 0x0400 + 1;
        private const int WM_USER_HIDE = 0x0400 + 2;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, [In] ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int DrawText(IntPtr hdc, string lpString, int nCount, ref RECT lpRect, uint uFormat);

        [DllImport("gdi32.dll")]
        private static extern uint SetTextColor(IntPtr hdc, uint crColor);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int iBkMode);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int TRANSPARENT = 1;
        private const uint DT_CENTER = 0x1;
        private const uint DT_VCENTER = 0x4;
        private const uint DT_SINGLELINE = 0x20;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private Thread? _curtainThread;
        private IntPtr _curtainHwnd = IntPtr.Zero;
        private IntPtr _winEventHook = IntPtr.Zero;
        private WinEventDelegate? _winEventProc;
        private WndProcDelegate? _wndProc;

        private readonly HashSet<uint> _allowedProcessIds = new();
        private readonly List<string> _allowedProcessNames = new();
        private IntPtr _lastGameWindowHwnd = IntPtr.Zero;

        private CancellationTokenSource? _curtainDelayCts;
        private readonly object _lock = new();

        public bool IsVisible { get; private set; }
        public event Action<bool>? OnCurtainVisibilityChanged;

        public PrivacyCurtain()
        {
            _allowedProcessNames.Add("sunshine.exe");
        }

        public void SetAllowedGame(string exePath, List<string>? extraProcesses)
        {
            lock (_lock)
            {
                _allowedProcessNames.Clear();
                _allowedProcessNames.Add("sunshine.exe");

                string mainExe = Path.GetFileName(exePath);
                if (!string.IsNullOrEmpty(mainExe)) _allowedProcessNames.Add(mainExe);

                if (extraProcesses != null)
                {
                    foreach (var ep in extraProcesses)
                    {
                        if (!string.IsNullOrEmpty(ep)) _allowedProcessNames.Add(ep);
                    }
                }

                // Çalışan süreçler arasından eşleşenleri ekle
                RefreshAllowedProcessIds();
            }
        }

        public void RefreshAllowedProcessIds()
        {
            lock (_lock)
            {
                _allowedProcessIds.Clear();
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        string name = proc.ProcessName + ".exe";
                        if (_allowedProcessNames.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase) || n.Equals(proc.ProcessName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _allowedProcessIds.Add((uint)proc.Id);
                            if (proc.MainWindowHandle != IntPtr.Zero)
                            {
                                _lastGameWindowHwnd = proc.MainWindowHandle;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        public void Start()
        {
            if (_curtainThread != null) return;

            var readyEvent = new ManualResetEventSlim(false);

            _curtainThread = new Thread(() =>
            {
                _wndProc = CurtainWndProc;

                var wcx = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = _wndProc,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = IntPtr.Zero,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = "RunaPrivacyCurtainClass",
                    hIconSm = IntPtr.Zero
                };
                RegisterClassEx(ref wcx);

                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);

                _curtainHwnd = CreateWindowEx(
                    WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                    "RunaPrivacyCurtainClass",
                    "RunaPrivacyCurtain",
                    WS_POPUP,
                    0, 0, screenW, screenH,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                readyEvent.Set();

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            })
            {
                IsBackground = true,
                Name = "RunaPrivacyCurtainThread"
            };

            _curtainThread.SetApartmentState(ApartmentState.STA);
            _curtainThread.Start();
            readyEvent.Wait(2000);

            // Ön plan pencere değişimlerini dinle (SetWinEventHook: PDF Bölüm 9.1)
            _winEventProc = WinEventCallback;
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_MINIMIZESTART,
                IntPtr.Zero,
                _winEventProc,
                0, 0,
                WINEVENT_OUTOFCONTEXT);
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || hwnd == _curtainHwnd) return;

            GetWindowThreadProcessId(hwnd, out uint pid);
            bool isAllowed = false;

            lock (_lock)
            {
                if (_allowedProcessIds.Contains(pid))
                {
                    isAllowed = true;
                    _lastGameWindowHwnd = hwnd;
                }
                else
                {
                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        string name = proc.ProcessName + ".exe";
                        if (_allowedProcessNames.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase) || n.Equals(proc.ProcessName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _allowedProcessIds.Add(pid);
                            isAllowed = true;
                            _lastGameWindowHwnd = hwnd;
                        }
                    }
                    catch { }
                }
            }

            if (!isAllowed)
            {
                // Ön plandaki pencerenin süreci izinli değilse ve bu durum 150 ms sürerse perde iner (PDF Bölüm 9.1)
                _curtainDelayCts?.Cancel();
                _curtainDelayCts = new CancellationTokenSource();
                var token = _curtainDelayCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(150, token);
                        if (!token.IsCancellationRequested)
                        {
                            Show();
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
            else
            {
                _curtainDelayCts?.Cancel();
                if (IsVisible)
                {
                    Hide();
                }
            }
        }

        public void Show()
        {
            if (IsVisible || _curtainHwnd == IntPtr.Zero) return;
            IsVisible = true;

            PostMessage(_curtainHwnd, WM_USER_SHOW, IntPtr.Zero, IntPtr.Zero);
            OnCurtainVisibilityChanged?.Invoke(true);

            // Perde inince odak hemen izinli oyun penceresine geri verilir (PDF Bölüm 9.1)
            RefocusGame();
        }

        public void Hide()
        {
            if (!IsVisible || _curtainHwnd == IntPtr.Zero) return;
            IsVisible = false;

            PostMessage(_curtainHwnd, WM_USER_HIDE, IntPtr.Zero, IntPtr.Zero);
            OnCurtainVisibilityChanged?.Invoke(false);
        }

        private void RefocusGame()
        {
            if (_lastGameWindowHwnd != IntPtr.Zero)
            {
                SetForegroundWindow(_lastGameWindowHwnd);
            }
        }

        private IntPtr CurtainWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_USER_SHOW:
                    SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    return IntPtr.Zero;

                case WM_USER_HIDE:
                    SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW);
                    return IntPtr.Zero;

                case WM_PAINT:
                    {
                        IntPtr hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);
                        GetClientRect(hWnd, out RECT rect);

                        // Koyu mor/siyah arka plan (#0E0B1D)
                        IntPtr brush = CreateSolidBrush(0x001D0B0E);
                        FillRect(hdc, ref rect, brush);
                        DeleteObject(brush);

                        SetBkMode(hdc, TRANSPARENT);
                        SetTextColor(hdc, 0x00FCFAF8); // Beyaz metin
                        string text = "🛡️ Host'un masaüstü gizli — oyuna dönülüyor…";
                        DrawText(hdc, text, text.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

                        EndPaint(hWnd, ref ps);
                        return IntPtr.Zero;
                    }

                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_KEYDOWN:
                    // Perdeye gelen tıklama/tuş arkaya ulaşmaz ve yine odağı oyuna verir (PDF Bölüm 9.1)
                    RefocusGame();
                    return IntPtr.Zero;

                case WM_CLOSE:
                    DestroyWindow(hWnd);
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            _curtainDelayCts?.Cancel();
            _curtainDelayCts?.Dispose();

            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }

            if (_curtainHwnd != IntPtr.Zero)
            {
                PostMessage(_curtainHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _curtainHwnd = IntPtr.Zero;
            }

            _curtainThread = null;
        }
    }
}
