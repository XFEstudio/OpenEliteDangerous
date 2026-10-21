using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using XFEEliteDangerous.Desktop;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace XFEEliteDangerous.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly UISettings settings = new();
    public MainWindow()
    {
        InitializeComponent();
        Title = DesktopContext.State.ServerName + " · " + DesktopContext.Role;
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var scale = Math.Max(GetDpiForWindow(WindowNative.GetWindowHandle(this)), 96) / 96d;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1280 * scale), Math.Max(1, work.Width - (int)(48 * scale)));
        var height = Math.Min((int)(900 * scale), Math.Max(1, work.Height - (int)(48 * scale)));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        settings.ColorValuesChanged += Changed;
        DesktopContext.State.PropertyChanged += StateChanged;
        Closed += (_, _) => { settings.ColorValuesChanged -= Changed; DesktopContext.State.PropertyChanged -= StateChanged; };
    }
    private void Changed(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => AppThemeHelper.ChangeTheme(SystemProfile.Theme));
    private void StateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DesktopState.ServerName) or nameof(DesktopState.RoleLabel)) Title = DesktopContext.State.ServerName + " · " + DesktopContext.Role;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint hwnd);
}
