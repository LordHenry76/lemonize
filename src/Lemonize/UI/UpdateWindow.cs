using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Lemonize.Core;
using Lemonize.Jobs;
using Orientation = System.Windows.Controls.Orientation;

namespace Lemonize.UI;

/// <summary>Suggests a new version: what's new, then update now, later or skip it.</summary>
public sealed class UpdateWindow : ThemedWindow
{
    private static UpdateWindow? _open;

    private readonly UpdateInfo _update;
    private readonly ProgressBar _progress = new() { Maximum = 1, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status;
    private readonly Button _skip = new() { Content = "Skip this version" };
    private readonly Button _later = new() { Content = "Later", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _install;
    private CancellationTokenSource? _download;
    private string? _installer;

    private UpdateWindow(UpdateInfo update)
    {
        _update = update;
        Title = Loc.T("Update available");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var dim = (Style)Application.Current.FindResource("Dim");
        _status = new TextBlock { Text = Loc.T("Lemonize closes and reopens by itself. Save any work still open first."), Style = dim, Margin = new Thickness(0, 12, 0, 0) };
        _install = new Button { Content = "Update now", MinWidth = 110, IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        body.Children.Add(new TextBlock { Text = Loc.F("Lemonize {0} is available", update.Version), Style = (Style)Application.Current.FindResource("Heading"), Margin = new Thickness(0, 0, 0, 2) });
        body.Children.Add(new TextBlock { Text = Loc.F("You have version {0}.", AppInfo.Version), Style = dim, Margin = new Thickness(0, 0, 0, 12) });
        if (update.Notes.Length > 0)
            body.Children.Add(new Border
            {
                Style = (Style)Application.Current.FindResource("Card"),
                Padding = new Thickness(14, 10, 14, 10),
                Child = new ScrollViewer
                {
                    MaxHeight = 240,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = update.Notes, TextWrapping = TextWrapping.Wrap },
                },
            });
        body.Children.Add(_progress);
        body.Children.Add(_status);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_later);
        right.Children.Add(_install);
        var buttons = new DockPanel { Margin = new Thickness(24, 8, 24, 20), LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_skip);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        _skip.Click += (_, _) =>
        {
            Updater.Skip(_update);
            Close();
        };
        _later.Click += (_, _) => Close();
        _install.Click += (_, _) => _ = UpdateAsync();
        Closed += (_, _) => _open = null;
    }

    public static void Open(UpdateInfo update)
    {
        _open ??= new UpdateWindow(update);
        _open.ShowOnTop();
    }

    /// <summary>Closing during the download cancels it.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _download?.Cancel();
        base.OnClosing(e);
    }

    private async Task UpdateAsync()
    {
        if (_installer is null && !await DownloadAsync()) return;

        // Checked now rather than before the download, which can take minutes.
        if (JobQueue.HasActiveJobs &&
            !Dialogs.Confirm(Loc.T("Update Lemonize?"), Loc.T("Some files are still being processed. Updating cancels them."), Loc.T("Update now")))
            return;

        try
        {
            Updater.Install(_installer!);
            Close();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            OfferManualDownload(ex.Message);
        }
    }

    /// <returns>True once the installer is ready.</returns>
    private async Task<bool> DownloadAsync()
    {
        _download = new CancellationTokenSource();
        _install.IsEnabled = false;
        _skip.IsEnabled = false;
        _later.Content = Loc.T("Cancel");
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _status.Text = Loc.T("Downloading the update…");
        try
        {
            _installer = await Updater.DownloadAsync(_update, p => Dispatcher.BeginInvoke(() =>
            {
                _progress.Value = p;
                _status.Text = Loc.F("Downloading the update… {0}%", (int)(p * 100));
            }), _download.Token);
            return true;
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            // User canceled.
            return false;
        }
        catch (Exception ex)
        {
            Diag.Log($"updater: download failed: {ex.Message}");
            OfferManualDownload(ex.Message);
            return false;
        }
        finally
        {
            _download = null;
            _install.IsEnabled = true;
            _skip.IsEnabled = true;
            _later.Content = Loc.T("Later");
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = Loc.T("Lemonize closes and reopens by itself. Save any work still open first.");
        }
    }

    private static void OfferManualDownload(string error)
    {
        if (Dialogs.Confirm(Loc.T("Update failed"), Loc.F("{0}\n\nYou can download the installer from GitHub instead.", error), Loc.T("Open GitHub")))
            Commands.Open(Updater.ReleasesPage);
    }
}
