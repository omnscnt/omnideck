using System.Text.Json.Serialization;

namespace Omnisound.Core.Hotkeys;

/// <summary>How the key has to be pressed for the binding to fire.</summary>
public enum TriggerKind
{
    Single,
    Double,
    Triple,
    /// <summary>Legacy: merged into <see cref="Single"/> (a held press repeats). Read from old settings only.</summary>
    Hold,
}

public enum HotkeyAction
{
    VolumeUp,
    VolumeDown,
    ToggleMute,
    PlayPause,
    NextTrack,
    PreviousTrack,
    /// <summary>Shows or hides the mixer flyout (handled by the app, not the sound core).</summary>
    OpenMixer,
}

/// <summary>What the action applies to.</summary>
public enum HotkeyTarget
{
    /// <summary>The output device volume (or, for media actions, the current media session).</summary>
    Master,
    /// <summary>The app that owns the foreground window.</summary>
    ActiveWindow,
    /// <summary>Whatever Windows considers the current media session (e.g. Spotify).</summary>
    MediaPlayer,
    /// <summary>A specific app, by process name (see <see cref="HotkeyBinding.AppName"/>).</summary>
    App,
}

/// <summary>Discord-style binding: key + trigger + action + target.</summary>
public sealed class HotkeyBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public KeyChord Keys { get; set; }
    private TriggerKind _trigger = TriggerKind.Single;

    /// <summary>A single press also repeats while held (for stepping actions), so Hold maps to it.</summary>
    public TriggerKind Trigger
    {
        get => _trigger;
        set => _trigger = value == TriggerKind.Hold ? TriggerKind.Single : value;
    }
    public HotkeyAction Action { get; set; } = HotkeyAction.VolumeUp;
    public HotkeyTarget Target { get; set; } = HotkeyTarget.Master;

    /// <summary>Process name ("spotify") when <see cref="Target"/> is <see cref="HotkeyTarget.App"/>.</summary>
    public string? AppName { get; set; }

    /// <summary>Volume step in percent for VolumeUp/VolumeDown.</summary>
    public int Step { get; set; } = 5;

    /// <summary>Swallow the key so other programs don't see it.</summary>
    public bool BlockKey { get; set; }

    [JsonIgnore]
    public bool IsAppAction => Action is HotkeyAction.OpenMixer;

    [JsonIgnore]
    public bool IsVolumeAction => Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown or HotkeyAction.ToggleMute;

    /// <summary>Only stepping actions repeat while the key is held.</summary>
    [JsonIgnore]
    public bool Repeats => Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown;

    public HotkeyBinding Clone() => (HotkeyBinding)MemberwiseClone();
}
