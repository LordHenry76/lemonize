using System.IO;

namespace Lemonize.Core;

public enum FileKind
{
    Image,
    AnimatedImage,
    Video,
    Audio,
    Pdf,
    WordDocument,
    Text,
    Ebook,
    Spreadsheet,
    Csv,
    Presentation,
    AppleIWork,
    Subtitle,
    Archive,
    Folder,
    Other,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Map(FileKind.Image, "jpg", "jpeg", "jfif", "png", "heic", "heif", "webp", "tif", "tiff", "bmp", "svg", "ico", "avif", "jxl", "tga", "psd");
        Map(FileKind.AnimatedImage, "gif");
        Map(FileKind.Video, "mp4", "m4v", "mov", "mkv", "avi", "wmv", "webm", "flv", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts", "ogv");
        Map(FileKind.Audio, "mp3", "m4a", "aac", "wav", "flac", "ogg", "oga", "opus", "aif", "aiff", "wma", "alac", "amr");
        Map(FileKind.Pdf, "pdf");
        Map(FileKind.WordDocument, "docx", "doc", "odt", "rtf");
        Map(FileKind.Text, "txt", "md", "markdown", "log");
        Map(FileKind.Ebook, "epub");
        Map(FileKind.Spreadsheet, "xlsx", "xls", "ods");
        Map(FileKind.Csv, "csv", "tsv");
        Map(FileKind.Presentation, "pptx", "ppt", "odp");
        Map(FileKind.AppleIWork, "pages", "numbers", "key");
        Map(FileKind.Subtitle, "srt", "vtt");
        Map(FileKind.Archive, "zip", "tar", "gz", "tgz", "rar", "7z", "bz2", "xz");
    }

    private static void Map(FileKind kind, params string[] extensions)
    {
        foreach (var ext in extensions) ByExtension[ext] = kind;
    }

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind Detect(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        return ByExtension.TryGetValue(Ext(path), out var kind) ? kind : FileKind.Other;
    }

    public static bool IsImageLike(FileKind kind) => kind is FileKind.Image or FileKind.AnimatedImage;
    public static bool IsMedia(FileKind kind) => kind is FileKind.Video or FileKind.Audio or FileKind.AnimatedImage;
}
