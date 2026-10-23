using System.Runtime.InteropServices;

namespace Omnideck.Core.Devices;

/// <summary>
/// Bluetooth devices that report their battery to Windows (what Settings → Bluetooth shows): the level is
/// a device property Windows keeps current, so this only reads it, no device I/O.
/// </summary>
public sealed class BluetoothBattery : IBatterySource
{
    private static readonly DEVPROPKEY BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DEVPROPKEY FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DEVPROPKEY DescriptionKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    public IReadOnlyList<DeviceBattery> Read()
    {
        var result = new List<DeviceBattery>();
        var set = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x4 /* PRESENT | ALLCLASSES */);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (ReadProperty(set, ref info, BatteryKey) is not { Length: > 0 } level) continue;
                string id = InstanceId(set, ref info) ?? $"bt:{i}";
                if (!id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue; // Bluetooth (classic and LE) nodes only
                string name = ReadString(set, ref info, FriendlyNameKey) ?? ReadString(set, ref info, DescriptionKey) ?? "Bluetooth device";
                result.Add(new DeviceBattery(id, name, Guess(name), Math.Clamp((int)level[0], 0, 100), false, "Bluetooth"));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        // A device can show up on more than one node (LE and its HID child): one entry per name.
        return result.GroupBy(b => b.Name).Select(g => g.First()).ToList();
    }

    /// <summary>What kind of device, from its name: Windows doesn't say on these nodes.</summary>
    private static DeviceKind Guess(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("мыш")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("клав")) return DeviceKind.Keyboard;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("xbox")) return DeviceKind.Gamepad;
        if (n.Contains("head") || n.Contains("buds") || n.Contains("pods") || n.Contains("earphone") || n.Contains("наушник")) return DeviceKind.Headset;
        return DeviceKind.Other;
    }

    private static byte[]? ReadProperty(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        var k = key;
        SetupDiGetDeviceProperty(set, ref info, ref k, out _, null, 0, out uint size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        return SetupDiGetDeviceProperty(set, ref info, ref k, out _, buffer, size, out _, 0) ? buffer : null;
    }

    private static string? ReadString(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key) =>
        ReadProperty(set, ref info, key) is { } bytes ? System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;

    private static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Length, out int length) ? new string(buffer, 0, Math.Max(0, length - 1)) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid) { public Guid fmtid = fmtid; public uint pid = pid; }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(IntPtr guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[]? buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, char[] id, int size, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
