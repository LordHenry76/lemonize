using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageMagick;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;
using Path = System.IO.Path;

using Orientation = System.Windows.Controls.Orientation;

namespace Lemonize.UI;

/// <summary>
/// Turns photos of paper documents into one A4 PDF: for each photo the sheet's corners are proposed and can be
/// dragged into place, then every page is straightened and cleaned.
/// </summary>
public sealed class ScanWindow : ThemedWindow
{
    private const int DisplayMax = 2000;
    private const double HandleReach = 16;

    private sealed class Page(string file)
    {
        public string File { get; } = file;
        public int Rotation { get; set; }
        /// <summary>In display pixels; null until the page has been shown.</summary>
        public Point[]? Corners { get; set; }
        public MagickImage? Display { get; set; }
        public BitmapSource? Bitmap { get; set; }
        /// <summary>Photo pixels per display pixel.</summary>
        public double Scale { get; set; } = 1;
    }

    private enum Grip { None, Corner, Side }

    private readonly List<Page> _pages;
    private int _index;

    private readonly Canvas _surface = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), Focusable = true };
    private readonly Image _image = new();
    private readonly MatrixTransform _transform = new();
    private readonly System.Windows.Shapes.Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Polygon _outline = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly List<Shape> _cornerHandles = [];
    private readonly List<Shape> _sideHandles = [];
    private readonly TextBlock _loading = new() { Text = "Loading…", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center };

    private readonly TextBlock _pageLabel = new();
    private readonly TextBlock _fileLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _previous = new() { Content = "Previous" };
    private readonly Button _next = new() { Content = "Next" };
    private readonly Button _remove = new() { Content = "Remove page" };
    private readonly ComboBox _look = new();
    private readonly Slider _whitening = new() { Minimum = 0, Maximum = 100, Value = 50 };
    private readonly Image _result = new() { Stretch = Stretch.Uniform };
    private readonly Button _save = new() { Content = "Save PDF", MinWidth = 110 };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _previewVersion;

    private double _zoom = 1;
    private Vector _pan;
    private bool _fitted = true;
    private Grip _grip;
    private int _gripIndex;
    private Point _gripLast;
    private bool? _lockHorizontal;
    private Point? _panFrom;

    public ScanWindow(IReadOnlyList<string> files)
    {
        _pages = [.. files.Order(StringComparer.CurrentCultureIgnoreCase).Select(f => new Page(f))];
        Title = _pages.Count == 1 ? Loc.F("Scan document · {0}", Path.GetFileName(_pages[0].File)) : Loc.F("Scan document · {0} photos", _pages.Count);
        Width = 1180;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdateResult();
        };
        Content = BuildLayout();
        Loaded += async (_, _) => await ShowPage(0);
        Closed += (_, _) =>
        {
            foreach (var page in _pages)
                lock (page) page.Display?.Dispose();
        };
    }

    private Page Current => _pages[_index];

    // ---------- Layout ----------

    private UIElement BuildLayout()
    {
        var side = new StackPanel { Width = 260, Margin = new Thickness(12) };
        _pageLabel.Style = (Style)FindResource("Heading");
        side.Children.Add(_pageLabel);
        _fileLabel.Style = (Style)FindResource("Dim");
        _fileLabel.Margin = new Thickness(0, 0, 0, 8);
        side.Children.Add(_fileLabel);

        var navigation = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        _next.Margin = new Thickness(6, 0, 0, 0);
        navigation.Children.Add(_previous);
        navigation.Children.Add(_next);
        _previous.Click += async (_, _) => await ShowPage(_index - 1);
        _next.Click += async (_, _) => await ShowPage(_index + 1);
        side.Children.Add(navigation);

        side.Children.Add(new TextBlock { Text = "Sheet", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        var transform = new WrapPanel();
        transform.Children.Add(SmallButton("", "Rotate left", () => Rotate(-90)));
        transform.Children.Add(SmallButton("", "Rotate right", () => Rotate(90), mirror: true));
        side.Children.Add(transform);
        var detect = new Button { Content = "Find the sheet again", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6) };
        detect.Click += (_, _) => SetCorners(Detect(Current));
        side.Children.Add(detect);
        var whole = new Button { Content = "Whole photo", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6) };
        whole.Click += (_, _) =>
        {
            if (Current.Display is { } d) SetCorners([new(0, 0), new(d.Width, 0), new(d.Width, d.Height), new(0, d.Height)]);
        };
        side.Children.Add(whole);
        _remove.HorizontalAlignment = HorizontalAlignment.Stretch;
        _remove.Click += async (_, _) => await RemovePage();
        side.Children.Add(_remove);

        side.Children.Add(new TextBlock { Text = "Look", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        _look.ItemsSource = new[] { Loc.T("Black and white"), Loc.T("Color"), Loc.T("Original") };
        _look.SelectedIndex = 0;
        _look.SelectionChanged += (_, _) =>
        {
            _whitening.IsEnabled = Look != ScanLook.Original;
            UpdateResult();
        };
        side.Children.Add(_look);
        var whiteningLabel = new TextBlock { Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 10, 0, 0) };
        void UpdateWhiteningLabel() => whiteningLabel.Text = $"{Loc.T("Whitening")} · {_whitening.Value:0}";
        _whitening.ValueChanged += (_, _) =>
        {
            UpdateWhiteningLabel();
            _previewTimer.Stop();
            _previewTimer.Start();
        };
        UpdateWhiteningLabel();
        side.Children.Add(whiteningLabel);
        side.Children.Add(_whitening);

        side.Children.Add(new TextBlock { Text = "Result", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 14, 0, 6) });
        side.Children.Add(new Border
        {
            Height = 250, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Child = _result,
        });

        _save.Style = (Style)FindResource("Primary");
        _save.Margin = new Thickness(0, 14, 0, 0);
        _save.Click += (_, _) => Save();
        side.Children.Add(_save);

        // Stage
        _image.RenderTransform = _transform;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _outline.Stroke = (Brush)FindResource("Accent");
        _surface.Children.Add(_image);
        _surface.Children.Add(_shade);
        _surface.Children.Add(_outline);
        for (var i = 0; i < 4; i++)
        {
            var side4 = new Rectangle { Width = 12, Height = 12, Fill = (Brush)FindResource("Accent"), Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false };
            _sideHandles.Add(side4);
            _surface.Children.Add(side4);
        }
        for (var i = 0; i < 4; i++)
        {
            var corner = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)), Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
            _cornerHandles.Add(corner);
            _surface.Children.Add(corner);
        }
        _surface.MouseDown += OnMouseDown;
        _surface.MouseMove += OnMouseMove;
        _surface.MouseUp += OnMouseUp;
        _surface.MouseWheel += OnMouseWheel;
        _surface.SizeChanged += (_, _) =>
        {
            if (_fitted) Fit();
            else Redraw();
        };

        var hint = new TextBlock
        {
            Text = "Drag the corners or the sides · Shift keeps a side straight · Wheel: zoom · Wheel button: move · Enter: next",
            Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        var stage = new DockPanel { Margin = new Thickness(12, 12, 0, 12) };
        DockPanel.SetDock(hint, Dock.Bottom);
        stage.Children.Add(hint);
        var stageGrid = new Grid();
        stageGrid.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _surface });
        _loading.VerticalAlignment = VerticalAlignment.Center;
        stageGrid.Children.Add(_loading);
        stage.Children.Add(stageGrid);

        var root = new DockPanel();
        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(sideScroll, Dock.Right);
        root.Children.Add(sideScroll);
        root.Children.Add(stage);
        return root;
    }

    private Button SmallButton(string glyph, string tip, Action action, bool mirror = false)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, RenderTransformOrigin = new Point(0.5, 0.5) };
        if (mirror) icon.RenderTransform = new ScaleTransform(-1, 1);
        var button = new Button { Content = icon, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 8, 10, 8) };
        button.Click += (_, _) => action();
        return button;
    }

    private ScanLook Look => (ScanLook)Math.Max(0, _look.SelectedIndex);

    // ---------- Pages ----------

    private async Task ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _index = index;
        var page = Current;
        _pageLabel.Text = Loc.F("Page {0} of {1}", index + 1, _pages.Count);
        _fileLabel.Text = Path.GetFileName(page.File);
        _fileLabel.ToolTip = page.File;
        _previous.IsEnabled = index > 0;
        _next.IsEnabled = index < _pages.Count - 1;
        _remove.IsEnabled = _pages.Count > 1;

        if (page.Display is null)
        {
            _loading.Visibility = Visibility.Visible;
            _image.Source = null;
            _result.Source = null;
            SetHandlesVisible(false);
            try
            {
                var (display, scale, corners) = await Task.Run(() =>
                {
                    using var photo = ScanEngine.LoadPhoto(page.File, page.Rotation);
                    var display = (MagickImage)photo.Clone();
                    if (display.Width > DisplayMax || display.Height > DisplayMax)
                        display.Resize(new MagickGeometry(DisplayMax, DisplayMax));
                    var corners = ScanEngine.DetectSheet(display) ?? ScanEngine.DefaultCorners(display.Width, display.Height);
                    return (display, photo.Width / (double)display.Width, corners);
                });
                page.Display = display;
                page.Scale = scale;
                page.Corners ??= corners;
                page.Bitmap = ImageEngine.ToBitmapSource(display);
            }
            catch (Exception ex)
            {
                _loading.Visibility = Visibility.Collapsed;
                Dialogs.Error(Loc.T("Can't open image"), $"{Path.GetFileName(page.File)}\n{ex.Message}");
                return;
            }
            if (page != Current) return;
        }
        _loading.Visibility = Visibility.Collapsed;
        _image.Source = page.Bitmap;
        _image.Width = page.Display!.Width;
        _image.Height = page.Display.Height;
        SetHandlesVisible(true);
        Fit();
        UpdateResult();
        _surface.Focus();
    }

    private Point[] Detect(Page page) =>
        page.Display is not { } d ? page.Corners ?? [] : ScanEngine.DetectSheet(d) ?? ScanEngine.DefaultCorners(d.Width, d.Height);

    private void SetCorners(Point[] corners)
    {
        if (corners.Length != 4) return;
        Current.Corners = corners;
        Redraw();
        UpdateResult();
    }

    private void Rotate(int degrees)
    {
        var page = Current;
        if (page.Display is not { } display || page.Corners is not { } corners) return;
        double w = display.Width, h = display.Height;
        lock (page)
        {
            display.Rotate(degrees);
            display.ResetPage();
        }
        page.Rotation = ((page.Rotation + degrees) % 360 + 360) % 360;
        page.Bitmap = ImageEngine.ToBitmapSource(display);
        // Clockwise, (x, y) lands on (h - y, x) and the old bottom-left becomes the top-left; the reverse for counter-clockwise.
        page.Corners = degrees > 0
            ? [.. new[] { corners[3], corners[0], corners[1], corners[2] }.Select(p => new Point(h - p.Y, p.X))]
            : [.. new[] { corners[1], corners[2], corners[3], corners[0] }.Select(p => new Point(p.Y, w - p.X))];
        _image.Source = page.Bitmap;
        _image.Width = display.Width;
        _image.Height = display.Height;
        Fit();
        UpdateResult();
    }

    private async Task RemovePage()
    {
        if (_pages.Count <= 1) return;
        lock (Current) Current.Display?.Dispose();
        _pages.RemoveAt(_index);
        Title = _pages.Count == 1 ? Loc.F("Scan document · {0}", Path.GetFileName(_pages[0].File)) : Loc.F("Scan document · {0} photos", _pages.Count);
        await ShowPage(Math.Min(_index, _pages.Count - 1));
    }

    // ---------- View ----------

    private double Margin0 => Current.Display is { } d ? Math.Max(d.Width, d.Height) * 0.06 : 0;

    private double FitZoom
    {
        get
        {
            if (Current.Display is not { } d || _surface.ActualWidth < 1) return 1;
            var m = Margin0;
            return Math.Min(_surface.ActualWidth / (d.Width + 2 * m), _surface.ActualHeight / (d.Height + 2 * m));
        }
    }

    private void Fit()
    {
        if (Current.Display is not { } d) return;
        _zoom = FitZoom;
        _pan = new Vector((_surface.ActualWidth - d.Width * _zoom) / 2, (_surface.ActualHeight - d.Height * _zoom) / 2);
        _fitted = true;
        Redraw();
    }

    private Point ToScreen(Point p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);

    private Point ToImage(Point s) => new((s.X - _pan.X) / _zoom, (s.Y - _pan.Y) / _zoom);

    private Point Clamp(Point p)
    {
        if (Current.Display is not { } d) return p;
        var m = Margin0;
        return new Point(Math.Clamp(p.X, -m, d.Width + m), Math.Clamp(p.Y, -m, d.Height + m));
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private void SetHandlesVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        _shade.Visibility = _outline.Visibility = v;
        foreach (var handle in _cornerHandles.Concat(_sideHandles)) handle.Visibility = v;
    }

    private void Redraw()
    {
        _transform.Matrix = new Matrix(_zoom, 0, 0, _zoom, _pan.X, _pan.Y);
        if (Current.Corners is not { } corners) return;
        var screen = corners.Select(ToScreen).ToArray();
        _outline.Points = new PointCollection(screen);
        var quad = new StreamGeometry();
        using (var ctx = quad.Open())
        {
            ctx.BeginFigure(screen[0], true, true);
            ctx.PolyLineTo(screen[1..], true, false);
        }
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(-10, -10, _surface.ActualWidth + 20, _surface.ActualHeight + 20)), quad);
        for (var i = 0; i < 4; i++)
        {
            Place(_cornerHandles[i], screen[i]);
            Place(_sideHandles[i], Mid(screen[i], screen[(i + 1) % 4]));
        }

        static void Place(FrameworkElement handle, Point at)
        {
            Canvas.SetLeft(handle, at.X - handle.Width / 2);
            Canvas.SetTop(handle, at.Y - handle.Height / 2);
        }
    }

    // ---------- Mouse ----------

    private (Grip Grip, int Index) HitTest(Point screen)
    {
        if (Current.Corners is not { } corners) return (Grip.None, -1);
        for (var i = 0; i < 4; i++)
            if ((ToScreen(corners[i]) - screen).Length <= HandleReach) return (Grip.Corner, i);
        for (var i = 0; i < 4; i++)
            if ((ToScreen(Mid(corners[i], corners[(i + 1) % 4])) - screen).Length <= HandleReach) return (Grip.Side, i);
        return (Grip.None, -1);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _surface.Focus();
        var at = e.GetPosition(_surface);
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panFrom = at;
            _surface.Cursor = Cursors.SizeAll;
            _surface.CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        (_grip, _gripIndex) = HitTest(at);
        if (_grip == Grip.None) return;
        _gripLast = ToImage(at);
        _lockHorizontal = null;
        _surface.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(_surface);
        if (_panFrom is { } from)
        {
            _pan += at - from;
            _panFrom = at;
            _fitted = false;
            Redraw();
            return;
        }
        if (_grip == Grip.None || Current.Corners is not { } corners)
        {
            _surface.Cursor = HitTest(at).Grip == Grip.None ? null : Cursors.Hand;
            return;
        }

        var p = ToImage(at);
        if (_grip == Grip.Corner)
        {
            corners[_gripIndex] = Clamp(p);
        }
        else
        {
            var delta = p - _gripLast;
            // Shift keeps the side moving along one axis, picked by the first clear movement.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (_lockHorizontal is null && delta.Length * _zoom > 3) _lockHorizontal = Math.Abs(delta.X) >= Math.Abs(delta.Y);
                if (_lockHorizontal == true) delta.Y = 0;
                else if (_lockHorizontal == false) delta.X = 0;
            }
            else
            {
                _lockHorizontal = null;
            }
            if (_lockHorizontal is null && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            var a = _gripIndex;
            var b = (_gripIndex + 1) % 4;
            corners[a] = Clamp(corners[a] + delta);
            corners[b] = Clamp(corners[b] + delta);
            _gripLast = p;
        }
        Redraw();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panFrom is not null && e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panFrom = null;
            _surface.Cursor = null;
            _surface.ReleaseMouseCapture();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _grip == Grip.None) return;
        _grip = Grip.None;
        _surface.ReleaseMouseCapture();
        UpdateResult();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var fit = FitZoom;
        var zoom = Math.Clamp(_zoom * Math.Pow(1.2, e.Delta / 120.0), fit, fit * 20);
        if (zoom <= fit * 1.001)
        {
            Fit();
            return;
        }
        // Keep the point under the cursor still.
        var at = e.GetPosition(_surface);
        var image = ToImage(at);
        _zoom = zoom;
        _pan = new Vector(at.X - image.X * zoom, at.Y - image.Y * zoom);
        _fitted = false;
        Redraw();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_index < _pages.Count - 1) _ = ShowPage(_index + 1);
            else Save();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---------- Result ----------

    private void UpdateResult()
    {
        if (Current.Display is not { } display || Current.Corners is not { } corners) return;
        var version = ++_previewVersion;
        var quad = corners.ToArray();
        var look = Look;
        var whitening = _whitening.Value / 100;
        var page = Current;
        Task.Run(() =>
        {
            // The preview straightens from the display copy, small enough to keep up with dragging.
            lock (page)
            {
                using var result = ScanEngine.Straighten(display, quad, 700);
                ScanEngine.Clean(result, look, whitening);
                return ImageEngine.ToBitmapSource(result);
            }
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && version == _previewVersion) _result.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Save()
    {
        if (_pages.Count == 0) return;
        var look = Look;
        var whitening = _whitening.Value / 100;
        var pages = _pages.Select(p => new ScanPage(p.File, p.Rotation,
            p.Corners?.Select(c => new Point(c.X * p.Scale, c.Y * p.Scale)).ToArray(), look, whitening)).ToList();
        var files = pages.Select(p => p.File).ToList();
        JobQueue.Enqueue(pages.Count == 1 ? Loc.T("Scan 1 photo → PDF") : Loc.F("Scan {0} photos → PDF", pages.Count), files[0], (job, _) =>
        {
            var output = files.Count == 1 ? OutputPaths.For(files[0], "pdf") : OutputPaths.ForGroup(files, Loc.T("Scan"), "pdf");
            ScanEngine.ToPdf(pages, output, job.Report);
            job.Outputs.Add(output);
            return Task.CompletedTask;
        });
        Close();
    }
}
