using System.Runtime.InteropServices;
using Omnideck.Core.Interop;

namespace Omnideck.Core;

/// <summary>
/// The sound core: default output device, its master volume and the live list of apps playing through it.
/// UI-agnostic — the mixer flyout and (later) global hotkeys both drive it.
/// Events fire on background threads; marshal to the UI thread yourself.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly object _gate = new();
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly DeviceNotifications _deviceNotifications;
    private readonly SessionNotifications _sessionNotifications;
    private readonly Dictionary<string, AudioApp> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AudioSession, AudioApp> _sessionOwners = [];
    private readonly Timer _reaper;
    private IAudioSessionManager2? _sessionManager;
    private MasterVolume? _master;
    private MasterVolume? _microphone;
    private int _micRebuildPending;
    private int _rebuildPending;
    private bool _disposed;

    /// <summary>The default output device changed (or appeared / disappeared). Master and Apps were rebuilt.</summary>
    public event EventHandler? DeviceChanged;

    /// <summary>The default recording device changed (or appeared / disappeared); <see cref="Microphone"/> was rebuilt.</summary>
    public event EventHandler? MicrophoneChanged;

    public event EventHandler<AudioApp>? AppAdded;
    public event EventHandler<AudioApp>? AppRemoved;

    public AudioService()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _deviceNotifications = new DeviceNotifications(this);
        _sessionNotifications = new SessionNotifications(this);
        _enumerator.RegisterEndpointNotificationCallback(_deviceNotifications);
        AttachToDefaultDevice();
        AttachMicrophone();
        _reaper = new Timer(_ => ReapDeadProcesses(), null, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5));
    }

    public MasterVolume? Master
    {
        get { lock (_gate) return _master; }
    }

    /// <summary>The default recording device's volume and mute; null when there is none.</summary>
    public MasterVolume? Microphone
    {
        get { lock (_gate) return _microphone; }
    }

    public IReadOnlyList<AudioApp> Apps
    {
        get { lock (_gate) return _apps.Values.ToArray(); }
    }

    /// <summary>Find an app by process name ("spotify", "spotify.exe") or full executable path.</summary>
    public AudioApp? FindApp(string processNameOrPath)
    {
        var name = Path.GetFileNameWithoutExtension(processNameOrPath).ToLowerInvariant();
        lock (_gate)
        {
            if (_apps.TryGetValue(processNameOrPath, out var byPath)) return byPath;
            return _apps.Values.FirstOrDefault(a => a.ProcessName == name);
        }
    }

    /// <summary>
    /// The app that owns the given process — matched by executable, so the foreground window's process
    /// finds the app even when audio comes from a sibling helper process (browsers, Electron).
    /// </summary>
    public AudioApp? FindAppByProcessId(int processId)
    {
        lock (_gate)
        {
            var direct = _apps.Values.FirstOrDefault(a => a.ProcessIds.Contains(processId));
            if (direct != null) return direct;
        }
        var path = ProcessInfo.GetExecutablePath(processId);
        if (path == null) return null;
        lock (_gate) return _apps.GetValueOrDefault(path);
    }

    // ---- output devices ----------------------------------------------------------------------

    /// <summary>All active output devices, with the current default marked.</summary>
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        string? defaultId = null;
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var defaultDevice) == 0 && defaultDevice != null)
        {
            defaultDevice.GetId(out defaultId);
            Marshal.ReleaseComObject(defaultDevice);
        }

        var result = new List<AudioDeviceInfo>();
        if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, 1 /* DEVICE_STATE_ACTIVE */, out var collection) != 0) return result;
        try
        {
            collection.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device == null) continue;
                device.GetId(out var id);
                result.Add(new AudioDeviceInfo(id, ReadFriendlyName(device) ?? id, id == defaultId));
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }
        return result;
    }

    /// <summary>
    /// Makes the device the default output for every role (console, multimedia, communications),
    /// like "Set as default" in Windows. <see cref="DeviceChanged"/> follows once Windows applies it.
    /// </summary>
    public void SetDefaultOutput(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClientComObject();
        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    // ---- device lifetime -------------------------------------------------------------------

    private void AttachToDefaultDevice()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0 || device == null)
            return; // No output device at all.

        device.GetId(out var deviceId);
        var master = new MasterVolume(device, deviceId, ReadFriendlyName(device) ?? "Output device");

        var iid = AudioIids.AudioSessionManager2;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, AudioIids.ClsCtxAll, IntPtr.Zero, out var managerObject));
        var manager = (IAudioSessionManager2)managerObject;

        lock (_gate)
        {
            _master = master;
            _sessionManager = manager;
        }

        manager.RegisterSessionNotification(_sessionNotifications);

        // Enumerating also "arms" session notifications (a long-standing Core Audio quirk).
        if (manager.GetSessionEnumerator(out var sessions) == 0)
        {
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var sessionObject) == 0)
                    AddSession(sessionObject, raiseEvent: false);
            }
            Marshal.ReleaseComObject(sessions);
        }
        Marshal.ReleaseComObject(device);
    }

    private void DetachFromDevice()
    {
        IAudioSessionManager2? manager;
        MasterVolume? master;
        AudioSession[] sessions;
        lock (_gate)
        {
            manager = _sessionManager;
            master = _master;
            sessions = _sessionOwners.Keys.ToArray();
            _sessionManager = null;
            _master = null;
            _sessionOwners.Clear();
            _apps.Clear();
        }

        foreach (var session in sessions) session.Dispose();
        master?.Dispose();
        if (manager != null)
        {
            try { manager.UnregisterSessionNotification(_sessionNotifications); } catch (COMException) { }
            // Left to the GC rather than released here, like the sessions and master (see AudioSession.Dispose).
        }
    }

    /// <summary>The default recording device (console role: what apps record from unless told otherwise).</summary>
    private void AttachMicrophone()
    {
        MasterVolume? microphone = null;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out var device) == 0 && device != null)
            {
                device.GetId(out var deviceId);
                microphone = new MasterVolume(device, deviceId, ReadFriendlyName(device) ?? "Microphone");
                Marshal.ReleaseComObject(device);
            }
        }
        catch (COMException) { }
        MasterVolume? old;
        lock (_gate)
        {
            old = _microphone;
            _microphone = microphone;
        }
        old?.Dispose();
    }

    private void ScheduleMicrophoneRebuild()
    {
        if (Interlocked.Exchange(ref _micRebuildPending, 1) == 1) return;
        Task.Delay(150).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _micRebuildPending, 0);
            if (_disposed) return;
            AttachMicrophone();
            MicrophoneChanged?.Invoke(this, EventArgs.Empty);
        }, TaskScheduler.Default);
    }

    private void ScheduleRebuild()
    {
        // Device callbacks must not call back into the enumerator; hop off the notification thread and coalesce bursts.
        if (Interlocked.Exchange(ref _rebuildPending, 1) == 1) return;
        Task.Delay(150).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _rebuildPending, 0);
            if (_disposed) return;
            DetachFromDevice();
            try { AttachToDefaultDevice(); }
            catch (COMException) { /* device vanished mid-attach; next notification retries */ }
            DeviceChanged?.Invoke(this, EventArgs.Empty);
        }, TaskScheduler.Default);
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        try
        {
            var key = PropertyKey.DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) != 0) return null;
            var name = value.AsString();
            NativeMethods.PropVariantClear(ref value);
            return name;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // ---- sessions ----------------------------------------------------------------------------

    private void AddSession(object sessionObject, bool raiseEvent)
    {
        var session = AudioSession.TryCreate(sessionObject);
        if (session == null) return;

        if (!session.IsSystemSounds && !ProcessInfo.IsAlive(session.ProcessId))
        {
            session.Dispose(); // Stale inactive session of a process that already exited.
            return;
        }

        string? exePath = session.IsSystemSounds ? null : ProcessInfo.GetExecutablePath(session.ProcessId);
        string key = session.IsSystemSounds ? "#system" : exePath ?? $"#pid:{session.ProcessId}";

        AudioApp app;
        bool created = false;
        lock (_gate)
        {
            if (_sessionManager == null || _apps.Values.Any(a => a.Contains(session.InstanceId)))
            {
                session.Dispose();
                return;
            }

            if (!_apps.TryGetValue(key, out app!))
            {
                var name = session.IsSystemSounds
                    ? "System sounds"
                    : !string.IsNullOrWhiteSpace(session.SessionDisplayName) && !session.SessionDisplayName.StartsWith('@')
                        ? session.SessionDisplayName
                        : ProcessInfo.GetFriendlyName(exePath, session.ProcessId);
                app = new AudioApp(key, name, exePath, session.IconPath, session.IsSystemSounds);
                _apps[key] = app;
                created = true;
            }
            _sessionOwners[session] = app;
        }

        app.Add(session);
        session.Ended += (_, _) => ThreadPool.QueueUserWorkItem(_ => RemoveSession(session));
        if (created && raiseEvent) AppAdded?.Invoke(this, app);
    }

    private void RemoveSession(AudioSession session)
    {
        AudioApp? emptied = null;
        lock (_gate)
        {
            if (!_sessionOwners.Remove(session, out var app)) return;
            app.Remove(session);
            if (app.IsEmpty && _apps.Remove(app.Key)) emptied = app;
        }
        session.Dispose();
        if (emptied != null) AppRemoved?.Invoke(this, emptied);
    }

    private void ReapDeadProcesses()
    {
        AudioSession[] candidates;
        lock (_gate) candidates = _sessionOwners.Keys.Where(s => !s.IsSystemSounds).ToArray();

        foreach (var session in candidates)
        {
            if (!ProcessInfo.IsAlive(session.ProcessId) || session.State == AudioSessionState.Expired)
                RemoveSession(session);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reaper.Dispose();
        try { _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifications); } catch (COMException) { }
        DetachFromDevice();
        Microphone?.Dispose();
        Marshal.ReleaseComObject(_enumerator);
    }

    // ---- COM callbacks -----------------------------------------------------------------------

    private sealed class SessionNotifications(AudioService owner) : IAudioSessionNotification
    {
        public int OnSessionCreated(IntPtr newSession)
        {
            if (newSession == IntPtr.Zero) return 0;
            var sessionObject = Marshal.GetObjectForIUnknown(newSession);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { owner.AddSession(sessionObject, raiseEvent: true); }
                catch (COMException) { }
            });
            return 0;
        }
    }

    private sealed class DeviceNotifications(AudioService owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia) owner.ScheduleRebuild();
            else if (flow == EDataFlow.Capture && role == ERole.Console) owner.ScheduleMicrophoneRebuild();
            return 0;
        }
    }
}
