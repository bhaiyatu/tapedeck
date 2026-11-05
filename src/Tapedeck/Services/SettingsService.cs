using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tapedeck.Services;

public enum RepeatMode
{
    Off,
    All,
    One,
}

public sealed class AppSettings
{
    public double Volume { get; set; } = 0.8;
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public string Theme { get; set; } = "Default";
    public string? SidePanel { get; set; }
    public bool ShowNotifications { get; set; }
    public int WindowWidth { get; set; } = 1320;
    public int WindowHeight { get; set; } = 860;
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Persists small user preferences to %LOCALAPPDATA%\Tapedeck\settings.json.</summary>
public sealed class SettingsService
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tapedeck");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    private readonly System.Threading.Timer _saveTimer;

    public AppSettings Current { get; }

    public SettingsService()
    {
        Directory.CreateDirectory(DataFolder);
        Current = Load();
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file shouldn't stop the app from starting.
        }
        return new AppSettings();
    }

    /// <summary>Debounced save, so dragging the volume slider doesn't hammer the disk.</summary>
    public void Save() => _saveTimer.Change(500, Timeout.Infinite);

    public void SaveNow()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch
        {
            // Best effort.
        }
    }
}
