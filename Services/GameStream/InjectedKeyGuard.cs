using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RunaVoice_WinUI.Services.GameStream
{
    /// <summary>
    /// LLKHF_INJECTED bayraklı uzaktan enjekte edilen tehlikeli tuş kombinasyonlarını yutan,
    /// host'un fiziksel acil durdurma kısayolunu (Ctrl+Alt+End) dinleyen Win32 klavye kancası (PDF Bölüm 9.1)
    /// </summary>
    public class InjectedKeyGuard : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const byte VK_TAB = 0x09;
        private const byte VK_ESCAPE = 0x1B;
        private const byte VK_END = 0x23;
        private const byte VK_LWIN = 0x5B;
        private const byte VK_RWIN = 0x5C;
        private const byte VK_D = 0x44;
        private const byte VK_R = 0x52;
        private const byte VK_E = 0x45;
        private const byte VK_L = 0x4C;
        private const byte VK_X = 0x58;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12; // Alt

        private const int LLKHF_EXTENDED = 0x01;
        private const int LLKHF_LOWER_IL_INJECTED = 0x02;
        private const int LLKHF_INJECTED = 0x10;
        private const int LLKHF_ALTDOWN = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc? _proc;
        private bool _remoteWinDown = false;

        public bool IsInstalled => _hookId != IntPtr.Zero;
        public event Action? OnEmergencyStopTriggered; // Ctrl+Alt+End (yalnızca fiziksel tuş, PDF Bölüm 9.1)

        public void Install()
        {
            if (IsInstalled) return;

            _proc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr moduleHandle = GetModuleHandle(curModule?.ModuleName);

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        }

        public void Uninstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _proc = null;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && lParam != IntPtr.Zero)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool isInjected = (kbd.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
                int msg = (int)wParam;
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                // 1. Host acil kısayolu: Ctrl+Alt+End (YALNIZCA enjekte olmayan / fiziksel tuş) (PDF Bölüm 9.1)
                if (!isInjected && isDown && kbd.vkCode == VK_END)
                {
                    bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (kbd.flags & LLKHF_ALTDOWN) != 0;

                    if (ctrlDown && altDown)
                    {
                        OnEmergencyStopTriggered?.Invoke();
                        return (IntPtr)1; // Tuşu yut
                    }
                }

                // 2. Enjekte Tuş Koruması (PDF Bölüm 9.1: Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Win+D/R/E/L/X vb.)
                if (isInjected)
                {
                    if (kbd.vkCode == VK_LWIN || kbd.vkCode == VK_RWIN)
                    {
                        _remoteWinDown = isDown;
                        return (IntPtr)1; // Uzaktan gelen Win tuşunu tamamen yut
                    }

                    // Uzaktan Win kombinasyonları (Win+D, Win+R, Win+E, Win+L, Win+X vb.)
                    if (_remoteWinDown)
                    {
                        return (IntPtr)1;
                    }

                    // Uzaktan Alt+Tab veya Alt+Esc
                    bool altDown = (kbd.flags & LLKHF_ALTDOWN) != 0;
                    if (altDown && (kbd.vkCode == VK_TAB || kbd.vkCode == VK_ESCAPE))
                    {
                        return (IntPtr)1;
                    }

                    // Uzaktan Ctrl+Esc
                    bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    if (ctrlDown && kbd.vkCode == VK_ESCAPE)
                    {
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Uninstall();
        }
    }
}
