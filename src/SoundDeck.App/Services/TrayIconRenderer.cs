using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace SoundDeck.Services;

/// <summary>
/// Draws the tray icon from a Segoe Fluent glyph with DirectWrite (Win2D) — the same rasteriser the
/// shell uses for its own tray glyphs, so it is exactly as crisp — at the size the taskbar shows
/// icons for its DPI, centred on the glyph's real ink bounds and snapped to whole pixels.
/// </summary>
public static class TrayIconRenderer
{
    private static readonly string FontName = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons")
        ? "Segoe Fluent Icons"
        : "Segoe MDL2 Assets";

    /// <summary>Small-icon size (SM_CXSMICON) at the taskbar's DPI.</summary>
    public static int TraySize
    {
        get
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
            return GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi != 0 ? dpi : GetDpiForSystem());
        }
    }

    public static Icon Render(string glyph, Color color, int size)
    {
        var device = CanvasDevice.GetSharedDevice();

        // Font px == icon px, like the shell; shrink only if the ink would not fit at all.
        float fontSize = size;
        CanvasTextFormat format;
        CanvasTextLayout layout;
        while (true)
        {
            format = new CanvasTextFormat { FontFamily = FontName, FontSize = fontSize, WordWrapping = CanvasWordWrapping.NoWrap };
            layout = new CanvasTextLayout(device, glyph, format, 1000, 1000);
            var bounds = layout.DrawBounds;
            if ((bounds.Width <= size && bounds.Height <= size) || fontSize <= size * 0.6f) break;
            layout.Dispose();
            format.Dispose();
            fontSize -= 1;
        }

        byte[] pixels;
        using (format)
        using (layout)
        using (var target = new CanvasRenderTarget(device, size, size, 96))
        {
            var ink = layout.DrawBounds;
            float x = MathF.Round((size - (float)ink.Width) / 2f - (float)ink.X);
            float y = MathF.Round((size - (float)ink.Height) / 2f - (float)ink.Y);

            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                ds.DrawTextLayout(layout, x, y, Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
            }
            pixels = target.GetPixelBytes(); // BGRA, premultiplied
        }

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Icons want straight (non-premultiplied) alpha.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                if (a is > 0 and < 255)
                {
                    pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                    pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                    pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
                }
            }
            for (int row = 0; row < size; row++)
                Marshal.Copy(pixels, row * size * 4, data.Scan0 + row * data.Stride, size * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
