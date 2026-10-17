using System.Runtime.InteropServices;
using Omnisound.Core.Interop;

namespace Omnisound.Core;

public sealed class VolumeChangedEventArgs(float volume, bool isMuted, Guid context) : EventArgs
{
    public float Volume { get; } = volume;
    public bool IsMuted { get; } = isMuted;

    /// <summary>The event context passed by whoever made the change (Guid.Empty for unknown/external).</summary>
    public Guid Context { get; } = context;
}

/// <summary>One WASAPI audio session. Apps can own several; <see cref="AudioApp"/> groups them.</summary>
internal sealed class AudioSession : IDisposable
{
    private readonly IAudioSessionControl2 _control;
    private readonly ISimpleAudioVolume _volume;
    private readonly IAudioMeterInformation? _meter;
    private readonly SessionEvents _events;
    private int _disposed;

    public string InstanceId { get; }
    public int ProcessId { get; }
    public bool IsSystemSounds { get; }
    public string? SessionDisplayName { get; }
    public string? IconPath { get; }

    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;
    public event EventHandler? Ended;

    private AudioSession(IAudioSessionControl2 control)
    {
        _control = control;
        _volume = (ISimpleAudioVolume)control;
        _meter = control as IAudioMeterInformation;

        control.GetSessionInstanceIdentifier(out var instanceId);
        InstanceId = instanceId ?? Guid.NewGuid().ToString();
        control.GetProcessId(out uint pid);
        ProcessId = (int)pid;
        IsSystemSounds = control.IsSystemSoundsSession() == 0;
        if (control.GetDisplayName(out var name) == 0) SessionDisplayName = name;
        if (control.GetIconPath(out var icon) == 0) IconPath = icon;

        _events = new SessionEvents(this);
        control.RegisterAudioSessionNotification(_events);
    }

    public static AudioSession? TryCreate(object comObject)
    {
        if (comObject is not IAudioSessionControl2 control) return null;
        try
        {
            if (control.GetState(out var state) != 0 || state == AudioSessionState.Expired) return null;
            return new AudioSession(control);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    // After Dispose every member is a harmless no-op: other threads (the mixer's meters) may still hold us.
    public float Volume => Read(() => _volume.GetMasterVolume(out float v) == 0 ? v : 0f, 0f);

    public bool IsMuted => Read(() => _volume.GetMute(out bool m) == 0 && m, false);

    public float Peak => _meter == null ? 0f : Read(() => _meter.GetPeakValue(out float p) == 0 ? p : 0f, 0f);

    public AudioSessionState State =>
        Read(() => _control.GetState(out var s) == 0 ? s : AudioSessionState.Expired, AudioSessionState.Expired);

    public void SetVolume(float volume, Guid context) =>
        Read(() => _volume.SetMasterVolume(Math.Clamp(volume, 0f, 1f), ref context), 0);

    public void SetMute(bool mute, Guid context) => Read(() => _volume.SetMute(mute, ref context), 0);

    private T Read<T>(Func<T> call, T fallback)
    {
        if (Volatile.Read(ref _disposed) != 0) return fallback;
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return fallback; // the session or its device went away
        }
    }

    private void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _control.UnregisterAudioSessionNotification(_events); } catch (COMException) { }
        // No Marshal.ReleaseComObject: releasing while another thread is inside a call on this object
        // (e.g. a meter read during a device switch) is an access violation. The GC releases it once
        // nothing references the session any more.
    }

    private sealed class SessionEvents(AudioSession owner) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(string name, ref Guid eventContext) => 0;
        public int OnIconPathChanged(string path, ref Guid eventContext) => 0;

        public int OnSimpleVolumeChanged(float volume, bool mute, ref Guid eventContext)
        {
            owner.VolumeChanged?.Invoke(owner, new VolumeChangedEventArgs(volume, mute, eventContext));
            return 0;
        }

        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumes, uint changedChannel, ref Guid eventContext) => 0;
        public int OnGroupingParamChanged(ref Guid groupingParam, ref Guid eventContext) => 0;

        public int OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.Expired) owner.RaiseEnded();
            return 0;
        }

        public int OnSessionDisconnected(AudioSessionDisconnectReason reason)
        {
            owner.RaiseEnded();
            return 0;
        }
    }
}
