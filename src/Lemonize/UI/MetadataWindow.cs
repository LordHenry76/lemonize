using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;

namespace Lemonize.UI;

public sealed class MetadataWindow : ThemedWindow
{
    private readonly IReadOnlyList<string> _files;
    private readonly ComboBox _picker = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _editor = new();
    private readonly StackPanel _details = new();
    private readonly Dictionary<string, TextBox> _fields = [];
    private Dictionary<string, string> _initial = [];
    private string _current;

    public MetadataWindow(IReadOnlyList<string> files)
    {
        _files = files;
        _current = files[0];
        Title = "Metadata";
        Width = 620;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(20) };
        if (files.Count > 1)
        {
            _picker.ItemsSource = files.Select(Path.GetFileName).ToList();
            _picker.SelectedIndex = 0;
            _picker.SelectionChanged += (_, _) => Load(_files[_picker.SelectedIndex]);
            DockPanel.SetDock(_picker, Dock.Top);
            root.Children.Add(_picker);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var strip = new Button { Content = "Remove all metadata", Margin = new Thickness(0, 0, 8, 0) };
        strip.Click += (_, _) => Strip();
        var save = new Button { Content = "Save changes", Style = (Style)FindResource("Primary") };
        save.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(strip);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel();
        content.Children.Add(_editor);
        content.Children.Add(_details);
        scroll.Content = content;
        root.Children.Add(scroll);
        Content = root;

        Loaded += (_, _) => Load(_current);
    }

    private async void Load(string path)
    {
        _current = path;
        _editor.Children.Clear();
        _details.Children.Clear();
        _fields.Clear();
        _details.Children.Add(new TextBlock { Text = Loc.T("Reading…"), Style = (Style)FindResource("Dim") });

        List<MetaEntry> entries;
        try
        {
            entries = await Task.Run(() => MetadataEngine.ReadAsync(path));
        }
        catch (Exception ex)
        {
            _details.Children.Clear();
            _details.Children.Add(new TextBlock { Text = Loc.F("Couldn't read metadata: {0}", ex.Message), Foreground = (Brush)FindResource("Danger"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (path != _current) return;

        _details.Children.Clear();
        _initial = InitialValues(path, entries);

        // Editable fields.
        var editable = MetadataEngine.EditableFields(path);
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 16) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < editable.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Loc.T(Capitalize(editable[i])), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            var box = new TextBox { Text = _initial.GetValueOrDefault(editable[i], ""), Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            _fields[editable[i]] = box;
        }
        var editStack = new StackPanel();
        editStack.Children.Add(new TextBlock { Text = Loc.T("Edit"), Style = (Style)FindResource("Heading") });
        editStack.Children.Add(grid);
        card.Child = editStack;
        _editor.Children.Add(card);

        // Everything that was found.
        foreach (var group in entries.GroupBy(e => e.Group))
        {
            _details.Children.Add(new TextBlock { Text = Loc.T(group.Key), Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 8, 0, 6) });
            foreach (var entry in group)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var key = new TextBlock { Text = Loc.T(entry.Key), Width = 170, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Key };
                DockPanel.SetDock(key, Dock.Left);
                row.Children.Add(key);
                row.Children.Add(new TextBox { Text = entry.Value, IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap });
                _details.Children.Add(row);
            }
        }
    }

    private static Dictionary<string, string> InitialValues(string path, List<MetaEntry> entries)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Find(string group, string key) => entries.FirstOrDefault(e => e.Group == group && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

        foreach (var field in MetadataEngine.EditableFields(path))
        {
            var value = field switch
            {
                "Created" or "Modified" when Find(MetadataEngine.FileGroup, field) is { } v => v,
                "Artist" or "Copyright" => Find("EXIF", field),
                "Description" => Find("EXIF", "ImageDescription") ?? Find("Document", field),
                "Date taken" => Find("EXIF", "DateTimeOriginal") is { } d && d.Length >= 19 ? d[..10].Replace(':', '-') + d[10..] : null,
                _ => Find("Document", field) ?? Find("Tags", field),
            };
            if (value is not null) values[field] = value;
        }
        return values;
    }

    private async Task SaveAsync()
    {
        var changed = _fields.Where(f => f.Value.Text != _initial.GetValueOrDefault(f.Key, ""))
            .ToDictionary(f => f.Key, f => f.Value.Text);
        if (changed.Count == 0)
        {
            Close();
            return;
        }
        IsEnabled = false;
        try
        {
            await Task.Run(() => MetadataEngine.WriteAsync(_current, changed, CancellationToken.None));
            Load(_current);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Couldn't save metadata"), ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Strip()
    {
        var supported = _files.Where(f => MetadataEngine.SupportsStrip(FileKinds.Detect(f))).ToList();
        if (supported.Count == 0)
        {
            Dialogs.Error(Loc.T("Remove metadata"), Loc.T("Removing metadata isn't supported for this file type."));
            return;
        }
        foreach (var file in supported)
        {
            JobQueue.Enqueue(Loc.F("Remove metadata · {0}", Path.GetFileName(file)), file, async (job, token) =>
            {
                var output = OutputPaths.ForToolResult(file, Loc.T("(clean)"));
                await MetadataEngine.StripAsync(file, output, token);
                job.Outputs.Add(output);
            });
        }
        Close();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].Replace('_', ' ');
}
