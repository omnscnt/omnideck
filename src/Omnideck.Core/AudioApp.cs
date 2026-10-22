namespace Omnideck.Core;

/// <summary>
/// An application as the user sees it: every audio session of the same executable
/// (all Chrome/Discord helper processes, several streams of one process) folded into one volume.
/// All members are thread-safe; events fire on Core Audio worker threads.
/// </summary>
public sealed class AudioApp : IVolumeControl
{
    private readonly object _gate = new();
    private readonly List<AudioSession> _sessions = [];

    public string Key { get; }
    public string DisplayName { get; }
    public string? ExecutablePath { get; }
    public string? IconPath { get; }
    public bool IsSystemSounds { get; }

    /// <summary>File name of the executable without extension, lowercase (e.g. "spotify"). Empty for system sounds.</summary>
    public string ProcessName { get; }

    /// <summary>Raised when the volume or mute state changes, from anywhere (this app, the OS mixer, the app itself).</summary>
    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;

    internal AudioApp(string key, string displayName, string? executablePath, string? iconPath, bool isSystemSounds)
    {
        Key = key;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        IconPath = iconPath;
        IsSystemSounds = isSystemSounds;
        ProcessName = executablePath is null ? "" : Path.GetFileNameWithoutExtension(executablePath).ToLowerInvariant();
    }

    public IReadOnlyList<int> ProcessIds
    {
        get { lock (_gate) return _sessions.Select(s => s.ProcessId).Distinct().ToArray(); }
    }

    public float Volume
    {
        get { lock (_gate) return _sessions.Count > 0 ? _sessions[0].Volume : 0f; }
    }

    public bool IsMuted
    {
        get { lock (_gate) return _sessions.Count > 0 && _sessions.All(s => s.IsMuted); }
    }

    /// <summary>Current peak level 0..1 (loudest session), for level meters.</summary>
    public float Peak
    {
        get { lock (_gate) return _sessions.Count == 0 ? 0f : _sessions.Max(s => s.Peak); }
    }

    public void SetVolume(float volume, Guid context = default)
    {
        foreach (var session in Snapshot()) Try(() => session.SetVolume(volume, context));
    }

    /// <summary>Steps by <paramref name="delta"/> of the slider (see <see cref="VolumeCurve"/>), as the mixer shows it.</summary>
    public void AdjustVolume(float delta, Guid context = default) =>
        SetVolume(VolumeCurve.ToScalar(VolumeCurve.ToPosition(Volume) + delta), context);

    public void SetMute(bool mute, Guid context = default)
    {
        foreach (var session in Snapshot()) Try(() => session.SetMute(mute, context));
    }

    public void ToggleMute(Guid context = default) => SetMute(!IsMuted, context);

    internal bool IsEmpty
    {
        get { lock (_gate) return _sessions.Count == 0; }
    }

    internal bool Contains(string instanceId)
    {
        lock (_gate) return _sessions.Any(s => s.InstanceId == instanceId);
    }

    internal void Add(AudioSession session)
    {
        session.VolumeChanged += OnSessionVolumeChanged;
        float? sync = null;
        bool mute = false;
        lock (_gate)
        {
            // Keep the group consistent: a new stream of an app we already show adopts its current level.
            if (_sessions.Count > 0)
            {
                sync = _sessions[0].Volume;
                mute = _sessions[0].IsMuted;
            }
            _sessions.Add(session);
        }
        if (sync is float level && Math.Abs(session.Volume - level) > 0.001f)
        {
            Try(() => session.SetVolume(level, Guid.Empty));
            Try(() => session.SetMute(mute, Guid.Empty));
        }
    }

    internal void Remove(AudioSession session)
    {
        session.VolumeChanged -= OnSessionVolumeChanged;
        lock (_gate) _sessions.Remove(session);
    }

    internal AudioSession[] Snapshot()
    {
        lock (_gate) return _sessions.ToArray();
    }

    private void OnSessionVolumeChanged(object? sender, VolumeChangedEventArgs e)
    {
        try
        {
            VolumeChanged?.Invoke(this, e);
        }
        catch
        {
            // Never let a subscriber's exception unwind into Core Audio.
        }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (System.Runtime.InteropServices.COMException) { /* session died mid-call */ }
        catch (System.Runtime.InteropServices.InvalidComObjectException) { }
    }

    public override string ToString() => $"{DisplayName} ({Key})";
}
