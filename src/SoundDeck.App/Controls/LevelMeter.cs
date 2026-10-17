using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SoundDeck.Controls;

/// <summary>
/// Small vertical audio level bar: a dim track with a fill rising from the bottom.
/// <see cref="Level"/> is 0..1 of the full height. Colours come from the slider track brushes
/// (Track = <see cref="Panel.Background"/>, fill = <see cref="FillBrush"/>) so both read as one system.
/// </summary>
public sealed class LevelMeter : Grid
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter), new PropertyMetadata(0.0, (d, _) => ((LevelMeter)d).UpdateFill()));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LevelMeter), new PropertyMetadata(null, (d, e) => ((LevelMeter)d)._fill.Background = (Brush)e.NewValue));

    private readonly Border _fill = new() { VerticalAlignment = VerticalAlignment.Bottom, Height = 0 };

    public LevelMeter()
    {
        Children.Add(_fill);
        SizeChanged += (_, _) => UpdateFill();
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    private void UpdateFill()
    {
        _fill.CornerRadius = CornerRadius;
        double height = Math.Clamp(Level, 0, 1) * ActualHeight;
        _fill.Height = height < 1 ? 0 : height;
    }
}
