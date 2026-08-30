using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class AssemblyView : UserControl
{
    private VoiceBenchProjectDocument? _project;
    private bool _loading;

    public AssemblyView()
    {
        InitializeComponent();
        Loaded += AssemblyView_Loaded;
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    private async void AssemblyView_Loaded(object sender, RoutedEventArgs e) => await LoadProjectAsync();

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        _ = DispatcherQueue.TryEnqueue(async () => await LoadProjectAsync());

    private async Task LoadProjectAsync()
    {
        _loading = true;
        try
        {
            _project = App.Services.Session.Project;
            AssemblyChaptersList.Items.Clear();
            if (_project is null)
            {
                AssemblyTitleText.Text = "Open a project to assemble narration";
                BuildCompilationButton.IsEnabled = false;
                ShowStatus(InfoBarSeverity.Informational, "No project is open", "Open or create a project from Projects first.");
                return;
            }

            AssemblyTitleText.Text = _project.Name;
            Dictionary<string, ChapterDocument> chapters = _project.Chapters.ToDictionary(item => item.ChapterId, StringComparer.Ordinal);
            Dictionary<string, SegmentDocument> segments = _project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
            int included = 0;
            int approved = 0;
            int total = 0;
            TimeSpan duration = TimeSpan.Zero;
            int number = 0;
            foreach (string chapterId in _project.ChapterOrder)
            {
                if (!chapters.TryGetValue(chapterId, out ChapterDocument? chapter) || chapter.Status == ChapterStatus.Archived) continue;
                number++;
                if (chapter.IncludedInCompilation) included++;
                List<SegmentDocument> ordered = chapter.SegmentOrder.Where(segments.ContainsKey).Select(id => segments[id]).ToList();
                total += chapter.IncludedInCompilation ? ordered.Count : 0;
                int chapterApproved = ordered.Count(IsAssemblyCurrent);
                if (chapter.IncludedInCompilation)
                {
                    approved += chapterApproved;
                    duration += EstimateChapterDuration(ordered, _project.AudioStandard.PauseMilliseconds);
                }

                MasterEligibility eligibility = MasterEligibilityEvaluator.Evaluate(_project, MasterScope.Chapter, chapter.ChapterId);
                AssemblyChaptersList.Items.Add(CreateChapterCard(number, chapter, ordered, chapterApproved, eligibility));
            }

            if (included > 1) duration += TimeSpan.FromMilliseconds(_project.AudioStandard.ChapterPauseMilliseconds * (included - 1));
            IncludedCountText.Text = included.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApprovedCountText.Text = $"{approved} / {total}";
            DurationText.Text = FormatDuration(duration);
            MasterEligibility compilation = MasterEligibilityEvaluator.Evaluate(_project);
            BuildCompilationButton.IsEnabled = compilation.CanBuild;
            if (!compilation.CanBuild)
            {
                ShowStatus(
                    InfoBarSeverity.Warning,
                    "Compilation has blockers",
                    string.Join("  ", compilation.Issues.Take(3).Select(issue => issue.Message)));
            }
            else
            {
                AssemblyInfoBar.IsOpen = false;
            }
        }
        finally
        {
            _loading = false;
        }

        await Task.CompletedTask;
    }

    private ListViewItem CreateChapterCard(
        int number,
        ChapterDocument chapter,
        IReadOnlyList<SegmentDocument> segments,
        int approved,
        MasterEligibility eligibility)
    {
        Grid content = new() { Padding = new Thickness(16), ColumnSpacing = 12, RowSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel title = new() { Spacing = 3 };
        TextBlock heading = new() { Text = $"{number:00}  {chapter.Title}" };
        heading.Style = (Style)Application.Current.Resources["StudioHeadingTextStyle"];
        title.Children.Add(heading);
        TextBlock metadata = new()
        {
            Text = $"{approved} / {segments.Count} APPROVED  ·  {FormatDuration(EstimateChapterDuration(segments, _project!.AudioStandard.PauseMilliseconds))}",
        };
        metadata.Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"];
        title.Children.Add(metadata);
        content.Children.Add(title);

        ToggleSwitch inclusion = new()
        {
            Header = "Compilation",
            IsOn = chapter.IncludedInCompilation,
            Tag = chapter.ChapterId,
            OnContent = "Included",
            OffContent = "Excluded",
        };
        inclusion.Toggled += Inclusion_Toggled;
        Grid.SetColumn(inclusion, 1);
        content.Children.Add(inclusion);

        TextBlock status = new()
        {
            Text = eligibility.CanBuild
                ? "READY  ·  every segment has an approved take aligned to its clean source"
                : string.Join(Environment.NewLine, eligibility.Issues.Select(issue => $"• {issue.Message}")),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources[eligibility.CanBuild ? "ApprovalBrush" : "PickupBrush"],
        };
        status.Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"];
        status.Foreground = (Brush)Application.Current.Resources[eligibility.CanBuild ? "ApprovalBrush" : "PickupBrush"];
        Grid.SetRow(status, 1);
        Grid.SetColumnSpan(status, 2);
        content.Children.Add(status);

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button up = new() { Content = "↑", Tag = $"{chapter.ChapterId}|-1" };
        Button down = new() { Content = "↓", Tag = $"{chapter.ChapterId}|1" };
        Button build = new() { Content = "Build chapter", Tag = chapter.ChapterId, IsEnabled = eligibility.CanBuild };
        up.Click += MoveChapter_Click;
        down.Click += MoveChapter_Click;
        build.Click += BuildChapter_Click;
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(build);
        Grid.SetRow(actions, 2);
        Grid.SetColumnSpan(actions, 2);
        content.Children.Add(actions);

        return new ListViewItem
        {
            Content = new Border
            {
                Style = (Style)Application.Current.Resources["StudioPanelStyle"],
                Child = content,
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 0, 10),
            IsTabStop = false,
        };
    }

    private async void Inclusion_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { Tag: string chapterId } toggle) return;
        await App.Services.ChapterAuthoring.UpdateChapterAsync(
            App.Services.Session.RequireProjectRoot(), chapterId, includedInCompilation: toggle.IsOn);
        await App.Services.Session.RefreshAsync();
    }

    private async void MoveChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        string[] parts = tag.Split('|');
        await App.Services.ChapterAuthoring.MoveChapterAsync(
            App.Services.Session.RequireProjectRoot(), parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        await App.Services.Session.RefreshAsync();
    }

    private async void BuildChapter_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not Button { Tag: string chapterId }) return;
        ChapterDocument chapter = _project.Chapters.Single(item => item.ChapterId == chapterId);
        await BuildAsync($"{_project.Name} {chapter.Title}", MasterScope.Chapter, chapterId);
    }

    private async void BuildCompilation_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        await BuildAsync($"{_project.Name} compilation", MasterScope.Compilation, null);
    }

    private async Task BuildAsync(string name, MasterScope scope, string? chapterId)
    {
        try
        {
            BuildCompilationButton.IsEnabled = false;
            ShowStatus(InfoBarSeverity.Informational, "Building aligned master", "Normalizing approved takes, inserting pauses, verifying audio, and writing versioned exports.");
            MasteringResult result = await App.Services.BuildMasterAsync(name, scope, chapterId);
            ShowStatus(InfoBarSeverity.Success, "Assembly verified", $"Created {result.MasterRelativePath}. Final scripts, submitted-text audit, cue sheets, and mastering reports share the same version.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Assembly is blocked", exception.Message);
        }
        finally
        {
            await LoadProjectAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.Session.HasProject) await App.Services.Session.RefreshAsync();
        else await LoadProjectAsync();
    }

    private static bool IsAssemblyCurrent(SegmentDocument segment)
    {
        RenderRecord? render = segment.ApprovedRenderId is Guid id
            ? segment.RenderHistory.SingleOrDefault(item => item.RenderId == id)
            : null;
        return segment.Status == SegmentStatus.Approved && render?.Status == RenderStatus.Approved &&
            string.Equals(render.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(render.ApprovalSourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath);
    }

    private static TimeSpan EstimateChapterDuration(IReadOnlyList<SegmentDocument> segments, int pauseMilliseconds)
    {
        TimeSpan duration = TimeSpan.Zero;
        foreach (SegmentDocument segment in segments)
        {
            RenderRecord? render = segment.ApprovedRenderId is Guid id
                ? segment.RenderHistory.SingleOrDefault(item => item.RenderId == id)
                : null;
            duration += render?.Duration ?? TimeSpan.Zero;
        }
        if (segments.Count > 1) duration += TimeSpan.FromMilliseconds(pauseMilliseconds * (segments.Count - 1));
        return duration;
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        AssemblyInfoBar.Severity = severity;
        AssemblyInfoBar.Title = title;
        AssemblyInfoBar.Message = message;
        AssemblyInfoBar.IsOpen = true;
    }
}
