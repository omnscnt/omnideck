using Omnisound.Core.Media;

namespace Omnisound.Taskbar;

public enum WidgetPosition
{
    /// <summary>Left end of the taskbar (centred taskbars only: Start lives there otherwise).</summary>
    Left,
    /// <summary>Just before the centred app icons.</summary>
    BeforeIcons,
    /// <summary>Just after the app icons.</summary>
    AfterIcons,
    /// <summary>Right end, next to the notification area.</summary>
    Right,
}

public enum WidgetSize { Small, Medium, Large }

/// <summary>Everything the taskbar widget can be configured with (saved in settings.json).</summary>
public sealed class TaskbarWidgetSettings
{
    public bool Enabled { get; set; } = true;
    public WidgetPosition Position { get; set; } = WidgetPosition.Left;

    /// <summary>Extra horizontal shift in pixels (negative = left).</summary>
    public int Offset { get; set; }

    public WidgetSize Size { get; set; } = WidgetSize.Medium;

    /// <summary>Width of the title / artist column in DIPs; the widget keeps it for every track.</summary>
    public int MaxTextWidth { get; set; } = 200;

    public bool ShowControls { get; set; } = true;

    /// <summary>How much the widget fades while paused, in percent (0 = not at all). Hovering lifts it.</summary>
    public int PausedDim { get; set; } = 40;

    /// <summary>Titles too long for the column scroll instead of ending in an ellipsis.</summary>
    public bool ScrollTitles { get; set; } = true;

    /// <summary>Blurred cover art behind the widget.</summary>
    public bool CoverBackdrop { get; set; } = true;

    public MediaFilterMode FilterMode { get; set; } = MediaFilterMode.All;
    public List<string> FilterApps { get; set; } = [];

    /// <summary>Exe paths of listed apps, remembered so their icons show even when they aren't running.</summary>
    public Dictionary<string, string> AppPaths { get; set; } = [];

    public MediaAppFilter ToFilter() => new(FilterMode, FilterApps.ToList());

    public TaskbarWidgetSettings Clone()
    {
        var copy = (TaskbarWidgetSettings)MemberwiseClone();
        copy.FilterApps = [.. FilterApps];
        copy.AppPaths = new Dictionary<string, string>(AppPaths);
        return copy;
    }
}
