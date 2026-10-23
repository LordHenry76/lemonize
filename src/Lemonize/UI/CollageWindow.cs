using System.Windows;
using System.Windows.Controls;
using ImageMagick;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;

using Orientation = System.Windows.Controls.Orientation;

namespace Lemonize.UI;

public sealed class CollageWindow : ThemedWindow
{
    private readonly IReadOnlyList<string> _files;
    private readonly Slider _columns;
    private readonly Slider _spacing = new() { Minimum = 0, Maximum = 80, Value = 16, TickFrequency = 2, IsSnapToTickEnabled = true };
    private readonly ComboBox _size = new() { ItemsSource = new[] { Loc.T("Small (400 px tiles)"), Loc.T("Medium (800 px tiles)"), Loc.T("Large (1200 px tiles)") }, SelectedIndex = 1 };
    private readonly ComboBox _background = new() { ItemsSource = new[] { Loc.T("White"), Loc.T("Black"), Loc.T("Transparent") }, SelectedIndex = 0 };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "JPEG", "PNG" }, SelectedIndex = 0 };

    public CollageWindow(IReadOnlyList<string> files)
    {
        _files = files.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        Title = "Collage";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _columns = new Slider
        {
            Minimum = 1, Maximum = Math.Min(10, files.Count), TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = Math.Ceiling(Math.Sqrt(files.Count)),
        };

        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = Loc.F("Collage of {0} images", files.Count), Style = (Style)FindResource("Heading") });
        var columnsLabel = Row(body, "Columns", _columns);
        var spacingLabel = Row(body, "Spacing", _spacing);
        Row(body, "Tile size", _size);
        Row(body, "Background", _background);
        Row(body, "Format", _format);
        _columns.ValueChanged += (_, _) => columnsLabel.Text = Loc.F("Columns · {0}", _columns.Value);
        _spacing.ValueChanged += (_, _) => spacingLabel.Text = Loc.F("Spacing · {0} px", _spacing.Value);
        columnsLabel.Text = Loc.F("Columns · {0}", _columns.Value);
        spacingLabel.Text = Loc.F("Spacing · {0} px", _spacing.Value);
        _background.SelectionChanged += (_, _) =>
        {
            if (_background.SelectedIndex == 2) _format.SelectedIndex = 1;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var go = new Button { Content = "Create collage", IsDefault = true, Style = (Style)FindResource("Primary") };
        go.Click += (_, _) => Start();
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        body.Children.Add(buttons);
        Content = body;
    }

    private static TextBlock Row(Panel parent, string label, FrameworkElement control)
    {
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) };
        parent.Children.Add(text);
        parent.Children.Add(control);
        return text;
    }

    private void Start()
    {
        var columns = (int)_columns.Value;
        var spacing = (int)_spacing.Value;
        var cell = _size.SelectedIndex switch { 0 => 400, 2 => 1200, _ => 800 };
        var background = _background.SelectedIndex switch { 1 => MagickColors.Black, 2 => MagickColors.Transparent, _ => MagickColors.White };
        var ext = _format.SelectedIndex == 1 ? "png" : "jpg";
        var files = _files;
        JobQueue.Enqueue(Loc.F("Collage of {0} images", files.Count), files[0], (job, _) =>
        {
            var output = OutputPaths.ForGroup(files, "Collage", ext);
            ImageEngine.Collage(files, output, columns, cell, spacing, background);
            job.Outputs.Add(output);
            return Task.CompletedTask;
        });
        Close();
    }
}
