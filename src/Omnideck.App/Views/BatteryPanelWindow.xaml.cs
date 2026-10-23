using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Omnideck.ViewModels;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace Omnideck.Views;

/// <summary>
/// The wireless batteries' plaque over the mixer. It never takes activation (the mixer closes when it
/// loses it), and the mixer's window moves, shows and hides it along with itself.
/// </summary>
public sealed partial class BatteryPanelWindow : Window
{
    /// <summary>Height in DIPs: a chip (28) and its padding.</summary>
    public const double HeightDip = 45;

    private readonly IntPtr _hwnd;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    public MixerViewModel ViewModel { get; }

    public IntPtr Handle => _hwnd;

    public BatteryPanelWindow(MixerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // Set here, not with x:Bind: a window that is never activated may not run its x:Bind setup.
        ChipsList.ItemsSource = viewModel.Batteries;
        _hwnd = WindowNative.GetWindowHandle(this);
        SetupBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false); // the mixer's look: rounded corners and shadow, no caption
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) => e.Cancel = true;

        // Clicks (a tooltip's hover) must not activate it: the mixer would lose activation and close.
        long ex = GetWindowLongPtr(_hwnd, -20 /* GWL_EXSTYLE */).ToInt64();
        SetWindowLongPtr(_hwnd, -20, new IntPtr(ex | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */));
        int disabled = 1;
        DwmSetWindowAttribute(_hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref disabled, sizeof(int));
    }

    public void Place(RectInt32 rect) => AppWindow.MoveAndResize(rect);

    public void Move(PointInt32 position) => AppWindow.Move(position);

    public void ShowQuietly() => AppWindow.Show(false);

    public void HideQuietly() => AppWindow.Hide();

    public void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
    }

    /// <summary>The mixer's material, so the two read as one family.</summary>
    private void SetupBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            return;
        }
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        _acrylic.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        RootGrid.ActualThemeChanged += (_, _) => SyncTheme();
        SyncTheme();
        Closed += (_, _) => _acrylic?.Dispose();
    }

    private void SyncTheme()
    {
        if (_backdropConfig == null) return;
        _backdropConfig.Theme = RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
