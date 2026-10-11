using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Av1Encoder;

public static class Encoder
{
    const string Preset = "p4";

    public const string PartialSuffix = ".av1-partial.mp4";

    static readonly ConcurrentDictionary<Process, byte> Running = new();

    public static string PartialPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + PartialSuffix);

    static List<string> BuildArgs(Job job, string partialPath)
    {
        var info = job.Info!;
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-i", job.SourcePath,
            "-map", $"0:{info.VideoIndex}",
            // several real video streams means a bad mux; keep only the audio that belongs to the first one
            "-map", info.HasExtraVideo ? "0:a:0?" : "0:a?",
        };
        // MP4 only holds text subtitles as mov_text; image subtitles (PGS, VobSub) are dropped
        foreach (var index in info.TextSubtitleIndexes)
            args.AddRange(["-map", $"0:{index}"]);

        args.AddRange([
            "-fps_mode", "passthrough",
            "-c:v", "av1_nvenc", "-preset", Preset, "-tune", "hq", "-rc", "vbr",
            "-cq", job.Quality.Cq.ToString(CultureInfo.InvariantCulture), "-b:v", "0",
        ]);
        var maxrate = info.Bitrate * job.Quality.MaxratePercent / 100;
        if (maxrate > 0)
        {
            args.AddRange([
                "-maxrate", maxrate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", (maxrate * 2).ToString(CultureInfo.InvariantCulture),
            ]);
        }
        args.AddRange(["-c:a", "copy", "-c:s", "mov_text", "-movflags", "+faststart", "-y", partialPath]);
        return args;
    }

    public static async Task<string?> EncodeAsync(Job job, Action<double, string> onProgress, CancellationToken cancellationToken)
    {
        var partial = PartialPath(job.OutputPath);
        var args = BuildArgs(job, partial);
        var renamed = false;
        try
        {
            using var process = Process.Start(Tools.StartInfo(Tools.Ffmpeg, args))!;
            Running.TryAdd(process, 0);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                var seconds = 0.0;
                var speed = "";
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                {
                    var separator = line.IndexOf('=');
                    if (separator < 0)
                        continue;
                    var key = line[..separator];
                    var value = line[(separator + 1)..].Trim();
                    if (key == "out_time_us" && long.TryParse(value, out var microseconds))
                        seconds = Math.Max(microseconds / 1_000_000.0, 0);
                    else if (key == "speed")
                        speed = value == "N/A" ? "" : value;
                    else if (key == "progress")
                        onProgress(seconds, speed);
                }
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Tools.Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            finally
            {
                Running.TryRemove(process, out _);
            }

            var errors = await stderr;
            if (process.ExitCode != 0)
            {
                AppLog.Error($"ffmpeg exited with {process.ExitCode} on {job.FileName}{Environment.NewLine}"
                             + $"  command: \"{Tools.Ffmpeg}\" {string.Join(' ', args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg))}{Environment.NewLine}"
                             + $"  stderr: {Tail(errors, 20)}");
                return $"ffmpeg failed: {FirstError(errors)}";
            }

            var reason = await VerifyOutputAsync(job.Info!, job.SourceBytes, partial, CancellationToken.None);
            if (reason is not null)
                return reason;
            File.Move(partial, job.OutputPath, overwrite: true);
            renamed = true;
            return null;
        }
        finally
        {
            if (!renamed)
                await DeleteWithRetryAsync(partial);
        }
    }

    /// <summary>Returns null when the output is a usable encode of the source, otherwise the reason it is not.</summary>
    public static async Task<string?> VerifyOutputAsync(MediaInfo source, long sourceBytes, string outputPath, CancellationToken cancellationToken)
    {
        var output = await Probe.ReadAsync(outputPath, cancellationToken);
        if (output is null)
            return "output is unreadable";
        if (output.Codec != "av1")
            return $"output is {output.Codec}, expected av1";
        if (Math.Abs(output.Duration - source.Duration) > 0.5)
            return $"duration mismatch ({source.Duration:0.0}s source, {output.Duration:0.0}s output)";
        if (new FileInfo(outputPath).Length >= sourceBytes)
            return "output is not smaller than the source";
        return null;
    }

    /// <summary>Returns null when this GPU and driver can encode AV1 with NVENC, otherwise ffmpeg's error.</summary>
    public static async Task<string?> CheckAv1NvencAsync()
    {
        var result = await Tools.RunAsync(Tools.Ffmpeg, [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "color=black:size=256x256:duration=0.2",
            "-c:v", "av1_nvenc", "-f", "null", "-",
        ]);
        return result.ExitCode == 0 ? null : FirstError(result.Errors);
    }

    public static void KillAll()
    {
        foreach (var process in Running.Keys)
        {
            Tools.Kill(process);
            process.WaitForExit(3000);
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Windows can hold a killed process's file handle briefly after the process exits
    static async Task DeleteWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (TryDelete(path))
                return;
            await Task.Delay(100);
        }
        AppLog.Warn($"could not delete partial file {path}; it is removed on the next Start");
    }

    public static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (TryDelete(path))
                return;
            Thread.Sleep(100);
        }
        AppLog.Warn($"could not delete partial file {path}; it is removed on the next Start");
    }

    // ffmpeg's first error line names the cause; later lines are consequences of it
    static string FirstError(string text)
    {
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return firstLine is null ? "no error output" : Regex.Replace(firstLine, @"^\[[^\]]*\]\s*", "");
    }

    static string Tail(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(empty)" : string.Join(Environment.NewLine + "    ", lines.TakeLast(count));
    }
}
