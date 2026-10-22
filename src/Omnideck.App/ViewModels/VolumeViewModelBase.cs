using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Omnideck.Core;

namespace Omnideck.ViewModels;

/// <summary>Shared slider/mute logic for the master row and app rows.</summary>
public abstract partial class VolumeViewModelBase : ObservableObject
{
    /// <summary>Event context stamped on every change the flyout makes, so we can ignore our own echoes.</summary>
    public static readonly Guid UiContext = new("6a4c2a5e-3c55-4c7b-9d0e-5d8f3f0b1a11");

    private readonly DispatcherQueue _dispatcher;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText), nameof(Glyph))]
    private double _volume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(ContentOpacity), nameof(MutedVisibility))]
    private bool _isMuted;

    public Microsoft.UI.Xaml.Visibility MutedVisibility =>
        IsMuted ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    protected VolumeViewModelBase(DispatcherQueue dispatcher, float volume, bool isMuted)
    {
        _dispatcher = dispatcher;
        _volume = Math.Round(volume * 100);
        _isMuted = isMuted;
    }

    public string PercentText => $"{Volume:0}%";

    public double ContentOpacity => IsMuted ? 0.45 : 1.0;

    public string Glyph => IsMuted || Volume <= 0
        ? ""
        : Volume < 34 ? "" : Volume < 67 ? "" : "";

    /// <summary>Live output level 0..1 (peak × volume, 0 when muted) for the row's level meter.</summary>
    [ObservableProperty]
    private double _level;

    /// <summary>Raw pre-volume peak from Core Audio.</summary>
    protected abstract float ReadPeak();

    /// <summary>Called ~30×/s while the flyout is open: fast attack, smooth release.</summary>
    public void TickMeter()
    {
        // Square root ≈ perceived loudness: quiet audio still shows, loud audio still tops out.
        double target = IsMuted ? 0 : Math.Sqrt(ReadPeak() * Volume / 100.0);
        double next = target >= Level ? target : Level * 0.8 + target * 0.2;
        Level = next < 0.002 ? 0 : next;
    }

    public void ResetMeter() => Level = 0;

    protected abstract void ApplyVolume(float volume);

    protected abstract void ApplyMute(bool mute);

    partial void OnVolumeChanged(double value)
    {
        if (_syncing) return;
        ApplyVolume((float)(value / 100.0));
        if (IsMuted && value > 0) IsMuted = false; // Dragging a muted slider unmutes, like the Windows mixer.
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_syncing) return;
        ApplyMute(value);
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Core Audio callback (any thread) → UI state, skipping changes we made ourselves.</summary>
    protected void OnCoreVolumeChanged(object? sender, VolumeChangedEventArgs e)
    {
        if (e.Context == UiContext) return;
        _dispatcher.TryEnqueue(() => Sync(e.Volume, e.IsMuted));
    }

    protected void Sync(float volume, bool isMuted)
    {
        _syncing = true;
        try
        {
            Volume = Math.Round(volume * 100);
            IsMuted = isMuted;
        }
        finally
        {
            _syncing = false;
        }
    }
}
