using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace Lemonize.Core;

/// <summary>Downloads the FFmpeg "essentials" build into %LOCALAPPDATA%\Lemonize\ffmpeg.</summary>
public static class FfmpegInstaller
{
    private const string Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static async Task InstallAsync(Action<double> progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Lemonize/1.0");
        var zipPath = AppPaths.NewTempFile("zip");
        try
        {
            using (var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = File.Create(zipPath);
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;
                    if (total > 0) progress(read / (double)total * 0.9);
                }
            }

            var destination = ToolLocator.FfmpegInstallDir;
            Directory.CreateDirectory(destination);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.Contains("/bin/") && e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    if (entry.Name == "ffplay.exe") continue;
                    entry.ExtractToFile(Path.Combine(destination, entry.Name), overwrite: true);
                }
            }
            progress(1);
            ToolLocator.Refresh();
            if (ToolLocator.Ffmpeg is null) throw new InvalidOperationException(Loc.T("FFmpeg was downloaded but could not be found in the archive."));
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }
}
