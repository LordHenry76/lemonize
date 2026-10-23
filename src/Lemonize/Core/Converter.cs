using System.IO;
using Lemonize.Engines;
using Lemonize.Jobs;

namespace Lemonize.Core;

/// <summary>A conversion target offered in the wheel.</summary>
public sealed record Target(string Id, string Label, string? Caption = null, bool Grouped = false);

public static class Converter
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["jpg"] = "JPEG", ["png"] = "PNG", ["heic"] = "HEIC", ["webp"] = "WebP", ["tiff"] = "TIFF", ["bmp"] = "BMP",
        ["gif"] = "GIF", ["svg"] = "SVG", ["pdf"] = "PDF", ["ico"] = "ICO", ["avif"] = "AVIF",
        ["mp4"] = "MP4", ["mov"] = "MOV", ["mkv"] = "MKV", ["avi"] = "AVI", ["wmv"] = "WMV", ["webm"] = "WebM",
        ["mp3"] = "MP3", ["m4a"] = "M4A", ["wav"] = "WAV", ["flac"] = "FLAC", ["ogg"] = "OGG", ["opus"] = "Opus", ["aiff"] = "AIFF",
        ["txt"] = "TXT", ["docx"] = "DOCX", ["epub"] = "EPUB", ["csv"] = "CSV", ["xlsx"] = "XLSX", ["pptx"] = "PPTX",
        ["srt"] = "SRT", ["vtt"] = "VTT", ["zip"] = "ZIP", ["tar"] = "TAR", ["tgz"] = "TAR.GZ", ["gz"] = "GZIP",
        ["extract"] = "Extract",
    };

    private static Target T(string id, string? caption = null, bool grouped = false) => new(id, Labels.GetValueOrDefault(id, id.ToUpperInvariant()), caption, grouped);

    private static string Normalized(string ext) => ext switch
    {
        "jpeg" or "jfif" => "jpg",
        "tif" => "tiff",
        "aif" => "aiff",
        "heif" => "heic",
        "oga" => "ogg",
        "aac" => "m4a",
        _ => ext,
    };

    private static IEnumerable<Target> ImageTargets() => ImageEngine.Targets.Select(t => T(t));

    public static List<Target> TargetsFor(string path)
    {
        var kind = FileKinds.Detect(path);
        var ext = Normalized(FileKinds.Ext(path));
        var office = OfficeEngine.IsAvailable(kind) ? null : "needs Office";
        IEnumerable<Target> targets = kind switch
        {
            FileKind.Image => ImageTargets(),
            FileKind.AnimatedImage => ImageTargets().Concat([T("mp4", "video"), T("webm", "video")]),
            FileKind.Video => MediaEngine.VideoTargets.Select(t => T(t, t == "gif" ? "animated" : null))
                .Concat(MediaEngine.AudioTargets.Select(t => T(t, "audio only"))),
            FileKind.Audio => MediaEngine.AudioTargets.Select(t => T(t)),
            FileKind.Pdf => [T("png", "pages"), T("jpg", "pages"), T("txt", "text"), T("docx", "text"), T("epub")],
            FileKind.WordDocument => [T("pdf", ext == "docx" ? null : office), T("docx", office), T("txt"), T("epub")],
            FileKind.Text => [T("pdf"), T("docx"), T("epub")],
            FileKind.Ebook => [T("pdf"), T("docx"), T("txt")],
            FileKind.Spreadsheet => [T("csv"), T("xlsx", office), T("pdf", office)],
            FileKind.Csv => [T("xlsx"), T("pdf", office)],
            FileKind.Presentation => [T("pdf", office), T("pptx", office)],
            FileKind.AppleIWork => [T("pdf", "preview"), T("jpg", "preview")],
            FileKind.Subtitle => SubtitleEngine.Targets.Select(t => T(t)),
            FileKind.Archive => [T("extract"), T("zip"), T("tar"), T("tgz")],
            _ => [],
        };
        var list = targets.Where(t => t.Id != ext).ToList();
        if (kind != FileKind.Archive)
        {
            list.Add(T("zip", null, grouped: true));
            if (kind is FileKind.Folder or FileKind.Other)
            {
                list.Add(T("tar", null, grouped: true));
                list.Add(T("tgz", null, grouped: true));
                if (kind == FileKind.Other) list.Add(T("gz"));
            }
        }
        return list;
    }

    /// <summary>Targets every selected file supports, in the order of the first file's list.</summary>
    public static List<Target> CommonTargets(IReadOnlyList<string> files)
    {
        var lists = files.Select(TargetsFor).ToList();
        var mixed = files.Select(FileKinds.Detect).Distinct().Count() > 1;
        return lists[0]
            .Where(t => lists.All(l => l.Any(o => o.Id == t.Id)))
            .Where(t => !mixed || t.Grouped || FileKinds.Detect(files[0]) != FileKind.Archive)
            .Select(t => mixed ? t with { Caption = t.Grouped ? t.Caption : null } : t)
            .ToList();
    }

    public static void Start(IReadOnlyList<string> files, Target target)
    {

        if (target.Grouped || (target.Id is "tar" or "tgz" && files.Any(f => FileKinds.Detect(f) is FileKind.Folder or FileKind.Other)))
        {
            var title = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} items";
            JobQueue.Enqueue($"{title} → {target.Label}", files[0], (job, token) =>
            {
                job.Outputs.Add(ArchiveEngine.Create(files, target.Id, job.Report, token));
                return Task.CompletedTask;
            });
            return;
        }

        foreach (var file in files)
        {
            JobQueue.Enqueue($"{Path.GetFileName(file)} → {target.Label}", file,
                async (job, token) => job.Outputs.AddRange(await ConvertAsync(file, target.Id, job, token)));
        }
    }

    public static async Task<IReadOnlyList<string>> ConvertAsync(string input, string target, Job job, CancellationToken token)
    {
        var kind = FileKinds.Detect(input);
        var ext = FileKinds.Ext(input);

        switch (kind)
        {
            case FileKind.Archive:
                return [target == "extract" ? ArchiveEngine.Extract(input, job.Report, token) : ArchiveEngine.Repack(input, target, job.Report, token)];
            case FileKind.Pdf when target is "png" or "jpg":
                return await PdfEngine.ToImagesAsync(input, target, job.Report, token);
            case FileKind.AppleIWork:
                return [Single(input, target, o => DocumentEngine.ExportIWorkPreview(input, o))];
            case FileKind.Subtitle:
                return [Single(input, target, o => SubtitleEngine.Convert(input, o, target))];
            case FileKind.Other when target == "gz":
                return [ArchiveEngine.Create([input], "gz", job.Report, token)];
            case FileKind.AnimatedImage when target is "mp4" or "webm":
            {
                var output = OutputPaths.For(input, target);
                await MediaEngine.GifToVideoAsync(input, output, target, job.Report, token);
                return [output];
            }
            case FileKind.Image or FileKind.AnimatedImage:
                return [Single(input, target, o => ImageEngine.Convert(input, o, target))];
            case FileKind.Video or FileKind.Audio:
            {
                var output = OutputPaths.For(input, target);
                await MediaEngine.ConvertAsync(input, output, target, job.Report, token);
                return [output];
            }
            case FileKind.Spreadsheet when target == "csv":
                if (ext == "xlsx") return DocumentEngine.XlsxToCsv(input);
                return await ViaTempAsync(input, "xlsx", token, temp => Task.FromResult<IReadOnlyList<string>>(DocumentEngine.XlsxToCsv(temp)));
            case FileKind.Csv when target == "xlsx":
                return [Single(input, target, o => DocumentEngine.CsvToXlsx(input, o))];
            case FileKind.Spreadsheet or FileKind.Csv or FileKind.Presentation:
                return [await OfficeAsync(input, target, token)];
            case FileKind.WordDocument when target is "pdf" or "docx" && (ext != "docx" || OfficeEngine.IsAvailable(kind)):
                return [await OfficeAsync(input, target, token)];
            case FileKind.WordDocument when ext != "docx":
                // .doc/.odt/.rtf → .docx first, then the built-in writers.
                return await ViaTempAsync(input, "docx", token, temp => DocumentAsync(temp, input, target, token));
            default:
                return [.. await DocumentAsync(input, input, target, token)];
        }
    }

    private static async Task<IReadOnlyList<string>> DocumentAsync(string source, string originalName, string target, CancellationToken token)
    {
        var output = OutputPaths.For(originalName, target);
        if (DocumentEngine.CanUsePandoc(source, target))
            await DocumentEngine.PandocAsync(source, output, token);
        else
            DocumentEngine.Write(DocumentEngine.Read(source) with { Title = OutputPaths.BaseName(originalName) }, output);
        return [output];
    }

    private static async Task<string> OfficeAsync(string input, string target, CancellationToken token)
    {
        var output = OutputPaths.For(input, target);
        await OfficeEngine.ConvertAsync(input, output, token);
        return output;
    }

    private static async Task<IReadOnlyList<string>> ViaTempAsync(string input, string tempExt, CancellationToken token,
        Func<string, Task<IReadOnlyList<string>>> next)
    {
        var tempDir = AppPaths.NewTempDir();
        var temp = Path.Combine(tempDir, OutputPaths.BaseName(input) + "." + tempExt);
        try
        {
            await OfficeEngine.ConvertAsync(input, temp, token);
            var results = await next(temp);
            // Outputs derived from the temp file land in the temp folder; move them next to the original.
            return results.Select(r =>
            {
                if (!r.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase)) return r;
                var destination = OutputPaths.For(input, FileKinds.Ext(r), Path.GetFileNameWithoutExtension(r)[OutputPaths.BaseName(input).Length..].Trim() is { Length: > 0 } s ? s : null);
                File.Move(r, destination);
                return destination;
            }).ToList();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string Single(string input, string target, Action<string> write)
    {
        var output = OutputPaths.For(input, target);
        try
        {
            write(output);
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
        return output;
    }
}
