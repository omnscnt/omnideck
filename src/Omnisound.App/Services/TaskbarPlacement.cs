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
        var point = new PointInt32(cursor.X, cursor.Y);
        return Measure(DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest), point, GetScale(cursor));
    }

    /// <summary>
    /// The same monitor as <paramref name="anchor"/>, with the taskbar as it is now: an auto-hidden one
    /// slides in and out while a flyout is open, and the flyout follows it.
    /// </summary>
    public static TaskbarAnchor Refresh(TaskbarAnchor anchor) =>
        Measure(DisplayArea.GetFromPoint(anchor.Cursor, DisplayAreaFallback.Nearest), anchor.Cursor, anchor.Scale);

    private static TaskbarAnchor Measure(DisplayArea display, PointInt32 cursor, double scale)
    {
        var work = display.WorkArea;
        var outer = display.OuterBounds;

        var edge = work.Y + work.Height < outer.Y + outer.Height ? TaskbarEdge.Bottom
            : work.Y > outer.Y ? TaskbarEdge.Top
            : work.X > outer.X ? TaskbarEdge.Left
            : work.X + work.Width < outer.X + outer.Width ? TaskbarEdge.Right
            : TaskbarEdge.Bottom;

        // Auto-hidden taskbar: the work area is the whole screen, so carve out the part of the taskbar
        // that is on screen right now. Hidden, it leaves only a sliver of a couple of pixels, and the
        // flyout sits down at the screen edge instead of above an empty strip.
        if (work.Equals(outer) && TryGetTaskbar(outer, out var bar))
        {
            int outerRight = outer.X + outer.Width, outerBottom = outer.Y + outer.Height;
            bool horizontal = bar.Right - bar.Left >= bar.Bottom - bar.Top;
            int visible = horizontal
                ? Math.Min(bar.Bottom, outerBottom) - Math.Max(bar.Top, outer.Y)
                : Math.Min(bar.Right, outerRight) - Math.Max(bar.Left, outer.X);
            if (visible <= 4 * scale) visible = 0;

            if (horizontal && bar.Top + bar.Bottom < outer.Y + outerBottom)
            {
                edge = TaskbarEdge.Top; work.Y += visible; work.Height -= visible;
            }
            else if (horizontal)
            {
                edge = TaskbarEdge.Bottom; work.Height -= visible;
            }
            else if (bar.Left + bar.Right < outer.X + outerRight)
            {
                edge = TaskbarEdge.Left; work.X += visible; work.Width -= visible;
            }
            else
            {
                edge = TaskbarEdge.Right; work.Width -= visible;
            }
        }

        return new TaskbarAnchor(edge, work, cursor, scale);
    }

    /// <summary>
    /// Makes <paramref name="hwnd"/> the foreground window. A tray click gives us that right, a hotkey
    /// from our keyboard hook does not (foreground lock): then join the foreground window's input queue
    /// for a moment, which lets the call through.
    /// </summary>
    public static bool BringToFront(IntPtr hwnd)
    {
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        var foreground = GetForegroundWindow();
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        uint self = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                AttachThreadInput(self, foregroundThread, false);
            }
        }
        return GetForegroundWindow() == hwnd;
    }

    public static IntPtr ForegroundWindow => GetForegroundWindow();

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

    /// <summary>The taskbar window on this monitor (the main one or a secondary monitor's), wherever it is now.</summary>
    private static bool TryGetTaskbar(RectInt32 outer, out RECT rect)
    {
        int outerRight = outer.X + outer.Width, outerBottom = outer.Y + outer.Height;
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            for (var hwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, className, null); hwnd != IntPtr.Zero;
                 hwnd = FindWindowEx(IntPtr.Zero, hwnd, className, null))
            {
                if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r)) continue;
                // Along its length the bar lies on this monitor; across it, it may be slid off the edge.
                bool horizontal = r.Right - r.Left >= r.Bottom - r.Top;
                int along = horizontal
                    ? Math.Min(r.Right, outerRight) - Math.Max(r.Left, outer.X)
                    : Math.Min(r.Bottom, outerBottom) - Math.Max(r.Top, outer.Y);
                bool touches = r.Right >= outer.X && r.Left <= outerRight && r.Bottom >= outer.Y && r.Top <= outerBottom;
                if (touches && along * 2 > (horizontal ? r.Right - r.Left : r.Bottom - r.Top))
                {
                    rect = r;
                    return true;
                }
            }
        }
        rect = default;
        return false;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
