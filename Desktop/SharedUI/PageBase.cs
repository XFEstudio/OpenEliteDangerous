using XFEEliteDangerous.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using CommunityToolkit.WinUI.Controls;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;

namespace XFEEliteDangerous.Desktop;

public abstract class DesktopPage : Page
{
    protected readonly StackPanel Body = new() { Spacing = 12 };
    protected readonly InfoBar Status = new() { IsOpen = false, IsClosable = true };
    protected readonly ProgressRing Spinner = new() { IsActive = false, Width = 24, Height = 24 };
    protected readonly CancellationTokenSource Lifetime = new();
    protected static ToggleSwitch Toggle(bool value = false) => new()
    {
        IsOn = value, OnContent = AppText.Get("Toggle.On"), OffContent = AppText.Get("Toggle.Off")
    };
    private int operations;
    protected DesktopPage(string title, string description)
    {
        Language = AppText.Language;
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(32, 28, 32, 32), MaxWidth = 1450, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        progress.Children.Add(Spinner);
        panel.Children.Add(progress);
        panel.Children.Add(Status);
        panel.Children.Add(Body);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Unloaded += (_, _) => Lifetime.Cancel();
    }
    protected async Task RunAsync(Func<Task> operation)
    {
        if (Lifetime.IsCancellationRequested) return;
        Interlocked.Increment(ref operations); Spinner.IsActive = true;
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DesktopContext.Log(error);
            // Refresh the local policy after a permission change; stale sessions are cleared by LocalClient.
            if (error is UnauthorizedAccessException && DesktopContext.State.IsLoggedIn && !DesktopContext.IsAdmin)
            {
                try { await DesktopContext.RefreshPermissionsAsync(); }
                catch (Exception refreshError) { DesktopContext.Log(refreshError); }
            }
            Status.Title = AppText.Get("PageBase.Text307");
            Status.Message = error.Message;
            Status.Severity = InfoBarSeverity.Error;
            Status.IsOpen = true;
        }
        finally { if (Interlocked.Decrement(ref operations) == 0) Spinner.IsActive = false; }
    }
    protected void Success(string message) { Status.Title = AppText.Get("PageBase.Text308"); Status.Message = message; Status.Severity = InfoBarSeverity.Success; Status.IsOpen = true; }
    protected static SettingsCard Card(string title, string description, object? content = null) => new() { Header = title, Description = description, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
    protected static Button Button(string text, Func<Task> action, bool accent = false)
    {
        var result = new Button { Content = text, MinHeight = 36 };
        if (accent) result.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        result.Click += async (_, _) => await action();
        return result;
    }
    protected Task<ContentDialogResult> ShowAsync(string title, UIElement content, string? primary = null)
        => DialogService.ShowAsync(new ContentDialog { Title = title, Content = content, PrimaryButtonText = primary ?? "", CloseButtonText = AppText.Get("AccountPage.Text080"), DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary });
    protected static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}

public static class DialogService
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await gate.WaitAsync();
        try
        {
            dialog.XamlRoot = ((FrameworkElement)DesktopContext.MainWindow.Content).XamlRoot;
            dialog.RequestedTheme = ((FrameworkElement)DesktopContext.MainWindow.Content).ActualTheme;
            return await dialog.ShowAsync();
        }
        finally { gate.Release(); }
    }
}
