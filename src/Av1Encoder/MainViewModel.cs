using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Av1Encoder;

public sealed class MainViewModel : INotifyPropertyChanged
{
    static readonly HashSet<string> VideoExtensions = [".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm"];

    readonly Settings settings = Settings.Load();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch elapsed = new();
    CancellationTokenSource? cancellation;
    string runId = "";
    int runWorkers;
    QualityPreset runQuality = QualityPreset.Default;
    List<Job> runJobs = [];
    bool shuttingDown;
    int running;
    int scanGeneration;
    double sessionWork;

    public ObservableCollection<Job> Jobs { get; } = [];

    public MainViewModel()
    {
        source = settings.Source;
        destination = string.IsNullOrWhiteSpace(settings.Destination) ? DefaultDestination(source) : settings.Destination;
        workers = Math.Clamp(settings.Workers, 1, 3);
        quality = Enum.IsDefined(settings.Quality) ? settings.Quality : QualityLevel.Default;
        timer.Tick += (_, _) => UpdateStats();
    }

    string source;
    public string Source
    {
        get => source;
        set
        {
            var oldDefault = DefaultDestination(source);
            if (!Set(ref source, value))
                return;
            if (string.IsNullOrWhiteSpace(Destination) || PathsEqual(Destination, oldDefault))
                Destination = DefaultDestination(value);
        }
    }

    string destination;
    public string Destination { get => destination; set => Set(ref destination, value); }

    int workers;
    public int Workers
    {
        get => workers;
        set
        {
            if (Set(ref workers, Math.Clamp(value, 1, 3)) && IsRunning)
                Pump();
        }
    }

    QualityLevel quality;
    public QualityLevel Quality { get => quality; set => Set(ref quality, value); }

    bool ffmpegMissing;
    public bool FfmpegMissing { get => ffmpegMissing; private set => Set(ref ffmpegMissing, value); }

    bool isInstallingFfmpeg;
    public bool IsInstallingFfmpeg
    {
        get => isInstallingFfmpeg;
        private set
        {
            if (Set(ref isInstallingFfmpeg, value))
                OnPropertyChanged(nameof(CanInstallFfmpeg));
        }
    }

    public bool CanInstallFfmpeg => !IsInstallingFfmpeg;

    bool gpuChecked;
    string? gpuError;

    bool isRunning;
    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (Set(ref isRunning, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsRunning;

    string statusText = "";
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    string completedText = "0 / 0";
    public string CompletedText { get => completedText; private set => Set(ref completedText, value); }

    string failedText = "";
    public string FailedText { get => failedText; private set => Set(ref failedText, value); }

    string savedText = "0 MB";
    public string SavedText { get => savedText; private set => Set(ref savedText, value); }

    string savedDetail = "";
    public string SavedDetail { get => savedDetail; private set => Set(ref savedDetail, value); }

    string etaText = "-";
    public string EtaText { get => etaText; private set => Set(ref etaText, value); }

    string elapsedText = "";
    public string ElapsedText { get => elapsedText; private set => Set(ref elapsedText, value); }

    double overallProgress;
    public double OverallProgress { get => overallProgress; private set => Set(ref overallProgress, value); }

    public void SaveSettings()
    {
        settings.Source = Source;
        settings.Destination = Destination;
        settings.Workers = Workers;
        settings.Quality = Quality;
        settings.Save();
    }

    public async Task<bool> ScanAsync()
    {
        var generation = ++scanGeneration;
        Jobs.Clear();
        UpdateStats();

        if (!await CheckRequirementsAsync())
            return false;
        if (string.IsNullOrWhiteSpace(Source) || !Directory.Exists(Source))
        {
            StatusText = "Pick a source folder.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Destination))
        {
            StatusText = "Pick a destination folder.";
            return false;
        }
        var sourceFolder = Path.GetFullPath(Source);
        var destinationFolder = Path.GetFullPath(Destination);
        if (PathsEqual(sourceFolder, destinationFolder))
        {
            StatusText = "The destination must be a different folder from the source.";
            return false;
        }

        StatusText = "Scanning...";
        var files = new DirectoryInfo(sourceFolder).EnumerateFiles()
            .Where(file => VideoExtensions.Contains(file.Extension.ToLowerInvariant()))
            .OrderBy(file => file.Length)
            .ToList();
        var outputNames = AllocateOutputNames(files);

        var jobs = new Job?[files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (index, cancellationToken) =>
        {
            var file = files[index];
            jobs[index] = await CreateJobAsync(file, Path.Combine(destinationFolder, outputNames[file.FullName]), cancellationToken);
        });
        if (generation != scanGeneration)
            return false;

        foreach (var job in jobs)
            Jobs.Add(job!);
        UpdateStats();

        var queued = Jobs.Where(job => job.State == JobState.Queued).ToList();
        var done = Jobs.Count(job => job.State == JobState.Done);
        var skipped = Jobs.Count(job => job.State == JobState.Skipped);
        StatusText = Jobs.Count == 0
            ? "No video files in the source folder."
            : $"{queued.Count} to encode ({Format.Bytes(queued.Sum(job => job.SourceBytes))}), {done} already done, {skipped} already AV1.";
        return true;
    }

    async Task<bool> CheckRequirementsAsync()
    {
        FfmpegMissing = !Tools.Locate();
        if (FfmpegMissing)
        {
            StatusText = "ffmpeg is not installed. Click Install ffmpeg to add it.";
            return false;
        }
        if (!gpuChecked)
        {
            gpuError = await Encoder.CheckAv1NvencAsync();
            gpuChecked = true;
            if (gpuError is not null)
                AppLog.Error($"AV1 NVENC check failed: {gpuError}");
        }
        if (gpuError is not null)
        {
            StatusText = $"AV1 encoding is unavailable. This app supports only NVIDIA RTX 40 series or newer GPUs with a current driver. ffmpeg reported: {gpuError}";
            return false;
        }
        return true;
    }

    public async Task InstallFfmpegAsync()
    {
        IsInstallingFfmpeg = true;
        StatusText = "Installing ffmpeg with winget. This can take a minute...";
        AppLog.Info("installing ffmpeg with winget");
        try
        {
            var result = await Tools.RunAsync("winget.exe", [
                "install", "--id", "Gyan.FFmpeg", "--exact", "--silent",
                "--accept-package-agreements", "--accept-source-agreements",
            ]);
            AppLog.Info($"winget exited with {result.ExitCode}{Environment.NewLine}{result.Output.Trim()}");
        }
        catch (Win32Exception exception)
        {
            AppLog.Error("winget could not be started", exception);
            StatusText = "winget is not available. Download ffmpeg from gyan.dev/ffmpeg/builds and put ffmpeg.exe and ffprobe.exe next to Av1Encoder.exe.";
            IsInstallingFfmpeg = false;
            return;
        }
        IsInstallingFfmpeg = false;
        if (await ScanAsync() || !FfmpegMissing)
            return;
        StatusText = "The ffmpeg install did not finish. Details are in app.log.";
    }

    static Dictionary<string, string> AllocateOutputNames(List<FileInfo> files)
    {
        // a.mkv and a.mp4 would both become a.mp4; keep the source extension in those names
        var duplicateStems = files
            .GroupBy(file => Path.GetFileNameWithoutExtension(file.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputNames = new Dictionary<string, string>();
        // allocate in name order so adding a file later does not rename existing outputs
        foreach (var file in files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(file.Name);
            var baseName = duplicateStems.Contains(stem) ? stem + file.Extension : stem;
            var outputName = baseName + ".mp4";
            for (var copy = 2; !taken.Add(outputName); copy++)
                outputName = $"{baseName} ({copy}).mp4";
            outputNames[file.FullName] = outputName;
        }
        return outputNames;
    }

    static async Task<Job> CreateJobAsync(FileInfo file, string outputPath, CancellationToken cancellationToken)
    {
        var info = await Probe.ReadAsync(file.FullName, cancellationToken);
        var job = new Job { SourcePath = file.FullName, OutputPath = outputPath, SourceBytes = file.Length, Info = info };
        if (info is null)
        {
            job.State = JobState.Failed;
            job.Status = "Can't read this file";
        }
        else if (info.Codec == "av1")
        {
            job.State = JobState.Skipped;
            job.Status = "Already AV1";
        }
        else if (File.Exists(outputPath) && await Encoder.VerifyOutputAsync(info, file.Length, outputPath, cancellationToken) is null)
        {
            job.State = JobState.Done;
            job.OutputBytes = new FileInfo(outputPath).Length;
            job.Progress = 100;
            job.Status = $"Done earlier, {SavedPercent(job)}% smaller";
        }
        else
        {
            job.State = JobState.Queued;
            job.Status = "Queued";
        }
        return job;
    }

    public async Task StartAsync()
    {
        if (IsRunning)
            return;
        SaveSettings();
        if (!await ScanAsync())
            return;

        runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        runWorkers = Workers;
        runQuality = QualityPreset.For(Quality);
        foreach (var unreadable in Jobs.Where(job => job.State == JobState.Failed && job.Info is null))
        {
            AppLog.Warn($"unreadable source skipped: {unreadable.SourcePath}");
            AppLog.Record(FileRecordFor(unreadable, "unreadable", "ffprobe could not read the file", 0));
        }
        if (!Jobs.Any(job => job.State == JobState.Queued))
        {
            StatusText = Jobs.Count == 0 ? StatusText : "Nothing left to encode.";
            return;
        }

        Directory.CreateDirectory(Destination);
        foreach (var stale in Directory.EnumerateFiles(Destination, "*" + Encoder.PartialSuffix))
            Encoder.TryDelete(stale);

        runJobs = Jobs.Where(job => job.State == JobState.Queued).ToList();
        AppLog.Info($"run {runId} started: {runJobs.Count} files, {Format.Bytes(runJobs.Sum(job => job.SourceBytes))}, workers {Workers}, quality {runQuality.Level} (cq {runQuality.Cq}, maxrate {runQuality.MaxratePercent}%), source \"{Source}\", destination \"{Destination}\"");

        cancellation = new CancellationTokenSource();
        sessionWork = 0;
        elapsed.Restart();
        IsRunning = true;
        StatusText = "Encoding...";
        timer.Start();
        Pump();
    }

    public void Stop()
    {
        if (cancellation is null || cancellation.IsCancellationRequested)
            return;
        StatusText = "Stopping...";
        cancellation.Cancel();
    }

    /// <summary>Synchronous stop for window close: async cleanup may not run once the app exits.</summary>
    public void Shutdown()
    {
        shuttingDown = true;
        cancellation?.Cancel();
        Encoder.KillAll();
        if (Directory.Exists(Destination))
            foreach (var partial in Directory.EnumerateFiles(Destination, "*" + Encoder.PartialSuffix))
                Encoder.DeleteWithRetry(partial);
        foreach (var job in Jobs.Where(candidate => candidate.State == JobState.Encoding))
            AppLog.Record(FileRecordFor(job, "stopped", $"app closed at {job.Progress:0}%", job.Elapsed.TotalSeconds));
        WriteRunRecord("closed");
        AppLog.Info($"run {runId} ended: app closed while encoding");
    }

    void Pump()
    {
        if (cancellation is null || cancellation.IsCancellationRequested)
            return;
        while (running < Workers && Jobs.FirstOrDefault(job => job.State == JobState.Queued) is { } next)
        {
            running++;
            _ = RunJobAsync(next, cancellation.Token);
        }
        if (running == 0)
            Finish(stopped: false);
    }

    async Task RunJobAsync(Job job, CancellationToken cancellationToken)
    {
        job.State = JobState.Encoding;
        job.Status = "Starting";
        job.Started = DateTime.UtcNow;
        job.WorkersAtStart = Workers;
        job.Quality = runQuality;
        AppLog.Info($"start  {job.FileName} ({job.Resolution}, {job.SourceSize}, {job.Info?.Codec})");
        try
        {
            var failure = await Encoder.EncodeAsync(job, job.ReportProgress, cancellationToken);
            sessionWork += job.Work * (failure is null ? 1 : job.Progress / 100);
            if (failure is null)
            {
                job.OutputBytes = new FileInfo(job.OutputPath).Length;
                job.State = JobState.Done;
                job.Progress = 100;
                job.Status = $"Done, {SavedPercent(job)}% smaller";
                AppLog.Info($"done   {job.FileName} {job.SourceSize} -> {Format.Bytes(job.OutputBytes)} in {Format.Duration(job.Elapsed)}");
                AppLog.Record(FileRecordFor(job, "done", null, job.Elapsed.TotalSeconds));
            }
            else
            {
                Fail(job, failure);
            }
        }
        catch (OperationCanceledException)
        {
            if (!shuttingDown)
            {
                AppLog.Info($"stopped {job.FileName} at {job.Progress:0}%");
                AppLog.Record(FileRecordFor(job, "stopped", $"stopped by user at {job.Progress:0}%", job.Elapsed.TotalSeconds));
            }
            job.Requeue();
        }
        catch (Exception exception)
        {
            AppLog.Error($"unexpected error encoding {job.SourcePath}", exception);
            Fail(job, exception.Message);
        }
        job.Speed = "";
        running--;
        if (shuttingDown)
            return;
        UpdateStats();

        if (cancellationToken.IsCancellationRequested)
        {
            if (running == 0)
                Finish(stopped: true);
            return;
        }
        Pump();
    }

    void Fail(Job job, string reason)
    {
        AppLog.Warn($"FAILED {job.FileName}: {reason}");
        AppLog.Record(FileRecordFor(job, "failed", reason, job.Elapsed.TotalSeconds));
        job.State = JobState.Failed;
        job.Status = reason;
    }

    void Finish(bool stopped)
    {
        timer.Stop();
        elapsed.Stop();
        IsRunning = false;
        UpdateStats();
        var done = runJobs.Where(job => job.State == JobState.Done).ToList();
        var failed = runJobs.Count(job => job.State == JobState.Failed);
        var saved = done.Sum(job => job.SourceBytes - job.OutputBytes);
        StatusText = stopped
            ? "Stopped. Unfinished files go back in the queue; press Start to continue."
            : $"Finished in {Format.Duration(elapsed.Elapsed)}. {done.Count} done, {failed} failed, {Format.Bytes(saved)} saved.";
        WriteRunRecord(stopped ? "stopped" : "finished");
        AppLog.Info($"run {runId} ended: {StatusText}");
    }

    void WriteRunRecord(string result)
    {
        var done = runJobs.Where(job => job.State == JobState.Done).ToList();
        var sourceBytes = done.Sum(job => job.SourceBytes);
        var outputBytes = done.Sum(job => job.OutputBytes);
        AppLog.Record(new RunRecord(
            DateTimeOffset.Now, runId, result, Source, Destination, runWorkers, runQuality.Level.ToString(),
            Queued: runJobs.Count,
            Done: done.Count,
            Failed: runJobs.Count(job => job.State == JobState.Failed),
            Remaining: runJobs.Count(job => job.State is JobState.Queued or JobState.Encoding),
            SourceBytes: sourceBytes,
            OutputBytes: outputBytes,
            SavedBytes: sourceBytes - outputBytes,
            ElapsedSec: Math.Round(elapsed.Elapsed.TotalSeconds, 1)));
    }

    FileRecord FileRecordFor(Job job, string result, string? reason, double encodeSeconds)
    {
        var info = job.Info;
        var done = result == "done";
        long? saved = done ? job.SourceBytes - job.OutputBytes : null;
        return new FileRecord(
            DateTimeOffset.Now, runId, job.FileName, job.SourcePath, job.OutputPath, result, reason,
            Codec: info?.Codec ?? "",
            Width: info?.Width ?? 0,
            Height: info?.Height ?? 0,
            DurationSec: Math.Round(info?.Duration ?? 0, 2),
            Fps: Math.Round(info?.Fps ?? 0, 3),
            SourceBytes: job.SourceBytes,
            OutputBytes: done ? job.OutputBytes : null,
            SavedBytes: saved,
            SavedPct: done && job.SourceBytes > 0 ? Math.Round(saved!.Value * 100.0 / job.SourceBytes, 1) : null,
            EncodeSec: Math.Round(encodeSeconds, 1),
            Speed: done && encodeSeconds > 0 ? Math.Round((info?.Duration ?? 0) / encodeSeconds, 2) : null,
            Workers: job.WorkersAtStart > 0 ? job.WorkersAtStart : Workers,
            Quality: job.Quality.Level.ToString());
    }

    void UpdateStats()
    {
        var counted = Jobs.Where(job => job.State != JobState.Skipped && job.Info is not null).ToList();
        var done = counted.Where(job => job.State == JobState.Done).ToList();
        var failed = Jobs.Count(job => job.State == JobState.Failed);

        CompletedText = $"{done.Count} / {counted.Count}";
        FailedText = failed > 0 ? $"{failed} failed" : "";

        var sourceBytes = done.Sum(job => job.SourceBytes);
        var saved = sourceBytes - done.Sum(job => job.OutputBytes);
        SavedText = Format.Bytes(saved);
        SavedDetail = sourceBytes > 0 ? $"{saved * 100.0 / sourceBytes:0}% smaller" : "";

        var totalWork = counted.Sum(job => job.Work);
        OverallProgress = totalWork > 0 ? counted.Sum(job => job.WorkDone) / totalWork * 100 : 0;

        if (!IsRunning)
        {
            EtaText = "-";
            ElapsedText = elapsed.Elapsed > TimeSpan.Zero ? $"took {Format.Duration(elapsed.Elapsed)}" : "";
            return;
        }

        ElapsedText = $"elapsed {Format.Duration(elapsed.Elapsed)}";
        var encoding = counted.Where(job => job.State == JobState.Encoding).ToList();
        var processed = sessionWork + encoding.Sum(job => job.WorkDone);
        var remaining = counted.Where(job => job.State == JobState.Queued).Sum(job => job.Work)
                        + encoding.Sum(job => job.Work - job.WorkDone);
        var seconds = elapsed.Elapsed.TotalSeconds;
        // pixel-weighted rate from this session, so small files finishing first don't skew it low
        EtaText = seconds >= 15 && processed > 0
            ? Format.Duration(TimeSpan.FromSeconds(remaining / (processed / seconds)))
            : "estimating...";
    }

    static int SavedPercent(Job job) =>
        job.SourceBytes > 0 ? (int)Math.Round((1 - (double)job.OutputBytes / job.SourceBytes) * 100) : 0;

    static string DefaultDestination(string sourceFolder) =>
        string.IsNullOrWhiteSpace(sourceFolder) ? "" : Path.Combine(sourceFolder.TrimEnd('\\', '/'), "av1");

    static bool PathsEqual(string first, string second) =>
        string.Equals(first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
