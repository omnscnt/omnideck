namespace Omnideck.Core.Devices;

public enum DeviceKind { Mouse, Keyboard, Headset, Gamepad, Other }

/// <summary>A wireless device's battery as last read.</summary>
/// <param name="Id">Stable for the device (its HID or device instance path), to tell readings apart.</param>
/// <param name="Level">0–100.</param>
/// <param name="Source">Where it came from ("SteelSeries", "Bluetooth"), for the settings list.</param>
public sealed record DeviceBattery(string Id, string Name, DeviceKind Kind, int Level, bool IsCharging, string Source);

/// <summary>One way of finding devices and reading their batteries. <see cref="Read"/> blocks (device I/O): call it off the UI thread.</summary>
public interface IBatterySource
{
    IReadOnlyList<DeviceBattery> Read();
}
