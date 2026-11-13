using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdUninstaller;

/// <summary>
/// pxd Uninstaller's icon: a lifted-lid bin with a neon sparkle (clean), on a violet-to-pink tile.
/// Drawn from vector shapes so it stays crisp at every size; used for the window and the exe's .ico.
/// </summary>
public static class BrandIcon
{
    public static Control Build(double size)
    {
        var a = new Canvas { Width = 100, Height = 100 };
        IBrush B(string c) => new SolidColorBrush(Color.Parse(c));
        void Stroke(string data, string color, double width) =>
            a.Children.Add(new ShapePath { Data = Geometry.Parse(data), Stroke = B(color), StrokeThickness = width, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        void Fill(string data, string color) => a.Children.Add(new ShapePath { Data = Geometry.Parse(data), Fill = B(color) });

        // Lid, lifted and tilted, with its handle.
        a.Children.Add(new Canvas
        {
            RenderTransform = new RotateTransform(-14, 0, 0),
            RenderTransformOrigin = new RelativePoint(0.22, 0.27, RelativeUnit.Relative),
            Width = 100, Height = 100,
            Children =
            {
                new ShapePath { Data = Geometry.Parse("M20,27 H78"), Stroke = B("#FFFFFF"), StrokeThickness = 8, StrokeLineCap = PenLineCap.Round },
                new ShapePath { Data = Geometry.Parse("M39,26 V18 H59 V26"), Stroke = B("#FFFFFF"), StrokeThickness = 6, StrokeJoin = PenLineJoin.Round },
            },
        });
        // Body and slots.
        Fill("M27,38 H73 L68.5,84 Q67.8,91 61,91 H39 Q32.2,91 31.5,84 Z", "#FFFFFF");
        Stroke("M41,49 L42,81", "#A23CE0", 5);
        Stroke("M50,49 V81", "#A23CE0", 5);
        Stroke("M59,49 L58,81", "#A23CE0", 5);
        // Sparkle: it's clean afterwards.
        Fill("M85,30 Q86.4,40 96,41.5 Q86.4,43 85,53 Q83.6,43 74,41.5 Q83.6,40 85,30 Z", "#19F58C");
        Fill("M79,6 Q79.8,11 84.5,12 Q79.8,13 79,18 Q78.2,13 73.5,12 Q78.2,11 79,6 Z", "#E6FFFFFF");
        return Tile(size, "#6A2BF2", "#F0389B", a, 0.11);
    }

    public static RenderTargetBitmap Render(int size)
    {
        var c = Build(size);
        c.Measure(new Size(size, size));
        c.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(c);
        return bitmap;
    }

    public static WindowIcon Window(int size = 64) => new(Render(size));

    /// <summary>Writes a multi-size .ico (PNG entries) for the exe, or a 256 px PNG when the path ends in .png.</summary>
    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = File.Create(path);
            Render(256).Save(png, PngBitmapEncoderOptions.Default);
            return;
        }

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            Render(s).Save(ms, PngBitmapEncoderOptions.Default);
            return ms.ToArray();
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(b);
            w.Write(b);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var img in images) w.Write(img);
    }

    private static Grid Tile(double size, string from, string to, Canvas art, double margin)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            },
        });
        g.Children.Add(new Viewbox { Margin = new Thickness(size * margin), Child = art });
        return g;
    }
}
