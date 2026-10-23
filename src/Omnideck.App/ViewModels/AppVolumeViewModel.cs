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
    [NotifyPropertyChangedFor(nameof(RoutedVisibility), nameof(ToolTip))]
    private string? _routedTo;

    public Visibility RoutedVisibility => RoutedTo != null ? Visibility.Visible : Visibility.Collapsed;

    public string ToolTip => RoutedTo != null ? $"{Name}\nPlays on {RoutedTo}" : Name;

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
