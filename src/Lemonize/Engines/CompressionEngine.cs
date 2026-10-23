using System.IO;
using ImageMagick;
using Lemonize.Core;
using Lemonize.Jobs;

namespace Lemonize.Engines;

/// <summary>Preset strengths (currently for PDF): an alternative to a percentage or a target size.</summary>
public enum CompressLevel { None, Light, Medium, Strong }

/// <summary>Reduce by a fraction (0.5 = half the size), aim for an absolute size, or use a preset level.</summary>
public sealed record CompressOptions(bool ByPercent, double Reduction, long TargetBytes, CompressLevel Level = CompressLevel.None)
{
    public long TargetFor(long originalBytes) =>
        ByPercent ? (long)(originalBytes * (1 - Math.Clamp(Reduction, 0.01, 0.99))) : TargetBytes;
}

public static class CompressionEngine
{
    public static string Suffix => Loc.T("(compressed)");

    public static bool Supports(FileKind kind) =>
        kind is FileKind.Image or FileKind.AnimatedImage or FileKind.Video or FileKind.Audio or FileKind.Pdf;

    public static async Task<string> CompressAsync(string input, CompressOptions options, Job job, CancellationToken token)
    {
        var original = new FileInfo(input).Length;
        if (options.Level != CompressLevel.None)
        {
            if (FileKinds.Detect(input) != FileKind.Pdf)
                throw new NotSupportedException(Loc.T("Compression levels are available for PDF files."));
            var leveled = await CompressPdfLevelAsync(input, options.Level, job, token);
            job.Report($"{OutputPaths.FormatSize(original)} → {OutputPaths.FormatSize(new FileInfo(leveled).Length)}");
            return leveled;
        }

        var target = options.TargetFor(original);
        if (target <= 0) throw new InvalidOperationException(Loc.T("Target size must be greater than zero."));

        var output = FileKinds.Detect(input) switch
        {
            FileKind.Image => CompressImage(input, target, job, token),
            FileKind.AnimatedImage => CompressGif(input, target, job, token),
            FileKind.Video => await CompressVideoAsync(input, target, job, token),
            FileKind.Audio => await CompressAudioAsync(input, target, job, token),
            FileKind.Pdf => await CompressPdfAsync(input, target, job, token),
            _ => throw new NotSupportedException(Loc.T("This file type can't be compressed.")),
        };

        var size = new FileInfo(output).Length;
        job.Report($"{OutputPaths.FormatSize(original)} → {OutputPaths.FormatSize(size)}" +
                   (size > target * 1.02 ? Loc.T(" (smallest possible)") : ""));
        return output;
    }

    private static string CompressImage(string input, long target, Job job, CancellationToken token)
    {
        var ext = FileKinds.Ext(input);
        using var source = ImageEngine.Load(input);
        var outExt = ext switch
        {
            "jpg" or "jpeg" or "jfif" or "webp" or "avif" => ext,
            "png" => "png",
            _ => source.HasAlpha ? "png" : "jpg",
        };
        var lossy = outExt != "png";

        byte[] Encode(double scale, int quality, bool quantize)
        {
            token.ThrowIfCancellationRequested();
            using var img = (MagickImage)source.Clone();
            if (scale < 0.999) img.Resize(new Percentage(scale * 100));
            img.Strip();
            if (lossy)
            {
                if (outExt is "jpg" or "jpeg" or "jfif" && img.HasAlpha)
                {
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaAction.Remove);
                }
                img.Quality = (uint)quality;
                if (outExt is "jpg" or "jpeg" or "jfif") img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
            }
            else
            {
                if (quantize) img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            }
            img.Format = ImageEngine.FormatFor(outExt);
            return img.ToByteArray();
        }

        byte[] best;
        if (lossy)
        {
            // Highest quality that fits, then shrink dimensions if even low quality is too big.
            int lo = 10, hi = 92;
            best = Encode(1, lo, false);
            if (best.Length <= target)
            {
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    var attempt = Encode(1, mid, false);
                    if (attempt.Length <= target)
                    {
                        lo = mid;
                        best = attempt;
                    }
                    else hi = mid - 1;
                    job.Report(0.1 + 0.8 * (1 - (hi - lo) / 82.0));
                }
            }
            else
            {
                var scale = 1.0;
                for (var i = 0; i < 8 && best.Length > target; i++)
                {
                    scale *= Math.Max(0.3, Math.Sqrt(target / (double)best.Length) * 0.95);
                    best = Encode(scale, 70, false);
                    job.Report(0.1 + i * 0.1);
                }
            }
        }
        else
        {
            best = Encode(1, 0, false);
            if (best.Length > target) best = Encode(1, 0, true);
            var scale = 1.0;
            for (var i = 0; i < 8 && best.Length > target; i++)
            {
                scale *= Math.Max(0.3, Math.Sqrt(target / (double)best.Length) * 0.95);
                best = Encode(scale, 0, true);
                job.Report(0.2 + i * 0.1);
            }
        }

        var output = OutputPaths.For(input, outExt, Suffix);
        File.WriteAllBytes(output, best);
        return output;
    }

    /// <summary>Fewer colors first, then smaller frames, until the animation fits.</summary>
    private static string CompressGif(string input, long target, Job job, CancellationToken token)
    {
        using var original = new MagickImageCollection(input);
        original.Coalesce();
        (int Colors, double Scale)[] steps = [(256, 1), (128, 1), (64, 1), (64, 0.8), (32, 0.7), (32, 0.55), (16, 0.45), (16, 0.35)];
        byte[]? best = null;
        for (var i = 0; i < steps.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            using var attempt = original.Clone();
            if (steps[i].Scale < 0.999)
                foreach (var frame in attempt) frame.Resize(new Percentage(steps[i].Scale * 100));
            attempt.Quantize(new QuantizeSettings { Colors = (uint)steps[i].Colors });
            attempt.Optimize();
            attempt.OptimizeTransparency();
            var bytes = attempt.ToByteArray(MagickFormat.Gif);
            if (best is null || bytes.Length < best.Length) best = bytes;
            job.Report((i + 1.0) / steps.Length);
            if (bytes.Length <= target) break;
        }
        var output = OutputPaths.For(input, "gif", Suffix);
        File.WriteAllBytes(output, best!);
        return output;
    }

    private static async Task<string> CompressVideoAsync(string input, long target, Job job, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        if (info.Duration <= 0) throw new InvalidOperationException(Loc.T("Can't determine the video duration."));

        var totalKbps = target * 8 / info.Duration / 1000 * 0.97; // container overhead
        var audioKbps = info.HasAudio ? Math.Clamp(totalKbps * 0.12, 24, 128) : 0;
        var videoKbps = totalKbps - audioKbps;
        if (videoKbps < 25) throw new InvalidOperationException(Loc.F("Target too small for a {0}s video. Try a larger size.", Math.Round(info.Duration)));

        var height = info.Height;
        if (videoKbps < 400 && height > 480) height = 480;
        else if (videoKbps < 1200 && height > 720) height = 720;
        else if (videoKbps < 3000 && height > 1080) height = 1080;
        var scale = height != info.Height ? $"scale=-2:{height}" : "scale=trunc(iw/2)*2:trunc(ih/2)*2";

        var output = OutputPaths.For(input, "mp4", Suffix);
        var passLog = Path.Combine(AppPaths.Temp, Guid.NewGuid().ToString("N"));
        var common = new List<string>
        {
            "-i", input, "-map", "0:v:0", "-vf", scale, "-c:v", "libx264", "-preset", "medium",
            "-b:v", $"{(int)videoKbps}k", "-maxrate", $"{(int)(videoKbps * 1.5)}k", "-bufsize", $"{(int)(videoKbps * 2)}k",
            "-pix_fmt", "yuv420p", "-passlogfile", passLog,
        };
        try
        {
            job.Report(Loc.T("Analyzing (pass 1 of 2)…"));
            await Ffmpeg.RunAsync([.. common, "-pass", "1", "-an", "-f", "null", "NUL"], info.Duration, p => job.Report(p * 0.4), token);
            job.Report(Loc.T("Compressing (pass 2 of 2)…"));
            string[] audio = audioKbps > 0 ? ["-map", "0:a:0", "-c:a", "aac", "-b:a", $"{(int)audioKbps}k"] : ["-an"];
            await Ffmpeg.RunAsync([.. common, "-pass", "2", .. audio, "-movflags", "+faststart", output],
                info.Duration, p => job.Report(0.4 + p * 0.6), token);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Temp, Path.GetFileName(passLog) + "*")) File.Delete(f);
        }
        return output;
    }

    private static async Task<string> CompressAudioAsync(string input, long target, Job job, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        if (info.Duration <= 0) throw new InvalidOperationException(Loc.T("Can't determine the audio duration."));
        var kbps = (int)Math.Clamp(target * 8 / info.Duration / 1000 * 0.97, 8, 320);
        var ext = FileKinds.Ext(input);
        var outExt = ext is "mp3" or "m4a" or "aac" or "ogg" or "opus" ? ext : "mp3";
        if (outExt == "mp3") kbps = new[] { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }.Last(b => b <= Math.Max(8, kbps));
        var output = OutputPaths.For(input, outExt, Suffix);
        await Ffmpeg.RunAsync(["-i", input, "-vn", "-map_metadata", "0", .. MediaEngine.AudioCodecArgs(outExt, kbps), output],
            info.Duration, job.Report, token);
        return output;
    }

    /// <summary>A PDF compression strength: a Ghostscript preset plus JPEG quality/scale for our own image re-encoding.</summary>
    private readonly record struct PdfStep(string Preset, int Quality, double Scale);

    private static readonly PdfStep[] TargetSteps =
    [
        new("/printer", 80, 1), new("/ebook", 65, 1), new("/ebook", 55, 0.8),
        new("/screen", 45, 0.65), new("/screen", 35, 0.5), new("/screen", 25, 0.38),
    ];

    private static Task<string> CompressPdfLevelAsync(string input, CompressLevel level, Job job, CancellationToken token)
    {
        var step = level switch
        {
            CompressLevel.Light => new PdfStep("/printer", 80, 1),
            CompressLevel.Medium => new PdfStep("/ebook", 60, 0.8),
            _ => new PdfStep("/screen", 40, 0.55),
        };
        return CompressPdfStepsAsync(input, [step], 0, job, token);
    }

    private static async Task<string> GhostscriptAsync(string gs, string input, string preset, CancellationToken token)
    {
        var attempt = AppPaths.NewTempFile("pdf");
        var (code, _, err) = await Ffmpeg.RunCaptureAsync(gs,
            ["-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.5", $"-dPDFSETTINGS={preset}", "-dNOPAUSE", "-dBATCH", "-dQUIET", $"-sOutputFile={attempt}", input], token);
        if (code != 0)
        {
            if (File.Exists(attempt)) File.Delete(attempt);
            throw new InvalidOperationException(Loc.F("Ghostscript failed: {0}", err.Trim()));
        }
        return attempt;
    }

    private static Task<string> CompressPdfAsync(string input, long target, Job job, CancellationToken token) =>
        CompressPdfStepsAsync(input, TargetSteps, target, job, token);

    /// <summary>
    /// Tries each step until the result fits <paramref name="target"/>. Every step compares our own image
    /// re-encoding, Ghostscript (if available) and Ghostscript followed by our re-encoding, keeping the smallest:
    /// Ghostscript wins on fonts and large scans, our pass on PDFs whose images Ghostscript leaves alone.
    /// </summary>
    private static async Task<string> CompressPdfStepsAsync(string input, IReadOnlyList<PdfStep> steps, long target, Job job, CancellationToken token)
    {
        var output = OutputPaths.For(input, "pdf", Suffix);
        var gs = ToolLocator.Ghostscript;
        var gsOutputs = new Dictionary<string, string>();
        string? best = null;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var step = steps[i];
                Keep(Recompress(input, step));
                if (gs is not null)
                {
                    try
                    {
                        if (!gsOutputs.TryGetValue(step.Preset, out var gsOut))
                            gsOutputs[step.Preset] = gsOut = await GhostscriptAsync(gs, input, step.Preset, token);
                        var copy = AppPaths.NewTempFile("pdf");
                        File.Copy(gsOut, copy);
                        Keep(copy);
                        Keep(Recompress(gsOut, step));
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Ghostscript can choke on unusual PDFs; our own pass still stands.
                        Diag.Log("pdf: ghostscript skipped: " + ex.Message);
                    }
                }
                job.Report((i + 1.0) / steps.Count);
                if (new FileInfo(best!).Length <= target) break;
            }
            if (new FileInfo(best!).Length >= new FileInfo(input).Length)
                throw new InvalidOperationException(Loc.T("This PDF is already as small as Lemonize can make it."));
            File.Move(best!, output);
            return output;
        }
        finally
        {
            if (best is not null && File.Exists(best)) File.Delete(best);
            foreach (var path in gsOutputs.Values) File.Delete(path);
        }

        static string Recompress(string source, PdfStep step)
        {
            var attempt = AppPaths.NewTempFile("pdf");
            PdfEngine.RecompressImages(source, attempt, step.Quality, step.Scale);
            return attempt;
        }

        void Keep(string attempt)
        {
            if (best is null || new FileInfo(attempt).Length < new FileInfo(best).Length)
            {
                if (best is not null) File.Delete(best);
                best = attempt;
            }
            else File.Delete(attempt);
        }
    }
}
