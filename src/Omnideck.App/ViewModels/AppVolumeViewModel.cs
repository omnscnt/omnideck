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

    /// <summary>The mixer is wide: the output button shows the device's name, not just an icon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputIconVisibility), nameof(OutputNameVisibility), nameof(OutputButtonWidth))]
    private bool _isExpanded;

    public string OutputLabel => MixerLayout.ShortName(RoutedTo ?? DefaultOutputName);

    public string OutputToolTip => RoutedTo != null ? $"Plays on {RoutedTo}" : $"Plays on the default output ({DefaultOutputName})";

    /// <summary>Its own device stands out; following the default stays quiet.</summary>
    public double OutputOpacity => RoutedTo != null ? 1 : 0.55;

    public Visibility OutputButtonVisibility => App.IsSystemSounds ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OutputIconVisibility => IsExpanded ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OutputNameVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public double OutputButtonWidth => IsExpanded ? MixerLayout.ExpandedOutputWidth : MixerLayout.IconOutputWidth;

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
