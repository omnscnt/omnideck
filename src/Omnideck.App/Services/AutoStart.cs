using Microsoft.Win32;

namespace Omnideck.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights, works for unpackaged apps).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "omnideck";
    private static readonly string[] LegacyValueNames = ["omnisound", "SoundDeck"]; // earlier names of the app
    public const string LaunchArgument = "--autostart";

    private static string Command => $"\"{InstallLayout.LaunchExe}\" {LaunchArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            // Either this exe or the launcher in front of it.
            return LegacyValueNames.Prepend(ValueName).Any(name => key?.GetValue(name) is string value
                && (value.Contains(InstallLayout.LaunchExe, StringComparison.OrdinalIgnoreCase)
                    || value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// Keeps "Start with Windows" on across the rename and moves: an entry under the old name, or one
    /// pointing at an exe that no longer exists, is rewritten for this exe.
    /// </summary>
    public static void Migrate()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        bool legacy = LegacyValueNames.Any(name => key?.GetValue(name) != null);
        bool stale = key?.GetValue(ValueName) is string value && !File.Exists(value.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim());
        if (legacy || stale) SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        foreach (var name in LegacyValueNames) key.DeleteValue(name, throwOnMissingValue: false);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
