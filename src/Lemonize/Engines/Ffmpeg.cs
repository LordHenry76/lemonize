using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Lemonize.Core;

namespace Lemonize.Engines;

public sealed record MediaInfo(
    double Duration,
    string? VideoCodec,
    string? AudioCodec,
    int Width,
    int Height,
    double FrameRate,
    int AudioChannels,
    long BitRate,
    Dictionary<string, string> Tags,
    JsonElement Raw)
{
    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;
}

public sealed class FfmpegMissingException() : Exception(Loc.T("FFmpeg is not installed. Open Lemonize settings to download it."));

public static class Ffmpeg
{
    public static string Exe => ToolLocator.Ffmpeg ?? throw new FfmpegMissingException();
    public static string ProbeExe => ToolLocator.Ffprobe ?? throw new FfmpegMissingException();

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        var (code, stdout, stderr) = await RunCaptureAsync(ProbeExe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], token);
        if (code != 0) throw new InvalidOperationException(Loc.F("Cannot read media file: {0}", LastLine(stderr)));

        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement.Clone();
        string? vcodec = null, acodec = null;
        int width = 0, height = 0, channels = 0;
        double fps = 0;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                var isCover = s.TryGetProperty("disposition", out var d) &&
                              d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && vcodec is null && !isCover)
                {
                    vcodec = codec;
                    width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    fps = ParseRate(s.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);
                }
                else if (type == "audio" && acodec is null)
                {
                    acodec = codec;
                    channels = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                }
            }
        }

        double duration = 0;
        long bitrate = 0;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur))
                double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            if (format.TryGetProperty("bit_rate", out var br))
                long.TryParse(br.GetString(), out bitrate);
            if (format.TryGetProperty("tags", out var t))
                foreach (var p in t.EnumerateObject())
                    tags[p.Name] = p.Value.ToString();
        }

        return new MediaInfo(duration, vcodec, acodec, width, height, fps, channels, bitrate, tags, root);
    }

    /// <summary>Runs ffmpeg reporting progress (0..1) against <paramref name="duration"/> seconds.</summary>
    public static async Task RunAsync(IEnumerable<string> args, double duration, Action<double>? progress, CancellationToken token)
    {
        // "-v error" keeps stderr to actual problems so failures can quote the real cause.
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);

        var psi = NewStartInfo(Exe, all);
        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 64_000) stderr.Remove(0, 32_000);
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || progress is null || duration <= 0) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan(12), out var us))
                progress(us / 1_000_000.0 / duration);
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        await using (token.Register(() => Kill(process)))
            await process.WaitForExitAsync(CancellationToken.None);
        token.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            string text;
            lock (stderr) text = stderr.ToString();
            throw new InvalidOperationException(Loc.F("FFmpeg failed: {0}", FirstError(text)));
        }
    }

    /// <summary>Grabs one frame as PNG bytes; <paramref name="maxWidth"/> 0 keeps full size.</summary>
    public static async Task<byte[]> FrameAsync(string path, double seconds, int maxWidth, CancellationToken token = default)
    {
        var args = new List<string>
        {
            "-v", "error", "-ss", Seconds(seconds), "-i", path, "-frames:v", "1",
        };
        if (maxWidth > 0) args.AddRange(["-vf", $"scale='min({maxWidth},iw)':-2"]);
        args.AddRange(["-f", "image2pipe", "-vcodec", "png", "pipe:1"]);
        var bytes = await CaptureBytesAsync(args, token);
        if (bytes.Length == 0) throw new InvalidOperationException(Loc.T("Could not extract a frame at this position."));
        return bytes;
    }

    /// <summary>Runs ffmpeg and returns what it writes to stdout (e.g. an image piped to pipe:1).</summary>
    public static async Task<byte[]> CaptureBytesAsync(IEnumerable<string> args, CancellationToken token)
    {
        using var process = Process.Start(NewStartInfo(Exe, args))!;
        _ = process.StandardError.ReadToEndAsync(token);
        using var ms = new MemoryStream();
        await using (token.Register(() => Kill(process)))
        {
            await process.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        token.ThrowIfCancellationRequested();
        return ms.ToArray();
    }

    public static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string exe, IEnumerable<string> args, CancellationToken token)
    {
        using var process = Process.Start(NewStartInfo(exe, args))!;
        var outTask = process.StandardOutput.ReadToEndAsync(token);
        var errTask = process.StandardError.ReadToEndAsync(token);
        await using (token.Register(() => Kill(process)))
            await process.WaitForExitAsync(CancellationToken.None);
        token.ThrowIfCancellationRequested();
        return (process.ExitCode, await outTask, await errTask);
    }

    public static ProcessStartInfo NewStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(rate, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string FirstError(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";
        // Drop the "[libx264 @ 0000020F...]" prefix; the message itself is what users can act on.
        return System.Text.RegularExpressions.Regex.Replace(line, @"^(\[[^\]]*\]\s*)+", "");
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => !l.StartsWith("Conversion failed", StringComparison.Ordinal)) ?? "unknown error";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
