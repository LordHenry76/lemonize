using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Lemonize.Core;

namespace Lemonize.Jobs;

public enum JobState { Queued, Running, Done, Failed, Canceled }

public sealed class Job : INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cts = new();
    private double _progress = -1;
    private JobState _state = JobState.Queued;
    private string _detail = Loc.T("Waiting…");

    public Job(string title, string? icon)
    {
        Title = title;
        IconPath = icon;
    }

    private System.Windows.Media.Imaging.BitmapSource? _thumbnail;

    /// <summary>Preview of the input file, loaded in the background.</summary>
    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public string Title { get; }
    public string? IconPath { get; }
    public List<string> Outputs { get; } = [];
    public CancellationToken Token => _cts.Token;

    /// <summary>0..1, or negative for indeterminate.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value, nameof(IsIndeterminate), nameof(ProgressPercent));
    }

    public bool IsIndeterminate => _progress < 0 && _state == JobState.Running;
    public double ProgressPercent => _state == JobState.Done ? 100 : Math.Max(0, _progress) * 100;

    public JobState State
    {
        get => _state;
        set => Set(ref _state, value, nameof(IsIndeterminate), nameof(ProgressPercent), nameof(IsFinished), nameof(IsActive));
    }

    public bool IsFinished => _state is JobState.Done or JobState.Failed or JobState.Canceled;
    public bool IsActive => !IsFinished;

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public void Report(double value) => OnUi(() => Progress = Math.Clamp(value, 0, 1));
    public void Report(string detail) => OnUi(() => Detail = detail);
    public void Cancel() => _cts.Cancel();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string? also1 = null, string? also2 = null, string? also3 = null,
        string? also4 = null, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in new[] { property, also1, also2, also3, also4 })
            if (name is not null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public delegate Task JobWork(Job job, CancellationToken token);

public static class JobQueue
{
    private static SemaphoreSlim _slots = new(Math.Max(1, Settings.Current.MaxParallelJobs));

    public static ObservableCollection<Job> Jobs { get; } = [];

    public static event Action<Job>? JobAdded;
    public static event Action<Job>? JobFinished;

    public static bool HasActiveJobs => Jobs.Any(j => j.IsActive);

    public static void Configure(int maxParallel) => _slots = new SemaphoreSlim(Math.Max(1, maxParallel));

    public static Job Enqueue(string title, string? iconPath, JobWork work)
    {
        var job = new Job(title, iconPath);
        if (iconPath is not null)
            _ = Interop.ShellThumbnails.GetAsync(iconPath, 32).ContinueWith(t => Job.OnUi(() => job.Thumbnail = t.Result),
                TaskContinuationOptions.OnlyOnRanToCompletion);
        Job.OnUi(() =>
        {
            Jobs.Insert(0, job);
            while (Jobs.Count > 100 && Jobs[^1].IsFinished) Jobs.RemoveAt(Jobs.Count - 1);
            JobAdded?.Invoke(job);
        });
        _ = RunAsync(job, work);
        return job;
    }

    private static async Task RunAsync(Job job, JobWork work)
    {
        var slots = _slots;
        using var claims = OutputPaths.ClaimScope();
        try
        {
            await slots.WaitAsync(job.Token);
            try
            {
                Job.OnUi(() =>
                {
                    job.State = JobState.Running;
                    job.Detail = Loc.T("Working…");
                });
                await Task.Run(() => work(job, job.Token), job.Token);
            }
            finally
            {
                slots.Release();
            }
            Job.OnUi(() =>
            {
                job.State = JobState.Done;
                job.Detail = job.Outputs.Count switch
                {
                    0 => Loc.T("Done"),
                    1 => Path.GetFileName(job.Outputs[0]),
                    var n => Loc.F("{0} files created", n),
                };
            });
        }
        catch (OperationCanceledException)
        {
            Job.OnUi(() =>
            {
                job.State = JobState.Canceled;
                job.Detail = Loc.T("Canceled");
            });
            DeletePartialOutputs(job);
        }
        catch (Exception ex)
        {
            Job.OnUi(() =>
            {
                job.State = JobState.Failed;
                job.Detail = ex.Message;
            });
            DeletePartialOutputs(job);
        }
        Job.OnUi(() => JobFinished?.Invoke(job));
    }

    private static void DeletePartialOutputs(Job job)
    {
        foreach (var output in job.Outputs)
        {
            try
            {
                if (File.Exists(output)) File.Delete(output);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
