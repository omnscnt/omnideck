using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Omnisound.Osd;

/// <summary>
/// On-screen volume indicator: appears on the monitor under the mouse when a volume changes, stays
/// for a moment and fades away. Purely informational: a layered, click-through, never-activating
/// topmost window (WinUI windows can't be click-through), drawn by <see cref="VolumeOsdView"/>.
/// Runs on its own thread so showing it never waits on the UI thread.
/// </summary>
public sealed class VolumeOsd : IDisposable
{
    private const int WM_APP_SHOW = 0x8001, WM_APP_SETTINGS = 0x8002, WM_APP_QUIT = 0x8003;
    private const int AnimationTimer = 1;
    private const double FadeIn = 0.15, FadeOut = 0.22, SlideDip = 8; // seconds, DIPs

    private enum Phase { Hidden, Showing, Holding, Hiding }

    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly WndProc _wndProc; // kept alive while the window class exists
    private readonly VolumeOsdView _view = new();
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd;

    // Shared (under _gate).
    private VolumeOsdSettings _settings = new();
    private OsdContent? _incoming;

    // Window thread.
    private OsdContent? _content;
    private Phase _phase = Phase.Hidden;
    private double _opacity, _level, _holdUntil, _lastTick;
    private int _x, _y; // top-left of the window at rest, physical px
    private double _scale = 1;
    private OsdLayout _layout = new VolumeOsdSettings().ToLayout();
    private bool _fromBottom, _light, _timer;

    public VolumeOsd()
    {
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "omnisound volume indicator" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public void Configure(VolumeOsdSettings settings)
    {
        lock (_gate) _settings = settings.Clone();
        PostMessage(_hwnd, WM_APP_SETTINGS, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Shows (or updates) the indicator. Any thread.</summary>
    public void Show(OsdContent content, bool force = false)
    {
        lock (_gate)
        {
            if (!_settings.Enabled && !force) return;
            _incoming = content;
        }
        PostMessage(_hwnd, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- window thread ---------------------------------------------------------------------------

    private void Run()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "OmnisoundVolumeIndicator",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            wc.lpszClassName, "omnisound volume", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>An exception must not escape into the native message loop: that would take the app down.</summary>
    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_APP_SHOW: OnShow(); return IntPtr.Zero;
                case WM_APP_SETTINGS: if (!Settings.Enabled) HideNow(); return IntPtr.Zero;
                case WM_TIMER: Tick(); return IntPtr.Zero;
                case WM_NCHITTEST: return (IntPtr)(-1); // HTTRANSPARENT: never takes the mouse
                case WM_MOUSEACTIVATE: return (IntPtr)3; // MA_NOACTIVATE
                case WM_APP_QUIT:
                    StopTimer();
                    DestroyWindow(hwnd);
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Omnisound.Services.CrashLog.Write("volume indicator", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private VolumeOsdSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void OnShow()
    {
        OsdContent? content;
        lock (_gate) content = _incoming;
        if (content == null) return;

        _content = content;
        if (_phase == Phase.Hidden)
        {
            _layout = Settings.ToLayout(); // the size is taken when it appears
            Place();
            _level = content.Volume; // a fresh indicator starts at the real value
            _opacity = 0;
            _light = AppsUseLightTheme();
            ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        if (_phase != Phase.Holding) _phase = Phase.Showing; // also turns a fade-out back around
        _holdUntil = Now + Math.Max(300, Settings.DurationMs) / 1000.0;
        StartTimer();
        Draw();
    }

    /// <summary>Picks the spot on the monitor under the mouse, in that monitor's DPI.</summary>
    private void Place()
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        _scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var work = info.rcWork;
        var size = VolumeOsdView.SizeDip(_layout);
        int w = (int)Math.Ceiling(size.Width * _scale), h = (int)Math.Ceiling(size.Height * _scale);
        // The pill (not its shadow margin) keeps this distance from the screen's edges.
        int edge = (int)Math.Round((48 - VolumeOsdView.ShadowMargin) * _scale);
        var position = Settings.Position;
        _fromBottom = position is OsdPosition.BottomCenter or OsdPosition.BottomLeft or OsdPosition.BottomRight;
        _x = position switch
        {
            OsdPosition.BottomLeft or OsdPosition.TopLeft => work.Left + edge,
            OsdPosition.BottomRight or OsdPosition.TopRight => work.Right - edge - w,
            _ => work.Left + (work.Right - work.Left - w) / 2,
        };
        _y = _fromBottom ? work.Bottom - edge - h : work.Top + edge;
    }

    private void Tick()
    {
        double now = Now, dt = Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        if (_content == null) { HideNow(); return; }

        double target = _content.Volume;
        bool levelMoving = Math.Abs(target - _level) > 0.0005;
        _level = levelMoving ? _level + (target - _level) * (1 - Math.Exp(-dt / 0.05)) : target;

        switch (_phase)
        {
            case Phase.Showing:
                _opacity = Math.Min(1, _opacity + dt / FadeIn);
                if (_opacity >= 1) _phase = Phase.Holding;
                break;
            case Phase.Holding:
                if (now >= _holdUntil) _phase = Phase.Hiding;
                else if (!levelMoving) return; // nothing changes on screen while it just sits there
                break;
            case Phase.Hiding:
                _opacity = Math.Max(0, _opacity - dt / FadeOut);
                if (_opacity <= 0) { HideNow(); return; }
                break;
        }
        Draw();
    }

    private void Draw()
    {
        if (_content == null) return;
        // Eased opacity; slides in from (and out toward) the nearer screen edge.
        float eased = (float)(1 - Math.Pow(1 - _opacity, 3));
        int slide = (int)Math.Round((1 - eased) * SlideDip * _scale) * (_fromBottom ? 1 : -1);
        var pixels = _view.Render(_content, _layout, (float)_level, eased, _light, _scale, out int w, out int h);
        Present(pixels, w, h, _x, _y + slide);
    }

    private void HideNow()
    {
        StopTimer();
        _phase = Phase.Hidden;
        _opacity = 0;
        ShowWindow(_hwnd, 0 /* SW_HIDE */);
    }

    private void StartTimer()
    {
        if (_timer) return;
        _timer = true;
        _lastTick = Now;
        SetTimer(_hwnd, (IntPtr)AnimationTimer, 15, IntPtr.Zero);
    }

    private void StopTimer()
    {
        if (!_timer) return;
        _timer = false;
        KillTimer(_hwnd, (IntPtr)AnimationTimer);
    }

    /// <summary>Hands the premultiplied pixels to the layered window, at (x, y) on screen.</summary>
    private void Present(byte[] pixels, int width, int height, int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            Marshal.Copy(pixels, 0, bits, Math.Min(pixels.Length, width * height * 4));
            var size = new SIZE { cx = width, cy = height };
            var source = new POINT();
            var position = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(_hwnd, screen, ref position, ref size, memory, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>The indicator sits over apps, so it follows the app (not the taskbar) theme.</summary>
    private static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    public void Dispose() => PostMessage(_hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);

    // ---- Win32 -------------------------------------------------------------------------------------

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const uint WM_TIMER = 0x0113, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
