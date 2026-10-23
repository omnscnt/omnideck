using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Omnideck.Core.Devices;

/// <summary>
/// A HID interface opened for vendor commands: write an output report, read input reports with a timeout.
/// Overlapped I/O, so a read that never gets an answer is cancelled rather than left hanging.
/// </summary>
internal sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;

    public int InputLength { get; }
    public int OutputLength { get; }

    private HidDevice(SafeFileHandle handle, int input, int output)
    {
        _handle = handle;
        InputLength = input;
        OutputLength = output;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
    }

    /// <summary>Null when it can't be opened (in use exclusively, gone, no access).</summary>
    public static HidDevice? Open(string path)
    {
        var handle = CreateFile(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3 /* share read | write */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x40000000 /* FILE_FLAG_OVERLAPPED */, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!HidD_GetPreparsedData(handle, out var data))
        {
            handle.Dispose();
            return null;
        }
        try
        {
            HidP_GetCaps(data, out var caps);
            return new HidDevice(handle, caps.InputReportByteLength, caps.OutputReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    /// <summary>Drops input reports queued before now (answers to other programs' requests).</summary>
    public void Flush() => HidD_FlushQueue(_handle);

    /// <summary>Sends <paramref name="payload"/> as an output report (report ID 0 in front, padded to the report's length).</summary>
    public bool Write(ReadOnlySpan<byte> payload)
    {
        if (OutputLength < payload.Length + 1) return false;
        var report = new byte[OutputLength];
        payload.CopyTo(report.AsSpan(1));
        try
        {
            using var cts = new CancellationTokenSource(500);
            _stream.WriteAsync(report, cts.Token).AsTask().Wait();
            return true;
        }
        catch (Exception ex) when (ex is IOException or AggregateException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The next input report without its report ID, or null when none comes within <paramref name="timeoutMs"/>.</summary>
    public byte[]? Read(int timeoutMs)
    {
        var report = new byte[InputLength];
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            int read = _stream.ReadAsync(report, cts.Token).AsTask().GetAwaiter().GetResult();
            return read > 1 ? report[1..read] : null;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }

    // ---- enumeration ----------------------------------------------------------------------------

    /// <summary>Paths of the HID interfaces present, lowercase, e.g. "\\?\hid#vid_1038&amp;pid_1840&amp;mi_03#...".</summary>
    public static List<string> EnumeratePaths()
    {
        var result = new List<string>();
        var hidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10 /* PRESENT | DEVICEINTERFACE */);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size == 0) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                        result.Add(Marshal.PtrToStringUni(detail + 4)!.ToLowerInvariant());
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    // ---- native ------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern bool HidD_FlushQueue(SafeFileHandle handle);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
