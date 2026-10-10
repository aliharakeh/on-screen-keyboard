using System.IO;
using System.Text.Json;

namespace OnScreenKeyboard;

public class Settings
{
    public bool ShowNumberRow { get; set; } = true;
    public bool ShowFunctionRow { get; set; }
    public bool ShowNavRow { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnScreenKeyboard", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
