using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;

namespace Lemonize.UI;

public sealed class PdfPageItem : INotifyPropertyChanged
{
    private int _rotation;
    private BitmapSource? _thumb;

    public PdfPageItem(string source, int index, string label)
    {
        Source = source;
        Index = index;
        Label = label;
    }

    public string Source { get; }
    public int Index { get; }
    public string Label { get; }

    public int Rotation
    {
        get => _rotation;
        set
        {
            _rotation = (value % 360 + 360) % 360;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rotation)));
        }
    }

    public BitmapSource? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PdfOrganizerWindow : ThemedWindow
{
    private readonly IReadOnlyList<string> _files;
    private readonly ObservableCollection<PdfPageItem> _pages = [];
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, AllowDrop = true };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private PdfPageItem? _dragItem;
    private Point _dragStart;

    public PdfOrganizerWindow(IReadOnlyList<string> files)
    {
        _files = files;
        Title = files.Count == 1 ? Loc.F("Organize · {0}", Path.GetFileName(files[0])) : Loc.F("Organize · {0} PDFs", files.Count);
        Width = 960;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var toolbar = new WrapPanel { Margin = new Thickness(16, 12, 16, 8) };
        toolbar.Children.Add(Tool("", "Rotate left", () => Rotate(-90)));
        toolbar.Children.Add(Tool("", "Rotate right", () => Rotate(90), flip: true));
        toolbar.Children.Add(Tool("", "Delete", Delete));
        toolbar.Children.Add(Tool("", "Move earlier", () => Move(-1)));
        toolbar.Children.Add(Tool("", "Move later", () => Move(1)));
        toolbar.Children.Add(Tool("", "Extract selected", Extract));

        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 16) };
        var save = new Button { Content = "Save PDF", Style = (Style)FindResource("Primary"), MinWidth = 110 };
        save.Click += (_, _) => Save();
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);
        _status.Style = (Style)FindResource("Dim");
        footer.Children.Add(_status);

        _list.ItemsSource = _pages;
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        _list.ItemTemplate = BuildTemplate();
        _list.Margin = new Thickness(12, 0, 12, 0);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) Delete();
        };
        _list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(_list);
            _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as PdfPageItem;
        };
        _list.PreviewMouseMove += OnListMouseMove;
        _list.Drop += OnListDrop;

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(footer);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
    }

    private Button Tool(string glyph, string text, Action action, bool flip = false)
    {
        var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) };
        if (flip) icon.RenderTransform = new ScaleTransform(-1, 1, 7, 7);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = panel, Margin = new Thickness(0, 0, 8, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    private static DataTemplate BuildTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.WidthProperty, 150.0);
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(6));

        var holder = new FrameworkElementFactory(typeof(Border));
        holder.SetValue(FrameworkElement.HeightProperty, 190.0);
        holder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x30)));
        holder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new Binding(nameof(PdfPageItem.Thumb)));
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        image.SetBinding(FrameworkElement.LayoutTransformProperty, new Binding(nameof(PdfPageItem.Rotation)) { Converter = new RotationConverter() });
        holder.AppendChild(image);
        stack.AppendChild(holder);

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(PdfPageItem.Label)));
        label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(TextBlock.FontSizeProperty, 11.5);
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
        stack.AppendChild(label);
        return new DataTemplate { VisualTree = stack };
    }

    private sealed class RotationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => new RotateTransform((int)value);
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private async Task LoadAsync()
    {
        _status.Text = Loc.T("Loading pages…");
        try
        {
            foreach (var file in _files)
            {
                var count = await PdfEngine.PageCountAsync(file);
                var prefix = _files.Count > 1 ? OutputPaths.BaseName(file) + " · " : "";
                var items = Enumerable.Range(0, count).Select(i => new PdfPageItem(file, i, $"{prefix}p. {i + 1}")).ToList();
                foreach (var item in items) _pages.Add(item);
                foreach (var item in items)
                {
                    var png = (await PdfEngine.RenderAsync(file, [item.Index], 280))[0];
                    item.Thumb = ImageEngine.BitmapFromBytes(png);
                }
            }
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _status.Text = Loc.F("This PDF can't be opened: {0}", ex.Message);
        }
    }

    private void UpdateStatus() => _status.Text = Loc.F("{0} pages · drag to reorder, Delete to remove", _pages.Count);

    private List<PdfPageItem> Selected() => _list.SelectedItems.Cast<PdfPageItem>().OrderBy(_pages.IndexOf).ToList();

    private void Rotate(int degrees)
    {
        foreach (var page in Selected()) page.Rotation += degrees;
    }

    private void Delete()
    {
        foreach (var page in Selected()) _pages.Remove(page);
        UpdateStatus();
    }

    private void Move(int direction)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var ordered = direction < 0 ? selected : Enumerable.Reverse(selected).ToList();
        foreach (var page in ordered)
        {
            var index = _pages.IndexOf(page);
            var target = index + direction;
            if (target < 0 || target >= _pages.Count || selected.Contains(_pages[target])) continue;
            _pages.Move(index, target);
        }
        foreach (var page in selected) _list.SelectedItems.Add(page);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_list);
        if (Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(_list, new DataObject(typeof(PdfPageItem), item), DragDropEffects.Move);
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PdfPageItem)) is not PdfPageItem dragged) return;
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as PdfPageItem;
        var from = _pages.IndexOf(dragged);
        var to = target is null ? _pages.Count - 1 : _pages.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) _pages.Move(from, to);
    }

    private IReadOnlyList<PdfPageRef> Refs(IEnumerable<PdfPageItem> pages) =>
        pages.Select(p => new PdfPageRef(p.Source, p.Index, p.Rotation)).ToList();

    private void Extract()
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            Dialogs.Message(Loc.T("Extract pages"), Loc.T("Select the pages to extract first (Ctrl+click or Shift+click for several)."));
            return;
        }
        Enqueue(Loc.T("Extract pages"), Refs(selected), OutputPaths.For(_files[0], "pdf", Loc.T("(extract)")));
    }

    private void Save()
    {
        if (_pages.Count == 0)
        {
            Dialogs.Error(Loc.T("Save PDF"), Loc.T("There are no pages left."));
            return;
        }
        var output = _files.Count == 1
            ? OutputPaths.ForToolResult(_files[0], Loc.T("(organized)"))
            : OutputPaths.ForGroup(_files, Loc.T("Merged"), "pdf");
        Enqueue(_files.Count == 1 ? Loc.F("Organize {0}", Path.GetFileName(_files[0])) : Loc.T("Combine PDFs"), Refs(_pages), output);
        Close();
    }

    private void Enqueue(string title, IReadOnlyList<PdfPageRef> pages, string output)
    {
        JobQueue.Enqueue(title, _files[0], (job, _) =>
        {
            PdfEngine.Assemble(pages, output);
            job.Outputs.Add(output);
            return Task.CompletedTask;
        });
    }
}
