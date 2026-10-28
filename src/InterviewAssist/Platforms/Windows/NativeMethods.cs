using System.Runtime.InteropServices;

namespace InterviewAssist.Native;

/// <summary>Win32 interop used by <see cref="WindowsWindowService"/>.</summary>
internal static partial class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const nint WS_EX_LAYERED = 0x00080000;
    internal const uint LWA_ALPHA = 0x2;

    internal const uint WDA_NONE = 0x0;
    internal const uint WDA_MONITOR = 0x1;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>Wheel movement of one mouse-wheel notch.</summary>
    internal const int WHEEL_DELTA = 120;

    internal const uint WM_NCDESTROY = 0x0082;
    internal const uint WM_HOTKEY = 0x0312;

    internal const uint MOD_ALT = 0x1;
    internal const uint MOD_CONTROL = 0x2;
    internal const uint MOD_SHIFT = 0x4;
    internal const uint MOD_WIN = 0x8;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const uint VK_F1 = 0x70;
    internal const uint VK_F24 = 0x87;

    /// <summary>SUBCLASSPROC. Instances passed to SetWindowSubclass must be kept alive by the caller.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    // The *Ptr variants only exist in 64-bit user32; 32-bit processes must use the plain LONG versions.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    internal static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtrW(hWnd, nIndex) : GetWindowLongW(hWnd, nIndex);

    internal static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtrW(hWnd, nIndex, dwNewLong) : SetWindowLongW(hWnd, nIndex, unchecked((int)dwNewLong));
}
