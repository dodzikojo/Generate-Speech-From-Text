using System.Collections.ObjectModel;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.App.Layout;
using IssueLab.VoiceBench.Infrastructure.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class ProjectsView : UserControl
{
    private int _recentProjectsRefreshVersion;

    public ObservableCollection<RecentProjectListItem> RecentProjects { get; } = [];

    public ProjectsView()
    {
        InitializeComponent();
        Loaded += ProjectsView_Loaded;
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    private void ApplyResponsiveState(double width)
    {
        bool isWide = width >= 960;
        VisualStateManager.GoToState(
            this,
            isWide ? "WideProjects" : width >= 560 ? "MediumProjects" : "NarrowProjects",
            false);
        StudioPageFrame.Resize(ProjectsRoot, width, 1180, isWide ? 32 : 16);
    }

    private async void ProjectsView_Loaded(object sender, RoutedEventArgs e) =>
        await UpdateRecentProjectsAsync();

    private async void Session_ProjectChanged(object? sender, EventArgs e) =>
        await UpdateRecentProjectsAsync();

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        NewProjectDialog.XamlRoot = XamlRoot;
        await NewProjectDialog.ShowAsync();
    }

    private async void BrowseProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await PickFolderAsync();
            if (folder is not null)
            {
                ProjectFolderBox.Text = folder;
                if (string.IsNullOrWhiteSpace(ProjectNameBox.Text))
                {
                    string folderName = new DirectoryInfo(folder).Name;
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        ProjectNameBox.Text = folderName;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Folder picker could not open", exception.Message);
        }
    }

    private async void NewProjectDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(ProjectNameBox.Text) || string.IsNullOrWhiteSpace(ProjectFolderBox.Text))
        {
            args.Cancel = true;
            ProjectStatusBar.Severity = InfoBarSeverity.Warning;
            ProjectStatusBar.Title = "Choose a project name and folder";
            ProjectStatusBar.Message = "Both fields are required before VoiceBench can create the project structure.";
            ProjectStatusBar.IsOpen = true;
            return;
        }

        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            await App.Services.Session.CreateAsync(ProjectFolderBox.Text.Trim(), ProjectNameBox.Text.Trim());
            ShowStatus(
                InfoBarSeverity.Success,
                "Project created",
                $"VoiceBench created the movable project at '{App.Services.Session.ProjectRoot}'.");
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            ShowStatus(InfoBarSeverity.Error, "Project could not be created", exception.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await PickFolderAsync();
            if (folder is null)
            {
                return;
            }

            await App.Services.Session.OpenAsync(folder);
            ShowStatus(InfoBarSeverity.Success, "Project opened", folder);
        }
        catch (Exception exception)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                "That folder is not a VoiceBench project",
                $"Choose a folder containing voicebench.project.json. {exception.Message}");
        }
    }

    private async void RecentProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string projectRoot })
        {
            return;
        }

        try
        {
            await App.Services.Session.OpenAsync(projectRoot);
            ShowStatus(InfoBarSeverity.Success, "Project opened", projectRoot);
        }
        catch (Exception exception)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                "Project could not be opened",
                $"The project may have moved or become unreadable. {exception.Message}");
        }
    }

    private static async Task<string?> PickFolderAsync()
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task UpdateRecentProjectsAsync()
    {
        int refreshVersion = ++_recentProjectsRefreshVersion;
        IReadOnlyList<RecentProjectEntry> entries;
        try
        {
            entries = await App.Services.RecentProjects.GetAsync();
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Recent projects could not be loaded", exception.Message);
            return;
        }

        List<RecentProjectListItem> items = [];
        foreach (RecentProjectEntry entry in entries)
        {
            string manifestPath = Path.Combine(entry.ProjectRoot, VoiceBenchProjectLayout.ManifestFileName);
            VoiceBenchProjectDocument? project = null;
            if (File.Exists(manifestPath))
            {
                try
                {
                    project = await App.Services.Projects.LoadAsync(entry.ProjectRoot);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);
                }
            }

            bool isAvailable = project is not null;
            bool isActive = isAvailable && string.Equals(
                App.Services.Session.ProjectRoot,
                entry.ProjectRoot,
                StringComparison.OrdinalIgnoreCase);
            TimeSpan renderedDuration = project is null
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(project.Segments.Sum(segment =>
                    segment.RenderHistory.OrderByDescending(render => render.TakeNumber).FirstOrDefault()?.Duration.Ticks ?? 0));
            string lastOpened = entry.LastOpenedAt.ToLocalTime().ToString(
                "g",
                System.Globalization.CultureInfo.CurrentCulture);
            string detailLine = project is null
                ? $"Folder moved, deleted, or unreadable · last opened {lastOpened}"
                : $"{project.Segments.Count} segments · " +
                  $"{project.Segments.Count(segment => segment.Status == SegmentStatus.Approved)} approved · " +
                  $"{(int)renderedDuration.TotalMinutes:00}:{renderedDuration.Seconds:00} rendered · " +
                  $"opened {lastOpened}";

            items.Add(new RecentProjectListItem(
                project?.Name ?? entry.Name,
                entry.ProjectRoot,
                detailLine,
                isActive ? "● ACTIVE" : isAvailable ? "AVAILABLE" : "MISSING FOLDER",
                isActive ? "Continue" : isAvailable ? "Open" : "Missing",
                isAvailable,
                isAvailable
                    ? $"{(isActive ? "Continue" : "Open")} {project?.Name ?? entry.Name}"
                    : $"{entry.Name} is unavailable"));
        }

        if (refreshVersion != _recentProjectsRefreshVersion)
        {
            return;
        }

        RecentProjects.Clear();
        foreach (RecentProjectListItem item in items)
        {
            RecentProjects.Add(item);
        }

        RecentProjectCountText.Text = $"{items.Count} SAVED";
        RecentProjectsEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentProjectsList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        ProjectStatusBar.Severity = severity;
        ProjectStatusBar.Title = title;
        ProjectStatusBar.Message = message;
        ProjectStatusBar.IsOpen = true;
    }
}

public sealed class RecentProjectListItem(
    string name,
    string projectRoot,
    string detailLine,
    string statusLabel,
    string actionText,
    bool isAvailable,
    string automationName)
{
    public string Name { get; set; } = name;

    public string ProjectRoot { get; set; } = projectRoot;

    public string DetailLine { get; set; } = detailLine;

    public string StatusLabel { get; set; } = statusLabel;

    public string ActionText { get; set; } = actionText;

    public bool IsAvailable { get; set; } = isAvailable;

    public string AutomationName { get; set; } = automationName;
}
