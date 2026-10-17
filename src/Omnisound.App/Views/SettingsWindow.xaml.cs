using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Omnisound.Core.Hotkeys;
using Omnisound.Services;
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

        Nav.SelectedItem = HotkeysItem;
        Nav.Loaded += (_, _) => FloatPane();
        AutoStartSwitch.IsOn = AutoStart.IsEnabled;
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
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
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

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "hotkeys";
        HotkeysPage.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Adds a blank hotkey above the add card; its card unfolds and starts recording (see HotkeyCard).</summary>
    private void OnAddClick(object sender, RoutedEventArgs e) => Hotkeys.AddDraft();

    private void OnHotkeysReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Hotkeys.Save();

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (AutoStartSwitch.IsOn != AutoStart.IsEnabled) AutoStart.SetEnabled(AutoStartSwitch.IsOn);
    }

    private async void OnWindowsSoundClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
