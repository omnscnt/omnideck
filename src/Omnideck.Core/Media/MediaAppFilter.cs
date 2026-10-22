namespace Omnideck.Core.Media;

public enum MediaFilterMode
{
    /// <summary>Every app's media session.</summary>
    All,
    /// <summary>Only the listed apps (allow list).</summary>
    OnlyListed,
    /// <summary>Every app except the listed ones (block list).</summary>
    ExceptListed,
}

/// <summary>Which apps' media sessions may be shown, by app name ("spotify", "chrome", "msedge").</summary>
public sealed record MediaAppFilter(MediaFilterMode Mode, IReadOnlyCollection<string> Apps)
{
    public static readonly MediaAppFilter AllApps = new(MediaFilterMode.All, []);

    public bool Allows(string appName) => Mode switch
    {
        MediaFilterMode.OnlyListed => Apps.Contains(appName, StringComparer.OrdinalIgnoreCase),
        MediaFilterMode.ExceptListed => !Apps.Contains(appName, StringComparer.OrdinalIgnoreCase),
        _ => true,
    };
}
