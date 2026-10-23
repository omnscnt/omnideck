using System.Text.Json;
using System.Text.Json.Serialization;
using Omnideck.Core.Hotkeys;
using Omnideck.Osd;
using Omnideck.Taskbar;

namespace Omnideck.Services;

public sealed class AppSettings
{
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    public TaskbarWidgetSettings TaskbarWidget { get; set; } = new();

    public VolumeOsdSettings VolumeOsd { get; set; } = new();

    /// <summary>App sliders follow loudness (decibels) instead of the raw volume value, like EarTrumpet.</summary>
    public bool LogarithmicVolume { get; set; }

    /// <summary>The mixer shows each app's output device by name (wider), rather than as an icon.</summary>
    public bool MixerExpanded { get; set; }

    /// <summary>Each app in the mixer gets an output device button (Windows' per-app output).</summary>
    public bool AppOutputs { get; set; } = true;

    /// <summary>How wide the output buttons get in the wide mixer, in DIPs.</summary>
    public int ExpandedOutputWidth { get; set; } = 128;

    /// <summary>Device names with the adapter in brackets ("Headphones (CS43131 HIFI Audio)"), not just "Headphones".</summary>
    public bool FullDeviceNames { get; set; }

    public DeviceSettings Devices { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();
}

public sealed class UpdateSettings
{
    /// <summary>Look for a new release a minute after start and then once a day.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>The release already announced in the tray, so each one is announced once.</summary>
    public string? AnnouncedVersion { get; set; }
}

/// <summary>Settings persisted as JSON in %LOCALAPPDATA%\omnideck\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Folder = Path.Combine(LocalAppData, "omnideck");
    // Earlier names, newest first: omnisound (until 0.3), SoundDeck (the first builds).
    private static readonly string[] LegacyFolders = [Path.Combine(LocalAppData, "omnisound"), Path.Combine(LocalAppData, "SoundDeck")];

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    /// <summary>Moves settings saved under an earlier app name into the new folder (once).</summary>
    private static void MigrateLegacyFolder()
    {
        try
        {
            if (Directory.Exists(Folder)) return;
            var legacy = LegacyFolders.FirstOrDefault(Directory.Exists);
            if (legacy != null) Directory.Move(legacy, Folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The previous settings.json, kept by every save: the fallback when the file can't be read.</summary>
    private static string BackupPath => FilePath + ".bak";

    public static AppSettings Load()
    {
        MigrateLegacyFolder();
        if (!File.Exists(FilePath))
        {
            if (!File.Exists(BackupPath)) return new AppSettings(); // first run
            CrashLog.Write("settings load", new FileNotFoundException("settings.json is missing; using the backup", FilePath));
            return TryRead(BackupPath) ?? new AppSettings();
        }

        Exception? error = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
            catch (IOException ex)
            {
                error = ex; // held open for a moment (antivirus scan, an exiting instance): try again shortly
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                error = ex;
                break;
            }
        }

        // Unreadable: log it, keep a copy for inspection, and fall back to the last good version rather than
        // starting empty (an empty start would be saved over the user's settings on the next change).
        CrashLog.Write("settings load", error);
        try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
        return TryRead(BackupPath) ?? new AppSettings();
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("settings backup", ex);
            return null;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        try { if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, overwrite: true); } catch (IOException) { }
        File.Move(temp, FilePath, overwrite: true); // atomic replace: never leaves a half-written file
    }
}

/// <summary>Wireless devices' batteries: whether to read them, where to show them, when to warn.</summary>
public sealed class DeviceSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>A plaque above the mixer with each device's level.</summary>
    public bool ShowInMixer { get; set; } = true;

    /// <summary>A tray notice once a device drops to <see cref="LowThreshold"/> % (again only after it charged).</summary>
    public bool LowBatteryNotice { get; set; } = true;

    public int LowThreshold { get; set; } = 15;

    /// <summary>How often to read them, in minutes.</summary>
    public int PollMinutes { get; set; } = 5;

    public const int MinThreshold = 5, MaxThreshold = 50, MinPoll = 1, MaxPoll = 30;

    /// <summary>Devices left off the plaque and out of the low battery notice, by name.</summary>
    public List<string> HiddenDevices { get; set; } = [];

    public DeviceSettings Clone()
    {
        var copy = (DeviceSettings)MemberwiseClone();
        copy.HiddenDevices = [.. HiddenDevices];
        return copy;
    }
}
