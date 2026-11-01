using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IssueLab.VoiceBench.App.Automation;

namespace IssueLab.VoiceBench.App;

public sealed partial class MainWindow : Window
{
    public DesktopAutomationHost? LocalControl { get; private set; }
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        App.Services.Session.ProjectActivated += Session_ProjectActivated;
        ShellNavigation.SelectedItem = ProjectsNavigationItem;
        ShowSurface("projects");
        ShellRoot.Loaded += ShellRoot_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void ShellRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ShellRoot.Loaded -= ShellRoot_Loaded;
        await App.Services.Session.RestoreMostRecentAsync();
        LocalControl = new DesktopAutomationHost(this);
        await LocalControl.StartAsync();
    }

    private void Session_ProjectActivated(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => NavigateTo("workbench"));

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        App.Services.Session.ProjectActivated -= Session_ProjectActivated;
        if (LocalControl is not null) await LocalControl.DisposeAsync();
        App.Services.Dispose();
    }

    private void ShellNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string destination)
        {
            ShowSurface(destination);
        }
    }

    public void NavigateTo(string destination)
    {
        ShellNavigation.SelectedItem = destination switch
        {
            "projects" => ProjectsNavigationItem,
            "workbench" => WorkbenchNavigationItem,
            "voice-lab" => VoiceLabNavigationItem,
            "assembly" => AssemblyNavigationItem,
            "settings" => SettingsNavigationItem,
            _ => ShellNavigation.SelectedItem,
        };
        ShowSurface(destination);
    }

    private void ShowSurface(string destination)
    {
        ProjectsSurface.Visibility = destination == "projects" ? Visibility.Visible : Visibility.Collapsed;
        WorkbenchSurface.Visibility = destination == "workbench" ? Visibility.Visible : Visibility.Collapsed;
        VoiceLabSurface.Visibility = destination == "voice-lab" ? Visibility.Visible : Visibility.Collapsed;
        AssemblySurface.Visibility = destination == "assembly" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSurface.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }
}
