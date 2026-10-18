namespace Omnisound.Services;

/// <summary>
/// Where omnisound is installed. Releases since 0.3.0: <c>&lt;root&gt;\omnisound.exe</c> (a small launcher)
/// and <c>&lt;root&gt;\app\</c> (this app with its runtime). Older releases and development builds are flat:
/// the root is the app folder itself.
/// </summary>
public static class InstallLayout
{
    public static string AppDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string Root { get; } = FindRoot();

    public static bool HasLauncher => !string.Equals(Root, AppDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>What to start omnisound with (autostart): the launcher when there is one.</summary>
    public static string LaunchExe => HasLauncher ? Path.Combine(Root, "omnisound.exe") : Environment.ProcessPath!;

    private static string FindRoot()
    {
        var parent = Path.GetDirectoryName(AppDirectory);
        return Path.GetFileName(AppDirectory).Equals("app", StringComparison.OrdinalIgnoreCase)
            && parent != null && File.Exists(Path.Combine(parent, "omnisound.exe"))
            ? parent
            : AppDirectory;
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
            if (relative.Equals("omnisound.exe", StringComparison.OrdinalIgnoreCase)) continue; // that's the launcher now
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
