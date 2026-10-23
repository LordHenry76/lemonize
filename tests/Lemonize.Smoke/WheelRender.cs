using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lemonize.Core;
using Lemonize.UI;

/// <summary>Renders the wheel for given files and mode to PNG without showing any window.
/// Note: the process may not exit by itself afterwards (WPF dispatcher on a helper thread).</summary>
public static class WheelRender
{
    public static void Run(string outDir, IReadOnlyList<(string Name, string[] Files, WheelMode Mode)> cases)
    {
        var thread = new Thread(() =>
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Lemonize;component/Themes/Theme.xaml") });
            foreach (var (name, files, mode) in cases)
            {
                var wheel = new WheelWindow();
                var t = typeof(WheelWindow);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                t.GetField("_files", flags)!.SetValue(wheel, files);
                t.GetProperty("Mode")!.SetValue(wheel, mode);
                t.GetProperty("Revealed")!.SetValue(wheel, true);
                t.GetProperty("Thumbnail", flags)!.SetValue(wheel, Lemonize.Interop.ShellThumbnails.Get(files[0], 96));
                t.GetMethod("Rebuild", flags)!.Invoke(wheel, null);
                var canvas = (FrameworkElement)t.GetField("_canvas", flags)!.GetValue(wheel)!;
                canvas.Measure(new Size(780, 780));
                canvas.Arrange(new Rect(0, 0, 780, 780));
                var host = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(0x80, 0x88, 0x90)), Child = null };
                var rtb = new RenderTargetBitmap(780, 780, 96, 96, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)), null, new Rect(0, 0, 780, 780));
                rtb.Render(bg);
                rtb.Render(canvas);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(outDir, $"wheel-{name}.png"));
                enc.Save(fs);
                _ = host;
                wheel.Close();
            }
            app.Shutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
    }
}
