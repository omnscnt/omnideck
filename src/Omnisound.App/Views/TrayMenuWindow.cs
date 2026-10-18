using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Omnisound.Services;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Omnisound.Views;

/// <summary>
/// Invisible 1×1 host for the tray context menu. The MenuFlyout renders as a windowed popup
/// (ShouldConstrainToRootBounds = false), so it gets the real Windows 11 look while we control
/// exactly where it opens: just off the taskbar, next to the cursor.
/// </summary>
public sealed class TrayMenuWindow : Window
{
    private readonly Grid _root = new();
    private readonly MenuFlyout _menu;
    private readonly IntPtr _hwnd;

    public TrayMenuWindow(MenuFlyout menu)
    {
        _menu = menu;
        Content = _root;
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        presenter.IsAlwaysOnTop = true; // only takes effect once the presenter is attached
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) => e.Cancel = true;

        // Fully transparent host — only the popup (its own window) is visible.
        SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExLayered | WsExToolWindow);
        SetLayeredWindowAttributes(_hwnd, 0, 0, LwaAlpha);

        _menu.ShouldConstrainToRootBounds = false;

        // The default in-app acrylic has nothing to blur inside a windowed popup and renders solid;
        // a desktop acrylic backdrop on the popup itself gives the translucent shell-menu look.
        // Lighter than the flyout's Base preset: shell context menus are noticeably see-through.
        _menu.SystemBackdrop = new Controls.ActiveAcrylicBackdrop
        {
            TintColor = Windows.UI.Color.FromArgb(255, 32, 32, 32),
            TintOpacity = 0f,
            LuminosityOpacity = 0.6f,
        };
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        _menu.MenuFlyoutPresenterStyle = presenterStyle;
        _menu.Closed += (_, _) => AppWindow?.Hide(); // null once the app is exiting (menu "Exit" closes the window first)
    }

    public void ShowAtCursor()
    {
        var anchor = TaskbarPlacement.Capture();
        int gap = (int)Math.Round(8 * anchor.Scale);

        // Open away from the taskbar, ending at the cursor like the shell's own tray menus.
        var (point, placement) = anchor.Edge switch
        {
            TaskbarEdge.Top => (new PointInt32(anchor.Cursor.X, anchor.WorkArea.Y + gap), FlyoutPlacementMode.BottomEdgeAlignedRight),
            TaskbarEdge.Left => (new PointInt32(anchor.WorkArea.X + gap, anchor.Cursor.Y), FlyoutPlacementMode.RightEdgeAlignedBottom),
            TaskbarEdge.Right => (new PointInt32(anchor.Right - gap, anchor.Cursor.Y), FlyoutPlacementMode.LeftEdgeAlignedBottom),
            _ => (new PointInt32(anchor.Cursor.X, anchor.Bottom - gap), FlyoutPlacementMode.TopEdgeAlignedRight),
        };

        AppWindow.MoveAndResize(new RectInt32(point.X, point.Y, 1, 1));
        AppWindow.Show(true);
        Activate();
        TaskbarPlacement.BringToFront(_hwnd);

        // Let the host's XAML root load before anchoring the popup to it.
        DispatcherQueue.TryEnqueue(() =>
            _menu.ShowAt(_root, new FlyoutShowOptions { Position = new Point(0, 0), Placement = placement }));
    }

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;
    private const int LwaAlpha = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, int flags);
}
