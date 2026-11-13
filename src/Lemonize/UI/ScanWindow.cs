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
/// dragged into place, then every page is straightened and cleaned. In card mode the (up to four) photos are the
/// fronts and backs of one or two ID cards, laid out in a column on one A4 page in the order of the strip below
/// the photo, where they can be dragged around. With several sheets the scanned pages go
/// through a review (<see cref="PdfOrganizerWindow"/>) before the PDF is saved.
/// </summary>
public sealed class ScanWindow : ThemedWindow
{
    private const int DisplayMax = 2000;
    private const double HandleReach = 16;
    private static readonly Thickness CompactPadding = new(8, 4, 8, 4);
    private static readonly GridLength Fill = new(1, GridUnitType.Star);
    private static readonly GridLength LabelWidth = new(100);
    private const double SlotWidth = 150;
    private const double SlotPitch = SlotWidth + 8;

    private sealed class Page(string file)
    {
        public string File { get; } = file;
        public int Rotation { get; set; }
        public bool AutoTone { get; set; }
        /// <summary>In display pixels; null until the page has been shown.</summary>
        public Point[]? Corners { get; set; }
        public MagickImage? Display { get; set; }
        public BitmapSource? Bitmap { get; set; }
        /// <summary>Photo pixels per display pixel.</summary>
        public double Scale { get; set; } = 1;
        /// <summary>The page's JPEG size at full resolution, for the settings described by <see cref="EstimateKey"/>.</summary>
        public long EstimateBytes { get; set; }
        public string? EstimateKey { get; set; }
        /// <summary>The straightened card, for the strip; card mode only.</summary>
        public BitmapSource? Thumb { get; set; }
        /// <summary>Loading the photo, started once; false when it can't be opened.</summary>
        public Task<bool>? Loading { get; set; }
    }

    private enum Grip { None, Corner, Side }

    private readonly List<Page> _pages;
    private readonly bool _cards;
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
    private readonly Button _swap = new() { Content = "Swap front and back" };
    private readonly CheckBox _autoTone = new() { Content = "Auto tone" };
    private readonly ComboBox _look = new();
    private readonly Slider _whitening = new() { Minimum = 0, Maximum = 100, Value = 50 };
    private readonly ComboBox _quality = new();
    private readonly TextBlock _estimate = new() { HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Estimated size of the PDF" };
    private readonly DispatcherTimer _estimateTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _estimating;
    private readonly Image _result = new() { Stretch = Stretch.Uniform };
    private readonly Button _save = new() { Content = "Save PDF", MinWidth = 110 };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _stripBox = new() { Margin = new Thickness(0, 10, 0, 0) };
    private int _dragFrom = -1;
    private Point _dragStart;
    private bool _dragging;
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
    private bool _busy;
    private bool _closed;

    public ScanWindow(IReadOnlyList<string> files, bool cards = false)
    {
        _cards = cards;
        _pages = [.. files.Order(StringComparer.CurrentCultureIgnoreCase).Take(cards ? ScanEngine.MaxCards : int.MaxValue).Select(f => new Page(f))];
        UpdateTitle();
        Width = 1180;
        Height = Math.Min(820, SystemParameters.WorkArea.Height);
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdateResult();
        };
        _estimateTimer.Tick += async (_, _) =>
        {
            _estimateTimer.Stop();
            await Estimate();
        };
        Content = BuildLayout();
        Loaded += async (_, _) =>
        {
            await ShowPage(0);
            // The strip shows every side, so the other photos are opened right away.
            if (_cards)
                foreach (var page in _pages.ToList())
                    await Load(page);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            foreach (var page in _pages)
                lock (page) page.Display?.Dispose();
        };
    }

    private Page Current => _pages[_index];

    /// <summary>Several sheets are reviewed before saving; everything else goes straight to the PDF's name.</summary>
    private bool NeedsReview => !_cards && _pages.Count > 1;

    private void UpdateSaveLabel() => _save.Content = Loc.T(NeedsReview ? "Order pages…" : "Save PDF");

    private void UpdateTitle() => Title = _cards ? Loc.T(_pages.Count > 2 ? "Scan ID cards" : "Scan ID card")
        : _pages.Count == 1 ? Loc.F("Scan document · {0}", Path.GetFileName(_pages[0].File)) : Loc.F("Scan document · {0} photos", _pages.Count);

    // ---------- Layout ----------

    private UIElement BuildLayout()
    {
        var side = new StackPanel();
        _pageLabel.Style = (Style)FindResource("Heading");
        side.Children.Add(_pageLabel);
        _fileLabel.Style = (Style)FindResource("Dim");
        _fileLabel.Margin = new Thickness(0, 0, 0, 8);
        side.Children.Add(_fileLabel);

        _previous.Padding = _next.Padding = CompactPadding;
        var navigation = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        _next.Margin = new Thickness(6, 0, 0, 0);
        navigation.Children.Add(_previous);
        navigation.Children.Add(_next);
        _previous.Click += async (_, _) => await ShowPage(_index - 1);
        _next.Click += async (_, _) => await ShowPage(_index + 1);
        side.Children.Add(navigation);

        side.Children.Add(new TextBlock { Text = _cards ? "Card" : "Sheet", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 12, 0, 6) });
        _remove.Content = _cards ? "Remove this side" : "Remove page";
        _remove.Padding = CompactPadding;
        _remove.Click += async (_, _) => await RemovePage();
        side.Children.Add(Row(
            (SmallButton("", "Rotate left", () => Rotate(-90)), GridLength.Auto),
            (SmallButton("", "Rotate right", () => Rotate(90), mirror: true), GridLength.Auto),
            (_remove, Fill)));
        var detect = new Button { Content = _cards ? "Find the card again" : "Find the sheet again", Padding = CompactPadding };
        detect.Click += (_, _) => SetCorners(Detect(Current));
        var whole = new Button { Content = "Whole photo", Padding = CompactPadding, Margin = new Thickness(6, 0, 0, 0) };
        whole.Click += (_, _) =>
        {
            if (Current.Display is { } d) SetCorners([new(0, 0), new(d.Width, 0), new(d.Width, d.Height), new(0, d.Height)]);
        };
        var find = Row((detect, Fill), (whole, GridLength.Auto));
        find.Margin = new Thickness(0, 6, 0, 0);
        side.Children.Add(find);
        if (_cards)
        {
            _swap.Padding = CompactPadding;
            _swap.Margin = new Thickness(0, 6, 0, 0);
            _swap.Click += async (_, _) =>
            {
                // The photo shown and the other side of its document trade places.
                var partner = _index ^ 1;
                if (partner >= _pages.Count) return;
                (_pages[_index], _pages[partner]) = (_pages[partner], _pages[_index]);
                await ShowPage(partner);
            };
            side.Children.Add(_swap);
        }

        _autoTone.Margin = new Thickness(0, 14, 0, 0);
        _autoTone.ToolTip = _cards
            ? "Fixes dark photos and color casts, like Photoshop's Auto Tone. Applies to this side only."
            : "Fixes dark photos and color casts, like Photoshop's Auto Tone. Applies to this page only.";
        _autoTone.Click += (_, _) =>
        {
            Current.AutoTone = _autoTone.IsChecked == true;
            UpdateResult();
        };
        side.Children.Add(_autoTone);

        var look = new StackPanel { Visibility = _cards ? Visibility.Collapsed : Visibility.Visible };
        side.Children.Add(look);
        _look.ItemsSource = new[] { Loc.T("Black and white"), Loc.T("Color"), Loc.T("Original") };
        _look.SelectedIndex = 0;
        _look.SelectionChanged += (_, _) =>
        {
            _whitening.IsEnabled = Look != ScanLook.Original;
            UpdateResult();
        };
        var lookRow = Row((new TextBlock { Text = "Look", Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center }, LabelWidth), (_look, Fill));
        lookRow.Margin = new Thickness(0, 10, 0, 0);
        look.Children.Add(lookRow);
        var whiteningLabel = new TextBlock { Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        void UpdateWhiteningLabel() => whiteningLabel.Text = $"{Loc.T("Whitening")} · {_whitening.Value:0}";
        _whitening.ValueChanged += (_, _) =>
        {
            UpdateWhiteningLabel();
            _previewTimer.Stop();
            _previewTimer.Start();
        };
        UpdateWhiteningLabel();
        _whitening.VerticalAlignment = VerticalAlignment.Center;
        var whiteningRow = Row((whiteningLabel, LabelWidth), (_whitening, Fill));
        whiteningRow.Margin = new Thickness(0, 8, 0, 0);
        look.Children.Add(whiteningRow);
        _quality.ItemsSource = new[] { Loc.T("High"), Loc.T("Normal"), Loc.T("Light") };
        _quality.SelectedIndex = (int)Settings.Current.ScanQuality;
        _quality.ToolTip = "High: 300 dpi, as detailed as it gets · Normal: 200 dpi, sharp text at about a third of the size · Light: 150 dpi, for e-mail";
        _quality.SelectionChanged += (_, _) =>
        {
            Settings.Current.ScanQuality = Quality;
            Settings.Current.Save();
            ScheduleEstimate();
        };
        var qualityRow = Row((new TextBlock { Text = "Quality", Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center }, LabelWidth), (_quality, Fill));
        qualityRow.Margin = new Thickness(0, 8, 0, 0);
        look.Children.Add(qualityRow);

        var resultLabel = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
        _estimate.Style = (Style)FindResource("Dim");
        DockPanel.SetDock(_estimate, Dock.Right);
        resultLabel.Children.Add(_estimate);
        resultLabel.Children.Add(new TextBlock { Text = "Result", Style = (Style)FindResource("Dim") });
        var resultBox = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Child = _result,
        };

        _save.Style = (Style)FindResource("Primary");
        _save.Margin = new Thickness(0, 12, 0, 0);
        _save.Click += (_, _) => Save();

        // The controls keep their height, Save stays at the bottom and the preview takes whatever is left.
        var column = new DockPanel { Width = 260, Margin = new Thickness(12) };
        DockPanel.SetDock(side, Dock.Top);
        DockPanel.SetDock(resultLabel, Dock.Top);
        DockPanel.SetDock(_save, Dock.Bottom);
        column.Children.Add(side);
        column.Children.Add(resultLabel);
        column.Children.Add(_save);
        column.Children.Add(resultBox);

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
        if (_cards)
        {
            _stripBox.Children.Add(new TextBlock { Text = "Order in the PDF, top to bottom · drag to move", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 0, 0, 6) });
            _stripBox.Children.Add(_strip);
            _strip.MouseLeftButtonDown += OnStripDown;
            _strip.MouseMove += OnStripMove;
            _strip.MouseLeftButtonUp += OnStripUp;
            _strip.LostMouseCapture += (_, _) => EndStripDrag();
            DockPanel.SetDock(_stripBox, Dock.Bottom);
            stage.Children.Add(_stripBox);
            BuildStrip();
        }
        var stageGrid = new Grid();
        stageGrid.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _surface });
        _loading.VerticalAlignment = VerticalAlignment.Center;
        stageGrid.Children.Add(_loading);
        stage.Children.Add(stageGrid);

        var root = new DockPanel();
        DockPanel.SetDock(column, Dock.Right);
        root.Children.Add(column);
        root.Children.Add(stage);
        return root;
    }

    private static Grid Row(params (UIElement Element, GridLength Width)[] cells)
    {
        var row = new Grid();
        foreach (var (element, width) in cells)
        {
            Grid.SetColumn(element, row.ColumnDefinitions.Count);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            row.Children.Add(element);
        }
        return row;
    }

    private Button SmallButton(string glyph, string tip, Action action, bool mirror = false)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, RenderTransformOrigin = new Point(0.5, 0.5) };
        if (mirror) icon.RenderTransform = new ScaleTransform(-1, 1);
        var button = new Button { Content = icon, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), Padding = CompactPadding };
        button.Click += (_, _) => action();
        return button;
    }

    private ScanLook Look => (ScanLook)Math.Max(0, _look.SelectedIndex);

    private ScanQuality Quality => (ScanQuality)Math.Max(0, _quality.SelectedIndex);

    // ---------- Pages ----------

    private async Task ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _index = index;
        var page = Current;
        UpdatePageInfo();
        _autoTone.IsChecked = page.AutoTone;
        UpdateSaveLabel();

        if (page.Display is null)
        {
            _loading.Visibility = Visibility.Visible;
            _image.Source = null;
            _result.Source = null;
            SetHandlesVisible(false);
            if (!await Load(page))
            {
                if (page == Current) _loading.Visibility = Visibility.Collapsed;
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

    /// <summary>What the side column says about the page shown, whose place can change while dragging in the strip.</summary>
    private void UpdatePageInfo()
    {
        var page = Current;
        _pageLabel.Text = _cards ? SideName(_index) : Loc.F("Page {0} of {1}", _index + 1, _pages.Count);
        _fileLabel.Text = Path.GetFileName(page.File);
        _fileLabel.ToolTip = page.File;
        _previous.IsEnabled = _index > 0;
        _next.IsEnabled = _index < _pages.Count - 1;
        _remove.IsEnabled = _pages.Count > 1;
        _swap.IsEnabled = (_index ^ 1) < _pages.Count;
        RefreshStrip();
    }

    /// <summary>Front or back; with more than two sides, which document as well.</summary>
    private string SideName(int index)
    {
        var side = Loc.T(index % 2 == 0 ? "Front" : "Back");
        return _pages.Count <= 2 ? side : Loc.F("Document {0} · {1}", index / 2 + 1, side);
    }

    private Task<bool> Load(Page page) => page.Loading ??= LoadPhoto(page);

    private async Task<bool> LoadPhoto(Page page)
    {
        var cards = _cards;
        try
        {
            var (display, scale, corners, thumb) = await Task.Run(() =>
            {
                using var photo = ScanEngine.LoadPhoto(page.File, page.Rotation);
                var display = (MagickImage)photo.Clone();
                if (display.Width > DisplayMax || display.Height > DisplayMax)
                    display.Resize(new MagickGeometry(DisplayMax, DisplayMax));
                var corners = FindCorners(display);
                BitmapSource? thumb = null;
                if (cards)
                {
                    using var card = ScanEngine.StraightenCard(display, corners, 0.3);
                    thumb = ImageEngine.ToBitmapSource(card);
                }
                return (display, photo.Width / (double)display.Width, corners, thumb);
            });
            if (_closed || !_pages.Contains(page))
            {
                display.Dispose();
                return false;
            }
            page.Display = display;
            page.Scale = scale;
            page.Corners ??= corners;
            page.Thumb ??= thumb;
            page.Bitmap = ImageEngine.ToBitmapSource(display);
            RefreshStrip();
            return true;
        }
        catch (Exception ex)
        {
            if (page == Current) _loading.Visibility = Visibility.Collapsed;
            if (!_closed) Dialogs.Error(Loc.T("Can't open image"), $"{Path.GetFileName(page.File)}\n{ex.Message}");
            return false;
        }
    }

    // ---------- Strip (card mode) ----------

    /// <summary>One slot per side, left to right as they go top to bottom on the page.</summary>
    private void BuildStrip()
    {
        _strip.Children.Clear();
        foreach (var _ in _pages)
        {
            var image = new Image { Height = 86, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var label = new TextBlock { Style = (Style)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var content = new StackPanel();
            content.Children.Add(image);
            content.Children.Add(label);
            _strip.Children.Add(new Border
            {
                Width = SlotWidth, Margin = new Thickness(0, 0, SlotPitch - SlotWidth, 0), Padding = new Thickness(4),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), Child = content,
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), Cursor = Cursors.Hand,
            });
        }
        _stripBox.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RefreshStrip();
    }

    private void RefreshStrip()
    {
        if (!_cards || _strip.Children.Count != _pages.Count) return;
        for (var i = 0; i < _pages.Count; i++)
        {
            var slot = (Border)_strip.Children[i];
            var content = (StackPanel)slot.Child;
            ((Image)content.Children[0]).Source = _pages[i].Thumb;
            ((TextBlock)content.Children[1]).Text = SideName(i);
            slot.BorderBrush = i == _index ? (Brush)FindResource("Accent") : Brushes.Transparent;
            slot.Opacity = _dragging && i == _dragFrom ? 0.6 : 1;
        }
    }

    private int SlotAt(Point at)
    {
        var i = (int)Math.Floor(at.X / SlotPitch);
        return i >= 0 && i < _pages.Count ? i : -1;
    }

    private void OnStripDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        var at = e.GetPosition(_strip);
        _dragFrom = SlotAt(at);
        if (_dragFrom < 0) return;
        _dragStart = at;
        _dragging = false;
        _strip.CaptureMouse();
        e.Handled = true;
    }

    private void OnStripMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom < 0) return;
        var at = e.GetPosition(_strip);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
            _dragging = true;
            _strip.Cursor = Cursors.SizeWE;
        }
        // The side follows the mouse, the others make room.
        var to = Math.Clamp((int)Math.Floor(at.X / SlotPitch), 0, _pages.Count - 1);
        if (to != _dragFrom)
        {
            var current = Current;
            var moved = _pages[_dragFrom];
            _pages.RemoveAt(_dragFrom);
            _pages.Insert(to, moved);
            _dragFrom = to;
            _index = _pages.IndexOf(current);
            UpdatePageInfo();
        }
        RefreshStrip();
    }

    private void OnStripUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom < 0) return;
        var target = _dragFrom;
        EndStripDrag();
        _strip.ReleaseMouseCapture();
        // A click, or the end of a drag, shows that side.
        if (target != _index) _ = ShowPage(target);
    }

    private void EndStripDrag()
    {
        if (_dragFrom < 0 && !_dragging) return;
        _dragFrom = -1;
        _dragging = false;
        _strip.Cursor = null;
        RefreshStrip();
    }

    private Point[] Detect(Page page) => page.Display is { } d ? FindCorners(d) : page.Corners ?? [];

    private Point[] FindCorners(MagickImage display) => _cards
        ? ScanEngine.DetectCard(display) ?? ScanEngine.DefaultCardCorners(display.Width, display.Height)
        : ScanEngine.DetectSheet(display) ?? ScanEngine.DefaultCorners(display.Width, display.Height);

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
        UpdateTitle();
        if (_cards) BuildStrip();
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
            if (_busy) return;
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
        ScheduleEstimate();
        var version = ++_previewVersion;
        var quad = corners.ToArray();
        var look = Look;
        var whitening = _whitening.Value / 100;
        var page = Current;
        var cards = _cards;
        var autoTone = page.AutoTone;
        Task.Run(() =>
        {
            // The preview straightens from the display copy, small enough to keep up with dragging.
            lock (page)
            {
                using var result = cards ? ScanEngine.StraightenCard(display, quad, 0.6) : ScanEngine.Straighten(display, quad, 700);
                if (autoTone) ScanEngine.AutoTone(result);
                if (!cards) ScanEngine.Clean(result, look, whitening);
                return ImageEngine.ToBitmapSource(result);
            }
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || version != _previewVersion) return;
            _result.Source = t.Result;
            if (!cards) return;
            page.Thumb = t.Result;
            RefreshStrip();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------- Size estimate ----------

    /// <summary>Everything the page's size depends on; an estimate is still good while this stays the same.</summary>
    private string? KeyFor(Page page) => page.Corners is { } c
        ? $"{page.Rotation}|{string.Join(";", c.Select(p => $"{p.X:0.#},{p.Y:0.#}"))}|{Look}|{_whitening.Value:0}|{page.AutoTone}|{Quality}"
        : null;

    private void ScheduleEstimate()
    {
        if (_cards) return;
        UpdateEstimateLabel();
        _estimateTimer.Stop();
        _estimateTimer.Start();
    }

    /// <summary>
    /// Renders the current page from the full photo, exactly as saving would, and keeps its size. One at a time:
    /// whatever changes meanwhile is picked up by the check at the end.
    /// </summary>
    private async Task Estimate()
    {
        if (_estimating || _closed) return;
        var page = Current;
        var key = KeyFor(page);
        if (key is null || key == page.EstimateKey) return;
        var corners = page.Corners!.Select(c => new Point(c.X * page.Scale, c.Y * page.Scale)).ToArray();
        var (look, whitening, autoTone, quality, rotation) = (Look, _whitening.Value / 100, page.AutoTone, Quality, page.Rotation);
        _estimating = true;
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var photo = ScanEngine.LoadPhoto(page.File, rotation);
                return ScanEngine.RenderPage(photo, corners, look, whitening, autoTone, quality).LongLength;
            });
            page.EstimateBytes = bytes;
            page.EstimateKey = key;
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            // Only an estimate: the label keeps waiting and saving will report the real problem.
            return;
        }
        finally
        {
            _estimating = false;
        }
        if (_closed) return;
        UpdateEstimateLabel();
        await Estimate();
    }

    /// <summary>The average of the pages estimated with the current settings, times the number of pages.</summary>
    private void UpdateEstimateLabel()
    {
        var fresh = _pages.Where(p => p.EstimateKey is not null && p.EstimateKey == KeyFor(p)).Select(p => p.EstimateBytes).ToList();
        _estimate.Text = fresh.Count == 0 ? "PDF ≈ …" : $"PDF ≈ {OutputPaths.FormatSize((long)(fresh.Average() * _pages.Count))}";
    }

    private List<ScanPage> ScanPages()
    {
        var look = Look;
        var whitening = _whitening.Value / 100;
        return _pages.Select(p => new ScanPage(p.File, p.Rotation,
            p.Corners?.Select(c => new Point(c.X * p.Scale, c.Y * p.Scale)).ToArray(), look, whitening, p.AutoTone)).ToList();
    }

    private void Save()
    {
        if (_pages.Count == 0 || _busy) return;
        if (NeedsReview)
        {
            _ = ReviewThenSave();
            return;
        }
        var pages = ScanPages();
        var quality = Quality;
        var files = pages.Select(p => p.File).ToList();
        var output = Dialogs.AskPdfOutput(OutputPaths.DirectoryFor(files[0]), _cards && files.Count > 2 ? Loc.T("Documents")
            : files.Count == 1 || _cards ? OutputPaths.BaseName(files[0]) : Loc.T("Scan"));
        if (output is null) return;
        if (_cards)
        {
            JobQueue.Enqueue(Loc.T(files.Count > 2 ? "ID cards → PDF" : "ID card → PDF"), files[0], (job, _) =>
            {
                ScanEngine.CardsToPdf(pages, output, job.Report);
                job.Outputs.Add(output);
                return Task.CompletedTask;
            });
            Close();
            return;
        }
        JobQueue.Enqueue(Loc.T("Scan 1 photo → PDF"), files[0], (job, _) =>
        {
            ScanEngine.ToPdf(pages, output, job.Report, quality);
            job.Outputs.Add(output);
            return Task.CompletedTask;
        });
        Close();
    }

    /// <summary>
    /// Scans every sheet into a temporary PDF and opens it for reordering, rotating and deleting pages. Saving there
    /// asks for the name and writes the final PDF, reusing the scanned pages as they are; closing it comes back here
    /// with the corners and settings untouched.
    /// </summary>
    private async Task ReviewThenSave()
    {
        _busy = true;
        var content = (UIElement)Content;
        content.IsEnabled = false;
        var scans = ScanPages();
        var quality = Quality;
        var temp = AppPaths.NewTempFile("pdf");
        ShowPreparing(0);
        try
        {
            await Task.Run(() => ScanEngine.ToPdf(scans, temp, value => Dispatcher.BeginInvoke(() => ShowPreparing(value)), quality));
        }
        catch (Exception ex)
        {
            DeleteQuietly(temp);
            if (_closed) return;
            Restore();
            Dialogs.Error(Loc.T("Can't prepare the pages"), ex.Message);
            return;
        }
        if (_closed)
        {
            DeleteQuietly(temp);
            return;
        }

        var files = scans.Select(s => s.File).ToList();
        var saved = false;
        var organizer = new PdfOrganizerWindow([temp], pages =>
        {
            var output = Dialogs.AskPdfOutput(OutputPaths.DirectoryFor(files[0]), Loc.T("Scan"));
            if (output is null) return false;
            saved = true;
            JobQueue.Enqueue(pages.Count == 1 ? Loc.T("Scan 1 photo → PDF") : Loc.F("Scan {0} photos → PDF", pages.Count), files[0], (job, _) =>
            {
                try
                {
                    PdfEngine.Assemble(pages, output);
                    job.Outputs.Add(output);
                }
                finally
                {
                    DeleteQuietly(temp);
                }
                return Task.CompletedTask;
            });
            return true;
        }, files.Select(OutputPaths.BaseName).ToList());
        organizer.Closed += (_, _) =>
        {
            if (saved)
            {
                Close();
                return;
            }
            DeleteQuietly(temp);
            Restore();
            if (!App.IsExiting && !_closed) ShowOnTop();
        };
        organizer.ShowOnTop();
        Hide();

        void Restore()
        {
            _busy = false;
            content.IsEnabled = true;
            UpdateSaveLabel();
        }
    }

    private void ShowPreparing(double progress)
    {
        if (_busy) _save.Content = Loc.F("Preparing pages… {0}%", (int)(progress * 100));
    }

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a temporary file: not worth bothering the user.
        }
    }
}
