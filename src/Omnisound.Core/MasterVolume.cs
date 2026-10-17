using System.Runtime.InteropServices;
using Omnisound.Core.Interop;

namespace Omnisound.Core;

/// <summary>Endpoint (device) volume of the default output device.</summary>
public sealed class MasterVolume : IVolumeControl, IDisposable
{
    private readonly IAudioEndpointVolume _endpoint;
    private readonly IAudioMeterInformation? _meter;
    private readonly EndpointCallback _callback;
    private int _disposed;

    public string DeviceId { get; }
    public string DeviceName { get; }

    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;

    internal MasterVolume(IMMDevice device, string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;

        var iid = AudioIids.AudioEndpointVolume;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, AudioIids.ClsCtxAll, IntPtr.Zero, out var endpoint));
        _endpoint = (IAudioEndpointVolume)endpoint;

        iid = AudioIids.AudioMeterInformation;
        if (device.Activate(ref iid, AudioIids.ClsCtxAll, IntPtr.Zero, out var meter) == 0)
            _meter = (IAudioMeterInformation)meter;

        _callback = new EndpointCallback(this);
        _endpoint.RegisterControlChangeNotify(_callback);
    }

    // After Dispose every member is a harmless no-op: the mixer and hotkeys may still hold an old master.
    public float Volume => Read(() => _endpoint.GetMasterVolumeLevelScalar(out float v) == 0 ? v : 0f, 0f);

    public bool IsMuted => Read(() => _endpoint.GetMute(out bool m) == 0 && m, false);

    public float Peak => _meter == null ? 0f : Read(() => _meter.GetPeakValue(out float p) == 0 ? p : 0f, 0f);

    public void SetVolume(float volume, Guid context = default) =>
        Read(() => _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0f, 1f), ref context), 0);

    public void AdjustVolume(float delta, Guid context = default) => SetVolume(Volume + delta, context);

    public void SetMute(bool mute, Guid context = default) => Read(() => _endpoint.SetMute(mute, ref context), 0);

    private T Read<T>(Func<T> call, T fallback)
    {
        if (Volatile.Read(ref _disposed) != 0) return fallback;
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return fallback; // the device went away
        }
    }

    public void ToggleMute(Guid context = default) => SetMute(!IsMuted, context);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _endpoint.UnregisterControlChangeNotify(_callback); } catch (COMException) { }
        // No Marshal.ReleaseComObject: another thread may be mid-call (meters poll every frame), and
        // releasing under it is an access violation. The GC releases the endpoint when unreferenced.
    }

    private sealed class EndpointCallback(MasterVolume owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            try
            {
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
                owner.VolumeChanged?.Invoke(owner, new VolumeChangedEventArgs(data.MasterVolume, data.Muted, data.EventContext));
            }
            catch
            {
                // Never unwind into Core Audio.
            }
            return 0;
        }
    }
}
