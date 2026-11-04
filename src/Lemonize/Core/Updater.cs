using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lemonize.Core;

public sealed record UpdateInfo(string Version, string Notes, string DownloadUrl, string? Sha256);

/// <summary>
/// Looks for a newer release on GitHub and installs it. Only the public release data is read:
/// nothing about the user or their files is sent.
/// </summary>
public static class Updater
{
    private const string Repository = "LordHenry76/lemonize";
    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    private static readonly string DownloadDir = Path.Combine(AppPaths.LocalData, "updates");

    /// <summary>The release to suggest, once a check has found one the user did not skip.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Raised on the caller's thread when <see cref="Available"/> changes.</summary>
    public static event Action? AvailableChanged;

    /// <summary>Checks now and returns the newer release, if any. Throws when GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var found = ParseRelease(await response.Content.ReadAsStringAsync(token), AppInfo.Version, Loc.IsItalian);

        var suggested = found is not null && found.Version != Settings.Current.SkippedUpdateVersion ? found : null;
        if (suggested != Available)
        {
            Available = suggested;
            AvailableChanged?.Invoke();
        }
        return found;
    }

    /// <summary>Stops suggesting this version; a later one is suggested again.</summary>
    public static void Skip(UpdateInfo update)
    {
        Settings.Current.SkippedUpdateVersion = update.Version;
        Settings.Current.Save();
        Available = null;
        AvailableChanged?.Invoke();
    }

    /// <summary>Null when the release is not newer or its installer is not there (yet).</summary>
    public static UpdateInfo? ParseRelease(string json, string currentVersion, bool italian)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        var version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
        if (!IsNewer(version, currentVersion)) return null;

        var installer = $"Lemonize-Setup-{version}.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != installer) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha256 = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest["sha256:".Length..] : null;
            var body = release.TryGetProperty("body", out var b) ? b.GetString() : null;
            return new UpdateInfo(version, NotesFor(body ?? "", italian), url, sha256);
        }
        return null;
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;

    /// <summary>
    /// The release notes in one language, as plain text. release.ps1 writes "### English" and
    /// "### Italiano" sections; the line about installing by hand doesn't apply here.
    /// </summary>
    public static string NotesFor(string body, bool italian)
    {
        var lines = body.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == (italian ? "### Italiano" : "### English"));
        IEnumerable<string> section = start < 0
            ? lines
            : lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("---", StringComparison.Ordinal) && !l.StartsWith("### ", StringComparison.Ordinal));
        var kept = section.Where(l =>
            !l.StartsWith("**Update:**", StringComparison.Ordinal) &&
            !l.StartsWith("**Aggiornamento:**", StringComparison.Ordinal) &&
            !l.StartsWith("🤖", StringComparison.Ordinal));
        return string.Join("\n", kept).Replace("**", "").Replace("`", "").Trim();
    }

    /// <summary>Downloads the installer and returns its path, once it is known to be intact.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, Action<double> progress, CancellationToken token)
    {
        Directory.CreateDirectory(DownloadDir);
        var path = Path.Combine(DownloadDir, $"Lemonize-Setup-{update.Version}.exe");
        var complete = false;
        try
        {
            using var http = NewClient(TimeSpan.FromMinutes(30));
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = File.Create(path))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    hash.AppendData(buffer, 0, n);
                    read += n;
                    if (total > 0) progress(read / (double)total);
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (update.Sha256 is not null && !actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.T("The downloaded file is damaged. Please try again."));
            complete = true;
            return path;
        }
        finally
        {
            if (!complete) TryDelete(path);
        }
    }

    /// <summary>
    /// Starts the installer. It closes Lemonize by itself (--quit), shows only its progress bar,
    /// keeps the options chosen at the first install and reopens Lemonize when it is done.
    /// </summary>
    public static void Install(string installer) =>
        Process.Start(new ProcessStartInfo(installer, "/SILENT /RELAUNCH=1")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        });

    /// <summary>Removes installers left by earlier updates. One still running stays until next time.</summary>
    public static void CleanDownloads()
    {
        if (!Directory.Exists(DownloadDir)) return;
        foreach (var file in Directory.GetFiles(DownloadDir)) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Log($"updater: can't delete {path}: {ex.Message}");
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lemonize/{AppInfo.Version}");
        return http;
    }
}
