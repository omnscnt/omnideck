using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Omnideck.Core;

namespace Omnideck.ViewModels;

/// <summary>Live view of <see cref="AudioService"/>: master volume plus one row per app.</summary>
public sealed partial class MixerViewModel : ObservableObject
{
    private readonly AudioService _audio;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterVisibility), nameof(NoDeviceVisibility))]
    private MasterVolumeViewModel? _master;

    public ObservableCollection<AppVolumeViewModel> Apps { get; } = [];

    public Visibility MasterVisibility => Master != null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDeviceVisibility => Master == null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public MixerViewModel(AudioService audio, DispatcherQueue dispatcher)
    {
        _audio = audio;
        _dispatcher = dispatcher;
        Apps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EmptyVisibility));

        audio.AppAdded += (_, app) => _dispatcher.TryEnqueue(() => Add(app));
        audio.AppRemoved += (_, app) => _dispatcher.TryEnqueue(() => Remove(app));
        audio.DeviceChanged += (_, _) => _dispatcher.TryEnqueue(Reload);
        Reload();
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => _audio.GetOutputDevices();

    /// <summary>Switches the system default output; the mixer reloads itself on the resulting DeviceChanged.</summary>
    public void SelectOutput(string deviceId)
    {
        try
        {
            _audio.SetDefaultOutput(deviceId);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Device vanished between opening the picker and choosing it; nothing to switch to.
        }
    }

    // ---- per-app output -------------------------------------------------------------------------

    public static bool CanRouteApps => AudioService.CanRouteApps;

    /// <summary>The device id an app is sent to; null when it follows the default.</summary>
    public string? GetAppOutput(AppVolumeViewModel app) => _audio.GetAppOutput(app.App);

    public void SetAppOutput(AppVolumeViewModel app, string? deviceId)
    {
        try
        {
            _audio.SetAppOutput(app.App, deviceId);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            // Windows refused (the process just exited, or no such feature): leave it as it was.
        }
        RefreshRouting();
    }

    /// <summary>
    /// Re-reads where each app plays (on open: Windows Settings or the app itself may have changed it).
    /// An app sent to the device that is the default anyway shows no badge.
    /// </summary>
    public void RefreshRouting()
    {
        var devices = _audio.GetOutputDevices();
        foreach (var app in Apps) ReadRouting(app, devices);
    }

    private void ReadRouting(AppVolumeViewModel app, IReadOnlyList<AudioDeviceInfo> devices)
    {
        var id = _audio.GetAppOutput(app.App);
        var device = id == null ? null : devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        app.RoutedTo = device == null || device.IsDefault ? null : device.Name;
    }

    private DispatcherQueueTimer? _meterTimer;

    /// <summary>Level meters poll Core Audio only while the flyout is visible.</summary>
    public void StartMetering()
    {
        if (_meterTimer == null)
        {
            _meterTimer = _dispatcher.CreateTimer();
            _meterTimer.Interval = TimeSpan.FromMilliseconds(33);
            _meterTimer.Tick += (_, _) =>
            {
                Master?.TickMeter();
                foreach (var app in Apps) app.TickMeter();
            };
        }
        _meterTimer.Start();
    }

    public void StopMetering()
    {
        _meterTimer?.Stop();
        Master?.ResetMeter();
        foreach (var app in Apps) app.ResetMeter();
    }

    private void Reload()
    {
        Master?.Dispose();
        Master = _audio.Master is { } master ? new MasterVolumeViewModel(master, _dispatcher) : null;

        foreach (var vm in Apps) vm.Dispose();
        Apps.Clear();
        foreach (var app in _audio.Apps) Add(app);
    }

    private void Add(AudioApp app)
    {
        if (Apps.Any(vm => vm.App == app)) return;
        var vm = new AppVolumeViewModel(app, _dispatcher);

        // Apps in order of appearance; system sounds always last, like the Windows mixer.
        int index = app.IsSystemSounds
            ? Apps.Count
            : Apps.Count(a => !a.App.IsSystemSounds);
        Apps.Insert(index, vm);
        if (!app.IsSystemSounds) ReadRouting(vm, _audio.GetOutputDevices());
    }

    private void Remove(AudioApp app)
    {
        var vm = Apps.FirstOrDefault(a => a.App == app);
        if (vm == null) return;
        vm.Dispose();
        Apps.Remove(vm);
    }
}
