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
PdfEngine.ImagesToPdf([P("photo.jpg"), P("image.png"), P("qr.png")], P("pages.pdf"), fitToA4: true);
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
