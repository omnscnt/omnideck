using System.Collections.Concurrent;
using Omnideck.Core;
using Omnideck.Core.Hotkeys;
using Omnideck.Core.Media;
using Omnideck.Services;
using Omnideck.ViewModels;

namespace Omnideck.Osd;

/// <summary>
/// Decides what the volume indicator shows: every volume change made by a hotkey, and (when the
/// setting is on) changes made anywhere else — media keys, the Windows mixer, apps themselves —
/// except those from our own mixer, where the slider already shows the value.
/// </summary>
public sealed class VolumeOsdFeed
{
    private const int IconPixels = 64;

    private readonly AudioService _audio;
    private readonly VolumeOsd _osd;
    private readonly Func<VolumeOsdSettings> _settings;
    private readonly object _gate = new();
    private readonly HashSet<AudioApp> _watchedApps = [];
    private readonly ConcurrentDictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private MasterVolume? _watchedMaster;

    // What apps are playing, read once and reused for a moment: a held hotkey changes the volume many times a second.
    private const double TrackFresh = 2, CoverPixels = 96;
    private readonly MediaSessionService _media;
    private readonly Func<string?> _widgetApp;
    private readonly ConcurrentDictionary<string, (double At, Task<OsdMedia?> Read)> _tracks = new();
    private (string Key, byte[]? Pixels) _cover; // the last cover, decoded, for "AppId|Title"

    /// <param name="widgetApp">The app whose track the taskbar widget shows right now, if any: no card for that one.</param>
    public VolumeOsdFeed(AudioService audio, HotkeyActionRunner runner, VolumeOsd osd, Func<VolumeOsdSettings> settings, MediaSessionService media, Func<string?> widgetApp)
    {
        _audio = audio;
        _osd = osd;
        _settings = settings;
        _media = media;
        _widgetApp = widgetApp;

        runner.VolumeChanged += (_, target) => { if (_settings().Enabled) Show(target); };
        runner.TrackSwitched += (app, track) => _ = OnTrackSwitchedAsync(app, track);
        audio.AppAdded += (_, app) => Watch(app);
        audio.AppRemoved += (_, app) => { lock (_gate) _watchedApps.Remove(app); };
        audio.DeviceChanged += (_, _) => WatchAll(); // a new device brings a new master and new app objects
        WatchAll();
    }

    /// <summary>Shows the indicator for the master volume, even when it is switched off (settings preview).</summary>
    public void Preview()
    {
        if (_audio.Master is { } master) _osd.Show(ContentFor(master), force: true);
    }

    private void WatchAll()
    {
        lock (_gate)
        {
            _watchedApps.Clear();
            var master = _audio.Master;
            if (master != null && !ReferenceEquals(master, _watchedMaster))
            {
                _watchedMaster = master;
                master.VolumeChanged += (_, e) => OnExternalChange(master, e);
            }
        }
        foreach (var app in _audio.Apps) Watch(app);
    }

    private void Watch(AudioApp app)
    {
        lock (_gate)
            if (!_watchedApps.Add(app)) return;
        app.VolumeChanged += (_, e) => OnExternalChange(app, e);
    }

    private void OnExternalChange(IVolumeControl target, VolumeChangedEventArgs e)
    {
        var settings = _settings();
        if (e.Context == VolumeViewModelBase.UiContext || !settings.Enabled || !settings.ShowExternalChanges) return;
        Show(target);
    }

    /// <summary>
    /// An app's volume comes with a card for what it plays, when it is playing (not paused) and the taskbar
    /// widget isn't already showing it. The track is read asynchronously, so the first change of a burst
    /// waits for it (a few milliseconds); the rest reuse it.
    /// </summary>
    private void Show(IVolumeControl target)
    {
        if (target is not AudioApp app || app.ProcessName.Length == 0 || !_settings().ShowNowPlaying || !CardWanted(app.ProcessName))
        {
            _osd.Show(ContentFor(target));
            return;
        }
        double now = OsdMedia.Now;
        var entry = _tracks.AddOrUpdate(app.ProcessName,
            _ => (now, ReadTrackAsync(app.ProcessName)),
            (_, old) => now - old.At < TrackFresh ? old : (now, ReadTrackAsync(app.ProcessName)));
        if (entry.Read.IsCompletedSuccessfully) _osd.Show(ContentFor(app) with { Media = entry.Read.Result });
        else _ = ShowWhenReadAsync(app, entry.Read);
    }

    /// <summary>
    /// No card where the track is already in sight: the taskbar widget showing that app, or (when set)
    /// the app's own window in front.
    /// </summary>
    private bool CardWanted(string processName) =>
        _widgetApp() != processName && !(_settings().NowPlayingSkipActive && ForegroundProcessName() == processName);

    private static string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // it just exited
        }
    }

    /// <summary>A hotkey skipped to another track: the card alone, with the new track.</summary>
    private async Task OnTrackSwitchedAsync(string processName, MediaTrack track)
    {
        var settings = _settings();
        if (!settings.ShowNowPlaying || !settings.NowPlayingOnTrackSwitch || !CardWanted(processName)) return;
        string key = track.AppId + "|" + track.Title;
        byte[]? pixels = track.Cover is { } bytes ? await DecodeCoverAsync(bytes, (int)CoverPixels).ConfigureAwait(false) : null;
        if (pixels != null) _cover = (key, pixels);
        _tracks.TryRemove(processName, out _); // a volume change next shows the new track, not the cached one
        var media = new OsdMedia(track.Title, track.Artist, pixels, (int)CoverPixels, track.Position, track.Duration, OsdMedia.Now);
        _osd.Show(new OsdContent("track:" + processName, null, 0, 0, false, Media: media, CardOnly: true));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private async Task ShowWhenReadAsync(AudioApp app, Task<OsdMedia?> read)
    {
        OsdMedia? media = null;
        try
        {
            media = await read.WaitAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Slow or failing media service: the volume alone.
        }
        _osd.Show(ContentFor(app) with { Media = media });
    }

    private async Task<OsdMedia?> ReadTrackAsync(string processName)
    {
        var cached = _cover;
        // The cover is decoded once per track; the timeline is read every time.
        var track = await _media.GetPlayingTrackAsync(processName, withCover: false).ConfigureAwait(false);
        if (track == null) return null;
        string key = track.AppId + "|" + track.Title;
        byte[]? pixels = cached.Key == key ? cached.Pixels : null;
        if (cached.Key != key)
        {
            var withCover = await _media.GetPlayingTrackAsync(processName, withCover: true).ConfigureAwait(false);
            if (withCover?.Cover is { } bytes) pixels = await DecodeCoverAsync(bytes, (int)CoverPixels).ConfigureAwait(false);
            if (withCover?.Cover != null || withCover == null) _cover = (key, pixels); // a cover that hasn't arrived yet is tried again next time
        }
        return new OsdMedia(track.Title, track.Artist, pixels, (int)CoverPixels, track.Position, track.Duration, OsdMedia.Now);
    }

    /// <summary>The cover scaled to fill a square (cropped in the middle if it isn't one), as premultiplied BGRA.</summary>
    private static async Task<byte[]?> DecodeCoverAsync(byte[] bytes, int size)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            double fill = Math.Max(size / (double)decoder.PixelWidth, size / (double)decoder.PixelHeight);
            uint w = (uint)Math.Max(size, Math.Round(decoder.PixelWidth * fill)), h = (uint)Math.Max(size, Math.Round(decoder.PixelHeight * fill));
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = w, ScaledHeight = h, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = (w - (uint)size) / 2, Y = (h - (uint)size) / 2, Width = (uint)size, Height = (uint)size },
            };
            var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform, Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();
            return pixels.Length == size * size * 4 ? pixels : null;
        }
        catch (Exception)
        {
            return null; // unsupported or broken image: the placeholder
        }
    }

    private OsdContent ContentFor(IVolumeControl target) => target switch
    {
        AudioApp app => new OsdContent(app.Key, IconFor(app.ExecutablePath), IconPixels, VolumeCurve.ToPosition(app.Volume), app.IsMuted),
        MasterVolume master => new OsdContent("master:" + master.DeviceId, null, 0, master.Volume, master.IsMuted),
        AppGroup group => new OsdContent("others", null, 0, VolumeCurve.ToPosition(group.Volume), group.IsMuted),
        _ => new OsdContent("?", null, 0, target.Volume, target.IsMuted),
    };

    private byte[]? IconFor(string? exePath) =>
        exePath == null ? null : _icons.GetOrAdd(exePath, path => AppIconLoader.ExtractPixels(path, IconPixels));
}
