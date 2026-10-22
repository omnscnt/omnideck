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
    public bool ShowLockKeys { get; set; } = true;
    public bool ShowKeyboardLayout { get; set; } = true;
    public bool ShowOutputDevice { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyEnabled => Enabled || ShowMicrophone || ShowLockKeys || ShowKeyboardLayout || ShowOutputDevice;

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
        Math.Clamp(TextSize, MinTextSize, MaxTextSize));

    public VolumeOsdSettings Clone() => (VolumeOsdSettings)MemberwiseClone();
}

/// <summary>Sizes of one indicator in DIPs: the content box, the backing's extra margin, the corner rounding, the number.</summary>
public readonly record struct OsdLayout(float Width, float Height, float Backing, float CornerPercent, float TextSize);

/// <summary>What one notice shows: a volume bar with its number, or (with <paramref name="Text"/>) a line of text.</summary>
/// <param name="Key">Identifies the target: a new key restarts the appear animation, the same one just updates.</param>
/// <param name="Icon">An app's icon as premultiplied BGRA, <paramref name="IconSize"/> square; null for a glyph.</param>
/// <param name="Glyph">A Segoe Fluent glyph for the icon spot; null with no <paramref name="Icon"/> means the speaker that shows the level.</param>
/// <param name="Text">Shown instead of the bar and number (microphone, lock keys, layout, device).</param>
public sealed record OsdContent(string Key, byte[]? Icon, int IconSize, float Volume, bool Muted, string? Glyph = null, string? Text = null)
{
    public static OsdContent Message(string key, string glyph, string text, bool dimmed = false) =>
        new(key, null, 0, 0, dimmed, glyph, text);
}
