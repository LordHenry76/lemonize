using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;
using Path = System.IO.Path;

namespace Lemonize.UI;

public sealed class VideoEditorWindow : ThemedWindow
{
    private readonly string _file;
    private MediaInfo? _info;
    private double _duration;
    private double _start;
    private double _end;
    private readonly List<double> _splits = [];
    private CropRect? _crop;

    private readonly Grid _stage = new();
    private readonly Image _frame = new() { Stretch = Stretch.Fill };
    private readonly Canvas _cropCanvas = new() { Background = Brushes.Transparent };
    private readonly Rectangle _cropRect = new() { Stroke = Brushes.Gold, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed };
    private readonly Slider _position = new() { Minimum = 0, SmallChange = 0.04, LargeChange = 1 };
    private readonly Canvas _timeline = new() { Height = 26, Background = Brushes.Transparent, ClipToBounds = true };
    private readonly TextBlock _time = new() { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new();
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "0.25×", "0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×", "4×" }, SelectedIndex = 3, Width = 90 };
    private readonly CheckBox _mute = new() { Content = "Remove audio" };
    private readonly CheckBox _cropMode = new() { Content = "Draw crop area on the video" };
    private readonly DispatcherTimer _scrub = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private CancellationTokenSource? _frameCts;
    private Point _cropStart;
    private int _frameW;
    private int _frameH;

    private static readonly double[] Speeds = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4];

    public VideoEditorWindow(string file)
    {
        _file = file;
        Title = Loc.F("Edit video · {0}", Path.GetFileName(file));
        Width = 1100;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _scrub.Tick += async (_, _) =>
        {
            _scrub.Stop();
            await ShowFrameAsync(_position.Value);
        };
        _position.ValueChanged += (_, _) =>
        {
            _time.Text = $"{Fmt(_position.Value)} / {Fmt(_duration)}";
            DrawTimeline();
            _scrub.Stop();
            _scrub.Start();
        };
        _timeline.SizeChanged += (_, _) => DrawTimeline();
        _cropCanvas.MouseLeftButtonDown += OnCropDown;
        _cropCanvas.MouseMove += OnCropMove;
        _cropCanvas.MouseLeftButtonUp += (_, _) => _cropCanvas.ReleaseMouseCapture();
        PreviewKeyDown += OnKey;

        Content = BuildLayout();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        // Right panel: options and export.
        var side = new StackPanel { Width = 250, Margin = new Thickness(16, 0, 0, 0) };
        side.Children.Add(new TextBlock { Text = "Range", Style = (Style)FindResource("Heading") });
        var range = new WrapPanel();
        range.Children.Add(Btn("Set start  [I]", () => SetStart()));
        range.Children.Add(Btn("Set end  [O]", () => SetEnd()));
        range.Children.Add(Btn("Split here  [S]", AddSplit));
        range.Children.Add(Btn("Clear splits", () =>
        {
            _splits.Clear();
            DrawTimeline();
        }));
        side.Children.Add(range);
        _summary.Style = (Style)FindResource("Dim");
        _summary.Margin = new Thickness(0, 4, 0, 0);
        side.Children.Add(_summary);

        side.Children.Add(new TextBlock { Text = "Crop", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        side.Children.Add(_cropMode);
        side.Children.Add(Btn("Reset crop", () =>
        {
            _crop = null;
            _cropRect.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }));

        side.Children.Add(new TextBlock { Text = "Speed & audio", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        side.Children.Add(_speed);
        side.Children.Add(_mute);
        _speed.SelectionChanged += (_, _) => UpdateSummary();

        side.Children.Add(new TextBlock { Text = "Frame", Style = (Style)FindResource("Heading"), Margin = new Thickness(0, 18, 0, 8) });
        side.Children.Add(Btn("Save frame as PNG", Snapshot));

        var export = new Button { Content = "Export", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 24, 0, 0), Padding = new Thickness(14, 9, 14, 9) };
        export.Click += (_, _) => Export();
        side.Children.Add(export);
        side.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 8, 0, 0),
            Text = "Splits export each part as its own file. Arrow keys step through the video; Shift+arrows jump 1 s.",
        });
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        // Transport.
        var transport = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var steps = new StackPanel { Orientation = Orientation.Horizontal };
        steps.Children.Add(IconBtn("", "Previous frame", () => Step(-1)));
        steps.Children.Add(IconBtn("", "Next frame", () => Step(1)));
        steps.Children.Add(IconBtn("", "Play in default player", () => Commands.Open(_file)));
        DockPanel.SetDock(steps, Dock.Left);
        DockPanel.SetDock(_time, Dock.Right);
        transport.Children.Add(steps);
        transport.Children.Add(_time);
        var timelineStack = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        timelineStack.Children.Add(_position);
        timelineStack.Children.Add(_timeline);
        transport.Children.Add(timelineStack);
        DockPanel.SetDock(transport, Dock.Bottom);
        root.Children.Add(transport);

        // Stage.
        _stage.Children.Add(_frame);
        _cropCanvas.Children.Add(_cropRect);
        _stage.Children.Add(_cropCanvas);
        root.Children.Add(new Border
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
            Child = new Viewbox { Child = _stage },
        });
        return root;
    }

    private Button Btn(string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        b.Click += (_, _) => action();
        return b;
    }

    private Button IconBtn(string glyph, string tip, Action action)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton") };
        b.Click += (_, _) => action();
        return b;
    }

    private async Task LoadAsync()
    {
        try
        {
            _info = await Ffmpeg.ProbeAsync(_file);
            if (!_info.HasVideo) throw new InvalidOperationException(Loc.T("This file has no video track."));
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Can't open video"), ex.Message);
            Close();
            return;
        }
        _duration = Math.Max(0.1, _info.Duration);
        _start = 0;
        _end = _duration;
        _position.Maximum = _duration;
        _frameW = _info.Width;
        _frameH = _info.Height;
        _stage.Width = _cropCanvas.Width = _frameW;
        _stage.Height = _cropCanvas.Height = _frameH;
        _time.Text = $"{Fmt(0)} / {Fmt(_duration)}";
        UpdateSummary();
        await ShowFrameAsync(0);
    }

    private async Task ShowFrameAsync(double seconds)
    {
        _frameCts?.Cancel();
        var cts = _frameCts = new CancellationTokenSource();
        try
        {
            var png = await Ffmpeg.FrameAsync(_file, Math.Min(seconds, _duration - 0.05), 960, cts.Token);
            if (cts.IsCancellationRequested) return;
            var bitmap = ImageEngine.BitmapFromBytes(png);
            // Phone videos carry a rotation flag; the decoded frame shows the real orientation.
            if (bitmap.PixelWidth > bitmap.PixelHeight != _frameW > _frameH && _info is not null)
            {
                (_frameW, _frameH) = (_frameH, _frameW);
                _stage.Width = _cropCanvas.Width = _frameW;
                _stage.Height = _cropCanvas.Height = _frameH;
            }
            _frame.Source = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer position.
        }
        catch (Exception ex)
        {
            _summary.Text = ex.Message;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.I: SetStart(); break;
            case Key.O: SetEnd(); break;
            case Key.S: AddSplit(); break;
            case Key.Left: Step(Keyboard.Modifiers == ModifierKeys.Shift ? -25 : -1); break;
            case Key.Right: Step(Keyboard.Modifiers == ModifierKeys.Shift ? 25 : 1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Step(int frames)
    {
        var fps = _info?.FrameRate > 0 ? _info.FrameRate : 25;
        _position.Value = Math.Clamp(_position.Value + frames / fps, 0, _duration);
    }

    private void SetStart()
    {
        _start = Math.Min(_position.Value, _end - 0.1);
        UpdateSummary();
    }

    private void SetEnd()
    {
        _end = Math.Max(_position.Value, _start + 0.1);
        UpdateSummary();
    }

    private void AddSplit()
    {
        var t = _position.Value;
        if (t <= _start + 0.1 || t >= _end - 0.1 || _splits.Any(s => Math.Abs(s - t) < 0.1)) return;
        _splits.Add(t);
        UpdateSummary();
    }

    private double Speed => Speeds[Math.Max(0, _speed.SelectedIndex)];

    private void UpdateSummary()
    {
        var parts = _splits.Count(s => s > _start && s < _end) + 1;
        var length = (_end - _start) / Speed;
        _summary.Text = Loc.F("{0} → {1}  ·  output {2}", Fmt(_start), Fmt(_end), Fmt(length)) +
                        (parts > 1 ? Loc.F("  ·  {0} parts", parts) : "") +
                        (_crop is { } c ? Loc.F("\ncrop {0} × {1}", c.Width, c.Height) : "");
        DrawTimeline();
    }

    private void DrawTimeline()
    {
        _timeline.Children.Clear();
        var w = _timeline.ActualWidth;
        if (w <= 0 || _duration <= 0) return;
        double X(double t) => t / _duration * w;
        _timeline.Children.Add(Rect(0, 10, w, 6, (Brush)FindResource("Border")));
        _timeline.Children.Add(Rect(X(_start), 8, Math.Max(2, X(_end) - X(_start)), 10, (Brush)FindResource("Accent")));
        foreach (var s in _splits)
            _timeline.Children.Add(Rect(X(s) - 1, 2, 2, 22, (Brush)FindResource("Text")));
        _timeline.Children.Add(Rect(X(_position.Value) - 1, 0, 2, 26, Brushes.White));
    }

    private static Rectangle Rect(double x, double y, double w, double h, Brush fill)
    {
        var r = new Rectangle { Width = w, Height = h, Fill = fill, RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        if (_cropMode.IsChecked != true) return;
        _cropStart = e.GetPosition(_cropCanvas);
        _cropCanvas.CaptureMouse();
    }

    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (!_cropCanvas.IsMouseCaptured) return;
        var p = e.GetPosition(_cropCanvas);
        p = new Point(Math.Clamp(p.X, 0, _frameW), Math.Clamp(p.Y, 0, _frameH));
        var r = new Rect(_cropStart, p);
        if (r.Width < 8 || r.Height < 8) return;
        _cropRect.Visibility = Visibility.Visible;
        _cropRect.StrokeThickness = Math.Max(2, _frameW / 300.0);
        Canvas.SetLeft(_cropRect, r.X);
        Canvas.SetTop(_cropRect, r.Y);
        _cropRect.Width = r.Width;
        _cropRect.Height = r.Height;
        _crop = new CropRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        UpdateSummary();
    }

    private void Snapshot()
    {
        var t = _position.Value;
        var file = _file;
        JobQueue.Enqueue(Loc.F("Frame from {0}", Path.GetFileName(file)), file, async (job, token) =>
            job.Outputs.Add(await MediaTools.SnapshotAsync(file, t, token)));
    }

    private void Export()
    {
        if (_info is null) return;
        var edit = new VideoEdit(_start, _end, _crop, Speed, _mute.IsChecked == true, _splits.ToList());
        var file = _file;
        JobQueue.Enqueue(Loc.F("Edit {0}", Path.GetFileName(file)), file, async (job, token) =>
            job.Outputs.AddRange(await MediaTools.EditVideoAsync(file, edit, job.Report, token)));
        Close();
    }

    private static string Fmt(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"mm\:ss\.f");
}
