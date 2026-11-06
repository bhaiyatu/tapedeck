using System.Text.Json;
using System.Text.Json.Serialization;
using Tapedeck.Services.Discord;

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

    /// <summary>
    /// When false, the Google session is kept in an in-memory (InPrivate) profile and is gone when
    /// Tapedeck closes; nothing about the sign-in is written to disk.
    /// </summary>
    public bool StaySignedIn { get; set; } = true;

    /// <summary>Look up synced lyrics on lrclib.net (sends the track's title/artist, nothing else).</summary>
    public bool UseLrclib { get; set; } = true;

    /// <summary>Show the playing track on your Discord profile. Off until you turn it on.</summary>
    public bool DiscordPresence { get; set; }

    /// <summary>What the Discord member list shows next to your name.</summary>
    public DiscordStatusDisplay DiscordStatus { get; set; } = DiscordStatusDisplay.App;

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
#if DEBUG
        // Debug builds can run against a separate profile (e.g. for tests) without touching your sign-in.
        Environment.GetEnvironmentVariable("TAPEDECK_DATA_DIR") is { Length: > 0 } dir ? dir :
#endif
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
