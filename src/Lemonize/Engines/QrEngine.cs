using ImageMagick;
using Lemonize.Core;
using ZXing;
using ZXing.Common;

namespace Lemonize.Engines;

public sealed record CodeResult(string Format, string Text);

public static class QrEngine
{
    public static async Task<List<CodeResult>> ReadAsync(string path)
    {
        MagickImage image;
        if (FileKinds.Detect(path) == FileKind.Pdf)
            image = new MagickImage((await PdfEngine.RenderAsync(path, [0], 2000))[0]);
        else
            image = ImageEngine.Load(path);

        using (image)
        {
            if (image.Width > 3000 || image.Height > 3000) image.Resize(new MagickGeometry(3000, 3000));
            image.Alpha(AlphaAction.Remove);
            image.Depth = 8;
            var rgb = image.ToByteArray(MagickFormat.Rgb);
            var source = new RGBLuminanceSource(rgb, (int)image.Width, (int)image.Height, RGBLuminanceSource.BitmapFormat.RGB24);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, TryInverted = true },
            };
            var results = reader.DecodeMultiple(source) ?? [];
            return results.Select(r => new CodeResult(r.BarcodeFormat.ToString().Replace('_', ' '), r.Text)).Distinct().ToList();
        }
    }
}
