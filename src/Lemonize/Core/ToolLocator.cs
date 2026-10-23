using System.IO;

namespace Lemonize.Core;

/// <summary>Finds optional external programs. Lookups are cached; call <see cref="Refresh"/> after installs.</summary>
public static class ToolLocator
{
    private static readonly Dictionary<string, string?> Cache = new();

    public static string FfmpegInstallDir => Path.Combine(AppPaths.LocalData, "ffmpeg", "bin");

    public static string? Ffmpeg => Get("ffmpeg", () => FindFfmpegTool("ffmpeg.exe"));
    public static string? Ffprobe => Get("ffprobe", () => FindFfmpegTool("ffprobe.exe"));
    public static string? LibreOffice => Get("soffice", FindLibreOffice);
    public static string? Ghostscript => Get("gs", FindGhostscript);
    public static string? Pandoc => Get("pandoc", () =>
        FirstExisting(Path.Combine(AppContext.BaseDirectory, "pandoc", "pandoc.exe"), // bundled (third_party/pandoc)
                      Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), "Pandoc", "pandoc.exe"),
                      Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "Pandoc", "pandoc.exe"))
        ?? OnPath("pandoc.exe"));

    public static bool HasWord => HasProgId("Word.Application");
    public static bool HasExcel => HasProgId("Excel.Application");
    public static bool HasPowerPoint => HasProgId("PowerPoint.Application");

    public static void Refresh()
    {
        lock (Cache) Cache.Clear();
    }

    private static string? Get(string key, Func<string?> find)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var value))
                Cache[key] = value = find();
            return value;
        }
    }

    private static bool HasProgId(string progId) =>
        Get("progid:" + progId, () => Type.GetTypeFromProgID(progId) is null ? null : "yes") is not null;

    private static string? FindFfmpegTool(string exe)
    {
        var custom = Settings.Current.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var dir = File.Exists(custom) ? Path.GetDirectoryName(custom)! : custom;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return FirstExisting(
                   Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe),
                   Path.Combine(AppContext.BaseDirectory, exe),
                   Path.Combine(FfmpegInstallDir, exe))
               ?? OnPath(exe)
               ?? FindWingetFfmpeg(exe);
    }

    private static string? FindWingetFfmpeg(string exe)
    {
        var packages = Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) return null;
        try
        {
            return Directory.EnumerateDirectories(packages, "*FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, exe, SearchOption.AllDirectories))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLibreOffice()
    {
        foreach (var root in new[] { Env(Environment.SpecialFolder.ProgramFiles), Env(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var path = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(path)) return path;
        }
        return OnPath("soffice.exe");
    }

    private static string? FindGhostscript()
    {
        // Copy shipped next to Lemonize (third_party/ghostscript), preferred over any installed one.
        var bundled = Path.Combine(AppContext.BaseDirectory, "ghostscript", "bin", "gswin64c.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var root in new[] { Env(Environment.SpecialFolder.ProgramFiles), Env(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var gsRoot = Path.Combine(root, "gs");
            if (!Directory.Exists(gsRoot)) continue;
            var found = Directory.EnumerateDirectories(gsRoot)
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "bin", "gswin64c.exe"))
                .FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        return OnPath("gswin64c.exe");
    }

    private static string? OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string Env(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
}
