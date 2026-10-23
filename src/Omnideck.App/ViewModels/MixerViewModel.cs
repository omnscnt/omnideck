using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // ---- layout: output buttons and the wide mixer -------------------------------------------------

    /// <summary>Wide mixer: each app's output device by name, not just an icon. Remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph), nameof(ExpandToolTip))]
    private bool _isExpanded;

    /// <summary>0 narrow, 1 wide; animated between them, and everything that changes width follows it.</summary>
    [ObservableProperty]
    private double _expandProgress;

    /// <summary>Apps get output buttons (a setting; off also when Windows doesn't support it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputsVisibility), nameof(MasterPadding))]
    private bool _outputsEnabled = true;

    private double _expandedOutputWidth = MixerLayout.DefaultExpandedOutputWidth;
    private bool _fullNames;

    /// <summary>Device names with the adapter in brackets, in the output buttons and their lists.</summary>
    public bool FullNames => _fullNames;

    public Visibility OutputsVisibility => OutputsEnabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The master row reaches as far right as the rows below: their output buttons, or their percentages.</summary>
    public Thickness MasterPadding => new(19, 10, OutputsEnabled ? 13 : 21, 8);

    public string ExpandGlyph => IsExpanded ? "" : "";

    public string ExpandToolTip => IsExpanded ? "Narrow mixer" : "Show each app's output device";

    /// <summary>The window's width now (mid-animation too).</summary>
    public double WindowWidth => OutputsEnabled
        ? MixerLayout.NarrowWidth + (_expandedOutputWidth - MixerLayout.IconOutputWidth) * ExpandProgress
        : MixerLayout.NarrowWidth;

    private readonly Action<bool> _saveExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        _saveExpanded(value);
        ExpandToggled?.Invoke(); // the window eases ExpandProgress over, frame by frame
    }

    /// <summary>Raised when <see cref="IsExpanded"/> flips: the window animates <see cref="ExpandProgress"/> to it.</summary>
    public event Action? ExpandToggled;

    partial void OnExpandProgressChanged(double value)
    {
        foreach (var app in Apps) app.ExpandProgress = value;
        OnPropertyChanged(nameof(WindowWidth));
    }

    /// <summary>From the settings: output buttons on or off, their width when wide, short or full device names.</summary>
    public void Configure(bool outputsEnabled, double expandedOutputWidth, bool fullNames)
    {
        _expandedOutputWidth = Math.Clamp(expandedOutputWidth, MixerLayout.MinExpandedOutputWidth, MixerLayout.MaxExpandedOutputWidth);
        _fullNames = fullNames;
        OutputsEnabled = outputsEnabled;
        foreach (var app in Apps) SyncLayout(app);
        OnPropertyChanged(nameof(WindowWidth));
    }

    private void SyncLayout(AppVolumeViewModel app)
    {
        app.ExpandedOutputWidth = _expandedOutputWidth;
        app.FullNames = _fullNames;
        app.OutputsEnabled = OutputsEnabled;
        app.ExpandProgress = ExpandProgress;
    }

    public MixerViewModel(AudioService audio, DispatcherQueue dispatcher, bool expanded, Action<bool> saveExpanded)
    {
        _audio = audio;
        _dispatcher = dispatcher;
        _isExpanded = expanded;
        _expandProgress = expanded ? 1 : 0;
        _saveExpanded = saveExpanded;
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
        app.DefaultOutputName = devices.FirstOrDefault(d => d.IsDefault)?.Name ?? "Default output";
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
        SyncLayout(vm);

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

/// <summary>Widths shared by the mixer window and its rows, in DIPs.</summary>
public static class MixerLayout
{
    public const double NarrowWidth = 360, IconOutputWidth = 28;
    public const double DefaultExpandedOutputWidth = 128, MinExpandedOutputWidth = 80, MaxExpandedOutputWidth = 280;


    /// <summary>A device's name without the adapter Windows adds in brackets: "Headphones (CS43131 HIFI Audio)" → "Headphones".</summary>
    public static string ShortName(string name)
    {
        int bracket = name.IndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }
}
