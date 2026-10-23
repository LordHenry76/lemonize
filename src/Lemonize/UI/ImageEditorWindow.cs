using System.Globalization;
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

public sealed class ImageEditorWindow : ThemedWindow
{
    private enum Tool { Crop, Pen, Rectangle, Arrow, Text, Redact, Pixelate }

    private const string PixelateTag = "pixelate";
    private const int PreviewMax = 1600;

    private readonly string _file;
    private MagickImage _base = null!;
    private readonly Stack<Action> _undo = new();

    private readonly Grid _stage = new();
    private readonly Image _preview = new() { Stretch = Stretch.Fill };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _cropRect = new() { Stroke = Brushes.Gold, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed };
    private readonly System.Windows.Shapes.Path _cropShade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private readonly Slider _brightness = new() { Minimum = -100, Maximum = 100 };
    private readonly Slider _contrast = new() { Minimum = -100, Maximum = 100 };
    private readonly Slider _saturation = new() { Minimum = 0, Maximum = 200, Value = 100 };
    private readonly TextBox _resizeW = new() { Width = 70 };
    private readonly TextBox _resizeH = new() { Width = 70 };
    private readonly ComboBox _format = new();
    private readonly TextBlock _info = new();
    private readonly DispatcherTimer _adjustTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private Tool _tool = Tool.Crop;
    private Color _color = Colors.Red;
    private Rect? _crop;
    private Point _start;
    private Shape? _drawing;
    private Polyline? _pen;
    private bool _syncingSize;

    public ImageEditorWindow(string file)
    {
        _file = file;
        Title = Loc.F("Edit · {0}", Path.GetFileName(file));
        Width = 1180;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _adjustTimer.Tick += (_, _) =>
        {
            _adjustTimer.Stop();
            UpdatePreview();
        };
        foreach (var slider in new[] { _brightness, _contrast, _saturation })
            slider.ValueChanged += (_, _) => _adjustTimer.Start();

        Content = BuildLayout();
        Loaded += (_, _) => LoadImage();
        Closed += (_, _) => _base?.Dispose();
    }

    // ---------- Layout ----------

    private UIElement BuildLayout()
    {
        var root = new DockPanel();

        // Tools
        var tools = new StackPanel { Width = 150, Margin = new Thickness(12) };
        tools.Children.Add(new TextBlock { Text = "Tools", Style = (Style)FindResource("Heading") });
        AddTool(tools, Tool.Crop, "", "Crop", true);
        AddTool(tools, Tool.Pen, "", "Pen");
        AddTool(tools, Tool.Rectangle, "", "Rectangle");
        AddTool(tools, Tool.Arrow, "", "Arrow");
        AddTool(tools, Tool.Text, "", "Text");
        AddTool(tools, Tool.Redact, "", "Redact");
        AddTool(tools, Tool.Pixelate, "", "Pixelate");

        tools.Children.Add(new TextBlock { Text = "Color", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 14, 0, 6) });
        var swatches = new WrapPanel();
        foreach (var color in new[] { Colors.Red, Color.FromRgb(0xFF, 0xD2, 0x3F), Color.FromRgb(0x2E, 0xCC, 0x71), Color.FromRgb(0x3D, 0x8B, 0xFF), Colors.White, Colors.Black })
        {
            var swatch = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color), BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _color = color;
                foreach (Border b in swatches.Children) b.BorderBrush = (Brush)FindResource("Border");
                swatch.BorderBrush = (Brush)FindResource("Text");
            };
            if (color == _color) swatch.BorderBrush = (Brush)FindResource("Text");
            swatches.Children.Add(swatch);
        }
        tools.Children.Add(swatches);

        var undo = new Button { Content = "Undo", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        undo.Click += (_, _) => Undo();
        tools.Children.Add(undo);
        DockPanel.SetDock(tools, Dock.Left);
        root.Children.Add(tools);

        // Adjustments
        var side = new StackPanel { Width = 240, Margin = new Thickness(12) };
        side.Children.Add(new TextBlock { Text = "Adjust", Style = (Style)FindResource("Heading") });
        AddSlider(side, "Brightness", _brightness);
        AddSlider(side, "Contrast", _contrast);
        AddSlider(side, "Saturation", _saturation);
        var resetAdjust = new Button { Content = "Reset adjustments", Margin = new Thickness(0, 6, 0, 0) };
        resetAdjust.Click += (_, _) =>
        {
            _brightness.Value = 0;
            _contrast.Value = 0;
            _saturation.Value = 100;
        };
        side.Children.Add(resetAdjust);

        side.Children.Add(new TextBlock { Text = "Transform", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        var transform = new WrapPanel();
        transform.Children.Add(SmallButton("", "Rotate left", () => Transform(i => i.Rotate(-90))));
        transform.Children.Add(SmallButton("", "Rotate right", () => Transform(i => i.Rotate(90)), mirror: true));
        transform.Children.Add(SmallButton("", "Flip horizontal", () => Transform(i => i.Flop())));
        transform.Children.Add(SmallButton("", "Flip vertical", () => Transform(i => i.Flip()), rotate: true));
        side.Children.Add(transform);

        side.Children.Add(new TextBlock { Text = "Resize (pixels)", Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 12, 0, 6) });
        var resize = new StackPanel { Orientation = Orientation.Horizontal };
        resize.Children.Add(_resizeW);
        resize.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        resize.Children.Add(_resizeH);
        var applyResize = new Button { Content = "Apply", Margin = new Thickness(8, 0, 0, 0) };
        applyResize.Click += (_, _) => Resize();
        resize.Children.Add(applyResize);
        side.Children.Add(resize);
        _resizeW.TextChanged += (_, _) => SyncAspect(fromWidth: true);
        _resizeH.TextChanged += (_, _) => SyncAspect(fromWidth: false);

        side.Children.Add(new TextBlock { Text = "Save as", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        var ext = FileKinds.Ext(_file);
        _format.ItemsSource = new[] { Loc.F("Same ({0})", ext.ToUpperInvariant()), "JPEG", "PNG", "WebP", "TIFF" };
        _format.SelectedIndex = 0;
        side.Children.Add(_format);
        var save = new Button { Content = "Save copy", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 10, 0, 0) };
        save.Click += (_, _) => Save();
        side.Children.Add(save);
        _info.Style = (Style)FindResource("Dim");
        _info.Margin = new Thickness(0, 10, 0, 0);
        side.Children.Add(_info);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        // Stage
        _stage.Children.Add(_preview);
        _stage.Children.Add(_canvas);
        _overlay.Children.Add(_cropShade);
        _overlay.Children.Add(_cropRect);
        _stage.Children.Add(_overlay);
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        var stageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 12, 0, 12), Padding = new Thickness(12),
            Child = new Viewbox { Child = _stage, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both },
        };
        root.Children.Add(stageHost);
        return root;
    }

    private void AddTool(Panel panel, Tool tool, string glyph, string label, bool isChecked = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, Foreground = null });
        var button = new RadioButton { Style = (Style)FindResource("ToolToggle"), Content = content, GroupName = "tools", IsChecked = isChecked };
        // Let the label follow the toggle's foreground (dark on the lemon highlight).
        foreach (var child in content.Children.OfType<TextBlock>())
            child.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        button.Checked += (_, _) => _tool = tool;
        panel.Children.Add(button);
    }

    private void AddSlider(Panel panel, string label, Slider slider)
    {
        var text = new TextBlock { Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 6, 0, 0) };
        void Update() => text.Text = $"{Loc.T(label)} · {slider.Value:0}";
        slider.ValueChanged += (_, _) => Update();
        Update();
        panel.Children.Add(text);
        panel.Children.Add(slider);
    }

    private Button SmallButton(string glyph, string tip, Action action, bool mirror = false, bool rotate = false)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, RenderTransformOrigin = new Point(0.5, 0.5) };
        if (mirror) icon.RenderTransform = new ScaleTransform(-1, 1);
        if (rotate) icon.RenderTransform = new RotateTransform(90);
        var button = new Button { Content = icon, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 8, 10, 8) };
        button.Click += (_, _) => action();
        return button;
    }

    // ---------- Image state ----------

    private void LoadImage()
    {
        try
        {
            _base = ImageEngine.Load(_file);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Can't open image"), ex.Message);
            Close();
            return;
        }
        ApplyBase();
    }

    private void ApplyBase()
    {
        _stage.Width = _canvas.Width = _overlay.Width = _base.Width;
        _stage.Height = _canvas.Height = _overlay.Height = _base.Height;
        _syncingSize = true;
        _resizeW.Text = _base.Width.ToString();
        _resizeH.Text = _base.Height.ToString();
        _syncingSize = false;
        _info.Text = $"{_base.Width} × {_base.Height} px";
        UpdatePreview();
        UpdateCropVisual();
    }

    private void UpdatePreview()
    {
        using var preview = (MagickImage)_base.Clone();
        if (preview.Width > PreviewMax || preview.Height > PreviewMax)
            preview.Resize(new MagickGeometry(PreviewMax, PreviewMax));
        ApplyAdjustments(preview);
        _preview.Source = ImageEngine.ToBitmapSource(preview);
    }

    private void ApplyAdjustments(MagickImage image)
    {
        if (Math.Abs(_brightness.Value) > 0.5 || Math.Abs(_contrast.Value) > 0.5)
            image.BrightnessContrast(new Percentage(_brightness.Value), new Percentage(_contrast.Value));
        if (Math.Abs(_saturation.Value - 100) > 0.5)
            image.Modulate(new Percentage(100), new Percentage(_saturation.Value), new Percentage(100));
    }

    private double StrokeWidth => Math.Max(2, Math.Min(_base.Width, _base.Height) / 220.0);

    // ---------- Drawing ----------

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
        var brush = new SolidColorBrush(_color);
        switch (_tool)
        {
            case Tool.Pen:
                _pen = new Polyline { Stroke = brush, StrokeThickness = StrokeWidth * 1.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _pen.Points.Add(_start);
                AddShape(_pen);
                break;
            case Tool.Rectangle:
                _drawing = new Rectangle { Stroke = brush, StrokeThickness = StrokeWidth * 1.5 };
                AddShape(_drawing);
                break;
            case Tool.Redact:
                _drawing = new Rectangle { Fill = Brushes.Black };
                AddShape(_drawing);
                break;
            case Tool.Pixelate:
                _drawing = new Rectangle
                {
                    Tag = PixelateTag, Stroke = Brushes.White, StrokeDashArray = [2, 2], StrokeThickness = StrokeWidth,
                    Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
                };
                AddShape(_drawing);
                break;
            case Tool.Arrow:
                _drawing = new System.Windows.Shapes.Path { Stroke = brush, Fill = brush, StrokeThickness = StrokeWidth * 1.5, StrokeLineJoin = PenLineJoin.Round };
                AddShape(_drawing);
                break;
            case Tool.Text:
                _canvas.ReleaseMouseCapture();
                PlaceText(_start);
                break;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_canvas.IsMouseCaptured) return;
        var p = e.GetPosition(_canvas);
        p = new Point(Math.Clamp(p.X, 0, _canvas.Width), Math.Clamp(p.Y, 0, _canvas.Height));
        var rect = new Rect(_start, p);
        switch (_tool)
        {
            case Tool.Crop:
                _crop = rect;
                UpdateCropVisual();
                break;
            case Tool.Pen:
                _pen?.Points.Add(p);
                break;
            case Tool.Rectangle or Tool.Redact or Tool.Pixelate when _drawing is not null:
                Canvas.SetLeft(_drawing, rect.X);
                Canvas.SetTop(_drawing, rect.Y);
                _drawing.Width = rect.Width;
                _drawing.Height = rect.Height;
                break;
            case Tool.Arrow when _drawing is System.Windows.Shapes.Path path:
                path.Data = ArrowGeometry(_start, p, StrokeWidth * 6);
                break;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        if (_tool == Tool.Crop && _crop is { } c && (c.Width < 4 || c.Height < 4))
        {
            _crop = null;
            UpdateCropVisual();
        }
        _drawing = null;
        _pen = null;
    }

    private void AddShape(UIElement shape)
    {
        _canvas.Children.Add(shape);
        _undo.Push(() => _canvas.Children.Remove(shape));
    }

    private static Geometry ArrowGeometry(Point from, Point to, double head)
    {
        var v = to - from;
        if (v.Length < 1) return Geometry.Empty;
        v.Normalize();
        var normal = new Vector(-v.Y, v.X);
        var baseOfHead = to - v * head;
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(from, baseOfHead));
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(baseOfHead + normal * head * 0.55, true, true);
            ctx.LineTo(baseOfHead - normal * head * 0.55, true, true);
        }
        g.Children.Add(tri);
        return g;
    }

    private void PlaceText(Point at)
    {
        var size = Math.Max(14, _base.Height / 22.0);
        var box = new TextBox
        {
            FontSize = size, MinWidth = size * 4, Foreground = new SolidColorBrush(_color), Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            BorderThickness = new Thickness(1), FontWeight = FontWeights.Bold, Padding = new Thickness(2),
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y - size * 0.7);
        _canvas.Children.Add(box);
        var committed = false;
        void Commit()
        {
            if (committed) return;
            committed = true;
            _canvas.Children.Remove(box);
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            var text = new TextBlock
            {
                Text = box.Text, FontSize = size, FontWeight = FontWeights.Bold, Foreground = box.Foreground,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = size / 6, ShadowDepth = 0, Opacity = 0.8 },
            };
            Canvas.SetLeft(text, at.X + 3);
            Canvas.SetTop(text, at.Y - size * 0.7 + 3);
            AddShape(text);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            if (e.Key == Key.Escape)
            {
                box.Text = "";
                Commit();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Focus());
    }

    private void UpdateCropVisual()
    {
        if (_crop is not { } c)
        {
            _cropRect.Visibility = _cropShade.Visibility = Visibility.Collapsed;
            return;
        }
        _cropRect.Visibility = _cropShade.Visibility = Visibility.Visible;
        _cropRect.StrokeThickness = StrokeWidth;
        Canvas.SetLeft(_cropRect, c.X);
        Canvas.SetTop(_cropRect, c.Y);
        _cropRect.Width = c.Width;
        _cropRect.Height = c.Height;
        _cropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, _base.Width, _base.Height)), new RectangleGeometry(c));
        _info.Text = Loc.F("{0} × {1} px · crop {2} × {3}", _base.Width, _base.Height, Math.Round(c.Width), Math.Round(c.Height));
    }

    // ---------- Baking edits ----------

    /// <summary>Renders the current edits into a full-resolution image.</summary>
    private MagickImage Render(bool applyAdjustments, bool applyCrop)
    {
        var result = (MagickImage)_base.Clone();
        if (applyAdjustments) ApplyAdjustments(result);

        foreach (var rect in _canvas.Children.OfType<Rectangle>().Where(r => PixelateTag.Equals(r.Tag)))
        {
            var r = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height);
            if (double.IsNaN(r.X) || r.Width < 2 || r.Height < 2) continue;
            var geometry = new MagickGeometry((int)r.X, (int)r.Y, (uint)r.Width, (uint)r.Height);
            using var region = (MagickImage)result.Clone();
            region.Crop(geometry);
            region.ResetPage();
            var block = Math.Max(6, (int)(Math.Min(_base.Width, _base.Height) / 60));
            region.Scale(new Percentage(100.0 / block));
            region.Scale(geometry.Width, geometry.Height);
            result.Composite(region, (int)r.X, (int)r.Y, CompositeOperator.Over);
        }

        var drawable = _canvas.Children.OfType<UIElement>().Where(c => !(c is Rectangle { Tag: PixelateTag })).ToList();
        if (drawable.Count > 0)
        {
            var hidden = _canvas.Children.OfType<Rectangle>().Where(r => PixelateTag.Equals(r.Tag)).ToList();
            foreach (var h in hidden) h.Visibility = Visibility.Hidden;
            var rtb = new RenderTargetBitmap((int)_base.Width, (int)_base.Height, 96, 96, PixelFormats.Pbgra32);
            var background = _canvas.Background;
            _canvas.Background = null;
            rtb.Render(_canvas);
            _canvas.Background = background;
            foreach (var h in hidden) h.Visibility = Visibility.Visible;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            using var overlay = new MagickImage(ms.ToArray());
            result.Composite(overlay, 0, 0, CompositeOperator.Over);
        }

        if (applyCrop && _crop is { } c && c.Width >= 2 && c.Height >= 2)
        {
            result.Crop(new MagickGeometry((int)c.X, (int)c.Y, (uint)c.Width, (uint)c.Height));
            result.ResetPage();
        }
        return result;
    }

    /// <summary>Folds annotations and crop into the base image so geometric changes affect everything.</summary>
    private void Transform(Action<MagickImage> change)
    {
        var previousBase = (MagickImage)_base.Clone();
        var previousChildren = _canvas.Children.Cast<UIElement>().ToList();
        var previousCrop = _crop;

        var baked = Render(applyAdjustments: false, applyCrop: true);
        change(baked);
        baked.ResetPage();
        _base.Dispose();
        _base = baked;
        _canvas.Children.Clear();
        _crop = null;
        ApplyBase();

        _undo.Push(() =>
        {
            _base.Dispose();
            _base = previousBase;
            _canvas.Children.Clear();
            foreach (var child in previousChildren) _canvas.Children.Add(child);
            _crop = previousCrop;
            ApplyBase();
        });
    }

    private void Resize()
    {
        if (!uint.TryParse(_resizeW.Text, out var w) || !uint.TryParse(_resizeH.Text, out var h) || w == 0 || h == 0 || w > 30000 || h > 30000)
        {
            Dialogs.Error(Loc.T("Resize"), Loc.T("Enter a valid width and height in pixels."));
            return;
        }
        Transform(i => i.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true }));
    }

    private void SyncAspect(bool fromWidth)
    {
        if (_syncingSize || _base is null) return;
        _syncingSize = true;
        var ratio = _base.Width / (double)_base.Height;
        if (fromWidth && double.TryParse(_resizeW.Text, out var w)) _resizeH.Text = Math.Max(1, Math.Round(w / ratio)).ToString(CultureInfo.InvariantCulture);
        if (!fromWidth && double.TryParse(_resizeH.Text, out var h)) _resizeW.Text = Math.Max(1, Math.Round(h * ratio)).ToString(CultureInfo.InvariantCulture);
        _syncingSize = false;
    }

    private void Undo()
    {
        if (_crop is not null)
        {
            _crop = null;
            UpdateCropVisual();
            return;
        }
        if (_undo.Count > 0) _undo.Pop()();
    }

    private void Save()
    {
        var ext = _format.SelectedIndex switch
        {
            1 => "jpg",
            2 => "png",
            3 => "webp",
            4 => "tiff",
            _ => FileKinds.Ext(_file) switch
            {
                "svg" or "ico" or "psd" or "tga" => "png",
                "heic" or "heif" => "jpg", // HEIC is read-only
                var same => same,
            },
        };
        MagickImage rendered;
        try
        {
            rendered = Render(applyAdjustments: true, applyCrop: true);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Couldn't render the image"), ex.Message);
            return;
        }
        var file = _file;
        JobQueue.Enqueue(Loc.F("Edit {0}", Path.GetFileName(file)), file, (job, _) =>
        {
            using (rendered)
            {
                var output = OutputPaths.For(file, ext, Loc.T("(edited)"));
                ImageEngine.Save(rendered, output, ext, Settings.Current.JpegQuality);
                job.Outputs.Add(output);
            }
            return Task.CompletedTask;
        });
        Close();
    }
}
