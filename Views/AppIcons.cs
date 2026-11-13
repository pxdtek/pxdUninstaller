using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using pxdUninstaller.Core;

namespace pxdUninstaller.Views;

/// <summary>
/// Program icons: the Apps &amp; features DisplayIcon (exe, dll or ico, with its index), a Store app's logo, else a
/// coloured tile with the program's initials. Loaded once per file and kept.
/// </summary>
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads every icon off the UI thread (call before showing the list).</summary>
    public static void Preload(IEnumerable<InstalledApp> apps)
    {
        Parallel.ForEach(apps, new ParallelOptions { MaxDegreeOfParallelism = 4 }, a => Get(a));
    }

    public static Bitmap? Get(InstalledApp a)
    {
        var key = a.StoreLogo ?? a.DisplayIcon;
        if (string.IsNullOrWhiteSpace(key)) return null;
        return Cache.GetOrAdd(key, _ => a.StoreLogo is { } logo ? Png(logo) : Extract(a.DisplayIcon));
    }

    private static Bitmap? Png(string file)
    {
        try
        {
            return File.Exists(file) ? new Bitmap(file) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? Extract(string displayIcon)
    {
        var (path, index) = Text.SplitIcon(displayIcon);
        if (path.Length == 0 || !File.Exists(path)) return null;
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return Png(path);
        var icons = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            if (PrivateExtractIconsW(path, index, 48, 48, icons, ids, 1, 0) < 1 || icons[0] == IntPtr.Zero)
                return null;
            return FromIcon(icons[0]);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
        }
    }

    /// <summary>HICON → 32-bit BGRA bitmap (the icon's own alpha, or its mask for old icons).</summary>
    private static Bitmap? FromIcon(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null;
            var bm = new BITMAP();
            if (GetObjectW(info.hbmColor, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
            int w = bm.bmWidth, h = bm.bmHeight;
            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            var dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, info.hbmColor, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
                if (!pixels.Where((_, i) => i % 4 == 3).Any(b => b != 0) && info.hbmMask != IntPtr.Zero)
                {
                    // No alpha: transparent where the mask is set.
                    var mask = new byte[w * h * 4];
                    var mbi = bmi;
                    if (GetDIBits(dc, info.hbmMask, 0, (uint)h, mask, ref mbi, 0) != 0)
                        for (var i = 0; i < w * h; i++)
                            pixels[i * 4 + 3] = mask[i * 4] == 0 ? (byte)255 : (byte)0;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }

            var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var fb = bmp.Lock())
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            }

            return bmp;
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    private static readonly string[] TileColors = ["#6A2BF2", "#F0389B", "#00A3FF", "#12B886", "#F59F00", "#E8590C", "#7048E8", "#0CA678", "#D6336C", "#1C7ED6"];

    /// <summary>The icon at <paramref name="size"/>, or an initials tile.</summary>
    public static Control View(InstalledApp a, double size)
    {
        if (Get(a) is { } bmp)
            return new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Uniform };
        var words = Text.CleanName(a.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        var initials = words.Count switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)],
            _ => $"{words[0][0]}{words[1][0]}",
        };
        var color = TileColors[a.Name.ToLowerInvariant().Aggregate(17, (h, c) => unchecked(h * 31 + c)) is var h2 ? (int)((uint)h2 % TileColors.Length) : 0];   // same colour every start
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24),
            Background = new SolidColorBrush(Color.Parse(color)),
            Child = new TextBlock
            {
                Text = initials.ToUpperInvariant(), FontSize = size * 0.4, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr obj, int size, ref BITMAP bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
