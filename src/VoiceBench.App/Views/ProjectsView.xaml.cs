using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.App.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class ProjectsView : UserControl
{
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

    private void ProjectsView_Loaded(object sender, RoutedEventArgs e) => UpdateRecentProject();

    private void Session_ProjectChanged(object? sender, EventArgs e) => UpdateRecentProject();

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        NewProjectDialog.XamlRoot = XamlRoot;
        await NewProjectDialog.ShowAsync();
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

    private async void ContinueProject_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Session.HasProject)
        {
            App.MainWindow?.NavigateTo("workbench");
            return;
        }

        string? sampleRoot = FindSampleProject();
        if (sampleRoot is not null)
        {
            try
            {
                await App.Services.Session.OpenAsync(sampleRoot);
                return;
            }
            catch (Exception exception)
            {
                ShowStatus(InfoBarSeverity.Error, "Sample project could not be opened", exception.Message);
                return;
            }
        }

        OpenFolder_Click(sender, e);
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

    private void UpdateRecentProject()
    {
        VoiceBenchProjectDocument? project = App.Services.Session.Project;
        if (project is null)
        {
            return;
        }

        RecentProjectNameText.Text = project.Name;
        RecentProjectPathText.Text = App.Services.Session.ProjectRoot;
        RecentSegmentCountText.Text = project.Segments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RecentApprovedCountText.Text = project.Segments.Count(segment => segment.Status == SegmentStatus.Approved)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        TimeSpan renderedDuration = TimeSpan.FromTicks(project.Segments.Sum(segment =>
            segment.RenderHistory.OrderByDescending(render => render.TakeNumber).FirstOrDefault()?.Duration.Ticks ?? 0));
        RecentRenderedDurationText.Text = $"{(int)renderedDuration.TotalMinutes:00}:{renderedDuration.Seconds:00}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ContinueProjectButton,
            $"Continue {project.Name} in Workbench");
    }

    private static string? FindSampleProject()
    {
        foreach (string seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(seed);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "samples", "legacy-clashwise");
                if (File.Exists(Path.Combine(candidate, "voicebench.project.json")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        ProjectStatusBar.Severity = severity;
        ProjectStatusBar.Title = title;
        ProjectStatusBar.Message = message;
        ProjectStatusBar.IsOpen = true;
    }
}
