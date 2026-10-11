using System.Diagnostics;
using System.Text;

namespace Av1Encoder;

public sealed record ProcessResult(int ExitCode, string Output, string Errors);

public static class Tools
{
    public static string Ffmpeg { get; private set; } = "";
    public static string Ffprobe { get; private set; } = "";

    public static bool Locate()
    {
        foreach (var directory in CandidateDirs())
        {
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                Ffmpeg = ffmpeg;
                Ffprobe = ffprobe;
                return true;
            }
        }
        return false;
    }

    static IEnumerable<string> CandidateDirs()
    {
        yield return AppContext.BaseDirectory;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return directory.Trim('"');

        // winget installs FFmpeg here; a session started before the install has a stale PATH
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
        yield return Path.Combine(winget, "Links");
        var packages = Path.Combine(winget, "Packages");
        if (!Directory.Exists(packages))
            yield break;
        foreach (var package in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
            foreach (var build in Directory.EnumerateDirectories(package).OrderDescending())
                yield return Path.Combine(build, "bin");
    }

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(StartInfo(exe, args))!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
