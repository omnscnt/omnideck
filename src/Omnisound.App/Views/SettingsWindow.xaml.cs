using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Omnisound.Core.Hotkeys;
using Omnisound.Core.Media;
using Omnisound.Osd;
using Omnisound.Services;
using Omnisound.Taskbar;
using Omnisound.ViewModels;
using Windows.Graphics;

namespace Omnisound.Views;

/// <summary>Windows 11 Settings-style window: Hotkeys and General.</summary>
public sealed partial class SettingsWindow : Window
{
    public HotkeysViewModel Hotkeys { get; }

    public SettingsWindow(HotkeysViewModel hotkeys)
    {
        Hotkeys = hotkeys;
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "omnisound.ico"));
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
        LoadWidgetSettings();
        LoadOsdSettings();
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
        _paneCard.SetValue(Border.BorderBrushProperty, Application.Current.Resources["OmnisoundPaneStroke"]);
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

    private void OnHotkeysReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Hotkeys.Save();

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

    private void LoadOsdSettings()
    {
        _loadingOsd = true;
        _osd = App.Instance.VolumeOsdSettings;
        OsdSwitch.IsOn = _osd.Enabled;
        UpdateOsdPicker();
        OsdDurationSlider.Value = _osd.DurationMs / 1000.0;
        OsdExternalSwitch.IsOn = _osd.ShowExternalChanges;
        OsdWidthSlider.Value = _osd.Width;
        OsdHeightSlider.Value = _osd.Height;
        OsdWidthText.Text = _osd.Width.ToString();
        OsdHeightText.Text = _osd.Height.ToString();
        OsdBackingSlider.Value = _osd.Backing;
        OsdCornerSlider.Value = _osd.CornerPercent;
        UpdateOsdLabels();
        OsdDurationText.Text = $"{_osd.DurationMs / 1000.0:0.#} s";
        _loadingOsd = false;
    }

    private void OnOsdChanged(object sender, RoutedEventArgs e) => ApplyOsd();

    private void OnOsdSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyOsd();

    private void ApplyOsd()
    {
        if (_loadingOsd) return;
        _osd.Enabled = OsdSwitch.IsOn;
        _osd.DurationMs = (int)Math.Round(OsdDurationSlider.Value * 1000);
        _osd.ShowExternalChanges = OsdExternalSwitch.IsOn;
        _osd.Width = (int)Math.Round(OsdWidthSlider.Value);
        _osd.Height = (int)Math.Round(OsdHeightSlider.Value);
        _osd.Backing = (int)Math.Round(OsdBackingSlider.Value);
        _osd.CornerPercent = (int)Math.Round(OsdCornerSlider.Value);
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
            string key = slot.Tag as string == _osd.Position.ToString() ? "OmnisoundTrackValueFill" : "OmnisoundMeterTrack";
            slot.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
        }
    }

    private void OnOsdPreview(object sender, RoutedEventArgs e) => App.Instance.PreviewVolumeOsd();

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (AutoStartSwitch.IsOn != AutoStart.IsEnabled) AutoStart.SetEnabled(AutoStartSwitch.IsOn);
    }

    private async void OnWindowsSoundClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
