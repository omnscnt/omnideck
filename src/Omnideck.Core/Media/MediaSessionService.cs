using Windows.Media.Control;

namespace Omnideck.Core.Media;

/// <summary>
/// System media sessions (GlobalSystemMediaTransportControls): the same play/pause/next that the
/// Windows media flyout and media keys drive, so it works with Spotify, browsers, players, etc.
/// </summary>
public sealed class MediaSessionService
{
    private readonly SemaphoreSlim _init = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    internal async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_manager != null) return _manager;
        await _init.WaitAsync().ConfigureAwait(false);
        try
        {
            return _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            return null;
        }
        finally
        {
            _init.Release();
        }
    }

    /// <summary>
    /// The session for an app (matched against its AppUserModelId, e.g. "Spotify.exe"), or Windows'
    /// current session when <paramref name="appName"/> is null.
    /// </summary>
    public async Task<GlobalSystemMediaTransportControlsSession?> FindSessionAsync(string? appName)
    {
        var manager = await GetManagerAsync().ConfigureAwait(false);
        if (manager == null) return null;
        if (string.IsNullOrEmpty(appName)) return manager.GetCurrentSession();

        return manager.GetSessions().FirstOrDefault(s =>
            s.SourceAppUserModelId.Contains(appName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Process-style name of the current session's app ("spotify", "chrome", "msedge"), for matching
    /// it to an audio app. Derived from the AppUserModelId: "Spotify.exe", "Chrome", "Pkg_xyz!Spotify".
    /// </summary>
    public async Task<string?> GetCurrentAppNameAsync()
    {
        var session = await FindSessionAsync(null).ConfigureAwait(false);
        if (session == null) return null;
        return AppNameFromId(session.SourceAppUserModelId);
    }

    /// <summary>"Spotify.exe", "Chrome", "Pkg_xyz!Spotify" → "spotify", "chrome", "spotify".</summary>
    public static string AppNameFromId(string id)
    {
        int bang = id.LastIndexOf('!');
        if (bang >= 0) id = id[(bang + 1)..];
        return Path.GetFileNameWithoutExtension(id).ToLowerInvariant();
    }

    /// <summary>
    /// What an app (by process name, "spotify") is playing right now, with where in the track it is;
    /// null when it has no session or isn't playing (paused, stopped). The cover is read only when asked:
    /// callers keep it for the same track.
    /// </summary>
    public async Task<MediaTrack?> GetPlayingTrackAsync(string processName, bool withCover)
    {
        var manager = await GetManagerAsync().ConfigureAwait(false);
        if (manager == null || string.IsNullOrEmpty(processName)) return null;
        try
        {
            var session = manager.GetSessions().FirstOrDefault(s => IsPlaying(s) && AppNameFromId(s.SourceAppUserModelId) == processName)
                ?? manager.GetSessions().FirstOrDefault(s => IsPlaying(s) && s.SourceAppUserModelId.Contains(processName, StringComparison.OrdinalIgnoreCase));
            return session == null ? null : await ReadTrackAsync(session, withCover).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null; // the session went away mid-read
        }
    }

    /// <summary>
    /// Waits for an app's session (Windows' current one when <paramref name="appName"/> is null) to change
    /// track, then reads the new one, cover included. Start it before sending the skip: it listens from the
    /// first moment. Null when nothing changes within <paramref name="timeout"/>.
    /// </summary>
    public async Task<(string AppName, MediaTrack Track)?> WaitForTrackChangeAsync(string? appName, TimeSpan timeout)
    {
        var session = await FindSessionAsync(appName).ConfigureAwait(false);
        if (session == null) return null;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset changedAt = default;
        Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> handler = (_, _) => changed.TrySetResult();
        try
        {
            session.MediaPropertiesChanged += handler;
            try
            {
                if (await Task.WhenAny(changed.Task, Task.Delay(timeout)).ConfigureAwait(false) != changed.Task) return null;
                changedAt = DateTimeOffset.Now;
                await Task.Delay(300).ConfigureAwait(false); // title, artist and cover arrive in separate steps
            }
            finally
            {
                session.MediaPropertiesChanged -= handler;
            }
            var track = await ReadTrackAsync(session, withCover: true, changedAt).ConfigureAwait(false);
            return track == null ? null : (AppNameFromId(session.SourceAppUserModelId), track);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <param name="changedAt">When the track changed, if just now: a timeline older than that is still the last track's, so it starts at 0.</param>
    private static async Task<MediaTrack?> ReadTrackAsync(GlobalSystemMediaTransportControlsSession session, bool withCover, DateTimeOffset? changedAt = null)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

        byte[]? cover = null;
        if (withCover && props.Thumbnail != null)
        {
            try
            {
                cover = await NowPlayingWatcher.ReadAllAsync(props.Thumbnail).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException)
            {
            }
        }

        // Apps update the timeline now and then (Spotify every few seconds); between updates it runs on.
        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        var position = timeline.Position - timeline.StartTime;
        if (changedAt is { } at && timeline.LastUpdatedTime < at) position = TimeSpan.Zero;
        else if (timeline.LastUpdatedTime.Year > 2000) position += DateTimeOffset.Now - timeline.LastUpdatedTime;
        if (duration > TimeSpan.Zero) position = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;

        string artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist : props.AlbumArtist ?? "";
        return new MediaTrack(session.SourceAppUserModelId, props.Title, artist, cover, position, duration);
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session) =>
        session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    public async Task<bool> TogglePlayPauseAsync(string? appName) =>
        await FindSessionAsync(appName).ConfigureAwait(false) is { } s && await s.TryTogglePlayPauseAsync();

    public async Task<bool> NextAsync(string? appName) =>
        await FindSessionAsync(appName).ConfigureAwait(false) is { } s && await s.TrySkipNextAsync();

    public async Task<bool> PreviousAsync(string? appName) =>
        await FindSessionAsync(appName).ConfigureAwait(false) is { } s && await s.TrySkipPreviousAsync();
}
