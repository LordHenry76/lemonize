using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Lemonize.Core;
using Lemonize.Interop;

namespace Lemonize.UI;

/// <summary>A small always-on-top card holding files, ready to be dragged anywhere later.</summary>
public sealed class FloatWindow : Window
{
    private static readonly List<FloatWindow> Open = [];
    private readonly List<string> _files;
    private Point? _pressAt;

    private FloatWindow(IReadOnlyList<string> files)
    {
        _files = [.. files];
        Width = 150;
        Height = 176;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Title = "Lemonize float";
        SourceInitialized += (_, _) => Native.MakeToolWindow(this, noActivate: false);
        Build();
    }

    public static void Create(IReadOnlyList<string> files)
    {
        var window = new FloatWindow(files);
        var cursor = Native.CursorPixels();
        var scale = Native.ScaleAt(cursor);
        window.Left = cursor.X / scale + 12 + Open.Count * 14;
        window.Top = cursor.Y / scale - 60 + Open.Count * 14;
        Open.Add(window);
        window.Closed += (_, _) => Open.Remove(window);
        window.Show();
    }

    private void Build()
    {
        var surface = (Brush)FindResource("Surface");
        var card = new Border
        {
            Background = surface,
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(8),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5 },
            Cursor = Cursors.Hand,
            ToolTip = string.Join("\n", _files.Select(Path.GetFileName)) + "\n\n" + Loc.T("Drag to drop anywhere · double-click to open · right-drag to move"),
        };
        var grid = new Grid();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 14, 10, 10) };

        var preview = new Grid { Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
        if (_files.Count > 1)
            preview.Children.Add(new Border
            {
                Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(8),
                RenderTransform = new RotateTransform(-8, 42, 42),
            });
        preview.Children.Add(new Image { Source = ShellThumbnails.Get(_files[0], 128), Stretch = Stretch.Uniform });
        stack.Children.Add(preview);
        stack.Children.Add(new TextBlock
        {
            Text = _files.Count == 1 ? Path.GetFileName(_files[0]) : Loc.F("{0} items", _files.Count),
            TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 116,
            Foreground = (Brush)FindResource("Text"),
        });
        if (_files.Count == 1 && File.Exists(_files[0]))
            stack.Children.Add(new TextBlock
            {
                Text = OutputPaths.FormatSize(new FileInfo(_files[0]).Length), HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 10.5, Foreground = (Brush)FindResource("TextDim"),
            });
        grid.Children.Add(stack);

        var close = new Button
        {
            Content = "", Style = (Style)FindResource("IconButton"), FontSize = 9, Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2),
            Opacity = 0.6, ToolTip = Loc.T("Close"),
        };
        close.Click += (_, _) => Close();
        grid.Children.Add(close);
        card.Child = grid;
        Content = card;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            foreach (var file in _files) Commands.Open(file);
            return;
        }
        _pressAt = e.GetPosition(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _pressAt = null;
        var existing = _files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
        if (existing.Length == 0)
        {
            Close();
            return;
        }
        var data = new DataObject(DataFormats.FileDrop, existing);
        var effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        // A move consumed the files, so the card has nothing left to hold.
        if (effect == DragDropEffects.Move || existing.All(f => !File.Exists(f) && !Directory.Exists(f))) Close();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        // DragMove requires the left button, so emulate it for right-drag repositioning.
        var origin = e.GetPosition(this);
        CaptureMouse();
        MouseEventHandler move = (_, args) =>
        {
            var p = args.GetPosition(this);
            Left += p.X - origin.X;
            Top += p.Y - origin.Y;
        };
        MouseMove += move;
        MouseRightButtonUp += Release;

        void Release(object s, MouseButtonEventArgs a)
        {
            MouseMove -= move;
            MouseRightButtonUp -= Release;
            ReleaseMouseCapture();
        }
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        // Dropping more files onto a card adds them to the stack.
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _files.AddRange(files.Where(f => !_files.Contains(f, StringComparer.OrdinalIgnoreCase)));
            Build();
        }
    }
}
