using System.Globalization;
using System.IO;
using System.Text;
using Lemonize.Core;

namespace Lemonize.Engines;

public sealed record CropRect(int X, int Y, int Width, int Height);

public sealed record VideoEdit(double Start, double End, CropRect? Crop, double Speed, bool Mute, IReadOnlyList<double> SplitPoints);

public enum ChannelMode { Keep, Mono, Stereo, Swap }

public sealed record AudioEdit(double Start, double End, IReadOnlyList<(double Start, double End)> Bleeps, bool Normalize, ChannelMode Channels, double FadeIn, double FadeOut);

public static class MediaTools
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>atempo only accepts 0.5–2 per instance, so larger changes are chained.</summary>
    private static string Atempo(double speed)
    {
        var parts = new List<string>();
        while (speed > 2) { parts.Add("atempo=2"); speed /= 2; }
        while (speed < 0.5) { parts.Add("atempo=0.5"); speed /= 0.5; }
        parts.Add("atempo=" + F(speed));
        return string.Join(",", parts);
    }

    public static async Task<List<string>> EditVideoAsync(string input, VideoEdit edit, Action<double> progress, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        var ext = FileKinds.Ext(input) is "mov" or "mkv" ? FileKinds.Ext(input) : "mp4";
        var bounds = new List<double> { edit.Start };
        bounds.AddRange(edit.SplitPoints.Where(p => p > edit.Start + 0.05 && p < edit.End - 0.05).OrderBy(p => p));
        bounds.Add(edit.End);

        var filters = new List<string>();
        if (edit.Crop is { } c) filters.Add($"crop={c.Width / 2 * 2}:{c.Height / 2 * 2}:{c.X}:{c.Y}");
        if (Math.Abs(edit.Speed - 1) > 0.001) filters.Add($"setpts=PTS/{F(edit.Speed)}");
        filters.Add("format=yuv420p");

        var outputs = new List<string>();
        var segments = bounds.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            var suffix = segments > 1 ? Loc.F("(part {0})", i + 1) : Loc.T("(edited)");
            var output = OutputPaths.For(input, ext, suffix);
            outputs.Add(output);
            var args = new List<string>
            {
                "-ss", F(bounds[i]), "-to", F(bounds[i + 1]), "-i", input,
                "-map", "0:v:0", "-vf", string.Join(",", filters),
                "-c:v", "libx264", "-preset", "medium", "-crf", "20",
            };
            if (info.HasAudio && !edit.Mute)
            {
                args.AddRange(["-map", "0:a:0", "-c:a", "aac", "-b:a", "192k"]);
                if (Math.Abs(edit.Speed - 1) > 0.001) args.AddRange(["-af", Atempo(edit.Speed)]);
            }
            else args.Add("-an");
            if (ext == "mp4" || ext == "mov") args.AddRange(["-movflags", "+faststart"]);
            args.Add(output);

            var segmentDuration = (bounds[i + 1] - bounds[i]) / edit.Speed;
            var index = i;
            await Ffmpeg.RunAsync(args, segmentDuration, p => progress((index + p) / segments), token);
        }
        return outputs;
    }

    public static async Task<string> SnapshotAsync(string input, double seconds, CancellationToken token)
    {
        var png = await Ffmpeg.FrameAsync(input, seconds, 0, token);
        var stamp = TimeSpan.FromSeconds(seconds).ToString(@"hh\-mm\-ss");
        var output = OutputPaths.For(input, "png", Loc.F("(frame {0})", stamp));
        await File.WriteAllBytesAsync(output, png, token);
        return output;
    }

    public static async Task<string> EditAudioAsync(string input, AudioEdit edit, Action<double> progress, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        var graph = new StringBuilder("[0:a]aresample=48000");
        if (edit.Bleeps.Count > 0)
        {
            var expr = string.Join("+", edit.Bleeps.Select(b => $"between(t,{F(b.Start)},{F(b.End)})"));
            graph.Append($",volume=volume=0:enable='{expr}'[m];");
            graph.Append($"sine=frequency=1000:sample_rate=48000:duration={F(info.Duration + 1)},volume=0.3,volume=volume=0:enable='not({expr})'[b];");
            graph.Append("[m][b]amix=inputs=2:duration=first:normalize=0");
        }
        graph.Append($",atrim=start={F(edit.Start)}:end={F(edit.End)},asetpts=PTS-STARTPTS");
        var length = edit.End - edit.Start;
        if (edit.FadeIn > 0) graph.Append($",afade=t=in:st=0:d={F(edit.FadeIn)}");
        if (edit.FadeOut > 0) graph.Append($",afade=t=out:st={F(Math.Max(0, length - edit.FadeOut))}:d={F(edit.FadeOut)}");
        if (edit.Normalize) graph.Append(",loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000");
        graph.Append(edit.Channels switch
        {
            ChannelMode.Mono => ",pan=mono|c0=0.5*c0+0.5*c1",
            ChannelMode.Stereo => ",aformat=channel_layouts=stereo",
            ChannelMode.Swap => ",pan=stereo|c0=c1|c1=c0",
            _ => "",
        });
        graph.Append("[out]");

        var ext = FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, Loc.T("(edited)"));
        await Ffmpeg.RunAsync(["-i", input, "-filter_complex", graph.ToString(), "-map", "[out]", "-map_metadata", "0", .. MediaEngine.AudioCodecArgs(ext), output],
            length, progress, token);
        return output;
    }

    public static async Task<string> NormalizeAsync(string input, Action<double> progress, CancellationToken token)
    {
        var info = await Ffmpeg.ProbeAsync(input, token);
        var isVideo = info.HasVideo;
        var ext = FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, Loc.T("(normalized)"));
        var args = new List<string> { "-i", input, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000", "-map_metadata", "0" };
        if (isVideo) args.AddRange(["-c:v", "copy", "-c:a", ext == "webm" ? "libopus" : "aac", "-b:a", "192k"]);
        else args.AddRange(MediaEngine.AudioCodecArgs(ext));
        args.Add(output);
        await Ffmpeg.RunAsync(args, info.Duration, progress, token);
        return output;
    }

    /// <summary>Concatenates files in order, re-encoding so mismatched sizes and codecs still join cleanly.</summary>
    public static async Task<string> JoinAsync(IReadOnlyList<string> inputs, Action<double> progress, CancellationToken token)
    {
        var infos = new List<MediaInfo>();
        foreach (var input in inputs) infos.Add(await Ffmpeg.ProbeAsync(input, token));
        var video = infos[0].HasVideo;
        var total = infos.Sum(i => i.Duration);
        var args = new List<string>();
        foreach (var input in inputs) args.AddRange(["-i", input]);

        var graph = new StringBuilder();
        var concatInputs = new StringBuilder();
        if (video)
        {
            var w = infos[0].Width / 2 * 2;
            var h = infos[0].Height / 2 * 2;
            var fps = infos[0].FrameRate > 0 ? F(Math.Min(infos[0].FrameRate, 60)) : "30";
            var anyAudio = infos.Any(i => i.HasAudio);
            for (var i = 0; i < inputs.Count; i++)
            {
                if (!infos[i].HasVideo) throw new InvalidOperationException(Loc.F("{0} has no video.", Path.GetFileName(inputs[i])));
                graph.Append($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p[v{i}];");
                if (anyAudio)
                {
                    graph.Append(infos[i].HasAudio
                        ? $"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[a{i}];"
                        : $"anullsrc=r=48000:cl=stereo,atrim=duration={F(infos[i].Duration)}[a{i}];");
                }
                concatInputs.Append($"[v{i}]").Append(anyAudio ? $"[a{i}]" : "");
            }
            graph.Append($"{concatInputs}concat=n={inputs.Count}:v=1:a={(anyAudio ? 1 : 0)}[v]{(anyAudio ? "[a]" : "")}");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]"]);
            if (anyAudio) args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
            args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-movflags", "+faststart"]);
            var output = OutputPaths.ForGroup(inputs, Loc.T("Joined"), "mp4");
            args.Add(output);
            await Ffmpeg.RunAsync(args, total, progress, token);
            return output;
        }
        else
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                if (!infos[i].HasAudio) throw new InvalidOperationException(Loc.F("{0} has no audio.", Path.GetFileName(inputs[i])));
                graph.Append($"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
                concatInputs.Append($"[a{i}]");
            }
            graph.Append($"{concatInputs}concat=n={inputs.Count}:v=0:a=1[a]");
            var ext = FileKinds.Ext(inputs[0]);
            var output = OutputPaths.ForGroup(inputs, Loc.T("Joined"), ext);
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[a]", .. MediaEngine.AudioCodecArgs(ext), output]);
            await Ffmpeg.RunAsync(args, total, progress, token);
            return output;
        }
    }
}
