using System.Text.Json;
using System.Text.Json.Serialization;

namespace Av1Encoder;

public sealed class Settings
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public int Workers { get; set; } = 3;
    [JsonConverter(typeof(JsonStringEnumConverter<QualityLevel>))]
    public QualityLevel Quality { get; set; } = QualityLevel.Default;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Av1Encoder");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
        }
    }
}
