using Omnideck.Core.Devices;

namespace Omnideck.Services;

/// <summary>
/// One tray notice per device when its battery drops to the threshold; it can warn again only after the
/// device charged (or rose clearly above it), so a level hovering at the line doesn't nag.
/// </summary>
public sealed class LowBatteryNotifier(Action<string, string> notify)
{
    private const int Hysteresis = 5;
    private readonly HashSet<string> _warned = [];

    public void Check(IReadOnlyList<DeviceBattery> devices, DeviceSettings settings)
    {
        lock (_warned)
        {
            foreach (var device in devices)
            {
                if (device.IsCharging || device.Level > settings.LowThreshold + Hysteresis)
                    _warned.Remove(device.Name);
                else if (settings.LowBatteryNotice && device.Level <= settings.LowThreshold && _warned.Add(device.Name))
                    notify($"{KindName(device.Kind)} battery low", $"{device.Name}: {device.Level}%");
            }
        }
    }

    private static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Mouse",
        DeviceKind.Keyboard => "Keyboard",
        DeviceKind.Headset => "Headset",
        DeviceKind.Gamepad => "Controller",
        _ => "Device",
    };
}
