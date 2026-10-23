using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Omnideck.Core;
using Omnideck.Services;

namespace Omnideck.ViewModels;

public sealed partial class AppVolumeViewModel : VolumeViewModelBase, IDisposable
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackIconVisibility))]
    private ImageSource? _icon;

    public AudioApp App { get; }

    public string Name => App.DisplayName;

    public Visibility FallbackIconVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The device this app is sent to, when not the default (its name; null follows the default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputLabel), nameof(OutputToolTip), nameof(OutputOpacity))]
    private string? _routedTo;

    /// <summary>The default output's name: what an app that follows the default plays on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputLabel), nameof(OutputToolTip))]
    private string _defaultOutputName = "Default output";

    /// <summary>0 narrow (an icon) to 1 wide (the device's name), animated: the button grows and the two cross-fade.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputButtonWidth), nameof(OutputIconOpacity), nameof(OutputNameOpacity))]
    private double _expandProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputButtonWidth))]
    private double _expandedOutputWidth = MixerLayout.DefaultExpandedOutputWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputLabel))]
    private bool _fullNames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputColumnVisibility), nameof(RowPadding))]
    private bool _outputsEnabled = true;

    public string OutputLabel => FullNames ? RoutedTo ?? DefaultOutputName : MixerLayout.ShortName(RoutedTo ?? DefaultOutputName);

    public string OutputToolTip => RoutedTo != null ? $"Plays on {RoutedTo}" : $"Plays on the default output ({DefaultOutputName})";

    /// <summary>Its own device stands out; following the default stays quiet.</summary>
    public double OutputOpacity => RoutedTo != null ? 1 : 0.55;

    public Visibility OutputColumnVisibility => OutputsEnabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>System sounds can't be sent elsewhere: their spot stays empty, so the sliders still line up.</summary>
    public Visibility OutputButtonVisibility => App.IsSystemSounds ? Visibility.Collapsed : Visibility.Visible;

    public double OutputButtonWidth => MixerLayout.IconOutputWidth + (ExpandedOutputWidth - MixerLayout.IconOutputWidth) * ExpandProgress;
    public double OutputIconOpacity => Math.Clamp(1 - ExpandProgress * 2, 0, 1);
    public double OutputNameOpacity => Math.Clamp(ExpandProgress * 2 - 1, 0, 1);

    /// <summary>The output button sits as far from the card's right edge as from its top; a percentage needs more room.</summary>
    public Thickness RowPadding => new(10, 4, OutputsEnabled ? 4 : 12, 4);


    public string FallbackGlyph => App.IsSystemSounds ? "" : "";

    public AppVolumeViewModel(AudioApp app, DispatcherQueue dispatcher)
        : base(dispatcher, VolumeCurve.ToPosition(app.Volume), app.IsMuted)
    {
        App = app;
        app.VolumeChanged += OnCoreVolumeChanged;
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        if (App.IsSystemSounds) return;
        Icon = await AppIconLoader.LoadAsync(App.ExecutablePath);
    }

    protected override float ReadPeak() => App.Peak;

    protected override void ApplyVolume(float volume) => App.SetVolume(VolumeCurve.ToScalar(volume), UiContext);

    protected override float ToPosition(float volume) => VolumeCurve.ToPosition(volume);

    protected override double OutputScale => VolumeCurve.ToScalar((float)(Volume / 100.0));

    /// <summary>Re-reads the volume, e.g. after the volume curve was switched.</summary>
    public void Resync() => Sync(VolumeCurve.ToPosition(App.Volume), App.IsMuted);

    protected override void ApplyMute(bool mute) => App.SetMute(mute, UiContext);

    public void Dispose() => App.VolumeChanged -= OnCoreVolumeChanged;
}
