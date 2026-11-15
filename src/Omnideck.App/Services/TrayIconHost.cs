using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Omnideck.Services;

/// <summary>Tray icon: left click toggles the mixer, right click opens the Windows 11-style context menu.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Views.TrayMenuWindow _menuWindow;
    private readonly UISettings _uiSettings = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private System.Drawing.Icon? _currentIcon;
    private bool? _lightTaskbar;

    public TrayIconHost(Action toggleFlyout, Action openSettings, Action exit)
    {
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

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
            ToolTipText = "omnideck.",
            RightClickCommand = new RelayCommand(() => _menuWindow.ShowAtCursor()),
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(toggleFlyout),
        };
        UpdateIconForTheme();
        _icon.ForceCreate(enablesEfficiencyMode: false); // Efficiency mode would throttle the flyout.

        // Taskbar light/dark switches arrive as a color-values change.
        _uiSettings.ColorValuesChanged += (_, _) => _dispatcher.TryEnqueue(UpdateIconForTheme);
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph };

    private void UpdateIconForTheme()
    {
        bool light = IsTaskbarLight();
        if (_lightTaskbar == light) return;
        _lightTaskbar = light;

        var previous = _currentIcon;
        var color = light ? System.Drawing.Color.FromArgb(25, 25, 25) : System.Drawing.Color.White;
        _currentIcon = TrayIconRenderer.Render("", color, TrayIconRenderer.TraySize);
        _icon.Icon = _currentIcon;
#if DEBUG
        // Pixel-exact copy of what the tray receives, for checking rasterisation without capturing the taskbar.
        using (var png = _currentIcon.ToBitmap()) png.Save(Path.Combine(Path.GetTempPath(), "Omnideck-tray.png"));
#endif
        previous?.Dispose();
    }

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    public void ShowMenu() => _menuWindow.ShowAtCursor();

    /// <summary>A balloon from the tray icon (Windows shows it as a notification).</summary>
    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    public void Dispose()
    {
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
