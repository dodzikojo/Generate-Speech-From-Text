using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IssueLab.VoiceBench.App.Automation;
using IssueLab.VoiceBench.App.Services;

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
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
        ShellNavigation.SelectedItem = ProjectsNavigationItem;
        ShowSurface("projects");
        UpdateProjectMenu();
        ShellRoot.Loaded += ShellRoot_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void ShellRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ShellRoot.Loaded -= ShellRoot_Loaded;
        await App.Services.Session.RestoreMostRecentAsync();
        UpdateProjectMenu();
        LocalControl = new DesktopAutomationHost(this);
        await LocalControl.StartAsync();
    }

    private void Session_ProjectActivated(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => NavigateTo("workbench"));

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateProjectMenu);

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        App.Services.Session.ProjectActivated -= Session_ProjectActivated;
        App.Services.Session.ProjectChanged -= Session_ProjectChanged;
        if (LocalControl is not null) await LocalControl.DisposeAsync();
        App.Services.Dispose();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        ShellNavigation.IsPaneOpen = !ShellNavigation.IsPaneOpen;

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
            "audio" => AudioNavigationItem,
            "assembly" => AssemblyNavigationItem,
            "settings" => SettingsNavigationItem,
            _ => ShellNavigation.SelectedItem,
        };
        ShowSurface(destination);
    }

    /// <summary>Opens the Workbench and plays every approved take of a chapter in order.</summary>
    internal async Task PlayChapterAsync(string chapterId)
    {
        NavigateTo("workbench");
        await WorkbenchSurface.StartChapterPlaybackAsync(chapterId);
    }

    private void ShowSurface(string destination)
    {
        ProjectsSurface.Visibility = destination == "projects" ? Visibility.Visible : Visibility.Collapsed;
        WorkbenchSurface.Visibility = destination == "workbench" ? Visibility.Visible : Visibility.Collapsed;
        VoiceLabSurface.Visibility = destination == "voice-lab" ? Visibility.Visible : Visibility.Collapsed;
        AudioSurface.Visibility = destination == "audio" ? Visibility.Visible : Visibility.Collapsed;
        AssemblySurface.Visibility = destination == "assembly" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSurface.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (ShellNavigation.DisplayMode != NavigationViewDisplayMode.Expanded) ShellNavigation.IsPaneOpen = false;
    }

    private void UpdateProjectMenu()
    {
        bool hasProject = App.Services.Session.HasProject;
        string? name = App.Services.Session.Project?.Name;
        ProjectMenuText.Text = hasProject && !string.IsNullOrWhiteSpace(name) ? name : "No project open";
        AppTitleBar.Subtitle = hasProject ? string.Empty : "Narration studio";
        ProjectPathItem.Text = hasProject ? App.Services.Session.ProjectRoot ?? string.Empty : "Open or create a project to see its folders";
        foreach (MenuFlyoutItem item in new[]
                 {
                     OpenProjectFolderItem, OpenGeneratedAudioItem, OpenMastersItem,
                     OpenScriptsItem, OpenExportsItem, OpenReportsItem, CopyProjectPathItem,
                 })
        {
            item.IsEnabled = hasProject;
        }
    }

    private void ProjectMenu_Opening(object sender, object e) => UpdateProjectMenu();

    private async void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || !Enum.TryParse(tag, out ProjectFolder folder)) return;
        try
        {
            await ShellLauncher.OpenProjectFolderAsync(folder);
        }
        catch (Exception exception)
        {
            ShowShellError($"{ProjectLocations.DisplayName(folder)} could not be opened", exception.Message);
        }
    }

    private void CopyProjectPath_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Session.ProjectRoot is not string root) return;
        try { ShellLauncher.CopyPath(root); }
        catch (InvalidOperationException exception) { ShowShellError("Project path could not be copied", exception.Message); }
    }

    private void SwitchProject_Click(object sender, RoutedEventArgs e) => NavigateTo("projects");

    private void ShowShellError(string title, string message)
    {
        ShellInfoBar.Title = title;
        ShellInfoBar.Message = message;
        ShellInfoBar.Severity = InfoBarSeverity.Error;
        ShellInfoBar.IsOpen = true;
    }
}
