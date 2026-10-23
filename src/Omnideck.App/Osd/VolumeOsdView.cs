using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
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
    public Size SizeDip(OsdContent content, OsdLayout layout)
    {
        var (pill, card) = Arrange(content, layout);
        var union = card is Rect c ? Union(pill, c) : pill;
        return new(union.Width + 2 * ShadowMargin, union.Height + 2 * ShadowMargin);
    }

    // The media card, for a 48-high pill (it scales with it too).
    private const float CardGap = 8, CardCover = 48, CardPad = 12, CardMinWidth = 240, CardCorner = 14;

    /// <summary>
    /// Where the pill's backing and the media card (if any) sit in the window: the card on the side away
    /// from the screen's edge, both lined up on the side the indicator sits on (or centred).
    /// </summary>
    private (Rect Pill, Rect? Card) Arrange(OsdContent content, OsdLayout layout)
    {
        float pw = Math.Max(1, BoxWidth(content, layout) + 2 * layout.Backing);
        float ph = Math.Max(1, layout.Height + 2 * layout.Backing);
        if (content.Media == null) return (new Rect(ShadowMargin, ShadowMargin, pw, ph), null);

        float k = layout.Height / BaseHeight;
        float cw = Math.Max(pw, CardMinWidth * k), ch = CardCover * k + 2 * CardPad * k, gap = CardGap * k;
        float width = Math.Max(pw, cw);
        var position = layout.Position;
        float X(float w) => ShadowMargin + position switch
        {
            OsdPosition.BottomLeft or OsdPosition.TopLeft => 0,
            OsdPosition.BottomRight or OsdPosition.TopRight => width - w,
            _ => (width - w) / 2,
        };
        if (content.CardOnly)
        {
            var alone = new Rect(ShadowMargin, ShadowMargin, cw, ch);
            return (alone, alone); // no pill: its rect is the card's, never drawn
        }
        bool cardAbove = position is OsdPosition.BottomCenter or OsdPosition.BottomLeft or OsdPosition.BottomRight;
        var pill = new Rect(X(pw), ShadowMargin + (cardAbove ? ch + gap : 0), pw, ph);
        var card = new Rect(X(cw), cardAbove ? ShadowMargin : ShadowMargin + ph + gap, cw, ch);
        return (pill, card);
    }

    private static Rect Union(Rect a, Rect b)
    {
        double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

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
        if (content.KeyLabel != null) return KeyCapWidth(content.KeyLabel, content.KeyOn != null, content.KeyUnits, k);
        if (content.Icon != null) return IconSize * k;
        return content.Glyph != null ? GlyphInkWidth(content.Glyph, GlyphSize * k) : SpeakerInkWidth(GlyphSize * k);
    }

    /// <param name="level">The bar's (animated) level, 0..1.</param>
    /// <param name="opacity">The whole indicator's (animated) opacity.</param>
    /// <param name="mediaTime">Seconds since the media card's track appeared: drives the marquee of a long title.</param>
    public byte[] Render(OsdContent content, OsdLayout layout, float level, float opacity, bool light, double scale, out int width, out int height, double mediaTime = 0)
    {
        MediaScrolls = false;
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
        var (backing, card) = Arrange(content, layout);
        var box = new Rect(backing.X + layout.Backing, backing.Y + layout.Backing, BoxWidth(content, layout), layout.Height);
        float radius = (float)(Math.Clamp(layout.CornerPercent, 0, 100) / 100 * Math.Min(backing.Width, backing.Height) / 2);
        float cardRadius = Math.Clamp(layout.CornerPercent, 0, 100) / 100 * CardCorner * k;
        var backingColor = light ? Color.FromArgb(0xE8, 0xF7, 0xF7, 0xF7) : Color.FromArgb(0xE0, 0x24, 0x24, 0x24);
        var edgeColor = light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x24, 255, 255, 255);

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
                // Soft shadow: the backings' shapes, blurred, a little lower.
                using (var shape = new CanvasCommandList(_device))
                {
                    using (var s = shape.CreateDrawingSession())
                    {
                        if (!content.CardOnly) s.FillRoundedRectangle(backing, radius, radius, Color.FromArgb(255, 0, 0, 0));
                        if (card is Rect c) s.FillRoundedRectangle(c, cardRadius, cardRadius, Color.FromArgb(255, 0, 0, 0));
                    }
                    var shadow = new ShadowEffect { Source = shape, BlurAmount = 8, ShadowColor = Color.FromArgb(light ? (byte)50 : (byte)110, 0, 0, 0) };
                    ds.DrawImage(shadow, 0, 3);
                }

                if (card is Rect cardRect && content.Media != null)
                {
                    ds.FillRoundedRectangle(cardRect, cardRadius, cardRadius, backingColor);
                    float cardEdge = Math.Max(0, cardRadius - 0.5f);
                    ds.DrawRoundedRectangle(Inflate(cardRect, -0.5f), cardEdge, cardEdge, edgeColor, 1);
                    DrawMedia(ds, content.Media, cardRect, cardRadius, layout, k, light, Fore, scale, mediaTime);
                }
                if (content.CardOnly) goto done;

                // Semi-transparent backing with a hairline edge.
                ds.FillRoundedRectangle(backing, radius, radius, backingColor);
                float edge = Math.Max(0, radius - 0.5f);
                ds.DrawRoundedRectangle(Inflate(backing, -0.5f), edge, edge, edgeColor, 1);

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

    // ---- media card: cover, title, artist, progress ---------------------------------------------------

    private CanvasBitmap? _cover;
    private byte[]? _coverSource;

    /// <summary>
    /// The cover on the left (rounded to match the card, or a note on a tint while there is none); title
    /// and artist at its top, one line each, scrolling when too long (like the taskbar widget); the progress
    /// bar along its bottom.
    /// </summary>
    private void DrawMedia(CanvasDrawingSession ds, OsdMedia media, Rect card, float cardRadius, OsdLayout layout, float k, bool light, Func<double, Color> fore, double scale, double time)
    {
        if (!ReferenceEquals(media.Cover, _coverSource))
        {
            _coverSource = media.Cover;
            _cover?.Dispose();
            _cover = media.Cover == null ? null
                : CanvasBitmap.CreateFromBytes(_device, media.Cover, media.CoverSize, media.CoverSize,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        }

        float pad = CardPad * k, size = CardCover * k;
        var cover = new Rect(card.X + pad, card.Y + pad, size, size);
        float coverRadius = Math.Max(3 * k, cardRadius - pad) * Math.Clamp(layout.CornerPercent, 0, 100) / 100;
        if (_cover != null)
        {
            using var clip = CanvasGeometry.CreateRoundedRectangle(_device, cover, coverRadius, coverRadius);
            using (ds.CreateLayer(1, clip))
                ds.DrawImage(_cover, cover, _cover.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
        }
        else
        {
            ds.FillRoundedRectangle(cover, coverRadius, coverRadius, fore(light ? 0.08 : 0.1));
            DrawGlyph(ds, "", cover, 20 * k, fore(0.55), scale);
        }

        float textLeft = (float)cover.Right + 12 * k;
        float textWidth = Math.Max(1, (float)(card.Right - pad) - textLeft);
        CanvasTextFormat Format(float fontSize, bool bold) => new()
        {
            FontFamily = TextFont, FontSize = fontSize, WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };
        // Lines placed by their capital letters' ink, like the notices' text.
        Rect Caps(CanvasTextFormat format)
        {
            using var probe = new CanvasTextLayout(_device, "H", format, 1000, 1000);
            return probe.DrawBounds;
        }

        using var titleFormat = Format(layout.TextSize, bold: true);
        var titleCaps = Caps(titleFormat);
        float titleY = Snap((float)(cover.Y + 3 * k - titleCaps.Y), scale);
        DrawMarqueeLine(ds, media.Title, titleFormat, Snap(textLeft, scale), titleY, textWidth, time, fore(0.95));

        if (media.Artist.Length > 0)
        {
            using var artistFormat = Format(Math.Max(9, layout.TextSize - 2), bold: false);
            var artistCaps = Caps(artistFormat);
            float artistY = Snap((float)(titleY + titleCaps.Y + titleCaps.Height + 7 * k - artistCaps.Y), scale);
            DrawMarqueeLine(ds, media.Artist, artistFormat, Snap(textLeft, scale), artistY, textWidth, time, fore(0.6));
        }

        if (media.Progress(OsdMedia.Now) is float progress)
        {
            float barHeight = Math.Max(3, 4 * k);
            float barY = (float)cover.Bottom - barHeight - 2 * k;
            var track = new Rect(textLeft, barY, textWidth, barHeight);
            ds.FillRoundedRectangle(track, barHeight / 2, barHeight / 2, fore(light ? 0.18 : 0.22));
            float fill = progress * textWidth;
            if (fill > 0.5f)
                ds.FillRoundedRectangle(new Rect(textLeft, barY, Math.Max(fill, barHeight), barHeight), barHeight / 2, barHeight / 2, fore(1));
        }
    }

    /// <summary>True after a render whose card has a line that scrolls: it needs frames while it holds.</summary>
    public bool MediaScrolls { get; private set; }

    // The card shows briefly, so its marquee rests less than the widget's before it moves.
    private const double MarqueeWait = 0.8, MarqueeSpeed = 30; // seconds, DIPs per second

    /// <summary>
    /// One line: as is when it fits; otherwise a marquee, as on the taskbar widget (two copies so it loops,
    /// behind faded edges, the left one only once it moves).
    /// </summary>
    private void DrawMarqueeLine(CanvasDrawingSession ds, string text, CanvasTextFormat format, float x, float y, float width, double time, Color color)
    {
        using var layout = new CanvasTextLayout(_device, text, format, 100000, 1000);
        float natural = (float)Math.Ceiling(layout.LayoutBounds.Width);
        if (natural <= width + 0.5f)
        {
            ds.DrawTextLayout(layout, x, y, color);
            return;
        }
        MediaScrolls = true;

        float gap = Taskbar.TaskbarWidgetView.MarqueeGap, fade = Taskbar.TaskbarWidgetView.FadeWidth;
        double cycle = MarqueeWait + (natural + gap) / MarqueeSpeed, phase = time % cycle;
        float scroll = phase < MarqueeWait ? 0 : (float)((phase - MarqueeWait) * MarqueeSpeed);

        float fadeLeft = Math.Min(fade, scroll) / width, fadeRight = fade / width;
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0, Color = Color.FromArgb(fadeLeft > 0 ? (byte)0 : (byte)255, 0, 0, 0) },
            new CanvasGradientStop { Position = Math.Max(fadeLeft, 0.0001f), Color = Color.FromArgb(255, 0, 0, 0) },
            new CanvasGradientStop { Position = 1 - fadeRight, Color = Color.FromArgb(255, 0, 0, 0) },
            new CanvasGradientStop { Position = 1, Color = Color.FromArgb(0, 0, 0, 0) },
        };
        using var mask = new CanvasLinearGradientBrush(_device, stops)
        {
            StartPoint = new Vector2(x, 0), EndPoint = new Vector2(x + width, 0),
        };
        float height = (float)layout.LayoutBounds.Height;
        using (ds.CreateLayer(mask, new Rect(x, y - 2, width, height + 4)))
        {
            ds.DrawTextLayout(layout, x - scroll, y, color);
            ds.DrawTextLayout(layout, x - scroll + natural + gap, y, color);
        }
    }

    // ---- key caps: the icon for lock keys and the keyboard layout -----------------------------------

    private CanvasTextFormat KeyCapFormat(float k, bool withLight) => new()
    {
        FontFamily = TextFont, FontSize = (withLight ? 10 : 11) * k, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };


    /// <summary>
    /// As wide as the key on a keyboard (<paramref name="units"/> times its height: Num Lock square, Caps Lock
    /// wide), and wider still if a label ("EN") needs the room.
    /// </summary>
    private float KeyCapWidth(string label, bool withLight, float units, float k)
    {
        using var format = KeyCapFormat(k, withLight);
        using var text = new CanvasTextLayout(_device, label, format, 1000, 100);
        return Math.Max(KeyCapHeight * units * k, (float)text.DrawBounds.Width + 10 * k);
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
