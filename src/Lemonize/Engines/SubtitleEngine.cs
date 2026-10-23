using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Lemonize.Engines;

public static partial class SubtitleEngine
{
    public static readonly string[] Targets = ["srt", "vtt", "txt"];

    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    public static void Convert(string input, string output, string target)
    {
        var cues = Parse(DocumentEngine.ReadAllTextSmart(input));
        var sb = new StringBuilder();
        switch (target)
        {
            case "srt":
                for (var i = 0; i < cues.Count; i++)
                    sb.Append(i + 1).Append('\n')
                      .Append($"{Format(cues[i].Start, ',')} --> {Format(cues[i].End, ',')}\n")
                      .Append(cues[i].Text).Append("\n\n");
                break;
            case "vtt":
                sb.Append("WEBVTT\n\n");
                foreach (var cue in cues)
                    sb.Append($"{Format(cue.Start, '.')} --> {Format(cue.End, '.')}\n").Append(cue.Text).Append("\n\n");
                break;
            default:
                foreach (var cue in cues) sb.Append(TagRegex().Replace(cue.Text, "")).Append('\n');
                break;
        }
        File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
    }

    private static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n{2,}");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').ToList();
            var timeLine = lines.FindIndex(l => l.Contains("-->"));
            if (timeLine < 0) continue;
            var m = TimeRegex().Match(lines[timeLine]);
            if (!m.Success) continue;
            var body = string.Join("\n", lines.Skip(timeLine + 1)).Trim();
            cues.Add(new Cue(ParseTime(m.Groups[1].Value), ParseTime(m.Groups[2].Value), body));
        }
        return cues;
    }

    private static TimeSpan ParseTime(string s)
    {
        var parts = s.Replace(',', '.').Split(':');
        var hours = parts.Length > 2 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        return new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }

    private static string Format(TimeSpan t, char separator) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}";

    [GeneratedRegex(@"((?:\d+:)?\d{2}:\d{2}[,.]\d{3})\s*-->\s*((?:\d+:)?\d{2}:\d{2}[,.]\d{3})")]
    private static partial Regex TimeRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}
