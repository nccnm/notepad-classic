using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace IconGen;

/// <summary>
/// Draws the shapes of a minimal SVG (<c>path</c> with <c>d</c> and <c>fill</c>, <c>rect</c> with
/// <c>x y width height fill</c>, one <c>viewBox</c>) at each icon size and writes a PNG-compressed .ico.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: IconGen <icon.svg> <out.ico>");
            return 2;
        }

        var svg = XDocument.Load(args[0]).Root ?? throw new InvalidDataException("empty SVG");
        var viewBox = Numbers(svg.Attribute("viewBox")?.Value ?? throw new InvalidDataException("viewBox is required"));
        var drawing = new DrawingGroup();
        foreach (var element in svg.Descendants())
        {
            var geometry = element.Name.LocalName switch
            {
                "path" => Geometry.Parse(Required(element, "d")),
                "rect" => new RectangleGeometry(new Rect(
                    Number(element, "x"), Number(element, "y"), Number(element, "width"), Number(element, "height"))),
                "svg" => null,
                _ => throw new InvalidDataException($"unsupported element <{element.Name.LocalName}>"),
            };
            if (geometry is not null)
            {
                var brush = (Brush)new BrushConverter().ConvertFromInvariantString(Required(element, "fill"))!;
                drawing.Children.Add(new GeometryDrawing(brush, null, geometry));
            }
        }

        var images = Sizes.Select(size => Render(drawing, viewBox, size)).ToList();
        WriteIcon(args[1], images);
        Console.WriteLine($"{args[1]}: {string.Join(", ", Sizes)} px");
        return 0;
    }

    private static (int Size, byte[] Png) Render(Drawing drawing, double[] viewBox, int size)
    {
        // Even 32-grid coordinates land on whole pixels only at multiples of 16; elsewhere
        // anti-aliasing smears every edge, so those sizes are drawn aliased.
        var sized = new DrawingGroup { Children = { drawing } };
        if (size % 16 != 0)
        {
            RenderOptions.SetEdgeMode(sized, EdgeMode.Aliased);
        }

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size / viewBox[2], size / viewBox[3]));
            context.PushTransform(new TranslateTransform(-viewBox[0], -viewBox[1]));
            context.DrawDrawing(sized);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return (size, stream.ToArray());
    }

    /// <summary>ICONDIR, one ICONDIRENTRY per image (256 is stored as 0), then the PNG data.</summary>
    private static void WriteIcon(string path, List<(int Size, byte[] Png)> images)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        var offset = 6 + (16 * images.Count);
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            writer.Write(png);
        }
    }

    private static string Required(XElement element, string name) =>
        element.Attribute(name)?.Value ?? throw new InvalidDataException($"<{element.Name.LocalName}> needs {name}");

    private static double Number(XElement element, string name) => double.Parse(Required(element, name), CultureInfo.InvariantCulture);

    private static double[] Numbers(string value) =>
        value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
}
