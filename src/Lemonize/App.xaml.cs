using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Lemonize.Core;
using Lemonize.Interop;
using Lemonize.UI;
using Forms = System.Windows.Forms;

namespace Lemonize;

public partial class App
{
    private const string MutexName = "Lemonize.SingleInstance.v1";
    // The pipe name carries the owner's process id (published in a file), so a stale process that
    // still holds an old pipe can never swallow commands meant for the live instance.
    private const string PipePrefix = "Lemonize.Pipe.v2.";
    private static readonly string InstanceFile = Path.Combine(AppPaths.LocalData, "instance.pid");

    private static MouseHook? _hook;
    private static Forms.NotifyIcon? _tray;
    private static MainWindow? _main;
    private static ProgressWindow? _progress;
    private Mutex? _mutex;
    private readonly List<string> _pendingWheel = [];
    private DispatcherTimer? _wheelGather;

    public static WheelWindow Wheel { get; private set; } = null!;
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst && !TryTakeOver(_mutex) && ForwardToRunningInstance(e.Args))
        {
            Shutdown();
            return;
        }
        // Either we are first, or the other instance is gone/unresponsive (e.g. stuck while exiting): carry on.

        Loc.Initialize(Settings.Current.Language);
        Diag.Log($"app: start pid={Environment.ProcessId} first={isFirst} args=[{string.Join(" ", e.Args)}] exe={Environment.ProcessPath}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            Dialogs.Error(Loc.T("Something went wrong"), args.Exception.Message);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        Wheel = new WheelWindow();
        _progress = new ProgressWindow();
        CreateTray();
        ApplyHookSetting();
        _ = Task.Run(ListenAsync);

        var background = e.Args.Contains("--background");
        if (!background || !Settings.Current.FirstRunDone) ShowMain();
        if (!Settings.Current.FirstRunDone)
        {
            Settings.Current.FirstRunDone = true;
            Settings.Current.Save();
        }
        HandleArgs(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Diag.Log("app: exiting");
        _hook?.Dispose();
        Diag.Log("app: hook stopped");
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
        Diag.Log("app: exit done");
        Diag.Flush();

        // Everything is saved and cleaned up. End the process right here instead of letting Windows
        // unload every library: third-party components loaded by the OS (codecs, shell handlers)
        // have been seen hanging the process forever at that stage.
        Native.TerminateProcess(Native.GetCurrentProcess(), 0);
    }

    /// <summary>The installer passes the tasks the user ticked, so settings and registry stay in sync.</summary>
    private static void ApplyInstallerOptions(string[] args)
    {
        var startup = args.Contains("--enable-startup");
        var contextMenu = args.Contains("--enable-contextmenu");
        if (!startup && !contextMenu) return;
        if (startup) Settings.Current.LaunchAtStartup = true;
        if (contextMenu) Settings.Current.ExplorerContextMenu = true;
        Settings.Current.Save();
        try
        {
            SystemIntegration.Apply(Settings.Current);
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    public static void ApplyHookSetting()
    {
        if (Settings.Current.EnableDragWheel)
        {
            if (_hook is null)
            {
                _hook = new MouseHook();
                _hook.ModifiedDrag += (point, mods) => Wheel.Arm(point, mods);
                _hook.ButtonReleased += () => Wheel.OnMouseReleased();
            }
            _hook.Start();
        }
        else
        {
            _hook?.Stop();
        }
    }

    public static void ShowMain()
    {
        _main ??= new MainWindow();
        _main.ShowOnTop();
    }

    public static void Quit()
    {
        if (Jobs.JobQueue.HasActiveJobs &&
            !Dialogs.Confirm(Loc.T("Quit Lemonize?"), Loc.T("Some files are still being processed. Quitting cancels them."), Loc.T("Quit")))
            return;
        foreach (var job in Jobs.JobQueue.Jobs) job.Cancel();
        IsExiting = true;
        Current.Shutdown();
    }

    // ---------- Tray ----------

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Open Lemonize"), null, (_, _) => ShowMain());
        menu.Items.Add(Loc.T("Show progress"), null, (_, _) => _progress?.Show());
        var pause = new Forms.ToolStripMenuItem(Loc.T("Drag wheel enabled")) { Checked = Settings.Current.EnableDragWheel, CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            Settings.Current.EnableDragWheel = pause.Checked;
            Settings.Current.Save();
            ApplyHookSetting();
        };
        menu.Items.Add(pause);
        menu.Items.Add(Loc.T("Settings…"), null, (_, _) => SettingsWindow.Open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        // Let the menu close before shutting down; disposing the tray icon from inside its own menu can hang.
        menu.Items.Add(Loc.T("Quit Lemonize"), null, (_, _) => Dispatcher.BeginInvoke(Quit));
        menu.Opening += (_, _) => pause.Checked = Settings.Current.EnableDragWheel;

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = $"Lemonize {AppInfo.Version} — " + Loc.T("drag files with Shift"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowMain();
        };
    }

    // ---------- Single instance ----------

    /// <summary>The previous owner died without releasing the mutex: it is ours now.</summary>
    private static bool TryTakeOver(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <returns>True when a running instance received the arguments.</returns>
    private static bool ForwardToRunningInstance(string[] args)
    {
        try
        {
            if (!int.TryParse(File.ReadAllText(InstanceFile).Trim(), out var pid)) return false;
            using var client = new NamedPipeClientStream(".", PipePrefix + pid, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(2000);
            var message = System.Text.Encoding.UTF8.GetBytes((args.Length == 0 ? "--show" : string.Join("\t", args)) + "\n");
            using var timeout = new CancellationTokenSource(2000);
            client.WriteAsync(message, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        var pipeName = PipePrefix + Environment.ProcessId;
        File.WriteAllText(InstanceFile, Environment.ProcessId.ToString());
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 0);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync();
                if (line is not null) await Dispatcher.InvokeAsync(() => HandleArgs(line.Split('\t')));
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }
    }

    private void HandleArgs(string[] args)
    {
        // Also reached via the pipe when another copy was already running at the end of setup.
        ApplyInstallerOptions(args);
        if (args.Contains("--show")) ShowMain();
        if (args.Contains("--quit"))
        {
            // Used by scripts (e.g. before rebuilding) to close the running instance.
            foreach (var job in Jobs.JobQueue.Jobs) job.Cancel();
            IsExiting = true;
            Current.Shutdown();
            return;
        }
        var index = Array.IndexOf(args, "--wheel");
        if (index < 0 || index + 1 >= args.Length) return;

        // Explorer launches one process per selected item; gather them briefly into one wheel.
        _pendingWheel.AddRange(args.Skip(index + 1).Where(a => File.Exists(a) || Directory.Exists(a)));
        _wheelGather ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
        {
            _wheelGather!.Stop();
            var files = _pendingWheel.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingWheel.Clear();
            if (files.Count > 0) Wheel.ShowFor(files, Native.CursorPixels());
        }, Dispatcher);
        _wheelGather.Stop();
        _wheelGather.Start();
    }

    private static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.LocalData, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (IOException)
        {
            // Logging must never crash the app.
        }
    }
}
