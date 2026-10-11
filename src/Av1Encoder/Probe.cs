using System.Globalization;
using System.Text.Json;

namespace Av1Encoder;

public sealed record MediaInfo(
    string Codec,
    int Width,
    int Height,
    double Fps,
    long Bitrate,
    double Duration,
    int VideoIndex,
    bool HasExtraVideo,
    IReadOnlyList<int> TextSubtitleIndexes);

public static class Probe
{
    static readonly HashSet<string> ImageCodecs = ["png", "mjpeg", "bmp", "gif"];
    static readonly HashSet<string> TextSubtitleCodecs = ["subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text"];

    public static async Task<MediaInfo?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Tools.RunAsync(Tools.Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], cancellationToken);
            return Parse(result.Output, new FileInfo(path).Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static MediaInfo? Parse(string json, long size)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;

        JsonElement? video = null;
        JsonElement? imageVideo = null;
        var realVideoStreams = 0;
        var subtitles = new List<int>();
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = ReadString(stream, "codec_type");
                var codec = ReadString(stream, "codec_name");
                if (type == "video" && !IsAttachedPicture(stream))
                {
                    // image codecs are usually untagged thumbnails, but an MJPEG AVI has nothing else
                    if (ImageCodecs.Contains(codec))
                    {
                        imageVideo ??= stream;
                        continue;
                    }
                    realVideoStreams++;
                    video ??= stream;
                }
                else if (type == "subtitle" && TextSubtitleCodecs.Contains(codec))
                {
                    subtitles.Add(ReadInt(stream, "index"));
                }
            }
        }
        if ((video ?? imageVideo) is not JsonElement videoStream)
            return null;

        // container duration: the same value is measured on the output
        var duration = ReadDouble(format, "duration");
        if (duration <= 0)
            duration = ReadDouble(videoStream, "duration");
        if (duration <= 0)
            return null;

        var bitrate = ReadLong(videoStream, "bit_rate");
        if (bitrate <= 0)
        {
            var total = ReadLong(format, "bit_rate");
            bitrate = total > 0 ? Math.Max(total - 192_000, 0) : (long)(size * 8 / duration);
        }

        return new MediaInfo(
            ReadString(videoStream, "codec_name"),
            ReadInt(videoStream, "width"),
            ReadInt(videoStream, "height"),
            FrameRate(ReadString(videoStream, "avg_frame_rate")),
            bitrate,
            duration,
            ReadInt(videoStream, "index"),
            realVideoStreams > 1,
            subtitles);
    }

    static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) && ReadInt(disposition, "attached_pic") == 1;

    static double FrameRate(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
            return num / den;
        return 0;
    }

    static JsonElement? ReadProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    static string ReadString(JsonElement element, string name) =>
        ReadProperty(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

    static double ReadDouble(JsonElement element, string name) => ReadProperty(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } value => value.GetDouble(),
        { ValueKind: JsonValueKind.String } value when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    static long ReadLong(JsonElement element, string name) => (long)ReadDouble(element, name);

    static int ReadInt(JsonElement element, string name) => (int)ReadDouble(element, name);
}
