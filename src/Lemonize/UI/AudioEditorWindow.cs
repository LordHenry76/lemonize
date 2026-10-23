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

public sealed class AudioEditorWindow : ThemedWindow
{
    private readonly string _file;
    private double _duration;
    private double _start;
    private double _end;
    private (double Start, double End)? _selection;
    private readonly List<(double Start, double End)> _bleeps = [];

    private readonly Grid _wave = new() { Height = 220, ClipToBounds = true, Background = Brushes.Transparent, Cursor = Cursors.IBeam };
    private readonly Image _waveImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly TextBlock _status = new();
    private readonly CheckBox _normalize = new() { Content = "Normalize loudness (−16 LUFS)" };
    private readonly ComboBox _channels = new() { ItemsSource = new[] { Loc.T("Keep channels"), Loc.T("Mono"), Loc.T("Stereo"), Loc.T("Swap left / right") }, SelectedIndex = 0 };
    private readonly Slider _fadeIn = new() { Minimum = 0, Maximum = 10, TickFrequency = 0.5, IsSnapToTickEnabled = true };
    private readonly Slider _fadeOut = new() { Minimum = 0, Maximum = 10, TickFrequency = 0.5, IsSnapToTickEnabled = true };
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double? _stopAt;
    private double _dragFrom;

    public AudioEditorWindow(string file)
    {
        _file = file;
        Title = Loc.F("Edit audio · {0}", Path.GetFileName(file));
        Width = 1000;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildLayout();

        _wave.MouseLeftButtonDown += (_, e) =>
        {
            _dragFrom = TimeAt(e.GetPosition(_wave).X);
            _selection = null;
            _wave.CaptureMouse();
            Seek(_dragFrom);
            Draw();
        };
        _wave.MouseMove += (_, e) =>
        {
            if (!_wave.IsMouseCaptured) return;
            var t = TimeAt(e.GetPosition(_wave).X);
            if (Math.Abs(t - _dragFrom) > 0.02) _selection = (Math.Min(t, _dragFrom), Math.Max(t, _dragFrom));
            Draw();
        };
        _wave.MouseLeftButtonUp += (_, _) => _wave.ReleaseMouseCapture();
        _wave.SizeChanged += (_, _) => Draw();
        _playTimer.Tick += (_, _) =>
        {
            if (_stopAt is { } stop && _player.Position.TotalSeconds >= stop) StopPlayback();
            Draw();
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _player.Stop();
            _player.Close();
        };
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        var options = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        options.Children.Add(Group("Selection",
            Btn("Keep only selection", () => TrimTo()),
            Btn("Bleep selection", AddBleep),
            Btn("Clear bleeps", () =>
            {
                _bleeps.Clear();
                Draw();
            }),
            Btn("Reset range", () =>
            {
                _start = 0;
                _end = _duration;
                Draw();
            })));
        options.Children.Add(Group("Sound", _normalize, _channels));
        options.Children.Add(Group("Fades", Labeled("Fade in", _fadeIn), Labeled("Fade out", _fadeOut)));
        DockPanel.SetDock(options, Dock.Bottom);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var export = new Button { Content = "Export", Style = (Style)FindResource("Primary"), MinWidth = 110 };
        export.Click += (_, _) => Export();
        DockPanel.SetDock(export, Dock.Right);
        footer.Children.Add(export);
        footer.Children.Add(_status);
        _status.Style = (Style)FindResource("Dim");
        _status.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(options);

        var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        transport.Children.Add(Btn("▶ Play", () => Play(null)));
        transport.Children.Add(Btn("▶ Play selection", () =>
        {
            if (_selection is { } s) Play(s);
        }));
        transport.Children.Add(Btn("■ Stop", StopPlayback));
        DockPanel.SetDock(transport, Dock.Bottom);
        root.Children.Add(transport);

        _wave.Children.Add(_waveImage);
        _wave.Children.Add(_marks);
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Child = _wave, VerticalAlignment = VerticalAlignment.Top,
        });
        return root;
    }

    private Border Group(string title, params UIElement[] children)
    {
        var stack = new StackPanel { Width = 210 };
        stack.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading") });
        foreach (var child in children) stack.Children.Add(child);
        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 12, 0), Child = stack };
    }

    private StackPanel Labeled(string label, Slider slider)
    {
        var text = new TextBlock { Style = (Style)FindResource("Dim") };
        void Update() => text.Text = $"{Loc.T(label)} · {slider.Value:0.0} s";
        slider.ValueChanged += (_, _) => Update();
        Update();
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(slider);
        return panel;
    }

    private Button Btn(string text, Action action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => action();
        return b;
    }

    private async Task LoadAsync()
    {
        _status.Text = Loc.T("Reading audio…");
        try
        {
            var info = await Ffmpeg.ProbeAsync(_file);
            if (!info.HasAudio) throw new InvalidOperationException(Loc.T("This file has no audio."));
            _duration = Math.Max(0.1, info.Duration);
            _end = _duration;
            var png = await Ffmpeg.CaptureBytesAsync([
                "-v", "error", "-i", _file, "-filter_complex",
                "aformat=channel_layouts=mono,showwavespic=s=1800x300:colors=#FFD23F:scale=sqrt",
                "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1",
            ], CancellationToken.None);
            if (png.Length > 0) _waveImage.Source = ImageEngine.BitmapFromBytes(png);
            _player.Open(new Uri(_file));
            _status.Text = Loc.T("Drag on the waveform to select. Selection → trim or bleep.");
            Draw();
        }
        catch (Exception ex)
        {
            Dialogs.Error(Loc.T("Can't open audio"), ex.Message);
            Close();
        }
    }

    private double TimeAt(double x) => Math.Clamp(x / Math.Max(1, _wave.ActualWidth) * _duration, 0, _duration);

    private void Draw()
    {
        _marks.Children.Clear();
        var w = _wave.ActualWidth;
        var h = _wave.ActualHeight;
        if (w <= 0 || _duration <= 0) return;
        double X(double t) => t / _duration * w;
        var shade = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x12));
        Add(0, X(_start), shade);
        Add(X(_end), w - X(_end), shade);
        foreach (var (s, e) in _bleeps) Add(X(s), X(e) - X(s), new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x50, 0x50)));
        if (_selection is { } sel) Add(X(sel.Start), X(sel.End) - X(sel.Start), new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        var playhead = _player.Source is null ? _dragFrom : _player.Position.TotalSeconds;
        Add(X(playhead) - 1, 2, Brushes.White);

        var length = _end - _start;
        _status.Text = Loc.F("Range {0} → {1} ({2})", Fmt(_start), Fmt(_end), Fmt(length)) +
                       (_selection is { } s2 ? Loc.F("  ·  selection {0} → {1}", Fmt(s2.Start), Fmt(s2.End)) : "") +
                       (_bleeps.Count switch { 0 => "", 1 => Loc.T("  ·  1 bleep"), var n => Loc.F("  ·  {0} bleeps", n) });

        void Add(double x, double width, Brush fill)
        {
            var r = new Rectangle { Width = Math.Max(0, width), Height = h, Fill = fill };
            Canvas.SetLeft(r, x);
            _marks.Children.Add(r);
        }
    }

    private void TrimTo()
    {
        if (_selection is not { } s) return;
        (_start, _end) = s;
        _selection = null;
        Draw();
    }

    private void AddBleep()
    {
        if (_selection is not { } s) return;
        _bleeps.Add(s);
        _selection = null;
        Draw();
    }

    private void Seek(double t)
    {
        if (_player.Source is null) return;
        _player.Position = TimeSpan.FromSeconds(t);
    }

    private void Play((double Start, double End)? range)
    {
        if (_player.Source is null) return;
        _player.Position = TimeSpan.FromSeconds(range?.Start ?? _player.Position.TotalSeconds);
        _stopAt = range?.End;
        _player.Play();
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _player.Pause();
        _playTimer.Stop();
        _stopAt = null;
        Draw();
    }

    private void Export()
    {
        var edit = new AudioEdit(_start, _end, _bleeps.ToList(), _normalize.IsChecked == true,
            (ChannelMode)_channels.SelectedIndex, _fadeIn.Value, _fadeOut.Value);
        var file = _file;
        JobQueue.Enqueue(Loc.F("Edit {0}", Path.GetFileName(file)), file, async (job, token) =>
            job.Outputs.Add(await MediaTools.EditAudioAsync(file, edit, job.Report, token)));
        Close();
    }

    private static string Fmt(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss\.ff");
}
