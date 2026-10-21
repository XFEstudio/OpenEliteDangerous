using Microsoft.UI.Xaml;
using XFEEliteDangerous.Desktop;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace XFEEliteDangerous.Desktop;

public partial class App : Application
{
    public App()
    {
        XFEEliteDangerous.Client.AppText.ConfigureArguments(Environment.GetCommandLineArgs()[1..], SystemProfile.Language);
        DesktopContext.Initialize();
        InitializeComponent();
        UnhandledException += (_, args) => DesktopContext.Log(args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { DesktopContext.Log(args.Exception); args.SetObserved(); };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        DesktopContext.MainWindow = window;
        DesktopContext.Dispatcher = window.DispatcherQueue;
        AppThemeHelper.MainWindow = window;
        AppThemeHelper.Theme = SystemProfile.Theme;
        if (Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith("--verify-desktop=", StringComparison.Ordinal)) is string verify)
        {
            window.Content = new Microsoft.UI.Xaml.Controls.Grid();
            await DesktopVerification.RunAsync(verify["--verify-desktop=".Length..]);
            window.Close();
            Exit();
            return;
        }
        try{await DesktopContext.Local.ConnectLocalOwnerAsync();}catch(Exception error){DesktopContext.Log(error);}
        window.Content = new AppShellPage();
        AppThemeHelper.ChangeTheme(SystemProfile.Theme);
        window.Closed += (_, _) => { DesktopContext.Persist(); DesktopContext.Local.Dispose(); };
        window.Activate();
    }
}
