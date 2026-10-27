using System.Diagnostics;
using System.IO;
using System.Windows;
using Lemonize.Engines;
using Lemonize.Interop;
using Lemonize.Jobs;
using Lemonize.UI;

namespace Lemonize.Core;

public enum WheelMode { Convert, Tools, Actions }

public sealed record WheelItem(string Label, string? Caption, string? Glyph, Action Run);

public static class Glyphs
{
    public const string Compress = "";
    public const string Edit = "";
    public const string Info = "";
    public const string Clear = "";
    public const string Qr = "";
    public const string Scan = "";
    /// <summary>Not a font glyph: the wheel draws a small ID card (rounded corners, photo, text lines).</summary>
    public const string IdCard = "id-card";
    public const string Pdf = "";
    public const string Grid = "";
    public const string Join = "";
    public const string Cut = "";
    public const string Pages = "";
    public const string Volume = "";
    public const string Share = "";
    public const string Pin = "";
    public const string Copy = "";
    public const string Move = "";
    public const string Link = "";
    public const string Terminal = "";
    public const string Folder = "";
}

public static class Commands
{
    public static List<WheelItem> Items(WheelMode mode, IReadOnlyList<string> files) => mode switch
    {
        WheelMode.Convert => Converter.CommonTargets(files)
            .Select(t => new WheelItem(t.Label, t.Caption, null, () => Converter.Start(files, t)))
            .ToList(),
        WheelMode.Tools => Tools(files),
        _ => Actions(files),
    };

    private static List<WheelItem> Tools(IReadOnlyList<string> files)
    {
        var kinds = files.Select(FileKinds.Detect).Distinct().ToList();
        var kind = kinds.Count == 1 ? kinds[0] : (FileKind?)null;
        var single = files.Count == 1;
        var items = new List<WheelItem>();

        if (kinds.All(CompressionEngine.Supports))
            items.Add(new("Compress", null, Glyphs.Compress, () => new CompressWindow(files).Show()));

        switch (kind)
        {
            case FileKind.Image:
                if (single) items.Add(new("Edit", "crop · adjust · annotate", Glyphs.Edit, () => new ImageEditorWindow(files[0]).Show()));
                items.Add(new("Images to PDF", null, Glyphs.Pdf, () => ImagesToPdf(files)));
                items.Add(new("Scan sheet", "sheet · A4 PDF", Glyphs.Scan, () => new ScanWindow(files).Show()));
                if (files.Count <= 2) items.Add(new("ID card", "front · back", Glyphs.IdCard, () => new ScanWindow(files, cards: true).Show()));
                if (!single) items.Add(new("Collage", null, Glyphs.Grid, () => new CollageWindow(files).Show()));
                items.Add(new("Read QR", null, Glyphs.Qr, () => ReadCodes(files)));
                break;
            case FileKind.AnimatedImage:
                if (!single) items.Add(new("Images to PDF", null, Glyphs.Pdf, () => ImagesToPdf(files)));
                break;
            case FileKind.Video:
                if (single) items.Add(new("Edit", "trim · crop · speed · split", Glyphs.Edit, () => new VideoEditorWindow(files[0]).Show()));
                if (!single) items.Add(new("Join", null, Glyphs.Join, () => Join(files)));
                items.Add(new("Normalize audio", null, Glyphs.Volume, () => Normalize(files)));
                break;
            case FileKind.Audio:
                if (single) items.Add(new("Edit", "trim · bleep · channels", Glyphs.Edit, () => new AudioEditorWindow(files[0]).Show()));
                if (!single) items.Add(new("Join", null, Glyphs.Join, () => Join(files)));
                items.Add(new("Normalize", null, Glyphs.Volume, () => Normalize(files)));
                break;
            case FileKind.Pdf:
                if (!single) items.Add(new("Merge", null, Glyphs.Join, () => MergePdfs(files)));
                items.Add(new("Organize", single ? "reorder · rotate · delete" : "combine pages", Glyphs.Pages, () => new PdfOrganizerWindow(files).Show()));
                if (single) items.Add(new("Split", null, Glyphs.Cut, () => SplitPdf(files[0])));
                items.Add(new("Read QR", "first page", Glyphs.Qr, () => ReadCodes(files)));
                break;
        }

        if (single || kinds.All(k => k is not FileKind.Folder))
            items.Add(new("Metadata", single ? "view · edit" : "view", Glyphs.Info, () => new MetadataWindow(files).Show()));
        if (kinds.All(MetadataEngine.SupportsStrip))
            items.Add(new("Remove", "all metadata", Glyphs.Clear, () => StripMetadata(files)));
        return items;
    }

    private static List<WheelItem> Actions(IReadOnlyList<string> files) =>
    [
        new("Share", null, Glyphs.Share, () => ShareHelper.Share(files)),
        new("Float", null, Glyphs.Pin, () => FloatWindow.Create(files)),
        new("Copy", "to clipboard", Glyphs.Copy, () => CopyFiles(files)),
        new("Move to…", null, Glyphs.Move, () => MoveFiles(files)),
        new("Copy path", null, Glyphs.Link, () => Clipboard.SetText(string.Join(Environment.NewLine, files.Select(f => files.Count > 1 ? $"\"{f}\"" : f)))),
        new("Terminal", "open here", Glyphs.Terminal, () => OpenTerminal(files[0])),
    ];

    // ---------- Tool implementations ----------

    private static void Enqueue(string title, IReadOnlyList<string> files, Func<Job, CancellationToken, Task<IEnumerable<string>>> work)
    {
        JobQueue.Enqueue(title, files[0], async (job, token) => job.Outputs.AddRange(await work(job, token)));
    }

    private static void ImagesToPdf(IReadOnlyList<string> files)
    {
        var sorted = files.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        Enqueue(files.Count == 1 ? Loc.T("1 image → PDF") : Loc.F("{0} images → PDF", files.Count), sorted, (job, _) =>
        {
            var output = OutputPaths.ForGroup(sorted, sorted.Count == 1 ? OutputPaths.BaseName(sorted[0]) : Loc.T("Images"), "pdf");
            PdfEngine.ImagesToPdf(sorted, output, job.Report);
            return Task.FromResult<IEnumerable<string>>([output]);
        });
    }

    private static void MergePdfs(IReadOnlyList<string> files)
    {
        var sorted = files.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        Enqueue(Loc.F("Merge {0} PDFs", files.Count), sorted, (_, _) =>
        {
            var output = OutputPaths.ForGroup(sorted, Loc.T("Merged"), "pdf");
            PdfEngine.Merge(sorted, output);
            return Task.FromResult<IEnumerable<string>>([output]);
        });
    }

    private static void SplitPdf(string file)
    {
        int pages;
        try
        {
            pages = PdfEngine.PageCount(file);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Can't open PDF"), ex.Message);
            return;
        }
        var everyPage = Loc.T("every page");
        var ranges = Dialogs.Prompt(Loc.T("Split PDF"),
            Loc.F("Page ranges for the new files ({0} pages). Leave as is to split every page.\nExample: 1-3, 4, 5-{0}", pages),
            everyPage);
        if (ranges is null) return;
        List<List<int>> groups;
        try
        {
            groups = ranges.Trim().Equals(everyPage, StringComparison.OrdinalIgnoreCase) || ranges.Trim() == ""
                ? Enumerable.Range(0, pages).Select(i => new List<int> { i }).ToList()
                : PdfEngine.ParseRanges(ranges, pages);
        }
        catch (FormatException ex)
        {
            Dialogs.Error(Loc.T("Split PDF"), ex.Message);
            return;
        }
        Enqueue(Loc.F("Split {0}", Path.GetFileName(file)), [file], (_, _) => Task.FromResult<IEnumerable<string>>(PdfEngine.Split(file, groups)));
    }

    private static void Join(IReadOnlyList<string> files)
    {
        var sorted = files.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        Enqueue(Loc.F("Join {0} files", files.Count), sorted,
            async (job, token) => [await MediaTools.JoinAsync(sorted, job.Report, token)]);
    }

    private static void Normalize(IReadOnlyList<string> files)
    {
        foreach (var file in files)
            Enqueue(Loc.F("Normalize {0}", Path.GetFileName(file)), [file],
                async (job, token) => [await MediaTools.NormalizeAsync(file, job.Report, token)]);
    }

    private static void StripMetadata(IReadOnlyList<string> files)
    {
        foreach (var file in files)
            Enqueue(Loc.F("Remove metadata · {0}", Path.GetFileName(file)), [file], async (_, token) =>
            {
                var output = OutputPaths.ForToolResult(file, Loc.T("(clean)"));
                await MetadataEngine.StripAsync(file, output, token);
                return [output];
            });
    }

    private static async void ReadCodes(IReadOnlyList<string> files)
    {
        var lines = new List<(string, string)>();
        foreach (var file in files)
        {
            try
            {
                var results = await Task.Run(() => QrEngine.ReadAsync(file));
                if (results.Count == 0) lines.Add((Path.GetFileName(file), Loc.T("No QR code or barcode found.")));
                lines.AddRange(results.Select(r => ($"{Path.GetFileName(file)} · {r.Format}", r.Text)));
            }
            catch (Exception ex)
            {
                lines.Add((Path.GetFileName(file), Loc.F("Error: {0}", ex.Message)));
            }
        }
        InfoWindow.Show(Loc.T("Codes found"), lines);
    }

    // ---------- Actions ----------

    private static void CopyFiles(IReadOnlyList<string> files)
    {
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(files.ToArray());
        Clipboard.SetFileDropList(list);
    }

    private static void MoveFiles(IReadOnlyList<string> files)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = files.Count == 1 ? Loc.T("Move 1 item to…") : Loc.F("Move {0} items to…", files.Count),
            InitialDirectory = Settings.Current.LastMoveFolder ?? Path.GetDirectoryName(files[0]),
        };
        if (dialog.ShowDialog() != true) return;
        var destination = dialog.FolderName;
        Settings.Current.LastMoveFolder = destination;
        Settings.Current.Save();

        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var target = OutputPaths.Unique(Path.Combine(destination, Path.GetFileName(file.TrimEnd(Path.DirectorySeparatorChar))));
                if (!Directory.Exists(file)) File.Move(file, target);
                else if (SameVolume(file, target)) Directory.Move(file, target);
                else
                {
                    // Directory.Move can't cross drives: copy everything over, then remove the original.
                    CopyDirectory(file, target);
                    Directory.Delete(file, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (errors.Count > 0) Dialogs.Error(Loc.T("Some items were not moved"), string.Join("\n", errors));
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static void OpenTerminal(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var psi = File.Exists(wt)
            ? new ProcessStartInfo(wt) { ArgumentList = { "-d", dir } }
            : new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir };
        psi.UseShellExecute = false;
        Process.Start(psi);
    }

    public static void Reveal(string path)
    {
        if (Directory.Exists(path) && !File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
    }

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
