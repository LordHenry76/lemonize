using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lemonize.Core;
using Lemonize.Interop;

namespace Lemonize.UI;

public partial class MainWindow
{
    private CancellationTokenSource? _download;

    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => Refresh();
    }

    /// <summary>Closing the hub only hides it; Lemonize keeps running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (App.IsExiting) return;
        e.Cancel = true;
        Hide();
    }

    private void Refresh()
    {
        ComponentList.Children.Clear();
        AddComponent("FFmpeg", ToolLocator.Ffmpeg is not null, "video & audio");
        AddComponent("Office / LibreOffice", ToolLocator.LibreOffice is not null || ToolLocator.HasWord, "DOC, XLS, PPT → PDF");
        AddComponent("Ghostscript", ToolLocator.Ghostscript is not null, "stronger PDF compression", optional: true);
        AddComponent("Pandoc", ToolLocator.Pandoc is not null, "richer DOCX ↔ EPUB", optional: true);
        FfmpegButton.Visibility = ToolLocator.Ffmpeg is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddComponent(string name, bool ready, string purpose, bool optional = false)
    {
        var color = ready ? (Brush)FindResource("Success") : optional ? (Brush)FindResource("TextDim") : (Brush)FindResource("Danger");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 18, 2), ToolTip = Loc.T(purpose) };
        panel.Children.Add(new TextBlock { Text = ready ? "" : optional ? "" : "", Style = (Style)FindResource("Icon"), FontSize = 12, Foreground = color, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(new TextBlock { Text = name, FontSize = 12 });
        ComponentList.Children.Add(panel);
    }

    private void OnZoneDragEnter(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DashedBorder.Stroke = ok ? (Brush)FindResource("Accent") : (Brush)FindResource("Border");
        e.Handled = true;
    }

    private void OnZoneDragLeave(object sender, DragEventArgs e) => DashedBorder.Stroke = (Brush)FindResource("Border");

    private void OnZoneDrop(object sender, DragEventArgs e)
    {
        DashedBorder.Stroke = (Brush)FindResource("Border");
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        var mode = e.KeyStates.HasFlag(DragDropKeyStates.AltKey) ? WheelMode.Tools
            : e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? WheelMode.Actions
            : WheelMode.Convert;
        Dispatcher.BeginInvoke(() => App.Wheel.ShowFor(files, Native.CursorPixels(), mode));
    }

    private void OnBrowse(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Loc.T("Choose files for Lemonize") };
        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            App.Wheel.ShowFor(dialog.FileNames, Native.CursorPixels());
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsWindow.Open();

    private async void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        if (_download is not null)
        {
            _download.Cancel();
            return;
        }
        _download = new CancellationTokenSource();
        FfmpegButton.Content = Loc.T("Cancel");
        FfmpegProgress.Visibility = Visibility.Visible;
        try
        {
            await FfmpegInstaller.InstallAsync(p => Dispatcher.BeginInvoke(() => FfmpegProgress.Value = p), _download.Token);
        }
        catch (OperationCanceledException)
        {
            // User canceled.
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("FFmpeg download failed"), ex.Message);
        }
        finally
        {
            _download = null;
            FfmpegButton.Content = Loc.T("Download FFmpeg");
            FfmpegProgress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }
}
