using Lemonize.Core;

namespace Lemonize.Engines;

/// <summary>Video and audio conversion through FFmpeg.</summary>
public static class MediaEngine
{
    public static readonly string[] VideoTargets = ["mp4", "mov", "mkv", "avi", "wmv", "webm", "gif"];
    public static readonly string[] AudioTargets = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff"];

    private static readonly HashSet<string> CopyableVideo = ["h264", "hevc"];
    private static readonly HashSet<string> CopyableAudio = ["aac", "mp3"];

    public static async Task ConvertAsync(string input, string output, string target, Action<double> progress, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        var args = new List<string> { "-i", input };

        if (AudioTargets.Contains(target))
        {
            if (!info.HasAudio) throw new InvalidOperationException(Loc.T("This file has no audio track."));
            args.AddRange(["-vn", "-map_metadata", "0"]);
            args.AddRange(AudioCodecArgs(target));
        }
        else if (target == "gif")
        {
            args.AddRange([
                "-vf", "fps=12,scale='min(720,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                "-loop", "0",
            ]);
        }
        else
        {
            if (!info.HasVideo) throw new InvalidOperationException(Loc.T("This file has no video track."));
            args.AddRange(VideoArgs(info, target));
        }

        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, progress, token);
    }

    public static IEnumerable<string> VideoArgs(MediaInfo info, string target)
    {
        var canCopy = target is "mp4" or "mov" or "mkv" &&
                      CopyableVideo.Contains(info.VideoCodec ?? "") &&
                      (!info.HasAudio || CopyableAudio.Contains(info.AudioCodec ?? "") || target == "mkv");
        if (canCopy)
            return ["-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-map_metadata", "0", .. Faststart(target)];

        var evenSize = new[] { "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2" };
        string[] video = target switch
        {
            "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"],
            "avi" => ["-c:v", "mpeg4", "-q:v", "3", "-vtag", "xvid"],
            "wmv" => ["-c:v", "wmv2", "-b:v", BitrateFor(info) + "k"],
            _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", .. evenSize],
        };
        string[] audio = !info.HasAudio ? ["-an"] : target switch
        {
            "webm" => ["-c:a", "libopus", "-b:a", "128k"],
            "avi" => ["-c:a", "libmp3lame", "-q:a", "3"],
            "wmv" => ["-c:a", "wmav2", "-b:a", "192k"],
            _ => ["-c:a", "aac", "-b:a", "192k"],
        };
        return ["-map", "0:v:0", "-map", "0:a:0?", .. video, .. audio, "-map_metadata", "0", .. Faststart(target)];
    }

    public static string[] AudioCodecArgs(string target, int? kbps = null) => target switch
    {
        "mp3" => kbps is { } k ? ["-c:a", "libmp3lame", "-b:a", k + "k"] : ["-c:a", "libmp3lame", "-q:a", "2"],
        "m4a" or "aac" => ["-c:a", "aac", "-b:a", (kbps ?? 256) + "k"],
        "wav" => ["-c:a", "pcm_s16le"],
        "flac" => ["-c:a", "flac"],
        "ogg" => kbps is { } k2 ? ["-c:a", "libvorbis", "-b:a", k2 + "k"] : ["-c:a", "libvorbis", "-q:a", "6"],
        "opus" => ["-c:a", "libopus", "-b:a", (kbps ?? 160) + "k"],
        "aiff" or "aif" => ["-c:a", "pcm_s16be"],
        "wma" => ["-c:a", "wmav2", "-b:a", (kbps ?? 192) + "k"],
        _ => ["-c:a", "aac", "-b:a", (kbps ?? 192) + "k"],
    };

    /// <summary>Animated GIF → video.</summary>
    public static async Task GifToVideoAsync(string input, string output, string target, Action<double> progress, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        string[] codec = target == "webm"
            ? ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-pix_fmt", "yuva420p"]
            : ["-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart"];
        await Ffmpeg.RunAsync(["-i", input, "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", .. codec, "-an", output],
            info.Duration, progress, token);
    }

    private static string[] Faststart(string target) => target is "mp4" or "mov" ? ["-movflags", "+faststart"] : [];

    private static int BitrateFor(MediaInfo info)
    {
        var pixels = Math.Max(1, info.Width * info.Height);
        return Math.Clamp((int)(pixels * 30 * 0.1 / 1000), 800, 12000);
    }
}
