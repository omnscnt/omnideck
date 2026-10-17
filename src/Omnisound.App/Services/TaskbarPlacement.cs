using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Omnisound.Services;

public enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>Where the taskbar is on the monitor under the cursor, and the usable area next to it (physical px).</summary>
public readonly record struct TaskbarAnchor(TaskbarEdge Edge, RectInt32 WorkArea, PointInt32 Cursor, double Scale)
{
    public int Right => WorkArea.X + WorkArea.Width;
    public int Bottom => WorkArea.Y + WorkArea.Height;
}

/// <summary>Shared placement logic for everything that pops up from the tray (mixer flyout, context menu).</summary>
public static class TaskbarPlacement
{
    public static TaskbarAnchor Capture()
    {
        GetCursorPos(out var cursor);
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var outer = display.OuterBounds;

        var edge = work.Y + work.Height < outer.Y + outer.Height ? TaskbarEdge.Bottom
            : work.Y > outer.Y ? TaskbarEdge.Top
            : work.X > outer.X ? TaskbarEdge.Left
            : work.X + work.Width < outer.X + outer.Width ? TaskbarEdge.Right
            : TaskbarEdge.Bottom;

        // Auto-hidden taskbar: the work area is the whole screen, so carve the taskbar out ourselves.
        if (work.Equals(outer) && TryGetTaskbar(out var barEdge, out var bar)
            && bar.Right > outer.X && bar.Left < outer.X + outer.Width
            && bar.Bottom > outer.Y && bar.Top < outer.Y + outer.Height)
        {
            switch (barEdge)
            {
                case 0: edge = TaskbarEdge.Left; work.X = bar.Right; work.Width -= bar.Right - outer.X; break;
                case 1: edge = TaskbarEdge.Top; work.Y = bar.Bottom; work.Height -= bar.Bottom - outer.Y; break;
                case 2: edge = TaskbarEdge.Right; work.Width = bar.Left - outer.X; break;
                default: edge = TaskbarEdge.Bottom; work.Height = bar.Top - outer.Y; break;
            }
        }

        return new TaskbarAnchor(edge, work, new PointInt32(cursor.X, cursor.Y), GetScale(cursor));
    }

    public static void BringToFront(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>
    /// Puts an always-on-top window directly beneath the taskbar in z-order: still above every normal
    /// window, but the taskbar covers whatever part of it overlaps — so a flyout can slide out from behind it.
    /// </summary>
    public static void PlaceBelowTaskbar(IntPtr hwnd)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;
        SetWindowPos(hwnd, taskbar, 0, 0, 0, 0, 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | 0x0010 /* NOACTIVATE */);
    }

    private static double GetScale(POINT point)
    {
        var monitor = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    private static bool TryGetTaskbar(out uint edge, out RECT rect)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        bool ok = SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref data) != IntPtr.Zero;
        edge = data.uEdge;
        rect = data.rc;
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
