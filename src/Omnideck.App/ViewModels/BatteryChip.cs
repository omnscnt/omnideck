using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Omnideck.Core.Devices;

namespace Omnideck.ViewModels;

/// <summary>One wireless device's battery, as the plaque over the mixer and the Devices page show it.</summary>
public sealed class BatteryChip(DeviceBattery device, int lowThreshold)
{
    public string Name => device.Name;
    public string Glyph => GlyphFor(device.Kind);
    public string LevelText => $"{device.Level}%";
    public string Source => device.Source;

    public Visibility ChargingVisibility => device.IsCharging ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Charging in the system's success green, low (and not charging) in its critical red.</summary>
    public Brush LevelBrush => (Brush)Application.Current.Resources[
        device.IsCharging ? "SystemFillColorSuccessBrush"
        : device.Level <= lowThreshold ? "SystemFillColorCriticalBrush"
        : "TextFillColorPrimaryBrush"];

    public string ToolTip => $"{device.Name}: {device.Level}%{(device.IsCharging ? ", charging" : "")}";

    /// <summary>Segoe Fluent Icons.</summary>
    public static string GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Headset => "",
        DeviceKind.Gamepad => "",
        _ => "", // Bluetooth
    };
}
