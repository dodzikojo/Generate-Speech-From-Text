using System.Diagnostics;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class WorkbenchView : UserControl
{
    private const string ChapterRailVisibilitySetting = "Workbench.ShowChapterRail";
    private const string DirectorVisibilitySetting = "Workbench.ShowDirector";
    private const string TakeReviewVisibilitySetting = "Workbench.ShowTakeReview";

    private VoiceBenchProjectDocument? _project;
    private SegmentDocument? _segment;
    private string _loadedText = string.Empty;
    private string _loadedDiskHash = string.Empty;
    private bool _ignoreTextChanges;
    private bool _isDirty;
    private bool _externalOverwriteApproved;
    private CancellationTokenSource? _renderCancellation;
    private WritingProposal? _pendingWritingProposal;
    private WritingAction _pendingWritingAction;
    private string? _pendingWritingProviderId;
    private string? _pendingWritingModelId;
    private string? _pendingWritingSourceHash;
    private bool _ignoreCompactPicker;
    private ScriptFileMonitor? _scriptFileMonitor;
    private string? _monitoredProjectRoot;
    private int _scriptChangeVersion;
    private string _lastEditorText = string.Empty;
    private DispatcherQueueTimer? _cueFormatTimer;
    private bool _ignoreCueToggle;
    private bool _cueFormattingUnavailable;

    private sealed record RailItem(
        string Title,
        string Metadata,
        string ChapterId,
        string? SegmentId,
        Brush StatusBrush);

    private sealed record RevisionChoice(Guid RevisionId, bool RestorePreviousText);

    public WorkbenchView()
    {
        InitializeComponent();
        _cueFormatTimer = DispatcherQueue.CreateTimer();
        _cueFormatTimer.Interval = TimeSpan.FromMilliseconds(150);
        _cueFormatTimer.IsRepeating = false;
        _cueFormatTimer.Tick += (_, _) => ApplyCueFormatting();
        LoadWorkspacePanelPreferences();
        SetSelectedContent(ProviderComboBox, "OpenAI");
        PopulateProviderControls("openai", "gpt-4o-mini-tts", "marin");
        PopulateWritingProviders();
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
        Loaded += WorkbenchView_Loaded;
        Unloaded += WorkbenchView_Unloaded;
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    private void ApplyResponsiveState(double width)
    {
        VisualStateManager.GoToState(
            this,
            width >= 1180 ? "WideWorkbench" : width >= 860 ? "CompactWorkbench" : "NarrowWorkbench",
            false);
        ApplyWorkspacePanelLayout(width);
    }

    private void WorkspacePanelToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveWorkspacePanelPreferences();
        ApplyWorkspacePanelLayout(ActualWidth);
    }

    private void LoadWorkspacePanelPreferences()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        ChapterRailToggle.IsChecked = ReadWorkspacePanelPreference(values[ChapterRailVisibilitySetting]);
        DirectorToggle.IsChecked = ReadWorkspacePanelPreference(values[DirectorVisibilitySetting]);
        TakeReviewToggle.IsChecked = ReadWorkspacePanelPreference(values[TakeReviewVisibilitySetting]);
    }

    private void SaveWorkspacePanelPreferences()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        values[ChapterRailVisibilitySetting] = ChapterRailToggle.IsChecked == true;
        values[DirectorVisibilitySetting] = DirectorToggle.IsChecked == true;
        values[TakeReviewVisibilitySetting] = TakeReviewToggle.IsChecked == true;
    }

    private static bool ReadWorkspacePanelPreference(object? storedValue) =>
        storedValue is not bool value || value;

    private void ApplyWorkspacePanelLayout(double width)
    {
        if (TakeRailPanel is null) return;
        bool narrow = width < 860;
        bool wide = width >= 1180;
        bool showRail = !narrow && ChapterRailToggle.IsChecked == true;
        bool showDirector = DirectorToggle.IsChecked == true;

        TakeRailPanel.Visibility = showRail ? Visibility.Visible : Visibility.Collapsed;
        TakeRailColumn.Width = showRail ? new GridLength(wide ? 300 : 270) : new GridLength(0);
        CompactPickerPanel.Visibility = narrow || !showRail ? Visibility.Visible : Visibility.Collapsed;

        DirectorPanel.Visibility = showDirector ? Visibility.Visible : Visibility.Collapsed;
        DirectorColumn.Width = wide && showDirector ? new GridLength(350) : new GridLength(0);
        DirectorRow.Height = !wide && showDirector ? new GridLength(narrow ? 380 : 360) : new GridLength(0);
        DirectorPanel.SetValue(Grid.RowProperty, wide ? 0 : 1);
        DirectorPanel.SetValue(Grid.ColumnProperty, wide ? 2 : 1);

        WorkbenchFooter.Visibility = TakeReviewToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelComboBox is null || VoiceComboBox is null || ProviderComboBox.SelectedItem is null) return;
        PopulateProviderControls(GetSelectedProviderId(), null, null);
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox is null || ModelComboBox.SelectedItem is null || ProviderComboBox.SelectedItem is null) return;
        PopulateVoiceControls(GetSelectedProviderId(), null);
    }

    private void PopulateProviderControls(string providerId, string? preferredModel, string? preferredVoice)
    {
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
            item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
        ModelComboBox.Items.Clear();
        foreach (SpeechModelDescriptor model in provider.Models)
        {
            ModelComboBox.Items.Add(new ComboBoxItem
            {
                Content = model.DisplayName,
                Tag = model.ModelId,
            });
        }

        SetSelectedContent(ModelComboBox, preferredModel ?? provider.Models[0].ModelId);
        PopulateVoiceControls(providerId, preferredVoice);
    }

    private void PopulateVoiceControls(string providerId, string? preferredVoice)
    {
        if (ModelComboBox.SelectedItem is not ComboBoxItem) return;
        string modelId = GetSelectedContent(ModelComboBox);
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
            item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
        SpeechModelDescriptor model = provider.Models.Single(item => item.ModelId == modelId);
        string? currentVoice = preferredVoice ??
            ((VoiceComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? VoiceComboBox.SelectedItem?.ToString());
        VoiceComboBox.Items.Clear();
        foreach (string voice in model.Voices)
        {
            VoiceComboBox.Items.Add(new ComboBoxItem { Content = voice, Tag = voice });
        }

        SetSelectedContent(VoiceComboBox, currentVoice ?? model.Voices[0]);
    }

    private void PopulateWritingProviders()
    {
        WritingProviderComboBox.Items.Clear();
        foreach (WritingProviderDescriptor provider in App.Services.WritingProviders)
        {
            WritingProviderComboBox.Items.Add(new ComboBoxItem
            {
                Content = provider.DisplayName,
                Tag = provider.ProviderId,
            });
        }

        SetSelectedContent(WritingProviderComboBox, "openai");
        PopulateWritingModels("openai", "gpt-5.6-terra");
    }

    private void WritingProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WritingModelComboBox is null || WritingProviderComboBox.SelectedItem is null) return;
        PopulateWritingModels(GetSelectedContent(WritingProviderComboBox), null);
    }

    private void PopulateWritingModels(string providerId, string? preferredModel)
    {
        WritingProviderDescriptor provider = App.Services.WritingProviders.Single(item =>
            item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        WritingModelComboBox.Items.Clear();
        foreach (WritingModelDescriptor model in provider.Models)
        {
            WritingModelComboBox.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.ModelId });
        }

        SetSelectedContent(WritingModelComboBox, preferredModel ?? provider.Models[0].ModelId);
    }

    private async void WorkbenchView_Loaded(object sender, RoutedEventArgs e) => await LoadProjectAsync();

    private void WorkbenchView_Unloaded(object sender, RoutedEventArgs e)
    {
        _cueFormatTimer?.Stop();
        DisposeScriptFileMonitor();
    }

    private void DisposeScriptFileMonitor()
    {
        _scriptFileMonitor?.Dispose();
        _scriptFileMonitor = null;
        _monitoredProjectRoot = null;
    }

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        _ = DispatcherQueue.TryEnqueue(async () => await LoadProjectAsync(_segment?.SegmentId));

    private async Task LoadProjectAsync(string? preferredSegmentId = null)
    {
        if (!App.Services.Session.HasProject)
        {
            DisposeScriptFileMonitor();
            _project = null;
            _segment = null;
            ChapterTree.RootNodes.Clear();
            CompactSegmentPicker.Items.Clear();
            TakeTransport.ClearAudio();
            ResetTakeReviewContext();
            SetEditorText(string.Empty);
            SetEditorAvailability(hasSegment: false, showEmptyState: false);
            ShowStatus(InfoBarSeverity.Informational, "Open a project", "Create or open a folder-based project from Projects.");
            return;
        }

        _project = App.Services.Session.RequireProject();
        _ignoreCueToggle = true;
        GenerateProductionCuesToggle.IsOn = _project.WritingProfile.GenerateProductionCues;
        _ignoreCueToggle = false;
        EnsureScriptFileMonitor(App.Services.Session.RequireProjectRoot());
        ProjectHeaderText.Text = $"WORKBENCH  /  {_project.Name.ToUpperInvariant()}";
        ProjectTitleText.Text = _project.Name;
        int approved = _project.Segments.Count(item => item.Status == SegmentStatus.Approved);
        ChapterCountText.Text = _project.Chapters.Count(item => item.Status == ChapterStatus.Active).ToString();
        SegmentCountText.Text = _project.Segments.Count.ToString();
        ApprovedCountText.Text = approved.ToString();
        PopulateChapterTree(_project);
        PopulateWritingReferences(_project);
        LoadDefaultPreset(_project);

        string? segmentId = preferredSegmentId;
        if (segmentId is null || !_project.Segments.Any(item => item.SegmentId == segmentId))
        {
            segmentId = ProjectOrdering.GetPlaybackOrderedSegmentIds(_project).FirstOrDefault()
                ?? _project.Segments.FirstOrDefault()?.SegmentId;
        }

        if (segmentId is null)
        {
            _segment = null;
            TakeTransport.ClearAudio();
            ResetTakeReviewContext();
            SetEditorText(string.Empty);
            SegmentTitleText.Text = "NO SEGMENTS";
            SegmentStatsText.Text = "CREATE, DRAFT, OR IMPORT A CHAPTER TO BEGIN";
            SetEditorAvailability(hasSegment: false, showEmptyState: true);
            ShowStatus(InfoBarSeverity.Informational, "Start your first chapter", "Create a blank chapter, draft one with AI, or import one or more scripts here in VoiceBench.");
            return;
        }

        SetEditorAvailability(hasSegment: true, showEmptyState: false);
        SelectTakeRailItem(segmentId);
        await LoadSegmentAsync(segmentId);
        WorkbenchInfoBar.IsOpen = false;
    }

    private void SetEditorAvailability(bool hasSegment, bool showEmptyState)
    {
        ScriptEditor.IsHitTestVisible = hasSegment;
        ScriptTextBox.IsEnabled = hasSegment;
        ScriptTextBox.IsReadOnly = !hasSegment;
        EmptyAuthoringState.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
        if (showEmptyState)
        {
            EmptyNewChapterButton.Focus(FocusState.Programmatic);
        }
    }

    private void PopulateChapterTree(VoiceBenchProjectDocument project)
    {
        ChapterTree.RootNodes.Clear();
        CompactSegmentPicker.Items.Clear();
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(
            item => item.SegmentId,
            StringComparer.Ordinal);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(
            item => item.ChapterId,
            StringComparer.Ordinal);
        int chapterNumber = 0;
        foreach (string chapterId in project.ChapterOrder)
        {
            if (!chapters.TryGetValue(chapterId, out ChapterDocument? chapter) || chapter.Status == ChapterStatus.Archived) continue;
            chapterNumber++;
            int approved = chapter.SegmentOrder.Count(id =>
                segments.TryGetValue(id, out SegmentDocument? segment) && segment.Status == SegmentStatus.Approved);
            TreeViewNode chapterNode = new()
            {
                IsExpanded = true,
                Content = new RailItem(
                    $"{chapterNumber:00}  {chapter.Title}",
                    $"{chapter.SegmentOrder.Count} segments  ·  {approved} approved  ·  {(chapter.IncludedInCompilation ? "included" : "excluded")}",
                    chapter.ChapterId,
                    null,
                    (Brush)Application.Current.Resources[chapter.IncludedInCompilation ? "ActiveSignalBrush" : "MutedTextBrush"]),
            };
            int segmentNumber = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                segmentNumber++;
                string takes = segment.RenderHistory.Count == 0
                    ? "no takes"
                    : $"take {segment.RenderHistory.Max(item => item.TakeNumber):00}";
                TimeSpan duration = segment.RenderHistory.OrderByDescending(item => item.TakeNumber)
                    .Select(item => item.Duration).FirstOrDefault();
                string durationText = duration > TimeSpan.Zero ? $"  ·  {FormatDuration(duration)}" : string.Empty;
                chapterNode.Children.Add(new TreeViewNode
                {
                    Content = new RailItem(
                        $"{segmentNumber:000}  {segment.Title}",
                        $"{FormatStatus(segment.Status)}  ·  {takes}{durationText}",
                        chapter.ChapterId,
                        segment.SegmentId,
                        GetSegmentStatusBrush(segment.Status)),
                });
                CompactSegmentPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{chapter.Title}  /  {segment.Title}",
                    Tag = segment.SegmentId,
                });
            }

            ChapterTree.RootNodes.Add(chapterNode);
        }
    }

    private static Brush GetSegmentStatusBrush(SegmentStatus status)
    {
        string resourceKey = status switch
        {
            SegmentStatus.Approved => "ApprovalBrush",
            SegmentStatus.Stale or SegmentStatus.PickupRequired => "PickupBrush",
            SegmentStatus.Ready or SegmentStatus.Rendering or SegmentStatus.Rendered => "ActiveSignalBrush",
            _ => "MutedTextBrush",
        };
        return (Brush)Application.Current.Resources[resourceKey];
    }

    private async void ChapterTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        RailItem? item = args.InvokedItem switch
        {
            TreeViewNode node => node.Content as RailItem,
            RailItem railItem => railItem,
            _ => null,
        };
        if (item?.SegmentId is not string segmentId ||
            _segment?.SegmentId == segmentId) return;
        if (_isDirty && !await SaveCurrentScriptAsync())
        {
            SelectTakeRailItem(_segment?.SegmentId);
            return;
        }

        await LoadSegmentAsync(segmentId);
    }

    private void RailItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeViewNode node })
        {
            ChapterTree.SelectedNode = node;
        }
    }

    private void RailContextMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        bool chapterSelected = GetSelectedRailItem()?.SegmentId is null;
        foreach (MenuFlyoutItemBase item in flyout.Items)
        {
            if (string.Equals(item.Tag as string, "chapter", StringComparison.Ordinal))
            {
                item.Visibility = chapterSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (_project is null || selected is null) return;
        try
        {
            if (selected.SegmentId is string segmentId)
            {
                SegmentDocument segment = _project.Segments.Single(item => item.SegmentId == segmentId);
                string path = App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath);
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                if (!await Launcher.LaunchFileAsync(file))
                    throw new InvalidOperationException("Windows could not find an application associated with this script file.");
            }
            else
            {
                string folderPath = ResolveChapterFolder(selected.ChapterId);
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                if (!await Launcher.LaunchFolderAsync(folder))
                    throw new InvalidOperationException("Windows could not open the chapter folder.");
            }
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Location could not be opened", exception.Message);
        }
    }

    private async void ShowSelectedInFolder_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (_project is null || selected is null) return;
        try
        {
            if (selected.SegmentId is string segmentId)
            {
                SegmentDocument segment = _project.Segments.Single(item => item.SegmentId == segmentId);
                string path = App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(ResolveChapterFolder(selected.ChapterId));
                if (!await Launcher.LaunchFolderAsync(folder))
                    throw new InvalidOperationException("Windows could not open the chapter folder.");
            }
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Location could not be shown", exception.Message);
        }
    }

    private string ResolveChapterFolder(string chapterId)
    {
        SegmentDocument? firstSegment = _project?.Segments.FirstOrDefault(item => item.ChapterId == chapterId);
        if (firstSegment is null)
        {
            return App.Services.Session.ResolveProjectPath(VoiceBenchProjectLayout.ScriptsDirectory);
        }

        string scriptPath = App.Services.Session.ResolveProjectPath(firstSegment.ScriptRelativePath);
        return Path.GetDirectoryName(scriptPath)
            ?? App.Services.Session.ResolveProjectPath(VoiceBenchProjectLayout.ScriptsDirectory);
    }

    private void EnsureScriptFileMonitor(string projectRoot)
    {
        string normalizedRoot = Path.GetFullPath(projectRoot);
        if (_scriptFileMonitor is not null && string.Equals(
                _monitoredProjectRoot,
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase)) return;
        _scriptFileMonitor?.Dispose();
        _scriptFileMonitor = new ScriptFileMonitor(normalizedRoot);
        _scriptFileMonitor.ScriptChanged += ScriptFileMonitor_ScriptChanged;
        _monitoredProjectRoot = normalizedRoot;
    }

    private void ScriptFileMonitor_ScriptChanged(object? sender, string changedPath)
    {
        int version = Interlocked.Increment(ref _scriptChangeVersion);
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(180);
            if (version != _scriptChangeVersion || _segment is null) return;
            string currentPath = App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath);
            if (!string.Equals(Path.GetFullPath(changedPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase)) return;
            await HandleExternalScriptChangeAsync(currentPath);
        });
    }

    private async Task HandleExternalScriptChangeAsync(string path)
    {
        if (_segment is null || !File.Exists(path)) return;
        string diskText;
        try
        {
            diskText = await File.ReadAllTextAsync(path);
        }
        catch (IOException)
        {
            await Task.Delay(120);
            if (!File.Exists(path)) return;
            diskText = await File.ReadAllTextAsync(path);
        }

        string diskHash = SourceTextHasher.Compute(diskText);
        if (string.Equals(diskHash, _loadedDiskHash, StringComparison.OrdinalIgnoreCase)) return;
        if (_isDirty)
        {
            ExternalEditBar.IsOpen = true;
            ShowStatus(
                InfoBarSeverity.Warning,
                "The script changed outside VoiceBench",
                "Reload, keep the current editor text, or compare both versions. Nothing will be overwritten automatically.");
            return;
        }

        string segmentId = _segment.SegmentId;
        await LoadSegmentAsync(segmentId);
        ShowStatus(InfoBarSeverity.Success, "External edit loaded", "The clean editor and source status now match the canonical file on disk.");
    }

    private async void CompactSegmentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreCompactPicker || CompactSegmentPicker.SelectedItem is not ComboBoxItem { Tag: string segmentId } ||
            _segment?.SegmentId == segmentId) return;
        if (_isDirty && !await SaveCurrentScriptAsync())
        {
            SelectTakeRailItem(_segment?.SegmentId);
            return;
        }

        await LoadSegmentAsync(segmentId);
    }

    private async Task LoadSegmentAsync(string segmentId)
    {
        VoiceBenchProjectDocument project = _project ?? App.Services.Session.RequireProject();
        int index = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        if (index < 0) return;
        SegmentDocument segment = project.Segments[index];
        string path = App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath);
        string text = await File.ReadAllTextAsync(path);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        string sourceHash = SourceTextHasher.Compute(parsed.NarrationText);
        string authoringHash = SourceTextHasher.Compute(text);
        SegmentDocument current = SegmentStateRules.WithSourceHashes(segment, sourceHash, authoringHash);
        if (current != segment)
        {
            List<SegmentDocument> segments = [.. project.Segments];
            segments[index] = current;
            project = project with { Segments = segments };
            await App.Services.Session.SaveAsync(project);
            _project = App.Services.Session.RequireProject();
            project = _project;
        }

        _segment = current;
        _loadedText = text;
        _loadedDiskHash = authoringHash;
        _externalOverwriteApproved = false;
        ExternalEditBar.IsOpen = false;
        SetEditorText(text);
        ChapterDocument? chapter = project.Chapters.SingleOrDefault(item => item.ChapterId == current.ChapterId);
        int chapterPosition = Math.Max(0, project.ChapterOrder.IndexOf(current.ChapterId)) + 1;
        int segmentPosition = Math.Max(0, chapter?.SegmentOrder.IndexOf(current.SegmentId) ?? 0) + 1;
        SegmentTitleText.Text = $"{chapterPosition:00}.{segmentPosition:000}  /  {current.Title.ToUpperInvariant()}";
        TakeReviewTitleText.Text = current.Title;
        TakeReviewStatusText.Text = $"{FormatStatus(current.Status)}  ·  {current.RenderHistory.Count} {(current.RenderHistory.Count == 1 ? "TAKE" : "TAKES")}";
        ScriptPathText.Text = current.ScriptRelativePath.Replace('/', '\\');
        SelectTakeRailItem(current.SegmentId);
        UpdateEditorStats();
        PopulateTakeSelector(current);
        PopulateWritingRevisions(current);
        LoadSegmentPreset(project, current);
    }

    private async void ImportScript_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Session.HasProject)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Imported scripts are stored in the active project's 02-scripts folder.");
            return;
        }

        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        try
        {
            VoiceBenchProjectDocument before = App.Services.Session.RequireProject();
            VoiceBenchProjectDocument after = await App.Services.ScriptImporter.ImportFilesAsync(
                App.Services.Session.RequireProjectRoot(),
                files.Select(file => file.Path).ToArray());
            await App.Services.Session.RefreshAsync();
            ShowStatus(
                InfoBarSeverity.Success,
                files.Count == 1 ? "Script imported" : "Scripts imported",
                $"Created {after.Chapters.Count - before.Chapters.Count} chapter(s) and {after.Segments.Count - before.Segments.Count} canonical segment(s).");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Script import failed", exception.Message);
        }
    }

    private void PopulateWritingReferences(VoiceBenchProjectDocument project)
    {
        HashSet<string> selected = WritingReferencesList.SelectedItems
            .OfType<ListViewItem>()
            .Select(item => item.Tag as string)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        WritingReferencesList.Items.Clear();
        foreach (SourceReferenceDocument reference in project.SourceReferences)
        {
            ListViewItem item = new()
            {
                Content = reference.Name,
                Tag = reference.ReferenceId,
            };
            WritingReferencesList.Items.Add(item);
            if (selected.Contains(reference.ReferenceId)) WritingReferencesList.SelectedItems.Add(item);
        }
    }

    private void PopulateWritingRevisions(SegmentDocument segment)
    {
        WritingRevisionComboBox.Items.Clear();
        foreach (WritingRevisionRecord revision in segment.WritingRevisions.OrderByDescending(item => item.CreatedAt))
        {
            WritingRevisionComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{revision.CreatedAt.LocalDateTime:g} · accepted {revision.Action}",
                Tag = new RevisionChoice(revision.RevisionId, false),
            });
            WritingRevisionComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{revision.CreatedAt.LocalDateTime:g} · before {revision.Action}",
                Tag = new RevisionChoice(revision.RevisionId, true),
            });
        }
        WritingRevisionComboBox.SelectedIndex = WritingRevisionComboBox.Items.Count > 0 ? 0 : -1;
    }

    private async void RestoreRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null || WritingRevisionComboBox.SelectedItem is not ComboBoxItem { Tag: RevisionChoice choice }) return;
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Restore this writing revision?",
            Content = "The current text remains in revision history. Existing takes become stale if the source changes.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            string segmentId = _segment.SegmentId;
            await App.Services.ChapterAuthoring.RestoreRevisionAsync(
                App.Services.Session.RequireProjectRoot(),
                segmentId,
                choice.RevisionId,
                choice.RestorePreviousText);
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(segmentId);
            ShowStatus(InfoBarSeverity.Success, "Writing revision restored", "The canonical source and assembly status were updated.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Revision could not be restored", exception.Message);
        }
    }

    private async void ImportReference_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        try
        {
            foreach (Windows.Storage.StorageFile file in files)
            {
                await App.Services.ChapterAuthoring.ImportReferenceAsync(
                    App.Services.Session.RequireProjectRoot(), file.Path);
            }
            await App.Services.Session.RefreshAsync();
            ShowStatus(InfoBarSeverity.Success, "References imported", "References stay local until you explicitly select them for a writing request.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Reference import failed", exception.Message);
        }
    }

    private async void NewChapter_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        (ContentDialogResult result, string title, string brief) = await ShowNameDialogAsync(
            "New blank chapter",
            "Chapter title",
            "Chapter goal or brief (optional)",
            "Create chapter");
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title)) return;
        try
        {
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.CreateBlankChapterAsync(
                App.Services.Session.RequireProjectRoot(),
                title,
                brief);
            await App.Services.Session.RefreshAsync();
            string segmentId = updated.Chapters.Single(chapter => chapter.ChapterId == updated.ChapterOrder.Last())
                .SegmentOrder.Single();
            await LoadProjectAsync(segmentId);
            ShowStatus(InfoBarSeverity.Success, "Chapter created", "The blank chapter and its opening segment are ready to edit.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Chapter could not be created", exception.Message);
        }
    }

    private async void NewSegment_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        string? chapterId = GetSelectedRailItem()?.ChapterId ?? _segment?.ChapterId ??
            _project?.ChapterOrder.FirstOrDefault();
        if (chapterId is null)
        {
            NewChapter_Click(sender, e);
            return;
        }

        string? title = await PromptForTextAsync("New segment", "Segment title", "Opening", "Add segment");
        if (string.IsNullOrWhiteSpace(title)) return;
        VoiceBenchProjectDocument before = App.Services.Session.RequireProject();
        try
        {
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.AddSegmentAsync(
                App.Services.Session.RequireProjectRoot(), chapterId, title);
            await App.Services.Session.RefreshAsync();
            string newId = updated.Chapters.Single(chapter => chapter.ChapterId == chapterId).SegmentOrder
                .Except(before.Chapters.Single(chapter => chapter.ChapterId == chapterId).SegmentOrder)
                .Single();
            await LoadProjectAsync(newId);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Segment could not be created", exception.Message);
        }
    }

    private async void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (_project is null || selected is null) return;
        string current = selected.SegmentId is null
            ? _project.Chapters.Single(chapter => chapter.ChapterId == selected.ChapterId).Title
            : _project.Segments.Single(segment => segment.SegmentId == selected.SegmentId).Title;
        string? title = await PromptForTextAsync("Rename", "Name", current, "Rename");
        if (string.IsNullOrWhiteSpace(title)) return;
        if (selected.SegmentId is null)
        {
            await App.Services.ChapterAuthoring.UpdateChapterAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId, title);
        }
        else
        {
            await App.Services.ChapterAuthoring.RenameSegmentAsync(
                App.Services.Session.RequireProjectRoot(), selected.SegmentId, title);
        }

        await App.Services.Session.RefreshAsync();
    }

    private async void DuplicateSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (selected is null) return;
        if (selected.SegmentId is null)
        {
            await App.Services.ChapterAuthoring.DuplicateChapterAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId);
        }
        else
        {
            await App.Services.ChapterAuthoring.DuplicateSegmentAsync(
                App.Services.Session.RequireProjectRoot(), selected.SegmentId);
        }

        await App.Services.Session.RefreshAsync();
        ShowStatus(InfoBarSeverity.Success, "Duplicate created", "The copy has its own canonical source and stable ID.");
    }

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string offsetText } ||
            !int.TryParse(offsetText, out int offset) || GetSelectedRailItem() is not RailItem selected) return;
        if (selected.SegmentId is null)
        {
            await App.Services.ChapterAuthoring.MoveChapterAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId, offset);
        }
        else
        {
            await App.Services.ChapterAuthoring.MoveSegmentAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId, selected.SegmentId, offset);
        }

        await App.Services.Session.RefreshAsync();
    }

    private async void ToggleChapterInclusion_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        string? chapterId = selected?.ChapterId ?? _segment?.ChapterId;
        if (_project is null || chapterId is null) return;
        ChapterDocument chapter = _project.Chapters.Single(item => item.ChapterId == chapterId);
        await App.Services.ChapterAuthoring.UpdateChapterAsync(
            App.Services.Session.RequireProjectRoot(),
            chapterId,
            includedInCompilation: !chapter.IncludedInCompilation);
        await App.Services.Session.RefreshAsync();
    }

    private async void ArchiveChapter_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        string? chapterId = selected?.ChapterId ?? _segment?.ChapterId;
        if (chapterId is null) return;
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Archive this chapter?",
            Content = "Its files and history stay in the project, but it will leave the active rail and compilation.",
            PrimaryButtonText = "Archive",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await App.Services.ChapterAuthoring.UpdateChapterAsync(
            App.Services.Session.RequireProjectRoot(),
            chapterId,
            includedInCompilation: false,
            status: ChapterStatus.Archived);
        await App.Services.Session.RefreshAsync();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (_project is null || selected is null) return;
        if (_isDirty && !await SaveCurrentScriptAsync()) return;
        bool isChapter = selected.SegmentId is null;
        string name = isChapter
            ? _project.Chapters.Single(item => item.ChapterId == selected.ChapterId).Title
            : _project.Segments.Single(item => item.SegmentId == selected.SegmentId).Title;
        int segmentCount = isChapter
            ? _project.Chapters.Single(item => item.ChapterId == selected.ChapterId).SegmentOrder.Count
            : 1;
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {(isChapter ? "chapter" : "segment")} ‘{name}’?",
            Content = isChapter
                ? $"This removes the chapter and its {segmentCount} segment{(segmentCount == 1 ? "" : "s")} from VoiceBench. Canonical scripts move to 02-scripts/.trash; generated audio stays untouched."
                : "This removes the segment from its chapter. Its canonical script moves to 02-scripts/.trash; generated audio stays untouched.",
            PrimaryButtonText = "Move to trash",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (isChapter)
            {
                await App.Services.ChapterAuthoring.DeleteChapterAsync(
                    App.Services.Session.RequireProjectRoot(), selected.ChapterId);
            }
            else
            {
                await App.Services.ChapterAuthoring.DeleteSegmentAsync(
                    App.Services.Session.RequireProjectRoot(), selected.SegmentId!);
            }

            await App.Services.Session.RefreshAsync();
            ShowStatus(
                InfoBarSeverity.Success,
                isChapter ? "Chapter moved to trash" : "Segment moved to trash",
                "The canonical script can be recovered from 02-scripts/.trash. Generated audio was preserved.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Nothing was deleted", exception.Message);
        }
    }

    private async void DraftWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        TextBox titleBox = new() { Header = "Chapter title", PlaceholderText = "Coordinating the first review" };
        TextBox goalBox = new() { Header = "Goal", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
        TextBox audienceBox = new() { Header = "Audience", Text = project.WritingProfile.Audience };
        TextBox toneBox = new() { Header = "Tone", Text = project.WritingProfile.Tone };
        ToggleSwitch cueToggle = new()
        {
            Header = "Production guidance",
            OffContent = "Narration only",
            OnContent = "Generate video cues",
            IsOn = project.WritingProfile.GenerateProductionCues,
        };
        NumberBox durationBox = new() { Header = "Desired duration (minutes)", Value = 5, Minimum = 1, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        ComboBox providerBox = CreateWritingProviderPicker(project.WritingProfile.DefaultProviderId);
        ComboBox modelBox = new() { Header = "Exact model", HorizontalAlignment = HorizontalAlignment.Stretch };
        void SetDraftModels()
        {
            string providerId = GetSelectedContent(providerBox);
            WritingProviderDescriptor descriptor = App.Services.WritingProviders.Single(item => item.ProviderId == providerId);
            modelBox.Items.Clear();
            foreach (WritingModelDescriptor model in descriptor.Models)
                modelBox.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.ModelId });
            SetSelectedContent(modelBox,
                descriptor.Models.Any(model => model.ModelId == project.WritingProfile.DefaultModelId)
                    ? project.WritingProfile.DefaultModelId
                    : descriptor.Models[0].ModelId);
        }
        providerBox.SelectionChanged += (_, _) => SetDraftModels();
        SetDraftModels();
        ListView references = CreateReferencePicker(project, []);
        StackPanel fields = new() { Spacing = 10 };
        foreach (UIElement item in new UIElement[] { titleBox, goalBox, audienceBox, toneBox, cueToggle, durationBox, providerBox, modelBox,
            new TextBlock { Text = "Only checked local references are sent", Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"] }, references })
            fields.Children.Add(item);
        ContentDialog setup = new()
        {
            XamlRoot = XamlRoot,
            Title = "Draft a chapter with AI",
            Content = new ScrollViewer { Content = fields, MaxHeight = 560 },
            PrimaryButtonText = "Generate outline",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await setup.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(titleBox.Text) || string.IsNullOrWhiteSpace(goalBox.Text))
        {
            ShowStatus(InfoBarSeverity.Warning, "Chapter goal required", "Add a title and goal before generating an outline.");
            return;
        }

        string providerId = GetSelectedContent(providerBox);
        string modelId = GetSelectedContent(modelBox);
        string[] referenceIds = references.SelectedItems.OfType<ListViewItem>()
            .Select(item => item.Tag as string).OfType<string>().ToArray();
        WritingProfile profile = project.WritingProfile with
        {
            Audience = audienceBox.Text.Trim(),
            Tone = toneBox.Text.Trim(),
            DefaultProviderId = providerId,
            DefaultModelId = modelId,
            GenerateProductionCues = cueToggle.IsOn,
        };
        project = project with { WritingProfile = profile };
        await App.Services.Session.SaveAsync(project);
        try
        {
            WritingContext context = await App.Services.WritingContexts.LoadAsync(
                App.Services.Session.RequireProjectRoot(), goalBox.Text.Trim(), referenceIds);
            int targetSeconds = (int)Math.Round(durationBox.Value * 60);
            WritingOutlineRequest request = new(
                modelId,
                titleBox.Text.Trim(),
                goalBox.Text.Trim(),
                targetSeconds,
                context,
                EstimateWritingTokens(titleBox.Text, goalBox.Text, context));
            ShowStatus(InfoBarSeverity.Informational, "Generating outline", $"Sending only the brief and {referenceIds.Length} selected reference(s) to {providerId} / {modelId}.");
            ChapterOutline outline = await App.Services.WritingAssistant.CreateOutlineAsync(providerId, request);
            ChapterOutline? editedOutline = await ReviewOutlineAsync(outline);
            if (editedOutline is null) return;
            ShowStatus(InfoBarSeverity.Informational, "Writing full chapter", "Generating from the approved outline. No project files exist until you accept the preview.");
            ChapterDraft draft = await App.Services.WritingAssistant.CreateDraftAsync(
                providerId,
                new WritingDraftRequest(
                    modelId,
                    editedOutline,
                    context,
                    EstimateWritingTokens(goalBox.Text, context) + 500,
                    cueToggle.IsOn));
            if (!await PreviewDraftAsync(draft)) return;
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.CreateChapterFromDraftAsync(
                App.Services.Session.RequireProjectRoot(), draft, goalBox.Text.Trim(), referenceIds);
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(updated.Chapters.Single(chapter => chapter.ChapterId == updated.ChapterOrder.Last()).SegmentOrder.First());
            ShowStatus(InfoBarSeverity.Success, "AI draft accepted", "The chapter and canonical segment files were created atomically.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "AI chapter draft stopped", Actionable(exception));
        }
    }

    private async void WritingAction_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null || sender is not Button { Tag: string actionName } ||
            !Enum.TryParse(actionName, out WritingAction action)) return;
        if (!await SaveCurrentScriptAsync()) return;
        string providerId = GetSelectedContent(WritingProviderComboBox);
        string modelId = GetSelectedContent(WritingModelComboBox);
        (int selectionStart, int selectionLength, string selectedText) = GetEditorSelection();
        string originalWhole = GetEditorText();
        string target = selectionLength > 0 ? selectedText : originalWhole;
        string[] referenceIds = WritingReferencesList.SelectedItems.OfType<ListViewItem>()
            .Select(item => item.Tag as string).OfType<string>().ToArray();
        ChapterDocument? chapter = _project.Chapters.SingleOrDefault(item => item.ChapterId == _segment.ChapterId);
        try
        {
            WritingContext context = await App.Services.WritingContexts.LoadAsync(
                App.Services.Session.RequireProjectRoot(), chapter?.Brief ?? string.Empty, referenceIds);
            WritingRevisionRequest request = new(
                modelId,
                action,
                target,
                action == WritingAction.Custom ? CustomWritingInstructionTextBox.Text.Trim() : null,
                context,
                EstimateWritingTokens(target, CustomWritingInstructionTextBox.Text, context));
            ShowStatus(InfoBarSeverity.Informational, "Generating writing proposal", $"Using {providerId} / {modelId}. The canonical script will not change without acceptance.");
            WritingProposal partial = await App.Services.WritingAssistant.ReviseAsync(providerId, request);
            string proposedWhole = selectionLength > 0
                ? string.Concat(originalWhole.AsSpan(0, selectionStart), partial.ProposedText, originalWhole.AsSpan(selectionStart + selectionLength))
                : partial.ProposedText;
            WritingProposal proposal = partial with { ProposedText = proposedWhole };
            bool accept = await ShowWritingDiffAsync(originalWhole, proposal, action != WritingAction.Critique);
            if (!accept || action == WritingAction.Critique) return;
            _pendingWritingProposal = proposal;
            _pendingWritingAction = action;
            _pendingWritingProviderId = providerId;
            _pendingWritingModelId = modelId;
            _pendingWritingSourceHash = _segment.AuthoringSha256;
            await App.Services.ChapterAuthoring.ApplyWritingProposalAsync(
                App.Services.Session.RequireProjectRoot(),
                _segment.SegmentId,
                _pendingWritingSourceHash,
                proposal,
                action,
                providerId,
                modelId);
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(_segment.SegmentId);
            WritingProposalInfoBar.Message = $"Accepted {action}. A restorable local revision was saved.";
            WritingProposalInfoBar.IsOpen = true;
            bool narrationChanged = !string.Equals(
                SourceTextHasher.Compute(new ProductionScriptParser().Parse(originalWhole).NarrationText),
                SourceTextHasher.Compute(new ProductionScriptParser().Parse(proposal.ProposedText).NarrationText),
                StringComparison.OrdinalIgnoreCase);
            ShowStatus(
                InfoBarSeverity.Success,
                "Writing proposal accepted",
                narrationChanged
                    ? "Narration changed, so existing audio is stale and must be rerendered before Assembly."
                    : "Production guidance changed while the approved narration and audio remained current.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Writing proposal stopped", Actionable(exception));
        }
    }

    private async Task<bool> ShowWritingDiffAsync(string original, WritingProposal proposal, bool allowAccept)
    {
        ParsedScriptDocument originalParsed = new ProductionScriptParser().Parse(original);
        ParsedScriptDocument proposalParsed = new ProductionScriptParser().Parse(proposal.ProposedText);
        int originalWords = originalParsed.NarrationWordCount;
        int proposedWords = proposalParsed.NarrationWordCount;
        Grid comparison = new() { ColumnSpacing = 12, RowSpacing = 12 };
        bool sideBySide = ActualWidth >= 900;
        comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        comparison.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        comparison.RowDefinitions.Add(new RowDefinition { Height = sideBySide ? new GridLength(0) : GridLength.Auto });
        TextBox originalBox = new() { Header = "Original", Text = original, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 260 };
        TextBox proposedBox = new() { Header = allowAccept ? "Proposal" : "Critique context", Text = proposal.ProposedText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 260 };
        comparison.Children.Add(originalBox);
        Grid.SetColumn(proposedBox, sideBySide ? 1 : 0);
        Grid.SetRow(proposedBox, sideBySide ? 0 : 1);
        comparison.Children.Add(proposedBox);
        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"{proposal.Summary}\n\nSpoken words: {originalWords} → {proposedWords}  ·  Cues: {originalParsed.Cues.Count} → {proposalParsed.Cues.Count}  ·  Estimated duration: {FormatDuration(SegmentRenderService.EstimateDuration(originalParsed.NarrationText))} → {FormatDuration(SegmentRenderService.EstimateDuration(proposalParsed.NarrationText))}" +
                (proposal.Warnings.Count == 0 ? string.Empty : $"\nWarnings: {string.Join("; ", proposal.Warnings)}"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(comparison);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = allowAccept ? "Review original and proposal" : "Writing critique",
            Content = new ScrollViewer { Content = content, MaxHeight = 620 },
            PrimaryButtonText = allowAccept ? "Accept proposal" : null,
            CloseButtonText = allowAccept ? "Reject" : "Close",
            DefaultButton = allowAccept ? ContentDialogButton.Close : ContentDialogButton.None,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<ChapterOutline?> ReviewOutlineAsync(ChapterOutline outline)
    {
        TextBox title = new() { Header = "Chapter title", Text = outline.Title };
        StackPanel fields = new() { Spacing = 10 };
        fields.Children.Add(title);
        List<(TextBox Title, TextBox Purpose, NumberBox Seconds)> editors = [];
        foreach ((ChapterOutlineSegment segment, int index) in outline.Segments.Select((item, index) => (item, index)))
        {
            TextBox segmentTitle = new() { Header = $"Segment {index + 1} title", Text = segment.Title };
            TextBox purpose = new() { Header = "Purpose", Text = segment.Purpose };
            NumberBox seconds = new() { Header = "Estimated seconds", Value = segment.TargetSeconds, Minimum = 1, Maximum = 600 };
            editors.Add((segmentTitle, purpose, seconds));
            fields.Children.Add(segmentTitle);
            fields.Children.Add(purpose);
            fields.Children.Add(seconds);
        }
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Approve or edit the outline",
            Content = new ScrollViewer { Content = fields, MaxHeight = 580 },
            PrimaryButtonText = "Approve and draft",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return outline with
        {
            Title = title.Text.Trim(),
            Segments = editors.Select(item => new ChapterOutlineSegment(
                item.Title.Text.Trim(), item.Purpose.Text.Trim(), Math.Max(1, (int)item.Seconds.Value))).ToArray(),
        };
    }

    private async Task<bool> PreviewDraftAsync(ChapterDraft draft)
    {
        string preview = string.Join("\n\n", draft.Segments.Select(segment => $"## {segment.Title}\n\n{segment.Text}"));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Preview complete chapter — {draft.Title}",
            Content = new ScrollViewer
            {
                MaxHeight = 620,
                Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap },
            },
            PrimaryButtonText = "Accept and create chapter",
            CloseButtonText = "Reject",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ComboBox CreateWritingProviderPicker(string selectedProvider)
    {
        ComboBox box = new() { Header = "Provider", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (WritingProviderDescriptor provider in App.Services.WritingProviders)
            box.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.ProviderId });
        SetSelectedContent(box, selectedProvider);
        return box;
    }

    private static ListView CreateReferencePicker(VoiceBenchProjectDocument project, IEnumerable<string> selectedIds)
    {
        HashSet<string> selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        ListView list = new() { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 160 };
        foreach (SourceReferenceDocument reference in project.SourceReferences)
        {
            ListViewItem item = new() { Content = reference.Name, Tag = reference.ReferenceId };
            list.Items.Add(item);
            if (selected.Contains(reference.ReferenceId)) list.SelectedItems.Add(item);
        }
        return list;
    }

    private async Task<(ContentDialogResult Result, string Title, string Brief)> ShowNameDialogAsync(
        string dialogTitle, string titleHeader, string briefHeader, string primaryText)
    {
        TextBox title = new() { Header = titleHeader };
        TextBox brief = new() { Header = briefHeader, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(title);
        panel.Children.Add(brief);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = dialogTitle,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        return (result, title.Text.Trim(), brief.Text.Trim());
    }

    private async Task<string?> PromptForTextAsync(
        string dialogTitle, string header, string current, string primaryText)
    {
        TextBox box = new() { Header = header, Text = current };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = dialogTitle,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private RailItem? GetSelectedRailItem() => ChapterTree.SelectedNode?.Content as RailItem;

    private bool EnsureProjectForAuthoring()
    {
        if (App.Services.Session.HasProject) return true;
        ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Chapters are stored inside the active project folder.");
        return false;
    }

    private static int EstimateWritingTokens(params object?[] values)
    {
        int characters = values.Sum(value => value switch
        {
            null => 0,
            string text => text.Length,
            WritingContext context => context.References.Sum(reference => reference.Text.Length) +
                context.ChapterBrief.Length + context.Profile.Audience.Length + context.Profile.Tone.Length + context.Profile.StyleNotes.Length,
            _ => value.ToString()?.Length ?? 0,
        });
        return Math.Max(1, (int)Math.Ceiling(characters / 3d));
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void ScriptTextBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_ignoreTextChanges) return;
        string currentText = GetEditorText();
        if (string.Equals(currentText, _lastEditorText, StringComparison.Ordinal)) return;

        // RichEditBox also raises TextChanged for character-format changes. Remembering
        // the plain text prevents cue highlighting from scheduling itself forever.
        _lastEditorText = currentText;
        _isDirty = !string.Equals(currentText, _loadedText, StringComparison.Ordinal);
        UpdateEditorStats();
        QueueCueFormatting();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveCurrentScriptAsync())
            ShowStatus(InfoBarSeverity.Success, "Script saved", "The canonical script and manifest source state are current.");
    }

    private async Task<bool> SaveCurrentScriptAsync()
    {
        if (_project is null || _segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Open a project with at least one script segment.");
            return false;
        }

        string path = App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath);
        string externalHash = SourceTextHasher.Compute(await File.ReadAllTextAsync(path));
        if (!string.Equals(externalHash, _loadedDiskHash, StringComparison.OrdinalIgnoreCase) && !_externalOverwriteApproved)
        {
            ExternalEditBar.IsOpen = true;
            ShowStatus(InfoBarSeverity.Warning, "External edit needs a decision", "Reload, compare, or explicitly keep the current editor text before saving.");
            return false;
        }

        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, GetEditorText());
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        string editorText = GetEditorText();
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(editorText);
        string newHash = SourceTextHasher.Compute(parsed.NarrationText);
        string authoringHash = SourceTextHasher.Compute(editorText);
        SegmentDocument updated = SegmentStateRules.WithSourceHashes(_segment, newHash, authoringHash);
        int index = _project.Segments.FindIndex(item => item.SegmentId == _segment.SegmentId);
        List<SegmentDocument> segments = [.. _project.Segments];
        segments[index] = updated;
        await App.Services.Session.SaveAsync(_project with { Segments = segments });
        _project = App.Services.Session.RequireProject();
        _segment = _project.Segments.Single(item => item.SegmentId == updated.SegmentId);
        _loadedText = editorText;
        _loadedDiskHash = authoringHash;
        _externalOverwriteApproved = false;
        _isDirty = false;
        ExternalEditBar.IsOpen = false;
        UpdateEditorStats();
        return true;
    }

    private async void ReloadExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        await LoadSegmentAsync(_segment.SegmentId);
        ShowStatus(InfoBarSeverity.Informational, "External edit loaded", "The editor now shows the canonical file on disk.");
    }

    private async void KeepCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        string path = App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath);
        _loadedDiskHash = SourceTextHasher.Compute(await File.ReadAllTextAsync(path));
        _externalOverwriteApproved = true;
        if (await SaveCurrentScriptAsync())
            ShowStatus(InfoBarSeverity.Success, "Current editor text kept", "The explicit choice replaced the external version.");
    }

    private async void CompareExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        string external = await File.ReadAllTextAsync(App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Editor and external versions",
            CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock { Text = $"EDITOR\n\n{GetEditorText()}\n\n────────\nEXTERNAL FILE\n\n{external}", TextWrapping = TextWrapping.Wrap },
            },
        };
        await dialog.ShowAsync();
    }

    private void InsertCue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string type }) return;
        InsertCue(type);
    }

    private async void InsertCustomCue_Click(object sender, RoutedEventArgs e)
    {
        string? type = await PromptForCustomCueTypeAsync("Insert custom production cue", "Insert cue");
        if (type is not null) InsertCue(type);
    }

    private void InsertCue(string type)
    {
        if (_segment is null) return;
        string text = GetEditorText();
        (int start, int length, string selectedText) = GetEditorSelection();
        string replacement;
        int selectionStart;
        int selectionLength;
        if (length > 0)
        {
            string guidance = string.Join(' ', selectedText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            string before = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
            string after = start + length < text.Length && text[start + length] != '\n' ? "\n" : string.Empty;
            replacement = $"{before}@{type}: {guidance}{after}";
            selectionStart = start + before.Length + type.Length + 3;
            selectionLength = guidance.Length;
            ScriptTextBox.Document.GetRange(start, start + length).Text = replacement;
        }
        else
        {
            int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', Math.Min(start - 1, text.Length - 1)) + 1;
            replacement = $"@{type}: \n";
            ScriptTextBox.Document.GetRange(lineStart, lineStart).Text = replacement;
            selectionStart = lineStart + type.Length + 3;
            selectionLength = 0;
        }

        ScriptTextBox.Document.Selection.SetRange(selectionStart, selectionStart + selectionLength);
        ScriptTextBox.Focus(FocusState.Programmatic);
        QueueCueFormatting();
    }

    private void RemoveCueMarker_Click(object sender, RoutedEventArgs e)
    {
        string text = GetEditorText();
        int cursor = GetEditorSelection().Start;
        ProductionCue? cue = new ProductionScriptParser().Parse(text).Cues.FirstOrDefault(item =>
            cursor >= item.SourceStart && cursor <= item.SourceStart + item.SourceLength);
        if (cue is null)
        {
            ShowStatus(InfoBarSeverity.Informational, "No cue on this line", "Place the cursor in a production cue before removing its marker.");
            return;
        }

        ScriptTextBox.Document.GetRange(cue.SourceStart, cue.SourceStart + cue.SourceLength).Text = cue.Content;
        ScriptTextBox.Document.Selection.SetRange(cue.SourceStart, cue.SourceStart + cue.Content.Length);
        ScriptTextBox.Focus(FocusState.Programmatic);
    }

    private void ChangeCueType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string type }) ChangeCueType(type);
    }

    private async void ChangeCustomCueType_Click(object sender, RoutedEventArgs e)
    {
        string? type = await PromptForCustomCueTypeAsync("Change production cue type", "Change type");
        if (type is not null) ChangeCueType(type);
    }

    private void ChangeCueType(string type)
    {
        string text = GetEditorText();
        int cursor = GetEditorSelection().Start;
        ProductionCue? cue = new ProductionScriptParser().Parse(text).Cues.FirstOrDefault(item =>
            cursor >= item.SourceStart && cursor <= item.SourceStart + item.SourceLength);
        if (cue is null)
        {
            ShowStatus(InfoBarSeverity.Informational, "No cue on this line", "Place the cursor in a production cue before changing its type.");
            return;
        }

        string replacement = $"@{type}: {cue.Content}";
        ScriptTextBox.Document.GetRange(cue.SourceStart, cue.SourceStart + cue.SourceLength).Text = replacement;
        ScriptTextBox.Document.Selection.SetRange(cue.SourceStart, cue.SourceStart + replacement.Length);
        ScriptTextBox.Focus(FocusState.Programmatic);
        QueueCueFormatting();
    }

    private void ConvertLineToCue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string type }) ConvertCurrentLineToCue(type);
    }

    private async void ConvertLineToCustomCue_Click(object sender, RoutedEventArgs e)
    {
        string? type = await PromptForCustomCueTypeAsync("Convert line to custom cue", "Convert line");
        if (type is not null) ConvertCurrentLineToCue(type);
    }

    private void ConvertCurrentLineToCue(string type)
    {
        string text = GetEditorText();
        int cursor = Math.Clamp(GetEditorSelection().Start, 0, text.Length);
        int lineStart = cursor == 0 ? 0 : text.LastIndexOf('\n', Math.Min(cursor - 1, text.Length - 1)) + 1;
        int nextBreak = text.IndexOf('\n', cursor);
        int lineEnd = nextBreak < 0 ? text.Length : nextBreak;
        string guidance = text[lineStart..lineEnd].Trim();
        if (string.IsNullOrWhiteSpace(guidance))
        {
            ShowStatus(InfoBarSeverity.Informational, "This line is empty", "Place the cursor on a narration or note line to convert it into a cue.");
            return;
        }

        if (ProductionScriptParser.IsCueLine(text[lineStart..lineEnd]))
        {
            ShowStatus(InfoBarSeverity.Informational, "This line is already a cue", "Use Change cue type or Remove cue marker instead.");
            return;
        }

        string replacement = $"@{type}: {guidance}";
        ScriptTextBox.Document.GetRange(lineStart, lineEnd).Text = replacement;
        ScriptTextBox.Document.Selection.SetRange(lineStart, lineStart + replacement.Length);
        ScriptTextBox.Focus(FocusState.Programmatic);
        QueueCueFormatting();
    }

    private async Task<string?> PromptForCustomCueTypeAsync(string title, string primaryButtonText)
    {
        TextBox typeBox = new()
        {
            Header = "Custom cue type",
            PlaceholderText = "camera-note",
            Description = "Use lowercase letters, numbers, and single hyphens.",
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = typeBox,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        string type = typeBox.Text.Trim();
        ParsedScriptDocument validation = new ProductionScriptParser().Parse($"@{type}: guidance");
        if (!validation.HasErrors && validation.Cues.SingleOrDefault()?.Type == type) return type;
        ShowStatus(InfoBarSeverity.Warning, "Custom cue type is invalid", "Use a lowercase kebab-case type such as camera-note.");
        return null;
    }

    private async void ExportProductionPack_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || !await SaveCurrentScriptAsync()) return;
        try
        {
            ProductionExportResult result = await App.Services.ExportProductionPackAsync(_project.Name);
            ShowStatus(
                InfoBarSeverity.Success,
                "Production pack exported",
                $"Exported {result.SegmentCount} segments and {result.CueCount} cues to {result.ProductionScriptRelativePath}.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Production pack could not be exported", Actionable(exception));
        }
    }

    private async void GenerateProductionCuesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ignoreCueToggle || _project is null) return;
        WritingProfile profile = _project.WritingProfile with
        {
            GenerateProductionCues = GenerateProductionCuesToggle.IsOn,
        };
        await App.Services.Session.SaveAsync(_project with { WritingProfile = profile });
        _project = App.Services.Session.RequireProject();
    }

    private async void Pronunciation_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Pronunciations are stored in the project lexicon.");
            return;
        }

        TextBox writtenBox = new()
        {
            Header = "Written text",
            Text = GetEditorSelection().SelectedText.Trim(),
            PlaceholderText = "Navisworks",
        };
        TextBox spokenBox = new()
        {
            Header = "Speak as",
            PlaceholderText = "Nav-is-works",
        };
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(writtenBox);
        content.Children.Add(spokenBox);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Project pronunciation",
            PrimaryButtonText = "Save pronunciation",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(writtenBox.Text) || string.IsNullOrWhiteSpace(spokenBox.Text))
        {
            ShowStatus(InfoBarSeverity.Warning, "Pronunciation was not saved", "Both the written and spoken forms are required.");
            return;
        }

        List<PronunciationEntry> lexicon = [.. _project.Lexicon];
        PronunciationEntry entry = new()
        {
            Written = writtenBox.Text.Trim(),
            Spoken = spokenBox.Text.Trim(),
            WholeWord = true,
        };
        int existingIndex = lexicon.FindIndex(item => item.Written.Equals(entry.Written, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0) lexicon[existingIndex] = entry;
        else lexicon.Add(entry);
        await App.Services.Session.SaveAsync(_project with { Lexicon = lexicon });
        _project = App.Services.Session.RequireProject();
        ShowStatus(InfoBarSeverity.Success, "Pronunciation saved", $"Render copies will submit '{entry.Spoken}' wherever '{entry.Written}' appears as a whole word.");
    }

    private async void SplitAtCursor_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Choose a script segment before splitting it.");
            return;
        }

        int cursor = GetEditorSelection().Start;
        string editorText = GetEditorText();
        if (cursor <= 0 || cursor >= editorText.Length)
        {
            ShowStatus(InfoBarSeverity.Warning, "Place the cursor inside the script", "Both halves must contain narration text.");
            return;
        }

        string first = editorText[..cursor].TrimEnd();
        string second = editorText[cursor..].TrimStart();
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            ShowStatus(InfoBarSeverity.Warning, "That split would create an empty segment", "Move the cursor between two narration passages and try again.");
            return;
        }
        if (!await SaveCurrentScriptAsync()) return;
        try
        {
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.SplitSegmentAsync(
                App.Services.Session.RequireProjectRoot(),
                _segment.SegmentId,
                cursor,
                _segment.AuthoringSha256);
            await App.Services.Session.RefreshAsync();
            ChapterDocument chapter = updated.Chapters.Single(item => item.ChapterId == _segment.ChapterId);
            int originalIndex = chapter.SegmentOrder.IndexOf(_segment.SegmentId);
            string newSegmentId = chapter.SegmentOrder[originalIndex + 1];
            await LoadProjectAsync(newSegmentId);
            ShowStatus(InfoBarSeverity.Success, "Segment split", "Created a new segment immediately after the original.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Segment could not be split", exception.Message);
        }
    }

    private async void MarkReady_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveCurrentScriptAsync() || _project is null || _segment is null) return;
        if (!ValidateScriptForRender()) return;
        await SetSegmentStatusAsync(SegmentStatus.Ready);
        ShowStatus(InfoBarSeverity.Success, "Segment marked Ready", "It can now be rendered with the exact selected preset.");
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Open a project and select a script segment.");
            return;
        }

        if (_renderCancellation is not null)
        {
            ShowStatus(InfoBarSeverity.Warning, "A render is already active", "Cancel the current job or wait for it to finish.");
            return;
        }

        try
        {
            if (!await SaveCurrentScriptAsync()) return;
            if (!ValidateScriptForRender()) return;
            VoicePreset preset = GetOrCreateSelectedVoicePreset(
                _project,
                setAsProjectDefault: _project.DefaultVoicePresetId is null,
                out VoiceBenchProjectDocument projectWithPreset);
            int index = projectWithPreset.Segments.FindIndex(item => item.SegmentId == _segment.SegmentId);
            List<SegmentDocument> segments = [.. projectWithPreset.Segments];
            segments[index] = _segment with { VoicePresetId = preset.PresetId, Directions = DirectionTextBox.Text.Trim(), Status = SegmentStatus.Ready };
            VoiceBenchProjectDocument renderProject = projectWithPreset with { Segments = segments };
            _project = renderProject;
            _segment = segments[index];
            await App.Services.Session.SaveAsync(renderProject);
            ShowStatus(InfoBarSeverity.Informational, "Rendering segment", "VoiceBench is submitting the exact provider, model, voice, and direction you selected.");
            _renderCancellation = new CancellationTokenSource();
            SetRenderQueueBusy(true);
            SegmentRenderResult result = await App.Services.RenderSegmentAsync(_segment.SegmentId, _renderCancellation.Token);
            string guidance = result.ExceedsFourMinuteGuidance ? " The estimated duration exceeds four minutes; split it before the next take." : string.Empty;
            ShowStatus(InfoBarSeverity.Success, $"Take {result.Render.TakeNumber:00} rendered", $"Immutable raw audio was saved at {result.Render.RawAudioRelativePath}.{guidance}");
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Render cancelled", "The queued job is journaled as cancelled; existing takes were not changed.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Render failed", Actionable(exception));
        }
        finally
        {
            _renderCancellation?.Dispose();
            _renderCancellation = null;
            SetRenderQueueBusy(false);
        }
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e) => _renderCancellation?.Cancel();

    private void SetRenderQueueBusy(bool busy)
    {
        RenderQueueStatusText.Text = busy ? "1 ACTIVE" : "IDLE";
        RenderProgressBar.IsIndeterminate = busy;
        RenderProgressBar.Value = busy ? 50 : 0;
        CancelRenderButton.IsEnabled = busy;
    }

    private async void Pickup_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null) return;
        await SetSegmentStatusAsync(SegmentStatus.PickupRequired);
        ShowStatus(InfoBarSeverity.Warning, "Pickup requested", "Existing takes remain immutable while you prepare a replacement.");
    }

    private async Task SetSegmentStatusAsync(SegmentStatus status)
    {
        VoiceBenchProjectDocument project = _project ?? throw new InvalidOperationException("No project is open.");
        SegmentDocument segment = _segment ?? throw new InvalidOperationException("No segment is selected.");
        int index = project.Segments.FindIndex(item => item.SegmentId == segment.SegmentId);
        List<SegmentDocument> segments = [.. project.Segments];
        segments[index] = segment with { Status = status };
        await App.Services.Session.SaveAsync(project with { Segments = segments });
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Select a rendered segment first.");
            return;
        }

        try
        {
            ShowStatus(InfoBarSeverity.Informational, "Preparing take", "VoiceBench is converting and verifying the latest generated take with FFmpeg.");
            RenderRecord approved = await App.Services.ProcessAndApproveLatestAsync(_segment.SegmentId);
            ShowStatus(InfoBarSeverity.Success, $"Take {approved.TakeNumber:00} approved", "The processed take is approved against the current canonical source.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Take could not be approved", Actionable(exception));
        }
    }

    private void OpenAssembly_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("assembly");

    private async void UseProjectVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Project voice settings are stored in the active project folder.");
            return;
        }

        try
        {
            VoicePreset preset = GetOrCreateSelectedVoicePreset(_project, true, out VoiceBenchProjectDocument updated);
            _project = updated;
            await App.Services.Session.SaveAsync(updated);
            VoicePresetInfoBar.Severity = InfoBarSeverity.Success;
            VoicePresetInfoBar.Title = "Project voice selected";
            VoicePresetInfoBar.Message = $"{preset.ProviderId} / {preset.ModelId} / {preset.Voice}. You can render immediately; auditioning remains optional.";
            VoicePresetInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Project voice was not changed", exception.Message);
        }
    }

    private VoicePreset GetOrCreateSelectedVoicePreset(
        VoiceBenchProjectDocument project,
        bool setAsProjectDefault,
        out VoiceBenchProjectDocument updated)
    {
        string providerId = GetSelectedProviderId();
        string modelId = GetSelectedContent(ModelComboBox);
        string voice = GetSelectedContent(VoiceComboBox);
        VoicePreset? preset = project.VoicePresets.FirstOrDefault(item =>
            item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            && item.ModelId.Equals(modelId, StringComparison.Ordinal)
            && item.Voice.Equals(voice, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(item.Directions));
        List<VoicePreset> presets = [.. project.VoicePresets];
        if (preset is null)
        {
            SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
                item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
            preset = new VoicePreset
            {
                PresetId = $"direct-{Guid.NewGuid():N}",
                Name = $"{provider.DisplayName} · {voice}",
                ProviderId = providerId,
                ModelId = modelId,
                Voice = voice,
            };
            presets.Add(preset);
        }

        updated = project with
        {
            VoicePresets = presets,
            DefaultVoicePresetId = setAsProjectDefault ? preset.PresetId : project.DefaultVoicePresetId,
        };
        return preset;
    }

    private void LoadDefaultPreset(VoiceBenchProjectDocument project)
    {
        VoicePreset? preset = project.DefaultVoicePresetId is null ? null : project.VoicePresets.SingleOrDefault(item => item.PresetId == project.DefaultVoicePresetId);
        VoicePresetInfoBar.IsOpen = true;
        if (preset is null)
        {
            VoicePresetInfoBar.Severity = InfoBarSeverity.Informational;
            VoicePresetInfoBar.Title = "Choose any voice";
            VoicePresetInfoBar.Message = "Select a provider, model, and voice. Auditioning is optional.";
            return;
        }

        SetPresetControls(preset);
        VoicePresetInfoBar.Severity = InfoBarSeverity.Success;
        VoicePresetInfoBar.Title = "Project voice selected";
        VoicePresetInfoBar.Message = $"{preset.ProviderId} / {preset.ModelId} / {preset.Voice}";
    }

    private void LoadSegmentPreset(VoiceBenchProjectDocument project, SegmentDocument segment)
    {
        string? presetId = segment.VoicePresetId ?? project.DefaultVoicePresetId;
        VoicePreset? preset = project.VoicePresets.SingleOrDefault(item => item.PresetId == presetId);
        if (preset is null) return;
        SetPresetControls(preset);
        DirectionTextBox.Text = segment.Directions ?? preset.Directions ?? string.Empty;
    }

    private void SetPresetControls(VoicePreset preset)
    {
        SetSelectedContent(ProviderComboBox, preset.ProviderId == "openai" ? "OpenAI" : "Google Gemini");
        SetSelectedContent(ModelComboBox, preset.ModelId);
        SetSelectedContent(VoiceComboBox, preset.Voice);
    }

    private void PopulateTakeSelector(SegmentDocument segment)
    {
        TakeSelector.Items.Clear();
        foreach (RenderRecord render in segment.RenderHistory.OrderByDescending(item => item.TakeNumber))
            TakeSelector.Items.Add(new ComboBoxItem { Content = $"Take {render.TakeNumber:00}", Tag = render.RenderId });
        TakeSelector.SelectedIndex = TakeSelector.Items.Count > 0 ? 0 : -1;
        UpdateTransport();
    }

    private void ResetTakeReviewContext()
    {
        TakeReviewTitleText.Text = "No segment selected";
        TakeReviewStatusText.Text = "NO TAKES";
        TakeSelector.Items.Clear();
        TakeSelector.SelectedIndex = -1;
        PreviousTakeButton.IsEnabled = false;
        PickupButton.IsEnabled = false;
        ApproveButton.IsEnabled = false;
    }

    private void TakeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTransport();

    private void UpdateTransport()
    {
        RenderRecord? render = GetSelectedRender();
        bool hasTake = render is not null;
        PreviousTakeButton.IsEnabled = TakeSelector.SelectedIndex >= 0 && TakeSelector.SelectedIndex < TakeSelector.Items.Count - 1;
        PickupButton.IsEnabled = hasTake;
        ApproveButton.IsEnabled = render?.Status == RenderStatus.Generated;
        if (render is null)
        {
            TakeTransport.ClearAudio("NO TAKE SELECTED");
            return;
        }

        string relativePath = render.ProcessedAudioRelativePath ?? render.RawAudioRelativePath;
        string path = App.Services.Session.ResolveProjectPath(relativePath);
        if (File.Exists(path))
        {
            TakeTransport.LoadAudio(path, $"Take {render.TakeNumber:00} · {render.Voice}", render.Duration);
        }
        else
        {
            TakeTransport.ClearAudio("TAKE AUDIO MISSING");
        }
    }

    private RenderRecord? GetSelectedRender()
    {
        if (_segment is null || TakeSelector.SelectedItem is not ComboBoxItem { Tag: Guid renderId }) return null;
        return _segment.RenderHistory.SingleOrDefault(item => item.RenderId == renderId);
    }

    private void PreviousTake_Click(object sender, RoutedEventArgs e)
    {
        if (TakeSelector.SelectedIndex >= 0 && TakeSelector.SelectedIndex < TakeSelector.Items.Count - 1)
            TakeSelector.SelectedIndex++;
    }

    private void UpdateEditorStats()
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(GetEditorText());
        TimeSpan duration = SegmentRenderService.EstimateDuration(parsed.NarrationText);
        string diagnostics = parsed.HasErrors ? $"  ·  {parsed.Diagnostics.Count} ISSUE{(parsed.Diagnostics.Count == 1 ? "" : "S")}" : string.Empty;
        SegmentStatsText.Text = $"{parsed.NarrationWordCount} SPOKEN WORDS  ·  {parsed.Cues.Count} CUES  ·  EST. {FormatDuration(duration)}  ·  {(_isDirty ? "UNSAVED" : "SAVED")}{diagnostics}";
    }

    private bool ValidateScriptForRender()
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(GetEditorText());
        if (parsed.HasErrors)
        {
            ScriptDocumentDiagnostic first = parsed.Diagnostics[0];
            ShowStatus(
                InfoBarSeverity.Warning,
                "Fix production cues before rendering",
                $"Line {first.SourceLine}: {first.Message}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.NarrationText))
        {
            ShowStatus(
                InfoBarSeverity.Warning,
                "This segment has no narration",
                "Production cues are not spoken. Add narration before marking the segment ready or rendering it.");
            return false;
        }

        return true;
    }

    private string GetEditorText()
    {
        ScriptTextBox.Document.GetText(TextGetOptions.None, out string text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private (int Start, int Length, string SelectedText) GetEditorSelection()
    {
        ITextSelection selection = ScriptTextBox.Document.Selection;
        int start = Math.Min(selection.StartPosition, selection.EndPosition);
        int end = Math.Max(selection.StartPosition, selection.EndPosition);
        string selected = selection.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (end >= GetEditorText().Length && selected.EndsWith('\n')) selected = selected[..^1];
        return (start, Math.Max(0, end - start), selected);
    }

    private void QueueCueFormatting()
    {
        if (_cueFormatTimer is null) return;
        _cueFormatTimer.Stop();
        _cueFormatTimer.Start();
    }

    private void ApplyCueFormatting()
    {
        if (_ignoreTextChanges || _cueFormattingUnavailable) return;
        string text = GetEditorText();
        if (text.Length == 0) return;
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        ITextSelection selection = ScriptTextBox.Document.Selection;
        int selectionStart = selection.StartPosition;
        int selectionEnd = selection.EndPosition;
        _ignoreTextChanges = true;
        try
        {
            ITextRange all = ScriptTextBox.Document.GetRange(0, text.Length);
            ITextCharacterFormat baseFormat = all.CharacterFormat;
            baseFormat.ForegroundColor = ThemeColor("ReadingTextBrush", Color.FromArgb(255, 255, 255, 255));
            baseFormat.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            baseFormat.Bold = FormatEffect.Off;
            all.CharacterFormat = baseFormat;

            foreach (ProductionCue cue in parsed.Cues)
            {
                Color accent = cue.Kind switch
                {
                    ProductionCueKind.Visual => ThemeColor("ActiveSignalBrush", Color.FromArgb(255, 53, 199, 232)),
                    ProductionCueKind.Action => ThemeColor("PickupBrush", Color.FromArgb(255, 244, 155, 82)),
                    ProductionCueKind.Screen => ThemeColor("ApprovalBrush", Color.FromArgb(255, 88, 197, 139)),
                    ProductionCueKind.Note => ThemeColor("CueNoteBrush", Color.FromArgb(255, 179, 157, 219)),
                    _ => ThemeColor("CueCustomBrush", Color.FromArgb(255, 170, 176, 188)),
                };
                ITextRange range = ScriptTextBox.Document.GetRange(
                    cue.SourceStart,
                    Math.Min(text.Length, cue.SourceStart + cue.SourceLength));
                ITextCharacterFormat format = range.CharacterFormat;
                format.ForegroundColor = accent;
                format.BackgroundColor = Color.FromArgb(30, accent.R, accent.G, accent.B);
                format.Bold = FormatEffect.On;
                range.CharacterFormat = format;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Some RichEdit hosts reject TOM color mutations while the document is initializing.
            // Highlighting is decorative; keep the plain-text editor usable and stop retrying.
            _cueFormattingUnavailable = true;
            ShowStatus(
                InfoBarSeverity.Informational,
                "Cue highlighting unavailable",
                "Cue labels and production behavior are still active; this editor session will use plain-text styling.");
        }
        finally
        {
            ScriptTextBox.Document.Selection.SetRange(selectionStart, selectionEnd);
            _ignoreTextChanges = false;
        }
    }

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : fallback;

    private void SetEditorText(string text)
    {
        bool wasEnabled = ScriptTextBox.IsEnabled;
        bool wasReadOnly = ScriptTextBox.IsReadOnly;
        _ignoreTextChanges = true;
        try
        {
            // RichEdit's text document rejects SetText with E_ACCESSDENIED while its
            // owning control is disabled or read-only. Empty-project presentation
            // uses both states, so briefly unlock only the programmatic document load.
            ScriptTextBox.IsEnabled = true;
            ScriptTextBox.IsReadOnly = false;
            ScriptTextBox.Document.SetText(TextSetOptions.None, text);
            _lastEditorText = GetEditorText();
        }
        finally
        {
            ScriptTextBox.IsReadOnly = wasReadOnly;
            ScriptTextBox.IsEnabled = wasEnabled;
            _ignoreTextChanges = false;
        }

        _isDirty = false;
        QueueCueFormatting();
    }

    private void SelectTakeRailItem(string? segmentId)
    {
        if (segmentId is null) return;
        TreeViewNode? node = ChapterTree.RootNodes
            .SelectMany(chapter => chapter.Children)
            .FirstOrDefault(candidate => candidate.Content is RailItem item &&
                string.Equals(item.SegmentId, segmentId, StringComparison.Ordinal));
        if (node is not null) ChapterTree.SelectedNode = node;
        _ignoreCompactPicker = true;
        CompactSegmentPicker.SelectedItem = CompactSegmentPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, segmentId, StringComparison.Ordinal));
        _ignoreCompactPicker = false;
    }

    private string GetSelectedProviderId() =>
        (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string
        ?? throw new InvalidOperationException("Choose an exact speech provider.");

    private static string GetSelectedContent(ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem item
            ? item.Tag as string ?? item.Content?.ToString()
                ?? throw new InvalidOperationException("The selected option has no identifier.")
            : throw new InvalidOperationException($"Choose {comboBox.Header?.ToString()?.ToLowerInvariant() ?? "a value"}.");

    private static void SetSelectedContent(ComboBox comboBox, string content)
    {
        ComboBoxItem? item = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, content, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ComboBoxItem { Content = content };
            comboBox.Items.Add(item);
        }
        comboBox.SelectedItem = item;
    }

    private static string FormatStatus(SegmentStatus status) => status == SegmentStatus.PickupRequired ? "PICKUP REQUIRED" : status.ToString().ToUpperInvariant();

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private static string Actionable(Exception exception) => exception switch
    {
        SpeechProviderException { Kind: SpeechProviderFailureKind.Credentials } => $"{exception.Message} Open Settings and add the exact provider key.",
        SpeechProviderException { Kind: SpeechProviderFailureKind.Authentication } => $"{exception.Message} Replace the provider key in Settings and try again.",
        WritingProviderException { Kind: WritingProviderFailureKind.Credentials } => $"{exception.Message} Open Settings and add the exact provider key.",
        WritingProviderException { Kind: WritingProviderFailureKind.Authentication } => $"{exception.Message} Replace the provider key in Settings and try again.",
        FileNotFoundException => $"{exception.Message} Open Settings to download and verify the pinned FFmpeg toolchain.",
        _ => exception.Message,
    };

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        WorkbenchInfoBar.Severity = severity;
        WorkbenchInfoBar.Title = title;
        WorkbenchInfoBar.Message = message;
        WorkbenchInfoBar.IsOpen = true;
    }
}
