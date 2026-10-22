namespace Omnideck.Core;

/// <summary>
/// Several apps acted on as one (e.g. every app except the active one): steps move each app by the same
/// amount, and mute is all-or-nothing: if any of them still plays, all are muted; otherwise all are unmuted.
/// </summary>
public sealed class AppGroup(IReadOnlyList<AudioApp> apps) : IVolumeControl
{
    public IReadOnlyList<AudioApp> Apps { get; } = apps;

    /// <summary>The loudest member: what the group "sounds like".</summary>
    public float Volume => Apps.Count == 0 ? 0 : Apps.Max(a => a.Volume);

    public bool IsMuted => Apps.Count > 0 && Apps.All(a => a.IsMuted);

    public void AdjustVolume(float delta, Guid context = default)
    {
        foreach (var app in Apps) app.AdjustVolume(delta, context);
    }

    public void ToggleMute(Guid context = default)
    {
        bool mute = !IsMuted;
        foreach (var app in Apps) app.SetMute(mute, context);
    }
}
