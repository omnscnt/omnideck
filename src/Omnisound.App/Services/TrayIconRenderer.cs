using System.Drawing;
using System.Runtime.InteropServices;

namespace Omnisound.Services;

/// <summary>The tray icon: the app icon's entry drawn for the size the taskbar shows icons at for its DPI.</summary>
public static class TrayIconRenderer
{
    /// <summary>Small-icon size (SM_CXSMICON) at the taskbar's DPI.</summary>
    public static int TraySize
    {
        get
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
            return GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi != 0 ? dpi : GetDpiForSystem());
        }
    }

    /// <summary>
    /// The entry of Assets\omnisound.ico made for <paramref name="size"/> (16–32 px are drawn separately,
    /// see tools\make-icon.ps1), so the tray never shows a rescaled one.
    /// </summary>
    public static Icon Load(int size) => new(Path.Combine(AppContext.BaseDirectory, "Assets", "omnisound.ico"), size, size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
