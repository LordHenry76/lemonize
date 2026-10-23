using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lemonize.Core;
using Lemonize.Interop;

namespace Lemonize.UI;

/// <summary>Base for every Lemonize window: dark chrome and the shared theme.</summary>
public class ThemedWindow : Window
{
    /// <summary>False for borderless popups: the Windows 11 frame would draw a light outline around them.</summary>
    protected bool UseSystemChrome { get; set; } = true;

    public ThemedWindow()
    {
        SetResourceReference(StyleProperty, "LemonWindow");
        SourceInitialized += (_, _) =>
        {
            if (UseSystemChrome) Native.ApplyDarkChrome(this);
            else Native.RemoveSystemFrame(this);
        };
        Loaded += (_, _) => Loc.Apply(this);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/lemonize.ico"));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled) Close();
    }

    /// <summary>Brings a window to the front even when the app has no foreground window (tray app).</summary>
    public void ShowOnTop()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Topmost = false;
    }
}

public static class Dialogs
{
    public static void Error(string title, string message) => Message(title, message, isError: true);

    public static void Message(string title, string message, bool isError = false)
    {
        var window = Build(title, out var body, out var buttons);
        if (isError)
            body.Children.Add(new TextBlock
            {
                Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 22,
                Foreground = (Brush)Application.Current.FindResource("Danger"), Margin = new Thickness(0, 0, 0, 10),
            });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = (Style)Application.Current.FindResource("Primary") };
        ok.Click += (_, _) => window.Close();
        buttons.Children.Add(ok);
        window.ShowOnTop();
    }

    public static string? Prompt(string title, string message, string initial)
    {
        var window = Build(title, out var body, out var buttons);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = initial, MinWidth = 360 };
        body.Children.Add(input);
        string? result = null;
        AddOkCancel(window, buttons, () => result = input.Text);
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        window.Topmost = true;
        window.ShowDialog();
        return result;
    }

    public static bool Confirm(string title, string message, string okText = "OK")
    {
        var window = Build(title, out var body, out var buttons);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        var confirmed = false;
        AddOkCancel(window, buttons, () => confirmed = true, okText);
        window.Topmost = true;
        window.ShowDialog();
        return confirmed;
    }

    private static void AddOkCancel(Window window, Panel buttons, Action onOk, string okText = "OK")
    {
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Style = (Style)Application.Current.FindResource("Primary") };
        ok.Click += (_, _) =>
        {
            onOk();
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
    }

    private static ThemedWindow Build(string title, out StackPanel body, out StackPanel buttons)
    {
        body = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 20) };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        return new ThemedWindow
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            MinWidth = 320,
        };
    }
}
