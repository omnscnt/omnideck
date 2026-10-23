using System.Numerics;
using Omnideck.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Omnideck.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT;
using WinRT.Interop;

namespace Omnideck.Views;

/// <summary>Borderless acrylic flyout that pops up next to the tray, Windows 11 quick-settings style.</summary>
public sealed partial class FlyoutWindow : Window
{
    // Clipped (see EnterClipped), the window is held at the wide width and a region shows the part in use.
    private double WidthDip => _clipped ? ViewModel.WideWindowWidth : ViewModel.WindowWidth;
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
        // Mid-widening the frame callback sizes the window itself, in step with the content.
        ViewModel.PropertyChanged += (_, _) => { if (!_expanding) QueueResize(); };
        ViewModel.ExpandToggled += StartExpand;

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
        ViewModel.RefreshRouting();
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
            ExitClipped(); // it opens at its own width next time
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

    // ---- widening: window and output buttons grow together ------------------------------------------

    private const double ExpandMs = 240;
    private readonly System.Diagnostics.Stopwatch _expandClock = new();
    private double _expandFrom;
    private bool _expanding;

    /// <summary>
    /// Eases the mixer between narrow and wide on composition frames (like the slide), sizing the
    /// window in the same frame as the content, so the two never drift apart. Closed, it just jumps.
    /// </summary>
    private void StartExpand()
    {
        double target = ViewModel.IsExpanded ? 1 : 0;
        if (!_isOpen)
        {
            ViewModel.ExpandProgress = target;
            return;
        }
        _expandFrom = ViewModel.ExpandProgress;
        _expandClock.Restart();
        if (_anchor.Edge != TaskbarEdge.Left) EnterClipped(); // a left taskbar: the window grows rightward, its left edge stays
        if (_expanding) return;
        _expanding = true;
        CompositionTarget.Rendering += OnExpandFrame;
    }

    private void OnExpandFrame(object? sender, object? e)
    {
        double t = Math.Clamp(_expandClock.Elapsed.TotalMilliseconds / ExpandMs, 0, 1);
        double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease-in-out cubic
        double target = ViewModel.IsExpanded ? 1 : 0;
        ViewModel.ExpandProgress = _expandFrom + (target - _expandFrom) * eased;
        if (_clipped) RootGrid.Width = ViewModel.WindowWidth;
        UpdateBounds();
        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnExpandFrame;
            _expanding = false;
            if (ViewModel.IsExpanded) ExitClipped(); // the window is wide already: just drop the region
        }
    }

    // ---- clipped: the window stays wide, a region shows the part in use ----------------------------

    // Windows draws a window's last frame from its left edge: when the left edge moves (the mixer grows
    // leftward from the tray), everything jumps sideways for a frame until XAML catches up, the right edge
    // most of all. So while widening, the window keeps its wide size and position, and a region cuts it
    // to the width in use; the content is laid out right-aligned at that width. Only a region's corners
    // are plain (no system rounding, border or shadow), so it lasts only while needed: until widened
    // fully, or after narrowing until the mixer closes (narrowing back would move the left edge again).
    private bool _clipped;

    private void EnterClipped()
    {
        if (_clipped) return;
        _clipped = true;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Right;
        RootGrid.Width = ViewModel.WindowWidth;
        UpdateBounds(); // widening from narrow: the one move of the left edge, with the region already on
        PaintGrowBackground();
    }

    private void ExitClipped()
    {
        if (!_clipped) return;
        _clipped = false;
        SetWindowRgn(_hwnd, IntPtr.Zero, true);
        RootGrid.ClearValue(FrameworkElement.WidthProperty);
        RootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (_isOpen) UpdateBounds();
    }

    /// <summary>The region: the right part of the window, as wide as the mixer is now, with rounded corners.</summary>
    private void ClipTo(int width, int height, double scale, int nonClient)
    {
        int visible = Math.Min(width, (int)Math.Round(ViewModel.WindowWidth * scale) + nonClient);
        int corner = (int)Math.Round(16 * scale); // the ellipse's size: an 8 DIP radius, like the system's
        var region = CreateRoundRectRgn(width - visible, 0, width + 1, height + 1, corner, corner);
        SetWindowRgn(_hwnd, region, true); // the window owns the region from here
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

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

    /// <summary>
    /// The window's own (GDI) surface sits under XAML and is what shows in a strip the window just grew
    /// by, until XAML draws there a frame later: black unless painted. Painted a grey close to the
    /// acrylic's right after each resize, the strip all but disappears.
    /// </summary>
    private void PaintGrowBackground()
    {
        if (!GetClientRect(_hwnd, out var rect)) return;
        var dc = GetDC(_hwnd);
        if (dc == IntPtr.Zero) return;
        var brush = CreateSolidBrush(RootGrid.ActualTheme == ElementTheme.Light ? 0xEEEEEEu : 0x282828u); // 0x00BBGGRR, grey
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
        ReleaseDC(_hwnd, dc);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

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
        Card.Measure(new Size(ViewModel.WindowWidth, double.PositiveInfinity));
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
        if (_clipped) ClipTo(width, height, scale, frame.Width - client.Width);
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

    // ---- device pickers ----------------------------------------------------------------------------

    /// <summary>The default output for everything. Devices are read on every open, so plugged and unplugged ones are current.</summary>
    private void OnOutputClick(object sender, RoutedEventArgs e)
    {
        var devices = ViewModel.GetOutputDevices();
        var choices = devices.Select(d => (d.Name, (string?)d.Id, d.IsDefault)).ToList();
        ShowDevicePicker((FrameworkElement)sender, choices, id => { if (id != null) ViewModel.SelectOutput(id); });
    }

    /// <summary>
    /// One app's output: follow the default, or a device of its own (Windows remembers it for the app).
    /// </summary>
    private void OnAppOutputClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppVolumeViewModel app || !MixerViewModel.CanRouteApps) return;
        var devices = ViewModel.GetOutputDevices();
        string? current = ViewModel.GetAppOutput(app);
        if (current != null && !devices.Any(d => SameId(d.Id, current)))
            current = null; // sent to a device that is gone: it plays on the default
        var choices = new List<(string, string?, bool)> { ("Default", null, current == null) };
        choices.AddRange(devices.Select(d => (d.Name, (string?)d.Id, current != null && SameId(d.Id, current))));
        ShowDevicePicker((FrameworkElement)sender, choices, id => ViewModel.SetAppOutput(app, id));
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // An icon button's picker still needs room for names.
    private const double MinPickerWidth = 180;

    /// <summary>
    /// A compact list under <paramref name="target"/>, as wide as it (at least <see cref="MinPickerWidth"/>):
    /// a check by the current choice, names cut with an ellipsis (whole in the tooltip). In-window, so the
    /// mixer keeps the activation and stays open.
    /// </summary>
    private void ShowDevicePicker(FrameworkElement target, IReadOnlyList<(string Name, string? Id, bool IsCurrent)> choices, Action<string?> pick)
    {
        // Full names (a setting): as wide as they need, at least the button; short ones fit the button.
        double width = Math.Max(target.ActualWidth, MinPickerWidth) - 8; // minus the presenter's padding and border
        var list = ViewModel.FullNames ? new StackPanel { MinWidth = width } : new StackPanel { Width = width };
        var flyout = new Flyout
        {
            Content = list,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = (Style)RootGrid.Resources["PickerPresenterStyle"],
        };
        var itemStyle = (Style)RootGrid.Resources["PickerItemStyle"];
        // "Headphones (CS43131 HIFI Audio)" lists as "Headphones", unless two devices would then read the same.
        var shortNames = choices.Select(c => MixerLayout.ShortName(c.Name)).ToList();
        foreach (var ((fullName, id, isCurrent), i) in choices.Select((c, i) => (c, i)))
        {
            string name = ViewModel.FullNames || shortNames.Count(n => n == shortNames[i]) > 1 ? fullName : shortNames[i];
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (isCurrent) row.Children.Add(new FontIcon { FontSize = 10, Glyph = "", VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var item = new Button { Content = row, Style = itemStyle };
            ToolTipService.SetToolTip(item, fullName);
            item.Click += (_, _) =>
            {
                flyout.Hide();
                pick(id);
            };
            list.Children.Add(item);
        }
        if (choices.Count == 0)
            list.Children.Add(new TextBlock { Text = "No output devices", FontSize = 12, Margin = new Thickness(8, 6, 8, 6), Opacity = 0.6 });
        flyout.ShowAt(target);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Instance.ShowSettings();
    }
}
