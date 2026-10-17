using System.Runtime.InteropServices;
using SoundDeck.Core.Interop;

namespace SoundDeck.Core;

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

    public float Volume => _volume.GetMasterVolume(out float v) == 0 ? v : 0f;

    public bool IsMuted => _volume.GetMute(out bool m) == 0 && m;

    public float Peak => _meter != null && _meter.GetPeakValue(out float p) == 0 ? p : 0f;

    public AudioSessionState State =>
        _control.GetState(out var s) == 0 ? s : AudioSessionState.Expired;

    public void SetVolume(float volume, Guid context) =>
        _volume.SetMasterVolume(Math.Clamp(volume, 0f, 1f), ref context);

    public void SetMute(bool mute, Guid context) => _volume.SetMute(mute, ref context);

    private void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _control.UnregisterAudioSessionNotification(_events); } catch (COMException) { }
        Marshal.ReleaseComObject(_control);
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
