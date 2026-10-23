using System.Windows;
using Lemonize.Core;
using Lemonize.Jobs;

namespace Lemonize.UI;

public partial class SettingsWindow
{
    private static SettingsWindow? _open;
    private string? _customFolder;

    private SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _open = null;
        var s = Settings.Current;
        DragWheel.IsChecked = s.EnableDragWheel;
        Startup.IsChecked = s.LaunchAtStartup;
        ContextMenuBox.IsChecked = s.ExplorerContextMenu;
        ProgressWin.IsChecked = s.ShowProgressWindow;
        Reveal.IsChecked = s.RevealWhenDone;
        Beside.IsChecked = s.OutputLocation == OutputLocation.BesideOriginal;
        Custom.IsChecked = s.OutputLocation == OutputLocation.CustomFolder;
        _customFolder = s.CustomOutputFolder;
        FolderLabel.Text = _customFolder ?? Loc.T("(none)");
        Overwrite.IsChecked = s.ToolsOverwriteOriginal;
        Quality.Value = s.JpegQuality;
        Parallel.Value = s.MaxParallelJobs;
        FfmpegPath.Text = s.FfmpegPath ?? "";
        FfmpegStatus.Text = ToolLocator.Ffmpeg is { } ff ? Loc.F("Using {0}", ff) : Loc.T("FFmpeg not found. Use “Download FFmpeg” in the main window.");
        LanguageBox.ItemsSource = Loc.Languages.Select(l => Loc.T(l.Name)).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == s.Language));
    }

    public static void Open()
    {
        _open ??= new SettingsWindow();
        _open.ShowOnTop();
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => QualityLabel.Text = ((int)e.NewValue).ToString();

    private void OnParallelChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ParallelLabel.Text = ((int)e.NewValue).ToString();

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Save converted files to…"), InitialDirectory = _customFolder };
        if (dialog.ShowDialog(this) != true) return;
        _customFolder = dialog.FolderName;
        FolderLabel.Text = _customFolder;
        Custom.IsChecked = true;
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe", Title = Loc.T("Locate ffmpeg.exe") };
        if (dialog.ShowDialog(this) == true) FfmpegPath.Text = dialog.FileName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        s.EnableDragWheel = DragWheel.IsChecked == true;
        s.LaunchAtStartup = Startup.IsChecked == true;
        s.ExplorerContextMenu = ContextMenuBox.IsChecked == true;
        s.ShowProgressWindow = ProgressWin.IsChecked == true;
        s.RevealWhenDone = Reveal.IsChecked == true;
        s.OutputLocation = Custom.IsChecked == true && _customFolder is not null ? OutputLocation.CustomFolder : OutputLocation.BesideOriginal;
        s.CustomOutputFolder = _customFolder;
        s.ToolsOverwriteOriginal = Overwrite.IsChecked == true;
        s.JpegQuality = (int)Quality.Value;
        s.MaxParallelJobs = (int)Parallel.Value;
        s.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath.Text) ? null : FfmpegPath.Text.Trim();
        var language = Loc.Languages[Math.Max(0, LanguageBox.SelectedIndex)].Code;
        var languageChanged = language != s.Language;
        s.Language = language;
        s.Save();

        try
        {
            SystemIntegration.Apply(s);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Couldn't update Windows integration"), ex.Message);
        }
        ToolLocator.Refresh();
        JobQueue.Configure(s.MaxParallelJobs);
        App.ApplyHookSetting();
        if (languageChanged) Dialogs.Message(Loc.T("Language"), Loc.T("Restart Lemonize to apply the new language."));
        Close();
    }
}
