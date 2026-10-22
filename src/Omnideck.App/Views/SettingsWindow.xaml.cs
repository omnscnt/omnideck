using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Omnideck.Core.Hotkeys;
using Omnideck.Core.Media;
using Omnideck.Osd;
using Omnideck.Services;
using Omnideck.Taskbar;
using Omnideck.ViewModels;
using Windows.Foundation;
using Windows.Graphics;

namespace Omnideck.Views;

/// <summary>Windows 11 Settings-style window: Hotkeys and General.</summary>
public sealed partial class SettingsWindow : Window
{
    public HotkeysViewModel Hotkeys { get; }

    public SettingsWindow(HotkeysViewModel hotkeys)
    {
        Hotkeys = hotkeys;
        InitializeComponent();
        SetupReorder();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        VersionRun.Text = "  " + Updater.CurrentText;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "omnideck.ico"));
        CenterOnScreen(820, 600);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this the cards get too cramped for their controls.
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            presenter.PreferredMinimumWidth = (int)(600 * scale);
            presenter.PreferredMinimumHeight = (int)(420 * scale);
        }

        Nav.SelectedItem = HotkeysItem;
        Nav.Loaded += (_, _) => FloatPane();
        AutoStartSwitch.IsOn = AutoStart.IsEnabled;
        LogVolumeSwitch.IsOn = App.Instance.LogarithmicVolume;
        LoadWidgetSettings();
        SetupOsdTiles();
        LoadOsdSettings();
        LoadUpdateSettings();
        SettingsPathText.Text = $"Settings are stored in {SettingsStore.FilePath}";
    }

    private void CenterOnScreen(int widthDip, int heightDip)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(widthDip * scale), h = (int)(heightDip * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    private const double PaneInset = 8;
    private Border? _paneCard;

    /// <summary>
    /// Turns the tab pane into a floating card: inset from the window edges, rounded on every corner.
    /// NavigationView has no resource for this, and the pane content is clipped (not resized) when the
    /// pane collapses, so the card is a separate layer behind the items whose width follows the pane
    /// state. The items themselves are inset via NavigationViewItemButtonMargin (XAML).
    /// </summary>
    private void FloatPane()
    {
        if (_paneCard != null || FindDescendant(Nav, "PaneContentGrid") is not Grid paneGrid) return;

        _paneCard = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            // PaneContentGrid starts 3 px down inside the pane: compensate so the bottom inset is exact, and
            // lift the top 2 px so the first item (hamburger) has the same 3 px gap above as at the sides.
            Margin = new Thickness(PaneInset, -2, 0, PaneInset - 3),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        _paneCard.SetValue(Border.BackgroundProperty, Application.Current.Resources["LayerFillColorDefaultBrush"]);
        _paneCard.SetValue(Border.BorderBrushProperty, Application.Current.Resources["OmnideckPaneStroke"]);
        Grid.SetRowSpan(_paneCard, Math.Max(1, paneGrid.RowDefinitions.Count));
        paneGrid.Children.Insert(0, _paneCard);

        Nav.PaneOpened += (_, _) => SuppressPaneToolTips();
        Nav.PaneClosed += (_, _) => SuppressPaneToolTips();
        SuppressPaneToolTips();

        Nav.PaneOpening += (_, _) => SizePaneCard(open: true);
        Nav.PaneClosing += (_, _) => SizePaneCard(open: false);
        // Start collapsed. Left mode opens the pane on load regardless of IsPaneOpen in XAML.
        Nav.IsPaneOpen = false;
        SizePaneCard(Nav.IsPaneOpen);
    }

    /// <summary>
    /// The hamburger is a regular (non-selecting) menu item rather than NavigationView's own toggle
    /// button, so it has exactly the same highlight, corners, insets and icon column as the tabs.
    /// </summary>
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == MenuToggleItem) Nav.IsPaneOpen = !Nav.IsPaneOpen;
    }

    /// <summary>
    /// No hover tooltips on the pane. NavigationView assigns item names itself when the pane is
    /// collapsed unless a tooltip is already set, so give each a collapsed one; re-applied whenever
    /// the pane changes state.
    /// </summary>
    private void SuppressPaneToolTips()
    {
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
            ToolTipService.SetToolTip(item, new ToolTip { Visibility = Visibility.Collapsed });
    }

    private void SizePaneCard(bool open)
    {
        if (_paneCard == null) return;
        double pane = open ? Nav.OpenPaneLength : Nav.CompactPaneLength;
        _paneCard.Width = pane - 2 * PaneInset;
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindFirst<T>(child) is { } found) return found;
        }
        return null;
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendant(child, name) is { } found) return found;
        }
        return null;
    }

    private const double PageMaxWidth = 900, PageGutter = 36;

    /// <summary>
    /// Every page: content at most <see cref="PageMaxWidth"/> wide, centred once the window is wider.
    /// Done with padding rather than MaxWidth so the scrollbar stays at the window edge.
    /// </summary>
    private void OnPageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Past the maximum width the content stops growing and centres.
        double side = Math.Max(PageGutter, (e.NewSize.Width - PageMaxWidth) / 2);
        HotkeysPage.Padding = new Thickness(side, 8, side, 36);
        WidgetContent.Padding = new Thickness(side, 8, side, 36);
        GeneralContent.Padding = new Thickness(side, 8, side, 36);
        OsdContentGrid.Padding = new Thickness(side, 8, side, 36);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "hotkeys";
        HotkeysPage.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        WidgetPage.Visibility = tag == "widget" ? Visibility.Visible : Visibility.Collapsed;
        OsdPage.Visibility = tag == "osd" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Adds a blank hotkey above the add card; its card unfolds and starts recording (see HotkeyCard).</summary>
    private void OnAddClick(object sender, RoutedEventArgs e) => Hotkeys.AddDraft();

    // ---- hotkey card reordering --------------------------------------------------------------------
    // Our own drag instead of ListView's: the card itself follows the pointer freely (opaque, above the
    // others), the others slide aside to open its slot, it glides into that slot when dropped, and dropping
    // it back where it came from changes nothing.
    // Positions are taken once when the drag starts; everything moves by Translation, and the collection
    // is only reordered on release.

    private const double DragThreshold = 6, CardGap = 4, AutoScrollZone = 56, AutoScrollMaxSpeed = 900; // DIPs, DIPs per second

    private HotkeyItemViewModel? _pressedItem;
    private Point _pressPoint;
    private bool _dragging;
    private int _dragFrom, _dragTo;
    private double _dragStartY;          // pointer, in panel coordinates, when the drag started
    private double _dragViewportX, _dragViewportY; // last pointer position over the list (for auto-scroll)
    private bool _dropping;              // the dropped card is gliding into its slot
    private (ListViewItem? Container, double Top, double Height)[] _slots = [];
    private bool _autoScrolling;
    private ScrollViewer? _scroller;
    private ScrollViewer? Scroller => _scroller ??= FindChild<ScrollViewer>(HotkeysPage);
    private double _scrollTarget;        // the scroll offset we asked for (ChangeView applies it on the next layout)
    private readonly System.Diagnostics.Stopwatch _autoScrollClock = new();


    private void SetupReorder()
    {
        // handledEventsToo: the list item marks presses handled for its own selection logic.
        HotkeysPage.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnCardPointerPressed), true);
        HotkeysPage.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnCardPointerMoved), true);
        HotkeysPage.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnCardPointerReleased), true);
        HotkeysPage.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCardPointerCaptureLost), true);
    }

    /// <summary>A press on a card's header (not on its buttons, switch or editor) may start a drag.</summary>
    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressedItem = null;
        if (_dragging || _dropping || !e.GetCurrentPoint(HotkeysPage).Properties.IsLeftButtonPressed) return;
        bool onHeader = false;
        for (var o = e.OriginalSource as DependencyObject; o != null && o is not ListViewItem; o = VisualTreeHelper.GetParent(o))
        {
            if (o is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or ToggleSwitch or TextBox or ComboBox or Slider or NumberBox) return;
            if (o is FrameworkElement { Name: "Header" }) onHeader = true;
        }
        if (!onHeader) return;
        _pressedItem = (FindParent<ListViewItem>(e.OriginalSource as DependencyObject)?.Content) as HotkeyItemViewModel;
        _pressPoint = e.GetCurrentPoint(HotkeysPage).Position;
    }

    private void OnCardPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(HotkeysPage).Position;
        if (!_dragging)
        {
            if (_pressedItem == null || !e.GetCurrentPoint(HotkeysPage).Properties.IsLeftButtonPressed) return;
            if (Math.Abs(point.Y - _pressPoint.Y) < DragThreshold && Math.Abs(point.X - _pressPoint.X) < DragThreshold) return;
            if (!BeginDrag(e)) return;
        }
        _dragViewportX = point.X;
        _dragViewportY = point.Y;
        UpdateDrag();
        e.Handled = true;
    }

    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedItem = null;
        if (!_dragging) return;
        EndDrag(commit: true);
        HotkeysPage.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnCardPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) EndDrag(commit: true);
    }

    private bool BeginDrag(PointerRoutedEventArgs e)
    {
        var panel = HotkeysPage.ItemsPanelRoot;
        _dragFrom = _dragTo = Hotkeys.Items.IndexOf(_pressedItem!);
        if (panel == null || _dragFrom < 0 || !HotkeysPage.CapturePointer(e.Pointer)) return false;

        // Where every card sits now (cards scrolled far out of view may have no container: they just don't move).
        _slots = new (ListViewItem?, double, double)[Hotkeys.Items.Count];
        for (int i = 0; i < _slots.Length; i++)
        {
            var container = HotkeysPage.ContainerFromIndex(i) as ListViewItem;
            if (container == null) continue;
            double top = container.TransformToVisual(panel).TransformPoint(default).Y;
            _slots[i] = (container, top, container.ActualHeight + CardGap);
            container.TranslationTransition = i == _dragFrom ? null : new Vector3Transition { Duration = TimeSpan.FromMilliseconds(170) };
        }
        if (_slots[_dragFrom].Container is not { } dragged) return false;
        Canvas.SetZIndex(dragged, 1);
        // The card fill is translucent (over Mica): lifted over the other cards it needs an opaque base.
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var solid) && solid is Brush brush)
            dragged.Background = brush;

        _scrollTarget = Scroller?.VerticalOffset ?? 0;
        _dragStartY = e.GetCurrentPoint(HotkeysPage).Position.Y + _scrollTarget;
        _dragging = true;
        return true;
    }

    private void UpdateDrag()
    {
        var from = _slots[_dragFrom];
        int last = _slots.Length - 1;
        // Pointer in content coordinates: over the list plus the scroll offset. While auto-scrolling that is
        // the offset just asked for, so the card moves in step with the scroll on the same frame.
        if (!_autoScrolling) _scrollTarget = Scroller?.VerticalOffset ?? _scrollTarget;
        // The card stays within the list: from the first slot's top to the last slot's bottom.
        double dy = _dragViewportY + _scrollTarget - _dragStartY;
        double minDy = _slots[0].Container != null ? _slots[0].Top - from.Top : double.NegativeInfinity;
        double maxDy = _slots[last].Container != null ? _slots[last].Top + _slots[last].Height - (from.Top + from.Height) : double.PositiveInfinity;
        dy = Math.Clamp(dy, Math.Min(0, minDy), Math.Max(0, maxDy));
        // Sideways it simply follows the pointer; only the vertical position picks the slot.
        float dx = (float)(_dragViewportX - _pressPoint.X);
        from.Container!.Translation = new System.Numerics.Vector3(dx, (float)dy, 0);

        // The slot it would drop into: past every card whose middle its own middle has reached (reached, not
        // passed: pressed against the end of the list, an equal-height card sits exactly on the last middle).
        double middle = from.Top + dy + from.Height / 2;
        int to = _dragFrom;
        for (int i = _dragFrom + 1; i <= last; i++)
            if (_slots[i].Container != null && middle >= _slots[i].Top + _slots[i].Height / 2 - 0.5) to = i;
        for (int i = _dragFrom - 1; i >= 0; i--)
            if (_slots[i].Container != null && middle <= _slots[i].Top + _slots[i].Height / 2 + 0.5) to = i;
        // Pressed against either end (a tall, unfolded card's middle may never reach the last one's): that end.
        if (dy >= maxDy - 0.5 && _slots[last].Container != null) to = last;
        if (dy <= minDy + 0.5 && _slots[0].Container != null) to = 0;
        _dragTo = to;

        // Cards between the old and the new slot step aside by the dragged card's height.
        for (int i = 0; i <= last; i++)
        {
            if (i == _dragFrom || _slots[i].Container is not { } c) continue;
            double shift = _dragFrom < i && i <= to ? -from.Height : to <= i && i < _dragFrom ? from.Height : 0;
            c.Translation = new System.Numerics.Vector3(0, (float)shift, 0);
        }

        UpdateAutoScroll();
    }

    /// <summary>
    /// Near the top or bottom of the list the page scrolls, carrying the card along: every frame, faster
    /// the deeper the pointer is in the edge zone, by elapsed time rather than a fixed step.
    /// </summary>
    private void UpdateAutoScroll()
    {
        bool near = _dragViewportY < AutoScrollZone || _dragViewportY > HotkeysPage.ActualHeight - AutoScrollZone;
        if (near == _autoScrolling) return;
        _autoScrolling = near;
        if (near)
        {
            _autoScrollClock.Restart();
            CompositionTarget.Rendering += OnAutoScrollFrame;
        }
        else CompositionTarget.Rendering -= OnAutoScrollFrame;
    }

    private void StopAutoScroll()
    {
        if (!_autoScrolling) return;
        _autoScrolling = false;
        CompositionTarget.Rendering -= OnAutoScrollFrame;
    }

    private void OnAutoScrollFrame(object? sender, object e)
    {
        var scroller = Scroller;
        if (!_dragging || scroller == null) { StopAutoScroll(); return; }
        double dt = Math.Min(0.05, _autoScrollClock.Elapsed.TotalSeconds);
        _autoScrollClock.Restart();

        double depth = _dragViewportY < AutoScrollZone
            ? -(AutoScrollZone - _dragViewportY)
            : _dragViewportY - (HotkeysPage.ActualHeight - AutoScrollZone);
        double speed = Math.Clamp(depth / AutoScrollZone, -1, 1) * AutoScrollMaxSpeed;
        double target = Math.Clamp(_scrollTarget + speed * dt, 0, scroller.ScrollableHeight);
        if (Math.Abs(target - _scrollTarget) < 0.01) return;
        _scrollTarget = target;
        scroller.ChangeView(null, target, null, true);
        UpdateDrag();
    }

    /// <summary>
    /// Drops the card: it glides into the slot it was over, then the collection is reordered and every
    /// card's offset cleared in the same frame, so nothing jumps (the list's own reorder animations are
    /// off for that one layout pass).
    /// </summary>
    private void EndDrag(bool commit)
    {
        if (!_dragging) return; // released and capture-lost both end it
        _dragging = false;
        _pressedItem = null;
        StopAutoScroll();
        if (!commit) _dragTo = _dragFrom;

        // Where its slot is, relative to where it started: past the cards it displaced.
        double slotDy = 0;
        for (int i = Math.Min(_dragFrom, _dragTo); i <= Math.Max(_dragFrom, _dragTo); i++)
            if (i != _dragFrom && _slots[i].Container != null) slotDy += _dragTo > _dragFrom ? _slots[i].Height : -_slots[i].Height;
        if (_slots[_dragFrom].Container is { } dragged)
        {
            _dropping = true;
            dragged.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
            dragged.Translation = new System.Numerics.Vector3(0, (float)slotDy, 0);
            var settle = DispatcherQueue.CreateTimer();
            settle.Interval = TimeSpan.FromMilliseconds(160);
            settle.IsRepeating = false;
            settle.Tick += (_, _) =>
            {
                _dropping = false;
                FinishDrop();
            };
            settle.Start();
        }
        else FinishDrop();
    }

    private void FinishDrop()
    {
        var transitions = HotkeysPage.ItemContainerTransitions;
        bool move = _dragTo != _dragFrom;
        if (move) HotkeysPage.ItemContainerTransitions = [];
        foreach (var (container, _, _) in _slots)
        {
            if (container == null) continue;
            container.TranslationTransition = null;
            container.Translation = default;
            container.ClearValue(Control.BackgroundProperty); // back to the style's transparent
            Canvas.SetZIndex(container, 0);
        }
        _slots = [];
        if (!move) return;
        Hotkeys.Items.Move(_dragFrom, _dragTo);
        Hotkeys.Save();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => HotkeysPage.ItemContainerTransitions = transitions);
    }

    private static T? FindParent<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o != null && o is not T) o = VisualTreeHelper.GetParent(o);
        return o as T;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---- taskbar widget page ---------------------------------------------------------------------
    // The page edits a copy of the widget settings and applies (and saves) it after every change.

    private TaskbarWidgetSettings _widget = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<WidgetAppItem> _widgetApps = [];
    private bool _loadingWidget = true; // until LoadWidgetSettings (controls raise change events while being built)

    private void LoadWidgetSettings()
    {
        _loadingWidget = true;
        _widget = App.Instance.TaskbarWidgetSettings;
        WidgetSwitch.IsOn = _widget.Enabled;
        WidgetPositionBox.SelectedIndex = (int)_widget.Position;
        WidgetOffsetBox.Value = _widget.Offset;
        WidgetSizeBox.SelectedIndex = (int)_widget.Size;
        WidgetWidthBox.Value = _widget.MaxTextWidth;
        WidgetControlsSwitch.IsOn = _widget.ShowControls;
        WidgetBackdropSwitch.IsOn = _widget.CoverBackdrop;
        WidgetDimSlider.Value = _widget.PausedDim;
        WidgetDimText.Text = $"{_widget.PausedDim}%";
        WidgetScrollSwitch.IsOn = _widget.ScrollTitles;
        WidgetFullscreenSwitch.IsOn = _widget.HideInFullscreen;
        WidgetFilterBox.SelectedIndex = (int)_widget.FilterMode;
        foreach (var app in _widget.FilterApps)
        {
            // A running app refreshes its remembered path (apps added before paths were kept get one now).
            string? path = App.Instance.FindExePath(app) ?? _widget.AppPaths.GetValueOrDefault(app);
            if (path != null) _widget.AppPaths[app] = path;
            _widgetApps.Add(new WidgetAppItem(app, path));
        }
        WidgetAppsList.ItemsSource = _widgetApps;
        _loadingWidget = false;
        UpdateWidgetPage();
    }

    private void OnWidgetChanged(object sender, RoutedEventArgs e) => ApplyWidget();

    private void OnWidgetNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyWidget();

    private void OnWidgetSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyWidget();

    private void ApplyWidget()
    {
        if (_loadingWidget) return;
        _widget.Enabled = WidgetSwitch.IsOn;
        _widget.Position = (WidgetPosition)Math.Max(0, WidgetPositionBox.SelectedIndex);
        _widget.Offset = double.IsNaN(WidgetOffsetBox.Value) ? 0 : (int)Math.Round(WidgetOffsetBox.Value);
        _widget.Size = (WidgetSize)Math.Max(0, WidgetSizeBox.SelectedIndex);
        _widget.MaxTextWidth = double.IsNaN(WidgetWidthBox.Value) ? 200 : (int)Math.Round(WidgetWidthBox.Value);
        _widget.ShowControls = WidgetControlsSwitch.IsOn;
        _widget.CoverBackdrop = WidgetBackdropSwitch.IsOn;
        _widget.PausedDim = (int)Math.Round(WidgetDimSlider.Value);
        _widget.ScrollTitles = WidgetScrollSwitch.IsOn;
        _widget.HideInFullscreen = WidgetFullscreenSwitch.IsOn;
        WidgetDimText.Text = $"{_widget.PausedDim}%";
        _widget.FilterMode = (MediaFilterMode)Math.Max(0, WidgetFilterBox.SelectedIndex);
        _widget.FilterApps = [.. _widgetApps.Select(a => a.Name)];
        App.Instance.ApplyTaskbarWidget(_widget);
        UpdateWidgetPage();
    }

    /// <summary>The app list only matters when filtering.</summary>
    private void UpdateWidgetPage() =>
        WidgetAppsPanel.Visibility = _widget.FilterMode == MediaFilterMode.All ? Visibility.Collapsed : Visibility.Visible;

    private bool IsListed(string name) => _widgetApps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private void OnWidgetAppBoxOpened(object? sender, object e)
    {
        // Offer the apps that have a media session or play sound right now, with their icons.
        WidgetAppBox.Items.Clear();
        foreach (var name in App.Instance.MediaAppNames().Where(n => !IsListed(n)))
            WidgetAppBox.Items.Add(new WidgetAppItem(name, App.Instance.FindExePath(name)));
    }

    private void OnWidgetAppAdd(object sender, RoutedEventArgs e)
    {
        string text = (WidgetAppBox.SelectedItem?.ToString() ?? WidgetAppBox.Text ?? "").Trim();
        string name = Path.GetFileNameWithoutExtension(text).ToLowerInvariant();
        if (name.Length == 0 || IsListed(name)) return;

        // Remember where the exe lives so the icon still shows once the app is closed.
        string? path = (WidgetAppBox.SelectedItem as WidgetAppItem)?.ExePath ?? App.Instance.FindExePath(name);
        if (path != null) _widget.AppPaths[name] = path;
        _widgetApps.Add(new WidgetAppItem(name, path));
        WidgetAppBox.SelectedItem = null;
        WidgetAppBox.Text = "";
        ApplyWidget();
    }

    private void OnWidgetAppRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WidgetAppItem item) return;
        _widgetApps.Remove(item);
        _widget.AppPaths.Remove(item.Name);
        ApplyWidget();
    }

    // ---- volume indicator page -----------------------------------------------------------------------

    private VolumeOsdSettings _osd = new();
    private bool _loadingOsd = true; // until LoadOsdSettings: the slider fires ValueChanged while XAML is still being built

    private OsdKindTile _osdVolume = null!, _osdMic = null!, _osdLocks = null!, _osdLayout = null!, _osdDevice = null!;

    /// <summary>The tiles that pick which notices show; each change applies (and saves) at once.</summary>
    private void SetupOsdTiles()
    {
        _osdVolume = new("Volume", "The new volume when a hotkey changes it", "", ApplyOsd);
        _osdMic = new("Microphone", "Muted or on, by a hotkey or anything else", "", ApplyOsd);
        _osdLocks = new("Lock keys", "Caps, Num and Scroll Lock on or off", "", ApplyOsd);
        _osdLayout = new("Keyboard layout", "The input language you switch to", "", ApplyOsd);
        _osdDevice = new("Output device", "The new default output", "", ApplyOsd);
        OsdKindTiles.ItemsSource = new[] { _osdVolume, _osdMic, _osdLocks, _osdLayout, _osdDevice };
    }

    private void LoadOsdSettings()
    {
        _loadingOsd = true;
        _osd = App.Instance.VolumeOsdSettings;
        _osdVolume.Load(_osd.Enabled);
        _osdMic.Load(_osd.ShowMicrophone);
        _osdLocks.Load(_osd.ShowLockKeys);
        _osdLayout.Load(_osd.ShowKeyboardLayout);
        _osdDevice.Load(_osd.ShowOutputDevice);
        UpdateOsdPicker();
        OsdDurationSlider.Value = _osd.DurationMs / 1000.0;
        OsdExternalSwitch.IsOn = _osd.ShowExternalChanges;
        OsdWidthSlider.Value = _osd.Width;
        OsdHeightSlider.Value = _osd.Height;
        OsdWidthText.Text = _osd.Width.ToString();
        OsdHeightText.Text = _osd.Height.ToString();
        OsdBackingSlider.Value = _osd.Backing;
        OsdCornerSlider.Value = _osd.CornerPercent;
        OsdTextSlider.Value = _osd.TextSize;
        UpdateOsdLabels();
        OsdDurationText.Text = $"{_osd.DurationMs / 1000.0:0.#} s";
        _loadingOsd = false;
    }

    private void OnOsdChanged(object sender, RoutedEventArgs e) => ApplyOsd();

    private void OnOsdSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyOsd();

    private void ApplyOsd()
    {
        if (_loadingOsd) return;
        _osd.Enabled = _osdVolume.IsOn;
        _osd.ShowMicrophone = _osdMic.IsOn;
        _osd.ShowLockKeys = _osdLocks.IsOn;
        _osd.ShowKeyboardLayout = _osdLayout.IsOn;
        _osd.ShowOutputDevice = _osdDevice.IsOn;
        _osd.DurationMs = (int)Math.Round(OsdDurationSlider.Value * 1000);
        _osd.ShowExternalChanges = OsdExternalSwitch.IsOn;
        _osd.Width = (int)Math.Round(OsdWidthSlider.Value);
        _osd.Height = (int)Math.Round(OsdHeightSlider.Value);
        _osd.Backing = (int)Math.Round(OsdBackingSlider.Value);
        _osd.CornerPercent = (int)Math.Round(OsdCornerSlider.Value);
        _osd.TextSize = (int)Math.Round(OsdTextSlider.Value);
        UpdateOsdLabels();
        OsdWidthText.Text = _osd.Width.ToString();
        OsdHeightText.Text = _osd.Height.ToString();
        OsdDurationText.Text = $"{_osd.DurationMs / 1000.0:0.#} s";
        App.Instance.ApplyVolumeOsd(_osd);
    }

    private void UpdateOsdLabels()
    {
        OsdBackingText.Text = _osd.Backing > 0 ? $"+{_osd.Backing}" : _osd.Backing.ToString();
        OsdCornerText.Text = $"{_osd.CornerPercent}%";
        OsdTextText.Text = _osd.TextSize.ToString();
    }

    private void OnOsdSlotClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<OsdPosition>(tag, out var position)) return;
        _osd.Position = position;
        UpdateOsdPicker();
        ApplyOsd();
        App.Instance.PreviewVolumeOsd(); // show it in the new spot right away
    }

    /// <summary>The chosen spot is bright, the others dim (not the accent: it can be grey too).</summary>
    private void UpdateOsdPicker()
    {
        foreach (var slot in OsdPositionPicker.Children.OfType<Button>())
        {
            string key = slot.Tag as string == _osd.Position.ToString() ? "OmnideckTrackValueFill" : "OmnideckMeterTrack";
            slot.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
        }
    }

    private void OnOsdPreview(object sender, RoutedEventArgs e) => App.Instance.PreviewVolumeOsd();

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (AutoStartSwitch.IsOn != AutoStart.IsEnabled) AutoStart.SetEnabled(AutoStartSwitch.IsOn);
    }

    private void OnLogVolumeToggled(object sender, RoutedEventArgs e)
    {
        if (LogVolumeSwitch.IsOn != App.Instance.LogarithmicVolume) App.Instance.LogarithmicVolume = LogVolumeSwitch.IsOn;
    }

    private async void OnWindowsSoundClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));

    // ---- updates ----------------------------------------------------------------------------------

    private UpdateInfo? _update;
    private bool _updateBusy;

    private void LoadUpdateSettings()
    {
        AutoUpdateSwitch.IsOn = App.Instance.CheckUpdatesAutomatically;
        UpdateStatusText.Text = $"Version {Updater.CurrentText}";
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (AutoUpdateSwitch.IsOn != App.Instance.CheckUpdatesAutomatically) App.Instance.CheckUpdatesAutomatically = AutoUpdateSwitch.IsOn;
    }

    /// <summary>First click checks; when a newer release exists the button becomes "Update now", which installs it.</summary>
    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (_update == null)
            {
                UpdateStatusText.Text = "Checking…";
                _update = await Updater.CheckAsync();
                if (_update == null)
                {
                    UpdateStatusText.Text = $"You have the latest version ({Updater.CurrentText}).";
                    return;
                }
                UpdateStatusText.Text = $"Version {Updater.Format(_update.Version)} is available (you have {Updater.CurrentText})."
                    + (Updater.CanInstall ? "" : " This is a development build: install releases from GitHub.");
                UpdateButton.Content = "Update now";
                return;
            }

            UpdateStatusText.Text = $"Downloading version {Updater.Format(_update.Version)}…";
            UpdateProgress.Value = 0;
            UpdateProgress.Visibility = Visibility.Visible;
            await Updater.PrepareAsync(_update, new Progress<double>(p => UpdateProgress.Value = p));
            UpdateStatusText.Text = "Restarting…";
            App.Instance.ExitForUpdate();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't {(_update == null ? "check for updates" : "update")}: {ex.Message}";
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = _update == null || Updater.CanInstall;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
