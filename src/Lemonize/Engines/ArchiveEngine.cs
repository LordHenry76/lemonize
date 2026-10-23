using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using Lemonize.Core;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Lemonize.Engines;

public static class ArchiveEngine
{
    /// <summary>Packs files and folders into a .zip, .tar, .tar.gz or (single file) .gz.</summary>
    public static string Create(IReadOnlyList<string> inputs, string format, Action<double> progress, CancellationToken token)
    {
        var name = inputs.Count == 1 ? OutputPaths.BaseName(inputs[0]) : Loc.T("Archive");
        var extension = format switch { "tgz" => "tar.gz", _ => format };
        var output = OutputPaths.ForGroup(inputs, name, extension);
        var entries = Enumerate(inputs).ToList();
        var total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;

        try
        {
            switch (format)
            {
                case "zip":
                {
                    using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
                    foreach (var (path, entryName, length) in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                        progress((done += length) / (double)total);
                    }
                    break;
                }
                case "tar" or "tgz":
                {
                    using var file = File.Create(output);
                    using Stream stream = format == "tgz" ? new GZipStream(file, CompressionLevel.Optimal) : file;
                    using var tar = new TarWriter(stream, TarEntryFormat.Pax);
                    foreach (var (path, entryName, length) in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        tar.WriteEntry(path, entryName);
                        progress((done += length) / (double)total);
                    }
                    break;
                }
                case "gz":
                {
                    if (entries.Count != 1) throw new InvalidOperationException(Loc.T("GZIP holds a single file. Use TAR.GZ for several files."));
                    var gzOutput = OutputPaths.Unique(Path.Combine(OutputPaths.DirectoryFor(inputs[0]), Path.GetFileName(inputs[0]) + ".gz"));
                    File.Delete(output);
                    output = gzOutput;
                    using var source = File.OpenRead(inputs[0]);
                    using var gz = new GZipStream(File.Create(output), CompressionLevel.Optimal);
                    source.CopyTo(gz);
                    break;
                }
                default:
                    throw new NotSupportedException(format);
            }
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
        return output;
    }

    private static IEnumerable<(string Path, string EntryName, long Length)> Enumerate(IEnumerable<string> inputs)
    {
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar))!;
                foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                    yield return (file, Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length);
            }
            else if (File.Exists(input))
            {
                yield return (input, Path.GetFileName(input), new FileInfo(input).Length);
            }
        }
    }

    /// <summary>Extracts into a new folder next to the archive and returns that folder.</summary>
    public static string Extract(string input, Action<double> progress, CancellationToken token)
    {
        var ext = FileKinds.Ext(input);
        if (ext == "gz" && !input.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var output = OutputPaths.Unique(Path.Combine(OutputPaths.DirectoryFor(input), Path.GetFileNameWithoutExtension(input)));
            using var gz = new GZipStream(File.OpenRead(input), CompressionMode.Decompress);
            using var target = File.Create(output);
            gz.CopyTo(target);
            return output;
        }

        var folder = OutputPaths.FolderFor(input);
        ExtractTo(input, folder, progress, token);
        return folder;
    }

    /// <summary>Converts between archive formats by extracting to a temporary folder and repacking.</summary>
    public static string Repack(string input, string format, Action<double> progress, CancellationToken token)
    {
        var temp = AppPaths.NewTempDir();
        try
        {
            var staging = Path.Combine(temp, OutputPaths.BaseName(input));
            Directory.CreateDirectory(staging);
            var extracted = ExtractTo(input, staging, null, token);
            progress(0.5);
            var name = OutputPaths.BaseName(input);
            var outputExt = format == "tgz" ? "tar.gz" : format;
            var output = OutputPaths.ForGroup([input], name, outputExt);
            var packed = Create(Directory.EnumerateFileSystemEntries(extracted).ToList(), format, p => progress(0.5 + p / 2), token);
            File.Move(packed, output);
            return output;
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static string ExtractTo(string input, string folder, Action<double>? progress, CancellationToken token)
    {
        var name = Path.GetFileName(input).ToLowerInvariant();
        if (name.EndsWith(".tar"))
        {
            TarFile.ExtractToDirectory(input, folder, overwriteFiles: true);
            return folder;
        }
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz"))
        {
            using var gz = new GZipStream(File.OpenRead(input), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, folder, overwriteFiles: true);
            return folder;
        }

        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.OpenArchive(input);
        var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = files[i];
            var destination = Path.GetFullPath(Path.Combine(folder, entry.Key ?? $"file{i}"));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var source = entry.OpenEntryStream())
            using (var target = File.Create(destination))
                source.CopyTo(target);
            progress?.Invoke((i + 1.0) / files.Count);
        }
        return folder;
    }
}
