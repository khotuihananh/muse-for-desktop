using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace MuseDesktop;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_SPACE = 0x20;
    public const uint VK_M = 0x4D;
    public const uint VK_V = 0x56;

    // System metrics for the virtual (all-monitors) screen, in physical pixels.
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL_KEY = 0x11;
    private const byte VK_V_KEY = 0x56;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    // ---- Dark title bar (the window's top frame) via DWM ----------------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Ask DWM for the immersive dark caption. Attribute 20 is the
    /// documented one (Win10 2004+/Win11); 19 is the pre-2004 spelling.</summary>
    public static void TryDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* Older Windows: the caption just stays light. */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- Low-level keyboard hook (the v1.6.4 hotkey engine) ------------
    // RegisterHotKey was retired: on the user's machine bindings saved
    // fine but WM_HOTKEY never arrived, silently. A WH_KEYBOARD_LL hook
    // sees every key before the system translates it, matches it against
    // the live bindings, and swallows matches — there is no per-key
    // registration step left that can fail unnoticed. The hook either
    // installs (checked once, loudly) or it does not.
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public static void SendCtrlV()
    {
        keybd_event(VK_CONTROL_KEY, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V_KEY, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V_KEY, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL_KEY, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void SendEnterKey()
    {
        const byte VK_RETURN_KEY = 0x0D;
        keybd_event(VK_RETURN_KEY, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN_KEY, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ------------------------------------------------------------ keyboard hook

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_APP_AUTOSEND = 0x8000 + 42;
    public const uint VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int LLKHF_INJECTED = 0x10;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------ window listing

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRectNative(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static bool TryGetWindowRect(IntPtr hWnd, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        if (!GetWindowRectNative(hWnd, out var r)) return false;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;
        rect = new Rectangle(r.Left, r.Top, w, h);
        return true;
    }
}
