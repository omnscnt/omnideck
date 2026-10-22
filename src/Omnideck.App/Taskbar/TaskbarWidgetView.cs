using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Omnideck.Core.Media;
using Windows.Foundation;
using Windows.UI;

namespace Omnideck.Taskbar;

/// <summary>The clickable parts of the widget.</summary>
public enum WidgetPart { None, Info, Previous, PlayPause, Next }

/// <summary>Animated state for one frame (computed by the window, drawn by the view).</summary>
public readonly record struct WidgetVisual(
    double Hover,        // 0..1: hover fill and top highlight
    double Dim,          // 0..1: how much the whole widget is faded (paused)
    float TitleScroll,   // marquee offsets in DIPs
    float ArtistScroll,
    WidgetPart Hot,
    WidgetPart Pressed,
    double Transition = 1, // 0..1: track-change slide progress (1 = none running)
    int Direction = 1);    // +1: the new track comes in from the right (next), -1: from the left (previous)

/// <summary>
/// Lays out and draws the taskbar widget with Win2D, in the style of FluentFlyout's widget: a rounded
/// box that lights up on hover (faint fill plus a highlight along the top edge), optionally the cover
/// blurred behind it, then cover, title / artist and filled previous / play / next buttons.
/// Long titles either end in an ellipsis or scroll (marquee) behind faded edges.
/// Output is premultiplied BGRA for a layered window. Pure drawing: no window or input handling.
/// </summary>
public sealed class TaskbarWidgetView
{
    /// <summary>Per-size metrics in DIPs.</summary>
    private sealed record Metrics(float Box, float Cover, float Button, float Glyph, float Font, bool TwoLines);

    private static readonly Metrics SmallMetrics = new(30, 22, 24, 12, 12, TwoLines: false);
    private static readonly Metrics MediumMetrics = new(40, 32, 30, 14, 12, TwoLines: true);
    private static readonly Metrics LargeMetrics = new(44, 36, 34, 16, 13, TwoLines: true);

    private const float Corner = 6, CoverCorner = 4, PadRight = 4, CoverGap = 8, TextGap = 6, NoControlsPad = 10;
    public const float MarqueeGap = 36, FadeWidth = 14;

    private static readonly string TextFont = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    private static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    // Filled Segoe Fluent glyphs.
    private const string PlayGlyph = "", PauseGlyph = "", PreviousGlyph = "", NextGlyph = "", NoteGlyph = "";

    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private CanvasRenderTarget? _target;

    // Last layout, in DIPs.
    private Metrics _m = MediumMetrics;
    private bool _controls = true;
    private float _scale = 1, _textWidth;
    private Rect _box, _cover, _info, _prev, _play, _next;

    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    /// <summary>Full (untrimmed) widths of the two lines, for the marquee.</summary>
    public float TitleWidth { get; private set; }
    public float ArtistWidth { get; private set; }

    /// <summary>Width of the text column; a line wider than this overflows.</summary>
    public float TextWidth => _textWidth;

    public bool ShowsArtist { get; private set; }

    /// <summary>
    /// Measures the widget for a taskbar <paramref name="heightPx"/> tall at <paramref name="scale"/>;
    /// the text column shrinks so the widget never exceeds <paramref name="maxWidthPx"/>.
    /// </summary>
    public void Layout(NowPlaying track, TaskbarWidgetSettings settings, double scale, int heightPx, int maxWidthPx)
    {
        _scale = (float)scale;
        _controls = settings.ShowControls;
        float height = heightPx / _scale;
        var m = settings.Size switch { WidgetSize.Small => SmallMetrics, WidgetSize.Large => LargeMetrics, _ => MediumMetrics };
        // Never taller than the taskbar allows (2 px breathing room each side).
        float fit = Math.Min(1, (height - 4) / m.Box);
        _m = m = m with { Box = m.Box * fit, Cover = m.Cover * fit, Button = m.Button * fit };

        float controlsWidth = _controls ? TextGap + 3 * m.Button + PadRight : NoControlsPad;
        float fixedWidth = (m.Box - m.Cover) / 2 + m.Cover + CoverGap + controlsWidth;

        using (var title = CreateLayout(track.Title, m.Font, float.MaxValue))
        using (var artist = CreateLayout(track.Artist, m.Font, float.MaxValue))
        {
            TitleWidth = (float)Math.Ceiling(title.LayoutBounds.Width);
            ArtistWidth = (float)Math.Ceiling(artist.LayoutBounds.Width);
        }
        ShowsArtist = m.TwoLines && !string.IsNullOrWhiteSpace(track.Artist);
        // Fixed width (only squeezed when the taskbar has no room): the buttons never move between tracks.
        float room = maxWidthPx / _scale - fixedWidth;
        _textWidth = Math.Max(0, Math.Min(settings.MaxTextWidth, room));

        float width = fixedWidth + _textWidth;
        PixelWidth = (int)Math.Ceiling(width * _scale);
        PixelHeight = heightPx;

        float top = (height - m.Box) / 2;
        float inset = (m.Box - m.Cover) / 2;
        _box = new Rect(0, top, width, m.Box);
        _cover = new Rect(inset, top + inset, m.Cover, m.Cover); // same gap on the left as above and below
        double buttonsLeft = width - PadRight - 3 * m.Button;
        _info = _controls ? new Rect(0, top, buttonsLeft - TextGap / 2, m.Box) : _box;
        double buttonTop = top + (m.Box - m.Button) / 2;
        _prev = _controls ? new Rect(buttonsLeft, buttonTop, m.Button, m.Button) : Rect.Empty;
        _play = _controls ? new Rect(buttonsLeft + m.Button, buttonTop, m.Button, m.Button) : Rect.Empty;
        _next = _controls ? new Rect(buttonsLeft + 2 * m.Button, buttonTop, m.Button, m.Button) : Rect.Empty;
    }

    /// <summary>Which part is under a point given in window pixels.</summary>
    public WidgetPart HitTest(int xPx, int yPx)
    {
        var p = new Point(xPx / _scale, yPx / _scale);
        if (_controls && _prev.Contains(p)) return WidgetPart.Previous;
        if (_controls && _play.Contains(p)) return WidgetPart.PlayPause;
        if (_controls && _next.Contains(p)) return WidgetPart.Next;
        if (_info.Contains(p)) return WidgetPart.Info;
        return WidgetPart.None;
    }

    /// <summary>
    /// Draws a frame. While <see cref="WidgetVisual.Transition"/> runs, <paramref name="previous"/> slides
    /// out to one side and fades as <paramref name="track"/> slides in from the other; the buttons stay.
    /// </summary>
    public byte[] Render(NowPlaying track, CanvasBitmap? cover, NowPlaying? previous, CanvasBitmap? previousCover,
        TaskbarWidgetSettings settings, bool light, WidgetVisual visual)
    {
        var fore = light ? Color.FromArgb(0xE4, 0x1C, 0x1C, 0x1C) : Color.FromArgb(255, 255, 255, 255);
        Color Fore(double alpha) => Color.FromArgb((byte)(alpha * fore.A), fore.R, fore.G, fore.B);
        Color White(double alpha) => Color.FromArgb((byte)(alpha * 255), 255, 255, 255);
        float opacity = (float)(1 - visual.Dim);
        bool sliding = previous != null && visual.Transition < 1;
        float progress = sliding ? EaseOutCubic((float)visual.Transition) : 1;

        if (_target == null || _target.SizeInPixels.Width != PixelWidth || _target.SizeInPixels.Height != PixelHeight)
        {
            _target?.Dispose();
            _target = new CanvasRenderTarget(_device, PixelWidth, PixelHeight, 96);
        }
        var scale = Matrix3x2.CreateScale(_scale);
        using (var ds = _target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            ds.Transform = scale;
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale; // no ClearType on a transparent surface

            using var boxShape = CanvasGeometry.CreateRoundedRectangle(_device, _box, Corner, Corner);
            // Nearly invisible fill so the whole box takes the mouse (layered windows hit-test by alpha).
            ds.FillGeometry(boxShape, Color.FromArgb(1, 0, 0, 0));

            // Blurred cover behind everything; cross-fades on a track change.
            if (settings.CoverBackdrop)
            {
                float backdrop = (light ? 0.3f : 0.4f) * opacity;
                if (sliding && previousCover != null)
                    using (ds.CreateLayer(backdrop * (1 - progress), boxShape))
                        ds.DrawImage(BlurredCover(previousCover));
                if (cover != null)
                    using (ds.CreateLayer(backdrop * progress, boxShape))
                        ds.DrawImage(BlurredCover(cover));
            }

            if (visual.Hover > 0)
            {
                ds.FillGeometry(boxShape, light ? White(0.6 * visual.Hover) : White(0.06 * visual.Hover));
                // Highlight along the top edge: the box minus itself moved down a little.
                using var lower = CanvasGeometry.CreateRoundedRectangle(_device,
                    new Rect(_box.X, _box.Y + 1.25, _box.Width, _box.Height), Corner, Corner);
                using var rim = boxShape.CombineWith(lower, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                ds.FillGeometry(rim, light ? White(0.37 * visual.Hover) : White(0.09 * visual.Hover));
            }

            // Everything below fades together while paused.
            using (ds.CreateLayer(opacity))
            {
                // Cover + text, clipped to their part of the box so a slide never runs under the buttons.
                using (var infoRect = CanvasGeometry.CreateRectangle(_device, _info))
                using (var infoClip = boxShape.CombineWith(infoRect, Matrix3x2.Identity, CanvasGeometryCombine.Intersect))
                using (ds.CreateLayer(1, infoClip))
                {
                    if (sliding)
                    {
                        float distance = SlideDistance * visual.Direction;
                        DrawInfo(ds, scale, previous!, previousCover, -distance * progress, 1 - progress, 0, 0, settings, Fore);
                        DrawInfo(ds, scale, track, cover, distance * (1 - progress), progress, visual.TitleScroll, visual.ArtistScroll, settings, Fore);
                    }
                    else
                    {
                        DrawInfo(ds, scale, track, cover, 0, 1, visual.TitleScroll, visual.ArtistScroll, settings, Fore);
                    }
                }

                if (_controls)
                {
                    DrawButton(ds, _prev, PreviousGlyph, track.CanPrevious, WidgetPart.Previous, visual, light, Fore);
                    DrawButton(ds, _play, track.IsPlaying ? PauseGlyph : PlayGlyph, true, WidgetPart.PlayPause, visual, light, Fore);
                    DrawButton(ds, _next, NextGlyph, track.CanNext, WidgetPart.Next, visual, light, Fore);
                }
            }
        }
        return _target.GetPixelBytes(); // BGRA, premultiplied: what UpdateLayeredWindow wants
    }

    private const float SlideDistance = 28;

    private static float EaseOutCubic(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>Cover and title / artist of one track, shifted by <paramref name="dx"/> and faded.</summary>
    private void DrawInfo(CanvasDrawingSession ds, Matrix3x2 scale, NowPlaying track, CanvasBitmap? cover, float dx, float alpha,
        float titleScroll, float artistScroll, TaskbarWidgetSettings settings, Func<double, Color> fore)
    {
        if (alpha <= 0.001f) return;
        ds.Transform = Matrix3x2.CreateTranslation(dx, 0) * scale;
        using (ds.CreateLayer(alpha))
        {
            // Cover (or a note glyph when the app gives none).
            using (var clip = CanvasGeometry.CreateRoundedRectangle(_device, _cover, CoverCorner, CoverCorner))
            using (ds.CreateLayer(1, clip))
            {
                if (cover != null)
                {
                    ds.DrawImage(cover, _cover, FitSquare(cover.Size), 1, CanvasImageInterpolation.HighQualityCubic);
                }
                else
                {
                    ds.FillRectangle(_cover, fore(0.08));
                    DrawGlyph(ds, NoteGlyph, _cover, _m.Cover * 0.6f, fore(0.8));
                }
            }

            // Title and artist, vertically centred as a pair.
            if (_textWidth > 0)
            {
                float lineHeight;
                using (var probe = CreateLayout("Ag", _m.Font, float.MaxValue)) lineHeight = (float)probe.LayoutBounds.Height;
                bool showArtist = _m.TwoLines && !string.IsNullOrWhiteSpace(track.Artist);
                float pair = showArtist ? 2 * lineHeight - 1 : lineHeight;
                float x = (float)(_cover.Right + CoverGap);
                float y = (float)(_box.Top + (_box.Height - pair) / 2);
                DrawLine(ds, track.Title, x, y, lineHeight, titleScroll, settings.ScrollTitles, fore(1));
                if (showArtist) DrawLine(ds, track.Artist, x, y + lineHeight - 1, lineHeight, artistScroll, settings.ScrollTitles, fore(0.5));
            }
        }
        ds.Transform = scale;
    }

    /// <summary>
    /// One line of text. When it doesn't fit: with scrolling on, a marquee (two copies, so it loops
    /// seamlessly) behind faded edges; otherwise an ellipsis.
    /// </summary>
    private void DrawLine(CanvasDrawingSession ds, string text, float x, float y, float height, float scroll, bool marquee, Color color)
    {
        using var layout = CreateLayout(text, _m.Font, float.MaxValue);
        float natural = (float)Math.Ceiling(layout.LayoutBounds.Width);
        if (!marquee || natural <= _textWidth + 0.5f)
        {
            using var trimmed = CreateLayout(text, _m.Font, _textWidth);
            ds.DrawTextLayout(trimmed, x, y, color);
            return;
        }

        float w = _textWidth;
        // The left edge fades in only once the text has started to move.
        float fadeLeft = Math.Min(FadeWidth, scroll) / w, fadeRight = FadeWidth / w;
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0, Color = Color.FromArgb(fadeLeft > 0 ? (byte)0 : (byte)255, 0, 0, 0) },
            new CanvasGradientStop { Position = Math.Max(fadeLeft, 0.0001f), Color = Color.FromArgb(255, 0, 0, 0) },
            new CanvasGradientStop { Position = 1 - fadeRight, Color = Color.FromArgb(255, 0, 0, 0) },
            new CanvasGradientStop { Position = 1, Color = Color.FromArgb(0, 0, 0, 0) },
        };
        using var mask = new CanvasLinearGradientBrush(_device, stops) { StartPoint = new Vector2(x, 0), EndPoint = new Vector2(x + w, 0) };
        using (ds.CreateLayer(mask, new Rect(x, y - 2, w, height + 4)))
        {
            ds.DrawTextLayout(layout, x - scroll, y, color);
            ds.DrawTextLayout(layout, x - scroll + natural + MarqueeGap, y, color);
        }
    }

    /// <summary>The cover stretched over the whole box (uniform to fill) and heavily blurred.</summary>
    private ICanvasImage BlurredCover(CanvasBitmap cover)
    {
        var size = cover.Size;
        float zoom = (float)Math.Max(_box.Width / size.Width, _box.Height / size.Height) * 1.2f; // overscan: soft edges stay outside
        var cx = (float)(_box.X + _box.Width / 2);
        var cy = (float)(_box.Y + _box.Height / 2);
        var placed = new Transform2DEffect
        {
            Source = cover,
            TransformMatrix = Matrix3x2.CreateTranslation(-(float)size.Width / 2, -(float)size.Height / 2)
                * Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(cx, cy),
            InterpolationMode = CanvasImageInterpolation.Linear,
        };
        return new GaussianBlurEffect { Source = placed, BlurAmount = 18, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
    }

    private void DrawButton(CanvasDrawingSession ds, Rect rect, string glyph, bool enabled, WidgetPart part,
        WidgetVisual visual, bool light, Func<double, Color> fore)
    {
        bool pressed = visual.Pressed == part;
        if (enabled && visual.Hot == part)
            ds.FillRoundedRectangle(rect, 4, 4, fore(pressed ? (light ? 0.05 : 0.04) : (light ? 0.09 : 0.08)));
        DrawGlyph(ds, glyph, rect, _m.Glyph, fore(!enabled ? 0.35 : pressed ? 0.75 : 1.0));
    }

    private void DrawGlyph(CanvasDrawingSession ds, string glyph, Rect rect, float size, Color color)
    {
        using var format = new CanvasTextFormat { FontFamily = IconFont, FontSize = size, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(_device, glyph, format, 100, 100);
        var ink = layout.DrawBounds;
        // Centre the glyph's ink, snapped to device pixels so it stays crisp.
        float x = Snap((float)(rect.X + (rect.Width - ink.Width) / 2 - ink.X));
        float y = Snap((float)(rect.Y + (rect.Height - ink.Height) / 2 - ink.Y));
        ds.DrawTextLayout(layout, x, y, color);
    }

    private float Snap(float dip) => MathF.Round(dip * _scale) / _scale;

    private CanvasTextLayout CreateLayout(string text, float size, float width)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = TextFont,
            FontSize = size,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };
        return new CanvasTextLayout(_device, text, format, width == float.MaxValue ? 10000 : width, 100);
    }

    /// <summary>Centre-crops non-square covers (e.g. video thumbnails) to a square.</summary>
    private static Rect FitSquare(Size size)
    {
        double side = Math.Min(size.Width, size.Height);
        return new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
    }

    private static bool FontExists(string family) =>
        new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == family);
}
