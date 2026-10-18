using System.Numerics;
using Omnisound.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Omnisound.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT;
using WinRT.Interop;

namespace Omnisound.Views;

/// <summary>Borderless acrylic flyout that pops up next to the tray, Windows 11 quick-settings style.</summary>
public sealed partial class FlyoutWindow : Window
{
    private const double WidthDip = 360;
    private const double MarginDip = 12;
    private const double MaxAppsHeightDip = 540;

    private readonly IntPtr _hwnd;
    private bool _isOpen;
    private DateTime _lastHidden = DateTime.MinValue;
    private TaskbarAnchor _anchor;

    public MixerViewModel ViewModel { get; }

    public FlyoutWindow(MixerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        SetupBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false); // Keeps the Win11 rounded corners + shadow, drops the caption.
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        presenter.IsAlwaysOnTop = true; // only takes effect once the presenter is attached
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true; // Alt+F4 just hides; the app lives in the tray.
            Hide();
        };

        Activated += OnActivated;
        ViewModel.Apps.CollectionChanged += (_, _) => QueueResize();
        ViewModel.PropertyChanged += (_, _) => QueueResize();

        // No DWM show/hide transition: the slide is the animation, and a fade after it reads as a glitch.
        int disabled = 1;
        DwmSetWindowAttribute(_hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref disabled, sizeof(int));

        _taskbarWatch = DispatcherQueue.CreateTimer();
        _taskbarWatch.Interval = TimeSpan.FromMilliseconds(15);
        _taskbarWatch.Tick += (_, _) =>
        {
            CheckFocus();
            FollowTaskbar();
        };
    }

    public bool IsOpen => _isOpen;

    public void Toggle()
    {
        if (_isOpen) Hide();
        // A tray click first deactivates (and hides) us; don't let the same click reopen the flyout.
        else if (DateTime.UtcNow - _lastHidden > TimeSpan.FromMilliseconds(300)) Show();
    }

    public void Show()
    {
        _anchor = TaskbarPlacement.Capture();
        UpdateBounds();

        // Start pushed toward the taskbar, then slide the whole window (content and acrylic) out.
        // Cloaked until XAML has presented a frame, so a fresh window never flashes black.
        AppWindow.Move(Offset(_target.X, _target.Y, 1.0));
        SetCloaked(true);
        AppWindow.Show(true);
        if (!_isOpen) _previousForeground = TaskbarPlacement.ForegroundWindow;
        _focusAttempts = 0;
        Activate();
        _hasFocus = TaskbarPlacement.BringToFront(_hwnd);
        _isOpen = true;
        AppsScroller.ChangeView(null, 0, null, true);
        QueueResize(); // Item templates realize on the first layout pass; re-measure once they exist.
        ViewModel.StartMetering();
        _taskbarWatch.Start();

        StartSlide(entering: true, ShowDuration, onDone: null);
    }

    /// <summary>
    /// Shows the window once in place but DWM-cloaked (invisible, yet laid out and rendered — unlike an
    /// off-screen window), then hides it. Without this the very first open shows a couple of black frames
    /// while the swap chain and acrylic come up.
    /// </summary>
    public void Prewarm()
    {
        _anchor = TaskbarPlacement.Capture();
        UpdateBounds();
        SetCloaked(true);
        AppWindow.Show(false);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        void OnFrame(object? sender, object e)
        {
            if (++frames < 10 || clock.ElapsedMilliseconds < 300) return;
            CompositionTarget.Rendering -= OnFrame;
            if (_isOpen) return; // the user opened it meanwhile; Show() owns the cloak now
            AppWindow.Hide();
            SetCloaked(false);
        }
        CompositionTarget.Rendering += OnFrame;
    }

    public void Hide()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _hasFocus = false;
        _lastHidden = DateTime.UtcNow;
        ViewModel.StopMetering();

        StartSlide(entering: false, HideDuration, onDone: () =>
        {
            if (_isOpen) return;
            _taskbarWatch.Stop();
            // Cloaked first: the last position still leaves the top edge and shadow on screen (with an
            // auto-hidden taskbar nothing covers them) for the frame or two that Hide takes. Show re-cloaks anyway.
            SetCloaked(true);
            AppWindow.Hide();
        });
    }

    // ---- auto-hidden taskbar -------------------------------------------------------------------

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _taskbarWatch;

    /// <summary>
    /// An auto-hidden taskbar slides in and out while the flyout is open: keep the flyout just above it,
    /// or down at the screen edge while it is hidden. Polled, as nothing signals the taskbar moving.
    /// </summary>
    private void FollowTaskbar()
    {
        var anchor = TaskbarPlacement.Refresh(_anchor);
        if (anchor == _anchor) return;
        _anchor = anchor;
        UpdateBounds();
    }

    // ---- window slide ------------------------------------------------------------------------

    // The window travels its full size plus the margin: it starts (and ends) completely behind the
    // taskbar, and sits just under it in z-order so the taskbar hides the part not yet slid out.
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(220);

    private RectInt32 _target;
    private readonly System.Diagnostics.Stopwatch _slideClock = new();
    private TimeSpan _slideDuration;
    private bool _slideEntering;
    private Action? _slideDone;
    private bool _sliding;

    /// <summary>
    /// Moves the real window every composition frame. The whole flyout — acrylic included — slides
    /// out from the taskbar side as one piece, like the shell's own flyouts.
    /// </summary>
    private void StartSlide(bool entering, TimeSpan duration, Action? onDone)
    {
        _slideEntering = entering;
        _slideDuration = duration;
        _slideDone = onDone;
        // The clock starts on the first rendered frame, not now: right after Show() the first frame
        // can take longer than the whole animation (layout, backdrop), which would skip the slide.
        _slideClock.Reset();
        if (!_sliding)
        {
            _sliding = true;
            CompositionTarget.Rendering += OnSlideFrame;
        }
    }

    private void OnSlideFrame(object? sender, object? e)
    {
        if (_cloaked)
        {
            // Rendering fires before a frame is drawn and DWM puts it on screen later still (and on a
            // high-refresh display callbacks come every few ms): wait for frames *and* time.
            if (++_cloakedFrames < 2 || _cloakClock.ElapsedMilliseconds < 50) return;
            SetCloaked(false);
        }
        if (!_slideClock.IsRunning)
        {
            _slideClock.Start();
            // Activation lifts us to the top of the always-on-top band; drop back just under the taskbar.
            TaskbarPlacement.PlaceBelowTaskbar(_hwnd);
        }
        double t = Math.Clamp(_slideClock.Elapsed.TotalMilliseconds / _slideDuration.TotalMilliseconds, 0, 1);
        // Entering: strong decelerate (ease-out quart), like the shell. Leaving: accelerate (ease-in cubic).
        double progress = _slideEntering ? 1 - Math.Pow(1 - t, 4) : t * t * t;
        double away = _slideEntering ? 1 - progress : progress; // 1 = fully pushed toward the taskbar

        AppWindow.Move(Offset(_target.X, _target.Y, away));

        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnSlideFrame;
            _sliding = false;
            var done = _slideDone;
            _slideDone = null;
            done?.Invoke();
        }
    }

    private bool _cloaked;
    private int _cloakedFrames;
    private readonly System.Diagnostics.Stopwatch _cloakClock = new();

    private void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
        _cloaked = cloaked;
        _cloakedFrames = 0;
        if (cloaked) _cloakClock.Restart();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Window position pushed toward the taskbar by <paramref name="away"/> (0..1) of the slide distance:
    /// at 1 the window is entirely past the work-area edge, i.e. behind the taskbar.
    /// </summary>
    private PointInt32 Offset(int x, int y, double away)
    {
        int margin = (int)Math.Round(MarginDip * _anchor.Scale);
        int travel = _anchor.Edge is TaskbarEdge.Left or TaskbarEdge.Right ? _target.Width + margin : _target.Height + margin;
        int d = (int)Math.Round(travel * away);
        return _anchor.Edge switch
        {
            TaskbarEdge.Top => new PointInt32(x, y - d),
            TaskbarEdge.Left => new PointInt32(x - d, y),
            TaskbarEdge.Right => new PointInt32(x + d, y),
            _ => new PointInt32(x, y + d),
        };
    }

    // ---- backdrop ----------------------------------------------------------------------------

    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    /// <summary>
    /// Base acrylic — the same material as the Windows 11 quick-settings flyout. Kept "input active"
    /// so it doesn't fall back to a solid fill while the hide animation plays after deactivation.
    /// </summary>
    private void SetupBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            return;
        }

        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);

        RootGrid.ActualThemeChanged += (_, _) => SyncBackdropTheme();
        SyncBackdropTheme();
        Closed += (_, _) => _acrylic?.Dispose();
    }

    private void SyncBackdropTheme()
    {
        if (_backdropConfig == null) return;
        _backdropConfig.Theme = RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }

    // ---- placement ---------------------------------------------------------------------------

    private void QueueResize()
    {
        if (!_isOpen) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateBounds);
    }

    private void UpdateBounds()
    {
        var anchor = _anchor;
        double scale = anchor.Scale;
        double workHeightDip = anchor.WorkArea.Height / scale;

        AppsScroller.MaxHeight = Math.Max(120, Math.Min(MaxAppsHeightDip, workHeightDip - 260));
        Card.Measure(new Size(WidthDip, double.PositiveInfinity));
        double heightDip = Math.Min(Card.DesiredSize.Height, workHeightDip - 2 * MarginDip);

        // Size the client area, not the frame: the border-only presenter still reserves a few
        // non-client pixels, which otherwise get cut from the bottom of the content.
        var frame = AppWindow.Size;
        var client = AppWindow.ClientSize;
        int width = (int)Math.Round(WidthDip * scale) + (frame.Width - client.Width);
        int height = (int)Math.Ceiling(heightDip * scale) + (frame.Height - client.Height);
        int margin = (int)Math.Round(MarginDip * scale);

        int x = anchor.Edge == TaskbarEdge.Left ? anchor.WorkArea.X + margin : anchor.Right - margin - width;
        int y = anchor.Edge == TaskbarEdge.Top ? anchor.WorkArea.Y + margin : anchor.Bottom - margin - height;

        _target = new RectInt32(x, y, width, height);
        // Mid-slide the frame callback owns the position; only take the new size.
        if (_sliding) AppWindow.Resize(new SizeInt32(width, height));
        else AppWindow.MoveAndResize(_target);
    }

    // ---- events ------------------------------------------------------------------------------

    // Focus: closing on deactivation only counts once the flyout really had the foreground. A refused
    // SetForegroundWindow can still send activate + deactivate, which used to close it right after opening.
    private bool _hasFocus;
    private IntPtr _previousForeground;
    private int _focusAttempts;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            _hasFocus |= TaskbarPlacement.ForegroundWindow == _hwnd;
        else if (_hasFocus) Hide();
    }

    /// <summary>
    /// While the flyout is open without the foreground: keep asking for it for a moment, and close
    /// once the user moves on to another window (no deactivation will tell us then).
    /// </summary>
    private void CheckFocus()
    {
        if (!_isOpen || _hasFocus) return;
        var foreground = TaskbarPlacement.ForegroundWindow;
        if (foreground == _hwnd) _hasFocus = true;
        else if (foreground != _previousForeground && foreground != IntPtr.Zero) Hide();
        else if (++_focusAttempts % 6 == 0 && _focusAttempts <= 60) _hasFocus = TaskbarPlacement.BringToFront(_hwnd);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Hide();
    }

    /// <summary>Rebuilt on every open so plugged/unplugged devices are always current.</summary>
    private void OnOutputMenuOpening(object sender, object e)
    {
        OutputMenu.Items.Clear();
        foreach (var device in ViewModel.GetOutputDevices())
        {
            var item = new RadioMenuFlyoutItem { Text = device.Name, IsChecked = device.IsDefault, GroupName = "outputs" };
            var id = device.Id;
            item.Click += (_, _) => ViewModel.SelectOutput(id);
            OutputMenu.Items.Add(item);
        }
        if (OutputMenu.Items.Count == 0)
            OutputMenu.Items.Add(new MenuFlyoutItem { Text = "No output devices", IsEnabled = false });
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Instance.ShowSettings();
    }
}
