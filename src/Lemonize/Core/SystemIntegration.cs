using System.Diagnostics;
using Microsoft.Win32;

namespace Lemonize.Core;

/// <summary>Per-user registry integration; nothing here needs administrator rights.</summary>
public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Lemonize";
    private static readonly string[] MenuRoots = [@"Software\Classes\*\shell\Lemonize", @"Software\Classes\Directory\shell\Lemonize"];

    private static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static void Apply(Settings settings)
    {
        SetStartup(settings.LaunchAtStartup);
        SetContextMenu(settings.ExplorerContextMenu);
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppName, $"\"{ExePath}\" --background");
        else key.DeleteValue(AppName, false);
    }

    private static void SetContextMenu(bool enabled)
    {
        foreach (var root in MenuRoots)
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, false);
                continue;
            }
            using var key = Registry.CurrentUser.CreateSubKey(root);
            key.SetValue("MUIVerb", "Lemonize…");
            key.SetValue("Icon", $"\"{ExePath}\",0");
            // Explorer starts one process per selected file; the running instance gathers them into one wheel.
            key.SetValue("MultiSelectModel", "Player");
            using var command = key.CreateSubKey("command");
            command.SetValue("", $"\"{ExePath}\" --wheel \"%1\"");
        }
    }
}
