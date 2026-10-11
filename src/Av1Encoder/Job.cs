using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Av1Encoder;

public enum JobState { Queued, Encoding, Done, Failed, Skipped }

public sealed class Job : INotifyPropertyChanged
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required long SourceBytes { get; init; }
    public MediaInfo? Info { get; init; }

    public string FileName => Path.GetFileName(SourcePath);
    public string Resolution => Info is null ? "" : $"{Info.Width}x{Info.Height}";
    public string SourceSize => Format.Bytes(SourceBytes);

    /// <summary>Pixels to encode. Used to weight the ETA so 4K files count for more than 1080p ones.</summary>
    public double Work => Info is null ? 0 : Info.Duration * Info.Width * Info.Height * (Info.Fps > 0 ? Info.Fps : 30);

    public double WorkDone => State switch
    {
        JobState.Done or JobState.Failed => Work,
        JobState.Encoding => Work * Progress / 100,
        _ => 0,
    };

    JobState state;
    public JobState State { get => state; set => Set(ref state, value); }

    string status = "";
    public string Status { get => status; set => Set(ref status, value); }

    double progress;
    public double Progress { get => progress; set => Set(ref progress, value); }

    string speed = "";
    public string Speed { get => speed; set => Set(ref speed, value); }

    public long OutputBytes { get; set; }

    public DateTime Started { get; set; }
    public int WorkersAtStart { get; set; }
    public QualityPreset Quality { get; set; } = QualityPreset.Default;
    public TimeSpan Elapsed => Started == default ? TimeSpan.Zero : DateTime.UtcNow - Started;

    public void ReportProgress(double seconds, string currentSpeed)
    {
        Progress = Info is { Duration: > 0 } ? Math.Clamp(seconds / Info.Duration * 100, 0, 100) : 0;
        Speed = currentSpeed;
        Status = $"Encoding {Progress:0}%";
    }

    public void Requeue()
    {
        State = JobState.Queued;
        Status = "Queued";
        Progress = 0;
        Speed = "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
