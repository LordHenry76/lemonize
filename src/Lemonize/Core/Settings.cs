using System.IO;
using System.Text.Json;

namespace Lemonize.Core;

public static class AppPaths
{
    public static string AppData { get; } = Dir(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lemonize");
    public static string LocalData { get; } = Dir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lemonize");
    public static string Temp => Dir(Path.GetTempPath(), "Lemonize");

    public static string NewTempFile(string extension) =>
        Path.Combine(Temp, Guid.NewGuid().ToString("N") + (extension.StartsWith('.') ? extension : "." + extension));

    public static string NewTempDir() => Dir(Temp, Guid.NewGuid().ToString("N"));

    private static string Dir(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }
}

public enum OutputLocation { BesideOriginal, CustomFolder }

public sealed class Settings
{
    public OutputLocation OutputLocation { get; set; } = OutputLocation.BesideOriginal;
    public string? CustomOutputFolder { get; set; }
    public bool RevealWhenDone { get; set; } = false;
    public bool ShowProgressWindow { get; set; } = true;
    public bool ToolsOverwriteOriginal { get; set; } = false;
    public bool EnableDragWheel { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;
    public bool ExplorerContextMenu { get; set; } = false;
    public int JpegQuality { get; set; } = 90;
    public int MaxParallelJobs { get; set; } = 2;
    public string? FfmpegPath { get; set; }
    public string? LastMoveFolder { get; set; }
    public bool FirstRunDone { get; set; }

    /// <summary>"auto" (follow Windows), "en" or "it".</summary>
    public string Language { get; set; } = "auto";

    private static readonly string FilePath = Path.Combine(AppPaths.AppData, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Current { get; private set; } = Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        lock (FilePath)
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
