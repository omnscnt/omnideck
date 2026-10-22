using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Omnideck.Osd;

/// <summary>
/// Draws the volume indicator with Win2D: a pill on a semi-transparent backing with a soft shadow,
/// holding the app icon (or a speaker glyph that follows the level), a bar and the percentage.
/// Output is premultiplied BGRA for a layered window; the shadow needs a margin around the pill.
/// </summary>
public sealed class VolumeOsdView
{
    // DIPs. Inner sizes are for a 48-high content box and scale with the configured height.
    public const float ShadowMargin = 20;
    private const float BaseHeight = 48, Pad = 16, IconSize = 22, BarGap = 10, BarHeight = 4, GlyphSize = 18;

    private static readonly string TextFont = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    private static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private CanvasRenderTarget? _target;
    private CanvasBitmap? _icon;
    private byte[]? _iconSource;

    /// <summary>Widest a text notice gets before its text is cut with an ellipsis.</summary>
    private const float MaxTextBoxWidth = 480;

    /// <summary>The window's size for this content: the backing plus room for its shadow.</summary>
    public Size SizeDip(OsdContent content, OsdLayout layout) => new(
        Math.Max(1, BoxWidth(content, layout) + 2 * layout.Backing) + 2 * ShadowMargin,
        Math.Max(1, layout.Height + 2 * layout.Backing) + 2 * ShadowMargin);

    /// <summary>
    /// The content box's width: the configured width for the volume bar; a text notice is as wide as its
    /// icon and text need (padding on both sides), so it never has an empty stretch.
    /// </summary>
    private float BoxWidth(OsdContent content, OsdLayout layout)
    {
        if (content.Text == null) return layout.Width;
        float k = layout.Height / BaseHeight;
        using var format = new CanvasTextFormat { FontFamily = TextFont, FontSize = layout.TextSize, WordWrapping = CanvasWordWrapping.NoWrap };
        using var text = new CanvasTextLayout(_device, content.Text, format, 10000, 1000);
        float width = (2 * Pad + TextEndPad) * k + IconWidth(content, k) + TextGap * k + (float)Math.Ceiling(text.LayoutBounds.Width) + 1;
        return Math.Clamp(width, layout.Height * 2, MaxTextBoxWidth);
    }

    // TextEndPad: extra room after the text, so it doesn't run up to the rounded end of the backing.
    private const float TextGap = 12, TextEndPad = 6, KeyCapHeight = 20;

    /// <summary>How wide the icon spot is: an app icon, a key cap, a glyph by its ink, or the speaker.</summary>
    private float IconWidth(OsdContent content, float k)
    {
        if (content.KeyLabel != null) return KeyCapWidth(content.KeyLabel, content.KeyOn != null, k);
        if (content.Icon != null) return IconSize * k;
        return content.Glyph != null ? GlyphInkWidth(content.Glyph, GlyphSize * k) : SpeakerInkWidth(GlyphSize * k);
    }

    /// <param name="level">The bar's (animated) level, 0..1.</param>
    /// <param name="opacity">The whole indicator's (animated) opacity.</param>
    public byte[] Render(OsdContent content, OsdLayout layout, float level, float opacity, bool light, double scale, out int width, out int height)
    {
        var size = SizeDip(content, layout);
        width = (int)Math.Ceiling(size.Width * scale);
        height = (int)Math.Ceiling(size.Height * scale);
        if (_target == null || _target.SizeInPixels.Width != width || _target.SizeInPixels.Height != height)
        {
            _target?.Dispose();
            _target = new CanvasRenderTarget(_device, width, height, 96);
        }
        UpdateIcon(content);

        float k = layout.Height / BaseHeight;
        float pad = Pad * k, iconSize = IconSize * k, barHeight = Math.Max(3, BarHeight * k);
        var fore = light ? Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Color.FromArgb(255, 255, 255, 255);
        Color Fore(double alpha) => Color.FromArgb((byte)(alpha * 255), fore.R, fore.G, fore.B);

        // The content box holds icon, bar and number; the backing extends past it by Backing on every side.
        var box = new Rect(ShadowMargin + layout.Backing, ShadowMargin + layout.Backing, BoxWidth(content, layout), layout.Height);
        var backing = new Rect(ShadowMargin, ShadowMargin, size.Width - 2 * ShadowMargin, size.Height - 2 * ShadowMargin);
        float radius = (float)(Math.Clamp(layout.CornerPercent, 0, 100) / 100 * Math.Min(backing.Width, backing.Height) / 2);

        using var format = new CanvasTextFormat
        {
            FontFamily = TextFont, FontSize = layout.TextSize, WordWrapping = CanvasWordWrapping.NoWrap,
        };
        // The number column is exactly as wide as "100", so the bar never changes length with the value; its
        // ink (digits are all as tall) gives the offset that centres the digits on the bar, not the line box.
        float numberWidth, digitsTop, digitsHeight;
        using (var widest = new CanvasTextLayout(_device, "100", format, 1000, 100))
        {
            numberWidth = (float)Math.Ceiling(widest.LayoutBounds.Width);
            digitsTop = (float)widest.DrawBounds.Y;
            digitsHeight = (float)widest.DrawBounds.Height;
        }

        using (var ds = _target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            ds.Transform = Matrix3x2.CreateScale((float)scale);
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale; // no ClearType on a transparent surface

            using (ds.CreateLayer(opacity))
            {
                // Soft shadow: the backing's shape, blurred, a little lower.
                using (var shape = new CanvasCommandList(_device))
                {
                    using (var s = shape.CreateDrawingSession())
                        s.FillRoundedRectangle(backing, radius, radius, Color.FromArgb(255, 0, 0, 0));
                    var shadow = new ShadowEffect { Source = shape, BlurAmount = 8, ShadowColor = Color.FromArgb(light ? (byte)50 : (byte)110, 0, 0, 0) };
                    ds.DrawImage(shadow, 0, 3);
                }

                // Semi-transparent backing with a hairline edge.
                ds.FillRoundedRectangle(backing, radius, radius, light ? Color.FromArgb(0xE8, 0xF7, 0xF7, 0xF7) : Color.FromArgb(0xE0, 0x24, 0x24, 0x24));
                float edge = Math.Max(0, radius - 0.5f);
                ds.DrawRoundedRectangle(Inflate(backing, -0.5f), edge, edge, light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x24, 255, 255, 255), 1);

                // Icon: the app's own, a given glyph, or a speaker that shows the level.
                // Glyphs are laid out by their ink (the speaker by its widest variant, so the bar doesn't move with the level).
                float iconWidth = IconWidth(content, k);
                var iconRect = new Rect(box.X + pad, box.Y + (box.Height - iconSize) / 2, iconWidth, iconSize);
                if (content.KeyLabel != null)
                    DrawKeyCap(ds, content.KeyLabel, content.KeyOn, layout.LockLight, light, iconRect, k, Fore, scale);
                else if (_icon != null)
                    ds.DrawImage(_icon, iconRect, _icon.Bounds, content.Muted ? 0.4f : 1f, CanvasImageInterpolation.HighQualityCubic);
                else
                    DrawGlyph(ds, content.Glyph ?? SpeakerGlyph(content), iconRect, GlyphSize * k, Fore(1), scale, alignLeft: true);
                if (_icon != null && content.Muted)
                    DrawGlyph(ds, "", new Rect(iconRect.Right - 9 * k, iconRect.Bottom - 9 * k, 12 * k, 12 * k), 11 * k, Fore(1), scale); // mute badge

                if (content.Text != null)
                {
                    // A message instead of the bar: one line after the icon, cut with an ellipsis if too long.
                    float textLeft = (float)iconRect.Right + TextGap * k;
                    float textWidth = Math.Max(1, (float)(box.Right - pad) - textLeft);
                    using var textFormat = new CanvasTextFormat
                    {
                        FontFamily = TextFont, FontSize = layout.TextSize, WordWrapping = CanvasWordWrapping.NoWrap,
                        TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
                    };
                    using var text = new CanvasTextLayout(_device, content.Text, textFormat, textWidth, 1000);
                    // Centred by its capital letters' ink (the line box has room for accents and descenders,
                    // which would put the visible text off centre): the same for every text.
                    Rect caps;
                    using (var probe = new CanvasTextLayout(_device, "H", textFormat, 1000, 1000)) caps = probe.DrawBounds;
                    float textY = Snap((float)(box.Y + box.Height / 2 - caps.Height / 2 - caps.Y), scale);
                    ds.DrawTextLayout(text, Snap(textLeft, scale), textY, Fore(content.Muted ? 0.6 : 0.9));
                    goto done;
                }

                // Bar, then the number right after it.
                float numberLeft = (float)(box.Right - pad - numberWidth);
                float barLeft = (float)iconRect.Right + BarGap * k;
                float barRight = numberLeft - BarGap * k;
                float barY = (float)(box.Y + (box.Height - barHeight) / 2);
                var track = new Rect(barLeft, barY, Math.Max(0, barRight - barLeft), barHeight);
                ds.FillRoundedRectangle(track, barHeight / 2, barHeight / 2, Fore(light ? 0.18 : 0.22));
                float fill = Math.Clamp(level, 0, 1) * (float)track.Width;
                if (fill > 0.5f)
                    ds.FillRoundedRectangle(new Rect(barLeft, barY, Math.Max(fill, barHeight), barHeight), barHeight / 2, barHeight / 2, Fore(content.Muted ? 0.35 : 1));

                float numberY = Snap(barY + barHeight / 2 - digitsHeight / 2 - digitsTop, scale);
                ds.DrawText($"{(int)Math.Round(content.Volume * 100)}", numberLeft, numberY, Fore(content.Muted ? 0.45 : 0.85), format);
            done:;
            }
        }
        return _target.GetPixelBytes();
    }

    private static string SpeakerGlyph(OsdContent content) =>
        content.Muted ? ""
        : content.Volume <= 0.001f ? ""
        : content.Volume < 0.34f ? ""
        : content.Volume < 0.67f ? ""
        : "";

    private void UpdateIcon(OsdContent content)
    {
        if (ReferenceEquals(content.Icon, _iconSource)) return;
        _iconSource = content.Icon;
        _icon?.Dispose();
        _icon = content.Icon == null ? null
            : CanvasBitmap.CreateFromBytes(_device, content.Icon, content.IconSize, content.IconSize,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
    }

    // ---- key caps: the icon for lock keys and the keyboard layout -----------------------------------

    private CanvasTextFormat KeyCapFormat(float k, bool withLight) => new()
    {
        FontFamily = TextFont, FontSize = (withLight ? 10 : 11) * k, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    /// <summary>Room the light takes in the top-right corner, so the label sits clear of it.</summary>
    private const float LightInset = 5;

    /// <summary>
    /// At least square (a little wider with a light); wider for a longer label ("EN"), with room on both
    /// sides of it.
    /// </summary>
    private float KeyCapWidth(string label, bool withLight, float k)
    {
        using var format = KeyCapFormat(k, withLight);
        using var text = new CanvasTextLayout(_device, label, format, 1000, 100);
        float min = (KeyCapHeight + (withLight ? 6 : 0)) * k;
        return Math.Max(min, (float)text.DrawBounds.Width + (withLight ? 10 + LightInset : 10) * k);
    }

    /// <summary>
    /// A key: rounded outline and its label; lock keys also get a light in the top-right corner (grey when
    /// off, <paramref name="lightColor"/> when on), with the label centred in the space left beside and below
    /// it. Labels are centred by their ink, snapped to pixels.
    /// </summary>
    private void DrawKeyCap(CanvasDrawingSession ds, string label, bool? lit, Color lightColor, bool lightTheme, Rect spot, float k, Func<double, Color> fore, double scale)
    {
        float h = KeyCapHeight * k, w = (float)spot.Width;
        var cap = new Rect(spot.X, spot.Y + (spot.Height - h) / 2, w, h);
        float stroke = Math.Max(1, 1.4f * k), r = 4 * k;
        ds.DrawRoundedRectangle(Inflate(cap, -stroke / 2), r, r, fore(0.9), stroke);

        bool withLight = lit != null;
        var labelArea = cap; // centred on the whole key; the light sits in the corner, clear of a one- or two-letter label
        using var format = KeyCapFormat(k, withLight);
        using var text = new CanvasTextLayout(_device, label, format, 1000, 100);
        var ink = text.DrawBounds;
        float x = Snap((float)(labelArea.X + (labelArea.Width - ink.Width) / 2 - ink.X), scale);
        float y = Snap((float)(labelArea.Y + (labelArea.Height - ink.Height) / 2 - ink.Y), scale);
        ds.DrawTextLayout(text, x, y, fore(1));

        if (lit is bool on)
        {
            var center = new Vector2((float)cap.Right - 4.3f * k, (float)cap.Y + 4.3f * k);
            var off = lightTheme ? Color.FromArgb(255, 0xA8, 0xA8, 0xA8) : Color.FromArgb(255, 0x6E, 0x6E, 0x6E);
            ds.FillCircle(center, 1.9f * k, on ? lightColor : off);
        }
    }

    private float GlyphInkWidth(string glyph, float size)
    {
        using var format = new CanvasTextFormat { FontFamily = IconFont, FontSize = size, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(_device, glyph, format, 100, 100);
        return (float)layout.DrawBounds.Width;
    }

    private float SpeakerInkWidth(float size)
    {
        using var format = new CanvasTextFormat { FontFamily = IconFont, FontSize = size, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(_device, "", format, 100, 100); // full volume: the most waves
        return (float)layout.DrawBounds.Width;
    }

    private void DrawGlyph(CanvasDrawingSession ds, string glyph, Rect rect, float size, Color color, double scale, bool alignLeft = false)
    {
        using var format = new CanvasTextFormat { FontFamily = IconFont, FontSize = size, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(_device, glyph, format, 100, 100);
        var ink = layout.DrawBounds;
        // Centre (or left-align) the ink, snapped to device pixels so the glyph stays crisp.
        float x = Snap((float)(rect.X + (alignLeft ? 0 : (rect.Width - ink.Width) / 2) - ink.X), scale);
        float y = Snap((float)(rect.Y + (rect.Height - ink.Height) / 2 - ink.Y), scale);
        ds.DrawTextLayout(layout, x, y, color);
    }

    private static float Snap(float dip, double scale) => (float)(Math.Round(dip * scale) / scale);

    private static Rect Inflate(Rect r, float by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static bool FontExists(string family) =>
        new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == family);
}
