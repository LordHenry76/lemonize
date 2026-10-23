using System.Reflection;

namespace Lemonize.Core;

public static class AppInfo
{
    public const string Developer = "[wild]lab";
    public const string DeveloperUrl = "https://lordhenry76.github.io";

    /// <summary>"1.0.1": the project &lt;Version&gt;, without the trailing ".0" revision.</summary>
    public static string Version { get; } = FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);

    private static string FormatVersion(Version? v) => v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}
