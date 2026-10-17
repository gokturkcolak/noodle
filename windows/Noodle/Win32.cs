using System.Runtime.InteropServices;

namespace Noodle;

static partial class Win32
{
    public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct DWM_BLURBEHIND { public uint dwFlags; public bool fEnable; public IntPtr hRgnBlur; public bool fTransitionOnMaximized; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO info);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, ref int value, uint winIni);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int l, int t, int r, int b);

    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND bb);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Seconds since the last keyboard or mouse input anywhere in the session.
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    /// Settings > Accessibility > Visual effects > Animation effects, off. Windows' Reduce Motion.
    public static bool ReduceMotion()
    {
        const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        int on = 1;
        return SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0) && on == 0;
    }

    /// Scale of the monitor under `pt` relative to 96 dpi, so CSS px in the pages map to device pixels.
    public static double ScaleAt(Point pt)
    {
        var mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
    }

    /// Shows a window on top without taking focus from whatever you're typing in.
    public static void ShowNoActivate(IntPtr hwnd, Rectangle r) =>
        SetWindowPos(hwnd, HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// Per-pixel alpha for a borderless window: DWM blends its surface (painted black, alpha 0) with what's
    /// behind, so WebView2's transparent background really is see-through. Same trick Tauri/tao uses.
    public static void EnableAlpha(IntPtr hwnd)
    {
        var rgn = CreateRectRgn(0, 0, -1, -1);
        var bb = new DWM_BLURBEHIND { dwFlags = 0x1 | 0x2 /* ENABLE | BLURREGION */, fEnable = true, hRgnBlur = rgn };
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        DeleteObject(rgn);
    }

    /// Title bar to match the page: dark mode, and on Windows 11 the page's own background color.
    public static void StyleTitleBar(IntPtr hwnd, bool dark, Color caption)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        int bgr = caption.R | caption.G << 8 | caption.B << 16;
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref bgr, sizeof(int));
    }

    /// Windows' app theme (Settings > Personalization > Colors), which the pages follow too.
    public static bool DarkMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
