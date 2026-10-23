using System.IO;
using ImageMagick;
using Lemonize.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Lemonize.Engines;

public sealed record PdfPageRef(string Source, int Index, int ExtraRotation);

public static class PdfEngine
{
    public static readonly string[] Targets = ["png", "jpg", "txt", "docx", "epub"];

    static PdfEngine()
    {
        PdfSharp.Fonts.GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
    }

    public static void EnsureFonts()
    {
        // Touching the type runs the static constructor that enables Windows fonts.
    }

    public static async Task<int> PageCountAsync(string path)
    {
        var doc = await LoadWinPdfAsync(path);
        return (int)doc.PageCount;
    }

    /// <summary>Renders the given pages to PNG bytes at <paramref name="width"/> pixels wide.</summary>
    public static async Task<List<byte[]>> RenderAsync(string path, IEnumerable<int> pageIndexes, uint width, CancellationToken token = default)
    {
        var doc = await LoadWinPdfAsync(path);
        var result = new List<byte[]>();
        foreach (var index in pageIndexes)
        {
            token.ThrowIfCancellationRequested();
            if (index < 0 || index >= doc.PageCount) continue;
            using var page = doc.GetPage((uint)index);
            using var stream = new InMemoryRandomAccessStream();
            var options = new WinPdf.PdfPageRenderOptions { DestinationWidth = width, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms, token);
            result.Add(ms.ToArray());
        }
        return result;
    }

    /// <summary>
    /// Loads the PDF from an in-memory copy. Handing Windows the file itself (LoadFromFileAsync) lets it
    /// keep the file open until garbage collection on some Windows builds, so users couldn't rename or
    /// delete the original after a conversion.
    /// </summary>
    private static async Task<WinPdf.PdfDocument> LoadWinPdfAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        return await WinPdf.PdfDocument.LoadFromStreamAsync(stream);
    }

    /// <summary>One image per page; multi-page documents go into a folder.</summary>
    public static async Task<List<string>> ToImagesAsync(string input, string target, Action<double> progress, CancellationToken token)
    {
        var count = await PageCountAsync(input);
        var outputs = new List<string>();
        var folder = count > 1 ? OutputPaths.FolderFor(input, Loc.T("pages")) : null;
        for (var i = 0; i < count; i++)
        {
            var png = (await RenderAsync(input, [i], 2000, token))[0];
            var output = folder is null
                ? OutputPaths.For(input, target)
                : Path.Combine(folder, $"{OutputPaths.BaseName(input)}-{i + 1:D3}.{target}");
            if (target == "png") await File.WriteAllBytesAsync(output, png, token);
            else
            {
                using var image = new MagickImage(png);
                ImageEngine.Save(image, output, target, Settings.Current.JpegQuality);
            }
            outputs.Add(output);
            progress((i + 1.0) / count);
        }
        return folder is null ? outputs : [folder];
    }

    public static List<string> ExtractPageTexts(string input)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(input);
        return document.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)).ToList();
    }

    /// <summary>
    /// One portrait A4 page per image: the image is scaled to fit the page edge to edge, centered,
    /// never distorted. Pixels beyond 300 dpi at that size are dropped (no visible gain, much bigger PDF).
    /// </summary>
    public static void ImagesToPdf(IReadOnlyList<string> images, string output, Action<double>? progress = null)
    {
        const double maxDpi = 300;
        var pageWidth = XUnit.FromMillimeter(210).Point;
        var pageHeight = XUnit.FromMillimeter(297).Point;

        using var document = new PdfDocument();
        for (var i = 0; i < images.Count; i++)
        {
            using var image = ImageEngine.Load(images[i]);
            var scale = Math.Min(pageWidth / image.Width, pageHeight / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;

            var maxPixelsWide = (uint)Math.Ceiling(w / 72 * maxDpi);
            if (image.Width > maxPixelsWide)
                image.Resize(new MagickGeometry(maxPixelsWide, (uint)Math.Ceiling(h / 72 * maxDpi)));
            image.Format = image.HasAlpha ? MagickFormat.Png : MagickFormat.Jpeg;
            image.Quality = 92;
            using var stream = new MemoryStream(image.ToByteArray());
            using var ximage = XImage.FromStream(stream);

            var page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(ximage, (pageWidth - w) / 2, (pageHeight - h) / 2, w, h);
            progress?.Invoke((i + 1.0) / images.Count);
        }
        document.Save(output);
    }

    public static void Merge(IReadOnlyList<string> inputs, string output)
    {
        var pages = new List<PdfPageRef>();
        foreach (var input in inputs)
        {
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            pages.AddRange(Enumerable.Range(0, doc.PageCount).Select(i => new PdfPageRef(input, i, 0)));
        }
        Assemble(pages, output);
    }

    /// <summary>Builds a new PDF from pages of one or more source documents, in order.</summary>
    public static void Assemble(IReadOnlyList<PdfPageRef> pages, string output)
    {
        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var result = new PdfDocument();
            foreach (var pageRef in pages)
            {
                if (!sources.TryGetValue(pageRef.Source, out var source))
                    sources[pageRef.Source] = source = PdfReader.Open(pageRef.Source, PdfDocumentOpenMode.Import);
                var page = result.AddPage(source.Pages[pageRef.Index]);
                if (pageRef.ExtraRotation != 0)
                    page.Rotate = ((page.Rotate + pageRef.ExtraRotation) % 360 + 360) % 360;
            }
            var temp = AppPaths.NewTempFile("pdf");
            result.Save(temp);
            OutputPaths.CommitTemp(temp, output);
        }
        finally
        {
            foreach (var doc in sources.Values) doc.Dispose();
        }
    }

    public static int PageCount(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>Parses "1-3, 5, 8-" into zero-based page groups.</summary>
    public static List<List<int>> ParseRanges(string text, int pageCount)
    {
        var groups = new List<List<int>>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            int from = int.Parse(bounds[0] == "" ? "1" : bounds[0]);
            int to = bounds.Length == 1 ? from : bounds[1] == "" ? pageCount : int.Parse(bounds[1]);
            if (from < 1 || to > pageCount || from > to) throw new FormatException(Loc.F("Invalid page range \"{0}\" (document has {1} pages).", part, pageCount));
            groups.Add(Enumerable.Range(from - 1, to - from + 1).ToList());
        }
        if (groups.Count == 0) throw new FormatException(Loc.T("Enter at least one page range."));
        return groups;
    }

    public static List<string> Split(string input, List<List<int>> groups)
    {
        var folder = OutputPaths.FolderFor(input, Loc.T("split"));
        var outputs = new List<string>();
        foreach (var group in groups)
        {
            var label = group.Count == 1 ? $"{group[0] + 1}" : $"{group[0] + 1}-{group[^1] + 1}";
            var output = Path.Combine(folder, $"{OutputPaths.BaseName(input)} p{label}.pdf");
            Assemble(group.Select(i => new PdfPageRef(input, i, 0)).ToList(), output);
            outputs.Add(output);
        }
        return [folder];
    }

    public static Dictionary<string, string> ReadInfo(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var info = doc.Info;
        return new Dictionary<string, string>
        {
            ["Title"] = info.Title,
            ["Author"] = info.Author,
            ["Subject"] = info.Subject,
            ["Keywords"] = info.Keywords,
            ["Creator"] = info.Creator,
            ["Producer"] = info.Producer,
            ["Created"] = info.CreationDate == default ? "" : info.CreationDate.ToString("g"),
            ["Modified"] = info.ModificationDate == default ? "" : info.ModificationDate.ToString("g"),
            ["Pages"] = doc.PageCount.ToString(),
            ["PDF version"] = (doc.Version / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public static void WriteInfo(string input, string output, IReadOnlyDictionary<string, string>? values)
    {
        var temp = AppPaths.NewTempFile("pdf");
        using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var info = doc.Info;
            if (values is null)
            {
                foreach (var key in info.Elements.Keys.ToList()) info.Elements.Remove(key);
                doc.Internals.Catalog.Elements.Remove("/Metadata");
            }
            else
            {
                if (values.TryGetValue("Title", out var title)) info.Title = title;
                if (values.TryGetValue("Author", out var author)) info.Author = author;
                if (values.TryGetValue("Subject", out var subject)) info.Subject = subject;
                if (values.TryGetValue("Keywords", out var keywords)) info.Keywords = keywords;
                if (values.TryGetValue("Creator", out var creator)) info.Creator = creator;
            }
            doc.Save(temp);
        }
        OutputPaths.CommitTemp(temp, output);
    }

    /// <summary>Re-encodes embedded images as JPEG at <paramref name="quality"/>, scaled by <paramref name="scale"/>.</summary>
    public static void RecompressImages(string input, string output, int quality, double scale)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            var xobjects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
            if (xobjects is null) continue;
            foreach (var item in xobjects.Elements.Values)
            {
                if ((item as PdfReference)?.Value is not PdfDictionary x || !seen.Add(x)) continue;
                if (x.Elements.GetName("/Subtype") != "/Image" || x.Stream is null) continue;
                TryRecompress(x, quality, scale);
            }
        }
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;
        doc.Save(output);
    }

    private static void TryRecompress(PdfDictionary x, int quality, double scale)
    {
        if (x.Elements.GetBoolean("/ImageMask") || x.Elements.ContainsKey("/Decode")) return;
        var filter = x.Elements["/Filter"];
        var filterName = filter switch
        {
            PdfName n => n.Value,
            PdfArray { Elements.Count: 1 } a => (a.Elements[0] as PdfName)?.Value,
            _ => null,
        };
        var colorSpace = x.Elements["/ColorSpace"] switch
        {
            PdfName n => n.Value,
            PdfReference { Value: PdfArray a } when a.Elements.Count > 1 && a.Elements[0] is PdfName { Value: "/ICCBased" } =>
                ((a.Elements[1] as PdfReference)?.Value as PdfDictionary)?.Elements.GetInteger("/N") switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => null },
            PdfArray a when a.Elements.Count > 1 && a.Elements[0] is PdfName { Value: "/ICCBased" } =>
                ((a.Elements[1] as PdfReference)?.Value as PdfDictionary)?.Elements.GetInteger("/N") switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => null },
            _ => null,
        };
        if (colorSpace is not ("/DeviceRGB" or "/DeviceGray")) return;

        var original = x.Stream.Value;
        MagickImage image;
        try
        {
            if (filterName == "/DCTDecode")
            {
                image = new MagickImage(original);
            }
            else if (filterName == "/FlateDecode" && x.Elements.GetInteger("/BitsPerComponent") == 8 && !x.Elements.ContainsKey("/DecodeParms"))
            {
                if (!x.Stream.TryUncompress()) return;
                var width = (uint)x.Elements.GetInteger("/Width");
                var height = (uint)x.Elements.GetInteger("/Height");
                var format = colorSpace == "/DeviceGray" ? MagickFormat.Gray : MagickFormat.Rgb;
                image = new MagickImage(x.Stream.Value, new MagickReadSettings { Width = width, Height = height, Format = format, Depth = 8 });
            }
            else
            {
                return;
            }
        }
        catch (MagickException)
        {
            return;
        }

        using (image)
        {
            if (scale < 0.999)
                image.Resize(new Percentage(scale * 100));
            image.Strip();
            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)quality;
            var encoded = image.ToByteArray();
            if (encoded.Length >= original.Length)
            {
                // Not worth it: put the untouched (possibly uncompressed above) stream back.
                x.Stream.Value = original;
                x.Elements["/Filter"] = filter;
                return;
            }

            x.Stream.Value = encoded;
            x.Elements.SetName("/Filter", "/DCTDecode");
            x.Elements.Remove("/DecodeParms");
            x.Elements.SetInteger("/Width", (int)image.Width);
            x.Elements.SetInteger("/Height", (int)image.Height);
            x.Elements.SetInteger("/BitsPerComponent", 8);
            x.Elements.SetName("/ColorSpace", image.ChannelCount <= 2 ? "/DeviceGray" : "/DeviceRGB");
        }
    }
}

/// <summary>Serves Segoe UI (falling back to Arial) from the Windows fonts folder to PDFsharp.</summary>
internal sealed class WindowsFontResolver : PdfSharp.Fonts.IFontResolver
{
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public PdfSharp.Fonts.FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var face = (bold, italic) switch
        {
            (true, true) => "segoeuiz",
            (true, false) => "segoeuib",
            (false, true) => "segoeuii",
            _ => "segoeui",
        };
        if (!File.Exists(Path.Combine(FontsDir, face + ".ttf")))
            face = bold ? "arialbd" : "arial";
        return new PdfSharp.Fonts.FontResolverInfo(face);
    }

    public byte[]? GetFont(string faceName)
    {
        var path = Path.Combine(FontsDir, faceName + ".ttf");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
