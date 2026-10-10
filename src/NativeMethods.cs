using System.Runtime.InteropServices;

namespace OnScreenKeyboard;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_SPACE = 0x20;
    public const int VK_CAPITAL = 0x14;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vKey);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    public static void SendKey(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = { ki = { wVk = vk, wScan = scan, dwFlags = flags } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Primary language id (low 10 bits of the LANGID) of the focused window's keyboard layout.</summary>
    public static int GetForegroundPrimaryLanguage()
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        long hkl = GetKeyboardLayout(thread);
        return (int)(hkl & 0x3FF);
    }
}
