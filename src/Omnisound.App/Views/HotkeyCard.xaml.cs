using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Omnisound.ViewModels;

namespace Omnisound.Views;

/// <summary>
/// One hotkey in the settings list. The header shows the keys and summary; clicking it unfolds the
/// <see cref="HotkeyEditor"/> beneath with a height animation, so the cards below slide down with it.
/// </summary>
public sealed partial class HotkeyCard : UserControl
{
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(260);

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(HotkeyItemViewModel), typeof(HotkeyCard),
        new PropertyMetadata(null, (d, _) => ((HotkeyCard)d).OnItemChanged()));

    public HotkeyItemViewModel? Item
    {
        get => (HotkeyItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private HotkeyEditor? _editor;
    private Storyboard? _animation;
    private bool _pointerOver;

    public HotkeyCard()
    {
        InitializeComponent();
        Unloaded += (_, _) => _editor?.StopRecording();
    }

    /// <summary>Containers are recycled and re-created on reorder: restore the item's own state.</summary>
    private void OnItemChanged()
    {
        StopAnimation();
        DropEditor();
        Bindings.Update();
        if (Item is not { } item) return;

        if (item.PendingExpand)
        {
            item.PendingExpand = false;
            Apply(open: false, animate: false);
            // Wait for the first layout so the card has a width to measure the editor against.
            if (Root.IsLoaded) DispatcherQueue.TryEnqueue(() => ExpandNew(Root, new RoutedEventArgs()));
            else Root.Loaded += ExpandNew;
        }
        else
        {
            Apply(item.IsExpanded, animate: false);
        }
    }

    private void ExpandNew(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= ExpandNew;
        Apply(open: true, animate: true, record: true);
    }

    private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        // Clicks on the switch / delete button belong to them.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != Header; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase or ToggleSwitch) return;
        if (Item != null) Apply(!Item.IsExpanded, animate: true);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        _editor?.StopRecording();
        if (Item != null) Item.Owner.Remove(Item);
    }

    private void Apply(bool open, bool animate, bool record = false)
    {
        if (Item is not { } item) return;
        item.IsExpanded = open;
        StopAnimation();

        if (open)
        {
            if (_editor == null)
            {
                _editor = new HotkeyEditor(item);
                EditorHost.Content = _editor;
            }
            Body.Visibility = Visibility.Visible;
            if (record) _editor.StartRecording();
        }
        else
        {
            _editor?.StopRecording();
        }
        Header.CornerRadius = open ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7);

        if (!animate)
        {
            ChevronRotation.Angle = open ? 180 : 0;
            Body.MaxHeight = open ? double.PositiveInfinity : 0;
            if (!open) Collapsed(discardEmpty: false);
            return;
        }

        double from = open ? 0 : Body.ActualHeight, to = 0;
        if (open)
        {
            Body.MaxHeight = double.PositiveInfinity;
            Body.Measure(new Windows.Foundation.Size(Math.Max(0, Root.ActualWidth - 2), double.PositiveInfinity));
            to = Body.DesiredSize.Height;
        }
        Body.MaxHeight = from;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation { From = from, To = to, Duration = ExpandDuration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(height, Body);
        Storyboard.SetTargetProperty(height, "MaxHeight");
        var chevron = new DoubleAnimation { To = open ? 180 : 0, Duration = ExpandDuration, EasingFunction = ease };
        Storyboard.SetTarget(chevron, ChevronRotation);
        Storyboard.SetTargetProperty(chevron, "Angle");

        var sb = new Storyboard();
        sb.Children.Add(height);
        sb.Children.Add(chevron);
        sb.Completed += (_, _) =>
        {
            if (_animation != sb) return;
            _animation = null;
            sb.Stop(); // release the animated values so the local ones below take effect
            ChevronRotation.Angle = open ? 180 : 0;
            Body.MaxHeight = open ? double.PositiveInfinity : 0; // open: free to grow (e.g. app picker appears)
            if (open)
            {
                if (item.IsNew) Root.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
            else
            {
                Collapsed(discardEmpty: true);
            }
        };
        _animation = sb;
        sb.Begin();
    }

    private void Collapsed(bool discardEmpty)
    {
        Body.Visibility = Visibility.Collapsed;
        DropEditor(); // next open starts from the saved binding
        // A new hotkey closed without ever getting keys is discarded.
        if (discardEmpty && Item is { IsNew: true } item && item.Binding.Keys.IsEmpty) item.Owner.Remove(item);
    }

    private void DropEditor()
    {
        _editor?.StopRecording();
        _editor = null;
        EditorHost.Content = null;
    }

    private void StopAnimation()
    {
        if (_animation == null) return;
        var sb = _animation;
        _animation = null;
        sb.Stop();
    }

    private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        UpdateHeaderFill();
    }

    private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        UpdateHeaderFill();
    }

    private void UpdateHeaderFill() =>
        Header.Background = _pointerOver
            ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
