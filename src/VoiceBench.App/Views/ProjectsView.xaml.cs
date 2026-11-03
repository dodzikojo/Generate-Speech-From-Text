using System.Collections.ObjectModel;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.App.Layout;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class ProjectsView : UserControl
{
    private int _recentProjectsRefreshVersion;

    public ObservableCollection<RecentProjectListItem> RecentProjects { get; } = [];

    /// <summary>The recent projects that match the filter box.</summary>
    public ObservableCollection<RecentProjectListItem> VisibleProjects { get; } = [];

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

    private void Session_ProjectChanged(object? sender, EventArgs e)
    {
        bool queued = DispatcherQueue.TryEnqueue(async () => await UpdateRecentProjectsAsync());
        if (!queued)
        {
            System.Diagnostics.Debug.WriteLine(
                "The Projects view dispatcher was shutting down before the recent-project list could refresh.");
        }
    }

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
        if (sender is not FrameworkElement { Tag: string projectRoot })
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
            DurationProjectionSummary expectedTiming = project is null
                ? new DurationProjectionSummary(TimeSpan.Zero, 0, 0)
                : await ProjectDurationAsync(entry.ProjectRoot, project);
            string lastOpened = entry.LastOpenedAt.ToLocalTime().ToString(
                "g",
                System.Globalization.CultureInfo.CurrentCulture);
            string detailLine = project is null
                ? $"Folder moved, deleted, or unreadable · last opened {lastOpened}"
                : $"{project.Segments.Count} segment{(project.Segments.Count == 1 ? "" : "s")} · " +
                  $"{project.Segments.Count(segment => segment.Status == SegmentStatus.Approved)} approved · " +
                  $"{(int)expectedTiming.Duration.TotalMinutes:00}:{expectedTiming.Duration.Seconds:00} expected " +
                  $"({expectedTiming.MeasuredCount} measured, {expectedTiming.EstimatedCount} estimated) · " +
                  $"opened {lastOpened}";

            string displayName = project?.Name ?? entry.Name;
            string statusBrushKey = isActive ? "ActiveSignalBrush" : isAvailable ? "MutedTextBrush" : "PickupBrush";
            items.Add(new RecentProjectListItem(
                displayName,
                entry.ProjectRoot,
                detailLine,
                isActive ? "● ACTIVE" : isAvailable ? "AVAILABLE" : "MISSING FOLDER",
                isActive ? "Continue" : isAvailable ? "Open" : "Missing",
                isAvailable,
                isAvailable
                    ? $"{(isActive ? "Continue" : "Open")} {displayName}"
                    : $"{entry.Name} is unavailable")
            {
                StatusBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[statusBrushKey],
                CardBorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[isActive ? "ActiveSignalBrush" : "DividerBrush"],
            });
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
        ApplyProjectFilter();
    }

    private void ApplyProjectFilter()
    {
        string filter = ProjectFilterBox.Text?.Trim() ?? string.Empty;
        VisibleProjects.Clear();
        foreach (RecentProjectListItem item in RecentProjects)
        {
            if (filter.Length == 0
                || item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.ProjectRoot.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                VisibleProjects.Add(item);
            }
        }

        bool none = RecentProjects.Count == 0;
        EmptyProjectCard.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        ProjectFilterBox.Visibility = RecentProjects.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RecentProjectsEmptyText.Text = none
            ? "Projects you create or open will stay here after VoiceBench restarts."
            : $"No recent project matches “{filter}”.";
        RecentProjectsEmptyText.Visibility = VisibleProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentProjectsList.Visibility = VisibleProjects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProjectFilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplyProjectFilter();

    private async void ShowProjectInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string projectRoot }) return;
        try
        {
            await ShellLauncher.OpenFolderAsync(projectRoot);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Folder could not be opened", exception.Message);
        }
    }

    private void CopyProjectPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string projectRoot }) return;
        try
        {
            ShellLauncher.CopyPath(projectRoot);
            ShowStatus(InfoBarSeverity.Success, "Folder path copied", projectRoot);
        }
        catch (InvalidOperationException exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Folder path could not be copied", exception.Message);
        }
    }

    private async void RemoveRecentProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string projectRoot }) return;
        try
        {
            await App.Services.RecentProjects.RemoveAsync(projectRoot);
            await UpdateRecentProjectsAsync();
            ShowStatus(
                InfoBarSeverity.Informational,
                "Removed from recent projects",
                $"The folder was not changed. Use Open folder to add it back: {projectRoot}");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Project could not be removed from the list", exception.Message);
        }
    }

    private static async Task<DurationProjectionSummary> ProjectDurationAsync(
        string projectRoot,
        VoiceBenchProjectDocument project)
    {
        IReadOnlyDictionary<string, TimeSpan> estimates = await NarrationDurationProjection.LoadEstimatesAsync(
            projectRoot,
            project.Segments);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(item => item.ChapterId, StringComparer.Ordinal);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        TimeSpan duration = TimeSpan.Zero;
        int measured = 0;
        int estimated = 0;
        int includedChapters = 0;
        foreach (string chapterId in project.ChapterOrder)
        {
            if (!chapters.TryGetValue(chapterId, out ChapterDocument? chapter) ||
                chapter.Status == ChapterStatus.Archived ||
                !chapter.IncludedInCompilation)
            {
                continue;
            }

            List<SegmentDocument> ordered = chapter.SegmentOrder
                .Where(segments.ContainsKey)
                .Select(id => segments[id])
                .ToList();
            DurationProjectionSummary chapterTiming = NarrationDurationProjection.Summarize(
                ordered,
                estimates,
                project.AudioStandard.PauseMilliseconds);
            duration += chapterTiming.Duration;
            measured += chapterTiming.MeasuredCount;
            estimated += chapterTiming.EstimatedCount;
            includedChapters++;
        }

        if (includedChapters > 1)
        {
            duration += TimeSpan.FromMilliseconds(
                project.AudioStandard.ChapterPauseMilliseconds * (includedChapters - 1));
        }

        return new DurationProjectionSummary(duration, measured, estimated);
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

    public string ShowInFolderAutomationName => $"Show {Name} in File Explorer";

    public string RemoveAutomationName => $"Remove {Name} from recent projects";

    public Microsoft.UI.Xaml.Media.Brush? StatusBrush { get; set; }

    public Microsoft.UI.Xaml.Media.Brush? CardBorderBrush { get; set; }
}
