using System.Text.Json;
using System.Text.Json.Serialization;
using Omnisound.Core.Hotkeys;
using Omnisound.Taskbar;

namespace Omnisound.Services;

public sealed class AppSettings
{
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    public TaskbarWidgetSettings TaskbarWidget { get; set; } = new();
}

/// <summary>Settings persisted as JSON in %LOCALAPPDATA%\omnisound\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Folder = Path.Combine(LocalAppData, "omnisound");
    private static readonly string LegacyFolder = Path.Combine(LocalAppData, "SoundDeck"); // before the rename

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    /// <summary>Moves settings saved under the old app name into the new folder (once).</summary>
    private static void MigrateLegacyFolder()
    {
        try
        {
            if (!Directory.Exists(Folder) && Directory.Exists(LegacyFolder)) Directory.Move(LegacyFolder, Folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        MigrateLegacyFolder();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable settings: keep a copy for inspection and start clean rather than failing to launch.
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, FilePath, overwrite: true); // atomic replace: never leaves a half-written file
    }
}
