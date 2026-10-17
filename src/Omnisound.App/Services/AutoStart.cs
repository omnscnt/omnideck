using Microsoft.Win32;

namespace Omnisound.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights, works for unpackaged apps).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "omnisound";
    private const string LegacyValueName = "SoundDeck"; // before the rename
    public const string LaunchArgument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {LaunchArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return new[] { ValueName, LegacyValueName }.Any(name => key?.GetValue(name) is string value
                && value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Keeps "Start with Windows" on across the rename and moves: an entry under the old name, or one
    /// pointing at an exe that no longer exists, is rewritten for this exe.
    /// </summary>
    public static void Migrate()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        bool legacy = key?.GetValue(LegacyValueName) != null;
        bool stale = key?.GetValue(ValueName) is string value && !File.Exists(value.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim());
        if (legacy || stale) SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
