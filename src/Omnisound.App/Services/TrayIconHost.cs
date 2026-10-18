using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace Omnisound.Services;

/// <summary>Tray icon: left click toggles the mixer, right click opens the Windows 11-style context menu.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Views.TrayMenuWindow _menuWindow;
    private System.Drawing.Icon? _currentIcon;

    public TrayIconHost(Action toggleFlyout, Action openSettings, Action exit)
    {
        // Real WinUI MenuFlyout → Windows 11 look (rounded acrylic, icons, animation), positioned by us.
        // No toggle items: they reserve a checkmark column and push every label right
        // (autostart lives in the settings window instead).
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open mixer", Icon = Glyph("\uE995"), Command = new RelayCommand(toggleFlyout) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Settings", Icon = Glyph("\uE713"), Command = new RelayCommand(openSettings) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Icon = Glyph("\uE7E8"), Command = new RelayCommand(exit) });

        _menuWindow = new Views.TrayMenuWindow(menu);

        _icon = new TaskbarIcon
        {
            ToolTipText = "omnisound.",
            RightClickCommand = new RelayCommand(() => _menuWindow.ShowAtCursor()),
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(toggleFlyout),
        };
        _currentIcon = TrayIconRenderer.Load(TrayIconRenderer.TraySize);
        _icon.Icon = _currentIcon;
        _icon.ForceCreate(enablesEfficiencyMode: false); // Efficiency mode would throttle the flyout.
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph };

    public void ShowMenu() => _menuWindow.ShowAtCursor();

    public void Dispose()
    {
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
