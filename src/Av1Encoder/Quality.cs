namespace Av1Encoder;

public enum QualityLevel { Low, Default, High }

// measured with VMAF on 1080p and 4K h264 sources from an RTX 4090; Default is the settled baseline
public sealed record QualityPreset(QualityLevel Level, int Cq, int MaxratePercent)
{
    public static readonly QualityPreset Low = new(QualityLevel.Low, 40, 50);
    public static readonly QualityPreset Default = new(QualityLevel.Default, 32, 60);
    public static readonly QualityPreset High = new(QualityLevel.High, 28, 80);

    public static QualityPreset For(QualityLevel level) => level switch
    {
        QualityLevel.Low => Low,
        QualityLevel.High => High,
        _ => Default,
    };
}
