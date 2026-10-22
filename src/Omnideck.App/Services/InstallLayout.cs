namespace Omnideck.Services;

/// <summary>
/// Where omnideck is installed. Releases since 0.3.0: <c>&lt;root&gt;\omnideck.exe</c> (a small launcher)
/// and <c>&lt;root&gt;\app\</c> (this app with its runtime); before the rename (0.4.0) the launcher was
/// omnisound.exe. Older releases and development builds are flat: the root is the app folder itself.
/// </summary>
public static class InstallLayout
{
    public const string LauncherName = "omnideck.exe";
    private const string LegacyLauncherName = "omnisound.exe";

    public static string AppDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string Root { get; } = FindRoot();

    public static bool HasLauncher => !string.Equals(Root, AppDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>What to start omnideck with (autostart): the launcher when there is one.</summary>
    public static string LaunchExe
    {
        get
        {
            if (!HasLauncher) return Environment.ProcessPath!;
            string launcher = Path.Combine(Root, LauncherName);
            return File.Exists(launcher) ? launcher : Path.Combine(Root, LegacyLauncherName);
        }
    }

    private static string FindRoot()
    {
        var parent = Path.GetDirectoryName(AppDirectory);
        return Path.GetFileName(AppDirectory).Equals("app", StringComparison.OrdinalIgnoreCase)
            && parent != null
            && (File.Exists(Path.Combine(parent, LauncherName)) || File.Exists(Path.Combine(parent, LegacyLauncherName)))
            ? parent
            : AppDirectory;
    }

    /// <summary>
    /// After the update from omnisound: 0.3.x's update script only knows omnisound.exe, so the package
    /// carries the new launcher under that name too and the script puts it in the root. Give it its new
    /// name and drop the old one, so the folder is again just omnideck.exe and app\. Runs before autostart
    /// is migrated, which points at the launcher.
    /// </summary>
    public static void RenameLegacyLauncher()
    {
        if (!HasLauncher) return;
        string legacy = Path.Combine(Root, LegacyLauncherName), launcher = Path.Combine(Root, LauncherName);
        try
        {
            if (!File.Exists(legacy)) return;
            if (!File.Exists(launcher)) File.Copy(legacy, launcher);
            File.Delete(legacy);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// After an update from a flat release into the launcher layout: the old copies of this app's files
    /// still sit in the root. Removes exactly those (same relative path as a file in app\), nothing else,
    /// so a root shared with other things is safe.
    /// </summary>
    public static void RemoveFlatLeftovers()
    {
        if (!HasLauncher) return;
        foreach (var file in Directory.EnumerateFiles(AppDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(AppDirectory, file);
            if (relative.Equals(LauncherName, StringComparison.OrdinalIgnoreCase)) continue; // that's the launcher now
            string old = Path.Combine(Root, relative);
            try { if (File.Exists(old)) File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var dir in Directory.EnumerateDirectories(AppDirectory))
        {
            string old = Path.Combine(Root, Path.GetFileName(dir));
            try { if (Directory.Exists(old) && !Directory.EnumerateFileSystemEntries(old, "*", SearchOption.AllDirectories).Any(File.Exists)) Directory.Delete(old, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
