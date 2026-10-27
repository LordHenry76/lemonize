using System.Diagnostics;
using System.IO;
using ImageMagick;
using ImageMagick.Drawing;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;
using ZXing;

// End-to-end smoke test: generates sample files, then runs every conversion and tool against them.
// Usage: Lemonize.Smoke <empty work folder> [--render-wheels] [--italian] [--heic=<folder with .heic files>]
var work = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine(Path.GetTempPath(), "lemonize-smoke"));
if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(work);
Loc.Initialize(args.Contains("--italian") ? "it" : "en");
Settings.Current.OutputLocation = OutputLocation.BesideOriginal;
Settings.Current.ToolsOverwriteOriginal = false;

var results = new List<(string Name, bool Ok, string Detail)>();
string P(string name) => Path.Combine(work, name);

async Task Check(string name, Func<Task<IEnumerable<string>>> action)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var outputs = (await action()).ToList();
        var missing = outputs.Where(o => !File.Exists(o) && !Directory.Exists(o)).ToList();
        var empty = outputs.Where(o => File.Exists(o) && new FileInfo(o).Length == 0).ToList();
        if (outputs.Count == 0 || missing.Count > 0 || empty.Count > 0)
            results.Add((name, false, $"outputs={outputs.Count} missing={missing.Count} empty={empty.Count}"));
        else
            results.Add((name, true, $"{string.Join(", ", outputs.Select(o => Path.GetFileName(o) + (File.Exists(o) ? $" ({OutputPaths.FormatSize(new FileInfo(o).Length)})" : "/")))} [{sw.ElapsedMilliseconds} ms]"));
    }
    catch (Exception ex)
    {
        results.Add((name, false, ex.GetType().Name + ": " + ex.Message));
    }
    Console.WriteLine($"{(results[^1].Ok ? "OK  " : "FAIL")} {name}  {results[^1].Detail}");
}

Task<IEnumerable<string>> One(string path) => Task.FromResult<IEnumerable<string>>([path]);

// ---------- Samples ----------
using (var img = new MagickImage(new MagickColor("#3A7BD5"), 640, 480))
{
    new Drawables().FillColor(new MagickColor("#FFD23F")).Ellipse(320, 240, 180, 120, 0, 360).Draw(img);
    var exif = new ExifProfile();
    exif.SetValue(ExifTag.Artist, "Smoke Tester");
    exif.SetValue(ExifTag.Copyright, "secret");
    img.SetProfile(exif);
    img.Write(P("photo.jpg"), MagickFormat.Jpeg);
    img.Write(P("image.png"), MagickFormat.Png);
}
using (var frames = new MagickImageCollection())
{
    foreach (var c in new[] { "red", "green", "blue" })
    {
        var f = new MagickImage(new MagickColor(c), 200, 150) { AnimationDelay = 30 };
        frames.Add(f);
    }
    frames.Write(P("anim.gif"));
}
var qr = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 400, Height = 400, Margin = 2 } }
    .Write("https://lemonize.app/hello");
using (var qrImg = new MagickImage())
{
    qrImg.ReadPixels(qr.Pixels, new PixelReadSettings((uint)qr.Width, (uint)qr.Height, StorageType.Char, PixelMapping.BGRA));
    qrImg.Write(P("qr.png"), MagickFormat.Png);
}

await RunFfmpeg("-f", "lavfi", "-i", "testsrc=duration=4:size=640x360:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-metadata", "title=Smoke video", P("video.mp4"));
await RunFfmpeg("-f", "lavfi", "-i", "testsrc2=duration=2:size=320x240:rate=25", "-c:v", "libx264", "-pix_fmt", "yuv420p", P("clip2.mp4"));
await RunFfmpeg("-f", "lavfi", "-i", "sine=frequency=330:duration=5", "-ac", "2", P("audio.wav"));

File.WriteAllText(P("notes.txt"), "Lemonize smoke test\n\nThis is the first paragraph with some words.\n\nSecond paragraph, accentate: àèìòù.\n");
File.WriteAllText(P("readme.md"), "# Title\n\nSome **bold** text.\n\n## Section\n\nMore text here.\n");
File.WriteAllText(P("table.csv"), "name,qty,price\napple,3,1.5\n\"lemon, yellow\",10,0.4\n");
File.WriteAllText(P("subs.srt"), "1\n00:00:01,000 --> 00:00:02,500\nHello <i>world</i>\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond line\n");
Directory.CreateDirectory(P("folder"));
File.WriteAllText(P("folder/a.txt"), "a");
File.WriteAllText(P("folder/b.txt"), "b");
DocumentEngine.Write(DocumentEngine.Read(P("notes.txt")), P("doc.docx"));
DocumentEngine.Write(DocumentEngine.Read(P("notes.txt")), P("textdoc.pdf"));
PdfEngine.ImagesToPdf([P("photo.jpg"), P("image.png"), P("qr.png")], P("pages.pdf"));
var zip = ArchiveEngine.Create([P("folder")], "zip", _ => { }, default);
var tgz = ArchiveEngine.Create([P("notes.txt"), P("table.csv")], "tgz", _ => { }, default);
Console.WriteLine("Samples ready in " + work);

// ---------- Conversions ----------
string[] samples = ["photo.jpg", "image.png", "anim.gif", "video.mp4", "audio.wav", "notes.txt", "readme.md", "doc.docx", "textdoc.pdf",
    "pages.pdf", "table.csv", "subs.srt", Path.GetFileName(zip), Path.GetFileName(tgz)];
foreach (var sample in samples)
{
    foreach (var target in Converter.TargetsFor(P(sample)).Where(t => !t.Grouped))
    {
        if (target.Caption?.StartsWith("needs") == true) continue; // Optional software missing on this PC.
        await Check($"convert {sample} → {target.Id}", async () =>
            await Converter.ConvertAsync(P(sample), target.Id, new Job("t", null), default));
    }
}
await Check("convert epub → txt/pdf/docx", async () =>
{
    var epub = Directory.GetFiles(work, "notes*.epub").First();
    var outs = new List<string>();
    foreach (var t in new[] { "txt", "pdf", "docx" }) outs.AddRange(await Converter.ConvertAsync(epub, t, new Job("t", null), default));
    return outs;
});
await Check("archive folder+files → tar", () => Task.FromResult<IEnumerable<string>>([ArchiveEngine.Create([P("folder"), P("notes.txt")], "tar", _ => { }, default)]));
await Check("gzip single file", () => Task.FromResult<IEnumerable<string>>([ArchiveEngine.Create([P("notes.txt")], "gz", _ => { }, default)]));

// ---------- Tools ----------
foreach (var file in new[] { "photo.jpg", "image.png", "video.mp4", "audio.wav", "pages.pdf", "anim.gif" })
{
    await Check($"compress 50% {file}", async () =>
    {
        var job = new Job("t", null);
        var output = await CompressionEngine.CompressAsync(P(file), new CompressOptions(true, 0.5, 0), job, default);
        Console.WriteLine($"      {OutputPaths.FormatSize(new FileInfo(P(file)).Length)} → {OutputPaths.FormatSize(new FileInfo(output).Length)} ({job.Detail})");
        return [output];
    });
}
foreach (var level in new[] { CompressLevel.Light, CompressLevel.Medium, CompressLevel.Strong })
{
    await Check($"compress pages.pdf level {level}", async () =>
    {
        var output = await CompressionEngine.CompressAsync(P("pages.pdf"), new CompressOptions(false, 0, 0, level), new Job("t", null), default);
        Console.WriteLine($"      {OutputPaths.FormatSize(new FileInfo(P("pages.pdf")).Length)} → {OutputPaths.FormatSize(new FileInfo(output).Length)}");
        return [output];
    });
}
await Check("compress photo.jpg to 15 KB", async () =>
{
    var output = await CompressionEngine.CompressAsync(P("photo.jpg"), new CompressOptions(false, 0, 15 * 1024), new Job("t", null), default);
    if (new FileInfo(output).Length > 15 * 1024) throw new Exception("over target: " + new FileInfo(output).Length);
    return [output];
});

foreach (var file in new[] { "photo.jpg", "image.png", "video.mp4", "audio.wav", "pages.pdf", "doc.docx" })
{
    await Check($"metadata read {file}", async () =>
    {
        var entries = await MetadataEngine.ReadAsync(P(file));
        Console.WriteLine("      " + string.Join(" | ", entries.Take(8).Select(e => $"{e.Key}={e.Value}")));
        return [P(file)];
    });
    await Check($"metadata strip {file}", async () =>
    {
        var output = OutputPaths.ForToolResult(P(file), "(clean)");
        await MetadataEngine.StripAsync(P(file), output, default);
        return [output];
    });
}
await Check("metadata: EXIF gone after strip (jpg)", async () =>
{
    var clean = Directory.GetFiles(work, "photo (clean)*.jpg").First();
    var entries = await MetadataEngine.ReadAsync(clean);
    if (entries.Any(e => e.Value.Contains("Smoke Tester"))) throw new Exception("Artist still present");
    using var a = new MagickImage(P("photo.jpg"));
    using var b = new MagickImage(clean);
    if (a.Compare(b, ErrorMetric.RootMeanSquared) > 0.0001) throw new Exception("pixels changed");
    return [clean];
});
await Check("metadata write mp4 title + jpg artist + pdf title", async () =>
{
    var mp4 = P("video (copy).mp4");
    File.Copy(P("video.mp4"), mp4);
    await MetadataEngine.WriteAsync(mp4, new Dictionary<string, string> { ["title"] = "Edited title" }, default);
    var tags = (await Ffmpeg.ProbeAsync(mp4)).Tags;
    if (tags.GetValueOrDefault("title") != "Edited title") throw new Exception("mp4 title not written");
    var jpg = P("photo (copy).jpg");
    File.Copy(P("photo.jpg"), jpg);
    await MetadataEngine.WriteAsync(jpg, new Dictionary<string, string> { ["Artist"] = "New Artist", ["Modified"] = "2020-01-02 03:04:05" }, default);
    if (!(await MetadataEngine.ReadAsync(jpg)).Any(e => e.Value == "New Artist")) throw new Exception("jpg artist not written");
    if (File.GetLastWriteTime(jpg).Year != 2020) throw new Exception("timestamp not written");
    var pdf = P("pages (copy).pdf");
    File.Copy(P("pages.pdf"), pdf);
    await MetadataEngine.WriteAsync(pdf, new Dictionary<string, string> { ["Title"] = "My PDF" }, default);
    if (PdfEngine.ReadInfo(pdf)["Title"] != "My PDF") throw new Exception("pdf title not written");
    return [mp4, jpg, pdf];
});

await Check("QR read (png)", async () =>
{
    var codes = await QrEngine.ReadAsync(P("qr.png"));
    if (codes.FirstOrDefault()?.Text != "https://lemonize.app/hello") throw new Exception("got: " + string.Join(";", codes));
    return [P("qr.png")];
});
await Check("QR read (pdf page 1 has no code → empty ok)", async () =>
{
    await QrEngine.ReadAsync(P("pages.pdf"));
    return [P("pages.pdf")];
});

await Check("PDF non bloccato dopo conversione e anteprima", async () =>
{
    await Converter.ConvertAsync(P("textdoc.pdf"), "jpg", new Job("t", null), default);
    Lemonize.Interop.ShellThumbnails.Get(P("textdoc.pdf"), 96);
    await PdfEngine.PageCountAsync(P("textdoc.pdf"));
    // Renaming fails if any handle on the file is still open.
    File.Move(P("textdoc.pdf"), P("textdoc-renamed.pdf"));
    File.Move(P("textdoc-renamed.pdf"), P("textdoc.pdf"));
    return [P("textdoc.pdf")];
});
await Check("immagini in PDF: sempre A4 verticale", async () =>
{
    // A wide panorama, a tall strip and a 12 MP landscape photo: every page must be portrait A4.
    using (var wide = new MagickImage(MagickColors.SkyBlue, 3000, 800)) wide.Write(P("wide.jpg"), MagickFormat.Jpeg);
    using (var tall = new MagickImage(MagickColors.Orange, 600, 2400)) tall.Write(P("tall.png"), MagickFormat.Png);
    using (var big = new MagickImage(MagickColors.SeaGreen, 4000, 3000)) big.Write(P("big.jpg"), MagickFormat.Jpeg);
    var multi = OutputPaths.ForGroup([P("wide.jpg")], "A4-test", "pdf");
    PdfEngine.ImagesToPdf([P("wide.jpg"), P("tall.png"), P("big.jpg")], multi);
    var single = (await Converter.ConvertAsync(P("big.jpg"), "pdf", new Job("t", null), default))[0];
    foreach (var pdf in new[] { multi, single })
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(pdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        foreach (var page in doc.Pages)
        {
            var (wmm, hmm) = (page.Width.Millimeter, page.Height.Millimeter);
            if (Math.Abs(wmm - 210) > 0.5 || Math.Abs(hmm - 297) > 0.5) throw new Exception($"pagina {wmm:0}×{hmm:0} mm");
        }
        Console.WriteLine($"      {Path.GetFileName(pdf)}: {doc.PageCount} pagine A4 verticali, {OutputPaths.FormatSize(new FileInfo(pdf).Length)}");
    }
    return [multi, single];
});
await Check("pdf merge", () =>
{
    var o = P("Merged.pdf");
    PdfEngine.Merge([P("pages.pdf"), P("textdoc.pdf")], o);
    if (PdfEngine.PageCount(o) != PdfEngine.PageCount(P("pages.pdf")) + PdfEngine.PageCount(P("textdoc.pdf"))) throw new Exception("page count");
    return One(o);
});
await Check("pdf split ranges", () => Task.FromResult<IEnumerable<string>>(PdfEngine.Split(P("pages.pdf"), PdfEngine.ParseRanges("1-2, 3", 3))));
await Check("pdf assemble rotate/reorder", () =>
{
    var o = P("organized.pdf");
    PdfEngine.Assemble([new PdfPageRef(P("pages.pdf"), 2, 90), new PdfPageRef(P("pages.pdf"), 0, 0)], o);
    return One(o);
});
await Check("collage", () =>
{
    var o = P("Collage.jpg");
    ImageEngine.Collage([P("photo.jpg"), P("image.png"), P("qr.png")], o, 2, 300, 10, MagickColors.White);
    return One(o);
});

await Check("video edit trim+crop+speed+split", async () =>
    await MediaTools.EditVideoAsync(P("video.mp4"), new VideoEdit(0.5, 3.5, new CropRect(10, 10, 300, 200), 1.5, false, [2.0]), _ => { }, default));
await Check("video edit muted", async () =>
    await MediaTools.EditVideoAsync(P("video.mp4"), new VideoEdit(0, 2, null, 0.5, true, []), _ => { }, default));
await Check("video snapshot", async () => [await MediaTools.SnapshotAsync(P("video.mp4"), 1.2, default)]);
await Check("video join (with/without audio, different sizes)", async () => [await MediaTools.JoinAsync([P("video.mp4"), P("clip2.mp4")], _ => { }, default)]);
await Check("audio edit bleep+trim+normalize+mono+fades", async () =>
    [await MediaTools.EditAudioAsync(P("audio.wav"), new AudioEdit(0.5, 4.5, [(1, 1.5), (2, 2.2)], true, ChannelMode.Mono, 0.5, 0.5), _ => { }, default)]);
await Check("audio edit swap channels", async () =>
    [await MediaTools.EditAudioAsync(P("audio.wav"), new AudioEdit(0, 5, [], false, ChannelMode.Swap, 0, 0), _ => { }, default)]);
await Check("audio normalize", async () => [await MediaTools.NormalizeAsync(P("audio.wav"), _ => { }, default)]);
await Check("video normalize", async () => [await MediaTools.NormalizeAsync(P("video.mp4"), _ => { }, default)]);
await Check("audio join", async () =>
{
    var mp3 = (await Converter.ConvertAsync(P("audio.wav"), "mp3", new Job("t", null), default))[0];
    return [await MediaTools.JoinAsync([P("audio.wav"), mp3], _ => { }, default)];
});

await Check("nomi di output riservati tra lavori in parallelo", () =>
{
    string a, b;
    using (OutputPaths.ClaimScope())
    {
        // Two jobs converting "photo.heic" and "photo.jpg" both ask for "photo.webp" before either writes it.
        a = OutputPaths.For(P("photo.jpg"), "webp");
        b = OutputPaths.For(P("photo.jpg"), "webp");
    }
    if (a == b) throw new Exception($"stesso nome due volte: {a}");
    if (OutputPaths.For(P("photo.jpg"), "webp") != a) throw new Exception("prenotazione non rilasciata a fine lavoro");
    return One(P("photo.jpg"));
});
await Check("ffmpeg annullato: niente file parziale", async () =>
{
    var output = P("canceled.mp4");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await Ffmpeg.RunAsync(["-f", "lavfi", "-i", "testsrc=duration=600:size=1280x720", "-c:v", "libx264", "-preset", "veryslow", output], 600, null, cts.Token);
        throw new Exception("la codifica doveva essere annullata");
    }
    catch (OperationCanceledException)
    {
    }
    if (File.Exists(output)) throw new Exception("file parziale rimasto su disco");
    return [P("video.mp4")];
});
await Check("audio edit mono su sorgente mono: volume invariato", async () =>
{
    await RunFfmpeg("-f", "lavfi", "-i", "sine=duration=2", "-ac", "1", P("mono.wav"));
    var edited = await MediaTools.EditAudioAsync(P("mono.wav"), new AudioEdit(0, 2, [], false, ChannelMode.Mono, 0, 0), _ => { }, default);
    double Peak(string f)
    {
        var (_, _, err) = Ffmpeg.RunCaptureAsync(Ffmpeg.Exe, ["-hide_banner", "-i", f, "-af", "volumedetect", "-f", "null", "NUL"], default).GetAwaiter().GetResult();
        var line = err.Split('\n').First(l => l.Contains("max_volume"));
        return double.Parse(line.Split(':')[1].Replace("dB", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
    if (Math.Abs(Peak(edited) - Peak(P("mono.wav"))) > 1) throw new Exception($"volume cambiato: {Peak(P("mono.wav"))} → {Peak(edited)} dB");
    return [edited];
});
await Check("metadati di una cartella", async () =>
{
    var entries = await MetadataEngine.ReadAsync(P("folder"));
    if (!entries.Any(e => e.Key == "Created")) throw new Exception("data di creazione mancante");
    return [P("folder")];
});
await Check("CSV con NaN/Infinity → XLSX valido", () =>
{
    File.WriteAllText(P("special.csv"), "a,b,c\nNaN,Infinity,12\n");
    var o = P("special.xlsx");
    DocumentEngine.CsvToXlsx(P("special.csv"), o);
    using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(o, false);
    var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).ToList();
    if (errors.Count > 0) throw new Exception(errors[0].Description);
    return One(o);
});
await Check("scansiona documento: foglio storto trovato, raddrizzato su A4", async () =>
{
    // A sheet photographed from above at an angle, on a dark tablecloth, with a shadow across the paper.
    var sheet = new[] { new PointD(380, 260), new PointD(1620, 300), new PointD(1760, 2050), new PointD(250, 1990) };
    using (var photo = new MagickImage(new MagickColor("#4A3B30"), 2000, 2300))
    {
        new Drawables().FillColor(new MagickColor("#F2F0EA")).Polygon(sheet).Draw(photo);
        for (var line = 0; line < 12; line++)
            new Drawables().FillColor(MagickColors.Black).Rectangle(500, 450 + line * 90, 1450, 470 + line * 90).Draw(photo);
        new Drawables().FillColor(new MagickColor("#30000000")).Rectangle(1100, 0, 2000, 2300).Draw(photo);
        photo.Write(P("sheet-photo.jpg"), MagickFormat.Jpeg);
    }
    using (var photo = ScanEngine.LoadPhoto(P("sheet-photo.jpg"), 0))
    {
        var found = ScanEngine.DetectSheet(photo) ?? throw new Exception("foglio non trovato");
        for (var i = 0; i < 4; i++)
            if (Math.Abs(found[i].X - sheet[i].X) > 40 || Math.Abs(found[i].Y - sheet[i].Y) > 40)
                throw new Exception($"angolo {i}: ({found[i].X:0}, {found[i].Y:0}) invece di ({sheet[i].X}, {sheet[i].Y})");
    }
    using (var empty = new MagickImage(new MagickColor("#4A3B30"), 800, 600))
        if (ScanEngine.DetectSheet(empty) is not null) throw new Exception("foglio trovato in una foto senza fogli");

    // Evenly lit gray paper must stay even right up to the page edge: no darker frame.
    using (var flat = new MagickImage(new MagickColor("#B4B4B4"), 1200, 1600))
    using (var page = ScanEngine.Straighten(flat, [new(0, 0), new(1200, 0), new(1200, 1600), new(0, 1600)], 1600))
    {
        ScanEngine.Clean(page, ScanLook.BlackAndWhite, 1);
        double Mean(int x) { using var strip = (MagickImage)page.Clone(); strip.Crop(new MagickGeometry(x, 400, 6, 400)); return strip.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max * 255; }
        var (edge, inside) = (Mean(0), Mean((int)page.Width / 2));
        if (inside - edge > 4) throw new Exception($"cornice scura: bordo {edge:0}, interno {inside:0}");
    }

    var o = P("scan-a4.pdf");
    ScanEngine.ToPdf([
        new ScanPage(P("sheet-photo.jpg"), 0, null, ScanLook.BlackAndWhite, 0.5),
        new ScanPage(P("sheet-photo.jpg"), 90, null, ScanLook.Color, 1),
    ], o);
    using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
    {
        var sizes = doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => $"{p.Width.Millimeter:0}×{p.Height.Millimeter:0}").ToList();
        if (string.Join(" ", sizes) != "210×297 297×210") throw new Exception("pagine " + string.Join(" ", sizes));
    }
    using var render = new MagickImage((await PdfEngine.RenderAsync(o, [0], 100))[0]);
    // Paper fills the page: mostly white, with the dark text lines, and no tablecloth left around the edges.
    var mean = render.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    if (mean is < 0.7 or > 0.97) throw new Exception($"luminosità media {mean:0.00}");
    using var border = (MagickImage)render.Clone();
    border.Crop(new MagickGeometry(0, 0, render.Width, 8));
    var borderMean = border.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    if (borderMean < 0.8) throw new Exception($"bordo superiore scuro ({borderMean:0.00}): resta sfondo");
    Console.WriteLine($"      {OutputPaths.FormatSize(new FileInfo(o).Length)}, luminosità {mean:0.00}");
    return [o];
});
await Check("documento d'identità: fronte e retro a grandezza reale su A4", async () =>
{
    // A blue card on a dark table; the back photographed upright, so it must come out turned to landscape.
    using (var photo = new MagickImage(new MagickColor("#2A2A2A"), 1600, 1200))
    {
        new Drawables().FillColor(new MagickColor("#C8D8F0")).Polygon(new PointD(300, 300), new PointD(1290, 320), new PointD(1280, 940), new PointD(310, 930)).Draw(photo);
        photo.Write(P("card-front.jpg"), MagickFormat.Jpeg);
        photo.Rotate(90);
        photo.Write(P("card-back.jpg"), MagickFormat.Jpeg);
    }
    var o = P("card.pdf");
    ScanEngine.CardsToPdf([new ScanPage(P("card-front.jpg"), 0, null, ScanLook.Original, 0), new ScanPage(P("card-back.jpg"), 0, null, ScanLook.Original, 0)], o);
    if (PdfEngine.PageCount(o) != 1) throw new Exception("pagine: " + PdfEngine.PageCount(o));
    using var render = new MagickImage((await PdfEngine.RenderAsync(o, [0], 827))[0]);
    // At 100 dpi a card is 337 × 213 px, centered; front and back 276 px apart (54 + 70 mm).
    double Mean(int x, int y, int w, int h)
    {
        using var area = (MagickImage)render.Clone();
        area.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
        return area.Statistics().GetChannel(PixelChannel.Blue)!.Mean / Quantum.Max - area.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    }
    int cx = (int)render.Width / 2, top = ((int)render.Height - (2 * 213 + 276)) / 2;
    foreach (var (name, y) in new[] { ("fronte", top + 106), ("retro", top + 213 + 276 + 106) })
        if (Mean(cx - 140, y - 80, 280, 160) < 0.05) throw new Exception($"{name}: nessuna carta al suo posto");
    if (Mean(cx - 140, top + 213 + 60, 280, 150) > 0.02) throw new Exception("tra fronte e retro non è bianco");
    using (var corner = (MagickImage)render.Clone())
    {
        // The rounded corner leaves the very corner of the card's box white.
        corner.Crop(new MagickGeometry(cx - 168, top, 2, 2));
        if (corner.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max < 0.95) throw new Exception("angolo non arrotondato");
    }
    return [o];
});
await Check("PDF scansione in grigio salvata come JPEG RGB: compresso non resta bianco", async () =>
{
    // Like many scanners: gray content stored as a 3-channel JPEG. Re-encoding comes out single-channel.
    // ImageMagick would store gray pixels as a gray JPEG, so use the Windows encoder, which always writes three
    // channels. Paper grain and a high quality leave recompression something to gain.
    const int sw = 1240, sh = 1754;
    var pixels = new byte[sw * sh * 3];
    var random = new Random(1);
    for (var y = 0; y < sh; y++)
        for (var x = 0; x < sw; x++)
        {
            var v = (byte)(x is >= 100 and < 1140 && y is >= 100 and < 600 ? random.Next(0, 30) : random.Next(215, 255));
            pixels[(y * sw + x) * 3] = pixels[(y * sw + x) * 3 + 1] = pixels[(y * sw + x) * 3 + 2] = v;
        }
    var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 97 };
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
        sw, sh, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, pixels, sw * 3)));
    using (var fs = File.Create(P("scan.jpg"))) encoder.Save(fs);
    if (new MagickImageInfo(P("scan.jpg")).ColorSpace == ColorSpace.Gray) throw new Exception("campione non valido: JPEG già in grigio");
    using (var doc = new PdfSharp.Pdf.PdfDocument())
    {
        var page = doc.AddPage();
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
        using var ximage = PdfSharp.Drawing.XImage.FromFile(P("scan.jpg"));
        gfx.DrawImage(ximage, 0, 0, page.Width.Point, page.Height.Point);
        doc.Save(P("scan.pdf"));
    }
    var outputs = new List<string>();
    foreach (var level in new[] { CompressLevel.Light, CompressLevel.Strong })
    {
        var o = await CompressionEngine.CompressAsync(P("scan.pdf"), new CompressOptions(false, 0, 0, level), new Job("t", null), default);
        using var render = new MagickImage((await PdfEngine.RenderAsync(o, [0], 400))[0]);
        // The black rectangle covers ~23% of the page: a blank render would be almost pure white.
        var mean = render.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
        if (mean > 0.9) throw new Exception($"{level}: pagina bianca (luminosità media {mean:0.00})");
        outputs.Add(o);
    }
    return outputs;
});
await Check(".tar.gz non propone TAR.GZ come destinazione", () =>
{
    if (Converter.TargetsFor(P("backup.tar.gz")).Any(t => t.Id == "tgz")) throw new Exception("TAR.GZ proposto per un .tar.gz");
    return One(P("photo.jpg"));
});

if (args.Contains("--render-wheels")) WheelRender.Run(work, [
    ("audio-convert", [P("audio.wav")], WheelMode.Convert),
    ("image-tools", [P("photo.jpg")], WheelMode.Tools),
    ("images-tools", [P("photo.jpg"), P("image.png")], WheelMode.Tools),
    ("pdf-tools", [P("pages.pdf")], WheelMode.Tools),
    ("image-convert", [P("photo.jpg")], WheelMode.Convert),
    ("actions", [P("notes.txt")], WheelMode.Actions),
    ("mixed-convert", [P("notes.txt"), P("photo.jpg")], WheelMode.Convert),
]);

// Optional: real HEIC photos (e.g. from an iPhone) in a folder passed as --heic=<folder>.
if (args.FirstOrDefault(a => a.StartsWith("--heic=")) is { } heicArg)
{
    foreach (var heic in Directory.GetFiles(heicArg["--heic=".Length..], "*.heic"))
    {
        var copy = P(Path.GetFileName(heic));
        File.Copy(heic, copy, true);
        await Check($"HEIC → JPG {Path.GetFileName(heic)}", async () =>
        {
            var outputs = await Converter.ConvertAsync(copy, "jpg", new Job("t", null), default);
            using var jpg = new MagickImage(outputs[0]);
            Console.WriteLine($"      {jpg.Width} × {jpg.Height}, {OutputPaths.FormatSize(new FileInfo(outputs[0]).Length)}");
            return outputs;
        });
        await Check($"HEIC compress 50% {Path.GetFileName(heic)}", async () =>
            [await CompressionEngine.CompressAsync(copy, new CompressOptions(true, 0.5, 0), new Job("t", null), default)]);
        await Check($"HEIC metadata {Path.GetFileName(heic)}", async () =>
        {
            await MetadataEngine.ReadAsync(copy);
            return [copy];
        });
    }
}

var failed = results.Where(r => !r.Ok).ToList();
Console.WriteLine($"\n{results.Count - failed.Count}/{results.Count} passed");
foreach (var f in failed) Console.WriteLine($"FAIL {f.Name}: {f.Detail}");
return failed.Count == 0 ? 0 : 1;

static async Task RunFfmpeg(params string[] args)
{
    var (code, _, err) = await Ffmpeg.RunCaptureAsync(Ffmpeg.Exe, ["-y", "-v", "error", .. args], default);
    if (code != 0) throw new Exception(err);
}
