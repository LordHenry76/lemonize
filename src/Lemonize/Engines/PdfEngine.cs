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

/// <param name="FitA4">
/// Shrink the page to fit an A4 sheet (portrait or landscape, as the page is) when it is larger than A4.
/// Smaller pages (receipts, A5) are left as they are.
/// </param>
public sealed record PdfPageRef(string Source, int Index, int ExtraRotation, bool FitA4 = false);

/// <summary>A page's size in millimeters as it is shown, i.e. after its own rotation.</summary>
public readonly record struct PdfPageSize(double WidthMm, double HeightMm)
{
    /// <summary>
    /// A4 in either orientation, within 5 mm per side: scanners and printers to PDF round the size a little
    /// (Letter, 216 × 279 mm, is not A4).
    /// </summary>
    public bool IsA4 => Near(WidthMm, HeightMm, 210, 297) || Near(WidthMm, HeightMm, 297, 210);

    /// <summary>Wider or taller than an A4 sheet laid out the same way (landscape pages against landscape A4).</summary>
    public bool IsLargerThanA4 => WidthMm > HeightMm
        ? WidthMm > 297 + 5 || HeightMm > 210 + 5
        : WidthMm > 210 + 5 || HeightMm > 297 + 5;

    /// <summary>How much the page shrinks once brought to A4 (1 when it already fits).</summary>
    public double A4Scale => Math.Min(1, WidthMm > HeightMm
        ? Math.Min(297 / WidthMm, 210 / HeightMm)
        : Math.Min(210 / WidthMm, 297 / HeightMm));

    private static bool Near(double w, double h, double a, double b) => Math.Abs(w - a) <= 5 && Math.Abs(h - b) <= 5;

    public override string ToString() => $"{WidthMm:0} × {HeightMm:0} mm";
}

/// <param name="Dpi">
/// Resolution of the scan on the page as it is now: pixels of its largest image per inch of paper. Null when
/// no image covers at least half the page (text and drawings have no resolution).
/// </param>
public sealed record PdfPageInfo(PdfPageSize Size, int? Dpi);

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
        var result = new List<byte[]>();
        await RenderEachAsync(path, pageIndexes, width, (_, png) => result.Add(png), token);
        return result;
    }

    /// <summary>
    /// Renders pages one by one, handing each PNG to <paramref name="onPage"/> as soon as it is ready.
    /// The document is loaded once, so long PDFs aren't re-read for every page.
    /// </summary>
    public static async Task RenderEachAsync(string path, IEnumerable<int>? pageIndexes, uint width, Action<int, byte[]> onPage, CancellationToken token = default)
    {
        var doc = await LoadWinPdfAsync(path);
        foreach (var index in pageIndexes ?? Enumerable.Range(0, (int)doc.PageCount))
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
            onPage(index, ms.ToArray());
        }
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
        try
        {
            await RenderEachAsync(input, null, 2000, (i, png) =>
            {
                var output = folder is null
                    ? OutputPaths.For(input, target)
                    : Path.Combine(folder, $"{OutputPaths.BaseName(input)}-{i + 1:D3}.{target}");
                outputs.Add(output);
                if (target == "png") File.WriteAllBytes(output, png);
                else
                {
                    using var image = new MagickImage(png);
                    ImageEngine.Save(image, output, target, Settings.Current.JpegQuality);
                }
                progress((i + 1.0) / count);
            }, token);
        }
        catch
        {
            // Don't leave a half-filled "pages" folder behind when the job fails or is canceled.
            if (folder is not null) Directory.Delete(folder, true);
            else foreach (var output in outputs.Where(File.Exists)) File.Delete(output);
            throw;
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

    /// <param name="fitA4">Shrink every page larger than A4 to A4 (see <see cref="PdfPageRef.FitA4"/>).</param>
    public static void Merge(IReadOnlyList<string> inputs, string output, bool fitA4 = false)
    {
        var pages = new List<PdfPageRef>();
        foreach (var input in inputs)
        {
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            pages.AddRange(Enumerable.Range(0, doc.PageCount).Select(i => new PdfPageRef(input, i, 0, fitA4)));
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
                if (pageRef.FitA4 && SizeOf(page).IsLargerThanA4) ShrinkToA4(page);
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

    private static PdfPageSize SizeOf(PdfPage page)
    {
        var w = XUnit.FromPoint(page.MediaBox.Width).Millimeter;
        var h = XUnit.FromPoint(page.MediaBox.Height).Millimeter;
        return page.Rotate % 180 == 0 ? new PdfPageSize(w, h) : new PdfPageSize(h, w);
    }

    /// <summary>
    /// Turns the page into an A4 sheet with its content scaled down to fit, centered, never distorted. The
    /// content itself is untouched (text stays text, images keep their pixels), so the file doesn't grow and
    /// nothing blurs; links and notes are moved along. The work is done on the page as stored, before its own
    /// rotation, so a page shown sideways becomes a landscape A4 shown the same way.
    /// </summary>
    private static void ShrinkToA4(PdfPage page)
    {
        var box = page.MediaBox;
        var landscape = box.Width > box.Height;
        var sheetWidth = XUnit.FromMillimeter(landscape ? 297 : 210).Point;
        var sheetHeight = XUnit.FromMillimeter(landscape ? 210 : 297).Point;
        var scale = Math.Min(sheetWidth / box.Width, sheetHeight / box.Height);
        var dx = (sheetWidth - box.Width * scale) / 2 - box.X1 * scale;
        var dy = (sheetHeight - box.Height * scale) / 2 - box.Y1 * scale;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var before = page.Contents.PrependContent();
        before.CreateStream(System.Text.Encoding.ASCII.GetBytes(string.Format(inv, "q {0:0.######} 0 0 {0:0.######} {1:0.###} {2:0.###} cm\n", scale, dx, dy)));
        var after = page.Contents.AppendContent();
        after.CreateStream(System.Text.Encoding.ASCII.GetBytes("\nQ\n"));

        if (page.Elements.GetArray("/Annots") is { } annots)
            foreach (var item in annots.Elements)
                if ((item as PdfDictionary ?? (item as PdfReference)?.Value) is PdfDictionary annot && annot.Elements.GetRectangle("/Rect") is { IsZero: false } rect)
                    annot.Elements.SetRectangle("/Rect", new PdfRectangle(
                        new XPoint(rect.X1 * scale + dx, rect.Y1 * scale + dy), new XPoint(rect.X2 * scale + dx, rect.Y2 * scale + dy)));

        foreach (var key in new[] { "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox" }) page.Elements.Remove(key);
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(sheetWidth, sheetHeight));
    }

    /// <summary>The size of every page of the PDF, in order.</summary>
    public static List<PdfPageSize> PageSizes(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(SizeOf).ToList();
    }

    /// <summary>Size and scan resolution of every page. The resolution is only a hint: if it can't be read it's left out.</summary>
    public static List<PdfPageInfo> PageInfos(string path)
    {
        var sizes = PageSizes(path);
        var dpis = new int?[sizes.Count];
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
            for (var i = 0; i < Math.Min(doc.NumberOfPages, dpis.Length); i++)
            {
                try
                {
                    dpis[i] = ScanDpi(doc.GetPage(i + 1));
                }
                catch
                {
                    // An image PdfPig can't read: no resolution for this page.
                }
            }
        }
        catch
        {
            // PdfPig can't open it (e.g. encrypted): sizes only.
        }
        return sizes.Select((size, i) => new PdfPageInfo(size, dpis[i])).ToList();
    }

    private static int? ScanDpi(UglyToad.PdfPig.Content.Page page)
    {
        // Areas, not widths: they don't change when the image is drawn turned on the page.
        var largest = page.GetImages().Select(image => (Image: image, Area: Math.Abs(image.Bounds.Width * image.Bounds.Height))).MaxBy(x => x.Area);
        if (largest.Image is null || largest.Area < page.Width * page.Height / 2) return null;
        var pixels = (double)largest.Image.WidthInSamples * largest.Image.HeightInSamples;
        return (int)Math.Round(Math.Sqrt(pixels / (largest.Area / (72.0 * 72.0))));
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
            // ImageMagick writes a one-channel JPEG when every pixel is gray (typical of scans), even from an
            // RGB image: the PDF must declare what was actually encoded, or viewers show a blank page.
            var gray = new MagickImageInfo(encoded).ColorSpace == ColorSpace.Gray;
            x.Elements.SetName("/ColorSpace", gray ? "/DeviceGray" : "/DeviceRGB");
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
