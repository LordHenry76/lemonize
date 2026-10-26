using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using Lemonize.Core;
using Lemonize.Engines;
using Lemonize.Jobs;
using Lemonize.UI;
using Orientation = System.Windows.Controls.Orientation;
using TextAlignment = System.Windows.TextAlignment;

namespace Lemonize.Mockups;

/// <summary>
/// Renders the real Lemonize windows, with sample files and in Italian, to transparent PNGs framed like
/// Windows 11 windows, for the portfolio. Usage: Lemonize.Mockups [output folder] (default: ..\..\mockups).
/// </summary>
public static class Program
{
    private const double Dpi = 192; // 2× for crisp images
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static string _out = "";
    private static string _samples = "";

    [STAThread]
    public static void Main(string[] args)
    {
        _out = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), "mockups"));
        _samples = Path.Combine(Path.GetTempPath(), "lemonize-mockup-samples");
        Directory.CreateDirectory(_out);
        if (Directory.Exists(_samples)) Directory.Delete(_samples, true);
        Directory.CreateDirectory(_samples);

        Loc.Initialize("it");
        Settings.Current.OutputLocation = OutputLocation.BesideOriginal;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var dictionary in new[] { "Themes/Theme.xaml", "Themes/Brand.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lemonize;component/{dictionary}") });
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                File.WriteAllText(Path.Combine(_out, "errore.txt"), ex.ToString());
            }
            app.Shutdown();
        };
        app.Run();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lemonize.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Environment.CurrentDirectory;
    }

    private static string S(string name) => Path.Combine(_samples, name);

    private static async Task RunAsync()
    {
        await CreateSamplesAsync();
        var photo = S("Tramonto.jpg");
        var pdf = S("Relazione progetto.pdf");
        var video = S("Vacanze.mp4");
        var audio = S("Intervista.mp3");

        await WindowAsync("01-finestra-principale", new MainWindow(), w => Call(w, "Refresh"));
        await WheelAsync("02-ruota-converti", [photo], WheelMode.Convert, hover: 1);
        await WheelAsync("03-ruota-strumenti", [pdf], WheelMode.Tools, hover: 1);
        await WheelAsync("04-ruota-azioni", [video], WheelMode.Actions, hover: 0);
        await WheelAsync("05-ruota-converti-video", [video], WheelMode.Convert, hover: 7);
        await WindowAsync("06-comprimi-pdf", new CompressWindow([pdf]));
        await WindowAsync("07-editor-immagini", new ImageEditorWindow(photo), wait: 2500);
        await WindowAsync("08-organizza-pdf", new PdfOrganizerWindow([pdf]), wait: 3000, before: w =>
        {
            var list = (ListBox)w.GetType().GetField("_list", Private)!.GetValue(w)!;
            list.SelectedItems.Add(list.Items[2]);
            list.SelectedItems.Add(list.Items[3]);
        });
        await WindowAsync("09-editor-video", new VideoEditorWindow(video), wait: 3000, before: w =>
        {
            ((Slider)w.GetType().GetField("_position", Private)!.GetValue(w)!).Value = 4.2;
            w.GetType().GetField("_start", Private)!.SetValue(w, 1.0);
            w.GetType().GetField("_end", Private)!.SetValue(w, 10.5);
            ((List<double>)w.GetType().GetField("_splits", Private)!.GetValue(w)!).Add(6.0);
            Call(w, "UpdateSummary");
        }, settle: 2500);
        await WindowAsync("10-editor-audio", new AudioEditorWindow(audio), wait: 3000, before: w =>
        {
            w.GetType().GetField("_start", Private)!.SetValue(w, 1.5);
            w.GetType().GetField("_end", Private)!.SetValue(w, 18.0);
            ((List<(double, double)>)w.GetType().GetField("_bleeps", Private)!.GetValue(w)!).Add((7.2, 8.1));
            w.GetType().GetField("_selection", Private)!.SetValue(w, ((double, double)?)(11.0, 14.5));
            Call(w, "Draw");
        });
        await WindowAsync("11-metadati", new MetadataWindow([photo]), wait: 2000);
        await ProgressAsync("12-avanzamento", photo, pdf, video, audio);
    }

    // ---------- Samples ----------

    private static async Task CreateSamplesAsync()
    {
        // A drawn sunset: nothing copyrighted, and it reads well at thumbnail size.
        using (var img = new MagickImage("gradient:#FFB347-#6A3093", 2400, 1600))
        {
            img.Rotate(180);
            var d = new Drawables();
            d.FillColor(new MagickColor("#FFE29A")).Circle(1500, 900, 1500, 700);
            d.FillColor(new MagickColor("#5B2A6E")).Polygon(new PointD(0, 1150), new PointD(500, 900), new PointD(1000, 1080), new PointD(1600, 820), new PointD(2400, 1050), new PointD(2400, 1600), new PointD(0, 1600));
            d.FillColor(new MagickColor("#3B1B4A")).Polygon(new PointD(0, 1300), new PointD(700, 1120), new PointD(1300, 1260), new PointD(1900, 1100), new PointD(2400, 1250), new PointD(2400, 1600), new PointD(0, 1600));
            d.FillColor(new MagickColor("#22102C")).Polygon(new PointD(0, 1450), new PointD(900, 1330), new PointD(1700, 1440), new PointD(2400, 1380), new PointD(2400, 1600), new PointD(0, 1600));
            d.Draw(img);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Canon");
            exif.SetValue(ExifTag.Model, "EOS R6");
            exif.SetValue(ExifTag.Artist, "Marco Landucci");
            exif.SetValue(ExifTag.Copyright, "© 2026 [wild]lab");
            exif.SetValue(ExifTag.DateTimeOriginal, "2026:08:14 20:41:07");
            exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
            exif.SetValue(ExifTag.FNumber, new Rational(56, 10));
            exif.SetValue(ExifTag.ISOSpeedRatings, [(ushort)200]);
            img.SetProfile(exif);
            img.Quality = 92;
            img.Write(S("Tramonto.jpg"), MagickFormat.Jpeg);
        }

        var lorem = "Il progetto ha l'obiettivo di semplificare la conversione dei file direttamente dal desktop, senza passare da servizi online. " +
                    "Ogni operazione avviene in locale: i documenti restano sul computer dell'utente e vengono salvati accanto agli originali. " +
                    "Le prove sul campo hanno coinvolto immagini, video, documenti PDF e archivi, con tempi di elaborazione contenuti anche su macchine non recenti.";
        var blocks = new List<DocBlock>();
        foreach (var chapter in new[] { "Introduzione", "Obiettivi", "Architettura", "Ruota di trascinamento", "Motori di conversione", "Prove e risultati", "Conclusioni" })
        {
            if (blocks.Count > 0) blocks.Add(new DocBlock(BlockKind.PageBreak));
            blocks.Add(new DocBlock(BlockKind.Heading, chapter));
            for (var i = 0; i < 5; i++) blocks.Add(new DocBlock(BlockKind.Paragraph, lorem));
        }
        var text = S("testo.pdf");
        DocumentEngine.Write(new DocContent("Relazione progetto", blocks), text);
        var image = S("immagine.pdf");
        PdfEngine.ImagesToPdf([S("Tramonto.jpg")], image);
        PdfEngine.Assemble([new(text, 0, 0), new(text, 1, 0), new(image, 0, 0), new(text, 2, 0), new(text, 3, 0), new(text, 4, 0), new(text, 5, 0), new(text, 6, 0)],
            S("Relazione progetto.pdf"));

        // Slow pan over the sunset, with a soft chord: real frames and a waveform for the editors.
        await Ffmpeg.RunAsync(["-loop", "1", "-i", S("Tramonto.jpg"),
            "-f", "lavfi", "-i", "aevalsrc=0.4*sin(2*PI*220*t)*(0.6+0.4*sin(2*PI*0.25*t))+0.2*sin(2*PI*330*t):s=44100:d=14",
            "-vf", "scale=1920:-2,zoompan=z='1+0.0015*on':d=1:s=1280x720:fps=25", "-t", "14",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", S("Vacanze.mp4")], 14, null, default);
        await Ffmpeg.RunAsync(["-f", "lavfi", "-i",
            "aevalsrc=(0.5*sin(2*PI*180*t)+0.3*sin(2*PI*270*t))*abs(sin(PI*0.9*t))*(0.5+0.5*abs(sin(PI*0.13*t))):s=44100:d=22",
            "-c:a", "libmp3lame", "-q:a", "3", S("Intervista.mp3")], 22, null, default);
    }

    // ---------- Windows ----------

    private static async Task WindowAsync(string name, Window window, Action<Window>? before = null, int wait = 800, int settle = 300, bool frame = true)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.Show();
        await Task.Delay(wait);
        before?.Invoke(window);
        await Task.Delay(settle);

        // The template root covers the whole client area (background and margins included) and is already laid out.
        window.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var client = Render(root, root.ActualWidth, root.ActualHeight, null, layout: false);
        Save(name, frame ? Frame(client, root.ActualWidth, root.ActualHeight, window.Title) : client);
        window.Close();
    }

    /// <summary>Windows 11 dark window: title bar, rounded corners, thin border and a soft shadow.</summary>
    private static BitmapSource Frame(BitmapSource client, double width, double height, string title)
    {
        const double titleHeight = 32, margin = 48;
        var titleBar = new DockPanel { Height = titleHeight, Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) };
        var captions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var glyph in new[] { "", "", "" })
            captions.Children.Add(new TextBlock
            {
                Text = glyph, Width = 46, FontSize = 10, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
            });
        DockPanel.SetDock(captions, Dock.Right);
        titleBar.Children.Add(captions);
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        caption.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/lemonize.png")), Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        caption.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), VerticalAlignment = VerticalAlignment.Center });
        titleBar.Children.Add(caption);

        var body = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        body.Children.Add(titleBar);
        body.Children.Add(new Image { Source = client, Width = width, Height = height, Stretch = Stretch.Fill });

        var window = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4C)),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x19)),
            Child = body, Margin = new Thickness(margin),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Direction = 270, Opacity = 0.45 },
        };
        // Clip the content to the rounded corners.
        body.Clip = new RectangleGeometry(new Rect(0, 0, width, height + titleHeight), 7, 7);
        var w = width + 2 + 2 * margin;
        var h = height + titleHeight + 2 + 2 * margin;
        return Render(window, w, h, null);
    }

    private static async Task WheelAsync(string name, string[] files, WheelMode mode, int hover)
    {
        // Thumbnails off the UI thread: the synchronous Get deadlocks there for PDFs and videos.
        var thumbnail = await Interop.ShellThumbnails.GetAsync(files[0], 96);
        var wheel = new WheelWindow();
        var t = typeof(WheelWindow);
        t.GetField("_files", Private)!.SetValue(wheel, files);
        t.GetProperty("Mode")!.SetValue(wheel, mode);
        t.GetProperty("Revealed")!.SetValue(wheel, true);
        t.GetProperty("Thumbnail", Private)!.SetValue(wheel, thumbnail);
        t.GetMethod("Rebuild", Private)!.Invoke(wheel, null);
        t.GetProperty("Hover", Private)!.SetValue(wheel, hover);
        var canvas = (FrameworkElement)t.GetField("_canvas", Private)!.GetValue(wheel)!;
        Save(name, Render(canvas, wheel.Width, wheel.Height, null), trim: true);
        wheel.Close();
    }

    private static async Task ProgressAsync(string name, string photo, string pdf, string video, string audio)
    {
        Settings.Current.ShowProgressWindow = false; // shown by us, not by the queue events
        var window = new ProgressWindow();
        async Task Add(string title, string file, JobState state, double progress, string detail)
        {
            var job = new Job(title, file) { State = state, Detail = detail, Thumbnail = await Interop.ShellThumbnails.GetAsync(file, 32) };
            if (progress >= 0) job.Report(progress);
            JobQueue.Jobs.Add(job);
        }
        await Add("Vacanze.mp4 → WebM", video, JobState.Running, 0.62, "Conversione…");
        await Add("Comprimi Relazione progetto.pdf", pdf, JobState.Done, -1, "1,4 MB → 312 KB");
        await Add("Tramonto.jpg → PNG", photo, JobState.Done, -1, "Tramonto.png");
        await Add("Intervista.mp3 → M4A", audio, JobState.Done, -1, "Intervista.m4a");
        await WindowAsync(name, window, wait: 600, frame: false);
    }

    // ---------- Rendering ----------

    private static BitmapSource Render(FrameworkElement element, double width, double height, Brush? background, bool layout = true)
    {
        if (layout)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }
        var scale = Dpi / 96;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), Dpi, Dpi, PixelFormats.Pbgra32);
        if (background is not null)
        {
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            rtb.Render(bg);
        }
        rtb.Render(element);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(string name, BitmapSource bitmap, bool trim = false)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_out, name + ".png");
        using (var fs = File.Create(path)) encoder.Save(fs);
        if (trim)
        {
            // The wheel window is a fixed square: drop the empty transparent space around the drawing.
            using var image = new MagickImage(path);
            image.Trim();
            image.ResetPage();
            image.BorderColor = MagickColors.Transparent;
            image.Border(32);
            image.Write(path);
        }
    }

    private static void Call(object target, string method) => target.GetType().GetMethod(method, Private)!.Invoke(target, null);
}
