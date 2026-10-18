using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Win32;
using Omnisound.Core.Media;

namespace Omnisound.Taskbar;

/// <summary>
/// Now-playing widget drawn inside the taskbar. A layered (per-pixel alpha) window is made a child
/// of Shell_TrayWnd, so it sits in the taskbar itself: never covered by it, moves and auto-hides
/// with it, and disappears with it under fullscreen apps. WinUI can't render into a layered window,
/// so <see cref="TaskbarWidgetView"/> draws it with Win2D.
///
/// Runs on its own thread with its own message loop: a child of another process's window shares
/// that process's input queue, and this keeps any stall in Explorer away from the app's UI thread.
/// </summary>
public sealed class TaskbarWidget : IDisposable
{
    private const int WM_APP_UPDATE = 0x8001;
    private const int WM_APP_QUIT = 0x8002;
    private const int RefreshTimer = 1, AnimationTimer = 2, PendingTimer = 3;
    private const uint RefreshIntervalMs = 1500; // follows taskbar resizes, DPI changes, Explorer restarts

    private readonly NowPlayingWatcher _watcher;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly WndProc _wndProc; // kept alive while the window class exists
    private readonly WinEventProc _winEventProc; // kept alive while the hook exists
    private IntPtr _foregroundHook;
    private readonly TaskbarWidgetView _view = new();

    private IntPtr _hwnd;
    private IntPtr _parent;
    private bool _visible;

    // Shared with other threads (under _gate).
    private TaskbarWidgetSettings _settings = new() { Enabled = false };
    private NowPlaying? _track;
    private CanvasBitmap? _cover;
    private byte[]? _coverFor;    // the thumbnail bytes _cover was decoded from
    private byte[]? _coverSource;

    // Window-thread state.
    private bool _hovered, _trackingMouse, _light, _animating;
    private WidgetPart _hot, _pressed;
    private double _hoverLevel, _dimLevel, _lastTick, _marqueeStart;
    private bool _marqueeResting = true; // at the start, not moving (set by the frame timer)
    private string? _marqueeKey;
    private double _scale = 1;
    private uint _frameMs;
    private (int Left, int Width, int Height) _placement;
    private ((int Left, int Width, int Height), bool) _drawn;

    // What is on screen (it can lag the incoming track while its cover loads), and the slide between tracks.
    private NowPlaying? _shown, _previous;
    private CanvasBitmap? _shownCover, _previousCover;
    private double _transitionStart = double.NegativeInfinity, _pendingSince, _skipTime = double.NegativeInfinity;
    private int _direction = 1, _skipDirection = 1;
    private string? _pendingKey;

    public TaskbarWidget(NowPlayingWatcher watcher)
    {
        _watcher = watcher;
        _wndProc = WindowProc;
        _winEventProc = OnForegroundChanged;
        watcher.Changed += OnTrackChanged;
        _track = watcher.Current;
        _thread = new Thread(Run) { IsBackground = true, Name = "omnisound taskbar widget" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Configure(TaskbarWidgetSettings settings)
    {
        lock (_gate) _settings = settings.Clone();
        Post(WM_APP_UPDATE);
    }

    private void OnTrackChanged(NowPlaying? track)
    {
        lock (_gate) _track = track;
        var bytes = track?.Thumbnail;
        if (!ReferenceEquals(bytes, _coverSource))
        {
            _coverSource = bytes;
            if (bytes == null) lock (_gate) { _cover = null; _coverFor = null; }
            else _ = DecodeCoverAsync(bytes);
        }
        Post(WM_APP_UPDATE);
    }

    private async Task DecodeCoverAsync(byte[] bytes)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), stream);
            lock (_gate)
            {
                if (!ReferenceEquals(bytes, _coverSource)) return; // a newer cover already arrived
                _cover = bitmap;
                _coverFor = bytes;
            }
            Post(WM_APP_UPDATE);
        }
        catch (Exception)
        {
            // Unsupported or broken image: keep the placeholder.
        }
    }

    private void Post(int message)
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, message, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- window thread ---------------------------------------------------------------------------

    private void Run()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */),
            lpszClassName = "OmnisoundTaskbarWidget",
        };
        RegisterClassEx(ref wc);
        // Created as a hidden popup, then turned into a child of the taskbar (see Attach).
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "omnisound widget",
            unchecked((uint)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        SetTimer(_hwnd, (IntPtr)RefreshTimer, RefreshIntervalMs, IntPtr.Zero);
        // Another window coming to the front may be (or leave) a fullscreen one. Out-of-context: the
        // callback arrives through this thread's message loop. Fullscreen toggled inside the same
        // window (F11) is caught by the refresh timer.
        _foregroundHook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _winEventProc, 0, 0, 2 /* WINEVENT_SKIPOWNPROCESS */);
        Update();

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
            return HandleMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Omnisound.Services.CrashLog.Write("taskbar widget", ex);
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_UPDATE:
                Update();
                return IntPtr.Zero;

            case WM_TIMER:
                if (wParam == (IntPtr)AnimationTimer) Tick();
                else
                {
                    if (wParam == (IntPtr)PendingTimer) KillTimer(hwnd, (IntPtr)PendingTimer);
                    Update(refresh: wParam == (IntPtr)RefreshTimer);
                }
                return IntPtr.Zero;

            case WM_APP_QUIT:
                KillTimer(hwnd, (IntPtr)RefreshTimer);
                if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
                StopAnimation();
                DestroyWindow(hwnd);
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (!_trackingMouse)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
                    _trackingMouse = TrackMouseEvent(ref tme);
                }
                SetHover(true, _view.HitTest(LoX(lParam), HiY(lParam)));
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                if (_pressed == WidgetPart.None) SetHover(false, WidgetPart.None);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                _pressed = _view.HitTest(LoX(lParam), HiY(lParam));
                SetCapture(hwnd);
                Draw();
                return IntPtr.Zero;

            case WM_LBUTTONUP:
            {
                var part = _view.HitTest(LoX(lParam), HiY(lParam));
                var pressed = _pressed;
                _pressed = WidgetPart.None;
                ReleaseCapture();
                if (part == pressed) Invoke(part);
                SetHover(_trackingMouse, part);
                Draw();
                return IntPtr.Zero;
            }

            case WM_MOUSEACTIVATE:
                return (IntPtr)3; // MA_NOACTIVATE: clicking the widget doesn't take focus
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void SetHover(bool hovered, WidgetPart hot)
    {
        if (hovered == _hovered && hot == _hot) return;
        _hovered = hovered;
        _hot = hot;
        Draw();
        StartAnimation(); // hover fill and the paused dimming ease in / out
    }

    private void Invoke(WidgetPart part)
    {
        string? appId;
        lock (_gate) appId = _track?.AppName;
        // Remember the direction for the slide when the new track arrives.
        if (part is WidgetPart.Previous or WidgetPart.Next)
        {
            _skipTime = Now;
            _skipDirection = part == WidgetPart.Next ? 1 : -1;
        }
        Task.Run(async () =>
        {
            switch (part)
            {
                case WidgetPart.PlayPause: await _watcher.TogglePlayPauseAsync(); break;
                case WidgetPart.Previous: await _watcher.PreviousAsync(); break;
                case WidgetPart.Next: await _watcher.NextAsync(); break;
                case WidgetPart.Info: if (appId != null) ActivateApp(appId); break;
            }
        });
    }

    /// <summary>Brings the player's main window forward (restoring it if minimised).</summary>
    private static void ActivateApp(string appName)
    {
        foreach (var process in Process.GetProcessesByName(appName))
        {
            using (process)
            {
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero) continue;
                if (IsIconic(window)) ShowWindow(window, 9 /* SW_RESTORE */);
                SetForegroundWindow(window);
                return;
            }
        }
    }

    private const double CoverWait = 0.6, TransitionDuration = 0.35; // seconds

    private double TransitionProgress => Math.Clamp((Now - _transitionStart) / TransitionDuration, 0, 1);

    private static string Key(NowPlaying track) => $"{track.AppId}|{track.Title}|{track.Artist}";

    private void Hide()
    {
        SetVisible(false);
        StopAnimation();
        _shown = _previous = null;
        _shownCover = _previousCover = null;
        _pendingKey = null;
    }

    /// <summary>
    /// Re-reads state and placement, then shows, hides or redraws the widget. <paramref name="refresh"/> is
    /// the periodic check for taskbar changes: it redraws only when the placement or the taskbar theme changed.
    /// </summary>
    private void Update(bool refresh = false)
    {
        bool enabled;
        bool hideInFullscreen;
        NowPlaying? track;
        CanvasBitmap? cover;
        byte[]? coverFor;
        lock (_gate)
        {
            enabled = _settings.Enabled;
            hideInFullscreen = _settings.HideInFullscreen;
            track = _track;
            cover = _cover;
            coverFor = _coverFor;
        }
        if (!enabled || track == null || !Attach() || (hideInFullscreen && FullscreenInFront()))
        {
            Hide();
            return;
        }

        // The cover only counts once it has been decoded for this very track.
        bool coverReady = track.Thumbnail != null && ReferenceEquals(coverFor, track.Thumbnail);
        string key = Key(track);
        if (_shown == null || !_visible)
        {
            _shown = track;
            _shownCover = coverReady ? cover : null;
            _pendingKey = null;
        }
        else if (Key(_shown) == key)
        {
            _shown = track; // same track: play state, controls or a late cover
            if (coverReady) _shownCover = cover;
        }
        else
        {
            // A new track: hold the old one until the new cover is decoded (briefly), then slide.
            if (_pendingKey != key)
            {
                _pendingKey = key;
                _pendingSince = Now;
            }
            if (!coverReady && Now - _pendingSince < CoverWait)
            {
                SetTimer(_hwnd, (IntPtr)PendingTimer, 60, IntPtr.Zero);
                return;
            }
            _pendingKey = null;
            _previous = _shown;
            _previousCover = _shownCover;
            _shown = track;
            _shownCover = coverReady ? cover : null;
            _transitionStart = Now;
            _direction = Now - _skipTime < 3 ? _skipDirection : 1; // a skip from our buttons sets the side
        }

        if (!Place())
        {
            Hide();
            return;
        }
        if (key != _marqueeKey)
        {
            _marqueeKey = key; // new track: the marquee starts over (with its pause)
            _marqueeStart = Now;
            _marqueeResting = true;
        }
        if (!_visible)
        {
            // Appear in the final state rather than fading from the previous one.
            _hoverLevel = HoverTarget();
            _dimLevel = DimTarget();
        }
        var drawn = (_placement, _light);
        if (refresh && _visible && drawn == _drawn && TransitionProgress >= 1) return; // nothing moved: an animation, if any, keeps drawing
        _drawn = drawn;
        Draw(dump: true);
        SetVisible(true);
        StartAnimation();
    }

    /// <summary>Makes the window a child of the (current) taskbar. False when there is no taskbar.</summary>
    private bool Attach()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        if (taskbar == _parent && IsWindow(_parent)) return true;

        // New taskbar (first run, or Explorer restarted): reparent.
        long style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)((style & ~WS_POPUP) | WS_CHILD));
        SetParent(_hwnd, taskbar);
        _parent = taskbar;
        _visible = false;
        return true;
    }

    /// <summary>Lays the widget out for the current track and taskbar and moves it there. False when it doesn't fit.</summary>
    private bool Place()
    {
        NowPlaying? track;
        TaskbarWidgetSettings settings;
        lock (_gate)
        {
            track = _shown;
            settings = _settings;
        }
        if (track == null || _parent == IntPtr.Zero || !GetWindowRect(_parent, out var bar)) return false;

        int barWidth = bar.Right - bar.Left, barHeight = bar.Bottom - bar.Top;
        if (barWidth <= barHeight) return false; // vertical taskbars aren't supported
        double scale = GetDpiForWindow(_parent) / 96.0;
        int gap = (int)Math.Round(8 * scale);

        // The free spans: left end → app icons → tray. A left-aligned taskbar has Start and the
        // icons at its left end, so the left-side positions fall back to just after the icons.
        var icons = FindChildRect("ReBarWindow32", bar);
        int iconsLeft = icons?.Left ?? barWidth / 2, iconsRight = icons?.Right ?? barWidth / 2;
        int leftEnd = gap + (WidgetsButtonShown() ? (int)Math.Round(216 * scale) : 0); // after the weather widget
        int trayLeft = (FindChildRect("TrayNotifyWnd", bar)?.Left ?? barWidth) - gap / 2;
        var position = settings.Position;
        if (!TaskbarIsCentered() && position is WidgetPosition.Left or WidgetPosition.BeforeIcons) position = WidgetPosition.AfterIcons;

        bool leftSide = position is WidgetPosition.Left or WidgetPosition.BeforeIcons;
        int start = leftSide ? leftEnd : iconsRight + gap;
        int end = leftSide ? iconsLeft - gap : trayLeft;
        if (end - start < 120 * scale) return false; // no room

        _view.Layout(track, settings, scale, barHeight, end - start);
        bool alignStart = position is WidgetPosition.Left or WidgetPosition.AfterIcons;
        int left = (alignStart ? start : end - _view.PixelWidth) + (int)Math.Round(settings.Offset * scale);
        left = Math.Clamp(left, 0, Math.Max(0, barWidth - _view.PixelWidth));
        _light = IsLightTaskbar();

        _scale = scale;
        _placement = (left, _view.PixelWidth, _view.PixelHeight);
        SetWindowPos(_hwnd, HWND_TOP, left, 0, _view.PixelWidth, _view.PixelHeight, SWP_NOACTIVATE);
        return true;
    }

    /// <summary>Redraws the current layout with the current animation state.</summary>
    private void Draw(bool dump = false)
    {
        NowPlaying? track;
        CanvasBitmap? cover;
        TaskbarWidgetSettings settings;
        lock (_gate)
        {
            track = _shown;
            cover = _shownCover;
            settings = _settings;
        }
        if (track == null || _view.PixelWidth == 0) return;

        double t = Now - _marqueeStart;
        bool scrolling = !_marqueeResting && MarqueeActive(track, settings);
        var visual = new WidgetVisual(
            _hoverLevel,
            _dimLevel,
            scrolling ? Scroll(_view.TitleWidth, t) : 0,
            scrolling && _view.ShowsArtist ? Scroll(_view.ArtistWidth, t) : 0,
            _hot,
            _pressed,
            TransitionProgress,
            _direction);

        var previous = TransitionProgress < 1 ? _previous : null;
        var pixels = _view.Render(track, cover, previous, _previousCover, settings, _light, visual);
        Present(pixels, _view.PixelWidth, _view.PixelHeight);
#if DEBUG
        if (dump) DumpFrame(pixels, _view.PixelWidth, _view.PixelHeight);
#endif
    }

    // ---- animation ----------------------------------------------------------------------------------
    // A 15 ms timer runs only while something moves: hover fade, paused dimming, a scrolling title.

    private const double MarqueeWait = 2.0, MarqueeSpeed = 30; // seconds at rest, then DIPs per second

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private double HoverTarget() => _hovered ? 1 : 0;

    private double DimTarget()
    {
        lock (_gate)
            return _track is { IsPlaying: false } && !_hovered ? Math.Clamp(_settings.PausedDim, 0, 100) / 100.0 : 0;
    }

    private bool MarqueeActive(NowPlaying track, TaskbarWidgetSettings settings) =>
        settings.ScrollTitles
        && (_view.TitleWidth > _view.TextWidth + 0.5f || (_view.ShowsArtist && _view.ArtistWidth > _view.TextWidth + 0.5f));

    /// <summary>
    /// Whether the marquee moves on this frame: while playing; on pause it finishes the pass it is in
    /// (the loop ends exactly where it started) and then rests at the start until playback resumes.
    /// </summary>
    private bool MarqueeRunning(NowPlaying track, TaskbarWidgetSettings settings, double now)
    {
        if (!MarqueeActive(track, settings)) return false;
        if (track.IsPlaying) return true;
        if (_marqueeResting) return false;
        double t = now - _marqueeStart;
        return Scroll(_view.TitleWidth, t) > 0 || (_view.ShowsArtist && Scroll(_view.ArtistWidth, t) > 0);
    }

    /// <summary>Marquee offset: rest at the start, scroll one full length (plus the gap), repeat.</summary>
    private float Scroll(float width, double t)
    {
        if (width <= _view.TextWidth + 0.5f) return 0;
        double distance = width + TaskbarWidgetView.MarqueeGap;
        double cycle = MarqueeWait + distance / MarqueeSpeed;
        double phase = t % cycle;
        return phase < MarqueeWait ? 0 : (float)((phase - MarqueeWait) * MarqueeSpeed);
    }

    /// <summary>Starts the frame timer, or brings it back to full rate for something new to animate.</summary>
    private void StartAnimation()
    {
        if (!_visible) return;
        if (!_animating)
        {
            _animating = true;
            _lastTick = Now;
            _frameMs = 0;
        }
        SetFrameInterval(FrameMs);
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        KillTimer(_hwnd, (IntPtr)AnimationTimer);
    }

    // Frame pacing: ~60 fps while anything moves (a slower marquee rate stutters: timer ticks come in
    // 15.6 ms steps); nothing is drawn while an auto-hidden taskbar is off screen, only checked for coming back.
    private const uint FrameMs = 15, HiddenPollMs = 200;

    private void SetFrameInterval(uint ms)
    {
        if (ms == _frameMs) return;
        _frameMs = ms;
        SetTimer(_hwnd, (IntPtr)AnimationTimer, ms, IntPtr.Zero);
    }

    private void Tick()
    {
        double now = Now, dt = Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        if (!TaskbarOnScreen())
        {
            SetFrameInterval(HiddenPollMs);
            return;
        }
        bool settled = Ease(ref _hoverLevel, HoverTarget(), dt, 0.05) & Ease(ref _dimLevel, DimTarget(), dt, 0.09);

        NowPlaying? track;
        TaskbarWidgetSettings settings;
        lock (_gate)
        {
            track = _shown;
            settings = _settings;
        }
        bool scrolling = track != null && MarqueeRunning(track, settings, now);
        if (scrolling && _marqueeResting) _marqueeStart = now; // starts over, with its pause
        _marqueeResting = !scrolling;

        bool sliding = TransitionProgress < 1;
        Draw();
        if (!sliding) { _previous = null; _previousCover = null; }
        if (settled && !scrolling && !sliding) StopAnimation();
        else SetFrameInterval(FrameMs);
    }

    /// <summary>False while an auto-hidden taskbar is slid off its monitor (a sliver of a few px stays).</summary>
    private bool TaskbarOnScreen()
    {
        if (_parent == IntPtr.Zero || !GetWindowRect(_parent, out var bar)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(_parent, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)) return true;
        int visible = Math.Min(bar.Bottom, info.rcMonitor.Bottom) - Math.Max(bar.Top, info.rcMonitor.Top);
        return visible > 4 * _scale;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            Update(refresh: true);
        }
        catch (Exception ex)
        {
            Omnisound.Services.CrashLog.Write("taskbar widget", ex);
        }
    }

    /// <summary>
    /// A fullscreen window (video, game, F11 browser) is in front on the taskbar's monitor: it covers the
    /// whole monitor and has no caption. Maximized windows keep theirs, which tells them apart even when
    /// an auto-hidden taskbar lets them cover the whole monitor too.
    /// </summary>
    private bool FullscreenInFront()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow() || !GetWindowRect(window, out var r)) return false;
        var name = new System.Text.StringBuilder(32);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false; // the desktop

        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != MonitorFromWindow(_parent, 2)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var m = info.rcMonitor;
        if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return false;

        const long WS_CAPTION = 0x00C00000L;
        return (GetWindowLongPtr(window, -16 /* GWL_STYLE */).ToInt64() & WS_CAPTION) != WS_CAPTION;
    }

    /// <summary>Exponential ease toward <paramref name="target"/>; true once there.</summary>
    private static bool Ease(ref double value, double target, double dt, double tau)
    {
        value += (target - value) * (1 - Math.Exp(-dt / tau));
        if (Math.Abs(target - value) > 0.004) return false;
        value = target;
        return true;
    }

    private void SetVisible(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;
        ShowWindow(_hwnd, visible ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);
        if (!visible) { _hovered = false; _hot = WidgetPart.None; }
    }

    /// <summary>Hands the premultiplied pixels to the layered window.</summary>
    private void Present(byte[] pixels, int width, int height)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            Marshal.Copy(pixels, 0, bits, Math.Min(pixels.Length, width * height * 4));
            var size = new SIZE { cx = width, cy = height };
            var source = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(_hwnd, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

#if DEBUG
    /// <summary>Debug aid: the last frame as %TEMP%\omnisound-widget.png (the taskbar may be hidden).</summary>
    private static void DumpFrame(byte[] pixels, int width, int height)
    {
        using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        Marshal.Copy(pixels, 0, data.Scan0, Math.Min(pixels.Length, width * height * 4));
        bmp.UnlockBits(data);
        bmp.Save(Path.Combine(Path.GetTempPath(), "omnisound-widget.png"));
    }
#endif

    /// <summary>A taskbar child's rect relative to the taskbar.</summary>
    private RECT? FindChildRect(string className, RECT bar)
    {
        var child = FindWindowEx(_parent, IntPtr.Zero, className, null);
        if (child == IntPtr.Zero || !GetWindowRect(child, out var r) || r.Right <= r.Left) return null;
        return new RECT { Left = r.Left - bar.Left, Top = r.Top - bar.Top, Right = r.Right - bar.Left, Bottom = r.Bottom - bar.Top };
    }

    private static bool TaskbarIsCentered() => ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1) != 0;

    private static bool WidgetsButtonShown() => ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0) != 0;

    /// <summary>The taskbar follows the Windows (not app) theme.</summary>
    private static bool IsLightTaskbar() => ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 0;

    private static int ReadDword(string key, string name, int fallback)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) is int value ? value : fallback;
    }

    private static int LoX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int HiY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    public void Dispose() => Post(WM_APP_QUIT);

    // ---- Win32 -------------------------------------------------------------------------------------

    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000L, WS_CHILD = 0x40000000L;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_MOUSELEAVE = 0x02A3, WM_MOUSEACTIVATE = 0x0021;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

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
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

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
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
