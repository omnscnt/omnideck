using System.Runtime.InteropServices;

namespace Omnideck.Core.Interop;

/// <summary>
/// Windows' per-app output device ("App volume and device preferences" in Settings), through the
/// undocumented Windows.Media.Internal.AudioPolicyConfig factory, as EarTrumpet uses it. Windows keeps
/// the choice per app (it outlives the process). Called through the raw vtable: .NET 8 has no
/// IInspectable interop, and only these three methods are known.
/// </summary>
internal static unsafe class AudioPolicyConfig
{
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";

    // The factory's IID changed in Windows 10 21H2 (build 21390); the methods we use kept their slots.
    private static readonly Guid FactoryIid = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid FactoryIidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    // IUnknown (3) + IInspectable (3) + 19 methods before these.
    private const int SetSlot = 25, GetSlot = 26;

    // Device ids as this API wants them: the MMDevice id wrapped in the audio interface path.
    private const string IdPrefix = @"\\?\SWD#MMDEVAPI#", RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private static readonly Lazy<IntPtr> Factory = new(Create);

    public static bool IsAvailable => Factory.Value != IntPtr.Zero;

    /// <summary>The output device an app's process is routed to (an MMDevice id), or null for the default.</summary>
    public static string? GetOutput(int processId)
    {
        var factory = Factory.Value;
        if (factory == IntPtr.Zero) return null;
        var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)(*(IntPtr**)factory)[GetSlot];
        IntPtr hstring = IntPtr.Zero;
        if (get(factory, (uint)processId, 0 /* eRender */, 1 /* eMultimedia */, &hstring) != 0 || hstring == IntPtr.Zero) return null;
        try
        {
            var id = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hstring, out uint length), (int)length);
            if (id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)) id = id[IdPrefix.Length..];
            if (id.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^RenderSuffix.Length];
            return id.Length == 0 ? null : id;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>Routes an app's process to a device (an MMDevice id), or back to the default with null.</summary>
    public static void SetOutput(int processId, string? deviceId)
    {
        var factory = Factory.Value;
        if (factory == IntPtr.Zero) throw new PlatformNotSupportedException("Per-app output devices aren't available on this Windows.");
        var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)(*(IntPtr**)factory)[SetSlot];
        IntPtr hstring = IntPtr.Zero;
        if (deviceId != null)
        {
            string full = IdPrefix + deviceId + RenderSuffix;
            Marshal.ThrowExceptionForHR(WindowsCreateString(full, full.Length, out hstring));
        }
        try
        {
            // Both roles apps play in, as Settings does.
            Marshal.ThrowExceptionForHR(set(factory, (uint)processId, 0 /* eRender */, 1 /* eMultimedia */, hstring));
            Marshal.ThrowExceptionForHR(set(factory, (uint)processId, 0 /* eRender */, 0 /* eConsole */, hstring));
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    private static IntPtr Create()
    {
        if (WindowsCreateString(ClassName, ClassName.Length, out var name) != 0) return IntPtr.Zero;
        try
        {
            var iid = Environment.OSVersion.Version.Build >= 21390 ? FactoryIid : FactoryIidDownlevel;
            return RoGetActivationFactory(name, ref iid, out var factory) == 0 ? factory : IntPtr.Zero;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
