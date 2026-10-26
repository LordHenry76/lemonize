using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Interop;
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
    private DragGhost? _ghost;
    private int _insertAt = -1;

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
        _list.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(PdfPageItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
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
                await PdfEngine.RenderEachAsync(file, null, 280, (i, png) => items[i].Thumb = ImageEngine.BitmapFromBytes(png));
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
        if (_list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container
            || AdornerLayer.GetAdornerLayer(_list) is not { } layer)
        {
            DragDrop.DoDragDrop(_list, new DataObject(typeof(PdfPageItem), item), DragDropEffects.Move);
            return;
        }

        // A see-through copy of the page follows the pointer, held where it was grabbed; the page itself
        // stays dimmed in place, and a bar shows where it will land.
        _ghost = new DragGhost(_list, Snapshot(container), _dragStart - BoundsOf(container).TopLeft, (Brush)FindResource("Accent"));
        layer.Add(_ghost);
        container.Opacity = 0.35;
        // A timer tracks the pointer: during a drag WPF reports no mouse moves, and the list must keep
        // scrolling while the pointer rests near its edge.
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => TrackDrag(), Dispatcher);
        TrackDrag();
        timer.Start();
        try
        {
            DragDrop.DoDragDrop(_list, new DataObject(typeof(PdfPageItem), item), DragDropEffects.Move);
        }
        finally
        {
            timer.Stop();
            layer.Remove(_ghost);
            _ghost = null;
            _insertAt = -1;
            container.Opacity = 1;
            if (_list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem moved) moved.Opacity = 1;
        }
    }

    private void TrackDrag()
    {
        if (_ghost is null) return;
        var p = _list.PointFromScreen(Native.CursorPixels());
        _ghost.MoveTo(p);
        if (p.X < 0 || p.Y < 0 || p.X >= _list.ActualWidth || p.Y >= _list.ActualHeight)
        {
            _insertAt = -1;
            _ghost.ShowBar(null);
            return;
        }
        AutoScroll(p);
        (_insertAt, var bar) = InsertionAt(p);
        _ghost.ShowBar(bar);
    }

    /// <summary>Scrolls when the pointer is near the top or bottom edge, faster the closer it gets.</summary>
    private void AutoScroll(Point p)
    {
        const double zone = 48;
        if (FindChild<ScrollViewer>(_list) is not { } scroller) return;
        var step = p.Y < zone ? p.Y - zone : p.Y > _list.ActualHeight - zone ? p.Y - (_list.ActualHeight - zone) : 0;
        if (step != 0) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + step / 2);
    }

    /// <summary>
    /// Where a page dropped at <paramref name="p"/> goes: before the page under the pointer when on its left
    /// half, after it when on the right half. Returns the insertion index and the bar that marks it.
    /// </summary>
    private (int Index, Rect Bar) InsertionAt(Point p)
    {
        var slots = _pages
            .Select((page, i) => (Index: i, Bounds: _list.ItemContainerGenerator.ContainerFromItem(page) is ListBoxItem c ? BoundsOf(c) : Rect.Empty))
            .Where(s => !s.Bounds.IsEmpty).ToList();
        if (slots.Count == 0) return (_pages.Count, Rect.Empty);
        // The row under the pointer, or the nearest one when it's between rows or below the last.
        var rowTop = slots.MinBy(s => p.Y < s.Bounds.Top ? s.Bounds.Top - p.Y : p.Y > s.Bounds.Bottom ? p.Y - s.Bounds.Bottom : 0).Bounds.Top;
        var row = slots.Where(s => Math.Abs(s.Bounds.Top - rowTop) < 1).ToList();
        const double width = 3;
        foreach (var slot in row)
            if (p.X < slot.Bounds.Left + slot.Bounds.Width / 2)
                return (slot.Index, new Rect(slot.Bounds.Left - width / 2, slot.Bounds.Top + 6, width, slot.Bounds.Height - 12));
        var last = row[^1];
        return (last.Index + 1, new Rect(last.Bounds.Right - width / 2, last.Bounds.Top + 6, width, last.Bounds.Height - 12));
    }

    private Rect BoundsOf(FrameworkElement element) =>
        element.TransformToAncestor(_list).TransformBounds(new Rect(element.RenderSize));

    private static BitmapSource Snapshot(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Through a brush: rendering the element directly would include its offset inside the list.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PdfPageItem)) is not PdfPageItem dragged) return;
        Reorder(dragged, _insertAt >= 0 ? _insertAt : InsertionAt(e.GetPosition(_list)).Index);
    }

    /// <summary>Moves a page so that it lands at insertion index <paramref name="to"/> (0 = first, Count = last).</summary>
    private void Reorder(PdfPageItem dragged, int to)
    {
        var from = _pages.IndexOf(dragged);
        // Taking the page out first shifts every later slot back by one.
        if (to > from) to--;
        if (from >= 0 && to < _pages.Count && from != to) _pages.Move(from, to);
    }

    /// <summary>The dragged page at 80% opacity under the pointer, and the insertion bar.</summary>
    private sealed class DragGhost : Adorner
    {
        private readonly BitmapSource _image;
        private readonly Vector _grab;
        private readonly Brush _barBrush;
        private Point _pointer;
        private Rect? _bar;

        public DragGhost(UIElement adorned, BitmapSource image, Vector grab, Brush barBrush) : base(adorned)
        {
            _image = image;
            _grab = grab;
            _barBrush = barBrush;
            IsHitTestVisible = false;
        }

        public void MoveTo(Point pointer)
        {
            if (pointer == _pointer) return;
            _pointer = pointer;
            InvalidateVisual();
        }

        public void ShowBar(Rect? bar)
        {
            if (bar is { IsEmpty: true }) bar = null;
            if (bar == _bar) return;
            _bar = bar;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            dc.PushOpacity(0.8);
            dc.DrawImage(_image, new Rect(_pointer - _grab, new Size(_image.PixelWidth / dpi.DpiScaleX, _image.PixelHeight / dpi.DpiScaleY)));
            dc.Pop();
            // On top of the page, so the landing spot stays visible under it.
            if (_bar is { } bar) dc.DrawRoundedRectangle(_barBrush, null, bar, 1.5, 1.5);
        }
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
        var first = _files[0];
        var suffix = Loc.T("(extract)");
        Enqueue(Loc.T("Extract pages"), Refs(selected), () => OutputPaths.For(first, "pdf", suffix));
    }

    private void Save()
    {
        if (_pages.Count == 0)
        {
            Dialogs.Error(Loc.T("Save PDF"), Loc.T("There are no pages left."));
            return;
        }
        var files = _files;
        var suffix = Loc.T("(organized)");
        var merged = Loc.T("Merged");
        Enqueue(files.Count == 1 ? Loc.F("Organize {0}", Path.GetFileName(files[0])) : Loc.T("Combine PDFs"), Refs(_pages),
            () => files.Count == 1 ? OutputPaths.ForToolResult(files[0], suffix) : OutputPaths.ForGroup(files, merged, "pdf"));
        Close();
    }

    /// <summary>The output name is picked when the job runs, so it is reserved against other running jobs.</summary>
    private void Enqueue(string title, IReadOnlyList<PdfPageRef> pages, Func<string> outputFor)
    {
        JobQueue.Enqueue(title, _files[0], (job, _) =>
        {
            var output = outputFor();
            PdfEngine.Assemble(pages, output);
            job.Outputs.Add(output);
            return Task.CompletedTask;
        });
    }
}
