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

    public float Volume => _endpoint.GetMasterVolumeLevelScalar(out float v) == 0 ? v : 0f;

    public bool IsMuted => _endpoint.GetMute(out bool m) == 0 && m;

    public float Peak => _meter != null && _meter.GetPeakValue(out float p) == 0 ? p : 0f;

    public void SetVolume(float volume, Guid context = default) =>
        _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0f, 1f), ref context);

    public void AdjustVolume(float delta, Guid context = default) => SetVolume(Volume + delta, context);

    public void SetMute(bool mute, Guid context = default) => _endpoint.SetMute(mute, ref context);

    public void ToggleMute(Guid context = default) => SetMute(!IsMuted, context);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _endpoint.UnregisterControlChangeNotify(_callback); } catch (COMException) { }
        Marshal.ReleaseComObject(_endpoint);
        if (_meter != null) Marshal.ReleaseComObject(_meter);
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
