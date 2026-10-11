namespace Av1Encoder;

public static class Format
{
    // 1024-based to match the sizes File Explorer shows
    public static string Bytes(double bytes)
    {
        const double Gb = 1024.0 * 1024 * 1024;
        const double Mb = 1024.0 * 1024;
        return Math.Abs(bytes) >= Gb ? $"{bytes / Gb:0.00} GB" : $"{bytes / Mb:0} MB";
    }

    public static string Duration(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        if (time.TotalMinutes >= 1)
            return $"{time.Minutes}m {time.Seconds:00}s";
        return $"{Math.Max(time.Seconds, 0)}s";
    }
}
