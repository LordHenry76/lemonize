using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using ImageMagick;
using Lemonize.Core;

namespace Lemonize.Engines;

public sealed record MetaEntry(string Group, string Key, string Value);

public static class MetadataEngine
{
    public const string FileGroup = "File";
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly string[] MediaFields = ["title", "artist", "album", "album_artist", "date", "genre", "track", "comment"];
    private static readonly string[] ImageFields = ["Artist", "Copyright", "Description", "Date taken"];
    private static readonly string[] PdfFields = ["Title", "Author", "Subject", "Keywords", "Creator"];
    private static readonly string[] OfficeFields = ["Title", "Author", "Subject", "Keywords", "Description", "Category"];
    public static readonly string[] FileFields = ["Created", "Modified"];

    public static bool SupportsStrip(FileKind kind) =>
        kind is FileKind.Image or FileKind.AnimatedImage or FileKind.Video or FileKind.Audio or FileKind.Pdf
            or FileKind.WordDocument or FileKind.Spreadsheet or FileKind.Presentation;

    /// <summary>Fields the editor exposes for this file; file timestamps are always editable.</summary>
    public static string[] EditableFields(string path) => KindFields(path).Concat(FileFields).ToArray();

    private static string[] KindFields(string path) => FileKinds.Detect(path) switch
    {
        FileKind.Video or FileKind.Audio => MediaFields,
        FileKind.Image when FileKinds.Ext(path) is "jpg" or "jpeg" or "jfif" or "tif" or "tiff" or "webp" or "heic" or "png" => ImageFields,
        FileKind.Pdf => PdfFields,
        FileKind.WordDocument or FileKind.Spreadsheet or FileKind.Presentation when IsOpenXml(path) => OfficeFields,
        _ => [],
    };

    private static bool IsOpenXml(string path) => FileKinds.Ext(path) is "docx" or "xlsx" or "pptx";

    public static async Task<List<MetaEntry>> ReadAsync(string path, CancellationToken token = default)
    {
        var entries = new List<MetaEntry>();
        if (Directory.Exists(path))
        {
            // Folders are offered the editor too: name and timestamps only (FileInfo.Length would throw).
            var dir = new DirectoryInfo(path);
            entries.Add(new(FileGroup, "Name", dir.Name));
            entries.Add(new(FileGroup, "Created", dir.CreationTime.ToString(DateFormat)));
            entries.Add(new(FileGroup, "Modified", dir.LastWriteTime.ToString(DateFormat)));
            return entries;
        }
        var info = new FileInfo(path);
        entries.Add(new(FileGroup, "Name", info.Name));
        entries.Add(new(FileGroup, "Size", $"{OutputPaths.FormatSize(info.Length)} ({info.Length:N0} bytes)"));
        entries.Add(new(FileGroup, "Created", info.CreationTime.ToString(DateFormat)));
        entries.Add(new(FileGroup, "Modified", info.LastWriteTime.ToString(DateFormat)));

        switch (FileKinds.Detect(path))
        {
            case FileKind.Image or FileKind.AnimatedImage:
                entries.AddRange(ReadImage(path));
                break;
            case FileKind.Video or FileKind.Audio:
                entries.AddRange(await ReadMediaAsync(path, token));
                break;
            case FileKind.Pdf:
                entries.AddRange(PdfEngine.ReadInfo(path).Select(kv => new MetaEntry("Document", kv.Key, kv.Value)));
                break;
            case FileKind.WordDocument or FileKind.Spreadsheet or FileKind.Presentation when IsOpenXml(path):
                entries.AddRange(ReadOffice(path));
                break;
        }
        return entries;
    }

    private static IEnumerable<MetaEntry> ReadImage(string path)
    {
        using var image = new MagickImage(path, ImageEngine.ReadSettingsFor(path));
        yield return new("Image", "Format", image.Format.ToString());
        yield return new("Image", "Dimensions", $"{image.Width} × {image.Height}");
        yield return new("Image", "Color space", image.ColorSpace.ToString());
        yield return new("Image", "Bit depth", image.Depth.ToString());
        if (image.Density.X > 0) yield return new("Image", "Resolution", $"{image.Density.X:0} dpi");

        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            foreach (var value in exif.Values)
            {
                var text = FormatExif(value.GetValue());
                if (string.IsNullOrWhiteSpace(text) || text.Length > 300) continue;
                yield return new("EXIF", value.Tag.ToString(), text);
            }
            var gps = GpsLocation(exif);
            if (gps is not null) yield return new("EXIF", "GPS location", gps);
        }

        var iptc = image.GetIptcProfile();
        if (iptc is not null)
            foreach (var value in iptc.Values)
                yield return new("IPTC", value.Tag.ToString(), value.Value);

        if (image.GetXmpProfile() is { } xmp) yield return new("XMP", "XMP packet", $"{xmp.ToByteArray().Length:N0} bytes");
        if (image.GetColorProfile() is { } icc) yield return new("Image", "Color profile", icc.Description ?? icc.Model ?? "embedded");
        foreach (var name in image.AttributeNames)
        {
            if (name.StartsWith("png:", StringComparison.Ordinal) || name.StartsWith("exif:", StringComparison.Ordinal)) continue;
            var attr = image.GetAttribute(name);
            if (!string.IsNullOrWhiteSpace(attr) && attr.Length < 300) yield return new("Attributes", name, attr);
        }
    }

    private static string FormatExif(object? value) => value switch
    {
        null => "",
        string s => s.Trim('\0', ' '),
        Rational r => r.Denominator == 0 ? "0" : (r.Numerator / (double)r.Denominator).ToString("0.####", CultureInfo.InvariantCulture),
        SignedRational r => r.Denominator == 0 ? "0" : (r.Numerator / (double)r.Denominator).ToString("0.####", CultureInfo.InvariantCulture),
        Rational[] arr => string.Join(", ", arr.Select(r => FormatExif(r))),
        byte[] bytes => bytes.Length <= 16 ? BitConverter.ToString(bytes) : $"{bytes.Length} bytes",
        Array arr => string.Join(", ", arr.Cast<object>()),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string? GpsLocation(IExifProfile exif)
    {
        static double? Degrees(IExifValue<Rational[]>? v) =>
            v?.Value is { Length: 3 } r ? r[0].ToDouble() + r[1].ToDouble() / 60 + r[2].ToDouble() / 3600 : null;

        var lat = Degrees(exif.GetValue(ExifTag.GPSLatitude));
        var lon = Degrees(exif.GetValue(ExifTag.GPSLongitude));
        if (lat is null || lon is null) return null;
        if (exif.GetValue(ExifTag.GPSLatitudeRef)?.Value == "S") lat = -lat;
        if (exif.GetValue(ExifTag.GPSLongitudeRef)?.Value == "W") lon = -lon;
        return string.Create(CultureInfo.InvariantCulture, $"{lat:0.######}, {lon:0.######}");
    }

    private static async Task<IEnumerable<MetaEntry>> ReadMediaAsync(string path, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(path, token);
        var list = new List<MetaEntry>
        {
            new("Media", "Duration", TimeSpan.FromSeconds(info.Duration).ToString(@"hh\:mm\:ss\.ff")),
        };
        if (info.BitRate > 0) list.Add(new("Media", "Bit rate", $"{info.BitRate / 1000:N0} kb/s"));
        if (info.HasVideo)
        {
            list.Add(new("Media", "Video", $"{info.VideoCodec}, {info.Width} × {info.Height}, {info.FrameRate:0.##} fps"));
        }
        if (info.HasAudio) list.Add(new("Media", "Audio", $"{info.AudioCodec}, {info.AudioChannels} ch"));
        list.AddRange(info.Tags.Select(t => new MetaEntry("Tags", t.Key.ToLowerInvariant(), t.Value)));
        return list;
    }

    private static IEnumerable<MetaEntry> ReadOffice(string path)
    {
        using var package = OpenOffice(path, false);
        var p = package.PackageProperties;
        var values = new (string, string?)[]
        {
            ("Title", p.Title), ("Author", p.Creator), ("Subject", p.Subject), ("Keywords", p.Keywords),
            ("Description", p.Description), ("Category", p.Category), ("Last modified by", p.LastModifiedBy),
            ("Revision", p.Revision), ("Created", p.Created?.ToLocalTime().ToString(DateFormat)),
            ("Modified", p.Modified?.ToLocalTime().ToString(DateFormat)),
        };
        return values.Select(v => new MetaEntry("Document", v.Item1, v.Item2 ?? ""));
    }

    private static OpenXmlPackage OpenOffice(string path, bool editable) => FileKinds.Ext(path) switch
    {
        "docx" => WordprocessingDocument.Open(path, editable),
        "xlsx" => SpreadsheetDocument.Open(path, editable),
        "pptx" => PresentationDocument.Open(path, editable),
        _ => throw new NotSupportedException(),
    };

    /// <summary>Writes edited values. Keys come from <see cref="EditableFields"/>.</summary>
    public static async Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        var kindValues = values.Where(kv => !FileFields.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (kindValues.Count > 0)
        {
            var temp = AppPaths.NewTempFile(FileKinds.Ext(path));
            File.Copy(path, temp, true);
            switch (FileKinds.Detect(path))
            {
                case FileKind.Video or FileKind.Audio:
                    var args = new List<string> { "-i", path, "-map", "0", "-c", "copy", "-map_metadata", "0" };
                    foreach (var (key, value) in kindValues) args.AddRange(["-metadata", $"{key}={value}"]);
                    args.Add(temp);
                    await Ffmpeg.RunAsync(args, 0, null, token);
                    break;
                case FileKind.Image:
                    WriteImage(path, temp, kindValues);
                    break;
                case FileKind.Pdf:
                    PdfEngine.WriteInfo(path, temp, kindValues);
                    break;
                default:
                    using (var package = OpenOffice(temp, true))
                    {
                        var p = package.PackageProperties;
                        foreach (var (key, value) in kindValues)
                        {
                            var v = string.IsNullOrEmpty(value) ? null : value;
                            switch (key)
                            {
                                case "Title": p.Title = v; break;
                                case "Author": p.Creator = v; break;
                                case "Subject": p.Subject = v; break;
                                case "Keywords": p.Keywords = v; break;
                                case "Description": p.Description = v; break;
                                case "Category": p.Category = v; break;
                            }
                        }
                    }
                    break;
            }
            var created = File.GetCreationTime(path);
            OutputPaths.CommitTemp(temp, path);
            File.SetCreationTime(path, created);
        }

        var isFolder = Directory.Exists(path);
        if (values.TryGetValue("Created", out var c) && DateTime.TryParse(c, out var createdAt))
        {
            if (isFolder) Directory.SetCreationTime(path, createdAt);
            else File.SetCreationTime(path, createdAt);
        }
        if (values.TryGetValue("Modified", out var m) && DateTime.TryParse(m, out var modifiedAt))
        {
            if (isFolder) Directory.SetLastWriteTime(path, modifiedAt);
            else File.SetLastWriteTime(path, modifiedAt);
        }
    }

    private static void WriteImage(string path, string temp, Dictionary<string, string> values)
    {
        using var image = new MagickImage(path);
        var exif = image.GetExifProfile() ?? new ExifProfile();
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "Artist": exif.SetValue(ExifTag.Artist, value); break;
                case "Copyright": exif.SetValue(ExifTag.Copyright, value); break;
                case "Description": exif.SetValue(ExifTag.ImageDescription, value); break;
                case "Date taken":
                    if (DateTime.TryParse(value, out var date))
                        exif.SetValue(ExifTag.DateTimeOriginal, date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
            }
        }
        image.SetProfile(exif);
        image.Write(temp); // Keeps the detected source quality for JPEG.
    }

    /// <summary>Removes metadata, writing to <paramref name="output"/> (which may be the input itself).</summary>
    public static async Task StripAsync(string input, string output, CancellationToken token)
    {
        var ext = FileKinds.Ext(input);
        var temp = AppPaths.NewTempFile(ext);
        switch (FileKinds.Detect(input))
        {
            case FileKind.Image or FileKind.AnimatedImage when ext is "jpg" or "jpeg" or "jfif":
                File.WriteAllBytes(temp, StripJpeg(File.ReadAllBytes(input)));
                break;
            case FileKind.Image or FileKind.AnimatedImage:
                using (var images = new MagickImageCollection(input))
                {
                    foreach (var frame in images)
                    {
                        var icc = frame.GetColorProfile();
                        frame.Strip();
                        if (icc is not null) frame.SetProfile(icc); // Color accuracy is not personal data.
                    }
                    images.Write(temp);
                }
                break;
            case FileKind.Video or FileKind.Audio:
                await Ffmpeg.RunAsync(["-i", input, "-map", "0", "-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1",
                    "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", temp], 0, null, token);
                break;
            case FileKind.Pdf:
                PdfEngine.WriteInfo(input, temp, null);
                break;
            case FileKind.WordDocument or FileKind.Spreadsheet or FileKind.Presentation when IsOpenXml(input):
                File.Copy(input, temp, true);
                using (var package = OpenOffice(temp, true))
                {
                    var p = package.PackageProperties;
                    p.Title = p.Creator = p.Subject = p.Keywords = p.Description = p.Category = p.LastModifiedBy = p.Revision = null;
                    p.ContentStatus = p.Identifier = p.Language = p.Version = null;
                    if (package.GetPartsOfType<CustomFilePropertiesPart>().FirstOrDefault() is { } custom) package.DeletePart(custom);
                    if (package.GetPartsOfType<ExtendedFilePropertiesPart>().FirstOrDefault() is { } extended)
                    {
                        extended.Properties.Company = null;
                        extended.Properties.Manager = null;
                        extended.Properties.Template = null;
                    }
                }
                break;
            default:
                throw new NotSupportedException(Loc.T("Removing metadata isn't supported for this file type."));
        }
        OutputPaths.CommitTemp(temp, output);
    }

    /// <summary>Drops EXIF/XMP/IPTC/comment segments from a JPEG without re-encoding the image.</summary>
    private static byte[] StripJpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) throw new InvalidDataException(Loc.T("Not a valid JPEG file."));
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) throw new InvalidDataException(Loc.T("Corrupt JPEG structure."));
            var marker = data[pos + 1];
            if (marker == 0xDA) break; // Start of scan: the rest is image data.
            var length = (data[pos + 2] << 8) | data[pos + 3];
            var isIcc = marker == 0xE2 && length > 14 && System.Text.Encoding.ASCII.GetString(data, pos + 4, 11) == "ICC_PROFILE";
            var drop = (marker is >= 0xE1 and <= 0xEF && !isIcc) || marker == 0xFE;
            if (!drop) output.Write(data, pos, length + 2);
            pos += length + 2;
        }
        output.Write(data, pos, data.Length - pos);
        return output.ToArray();
    }
}
