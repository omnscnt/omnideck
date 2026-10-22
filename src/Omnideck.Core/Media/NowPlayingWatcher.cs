using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Omnideck.Core.Media;

/// <summary>What the followed media session is playing (title, artist, cover, state).</summary>
public sealed record NowPlaying(
    string AppId,
    string AppName,
    string Title,
    string Artist,
    bool IsPlaying,
    byte[]? Thumbnail,
    bool CanPrevious,
    bool CanNext);

/// <summary>
/// Follows one media session and raises <see cref="Changed"/> with a fresh snapshot whenever the
/// followed session, its track or its playback state changes (null when there is none).
///
/// The session is picked among the apps the <see cref="Filter"/> allows: Windows' current session
/// when allowed, otherwise an allowed one that is playing, otherwise the last one followed (so a
/// paused track stays). Events are coalesced: apps such as Spotify update title, cover and state in
/// separate steps.
/// </summary>
public sealed class NowPlayingWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly MediaSessionService _media;
    private readonly object _gate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _attached = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaAppFilter _filter = MediaAppFilter.AllApps;
    private Timer? _debounce;
    private int _version;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action<NowPlaying?>? Changed;

    public NowPlaying? Current { get; private set; }

    public NowPlayingWatcher(MediaSessionService media) => _media = media;

    public MediaAppFilter Filter
    {
        get { lock (_gate) return _filter; }
        set
        {
            lock (_gate) _filter = value;
            Schedule();
        }
    }

    public async Task StartAsync()
    {
        var manager = await _media.GetManagerAsync().ConfigureAwait(false);
        if (manager == null) return;
        lock (_gate)
        {
            if (_disposed || _manager != null) return;
            _manager = manager;
        }
        manager.CurrentSessionChanged += OnSessionsChanged;
        manager.SessionsChanged += OnSessionsChanged;
        OnSessionsChanged(manager, null);
    }

    // ---- controls for the followed session -------------------------------------------------------

    public Task<bool> TogglePlayPauseAsync() => WithSession(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task<bool> NextAsync() => WithSession(s => s.TrySkipNextAsync().AsTask());
    public Task<bool> PreviousAsync() => WithSession(s => s.TrySkipPreviousAsync().AsTask());

    private async Task<bool> WithSession(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        try
        {
            return session != null && await action(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- session tracking ------------------------------------------------------------------------

    /// <summary>Listens to every session: a session in the background starting to play can take over.</summary>
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object? args)
    {
        List<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = sender.GetSessions().ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return; // the media service is restarting; the next event brings the new list
        }
        lock (_gate)
        {
            foreach (var old in _attached.Where(a => !sessions.Contains(a)).ToList())
            {
                old.MediaPropertiesChanged -= OnSessionChanged;
                old.PlaybackInfoChanged -= OnSessionChanged;
                _attached.Remove(old);
            }
            foreach (var session in sessions.Where(s => !_attached.Contains(s)))
            {
                session.MediaPropertiesChanged += OnSessionChanged;
                session.PlaybackInfoChanged += OnSessionChanged;
                _attached.Add(session);
            }
        }
        Schedule();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ => _ = RefreshAsync(), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private GlobalSystemMediaTransportControlsSession? Choose()
    {
        if (_manager == null) return null;
        MediaAppFilter filter;
        GlobalSystemMediaTransportControlsSession? previous;
        lock (_gate)
        {
            filter = _filter;
            previous = _session;
        }

        var candidates = _manager.GetSessions().Where(s => filter.Allows(MediaSessionService.AppNameFromId(s.SourceAppUserModelId))).ToList();
        if (candidates.Count == 0) return null;
        var current = _manager.GetCurrentSession();
        bool anyPlaying = candidates.Any(IsPlaying);

        bool Keep(GlobalSystemMediaTransportControlsSession? s) => s != null && candidates.Contains(s) && (IsPlaying(s) || !anyPlaying);
        if (Keep(current)) return current;
        if (Keep(previous)) return previous;
        return candidates.FirstOrDefault(IsPlaying) ?? candidates[0];
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        int version;
        lock (_gate) version = ++_version;

        NowPlaying? snapshot = null;
        try
        {
            var session = Choose();
            lock (_gate) _session = session;
            if (session != null) snapshot = await ReadAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A session went away mid-read; the next event brings the new state.
        }

        lock (_gate)
        {
            if (version != _version || _disposed) return; // a newer refresh is on its way
            Current = snapshot;
        }
        Changed?.Invoke(snapshot);
    }

    private async Task<NowPlaying?> ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

        byte[]? thumbnail = null;
        if (props.Thumbnail != null)
        {
            try
            {
                thumbnail = await ReadAllAsync(props.Thumbnail).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException)
            {
            }
        }
        // Cover art often arrives a moment after the title: keep the previous one for the same track.
        var previous = Current;
        if (thumbnail == null && previous != null && previous.AppId == session.SourceAppUserModelId && previous.Title == props.Title)
            thumbnail = previous.Thumbnail;

        var controls = playback?.Controls;
        string artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist : props.AlbumArtist ?? "";
        return new NowPlaying(
            session.SourceAppUserModelId,
            MediaSessionService.AppNameFromId(session.SourceAppUserModelId),
            props.Title,
            artist,
            playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            thumbnail,
            controls?.IsPreviousEnabled ?? true,
            controls?.IsNextEnabled ?? true);
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        var buffer = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(buffer);
        return buffer;
    }

    /// <summary>App names of the media sessions open right now (for picking apps in the filter).</summary>
    public IReadOnlyList<string> SessionAppNames()
    {
        try
        {
            return _manager?.GetSessions().Select(s => MediaSessionService.AppNameFromId(s.SourceAppUserModelId)).Distinct().ToList() ?? [];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
            foreach (var session in _attached)
            {
                session.MediaPropertiesChanged -= OnSessionChanged;
                session.PlaybackInfoChanged -= OnSessionChanged;
            }
            _attached.Clear();
        }
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnSessionsChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
    }
}
