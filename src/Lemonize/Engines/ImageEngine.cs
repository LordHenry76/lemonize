using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;
using Lemonize.Core;

namespace Lemonize.Engines;

public static class ImageEngine
{
    public static readonly string[] Targets = ["jpg", "png", "webp", "tiff", "bmp", "gif", "svg", "pdf", "ico", "avif"];

    private static readonly HashSet<string> MultiFrameTargets = ["gif", "tiff", "webp"];

    public static MagickFormat FormatFor(string ext) => ext.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "jfif" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "heic" or "heif" => MagickFormat.Heic,
        "webp" => MagickFormat.WebP,
        "tif" or "tiff" => MagickFormat.Tiff,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Icon,
        "avif" => MagickFormat.Avif,
        "pdf" => MagickFormat.Pdf,
        _ => MagickFormat.Png,
    };

    public static MagickImage Load(string path)
    {
        var image = new MagickImage(path, ReadSettingsFor(path));
        image.AutoOrient();
        return image;
    }

    public static MagickReadSettings ReadSettingsFor(string path) =>
        FileKinds.Ext(path) == "svg" ? new MagickReadSettings { Density = new Density(192), BackgroundColor = MagickColors.Transparent } : new MagickReadSettings();

    public static void Convert(string input, string output, string target)
    {
        target = target.ToLowerInvariant();
        if (target == "pdf")
        {
            PdfEngine.ImagesToPdf([input], output, fitToA4: false);
            return;
        }
        if (target == "svg")
        {
            WriteEmbeddedSvg(input, output);
            return;
        }

        var inputExt = FileKinds.Ext(input);
        if (MultiFrameTargets.Contains(target) && inputExt is "gif" or "tif" or "tiff" or "webp")
        {
            using var frames = new MagickImageCollection(input);
            if (frames.Count > 1)
            {
                if (target == "gif") frames.Coalesce();
                foreach (var frame in frames) frame.Format = FormatFor(target);
                if (target == "gif") frames.Optimize();
                frames.Write(output, FormatFor(target));
                return;
            }
        }

        using var image = Load(input);
        Save(image, output, target, Settings.Current.JpegQuality);
    }

    public static void Save(MagickImage image, string output, string target, int quality)
    {
        target = target.ToLowerInvariant();
        switch (target)
        {
            case "jpg" or "jpeg" or "jfif" or "bmp":
                if (image.HasAlpha)
                {
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaAction.Remove);
                }
                break;
            case "ico":
                if (image.Width > 256 || image.Height > 256)
                    image.Resize(new MagickGeometry(256, 256));
                image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,16");
                break;
            case "png":
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                break;
            case "tif" or "tiff":
                image.Settings.Compression = CompressionMethod.LZW;
                break;
        }
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        image.Format = FormatFor(target);
        image.Write(output);
    }

    /// <summary>SVG output for raster sources: a vector container with the pixels embedded losslessly.</summary>
    private static void WriteEmbeddedSvg(string input, string output)
    {
        using var image = Load(input);
        image.Format = MagickFormat.Png;
        var data = System.Convert.ToBase64String(image.ToByteArray());
        File.WriteAllText(output,
            $"""
             <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{image.Width}" height="{image.Height}" viewBox="0 0 {image.Width} {image.Height}">
               <image width="{image.Width}" height="{image.Height}" xlink:href="data:image/png;base64,{data}"/>
             </svg>
             """);
    }

    /// <summary>Lays images out on a grid and writes one picture.</summary>
    public static void Collage(IReadOnlyList<string> inputs, string output, int columns, int cellSize, int spacing, MagickColor background)
    {
        columns = Math.Clamp(columns, 1, inputs.Count);
        var rows = (int)Math.Ceiling(inputs.Count / (double)columns);
        var width = columns * cellSize + (columns + 1) * spacing;
        var height = rows * cellSize + (rows + 1) * spacing;
        using var canvas = new MagickImage(background, (uint)width, (uint)height);

        for (var i = 0; i < inputs.Count; i++)
        {
            using var tile = Load(inputs[i]);
            tile.Resize(new MagickGeometry((uint)cellSize, (uint)cellSize) { FillArea = true });
            tile.Crop(new MagickGeometry((uint)cellSize, (uint)cellSize), Gravity.Center);
            tile.ResetPage();
            var x = spacing + i % columns * (cellSize + spacing);
            var y = spacing + i / columns * (cellSize + spacing);
            canvas.Composite(tile, x, y, CompositeOperator.Over);
        }
        Save(canvas, output, FileKinds.Ext(output), 92);
    }

    public static BitmapSource ToBitmapSource(MagickImage image)
    {
        using var clone = (MagickImage)image.Clone();
        clone.Format = MagickFormat.Png32;
        return BitmapFromBytes(clone.ToByteArray());
    }

    public static BitmapSource BitmapFromBytes(byte[] data)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(data);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
