namespace Omnideck.Core;

/// <summary>Anything with a volume and a mute: the output device or an app. What hotkeys act on.</summary>
public interface IVolumeControl
{
    float Volume { get; }
    bool IsMuted { get; }
    void AdjustVolume(float delta, Guid context = default);
    void ToggleMute(Guid context = default);
}
