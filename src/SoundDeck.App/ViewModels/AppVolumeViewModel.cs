using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SoundDeck.Core;
using SoundDeck.Services;

namespace SoundDeck.ViewModels;

public sealed partial class AppVolumeViewModel : VolumeViewModelBase, IDisposable
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackIconVisibility))]
    private ImageSource? _icon;

    public AudioApp App { get; }

    public string Name => App.DisplayName;

    public Visibility FallbackIconVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;

    public string FallbackGlyph => App.IsSystemSounds ? "" : "";

    public AppVolumeViewModel(AudioApp app, DispatcherQueue dispatcher)
        : base(dispatcher, app.Volume, app.IsMuted)
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

    protected override void ApplyVolume(float volume) => App.SetVolume(volume, UiContext);

    protected override void ApplyMute(bool mute) => App.SetMute(mute, UiContext);

    public void Dispose() => App.VolumeChanged -= OnCoreVolumeChanged;
}
