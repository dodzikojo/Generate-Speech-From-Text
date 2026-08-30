using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IssueLab.VoiceBench.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        App.Services.Session.ProjectActivated += Session_ProjectActivated;
        ShellNavigation.SelectedItem = ProjectsNavigationItem;
        ShowSurface("projects");
        ShellRoot.Loaded += ShellRoot_Loaded;
    }

    private async void ShellRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ShellRoot.Loaded -= ShellRoot_Loaded;
        await App.Services.Session.RestoreMostRecentAsync();
    }

    private void Session_ProjectActivated(object? sender, EventArgs e) => NavigateTo("workbench");

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
        SettingsSurface.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }
}
