using System.Collections.Concurrent;
using Omnideck.Core;
using Omnideck.Core.Hotkeys;
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
    private readonly Func<bool> _showExternal;
    private readonly object _gate = new();
    private readonly HashSet<AudioApp> _watchedApps = [];
    private readonly ConcurrentDictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private MasterVolume? _watchedMaster;

    public VolumeOsdFeed(AudioService audio, HotkeyActionRunner runner, VolumeOsd osd, Func<bool> showExternal)
    {
        _audio = audio;
        _osd = osd;
        _showExternal = showExternal;

        runner.VolumeChanged += (_, target) => _osd.Show(ContentFor(target));
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
        if (e.Context == VolumeViewModelBase.UiContext || !_showExternal()) return;
        _osd.Show(ContentFor(target));
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
