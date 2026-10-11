using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Av1Encoder;

/// <summary>app.log holds readable diagnostics; encodes.jsonl holds one JSON record per file and per run for metrics.</summary>
public static class AppLog
{
    const long AppLogMaxBytes = 5 * 1024 * 1024;
    const int AppLogArchives = 3;
    const long RecordsMaxBytes = 10 * 1024 * 1024;
    const int RecordsArchives = 5;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder => Path.Combine(Settings.Folder, "logs");

    public static void Info(string message) => WriteText("INFO ", message);

    public static void Warn(string message) => WriteText("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        WriteText("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public static void Record(object record) =>
        Append("encodes", ".jsonl", JsonSerializer.Serialize(record, record.GetType(), JsonOptions), RecordsMaxBytes, RecordsArchives);

    static void WriteText(string level, string message) =>
        Append("app", ".log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}", AppLogMaxBytes, AppLogArchives);

    static void Append(string name, string extension, string line, long maxBytes, int archives)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, name + extension);
                var file = new FileInfo(path);
                if (file.Exists && file.Length + Encoding.UTF8.GetByteCount(line + Environment.NewLine) > maxBytes)
                    Rotate(name, extension, archives);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // app.log -> app.1.log -> app.2.log ...; the oldest archive is deleted
    static void Rotate(string name, string extension, int archives)
    {
        string Archive(int number) => Path.Combine(Folder, $"{name}.{number}{extension}");
        File.Delete(Archive(archives));
        for (var number = archives - 1; number >= 1; number--)
            if (File.Exists(Archive(number)))
                File.Move(Archive(number), Archive(number + 1));
        File.Move(Path.Combine(Folder, name + extension), Archive(1));
    }
}

public sealed record FileRecord(
    DateTimeOffset Ts,
    string Run,
    string File,
    string Source,
    string Output,
    string Result,
    string? Reason,
    string Codec,
    int Width,
    int Height,
    double DurationSec,
    double Fps,
    long SourceBytes,
    long? OutputBytes,
    long? SavedBytes,
    double? SavedPct,
    double EncodeSec,
    double? Speed,
    int Workers,
    string Quality)
{
    public string Event => "file";
}

public sealed record RunRecord(
    DateTimeOffset Ts,
    string Run,
    string Result,
    string Source,
    string Destination,
    int Workers,
    string Quality,
    int Queued,
    int Done,
    int Failed,
    int Remaining,
    long SourceBytes,
    long OutputBytes,
    long SavedBytes,
    double ElapsedSec)
{
    public string Event => "run";
}
