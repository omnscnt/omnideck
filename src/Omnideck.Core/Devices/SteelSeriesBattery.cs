namespace Omnideck.Core.Devices;

/// <summary>
/// SteelSeries wireless mice, on their 2.4 GHz dongle or a cable, through the vendor HID interface the way
/// SteelSeries GG asks them (protocol as documented by rivalcfg). Request 0x92 (0xD2 over the dongle:
/// bit 6 marks it wireless); the answer echoes it, then a byte: bit 7 charging, the rest 1–21 for 0–100 %.
/// </summary>
public sealed class SteelSeriesBattery : IBatterySource
{
    private const int VendorId = 0x1038, Interface = 3;
    private const byte BatteryCommand = 0x92, WirelessFlag = 0x40, ChargingFlag = 0x80;

    private sealed record Model(int ProductId, string Name, bool Wireless);

    private static readonly Model[] Models =
    [
        new(0x1840, "SteelSeries Prime Wireless", true),
        new(0x1842, "SteelSeries Prime Wireless", false),
        new(0x1848, "SteelSeries Prime Mini Wireless", true),
        new(0x184A, "SteelSeries Prime Mini Wireless", false),
        new(0x1838, "SteelSeries Aerox 3 Wireless", true),
        new(0x183A, "SteelSeries Aerox 3 Wireless", false),
        new(0x1878, "SteelSeries Aerox 3 Wireless", true),
        new(0x187A, "SteelSeries Aerox 3 Wireless", false),
        new(0x1852, "SteelSeries Aerox 5 Wireless", true),
        new(0x1854, "SteelSeries Aerox 5 Wireless", false),
        new(0x185C, "SteelSeries Aerox 5 Wireless", true),
        new(0x185E, "SteelSeries Aerox 5 Wireless", false),
        new(0x1860, "SteelSeries Aerox 5 Wireless", true),
        new(0x1862, "SteelSeries Aerox 5 Wireless", false),
        new(0x1858, "SteelSeries Aerox 9 Wireless", true),
        new(0x185A, "SteelSeries Aerox 9 Wireless", false),
        new(0x1874, "SteelSeries Aerox 9 Wireless", true),
        new(0x1876, "SteelSeries Aerox 9 Wireless", false),
    ];

    public IReadOnlyList<DeviceBattery> Read()
    {
        var result = new List<DeviceBattery>();
        foreach (var path in HidDevice.EnumeratePaths())
        {
            if (!path.Contains($"vid_{VendorId:x4}&") || !path.Contains($"&mi_{Interface:x2}")) continue;
            var model = Models.FirstOrDefault(m => path.Contains($"&pid_{m.ProductId:x4}&"));
            if (model == null) continue;
            if (Query(path, model) is { } battery) result.Add(battery);
        }
        // On its charging cable a mouse can answer both there and through its receiver: one entry, the cable's.
        return result.GroupBy(b => b.Name).Select(g => g.OrderByDescending(b => b.IsCharging).First()).ToList();
    }

    private static DeviceBattery? Query(string path, Model model)
    {
        using var device = HidDevice.Open(path);
        if (device == null) return null;
        byte command = (byte)(BatteryCommand | (model.Wireless ? WirelessFlag : 0));
        // SteelSeries GG talks to the mouse too: its answers come through this interface as well, so the
        // queue is flushed first and replies are skipped until ours. Twice, in case ours got lost.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            device.Flush();
            if (!device.Write([command])) return null;
            var deadline = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < deadline)
            {
                var reply = device.Read((int)Math.Max(1, deadline - Environment.TickCount64));
                if (reply == null) break;
                if (reply.Length < 2 || reply[0] != command) continue;
                int raw = reply[1] & ~ChargingFlag;
                if (raw == 0) return null; // the mouse is off or asleep: the dongle has nothing to tell
                return new DeviceBattery(path, model.Name, DeviceKind.Mouse, Math.Clamp((raw - 1) * 5, 0, 100),
                    (reply[1] & ChargingFlag) != 0 || !model.Wireless, "SteelSeries");
            }
        }
        return null;
    }
}
