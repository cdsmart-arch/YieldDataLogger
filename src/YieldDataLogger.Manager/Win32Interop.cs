using System.Runtime.InteropServices;

namespace YieldDataLogger.Manager;

/// <summary>
/// Small P/Invoke surface used to style the window frame. Wrapped in its own file so the
/// ugliness of Win32 flags is quarantined and easy to extend (Mica, border colour, ...).
/// </summary>
internal static class Win32Interop
{
    // Attribute IDs for DwmSetWindowAttribute. 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on
    // Windows 10 build 19041+, Windows 11. Older 1809/1903 exposed the same flag as 19 -
    // we try 20 first, fall back to 19 if that returned a failure HRESULT.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE        = 20;
    // DWMWA_CAPTION_COLOR (Windows 11 22H2+): lets us paint the caption to match the dashboard.
    private const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Ask DWM to render the title bar in its dark variant. Safe to call on builds that
    /// don't support it - the attribute just won't apply and the call returns a failure
    /// HRESULT we ignore.
    /// </summary>
    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        var use = 1;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref use, sizeof(int));
        if (hr != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref use, sizeof(int));
    }

    /// <summary>
    /// Set the caption (title-bar) fill colour. 0xRRGGBB packed as 0x00BBGGRR little-endian.
    /// Ignored on builds earlier than Windows 11 22H2.
    /// </summary>
    public static void SetCaptionColor(IntPtr hwnd, byte r, byte g, byte b)
    {
        var bgr = (b << 16) | (g << 8) | r;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref bgr, sizeof(int));
    }
}
