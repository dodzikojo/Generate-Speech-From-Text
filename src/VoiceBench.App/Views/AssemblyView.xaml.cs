using System.Globalization;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class AssemblyView : UserControl
{
    private VoiceBenchProjectDocument? _project;
    private bool _loading;
    private string? _revealPath;
    private int _loadVersion;

    public AssemblyView()
    {
        InitializeComponent();
        Loaded += AssemblyView_Loaded;
        SizeChanged += (_, args) => ApplyMastersLayout(args.NewSize.Width);
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    /// <summary>Masters sit beside the chapter list when there is room, otherwise beneath it.</summary>
    private void ApplyMastersLayout(double width)
    {
        bool sideBySide = width >= 1100;
        MastersColumn.Width = sideBySide ? new GridLength(width >= 1400 ? 460 : 400) : new GridLength(0);
        MastersRow.Height = sideBySide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(MastersPanel, sideBySide ? 0 : 1);
        Grid.SetColumn(MastersPanel, sideBySide ? 1 : 0);
    }

    private async void AssemblyView_Loaded(object sender, RoutedEventArgs e) => await LoadProjectAsync();

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        _ = DispatcherQueue.TryEnqueue(async () => await LoadProjectAsync());

    private async Task LoadProjectAsync()
    {
        int loadVersion = ++_loadVersion;
        _loading = true;
        try
        {
            _project = App.Services.Session.Project;
            AssemblyChaptersList.Items.Clear();
            RefreshMasters();
            OpenMastersFolderButton.IsEnabled = _project is not null;
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
            VoiceBenchProjectDocument project = _project;
            IReadOnlyDictionary<string, TimeSpan> estimates = await NarrationDurationProjection.LoadEstimatesAsync(
                App.Services.Session.RequireProjectRoot(),
                project.Segments);
            if (loadVersion != _loadVersion) return;
            AssemblyChaptersList.Items.Clear();
            int included = 0;
            int approved = 0;
            int total = 0;
            int measured = 0;
            int estimated = 0;
            TimeSpan duration = TimeSpan.Zero;
            int number = 0;
            foreach (string chapterId in project.ChapterOrder)
            {
                if (!chapters.TryGetValue(chapterId, out ChapterDocument? chapter) || chapter.Status == ChapterStatus.Archived) continue;
                number++;
                if (chapter.IncludedInCompilation) included++;
                List<SegmentDocument> ordered = chapter.SegmentOrder.Where(segments.ContainsKey).Select(id => segments[id]).ToList();
                DurationProjectionSummary chapterTiming = NarrationDurationProjection.Summarize(
                    ordered,
                    estimates,
                    project.AudioStandard.PauseMilliseconds);
                total += chapter.IncludedInCompilation ? ordered.Count : 0;
                int chapterApproved = ordered.Count(IsAssemblyCurrent);
                if (chapter.IncludedInCompilation)
                {
                    approved += chapterApproved;
                    duration += chapterTiming.Duration;
                    measured += chapterTiming.MeasuredCount;
                    estimated += chapterTiming.EstimatedCount;
                }

                MasterEligibility eligibility = MasterEligibilityEvaluator.Evaluate(project, MasterScope.Chapter, chapter.ChapterId);
                TimeSpan approvedAudio = ChapterPlaylistBuilder.Build(project, chapter.ChapterId).TotalDuration;
                AssemblyChaptersList.Items.Add(CreateChapterCard(number, chapter, ordered, chapterApproved, approvedAudio, chapterTiming, eligibility));
            }

            if (included > 1) duration += TimeSpan.FromMilliseconds(project.AudioStandard.ChapterPauseMilliseconds * (included - 1));
            IncludedCountText.Text = included.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApprovedCountText.Text = $"{approved} / {total}";
            DurationText.Text = FormatDuration(duration);
            DurationBasisText.Text = $"{measured} measured  ·  {estimated} estimated";
            MasterEligibility compilation = MasterEligibilityEvaluator.Evaluate(project);
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
        TimeSpan approvedAudio,
        DurationProjectionSummary timing,
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
        heading.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["StudioHeadingTextStyle"];
        title.Children.Add(heading);
        TextBlock metadata = new()
        {
            Text = $"{approved} / {segments.Count} APPROVED  ·  {FormatDuration(approvedAudio)} APPROVED AUDIO  ·  {FormatDuration(timing.Duration)} EXPECTED  ·  {timing.MeasuredCount} MEASURED / {timing.EstimatedCount} ESTIMATED",
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(metadata, $"{approved} of {segments.Count} segments approved, {FormatDuration(approvedAudio)} of approved audio");
        metadata.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"];
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
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[eligibility.CanBuild ? "ApprovalBrush" : "PickupBrush"],
        };
        status.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"];
        status.Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[eligibility.CanBuild ? "ApprovalBrush" : "PickupBrush"];
        Grid.SetRow(status, 1);
        Grid.SetColumnSpan(status, 2);
        content.Children.Add(status);

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        Style iconStyle = (Style)Microsoft.UI.Xaml.Application.Current.Resources["IconButtonStyle"];
        Button up = new() { Content = new FontIcon { Glyph = "\uE74A", FontSize = 13 }, Tag = $"{chapter.ChapterId}|-1", Style = iconStyle };
        Button down = new() { Content = new FontIcon { Glyph = "\uE74B", FontSize = 13 }, Tag = $"{chapter.ChapterId}|1", Style = iconStyle };
        ToolTipService.SetToolTip(up, "Move chapter up");
        ToolTipService.SetToolTip(down, "Move chapter down");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, $"Move {chapter.Title} up");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, $"Move {chapter.Title} down");
        Button build = new()
        {
            Content = "Build chapter master",
            Tag = chapter.ChapterId,
            IsEnabled = eligibility.CanBuild,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["QuietButtonStyle"],
        };
        up.Click += MoveChapter_Click;
        down.Click += MoveChapter_Click;
        build.Click += BuildChapter_Click;
        Button play = new()
        {
            Tag = chapter.ChapterId,
            IsEnabled = approved > 0,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["QuietButtonStyle"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "\uE768", FontSize = 13 }, new TextBlock { Text = "Play approved takes" } },
            },
        };
        ToolTipService.SetToolTip(play, "Open the Workbench and play every approved take of this chapter in order");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(play, $"Play approved takes of {chapter.Title}");
        play.Click += PlayChapter_Click;
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(build);
        actions.Children.Add(play);
        Grid.SetRow(actions, 2);
        Grid.SetColumnSpan(actions, 2);
        content.Children.Add(actions);

        return new ListViewItem
        {
            Content = new Border
            {
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["StudioPanelStyle"],
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
        await App.Services.AuthoringTransactions.ExecuteAsync(
            App.Services.Session.RequireProjectRoot(),
            toggle.IsOn ? "Include chapter in compilation" : "Exclude chapter from compilation",
            cancellationToken => App.Services.ChapterAuthoring.UpdateChapterAsync(
                App.Services.Session.RequireProjectRoot(),
                chapterId,
                includedInCompilation: toggle.IsOn,
                cancellationToken: cancellationToken));
        await App.Services.Session.RefreshAsync();
    }

    private async void MoveChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        string[] parts = tag.Split('|');
        await App.Services.AuthoringTransactions.ExecuteAsync(
            App.Services.Session.RequireProjectRoot(),
            "Reorder chapter",
            cancellationToken => App.Services.ChapterAuthoring.MoveChapterAsync(
                App.Services.Session.RequireProjectRoot(),
                parts[0],
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken));
        await App.Services.Session.RefreshAsync();
    }

    private async void PlayChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string chapterId } || App.MainWindow is not { } window) return;
        await window.PlayChapterAsync(chapterId);
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
            Dictionary<string, object> arguments = new() { ["name"] = name, ["scope"] = scope.ToString() };
            if (chapterId is not null) arguments["chapterId"] = chapterId;
            MasteringResult result = await App.Services.RunTrackedJobAsync<MasteringResult>("master.start", arguments);
            string masterPath = ProjectLocations.ResolveInsideProject(App.Services.Session.RequireProjectRoot(), result.MasterRelativePath);
            ShowStatus(
                InfoBarSeverity.Success,
                "Assembly verified",
                $"Created {result.MasterRelativePath}. Final scripts, submitted-text audit, cue sheets, and mastering reports share the same version.",
                revealPath: masterPath);
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

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private void ShowStatus(InfoBarSeverity severity, string title, string message, string? revealPath = null)
    {
        _revealPath = revealPath;
        InfoBarRevealButton.Visibility = revealPath is null ? Visibility.Collapsed : Visibility.Visible;
        AssemblyInfoBar.Severity = severity;
        AssemblyInfoBar.Title = title;
        AssemblyInfoBar.Message = message;
        AssemblyInfoBar.IsOpen = true;
    }

    private void RefreshMasters()
    {
        string? selected = (MastersList.SelectedItem as MasterListItem)?.FullPath;
        List<MasterListItem> items = [];
        if (App.Services.Session.ProjectRoot is string root)
        {
            try
            {
                items = ProjectLocations.ListMasters(root).Select(master => new MasterListItem(master)).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ShowStatus(InfoBarSeverity.Warning, "Masters folder could not be read", exception.Message);
            }
        }

        MastersList.ItemsSource = items;
        MastersList.SelectedItem = items.FirstOrDefault(item => string.Equals(item.FullPath, selected, StringComparison.OrdinalIgnoreCase));
        MastersEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MastersList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MasterTransport.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MastersSummaryText.Text = items.Count switch
        {
            0 => App.Services.Session.HasProject ? "NOTHING BUILT YET" : "OPEN A PROJECT",
            1 => "1 FILE IN 05-MASTERS",
            _ => $"{items.Count} FILES IN 05-MASTERS  ·  NEWEST FIRST",
        };
        if (MastersList.SelectedItem is null && items.Count > 0) MastersList.SelectedIndex = 0;
    }

    private void MastersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MastersList.SelectedItem is MasterListItem item && File.Exists(item.FullPath))
            MasterTransport.LoadAudio(item.FullPath, item.Title, TimeSpan.Zero);
        else
            MasterTransport.ClearAudio("SELECT A MASTER TO LISTEN");
    }

    private async void OpenMastersFolder_Click(object sender, RoutedEventArgs e)
    {
        try { await ShellLauncher.OpenProjectFolderAsync(ProjectFolder.Masters); }
        catch (Exception exception) { ShowStatus(InfoBarSeverity.Error, "Masters folder could not be opened", exception.Message); }
    }

    private async void RevealMaster_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        try { await ShellLauncher.RevealFileAsync(path); }
        catch (Exception exception) { ShowStatus(InfoBarSeverity.Error, "Master could not be shown", exception.Message); }
    }

    private async void OpenMaster_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        try { await ShellLauncher.OpenFileAsync(path); }
        catch (Exception exception) { ShowStatus(InfoBarSeverity.Error, "Master could not be opened", exception.Message); }
    }

    private void CopyMasterPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        try
        {
            ShellLauncher.CopyPath(path);
            ShowStatus(InfoBarSeverity.Success, "Master path copied", path);
        }
        catch (InvalidOperationException exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Master path could not be copied", exception.Message);
        }
    }

    private async void InfoBarReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_revealPath is null) return;
        try { await ShellLauncher.RevealFileAsync(_revealPath); }
        catch (Exception exception) { ShowStatus(InfoBarSeverity.Error, "File could not be shown", exception.Message); }
    }
}

/// <summary>A built master or mix export row in Assembly.</summary>
public sealed class MasterListItem(BuiltMaster master)
{
    public string Title { get; } = master.DisplayName;

    public string FullPath { get; } = master.FullPath;

    public string RelativePath { get; } = master.RelativePath;

    public string Glyph { get; } = master.IsMp3 ? "\uE8D6" : "\uE93C";

    public string Detail { get; } =
        $"{(master.IsMp3 ? "MP3" : "WAV")}  ·  {FormatSize(master.SizeBytes)}  ·  {master.ModifiedAt.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture)}";

    public string AutomationName => $"{Title}, {Detail}";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024d):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };
}
