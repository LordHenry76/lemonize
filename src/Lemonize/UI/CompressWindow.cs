using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;

namespace Lemonize.UI;

public sealed class CompressWindow : ThemedWindow
{
    private readonly IReadOnlyList<string> _files;
    private readonly long _totalBytes;
    private readonly RadioButton _byLevel = new() { Content = "Compression level", GroupName = "mode" };
    private readonly RadioButton _byPercent = new() { Content = "Reduce by a percentage", GroupName = "mode", IsChecked = true };
    private readonly List<(RadioButton Button, CompressLevel Level)> _levels = [];
    private readonly RadioButton _bySize = new() { Content = "Target size for each file", GroupName = "mode" };
    private readonly Slider _percent = new() { Minimum = 10, Maximum = 90, Value = 50, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _percentLabel = new() { Width = 120, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _size = new() { Width = 100 };
    private readonly ComboBox _unit = new() { Width = 70, Margin = new Thickness(8, 0, 0, 0), ItemsSource = new[] { "KB", "MB", "GB" }, SelectedIndex = 1 };

    public CompressWindow(IReadOnlyList<string> files)
    {
        _files = files;
        _totalBytes = files.Sum(f => new FileInfo(f).Length);
        Title = "Compress";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var largest = files.Max(f => new FileInfo(f).Length);
        var suggested = largest / 2.0;
        _unit.SelectedIndex = suggested < 1024 * 1024 ? 0 : 1;
        _size.Text = (suggested / (_unit.SelectedIndex == 0 ? 1024.0 : 1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture);

        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock
        {
            Text = files.Count == 1 ? Path.GetFileName(files[0]) : Loc.F("{0} files", files.Count),
            Style = (Style)FindResource("Heading"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock { Text = Loc.F("Current size: {0}", OutputPaths.FormatSize(_totalBytes)), Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 0, 0, 16) });

        // Preset levels for PDFs: the simplest choice, so it comes first and is preselected.
        if (files.All(f => FileKinds.Detect(f) == FileKind.Pdf))
        {
            body.Children.Add(_byLevel);
            var levelRow = new UniformGrid { Rows = 1, Margin = new Thickness(26, 6, 0, 14) };
            AddLevel(levelRow, CompressLevel.Light, "Light", "best quality");
            AddLevel(levelRow, CompressLevel.Medium, "Medium", "balanced", selected: true);
            AddLevel(levelRow, CompressLevel.Strong, "Maximum", "smallest file");
            body.Children.Add(levelRow);
            _byLevel.IsChecked = true;
        }

        body.Children.Add(_byPercent);
        var percentRow = new DockPanel { Margin = new Thickness(26, 4, 0, 12) };
        DockPanel.SetDock(_percentLabel, Dock.Right);
        percentRow.Children.Add(_percentLabel);
        percentRow.Children.Add(_percent);
        body.Children.Add(percentRow);

        body.Children.Add(_bySize);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 4, 0, 4) };
        sizeRow.Children.Add(_size);
        sizeRow.Children.Add(_unit);
        body.Children.Add(sizeRow);

        body.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 14, 0, 0),
            Text = "Images keep their format when possible; videos become MP4 (H.264); PDFs re-compress embedded images. " +
                   "Results are saved as new files marked “(compressed)”.",
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var go = new Button { Content = "Compress", IsDefault = true, MinWidth = 100, Style = (Style)FindResource("Primary") };
        go.Click += (_, _) => Start();
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        body.Children.Add(buttons);
        Content = body;

        _percent.ValueChanged += (_, _) => UpdateLabel();
        _size.GotFocus += (_, _) => _bySize.IsChecked = true;
        _percent.GotFocus += (_, _) => _byPercent.IsChecked = true;
        _percent.PreviewMouseDown += (_, _) => _byPercent.IsChecked = true;
        _unit.DropDownOpened += (_, _) => _bySize.IsChecked = true;
        UpdateLabel();
    }

    private void AddLevel(Panel row, CompressLevel level, string label, string caption, bool selected = false)
    {
        var button = new RadioButton
        {
            Style = (Style)FindResource("ToolToggle"), GroupName = "level", IsChecked = selected,
            Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        // English on purpose: Loc.Apply translates the window once it loads. Translating here too would run
        // twice, and "Medium" → "Media" would become "Multimedia" (the media metadata group).
        var title = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var sub = new TextBlock { Text = caption, FontSize = 11, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center };
        // Follow the toggle's foreground (dark text on the lemon highlight when selected).
        foreach (var text in new[] { title, sub })
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(sub);
        button.Content = content;
        button.Checked += (_, _) => _byLevel.IsChecked = true;
        row.Children.Add(button);
        _levels.Add((button, level));
    }

    private void UpdateLabel()
    {
        var after = _totalBytes * (1 - _percent.Value / 100);
        _percentLabel.Text = $"{_percent.Value:0}% → {OutputPaths.FormatSize((long)after)}";
    }

    private void Start()
    {
        CompressOptions options;
        if (_byLevel.IsChecked == true)
        {
            var level = _levels.FirstOrDefault(l => l.Button.IsChecked == true).Level;
            options = new CompressOptions(false, 0, 0, level == CompressLevel.None ? CompressLevel.Medium : level);
        }
        else if (_byPercent.IsChecked == true)
        {
            options = new CompressOptions(true, _percent.Value / 100, 0);
        }
        else
        {
            if (!double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || value <= 0)
            {
                Dialogs.Error(Loc.T("Compress"), Loc.T("Enter a target size greater than zero."));
                return;
            }
            var multiplier = _unit.SelectedIndex switch { 0 => 1024L, 1 => 1024L * 1024, _ => 1024L * 1024 * 1024 };
            options = new CompressOptions(false, 0, (long)(value * multiplier));
        }

        foreach (var file in _files)
        {
            JobQueue.Enqueue(Loc.F("Compress {0}", Path.GetFileName(file)), file, async (job, token) =>
                job.Outputs.Add(await CompressionEngine.CompressAsync(file, options, job, token)));
        }
        Close();
    }
}
