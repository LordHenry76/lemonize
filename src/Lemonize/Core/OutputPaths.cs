using System.IO;

namespace Lemonize.Core;

public static class OutputPaths
{
    public static string DirectoryFor(string input)
    {
        var settings = Settings.Current;
        if (settings.OutputLocation == OutputLocation.CustomFolder &&
            !string.IsNullOrWhiteSpace(settings.CustomOutputFolder) &&
            Directory.Exists(settings.CustomOutputFolder))
            return settings.CustomOutputFolder!;

        var full = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(full) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    /// <summary>"photo.heic" + "jpg" → "photo.jpg" next to the original, never overwriting.</summary>
    public static string For(string input, string newExtension, string? suffix = null)
    {
        var name = BaseName(input) + (suffix is null ? "" : " " + suffix);
        return Unique(Path.Combine(DirectoryFor(input), name + "." + newExtension.TrimStart('.')));
    }

    /// <summary>Output for an operation that combines several inputs, e.g. "Merged.pdf".</summary>
    public static string ForGroup(IReadOnlyList<string> inputs, string name, string extension) =>
        Unique(Path.Combine(DirectoryFor(inputs[0]), name + "." + extension.TrimStart('.')));

    public static string FolderFor(string input, string? suffix = null)
    {
        var path = UniqueDirectory(Path.Combine(DirectoryFor(input), BaseName(input) + (suffix is null ? "" : " " + suffix)));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Destination for tools that modify a file: the original itself, or a sibling copy.</summary>
    public static string ForToolResult(string input, string suffix, string? newExtension = null)
    {
        var ext = newExtension ?? FileKinds.Ext(input);
        if (Settings.Current.ToolsOverwriteOriginal && string.Equals(ext, FileKinds.Ext(input), StringComparison.OrdinalIgnoreCase))
            return input;
        return For(input, ext, suffix);
    }

    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return name[..^7];
        return Directory.Exists(path) ? name : Path.GetFileNameWithoutExtension(name);
    }

    // Names handed out inside a job stay reserved until that job ends: two jobs running at once
    // (e.g. "photo.heic" and "photo.jpg" → PNG) would otherwise both pick "photo.png" before either writes it.
    private static readonly HashSet<string> Claimed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<List<string>?> JobClaims = new();

    /// <summary>Reserves every name <see cref="Unique"/> returns in the current async flow until disposed.</summary>
    public static IDisposable ClaimScope()
    {
        var claims = new List<string>();
        JobClaims.Value = claims;
        return new ClaimRelease(claims);
    }

    private sealed class ClaimRelease(List<string> claims) : IDisposable
    {
        public void Dispose()
        {
            lock (Claimed)
            {
                foreach (var path in claims) Claimed.Remove(path);
                claims.Clear();
            }
            if (JobClaims.Value == claims) JobClaims.Value = null;
        }
    }

    private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path) || Claimed.Contains(path);

    public static string Unique(string path)
    {
        lock (Claimed)
        {
            var result = FirstFree(path);
            if (JobClaims.Value is { } claims && Claimed.Add(result)) claims.Add(result);
            return result;
        }
    }

    private static string FirstFree(string path)
    {
        if (!Taken(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
            ext = ".tar" + ext;
        }
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Taken(candidate)) return candidate;
        }
    }

    private static string UniqueDirectory(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (var i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Writes through a temp file so a failed tool never leaves a half-written original.</summary>
    public static void CommitTemp(string tempFile, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(tempFile, destination);
            return;
        }
        // File.Replace only works within one volume and temp files live in %TEMP%, so originals on
        // another drive, a USB stick or a network share need the result staged next to them first.
        var staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $"~lemonize-{Guid.NewGuid():N}.tmp");
        File.Move(tempFile, staged);
        try
        {
            File.Replace(staged, destination, null);
        }
        catch
        {
            if (File.Exists(staged)) File.Delete(staged);
            throw;
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}
