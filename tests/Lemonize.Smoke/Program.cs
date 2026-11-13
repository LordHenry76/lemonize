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
string CopyOf(string file, string tag)
{
    var copy = P($"{Path.GetFileNameWithoutExtension(file)} {tag}{Path.GetExtension(file)}");
    File.Copy(P(file), copy, true);
    return copy;
}

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
        // A compressed PDF takes its input's name, so PDFs work on a copy to keep the samples for later checks.
        var source = file.EndsWith(".pdf") ? CopyOf(file, "50") : P(file);
        var output = await CompressionEngine.CompressAsync(source, new CompressOptions(true, 0.5, 0), job, default);
        Console.WriteLine($"      {OutputPaths.FormatSize(new FileInfo(P(file)).Length)} → {OutputPaths.FormatSize(new FileInfo(output).Length)} ({job.Detail})");
        return [output];
    });
}
foreach (var level in new[] { CompressLevel.Light, CompressLevel.Medium, CompressLevel.Strong })
{
    await Check($"compress pages.pdf level {level}", async () =>
    {
        var output = await CompressionEngine.CompressAsync(CopyOf("pages.pdf", level.ToString()), new CompressOptions(false, 0, 0, level), new Job("t", null), default);
        Console.WriteLine($"      {OutputPaths.FormatSize(new FileInfo(P("pages.pdf")).Length)} → {OutputPaths.FormatSize(new FileInfo(output).Length)}");
        return [output];
    });
}
await Check("PDF compresso: prende il nome, l'originale resta intatto come _original", async () =>
{
    var input = CopyOf("pages.pdf", "rename");
    var kept = P("pages rename" + Loc.T("_original") + ".pdf");
    var before = File.ReadAllBytes(input);
    var output = await CompressionEngine.CompressAsync(input, new CompressOptions(false, 0, 0, CompressLevel.Strong), new Job("t", null), default);
    if (output != input) throw new Exception("nome risultato: " + output);
    if (!File.ReadAllBytes(kept).SequenceEqual(before)) throw new Exception("originale alterato");
    if (new FileInfo(output).Length >= before.Length) throw new Exception("non compresso");
    if (PdfEngine.PageCount(output) != PdfEngine.PageCount(kept)) throw new Exception("pagine");
    return [output, kept];
});
await Check("PDF compresso due volte: il primo originale non viene sovrascritto", () =>
{
    var input = P("twice.pdf");
    var first = P("twice" + Loc.T("_original") + ".pdf");
    File.WriteAllText(input, "versione 1");
    File.WriteAllText(P("t1.tmp"), "versione 2");
    OutputPaths.ReplaceKeepingOriginal(P("t1.tmp"), input, Loc.T("_original"));
    File.WriteAllText(P("t2.tmp"), "versione 3");
    OutputPaths.ReplaceKeepingOriginal(P("t2.tmp"), input, Loc.T("_original"));
    var second = P("twice" + Loc.T("_original") + " (2).pdf");
    if (File.ReadAllText(first) != "versione 1" || File.ReadAllText(second) != "versione 2" || File.ReadAllText(input) != "versione 3")
        throw new Exception("contenuti scambiati");
    return Task.FromResult<IEnumerable<string>>([input]);
});
await Check("PDF aperto in un altro programma: errore chiaro, file al suo posto", () =>
{
    var input = P("locked.pdf");
    File.WriteAllText(input, "originale");
    File.WriteAllText(P("t3.tmp"), "compresso");
    using (new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try
        {
            OutputPaths.ReplaceKeepingOriginal(P("t3.tmp"), input, Loc.T("_original"));
            throw new Exception("nessun errore");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("locked.pdf"))
        {
            // Expected.
        }
    }
    if (File.ReadAllText(input) != "originale") throw new Exception("originale alterato");
    if (File.Exists(P("locked" + Loc.T("_original") + ".pdf"))) throw new Exception("rinominato");
    if (Directory.EnumerateFiles(work, "~lemonize-*").Any()) throw new Exception("temporaneo rimasto");
    return Task.FromResult<IEnumerable<string>>([input]);
});
await Check("PDF compresso con cartella di destinazione: originale non rinominato", () =>
{
    var input = P("custom.pdf");
    var folder = Directory.CreateDirectory(P("custom-out")).FullName;
    File.WriteAllText(input, "originale");
    File.WriteAllText(P("t4.tmp"), "compresso");
    Settings.Current.OutputLocation = OutputLocation.CustomFolder;
    Settings.Current.CustomOutputFolder = folder;
    try
    {
        var output = OutputPaths.ReplaceKeepingOriginal(P("t4.tmp"), input, Loc.T("_original"));
        if (output != Path.Combine(folder, "custom.pdf") || File.ReadAllText(output) != "compresso") throw new Exception("risultato: " + output);
        if (File.ReadAllText(input) != "originale") throw new Exception("originale alterato");
        return Task.FromResult<IEnumerable<string>>([output]);
    }
    finally
    {
        Settings.Current.OutputLocation = OutputLocation.BesideOriginal;
    }
});
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
await Check("pdf merge con destinazione uguale a un file di partenza", () =>
{
    var a = CopyOf("pages.pdf", "merge-a");
    var b = CopyOf("textdoc.pdf", "merge-b");
    var expected = PdfEngine.PageCount(a) + PdfEngine.PageCount(b);
    PdfEngine.Merge([a, b], a);
    if (PdfEngine.PageCount(a) != expected) throw new Exception("page count");
    return Task.FromResult<IEnumerable<string>>([a]);
});
await Check("pdf split ranges", () => Task.FromResult<IEnumerable<string>>(PdfEngine.Split(P("pages.pdf"), PdfEngine.ParseRanges("1-2, 3", 3))));
await Check("pdf assemble rotate/reorder", () =>
{
    var o = P("organized.pdf");
    PdfEngine.Assemble([new PdfPageRef(P("pages.pdf"), 2, 90), new PdfPageRef(P("pages.pdf"), 0, 0)], o);
    return One(o);
});
await Check("pdf pagine fuori misura portate ad A4", async () =>
{
    // A scan saved at 72 dpi: the page is as big as the image in pixels (here 1440 × 2036 pt, about 508 × 718 mm),
    // the same page turned sideways by /Rotate, and an A5 receipt, which must stay as it is.
    var big = P("oversize.pdf");
    using (var doc = new PdfSharp.Pdf.PdfDocument())
    {
        foreach (var rotate in new[] { 0, 90 })
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(1440);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(2036);
            using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
                gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.Black, 0, 0, 1440, 1018);
            page.Rotate = rotate;
        }
        var a5 = doc.AddPage();
        a5.Width = PdfSharp.Drawing.XUnit.FromMillimeter(148);
        a5.Height = PdfSharp.Drawing.XUnit.FromMillimeter(210);
        doc.Save(big);
    }
    var sizes = PdfEngine.PageSizes(big);
    if (!sizes[0].IsLargerThanA4 || !sizes[1].IsLargerThanA4 || sizes[2].IsLargerThanA4 || sizes[2].IsA4
        || $"{sizes[0]}" != "508 × 718 mm" || $"{sizes[1]}" != "718 × 508 mm") throw new Exception("misure: " + string.Join(", ", sizes));
    if (!PdfEngine.PageSizes(P("pages.pdf")).All(s => s.IsA4)) throw new Exception("A4 non riconosciuto");

    var o = P("oversize A4.pdf");
    PdfEngine.Merge([P("pages.pdf"), big], o, fitA4: true);
    var after = PdfEngine.PageSizes(o);
    if (after.Count != 6 || !after.Take(5).All(s => s.IsA4) || $"{after[5]}" != "148 × 210 mm") throw new Exception("dopo: " + string.Join(", ", after));
    if (after[4].WidthMm < after[4].HeightMm) throw new Exception("la pagina ruotata non è più orizzontale");
    // Top half black, bottom half white, as in the original.
    using var render = new MagickImage((await PdfEngine.RenderAsync(o, [3], 210))[0]);
    using var top = render.CloneArea(0, 0, render.Width, render.Height / 2 - 4);
    using var bottom = render.CloneArea(0, (int)render.Height / 2 + 4, render.Width, render.Height / 2 - 4);
    var t = top.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    var b = bottom.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    if (t > 0.1 || b < 0.9) throw new Exception($"contenuto spostato: sopra {t:0.00}, sotto {b:0.00}");
    using var turned = new MagickImage((await PdfEngine.RenderAsync(o, [4], 297))[0]);
    // Turned 90° clockwise: the black half is now on the right.
    using var left = turned.CloneArea(0, 0, turned.Width / 2 - 4, turned.Height);
    using var right = turned.CloneArea((int)turned.Width / 2 + 4, 0, turned.Width / 2 - 4, turned.Height);
    var l = left.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    var r = right.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    if (r > 0.1 || l < 0.9) throw new Exception($"pagina ruotata sbagliata: sinistra {l:0.00}, destra {r:0.00}");

    // Without the option nothing changes; A4 pages are copied as they are.
    var kept = P("oversize kept.pdf");
    PdfEngine.Merge([big], kept);
    if (PdfEngine.PageSizes(kept).Take(2).Any(s => s.IsA4)) throw new Exception("adattata senza richiesta");
    return [o, kept];
});
await Check("pdf dpi delle scansioni", () =>
{
    // 1000 × 1414 pixels on a page as big as the pixels in points: 72 dpi, about 121 dpi once on A4.
    using (var image = new MagickImage(MagickColors.Gray, 1000, 1414)) image.Write(P("scan72.jpg"));
    var o = P("scan72.pdf");
    using (var doc = new PdfSharp.Pdf.PdfDocument())
    {
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(1000);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(1414);
        using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
        using (var ximage = PdfSharp.Drawing.XImage.FromFile(P("scan72.jpg")))
            gfx.DrawImage(ximage, 0, 0, 1000, 1414);
        doc.AddPage();
        doc.Save(o);
    }
    var infos = PdfEngine.PageInfos(o);
    if (infos[0].Dpi != 72) throw new Exception($"dpi: {infos[0].Dpi}");
    var onA4 = (int)Math.Round(infos[0].Dpi!.Value / infos[0].Size.A4Scale);
    if (Math.Abs(onA4 - 121) > 1) throw new Exception($"dpi su A4: {onA4}");
    if (infos[1].Dpi is not null) throw new Exception("dpi su una pagina senza immagini");
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
await Check("scansione a qualità Alta/Normale/Leggera: A4 a 300/200/150 dpi, peso decrescente", () =>
{
    var sizes = new List<long>();
    var outputs = new List<string>();
    foreach (var (quality, width) in new[] { (ScanQuality.High, 2480), (ScanQuality.Normal, 1654), (ScanQuality.Light, 1240) })
    {
        var o = P($"scan-{quality}.pdf");
        ScanEngine.ToPdf([new ScanPage(P("sheet-photo.jpg"), 0, null, ScanLook.Color, 0.5)], o, quality: quality);
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            var page = doc.Pages[0];
            if ($"{page.Width.Millimeter:0}×{page.Height.Millimeter:0}" != "210×297") throw new Exception($"{quality}: pagina non A4");
        }
        using (var photo = ScanEngine.LoadPhoto(P("sheet-photo.jpg"), 0))
        using (var jpeg = new MagickImage(ScanEngine.RenderPage(photo, ScanEngine.DetectSheet(photo)!, ScanLook.Color, 0.5, false, quality)))
            if (Math.Abs((int)jpeg.Width - width) > 2) throw new Exception($"{quality}: larghezza {jpeg.Width} invece di {width}");
        sizes.Add(new FileInfo(o).Length);
        outputs.Add(o);
    }
    if (!(sizes[0] > sizes[1] && sizes[1] > sizes[2])) throw new Exception("pesi " + string.Join(" / ", sizes.Select(OutputPaths.FormatSize)));
    Console.WriteLine("      " + string.Join(" / ", sizes.Select(OutputPaths.FormatSize)));
    return Task.FromResult<IEnumerable<string>>(outputs);
});
await Check("scansione riordinata prima di salvare: ordine nuovo, immagini non ricompresse", () =>
{
    // What the scan's review does: the two pages of scan-a4.pdf (portrait, landscape) swapped, then one deleted.
    var o = P("scan-ordered.pdf");
    PdfEngine.Assemble([new PdfPageRef(P("scan-a4.pdf"), 1, 0), new PdfPageRef(P("scan-a4.pdf"), 0, 0)], o);
    using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
    {
        var sizes = doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => $"{p.Width.Millimeter:0}×{p.Height.Millimeter:0}").ToList();
        if (string.Join(" ", sizes) != "297×210 210×297") throw new Exception("pagine " + string.Join(" ", sizes));
    }
    var (before, after) = (new FileInfo(P("scan-a4.pdf")).Length, new FileInfo(o).Length);
    if (Math.Abs(after - before) > before / 10) throw new Exception($"dimensione cambiata: {before} → {after}");
    var single = P("scan-one.pdf");
    PdfEngine.Assemble([new PdfPageRef(P("scan-a4.pdf"), 1, 90)], single);
    if (PdfEngine.PageCount(single) != 1) throw new Exception("pagina eliminata ancora presente");
    return One(o);
});
await Check("tono automatico: foto scura e ciano torna neutra", () =>
{
    // Dim cyan "paper" with darker "text": red is squeezed low, so the paper comes out cyan until each channel is stretched.
    using var photo = new MagickImage(new MagickColor("#5A8C96"), 600, 400);
    new Drawables().FillColor(new MagickColor("#1E2A2E")).Rectangle(100, 100, 500, 140).Rectangle(100, 200, 400, 240).Draw(photo);
    ScanEngine.AutoTone(photo);
    var stats = photo.Statistics();
    double Max(PixelChannel c) => stats.GetChannel(c)!.Maximum / Quantum.Max * 255;
    var (r, g, b) = (Max(PixelChannel.Red), Max(PixelChannel.Green), Max(PixelChannel.Blue));
    if (Math.Min(r, Math.Min(g, b)) < 245) throw new Exception($"carta non bianca: {r:0} {g:0} {b:0}");
    Console.WriteLine($"      carta {r:0} {g:0} {b:0}");
    var o = P("autotone.jpg");
    photo.Write(o, MagickFormat.Jpeg);
    return Task.FromResult<IEnumerable<string>>([o]);
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
await Check("due documenti d'identità: quattro facce in colonna su A4", async () =>
{
    // The photos of the previous check: four sides, front and back of two documents.
    var o = P("cards4.pdf");
    var side = (string f) => new ScanPage(P(f), 0, null, ScanLook.Original, 0);
    ScanEngine.CardsToPdf([side("card-front.jpg"), side("card-back.jpg"), side("card-front.jpg"), side("card-back.jpg")], o);
    if (PdfEngine.PageCount(o) != 1) throw new Exception("pagine: " + PdfEngine.PageCount(o));
    using var render = new MagickImage((await PdfEngine.RenderAsync(o, [0], 827))[0]);
    double Mean(int x, int y, int w, int h)
    {
        using var area = (MagickImage)render.Clone();
        area.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
        return area.Statistics().GetChannel(PixelChannel.Blue)!.Mean / Quantum.Max - area.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
    }
    // At 100 dpi a card is 213 px high and the sides stand 39 px (10 mm) apart, the column centered.
    int cx = (int)render.Width / 2, top = ((int)render.Height - (4 * 213 + 3 * 39)) / 2;
    for (var i = 0; i < 4; i++)
    {
        var y = top + i * (213 + 39);
        if (Mean(cx - 140, y + 26, 280, 160) < 0.05) throw new Exception($"faccia {i + 1}: nessuna carta al suo posto");
        if (i < 3 && Mean(cx - 140, y + 213 + 8, 280, 22) > 0.02) throw new Exception($"dopo la faccia {i + 1} non è bianco");
    }
    if (Mean(cx - 140, 0, 280, top - 6) > 0.02 || Mean(cx - 140, top + 4 * 213 + 3 * 39 + 6, 280, top - 12) > 0.02)
        throw new Exception("margini non bianchi");
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
        var o = await CompressionEngine.CompressAsync(CopyOf("scan.pdf", level.ToString()), new CompressOptions(false, 0, 0, level), new Job("t", null), default);
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

// ---------- Updates ----------
void Expect(string name, bool ok, string detail = "")
{
    results.Add((name, ok, detail));
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}  {detail}");
}
const string ReleaseJson = """
    { "tag_name": "v1.2.10",
      "body": "### English\n\n**What's new:** Thing\n\nDetails `x`\n\n**Update:** download it\n\n---\n\n### Italiano\n\n**Novità:** Cosa\n\n**Aggiornamento:** scaricalo\n\n🤖 Generated",
      "assets": [ { "name": "Lemonize-Setup-1.2.10.exe", "digest": "sha256:abc",
                    "browser_download_url": "https://github.com/LordHenry76/lemonize/releases/download/v1.2.10/Lemonize-Setup-1.2.10.exe" } ] }
    """;
Expect("Update: 1.0.10 newer than 1.0.9", Updater.IsNewer("1.0.10", "1.0.9"));
Expect("Update: same version not newer", !Updater.IsNewer("1.0.9", "1.0.9"));
Expect("Update: older version not newer", !Updater.IsNewer("1.0.8", "1.0.9"));
Expect("Update: unreadable version ignored", !Updater.IsNewer("beta", "1.0.9"));
var parsed = Updater.ParseRelease(ReleaseJson, "1.2.9", italian: false);
Expect("Update: release found", parsed is { Version: "1.2.10", Sha256: "abc" }, parsed?.ToString() ?? "null");
Expect("Update: English notes", parsed?.Notes == "What's new: Thing\n\nDetails x", parsed?.Notes ?? "");
Expect("Update: Italian notes", Updater.ParseRelease(ReleaseJson, "1.2.9", italian: true)?.Notes == "Novità: Cosa");
Expect("Update: current version gets no update", Updater.ParseRelease(ReleaseJson, "1.2.10", false) is null);
Expect("Update: release without installer ignored", Updater.ParseRelease(ReleaseJson.Replace("\"Lemonize-Setup-1.2.10.exe\"", "\"other.exe\""), "1.2.9", false) is null);
Expect("Update: installer from elsewhere refused", Updater.ParseRelease(ReleaseJson.Replace("github.com/LordHenry76", "example.com/x"), "1.2.9", false) is null);

var failed = results.Where(r => !r.Ok).ToList();
Console.WriteLine($"\n{results.Count - failed.Count}/{results.Count} passed");
foreach (var f in failed) Console.WriteLine($"FAIL {f.Name}: {f.Detail}");
return failed.Count == 0 ? 0 : 1;

static async Task RunFfmpeg(params string[] args)
{
    var (code, _, err) = await Ffmpeg.RunCaptureAsync(Ffmpeg.Exe, ["-y", "-v", "error", .. args], default);
    if (code != 0) throw new Exception(err);
}
