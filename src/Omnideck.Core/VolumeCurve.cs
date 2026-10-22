namespace Omnideck.Core;

/// <summary>
/// How a slider position maps to an app's volume. Linear by default (what the Windows mixer shows); in
/// logarithmic mode the position follows loudness as it is heard (decibels), like EarTrumpet's option:
/// the lower half of the slider is no longer crammed into a few audible steps. Only app sessions use it:
/// Windows already scales the device volume this way.
/// </summary>
public static class VolumeCurve
{
    /// <summary>Position 0..1 covers this many decibels below full volume; position 0 is silence.</summary>
    private const double RangeDb = 50;

    public static bool Logarithmic { get; set; }

    /// <summary>Slider position (0..1) → volume scalar (0..1).</summary>
    public static float ToScalar(float position)
    {
        position = Math.Clamp(position, 0f, 1f);
        if (!Logarithmic || position <= 0) return position;
        return (float)Math.Pow(10, (position - 1) * RangeDb / 20);
    }

    /// <summary>Volume scalar (0..1) → slider position (0..1).</summary>
    public static float ToPosition(float scalar)
    {
        scalar = Math.Clamp(scalar, 0f, 1f);
        if (!Logarithmic || scalar <= 0) return scalar;
        return (float)Math.Clamp(1 + 20 * Math.Log10(scalar) / RangeDb, 0, 1);
    }
}
