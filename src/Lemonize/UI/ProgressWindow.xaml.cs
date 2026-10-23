using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Lemonize.Core;
using Lemonize.Interop;
using Lemonize.Jobs;

namespace Lemonize.UI;

public partial class ProgressWindow
{
    private readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _failureShown;
    private bool _movedByUser;
    private Point _anchor;

    public ProgressWindow()
    {
        UseSystemChrome = false;
        InitializeComponent();
        // Grows and shrinks with the job list: keep the bottom-right corner where it was.
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_movedByUser) PlaceBottomRight(_anchor);
        };
        List.ItemsSource = JobQueue.Jobs;
        SourceInitialized += (_, _) => Native.MakeToolWindow(this, noActivate: true);
        _autoHide.Tick += (_, _) =>
        {
            if (IsMouseOver || JobQueue.HasActiveJobs) return;
            _autoHide.Stop();
            Hide();
        };
        JobQueue.JobAdded += _ =>
        {
            if (!Settings.Current.ShowProgressWindow) return;
            if (!IsVisible)
            {
                _failureShown = false;
                _movedByUser = false;
                // Show it on the monitor the user is working on (where the files were dropped).
                _anchor = Native.CursorPixels();
            }
            Show();
            if (!_movedByUser) PlaceBottomRight(_anchor);
            _autoHide.Stop();
        };
        JobQueue.JobFinished += job =>
        {
            if (job.State == JobState.Failed)
            {
                // Keep failures on screen until the user looks.
                _failureShown = true;
                if (Settings.Current.ShowProgressWindow && !IsVisible)
                {
                    _anchor = Native.CursorPixels();
                    Show();
                    PlaceBottomRight(_anchor);
                }
                _autoHide.Stop();
                return;
            }
            if (job.State == JobState.Done && Settings.Current.RevealWhenDone && job.Outputs.Count > 0)
                Commands.Reveal(job.Outputs[0]);
            if (!JobQueue.HasActiveJobs && !_failureShown) _autoHide.Start();
        };
    }

    /// <summary>Bottom-right corner of the monitor containing <paramref name="pixels"/>, in physical pixels.</summary>
    private void PlaceBottomRight(Point pixels)
    {
        var area = Native.WorkAreaAt(pixels);
        var scale = Native.ScaleAt(pixels);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var w = ActualWidth * scale;
        var h = ActualHeight * scale;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(area.Right - w - 12 * scale), (int)(area.Bottom - h - 12 * scale), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        _movedByUser = true;
        DragMove();
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        _failureShown = false;
        Hide();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var job in JobQueue.Jobs.Where(j => j.IsFinished).ToList()) JobQueue.Jobs.Remove(job);
        if (JobQueue.Jobs.Count == 0) Hide();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Job job) job.Cancel();
        e.Handled = true;
    }

    private void OnJobClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Job job) return;
        if (job.State == JobState.Done && job.Outputs.Count > 0) Commands.Reveal(job.Outputs[0]);
        else if (job.State == JobState.Failed) Dialogs.Error(job.Title, job.Detail);
    }
}

public sealed class StateVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        JobState.Done => "",
        JobState.Failed => "",
        JobState.Canceled => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
