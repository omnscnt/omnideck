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
    private double WidthDip => ViewModel.WindowWidth;
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
        _panel = new BatteryPanelWindow(viewModel);
        ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MixerViewModel.BatteriesVisibility)) OnBatteriesVisibilityChanged(); };

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
        _panelShown = PanelWanted;
        UpdateBounds();

        // Start pushed toward the taskbar, then slide the whole window (content and acrylic) out.
        // Cloaked until XAML has presented a frame, so a fresh window never flashes black.
        AppWindow.Move(Offset(_target.X, _target.Y, 1.0));
        SetCloaked(true);
        if (_panelShown)
        {
            MovePanel(1.0);
            _panel.SetCloaked(true);
            _panel.ShowQuietly();
        }
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
        App.Instance.RefreshBatteries();
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
        _panel.Place(_panelTarget); // the plaque warms up the same way (it may be needed on the first open)
        _panel.SetCloaked(true);
        _panel.ShowQuietly();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        void OnFrame(object? sender, object e)
        {
            if (++frames < 10 || clock.ElapsedMilliseconds < 300) return;
            CompositionTarget.Rendering -= OnFrame;
            if (_isOpen) return; // the user opened it meanwhile; Show() owns the cloak now
            _panel.HideQuietly();
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
            _panel.SetCloaked(true);
            _panel.HideQuietly();
            _panelShown = false;
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
        if (_followingFrames) return; // the frame callback has it
        var anchor = TaskbarPlacement.Refresh(_anchor);
        if (anchor == _anchor) return;
        _anchor = anchor;
        UpdateBounds();
        // It's moving: follow on every composition frame (the timer's 15 ms is fewer frames than the
        // screen shows), until it has sat still for a moment.
        _followingFrames = true;
        _stillSince.Restart();
        CompositionTarget.Rendering += OnFollowFrame;
    }

    private bool _followingFrames;
    private readonly System.Diagnostics.Stopwatch _stillSince = new();

    private void OnFollowFrame(object? sender, object? e)
    {
        var anchor = TaskbarPlacement.Refresh(_anchor);
        if (anchor != _anchor && _isOpen)
        {
            _anchor = anchor;
            UpdateBounds();
            _stillSince.Restart();
        }
        else if (_stillSince.ElapsedMilliseconds > 250 || !_isOpen)
        {
            CompositionTarget.Rendering -= OnFollowFrame;
            _followingFrames = false;
        }
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
            if (_panelShown) _panel.SetCloaked(false);
        }
        if (!_slideClock.IsRunning)
        {
            _slideClock.Start();
            // Activation lifts us to the top of the always-on-top band; drop back just under the taskbar.
            TaskbarPlacement.PlaceBelowTaskbar(_hwnd);
            if (_panelShown) StackPanelOverMixer();
        }
        double t = Math.Clamp(_slideClock.Elapsed.TotalMilliseconds / _slideDuration.TotalMilliseconds, 0, 1);
        // Entering: strong decelerate (ease-out quart), like the shell. Leaving: accelerate (ease-in cubic).
        double progress = _slideEntering ? 1 - Math.Pow(1 - t, 4) : t * t * t;
        double away = _slideEntering ? 1 - progress : progress; // 1 = fully pushed toward the taskbar

        AppWindow.Move(Offset(_target.X, _target.Y, away));
        MovePanel(away);

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
        UpdateBounds();
        PaintGrowBackground();
        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnExpandFrame;
            _expanding = false;
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
        // Up from (or down to) the taskbar the plaque travels too: the slide covers it, so it hides behind the taskbar as well.
        int panel = _panelShown && _anchor.Edge is TaskbarEdge.Top or TaskbarEdge.Bottom ? _panelTarget.Height + margin : 0;
        int travel = _anchor.Edge is TaskbarEdge.Left or TaskbarEdge.Right ? _target.Width + margin : _target.Height + margin + panel;
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
        Card.Measure(new Size(WidthDip, double.PositiveInfinity));
        double panelDip = _panelShown ? BatteryPanelWindow.HeightDip + MarginDip : 0; // the plaque above takes its share
        double heightDip = Math.Min(Card.DesiredSize.Height, workHeightDip - 2 * MarginDip - panelDip);

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
        // The plaque: as wide as the mixer, a margin away on the side away from the taskbar.
        var panelFrame = _panel.AppWindow.Size;
        var panelClient = _panel.AppWindow.ClientSize;
        int panelHeight = (int)Math.Ceiling(BatteryPanelWindow.HeightDip * scale) + (panelFrame.Height - panelClient.Height);
        int panelWidth = width - (frame.Width - client.Width) + (panelFrame.Width - panelClient.Width);
        int panelY = anchor.Edge == TaskbarEdge.Top ? y + height + margin : y - margin - panelHeight;
        _panelTarget = new RectInt32(x, panelY, panelWidth, panelHeight);
        // Mid-slide the frame callback owns the position; only take the new size.
        if (_sliding)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            if (_panelShown) _panel.AppWindow.Resize(new SizeInt32(panelWidth, panelHeight));
        }
        else
        {
            AppWindow.MoveAndResize(_target);
            if (_panelShown) _panel.Place(_panelTarget);
        }
    }

    // ---- the batteries' plaque -----------------------------------------------------------------------

    private readonly BatteryPanelWindow _panel;
    private RectInt32 _panelTarget;
    private bool _panelShown; // shown with the mixer (decided as it opens, or when batteries come or go)

    private bool PanelWanted => ViewModel.BatteriesVisibility == Visibility.Visible;

    /// <summary>The plaque follows the mixer's slide, offset just as far.</summary>
    private void MovePanel(double away)
    {
        if (!_panelShown) return;
        var mixer = Offset(_target.X, _target.Y, away);
        _panel.Move(new PointInt32(_panelTarget.X + mixer.X - _target.X, _panelTarget.Y + mixer.Y - _target.Y));
    }

    /// <summary>Batteries found or gone while the mixer is open: the plaque appears or goes, the mixer makes room.</summary>
    private void OnBatteriesVisibilityChanged()
    {
        if (!_isOpen || PanelWanted == _panelShown) return;
        _panelShown = PanelWanted;
        UpdateBounds();
        if (_panelShown)
        {
            _panel.Place(_panelTarget);
            _panel.SetCloaked(false);
            _panel.ShowQuietly();
            StackPanelOverMixer();
        }
        else _panel.HideQuietly();
    }

    /// <summary>
    /// The plaque never activates, so nothing lifts it: put it just under the taskbar and the mixer right
    /// after it. Otherwise it could stay behind whatever window the mixer opened over.
    /// </summary>
    private void StackPanelOverMixer()
    {
        TaskbarPlacement.PlaceBelowTaskbar(_panel.Handle);
        SetWindowPos(_hwnd, _panel.Handle, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

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
