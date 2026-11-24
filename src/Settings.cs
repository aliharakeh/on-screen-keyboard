using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnScreenKeyboard;

public enum DockSide { Left, Top, Right, Bottom }

public class Settings
{
    public bool ShowNumberRow { get; set; } = true;
    public bool ShowFunctionRow { get; set; }
    public bool ShowNavigationPanel { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public bool WordPredictions { get; set; } = true;
    public bool LearnWords { get; set; } = true;
    public bool SpellCheck { get; set; } = true;
    public bool AutoCorrect { get; set; }
    /// <summary>Key combination the 🎤 button sends, e.g. "Win+H" (Windows voice typing) or an external app's shortcut.</summary>
    public string VoiceShortcut { get; set; } = OnScreenKeyboard.Shortcut.WindowsVoiceTyping;
    /// <summary>The — button shows a small toolbar on a screen edge instead of minimizing to the taskbar.</summary>
    public bool DockOnMinimize { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<DockSide>))]
    public DockSide DockSide { get; set; } = DockSide.Bottom;
    /// <summary>Where the docked toolbar sits along each edge: 0 = start, 0.5 = centered, 1 = end.</summary>
    public Dictionary<DockSide, double> DockOffsets { get; set; } = [];
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
