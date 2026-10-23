using System.Windows;
using System.Windows.Controls;
using Lemonize.Core;

namespace Lemonize.UI;

/// <summary>Shows a list of label/value results (QR codes and similar) with copy and open buttons.</summary>
public static class InfoWindow
{
    public static void Show(string title, IReadOnlyList<(string Label, string Value)> entries)
    {
        var list = new StackPanel { Margin = new Thickness(20) };
        foreach (var (label, value) in entries)
        {
            var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("Dim"), Margin = new Thickness(0, 0, 0, 6) });
            stack.Children.Add(new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(0) });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var copy = new Button { Content = "Copy", Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (_, _) => Clipboard.SetText(value);
            buttons.Children.Add(copy);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "tel")
            {
                var open = new Button { Content = "Open link", Style = (Style)Application.Current.FindResource("Primary") };
                open.Click += (_, _) => Commands.Open(uri.ToString());
                buttons.Children.Add(open);
            }
            stack.Children.Add(buttons);
            card.Child = stack;
            list.Children.Add(card);
        }

        new ThemedWindow
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 640,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        }.ShowOnTop();
    }
}
