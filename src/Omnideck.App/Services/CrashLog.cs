namespace Omnideck.Services;

/// <summary>Appends unhandled exceptions to %LOCALAPPDATA%\omnideck\crash.log.</summary>
public static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "crash.log");

    public static void Write(string where, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where})\n{exception}\n\n");
        }
        catch
        {
            // Nothing sensible left to do while crashing.
        }
    }
}
