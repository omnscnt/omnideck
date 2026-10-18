using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Omnisound.Services;

/// <summary>
/// Loads (and caches) an executable's icon as an ImageSource. UI thread only.
/// Extracts the HICON straight from the exe (shell thumbnails fail for many Electron/Squirrel apps).
/// </summary>
public static class AppIconLoader
{
    private const int IconSize = 48;
    private static readonly Dictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<ImageSource?> LoadAsync(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return Task.FromResult<ImageSource?>(null);
        if (!Cache.TryGetValue(exePath, out var task))
        {
            task = LoadCoreAsync(exePath);
            Cache[exePath] = task;
        }
        return task;
    }

    private static async Task<ImageSource?> LoadCoreAsync(string exePath)
    {
        var pixels = await Task.Run(() => ExtractPixels(exePath, IconSize));
        if (pixels == null) return null;

        var bitmap = new WriteableBitmap(IconSize, IconSize);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Premultiplied BGRA pixels (size × size) of the exe's main icon, or null. Any thread.</summary>
    public static byte[]? ExtractPixels(string path, int size)
    {
        if (SHDefExtractIcon(path, 0, 0, out var icon, out var small, (uint)(size | (16 << 16))) != 0 || icon == IntPtr.Zero)
            return null;
        if (small != IntPtr.Zero) DestroyIcon(small);

        try
        {
            if (!GetIconInfo(icon, out var info)) return null;
            try
            {
                var header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                };
                var pixels = new byte[size * size * 4];
                var dc = GetDC(IntPtr.Zero);
                try
                {
                    if (GetDIBits(dc, info.hbmColor, 0, (uint)size, pixels, ref header, 0) == 0) return null;
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, dc);
                }

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0) { hasAlpha = true; break; }
                }

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    // Legacy icons without alpha: treat every drawn pixel as opaque.
                    byte a = hasAlpha ? pixels[i + 3] : (byte)(pixels[i] | pixels[i + 1] | pixels[i + 2]) > 0 ? (byte)255 : (byte)0;
                    pixels[i] = (byte)(pixels[i] * a / 255);
                    pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                    pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
                    pixels[i + 3] = a;
                }
                return pixels;
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint iconSize);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
