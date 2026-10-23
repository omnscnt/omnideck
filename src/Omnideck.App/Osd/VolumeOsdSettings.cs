namespace Omnideck.Osd;

public enum OsdPosition { BottomCenter, TopCenter, BottomLeft, BottomRight, TopLeft, TopRight }

/// <summary>
/// The on-screen notices' settings (saved in settings.json as "VolumeOsd", its name from when volume was
/// the only one): which kinds show, and the look they all share.
/// </summary>
public sealed class VolumeOsdSettings
{
    /// <summary>The volume notice (hotkey changes; see <see cref="ShowExternalChanges"/> for the rest).</summary>
    public bool Enabled { get; set; } = true;

    public bool ShowMicrophone { get; set; } = true;
    public bool ShowCapsLock { get; set; } = true;
    public bool ShowNumLock { get; set; } = true;
    public bool ShowScrollLock { get; set; } = true;

    /// <summary>The lock keys' light when on, as #RRGGBB (off is always grey).</summary>
    public string LockLightColor { get; set; } = DefaultLockLight;

    public const string DefaultLockLight = "#4CC764";
    public bool ShowKeyboardLayout { get; set; } = true;
    public bool ShowOutputDevice { get; set; } = true;

    /// <summary>With an app's volume, a card with what it plays (cover, title, progress), unless the taskbar widget already shows it.</summary>
    public bool ShowNowPlaying { get; set; } = true;

    /// <summary>The card alone when a hotkey skips to the next or previous track.</summary>
    public bool NowPlayingOnTrackSwitch { get; set; } = true;

    /// <summary>No card for the foreground window's app (its own window shows the track).</summary>
    public bool NowPlayingSkipActive { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyEnabled => Enabled || ShowMicrophone || ShowCapsLock || ShowNumLock || ShowScrollLock || ShowKeyboardLayout || ShowOutputDevice;

    public OsdPosition Position { get; set; } = OsdPosition.BottomCenter;

    /// <summary>How long it stays after the last change, in milliseconds.</summary>
    public int DurationMs { get; set; } = 1500;

    /// <summary>Also show changes not made by hotkeys: media keys, the Windows mixer, apps themselves.</summary>
    public bool ShowExternalChanges { get; set; }

    /// <summary>Size of the pill in DIPs; icon, padding and text scale with the height.</summary>
    public int Width { get; set; } = 264;
    public int Height { get; set; } = 48;

    /// <summary>How far the backing reaches past the content (icon, bar, number), in DIPs; negative hugs it closer.</summary>
    public int Backing { get; set; }

    /// <summary>Corner rounding as a percentage of the fully round (pill) shape.</summary>
    public int CornerPercent { get; set; } = 100;

    /// <summary>Size of the percentage, in DIPs.</summary>
    public int TextSize { get; set; } = 14;

    public const int MinWidth = 160, MaxWidth = 480, MinHeight = 32, MaxHeight = 72, MinBacking = -8, MaxBacking = 24, MinTextSize = 10, MaxTextSize = 24;

    public OsdLayout ToLayout() => new(
        Math.Clamp(Width, MinWidth, MaxWidth), Math.Clamp(Height, MinHeight, MaxHeight),
        Math.Clamp(Backing, MinBacking, MaxBacking), Math.Clamp(CornerPercent, 0, 100),
        Math.Clamp(TextSize, MinTextSize, MaxTextSize), ParseColor(LockLightColor) ?? ParseColor(DefaultLockLight)!.Value, Position);

    /// <summary>"#RRGGBB" to a colour; null when it isn't one.</summary>
    public static Windows.UI.Color? ParseColor(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb)) return null;
        return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public VolumeOsdSettings Clone() => (VolumeOsdSettings)MemberwiseClone();
}

/// <summary>Sizes of one indicator in DIPs: the content box, the backing's extra margin, the corner rounding, the number.</summary>
/// <param name="Position">Where it sits: a media card goes on the side away from the screen's edge, aligned with the pill.</param>
public readonly record struct OsdLayout(float Width, float Height, float Backing, float CornerPercent, float TextSize, Windows.UI.Color LockLight, OsdPosition Position = OsdPosition.BottomCenter);

/// <summary>What one notice shows: a volume bar with its number, or (with <paramref name="Text"/>) a line of text.</summary>
/// <param name="Key">Identifies the target: a new key restarts the appear animation, the same one just updates.</param>
/// <param name="Icon">An app's icon as premultiplied BGRA, <paramref name="IconSize"/> square; null for a glyph.</param>
/// <param name="Glyph">A Segoe Fluent glyph for the icon spot; null with no <paramref name="Icon"/> means the speaker that shows the level.</param>
/// <param name="Text">Shown instead of the bar and number (microphone, lock keys, layout, device).</param>
/// <param name="KeyLabel">Draws a key cap with this label as the icon (lock keys, layout), instead of a glyph.</param>
/// <param name="KeyOn">The key cap's light: on, off, or none (null).</param>
/// <param name="KeyUnits">The key cap's width in key units (Caps Lock is wider than Num Lock).</param>
/// <param name="Media">What the app plays, shown as a card next to its volume.</param>
/// <param name="CardOnly">Only the media card, no pill (a track switched by a hotkey).</param>
public sealed record OsdContent(
    string Key, byte[]? Icon, int IconSize, float Volume, bool Muted,
    string? Glyph = null, string? Text = null, string? KeyLabel = null, bool? KeyOn = null, float KeyUnits = 1, OsdMedia? Media = null, bool CardOnly = false)
{
    public static OsdContent Message(string key, string glyph, string text, bool dimmed = false) =>
        new(key, null, 0, 0, dimmed, glyph, text);

    /// <param name="units">The key's width in key units, as on a keyboard (Caps Lock is 1.75, most keys 1).</param>
    public static OsdContent KeyNotice(string key, string label, bool? lit, string text, bool dimmed = false, float units = 1) =>
        new(key, null, 0, 0, dimmed, null, text, label, lit, units);
}

/// <summary>The media card: a cover (premultiplied BGRA, <paramref name="CoverSize"/> square; null for a placeholder), title, artist, progress.</summary>
/// <param name="ReadAt">When <paramref name="Position"/> was read (<see cref="Now"/>): the bar runs on from there.</param>
public sealed record OsdMedia(string Title, string Artist, byte[]? Cover, int CoverSize, TimeSpan Position, TimeSpan Duration, double ReadAt)
{
    public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>How far into the track, 0..1, at <paramref name="now"/>; null when the length is unknown.</summary>
    public float? Progress(double now) => Duration <= TimeSpan.Zero ? null
        : (float)Math.Clamp((Position.TotalSeconds + Math.Max(0, now - ReadAt)) / Duration.TotalSeconds, 0, 1);
}
