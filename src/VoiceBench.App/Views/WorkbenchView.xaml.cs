using System.Diagnostics;
using System.Runtime.InteropServices;
using IssueLab.VoiceBench.App.Controls;
using IssueLab.VoiceBench.App.Automation;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private const int EditorHistoryLimit = 200;
    private const string EditorFontSizeSetting = "Workbench.EditorFontSize";
    private const string ChapterRailVisibilitySetting = "Workbench.ShowChapterRail";
    private const string DirectorVisibilitySetting = "Workbench.ShowDirector";
    private const string TakeReviewVisibilitySetting = "Workbench.ShowTakeReview";
    private const string FollowScriptSetting = "Workbench.FollowScript";
    private const int MaxCueFormattingRetries = 4;

    private VoiceBenchProjectDocument? _project;
    private SegmentDocument? _segment;
    private IReadOnlyDictionary<string, TimeSpan> _estimatedSegmentDurations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
    private string _loadedText = string.Empty;
    private string _loadedDiskHash = string.Empty;
    private bool _ignoreTextChanges;
    private bool _isDirty;
    private bool _ignoreDirectionChanges;
    private bool _directionsDirty;
    private string? _directionDraftSegmentId;
    private string _loadedDirectionText = string.Empty;
    private bool _externalOverwriteApproved;
    private CancellationTokenSource? _renderCancellation;
    private CancellationTokenSource? _renderRequestCancellation;
    private CancellationTokenSource? _alignmentCancellation;
    private WritingProposal? _pendingWritingProposal;
    private WritingAction _pendingWritingAction;
    private string? _pendingWritingProviderId;
    private string? _pendingWritingModelId;
    private string? _pendingWritingSourceHash;
    private bool _ignoreCompactPicker;
    private ScriptFileMonitor? _scriptFileMonitor;
    private string? _monitoredProjectRoot;
    private int _scriptChangeVersion;
    private int _projectChangeVersion;
    private Task _pendingProjectRefresh = Task.CompletedTask;
    private int _transportLoadVersion;
    private bool _suppressProjectChangedReload;
    private string _lastEditorText = string.Empty;
    private string _lastEditorDirections = string.Empty;
    private DispatcherQueueTimer? _cueFormatTimer;
    private DispatcherQueueTimer? _editorHistoryTimer;
    private bool _ignoreCueToggle;
    private int _cueFormattingRetryCount;
    private bool _writingActionInProgress;
    private bool _authoringHistoryOperationInProgress;
    private bool _followSessionActive;
    private bool _restoringPlaybackSurface;
    private int _activeFollowWordOrdinal = -1;
    private string? _alignmentRecoveryRoot;
    private bool _listenTakePending;
    private WordAlignmentDocument? _activeWordAlignment;
    private WordAlignmentRecord? _activeAlignmentRecord;
    private RenderRecord? _activeAlignmentRender;
    private FollowEditorSnapshot? _followEditorSnapshot;
    private IReadOnlyList<NarrationWord>? _followNarrationWords;
    private int _followEditorTextLength;
    private readonly List<EditorHistorySnapshot> _editorUndoHistory = [];
    private readonly List<EditorHistorySnapshot> _editorRedoHistory = [];
    private EditorHistorySnapshot? _pendingEditorUndo;
    private int _lastEditorSelectionStart;
    private int _lastEditorSelectionEnd;
    private AuthoringTransactionState _authoringHistory = new(
        AuthoringTransactionState.CurrentSchemaVersion,
        [],
        0,
        false);

    private bool HasUnsavedSegmentChanges => _isDirty || _directionsDirty;

    private sealed record RailItem(
        string Title,
        string Metadata,
        string ChapterId,
        string? SegmentId,
        Brush StatusBrush);

    private sealed record RevisionChoice(Guid RevisionId, bool RestorePreviousText);

    private sealed record EditorHistorySnapshot(string Text, int SelectionStart, int SelectionEnd, string? Directions = null);

    private sealed record FollowEditorSnapshot(
        int SelectionStart,
        int SelectionEnd,
        bool WasReadOnly,
        bool WasFocused);

    public WorkbenchView()
    {
        InitializeComponent();
        AudioDock.Compact = true;
        App.Services.SelectedTakePlayback = TakeTransport.PlayAsync;
        App.Services.SelectedTakeSeek = TakeTransport.SeekToAsync;
        App.Services.SelectedTakeState = TakeTransport.ReadSelectedTakeState;
        _cueFormatTimer = DispatcherQueue.CreateTimer();
        _cueFormatTimer.Interval = TimeSpan.FromMilliseconds(150);
        _cueFormatTimer.IsRepeating = false;
        _cueFormatTimer.Tick += (_, _) => ApplyCueFormatting();
        _editorHistoryTimer = DispatcherQueue.CreateTimer();
        _editorHistoryTimer.Interval = TimeSpan.FromMilliseconds(650);
        _editorHistoryTimer.IsRepeating = false;
        _editorHistoryTimer.Tick += (_, _) => CommitPendingEditorUndo();
        ScriptTextBox.Document.UndoLimit = 0;
        LoadWorkspacePanelPreferences();
        LoadEditorFontSizePreference();
        LoadFollowScriptPreference();
        EditorFontSizeSlider.ValueChanged += EditorFontSizeSlider_ValueChanged;
        TakeTransport.PlaybackStarted += (_, _) => BeginFollowSession();
        TakeTransport.PlaybackPositionChanged += TakeTransport_PlaybackPositionChanged;
        TakeTransport.PlaybackStateChanged += TakeTransport_PlaybackStateChanged;
        InitializeMixFollow();
        SetSelectedContent(ProviderComboBox, "OpenAI");
        PopulateProviderControls("openai", "gpt-4o-mini-tts", "marin");
        PopulateWritingProviders();
        VoiceComboBox.SelectionChanged += (_, _) => InvalidateAutomationRevision();
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width, args.NewSize.Height);
        Loaded += WorkbenchView_Loaded;
        Unloaded += WorkbenchView_Unloaded;
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    private void ApplyResponsiveState(double width, double height)
    {
        VisualStateManager.GoToState(
            this,
            width >= 1180 ? "WideWorkbench" : width >= 860 ? "CompactWorkbench" : "NarrowWorkbench",
            false);
        WorkbenchInfoBar.Width = Math.Max(280, Math.Min(560, width - (width >= 860 ? 40 : 24)));
        WorkbenchInfoBar.Margin = width >= 860
            ? new Thickness(20, 76, 20, 0)
            : new Thickness(12, 118, 12, 0);
        ApplyWorkspacePanelLayout(width, height);
    }

    private void WorkspacePanelToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveWorkspacePanelPreferences();
        ApplyWorkspacePanelLayout(ActualWidth, ActualHeight);
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

    private void LoadEditorFontSizePreference()
    {
        object? storedValue = ApplicationData.Current.LocalSettings.Values[EditorFontSizeSetting];
        double fontSize = storedValue switch
        {
            double value => value,
            int value => value,
            _ => 15,
        };
        ApplyEditorFontSize(fontSize, persist: false);
    }

    private void EditorFontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ApplyEditorFontSize(e.NewValue, persist: true);

    private void ApplyEditorFontSize(double value, bool persist)
    {
        double fontSize = Math.Clamp(Math.Round(value), 12, 24);
        EditorFontSizeSlider.Value = fontSize;
        EditorFontSizeValueText.Text = $"{fontSize:0}";
        EditorFontSizeButton.Label = $"{fontSize:0} px";
        ScriptTextBox.FontSize = fontSize;
        if (persist)
            ApplicationData.Current.LocalSettings.Values[EditorFontSizeSetting] = fontSize;
        QueueCueFormatting();
    }

    private void LoadFollowScriptPreference()
    {
        object? value = ApplicationData.Current.LocalSettings.Values[FollowScriptSetting];
        FollowScriptToggle.IsChecked = value is not bool enabled || enabled;
    }

    private void ApplyWorkspacePanelLayout(double width, double height)
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
        double directorShare = narrow
            ? height < 720 ? 0.55 : 0.8
            : height < 720 ? 0.45 : 0.7;
        DirectorRow.Height = !wide && showDirector
            ? new GridLength(directorShare, GridUnitType.Star)
            : new GridLength(0);
        DirectorPanel.SetValue(Grid.RowProperty, wide ? 0 : 1);
        DirectorPanel.SetValue(Grid.ColumnProperty, wide ? 2 : 1);

        WorkbenchFooter.Visibility = Visibility.Visible;
        Visibility review = TakeReviewToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TakeReviewContext.Visibility = review; TakeSelectionControls.Visibility = review;
        TakeDecisionGroup.Visibility = review; AssemblyHandoff.Visibility = review;
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelComboBox is null || VoiceComboBox is null || ProviderComboBox.SelectedItem is null) return;
        InvalidateAutomationRevision();
        PopulateProviderControls(GetSelectedProviderId(), null, null);
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox is null || ModelComboBox.SelectedItem is null || ProviderComboBox.SelectedItem is null) return;
        InvalidateAutomationRevision();
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
        string? currentVoice = preferredVoice ?? GetSelectedContentOrNull(VoiceComboBox);
        VoiceComboBox.Items.Clear();
        foreach (SpeechVoiceDescriptor voice in model.VoiceDescriptors)
        {
            VoiceComboBox.Items.Add(VoiceComboBoxItemFactory.Create(voice));
        }

        string selectedVoice = currentVoice is not null && model.Voices.Contains(currentVoice, StringComparer.OrdinalIgnoreCase)
            ? currentVoice
            : model.Voices[0];
        SetSelectedContent(VoiceComboBox, selectedVoice);
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

    private async void WorkbenchView_Loaded(object sender, RoutedEventArgs e)
    {
        _pendingProjectRefresh = LoadProjectAsync();
        await _pendingProjectRefresh;
    }

    private void WorkbenchView_Unloaded(object sender, RoutedEventArgs e)
    {
        EndFollowSession();
        _renderCancellation?.Cancel();
        _renderRequestCancellation?.Cancel();
        _alignmentCancellation?.Cancel();
        _alignmentCancellation?.Dispose();
        _alignmentCancellation = null;
        _cueFormatTimer?.Stop();
        _editorHistoryTimer?.Stop();
        DisposeScriptFileMonitor();
    }

    private void DisposeScriptFileMonitor()
    {
        _scriptFileMonitor?.Dispose();
        _scriptFileMonitor = null;
        _monitoredProjectRoot = null;
    }

    private void Session_ProjectChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(InvalidateMixFollowForProject);
        if (_suppressProjectChangedReload) return;
        int version = Interlocked.Increment(ref _projectChangeVersion);
        string? preferredSegmentId = _segment?.SegmentId;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingProjectRefresh = completion.Task;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_suppressProjectChangedReload || version != _projectChangeVersion)
                {
                    completion.TrySetResult();
                    return;
                }
                if (_project is { } previous && App.Services.Session.Project is { } refreshed
                    && string.Equals(_monitoredProjectRoot,App.Services.Session.ProjectRoot,StringComparison.OrdinalIgnoreCase)
                    && IssueLab.VoiceBench.App.Services.TimelinePresentation.SameScriptWorkspace(previous,refreshed))
                {
                    _project = refreshed;
                    if (_segment is not null) _segment = refreshed.Segments.FirstOrDefault(item => item.SegmentId == _segment.SegmentId);
                    await RefreshAuthoringHistoryAsync();
                    completion.TrySetResult();
                    return;
                }
                await LoadProjectAsync(preferredSegmentId);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                ShowStatus(InfoBarSeverity.Error, "Project refresh stopped", Actionable(exception));
                completion.TrySetException(exception);
            }
        })) completion.TrySetException(new InvalidOperationException("The desktop is closing."));
    }

    internal async Task WaitForProjectRefreshAsync(CancellationToken cancellationToken)
    {
        Task pending;
        do
        {
            pending = _pendingProjectRefresh;
            await pending.WaitAsync(cancellationToken);
        } while (!ReferenceEquals(pending, _pendingProjectRefresh));
    }

    private async Task LoadProjectAsync(string? preferredSegmentId = null)
    {
        if (!App.Services.Session.HasProject)
        {
            _alignmentRecoveryRoot = null;
            EndFollowSession();
            DisposeScriptFileMonitor();
            _project = null;
            _segment = null;
            _estimatedSegmentDurations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            ChapterTree.RootNodes.Clear();
            CompactSegmentPicker.Items.Clear();
            TakeTransport.ClearAudio();
            ResetTakeReviewContext();
            SetEditorText(string.Empty);
            SetEditorAvailability(hasSegment: false, showEmptyState: false);
            ShowStatus(InfoBarSeverity.Informational, "Open a project", "Create or open a folder-based project from Projects.");
            await RefreshAuthoringHistoryAsync();
            return;
        }

        string projectRoot = App.Services.Session.RequireProjectRoot();
        bool preserveDraft = HasUnsavedSegmentChanges && _segment is not null &&
            string.Equals(_monitoredProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase) &&
            App.Services.Session.RequireProject().Segments.Any(item => item.SegmentId == _segment.SegmentId) &&
            (preferredSegmentId is null || preferredSegmentId == _segment.SegmentId);
        if (!string.Equals(_alignmentRecoveryRoot, projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            _alignmentRecoveryRoot = projectRoot;
            await App.Services.RecoverWordAlignmentsAsync();
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
        _estimatedSegmentDurations = await NarrationDurationProjection.LoadEstimatesAsync(
            App.Services.Session.RequireProjectRoot(),
            _project.Segments);
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
            await RefreshAuthoringHistoryAsync();
            return;
        }

        SetEditorAvailability(hasSegment: true, showEmptyState: false);
        SelectTakeRailItem(segmentId);
        preserveDraft = preserveDraft || (HasUnsavedSegmentChanges && _segment?.SegmentId == segmentId &&
            string.Equals(_monitoredProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase));
        if (preserveDraft && _segment?.SegmentId == segmentId)
        {
            _segment = _project.Segments.Single(item => item.SegmentId == segmentId);
            UpdateEditorStats();
            PopulateTakeSelector(_segment);
            PopulateWritingRevisions(_segment);
            if (!_directionsDirty) LoadSegmentPreset(_project, _segment);
        }
        else await LoadSegmentAsync(segmentId);
        WorkbenchInfoBar.IsOpen = false;
        await RefreshAuthoringHistoryAsync();
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
                    (Brush)Microsoft.UI.Xaml.Application.Current.Resources[chapter.IncludedInCompilation ? "ActiveSignalBrush" : "MutedTextBrush"]),
            };
            int segmentNumber = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                segmentNumber++;
                string takes = segment.RenderHistory.Count == 0
                    ? "no takes"
                    : $"take {segment.RenderHistory.Max(item => item.TakeNumber):00}";
                _estimatedSegmentDurations.TryGetValue(segment.SegmentId, out TimeSpan estimate);
                SegmentDurationProjection projection = NarrationDurationProjection.Resolve(segment, estimate);
                string durationText = projection.Duration > TimeSpan.Zero
                    ? projection.IsMeasured
                        ? $"  ·  {FormatDuration(projection.Duration)} latest take  ·  {FormatDuration(estimate)} est."
                        : $"  ·  {FormatDuration(projection.Duration)} est."
                    : "  ·  duration unavailable";
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
        return (Brush)Microsoft.UI.Xaml.Application.Current.Resources[resourceKey];
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
        if ((_isDirty || _directionsDirty) && !await SaveCurrentScriptAsync())
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
        if ((_isDirty || _directionsDirty) && !await SaveCurrentScriptAsync())
        {
            SelectTakeRailItem(_segment?.SegmentId);
            return;
        }

        await LoadSegmentAsync(segmentId);
    }

    private async Task LoadSegmentAsync(string segmentId, bool discardDraft = false)
    {
        EndFollowSession();
        string rootAtStart = App.Services.Session.RequireProjectRoot();
        string? segmentAtStart = _segment?.SegmentId;
        string revisionAtStart = _automationRevision;
        VoiceBenchProjectDocument project = _project ?? App.Services.Session.RequireProject();
        int index = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        if (index < 0) return;
        SegmentDocument segment = project.Segments[index];
        string path = App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath);
        string text = await File.ReadAllTextAsync(path);
        if (!string.Equals(rootAtStart, App.Services.Session.ProjectRoot, StringComparison.OrdinalIgnoreCase) ||
            _segment?.SegmentId != segmentAtStart) return;
        if (discardDraft && revisionAtStart != _automationRevision) return;
        if (!discardDraft && HasUnsavedSegmentChanges && _segment?.SegmentId == segmentId) return;
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
            VoiceBenchProjectDocument after = await ExecuteAuthoringTransactionAsync(
                files.Count == 1 ? "Import script" : $"Import {files.Count} scripts",
                cancellationToken => App.Services.ScriptImporter.ImportFilesAsync(
                    App.Services.Session.RequireProjectRoot(),
                    files.Select(file => file.Path).ToArray(),
                    cancellationToken));
            string? firstImportedSegmentId = after.ChapterOrder
                .Skip(before.ChapterOrder.Count)
                .SelectMany(chapterId => after.Chapters
                    .Where(chapter => chapter.ChapterId == chapterId)
                    .SelectMany(chapter => chapter.SegmentOrder))
                .FirstOrDefault();
            _suppressProjectChangedReload = true;
            try
            {
                await App.Services.Session.RefreshAsync();
            }
            finally
            {
                _suppressProjectChangedReload = false;
            }
            await LoadProjectAsync(firstImportedSegmentId);
            int importedChapterCount = after.Chapters.Count - before.Chapters.Count;
            int importedSegmentCount = after.Segments.Count - before.Segments.Count;
            ShowStatus(
                InfoBarSeverity.Success,
                files.Count == 1 ? "Script imported" : "Scripts imported",
                $"Created {importedChapterCount} chapter(s) and {importedSegmentCount} segment(s). Each ## heading is a separate segment in the chapter rail.");
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
            await ExecuteAuthoringTransactionAsync(
                "Restore writing revision",
                cancellationToken => App.Services.ChapterAuthoring.RestoreRevisionAsync(
                    App.Services.Session.RequireProjectRoot(),
                    segmentId,
                    choice.RevisionId,
                    choice.RestorePreviousText,
                    cancellationToken));
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
            await ExecuteAuthoringTransactionAsync(
                files.Count == 1 ? "Import writing reference" : $"Import {files.Count} writing references",
                async cancellationToken =>
                {
                    foreach (Windows.Storage.StorageFile file in files)
                    {
                        await App.Services.ChapterAuthoring.ImportReferenceAsync(
                            App.Services.Session.RequireProjectRoot(), file.Path, cancellationToken);
                    }
                });
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
            VoiceBenchProjectDocument updated = await ExecuteAuthoringTransactionAsync(
                $"Create chapter: {title.Trim()}",
                cancellationToken => App.Services.ChapterAuthoring.CreateBlankChapterAsync(
                    App.Services.Session.RequireProjectRoot(),
                    title,
                    brief,
                    cancellationToken));
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
            VoiceBenchProjectDocument updated = await ExecuteAuthoringTransactionAsync(
                $"Create segment: {title.Trim()}",
                cancellationToken => App.Services.ChapterAuthoring.AddSegmentAsync(
                    App.Services.Session.RequireProjectRoot(), chapterId, title, cancellationToken: cancellationToken));
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
        await ExecuteAuthoringTransactionAsync(
            selected.SegmentId is null ? "Rename chapter" : "Rename segment",
            async cancellationToken =>
            {
                if (selected.SegmentId is null)
                {
                    await App.Services.ChapterAuthoring.UpdateChapterAsync(
                        App.Services.Session.RequireProjectRoot(), selected.ChapterId, title, cancellationToken: cancellationToken);
                }
                else
                {
                    await App.Services.ChapterAuthoring.RenameSegmentAsync(
                        App.Services.Session.RequireProjectRoot(), selected.SegmentId, title, cancellationToken);
                }
            });

        await App.Services.Session.RefreshAsync();
    }

    private async void DuplicateSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (selected is null) return;
        await ExecuteAuthoringTransactionAsync(
            selected.SegmentId is null ? "Duplicate chapter" : "Duplicate segment",
            async cancellationToken =>
            {
                if (selected.SegmentId is null)
                {
                    await App.Services.ChapterAuthoring.DuplicateChapterAsync(
                        App.Services.Session.RequireProjectRoot(), selected.ChapterId, cancellationToken);
                }
                else
                {
                    await App.Services.ChapterAuthoring.DuplicateSegmentAsync(
                        App.Services.Session.RequireProjectRoot(), selected.SegmentId, cancellationToken);
                }
            });

        await App.Services.Session.RefreshAsync();
        ShowStatus(InfoBarSeverity.Success, "Duplicate created", "The copy has its own canonical source and stable ID.");
    }

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string offsetText } ||
            !int.TryParse(offsetText, out int offset) || GetSelectedRailItem() is not RailItem selected) return;
        await ExecuteAuthoringTransactionAsync(
            selected.SegmentId is null ? "Reorder chapter" : "Reorder segment",
            async cancellationToken =>
            {
                if (selected.SegmentId is null)
                {
                    await App.Services.ChapterAuthoring.MoveChapterAsync(
                        App.Services.Session.RequireProjectRoot(), selected.ChapterId, offset, cancellationToken);
                }
                else
                {
                    await App.Services.ChapterAuthoring.MoveSegmentAsync(
                        App.Services.Session.RequireProjectRoot(), selected.ChapterId, selected.SegmentId, offset, cancellationToken);
                }
            });

        await App.Services.Session.RefreshAsync();
    }

    private async void ToggleChapterInclusion_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        string? chapterId = selected?.ChapterId ?? _segment?.ChapterId;
        if (_project is null || chapterId is null) return;
        ChapterDocument chapter = _project.Chapters.Single(item => item.ChapterId == chapterId);
        await ExecuteAuthoringTransactionAsync(
            chapter.IncludedInCompilation ? "Exclude chapter from compilation" : "Include chapter in compilation",
            cancellationToken => App.Services.ChapterAuthoring.UpdateChapterAsync(
                App.Services.Session.RequireProjectRoot(),
                chapterId,
                includedInCompilation: !chapter.IncludedInCompilation,
                cancellationToken: cancellationToken));
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
        await ExecuteAuthoringTransactionAsync(
            "Archive chapter",
            cancellationToken => App.Services.ChapterAuthoring.UpdateChapterAsync(
                App.Services.Session.RequireProjectRoot(),
                chapterId,
                includedInCompilation: false,
                status: ChapterStatus.Archived,
                cancellationToken: cancellationToken));
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
            await ExecuteAuthoringTransactionAsync(
                isChapter ? $"Delete chapter: {name}" : $"Delete segment: {name}",
                async cancellationToken =>
                {
                    if (isChapter)
                    {
                        await App.Services.ChapterAuthoring.DeleteChapterAsync(
                            App.Services.Session.RequireProjectRoot(), selected.ChapterId, cancellationToken);
                    }
                    else
                    {
                        await App.Services.ChapterAuthoring.DeleteSegmentAsync(
                            App.Services.Session.RequireProjectRoot(), selected.SegmentId!, cancellationToken);
                    }
                });

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
            new TextBlock { Text = "Only checked local references are sent", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"] }, references })
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
        await ExecuteAuthoringTransactionAsync(
            "Update writing profile",
            cancellationToken => App.Services.Session.SaveAsync(project, cancellationToken));
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
            VoiceBenchProjectDocument updated = await ExecuteAuthoringTransactionAsync(
                $"Accept AI chapter draft: {draft.Title}",
                cancellationToken => App.Services.ChapterAuthoring.CreateChapterFromDraftAsync(
                    App.Services.Session.RequireProjectRoot(),
                    draft,
                    goalBox.Text.Trim(),
                    referenceIds,
                    cancellationToken));
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
        if (_writingActionInProgress || _project is null || _segment is null || sender is not Button { Tag: string actionName } ||
            !Enum.TryParse(actionName, out WritingAction action)) return;
        if (!await SaveCurrentScriptAsync()) return;
        _writingActionInProgress = true;
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
            SetWritingActivity(
                true,
                WritingActionProgressTitle(action),
                $"{providerId} / {modelId} · Loading the selected script context…");
            WritingContext context = await App.Services.WritingContexts.LoadAsync(
                App.Services.Session.RequireProjectRoot(), chapter?.Brief ?? string.Empty, referenceIds);
            WritingRevisionRequest request = new(
                modelId,
                action,
                target,
                action == WritingAction.Custom ? CustomWritingInstructionTextBox.Text.Trim() : null,
                context,
                EstimateWritingTokens(target, CustomWritingInstructionTextBox.Text, context));
            SetWritingActivity(
                true,
                WritingActionProgressTitle(action),
                $"{providerId} / {modelId} · Preparing a reviewable proposal…");
            ShowStatus(InfoBarSeverity.Informational, "Generating writing proposal", $"Using {providerId} / {modelId}. The canonical script will not change without acceptance.");
            WritingProposal partial = await App.Services.WritingAssistant.ReviseAsync(providerId, request);
            SetWritingActivity(false);
            WritingAction acceptedAction = action;
            if (action == WritingAction.Critique)
            {
                if (!await ShowWritingCritiqueAsync(target, partial))
                {
                    ShowWritingConfirmation(
                        InfoBarSeverity.Informational,
                        "Critique complete",
                        "You kept the script as-is. No canonical text changed.");
                    return;
                }
                string critiqueInstruction = BuildCritiqueRevisionInstruction(partial);
                WritingRevisionRequest critiqueRevision = request with
                {
                    Action = WritingAction.ApplyCritique,
                    CustomInstruction = critiqueInstruction,
                    EstimatedInputTokens = EstimateWritingTokens(target, critiqueInstruction, context),
                };
                SetWritingActivity(
                    true,
                    "Revising from critique",
                    $"{providerId} / {modelId} · Applying the editorial notes without changing production cues…");
                ShowStatus(
                    InfoBarSeverity.Informational,
                    "Revising from critique",
                    $"Using {providerId} / {modelId}. You will review the rewritten narration before anything changes.");
                partial = await App.Services.WritingAssistant.ReviseAsync(providerId, critiqueRevision);
                SetWritingActivity(false);
                acceptedAction = WritingAction.ApplyCritique;
            }

            string proposedWhole = selectionLength > 0
                ? string.Concat(originalWhole.AsSpan(0, selectionStart), partial.ProposedText, originalWhole.AsSpan(selectionStart + selectionLength))
                : partial.ProposedText;
            WritingProposal proposal = partial with { ProposedText = proposedWhole };
            if (!await ShowWritingDiffAsync(originalWhole, proposal))
            {
                ShowWritingConfirmation(
                    InfoBarSeverity.Informational,
                    "Proposal rejected",
                    "The canonical script remains unchanged.");
                return;
            }
            _pendingWritingProposal = proposal;
            _pendingWritingAction = acceptedAction;
            _pendingWritingProviderId = providerId;
            _pendingWritingModelId = modelId;
            _pendingWritingSourceHash = _segment.AuthoringSha256;
            await ExecuteAuthoringTransactionAsync(
                $"Accept writing proposal: {WritingActionDisplayName(acceptedAction)}",
                cancellationToken => App.Services.ChapterAuthoring.ApplyWritingProposalAsync(
                    App.Services.Session.RequireProjectRoot(),
                    _segment.SegmentId,
                    _pendingWritingSourceHash,
                    proposal,
                    acceptedAction,
                    providerId,
                    modelId,
                    cancellationToken));
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(_segment.SegmentId);
            ShowWritingConfirmation(
                InfoBarSeverity.Success,
                "Proposal accepted",
                $"{WritingActionDisplayName(acceptedAction)} was saved as a restorable local revision.");
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
            ShowWritingConfirmation(InfoBarSeverity.Error, "Writing request stopped", Actionable(exception));
            ShowStatus(InfoBarSeverity.Error, "Writing proposal stopped", Actionable(exception));
        }
        finally
        {
            _writingActionInProgress = false;
            SetWritingActivity(false);
        }
    }

    private void SetWritingActivity(bool isActive, string title = "", string message = "")
    {
        WritingActivityPanel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        WritingActivityRing.IsActive = isActive;
        WritingActivityTitle.Text = title;
        WritingActivityText.Text = message;
        WritingActionButtons.IsHitTestVisible = !isActive;
        WritingActionButtons.Opacity = isActive ? 0.55 : 1;
        ApplyCustomWritingButton.IsEnabled = !isActive;
        WritingProviderComboBox.IsEnabled = !isActive;
        WritingModelComboBox.IsEnabled = !isActive;
        if (isActive) WritingProposalInfoBar.IsOpen = false;
    }

    private void ShowWritingConfirmation(InfoBarSeverity severity, string title, string message)
    {
        WritingProposalInfoBar.Severity = severity;
        WritingProposalInfoBar.Title = title;
        WritingProposalInfoBar.Message = message;
        WritingProposalInfoBar.IsOpen = true;
    }

    private static string WritingActionProgressTitle(WritingAction action) => action switch
    {
        WritingAction.ImproveClarity => "Improving clarity",
        WritingAction.MakeConversational => "Making the narration conversational",
        WritingAction.Shorten => "Shortening the narration",
        WritingAction.Expand => "Expanding the narration",
        WritingAction.FixGrammar => "Checking grammar",
        WritingAction.Critique => "Critiquing the narration",
        WritingAction.GenerateProductionCues => "Generating production cues",
        WritingAction.RefineProductionCues => "Refining production cues",
        WritingAction.Custom => "Applying your instruction",
        WritingAction.ApplyCritique => "Revising from critique",
        _ => "Working on your script",
    };

    private static string WritingActionDisplayName(WritingAction action) => action switch
    {
        WritingAction.ApplyCritique => "Revision from critique",
        WritingAction.GenerateProductionCues => "Production-cue proposal",
        WritingAction.RefineProductionCues => "Production-cue revision",
        WritingAction.Custom => "Custom revision",
        _ => WritingActionProgressTitle(action),
    };

    private async Task<bool> ShowWritingCritiqueAsync(string reviewedText, WritingProposal critique)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Writing critique",
            Content = CreateWritingCritiqueContent(reviewedText, critique),
            PrimaryButtonText = "Revise from critique",
            CloseButtonText = "Keep script as-is",
            DefaultButton = ContentDialogButton.None,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private FrameworkElement CreateWritingCritiqueContent(string reviewedText, WritingProposal critique)
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(reviewedText);
        StackPanel content = new()
        {
            MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 14,
        };
        content.Children.Add(new TextBlock
        {
            Text = "EDITORIAL READ",
            FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["UtilityFont"],
            FontSize = 11,
            CharacterSpacing = 110,
            Foreground = ThemeBrush("ActiveSignalBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = critique.Summary,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(CreateWritingMetrics(parsed, parsed));

        if (critique.Warnings.Count > 0)
        {
            StackPanel warningList = new() { Spacing = 8 };
            warningList.Children.Add(new TextBlock
            {
                Text = critique.Warnings.Count == 1 ? "1 EDITORIAL NOTE" : $"{critique.Warnings.Count} EDITORIAL NOTES",
                FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["UtilityFont"],
                FontSize = 11,
                CharacterSpacing = 80,
                Foreground = ThemeBrush("PickupBrush"),
            });
            foreach (string warning in critique.Warnings)
            {
                warningList.Children.Add(new TextBlock
                {
                    Text = $"•  {warning}",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            content.Children.Add(new Border
            {
                Background = ThemeBrush("PanelBrush"),
                BorderBrush = ThemeBrush("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14),
                Child = warningList,
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = "Nothing has changed yet. Revise from critique creates a new proposal for you to compare and accept.",
            FontSize = 13,
            Foreground = ThemeBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        return new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(380, Math.Min(700, ActualHeight - 220)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private async Task<bool> ShowWritingDiffAsync(string original, WritingProposal proposal)
    {
        ParsedScriptDocument originalParsed = new ProductionScriptParser().Parse(original);
        ParsedScriptDocument proposalParsed = new ProductionScriptParser().Parse(proposal.ProposedText);
        StackPanel content = new()
        {
            MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            Text = proposal.Summary,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(CreateWritingMetrics(originalParsed, proposalParsed));
        if (proposal.Warnings.Count > 0)
        {
            TextBlock warningText = new()
            {
                Text = string.Join("\n", proposal.Warnings.Select(warning => $"•  {warning}")),
                FontSize = 13,
                Foreground = ThemeBrush("PickupBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            content.Children.Add(new Border
            {
                Background = ThemeBrush("PanelBrush"),
                BorderBrush = ThemeBrush("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new ScrollViewer
                {
                    Content = warningText,
                    MaxHeight = 110,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });
        }
        content.Children.Add(CreateWritingComparison(original, proposal.ProposedText));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Review original and proposal",
            Content = content,
            PrimaryButtonText = "Accept proposal",
            CloseButtonText = "Reject",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private FrameworkElement CreateWritingComparison(string original, string proposed)
    {
        double reviewHeight = EstimateWritingComparisonHeight(original, proposed);
        Grid sideBySide = new()
        {
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        sideBySide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sideBySide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Border originalPane = CreateWritingReviewPane("Original", original);
        Border proposedPane = CreateWritingReviewPane("Proposal", proposed);
        sideBySide.Children.Add(originalPane);
        Grid.SetColumn(proposedPane, 1);
        sideBySide.Children.Add(proposedPane);

        TabView proposalTabs = new TabView
        {
            IsAddTabButtonVisible = false,
            TabWidthMode = TabViewWidthMode.Equal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Visible,
        };
        proposalTabs.TabItems.Add(CreateWritingReviewTab("Original", original, reviewHeight));
        proposalTabs.TabItems.Add(CreateWritingReviewTab("Proposal", proposed, reviewHeight));

        Grid comparisonHost = new()
        {
            Height = reviewHeight,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        comparisonHost.Children.Add(sideBySide);
        comparisonHost.Children.Add(proposalTabs);
        comparisonHost.SizeChanged += (_, args) =>
            ApplyWritingComparisonLayout(sideBySide, proposalTabs, args.NewSize.Width);
        comparisonHost.Loaded += (_, _) =>
            ApplyWritingComparisonLayout(sideBySide, proposalTabs, comparisonHost.ActualWidth);
        return comparisonHost;
    }

    private static void ApplyWritingComparisonLayout(Grid sideBySide, TabView proposalTabs, double availableWidth)
    {
        bool showSideBySide = availableWidth >= 760;
        sideBySide.Visibility = showSideBySide ? Visibility.Visible : Visibility.Collapsed;
        proposalTabs.Visibility = showSideBySide ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TabViewItem CreateWritingReviewTab(string title, string text, double reviewHeight)
    {
        Border reviewPane = CreateWritingReviewPane(string.Empty, text);
        reviewPane.Height = Math.Max(180, reviewHeight - 48);
        return new TabViewItem
        {
            Header = new TextBlock
            {
                Text = title,
                FontSize = 13,
                Foreground = ThemeBrush("ReadingTextBrush"),
            },
            IsClosable = false,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = reviewPane,
        };
    }

    private static double EstimateWritingComparisonHeight(string original, string proposed)
    {
        ProductionScriptParser parser = new();
        ParsedScriptDocument originalParsed = parser.Parse(original);
        ParsedScriptDocument proposalParsed = parser.Parse(proposed);
        int words = Math.Max(originalParsed.NarrationWordCount, proposalParsed.NarrationWordCount);
        int cues = Math.Max(originalParsed.Cues.Count, proposalParsed.Cues.Count);
        return Math.Clamp(110 + (words * 1.6d) + (cues * 18d), 220, 440);
    }

    private static TextBlock CreateWritingMetrics(ParsedScriptDocument original, ParsedScriptDocument proposal) => new()
    {
        Text = $"{original.NarrationWordCount} → {proposal.NarrationWordCount} WORDS   ·   {original.Cues.Count} → {proposal.Cues.Count} CUES   ·   {FormatDuration(SegmentRenderService.EstimateDuration(original.NarrationText))} → {FormatDuration(SegmentRenderService.EstimateDuration(proposal.NarrationText))}",
        FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["UtilityFont"],
        FontSize = 11,
        CharacterSpacing = 45,
        Foreground = ThemeBrush("MutedTextBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static string BuildCritiqueRevisionInstruction(WritingProposal critique) =>
        critique.Warnings.Count == 0
            ? critique.Summary
            : $"{critique.Summary}\n\nSpecific editorial notes:\n{string.Join("\n", critique.Warnings.Select(warning => $"- {warning}"))}";

    private static Border CreateWritingReviewPane(string header, string text)
    {
        Grid layout = new()
        {
            RowSpacing = 8,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        if (!string.IsNullOrWhiteSpace(header))
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 13,
                Foreground = ThemeBrush("ReadingTextBrush"),
            });
        }

        int contentRow = layout.RowDefinitions.Count;
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        TextBlock body = new()
        {
            Text = ToMultilineDisplayText(text),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 14,
            Foreground = ThemeBrush("ReadingTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };

        ScrollViewer scroller = new()
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetRow(scroller, contentRow);
        layout.Children.Add(scroller);

        return new Border
        {
            Background = ThemeBrush("BaseBrush"),
            BorderBrush = ThemeBrush("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = layout,
        };
    }

    private static string ToMultilineDisplayText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private static Brush ThemeBrush(string key) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

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

    private async Task ExecuteAuthoringTransactionAsync(
        string label,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        async Task<bool> ExecuteAsync()
        {
            await App.Services.AuthoringTransactions.ExecuteAsync(
                App.Services.Session.RequireProjectRoot(), label, action, cancellationToken);
            return true;
        }
        if (App.Services.Automation is { } automation)
            await automation.Coordinator.ExecuteAsync(
                () => DesktopAutomationAdapter.OnDispatcherAsync(DispatcherQueue, ExecuteAsync, cancellationToken), cancellationToken);
        else await ExecuteAsync();
        await RefreshAuthoringHistoryAsync();
    }

    private async Task<T> ExecuteAuthoringTransactionAsync<T>(
        string label,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        T result = default!;
        await ExecuteAuthoringTransactionAsync(
            label,
            async token =>
            {
                result = await action(token);
            },
            cancellationToken);
        return result;
    }

    private async Task RefreshAuthoringHistoryAsync()
    {
        if (!App.Services.Session.HasProject)
        {
            _authoringHistory = new AuthoringTransactionState(
                AuthoringTransactionState.CurrentSchemaVersion,
                [],
                0,
                false);
            UpdateUndoRedoState();
            return;
        }

        _authoringHistory = await App.Services.AuthoringTransactions.GetStateAsync(
            App.Services.Session.RequireProjectRoot());
        UpdateUndoRedoState();
    }

    private void UpdateUndoRedoState()
    {
        bool hasEditor = _segment is not null && ScriptTextBox.IsEnabled;
        bool editorUndo = hasEditor && (_pendingEditorUndo is not null || _editorUndoHistory.Count > 0);
        bool editorRedo = hasEditor && _editorRedoHistory.Count > 0;
        bool durableUndo = !HasUnsavedSegmentChanges && _authoringHistory.CanUndo;
        bool durableRedo = !HasUnsavedSegmentChanges && _authoringHistory.CanRedo;
        UndoAuthoringButton.IsEnabled = !_authoringHistoryOperationInProgress && (editorUndo || durableUndo);
        RedoAuthoringButton.IsEnabled = !_authoringHistoryOperationInProgress && (editorRedo || durableRedo);
        AuthoringHistoryButton.IsEnabled = !_authoringHistoryOperationInProgress && _authoringHistory.Transactions.Count > 0;
        ToolTipService.SetToolTip(
            UndoAuthoringButton,
            editorUndo
                ? "Undo the latest editor change (Ctrl+Z)"
                : durableUndo
                    ? $"Undo saved action: {_authoringHistory.NextUndo!.Label} (Ctrl+Z)"
                    : HasUnsavedSegmentChanges ? "Finish or save the current edit before undoing saved actions" : "Nothing to undo");
        ToolTipService.SetToolTip(
            RedoAuthoringButton,
            editorRedo
                ? "Redo the latest editor change (Ctrl+Y)"
                : durableRedo
                    ? $"Redo saved action: {_authoringHistory.NextRedo!.Label} (Ctrl+Y)"
                    : HasUnsavedSegmentChanges ? "Finish or save the current edit before redoing saved actions" : "Nothing to redo");
    }

    private async void UndoAuthoring_Click(object sender, RoutedEventArgs e)
        => await UndoAuthoringAsync();

    private async void RedoAuthoring_Click(object sender, RoutedEventArgs e)
        => await RedoAuthoringAsync();

    private async void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (AudioDock.OwnsKeyboardFocus()) { await AudioDock.UndoTimelineAsync(true); return; }
        await UndoAuthoringAsync();
    }

    private async void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (AudioDock.OwnsKeyboardFocus()) { await AudioDock.UndoTimelineAsync(false); return; }
        await RedoAuthoringAsync();
    }

    private async Task UndoAuthoringAsync()
    {
        if (_authoringHistoryOperationInProgress) return;
        CommitPendingEditorUndo();
        if (_segment is not null && _editorUndoHistory.Count > 0)
        {
            EditorHistorySnapshot current = CaptureEditorSnapshot();
            EditorHistorySnapshot target = _editorUndoHistory[^1];
            _editorUndoHistory.RemoveAt(_editorUndoHistory.Count - 1);
            _editorRedoHistory.Add(current);
            ApplyEditorSnapshot(target);
            return;
        }
        await ApplyDurableHistoryAsync(undo: true);
    }

    private async Task RedoAuthoringAsync()
    {
        if (_authoringHistoryOperationInProgress) return;
        CommitPendingEditorUndo();
        if (_segment is not null && _editorRedoHistory.Count > 0)
        {
            EditorHistorySnapshot current = CaptureEditorSnapshot();
            EditorHistorySnapshot target = _editorRedoHistory[^1];
            _editorRedoHistory.RemoveAt(_editorRedoHistory.Count - 1);
            _editorUndoHistory.Add(current);
            ApplyEditorSnapshot(target);
            return;
        }
        await ApplyDurableHistoryAsync(undo: false);
    }

    private async Task ApplyDurableHistoryAsync(bool undo)
    {
        if (!App.Services.Session.HasProject || HasUnsavedSegmentChanges)
        {
            ShowStatus(
                InfoBarSeverity.Warning,
                "Save or finish the current edit",
                "Saved project actions can be restored only from a clean editor boundary.");
            return;
        }

        _authoringHistoryOperationInProgress = true;
        UpdateUndoRedoState();
        string? preferredSegmentId = _segment?.SegmentId;
        try
        {
            ShowStatus(
                InfoBarSeverity.Informational,
                undo ? "Undoing saved action" : "Redoing saved action",
                "Restoring the project manifest and canonical authoring files together…");
            AuthoringTransactionRecord transaction = undo
                ? await App.Services.AuthoringTransactions.UndoAsync(App.Services.Session.RequireProjectRoot())
                : await App.Services.AuthoringTransactions.RedoAsync(App.Services.Session.RequireProjectRoot());
            await App.Services.Session.RefreshAsync();
            VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
            string? segmentId = preferredSegmentId is not null && project.Segments.Any(item => item.SegmentId == preferredSegmentId)
                ? preferredSegmentId
                : ProjectOrdering.GetPlaybackOrderedSegmentIds(project).FirstOrDefault();
            await LoadProjectAsync(segmentId);
            ShowStatus(
                InfoBarSeverity.Success,
                undo ? "Saved action undone" : "Saved action redone",
                transaction.Label);
        }
        catch (Exception exception)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                undo ? "Undo stopped" : "Redo stopped",
                exception.Message);
        }
        finally
        {
            _authoringHistoryOperationInProgress = false;
            await RefreshAuthoringHistoryAsync();
        }
    }

    private async void ShowAuthoringHistory_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAuthoringHistoryAsync();
        StackPanel entries = new() { Spacing = 8 };
        for (int index = _authoringHistory.Transactions.Count - 1; index >= 0; index--)
        {
            AuthoringTransactionRecord transaction = _authoringHistory.Transactions[index];
            bool applied = index < _authoringHistory.Cursor;
            string state = applied ? "APPLIED" : "UNDONE";
            if (index == _authoringHistory.Cursor - 1) state = "NEXT UNDO";
            else if (index == _authoringHistory.Cursor) state = "NEXT REDO";
            entries.Children.Add(new Border
            {
                Background = ThemeBrush("PanelBrush"),
                BorderBrush = ThemeBrush("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 9, 12, 9),
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = transaction.Label,
                            FontSize = 14,
                            Foreground = ThemeBrush("ReadingTextBrush"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = $"{transaction.CreatedAt.LocalDateTime:g}  ·  {state}",
                            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"],
                        },
                    },
                },
            });
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Saved authoring history",
            CloseButtonText = "Close",
            Content = new StackPanel
            {
                Width = 520,
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Undo and redo restore the project manifest and canonical scripts together. Audio and exports are never changed.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeBrush("MutedTextBrush"),
                    },
                    new ScrollViewer
                    {
                        Content = entries,
                        MaxHeight = 460,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    },
                },
            },
        };
        await dialog.ShowAsync();
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

        // RichEditBox also raises TextChanged for character-format changes. The plain-text
        // comparison keeps visual cue formatting out of VoiceBench's editor history.
        if (_pendingEditorUndo is null)
        {
            _pendingEditorUndo = new EditorHistorySnapshot(
                _lastEditorText,
                _lastEditorSelectionStart,
                _lastEditorSelectionEnd,
                DirectionTextBox.Text);
        }
        _editorRedoHistory.Clear();
        _editorHistoryTimer?.Stop();
        _editorHistoryTimer?.Start();
        _lastEditorText = currentText;
        InvalidateAutomationRevision();
        CaptureEditorSelection();
        _isDirty = !string.Equals(currentText, _loadedText, StringComparison.Ordinal);
        ParsedScriptDocument parsed = UpdateEditorStats();
        UpdateUndoRedoState();
        QueueCueFormatting();
        UpdateFollowEligibility(parsed);
    }

    private void ScriptTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ignoreTextChanges) CaptureEditorSelection();
    }

    private void CaptureEditorSelection()
    {
        ITextSelection selection = ScriptTextBox.Document.Selection;
        _lastEditorSelectionStart = selection.StartPosition;
        _lastEditorSelectionEnd = selection.EndPosition;
    }

    private EditorHistorySnapshot CaptureEditorSnapshot()
    {
        ITextSelection selection = ScriptTextBox.Document.Selection;
        return new EditorHistorySnapshot(GetEditorText(), selection.StartPosition, selection.EndPosition, DirectionTextBox.Text);
    }

    private void CommitPendingEditorUndo()
    {
        _editorHistoryTimer?.Stop();
        if (_pendingEditorUndo is null) return;
        if (!string.Equals(_pendingEditorUndo.Text, GetEditorText(), StringComparison.Ordinal) ||
            !string.Equals(_pendingEditorUndo.Directions, DirectionTextBox.Text, StringComparison.Ordinal))
        {
            _editorUndoHistory.Add(_pendingEditorUndo);
            if (_editorUndoHistory.Count > EditorHistoryLimit) _editorUndoHistory.RemoveAt(0);
        }
        _pendingEditorUndo = null;
        UpdateUndoRedoState();
    }

    private void ResetEditorHistory()
    {
        _editorHistoryTimer?.Stop();
        _pendingEditorUndo = null;
        _editorUndoHistory.Clear();
        _editorRedoHistory.Clear();
        CaptureEditorSelection();
        UpdateUndoRedoState();
    }

    private void ApplyEditorSnapshot(EditorHistorySnapshot snapshot)
    {
        bool wasEnabled = ScriptTextBox.IsEnabled;
        bool wasReadOnly = ScriptTextBox.IsReadOnly;
        _ignoreTextChanges = true;
        try
        {
            ScriptTextBox.IsEnabled = true;
            ScriptTextBox.IsReadOnly = false;
            ScriptTextBox.Document.SetText(TextSetOptions.None, snapshot.Text);
            int textLength = GetEditorText().Length;
            int start = Math.Clamp(snapshot.SelectionStart, 0, textLength);
            int end = Math.Clamp(snapshot.SelectionEnd, 0, textLength);
            ScriptTextBox.Document.Selection.SetRange(start, end);
            ScriptTextBox.Document.ClearUndoRedoHistory();
            _lastEditorText = snapshot.Text;
            _lastEditorSelectionStart = start;
            _lastEditorSelectionEnd = end;
        }
        finally
        {
            ScriptTextBox.IsReadOnly = wasReadOnly;
            ScriptTextBox.IsEnabled = wasEnabled;
            _ignoreTextChanges = false;
        }

        _isDirty = !string.Equals(snapshot.Text, _loadedText, StringComparison.Ordinal);
        if (snapshot.Directions is not null)
        {
            _ignoreDirectionChanges = true;
            try { DirectionTextBox.Text = snapshot.Directions; }
            finally { _ignoreDirectionChanges = false; }
            _directionsDirty = !string.Equals(DirectionTextBox.Text.Trim(), _loadedDirectionText, StringComparison.Ordinal);
            _directionDraftSegmentId = _directionsDirty ? _segment?.SegmentId : null;
        }
        _lastEditorDirections = DirectionTextBox.Text;
        InvalidateAutomationRevision();
        UpdateEditorStats();
        UpdateUndoRedoState();
        QueueCueFormatting();
        ScriptTextBox.Focus(FocusState.Programmatic);
    }

    private void ScriptTextBox_PointerStateChanged(object sender, PointerRoutedEventArgs e) =>
        QueueCueFormatting();

    private void ScriptTextBox_FocusStateChanged(object sender, RoutedEventArgs e) =>
        QueueCueFormatting();

    private void ScriptTextBox_Loaded(object sender, RoutedEventArgs e) =>
        QueueCueFormatting();

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

        VoiceBenchProjectDocument projectToSave = _project;
        SegmentDocument segmentToSave = _segment;
        string rootToSave = App.Services.Session.RequireProjectRoot();
        string editorText = GetEditorText();
        string directionText = DirectionTextBox.Text;
        string path = App.Services.Session.ResolveProjectPath(segmentToSave.ScriptRelativePath);
        string externalHash = SourceTextHasher.Compute(await File.ReadAllTextAsync(path));
        if (!string.Equals(externalHash, _loadedDiskHash, StringComparison.OrdinalIgnoreCase) && !_externalOverwriteApproved)
        {
            ExternalEditBar.IsOpen = true;
            ShowStatus(InfoBarSeverity.Warning, "External edit needs a decision", "Reload, compare, or explicitly keep the current editor text before saving.");
            return false;
        }

        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(editorText);
        string newHash = SourceTextHasher.Compute(parsed.NarrationText);
        string authoringHash = SourceTextHasher.Compute(editorText);
        string? normalizedDirections = string.IsNullOrWhiteSpace(directionText)
            ? null
            : directionText.Trim();
        SegmentDocument updated = SegmentStateRules.WithSourceHashes(segmentToSave, newHash, authoringHash) with
        {
            Directions = normalizedDirections,
        };
        int index = projectToSave.Segments.FindIndex(item => item.SegmentId == segmentToSave.SegmentId);
        List<SegmentDocument> segments = [.. projectToSave.Segments];
        segments[index] = updated;
        await ExecuteAuthoringTransactionAsync(
            $"Save script: {segmentToSave.Title}",
            async cancellationToken =>
            {
                string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(temporaryPath, editorText, cancellationToken);
                    File.Move(temporaryPath, path, overwrite: true);
                    await App.Services.Session.SaveAsync(
                        projectToSave with { Segments = segments },
                        cancellationToken);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            });
        if (App.Services.Session.ProjectRoot != rootToSave || _segment?.SegmentId != segmentToSave.SegmentId) return true;
        _project = App.Services.Session.RequireProject();
        _segment = _project.Segments.Single(item => item.SegmentId == updated.SegmentId);
        _loadedText = editorText;
        _loadedDiskHash = authoringHash;
        _externalOverwriteApproved = false;
        _loadedDirectionText = normalizedDirections ?? string.Empty;
        _isDirty = !string.Equals(GetEditorText(), editorText, StringComparison.Ordinal);
        _directionsDirty = !string.Equals(DirectionTextBox.Text.Trim(), _loadedDirectionText, StringComparison.Ordinal);
        _directionDraftSegmentId = _directionsDirty ? _segment.SegmentId : null;
        if (!HasUnsavedSegmentChanges) ResetEditorHistory();
        ExternalEditBar.IsOpen = false;
        UpdateEditorStats();
        return true;
    }

    private async void ReloadExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        await LoadSegmentAsync(_segment.SegmentId, discardDraft: true);
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
            ProductionExportResult result = await App.Services.RunTrackedJobAsync<ProductionExportResult>(
                "exports.create", new { name = _project.Name, scope = "Compilation" });
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
        await ExecuteAuthoringTransactionAsync(
            "Update production-cue preference",
            cancellationToken => App.Services.Session.SaveAsync(
                _project with { WritingProfile = profile },
                cancellationToken));
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
        await ExecuteAuthoringTransactionAsync(
            $"Save pronunciation: {entry.Written}",
            cancellationToken => App.Services.Session.SaveAsync(
                _project with { Lexicon = lexicon },
                cancellationToken));
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
        _suppressProjectChangedReload = true;
        Interlocked.Increment(ref _projectChangeVersion);
        try
        {
            if (!await SaveCurrentScriptAsync()) return;
            string originalSegmentId = _segment.SegmentId;
            string originalChapterId = _segment.ChapterId;
            VoiceBenchProjectDocument updated = await ExecuteAuthoringTransactionAsync(
                $"Split segment: {_segment.Title}",
                cancellationToken => App.Services.ChapterAuthoring.SplitSegmentAsync(
                    App.Services.Session.RequireProjectRoot(),
                    originalSegmentId,
                    cursor,
                    _segment.AuthoringSha256,
                    cancellationToken));
            await App.Services.Session.RefreshAsync();
            ChapterDocument chapter = updated.Chapters.Single(item => item.ChapterId == originalChapterId);
            int originalIndex = chapter.SegmentOrder.IndexOf(originalSegmentId);
            if (originalIndex < 0 || originalIndex + 1 >= chapter.SegmentOrder.Count)
                throw new InvalidOperationException("The new segment could not be located after the split.");
            string newSegmentId = chapter.SegmentOrder[originalIndex + 1];
            await LoadProjectAsync(newSegmentId);
            ShowStatus(InfoBarSeverity.Success, "Segment split", "Created a new segment immediately after the original.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Segment could not be split", exception.Message);
        }
        finally
        {
            _suppressProjectChangedReload = false;
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

        if (_renderCancellation is not null || _renderRequestCancellation is not null)
        {
            ShowStatus(InfoBarSeverity.Warning, "A render is already active", "Cancel the current job or wait for it to finish.");
            return;
        }

        try
        {
            if (!await PrepareCurrentRenderAsync()) return;
            ShowStatus(
                InfoBarSeverity.Informational,
                "Rendering audio, then aligning words",
                "VoiceBench will save the take first, then request word timestamps from the same provider. Alignment cannot invalidate the audio.");
            _renderRequestCancellation = new CancellationTokenSource();
            SetRenderQueueBusy(true);
            SegmentRenderResult result = await App.Services.RunTrackedJobAsync<SegmentRenderResult>(
                "render.start", new { segmentId = _segment.SegmentId }, _renderRequestCancellation.Token);
            await WaitForProjectRefreshAsync(CancellationToken.None);
            if (_segment?.RenderHistory.Any(take => take.RenderId == result.Render.RenderId) == true)
            {
                TakeSelector.SelectedItem = TakeSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is Guid id && id == result.Render.RenderId);
                await UpdateTransportAsync();
                TakeReviewToggle.IsChecked = true;
                AudioDock.SelectTakeTarget();
            }
            string guidance = result.ExceedsFourMinuteGuidance ? " The estimated duration exceeds four minutes; split it before the next take." : string.Empty;
            string alignment = result.Alignment switch
            {
                { Status: WordAlignmentStatus.Aligned } complete => $" Word follow is ready ({complete.Coverage:P0}).",
                { Status: WordAlignmentStatus.Partial } partial => $" Partial word follow is ready ({partial.Coverage:P0}).",
                { Status: WordAlignmentStatus.Failed or WordAlignmentStatus.Interrupted } => " Audio is ready; word alignment can be retried from Take Review.",
                { Status: WordAlignmentStatus.Unsupported } => " Audio is ready; this provider does not support word alignment.",
                _ => string.Empty,
            };
            ShowStatus(
                InfoBarSeverity.Success,
                $"Take {result.Render.TakeNumber:00} rendered",
                $"Use Listen to take in Take Review to hear it.{alignment}{guidance}");
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
            _renderRequestCancellation?.Dispose();
            _renderRequestCancellation = null;
            SetRenderQueueBusy(_renderCancellation is not null);
        }
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e)
    {
        _renderCancellation?.Cancel();
        _renderRequestCancellation?.Cancel();
    }

    private async Task<bool> PrepareCurrentRenderAsync()
    {
        if (!await SaveCurrentScriptAsync() || _project is null || _segment is null) return false;
        if (!ValidateScriptForRender()) return false;
        VoicePreset preset = GetOrCreateSelectedVoicePreset(
            _project,
            setAsProjectDefault: _project.DefaultVoicePresetId is null,
            out VoiceBenchProjectDocument projectWithPreset);
        int index = projectWithPreset.Segments.FindIndex(item => item.SegmentId == _segment.SegmentId);
        List<SegmentDocument> segments = [.. projectWithPreset.Segments];
        segments[index] = _segment with { VoicePresetId = preset.PresetId, Directions = DirectionTextBox.Text.Trim(), Status = SegmentStatus.Ready };
        VoiceBenchProjectDocument renderProject = projectWithPreset with { Segments = segments };
        await App.Services.Session.SaveAsync(renderProject);
        _project = App.Services.Session.RequireProject();
        _segment = _project.Segments.Single(item => item.SegmentId == segments[index].SegmentId);
        return true;
    }

    private void SetRenderQueueBusy(bool busy)
    {
        RenderQueueStatusText.Text = busy ? "1 ACTIVE" : "IDLE";
        RenderProgressBar.IsIndeterminate = busy;
        RenderProgressBar.Value = 0;
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
            RenderRecord selected = GetSelectedRender() ?? throw new InvalidOperationException("Select a take before approving it.");
            bool confirmedTranscript = false;
            if (selected.Origin == RenderOrigin.Imported)
            {
                ContentDialog confirmation = new() { XamlRoot = XamlRoot, Title = "Confirm recording transcript",
                    Content = "Listen to this recording and verify that the current reference transcript accurately represents its spoken words before approving it.",
                    PrimaryButtonText = "Transcript is accurate · approve", CloseButtonText = "Cancel" };
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
                confirmedTranscript = true;
            }
            RenderRecord approved = await App.Services.ProcessAndApproveTakeAsync(_segment.SegmentId, selected.RenderId,confirmedTranscript: confirmedTranscript);
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

        updated = project with { VoicePresets = presets };
        if (setAsProjectDefault)
            updated = ProjectVoiceSelectionRules.SetProjectVoice(updated, preset.PresetId);
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
        if (preset is not null) SetPresetControls(preset);

        if (_directionsDirty && string.Equals(_directionDraftSegmentId, segment.SegmentId, StringComparison.Ordinal))
            return;

        SetDirectionText(segment.Directions ?? preset?.Directions ?? string.Empty);
    }

    private void DirectionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ignoreDirectionChanges || _segment is null) return;
        if (_pendingEditorUndo is null)
            _pendingEditorUndo = new EditorHistorySnapshot(GetEditorText(), _lastEditorSelectionStart, _lastEditorSelectionEnd, _lastEditorDirections);
        _lastEditorDirections = DirectionTextBox.Text;
        _editorRedoHistory.Clear();
        _editorHistoryTimer?.Stop();
        _editorHistoryTimer?.Start();
        InvalidateAutomationRevision();
        _directionsDirty = !string.Equals(
            DirectionTextBox.Text.Trim(),
            _loadedDirectionText,
            StringComparison.Ordinal);
        _directionDraftSegmentId = _directionsDirty ? _segment.SegmentId : null;
        UpdateEditorStats();
        UpdateUndoRedoState();
    }

    private void SetDirectionText(string text)
    {
        _ignoreDirectionChanges = true;
        try
        {
            DirectionTextBox.Text = text;
            _lastEditorDirections = text;
            _loadedDirectionText = text.Trim();
            _directionsDirty = false;
            _directionDraftSegmentId = null;
        }
        finally
        {
            _ignoreDirectionChanges = false;
        }
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
        _ = UpdateTransportAsync();
    }

    private void ResetTakeReviewContext()
    {
        TakeReviewTitleText.Text = "No segment selected";
        TakeReviewStatusText.Text = "NO TAKES";
        TakeSelector.Items.Clear();
        TakeSelector.SelectedIndex = -1;
        PreviousTakeButton.IsEnabled = false;
        ListenToTakeButton.IsEnabled = false;
        PickupButton.IsEnabled = false;
        ApproveButton.IsEnabled = false;
        AlignmentProgressRing.IsActive = false;
        AlignmentProgressRing.Visibility = Visibility.Collapsed;
        AlignmentStatusText.Text = "WORDS NOT ALIGNED";
        AlignWordsButton.Visibility = Visibility.Collapsed;
        AlignWordsButton.IsEnabled = false;
        FollowScriptToggle.IsEnabled = false;
        _activeWordAlignment = null;
        _activeAlignmentRecord = null;
        _activeAlignmentRender = null;
    }

    private async void TakeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EndFollowSession();
        await UpdateTransportAsync();
        if (GetSelectedRender() is not null) AudioDock.SelectTakeTarget();
    }

    private async void ListenToTake_Click(object sender,RoutedEventArgs e)
    {
        if (_listenTakePending) return;
        _listenTakePending = true;
        UpdateListenToTakeButton();
        AudioDock.SelectTakeTarget();
        try { await TakeTransport.TogglePlaybackAsync(); }
        finally { _listenTakePending = false; UpdateListenToTakeButton(); }
    }

    private void UpdateListenToTakeButton()
    {
        ListenToTakeButton.IsEnabled = TakeTransport.HasAudio && !_listenTakePending;
        ListenToTakeButton.Content = _listenTakePending ? "Loading take…" : TakeTransport.IsPlaying ? "Ⅱ Pause take" : "▶ Listen to take";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ListenToTakeButton,TakeTransport.IsPlaying ? "Pause selected narration take" : "Listen to selected narration take");
    }

    private async Task UpdateTransportAsync()
    {
        EndFollowSession();
        int loadVersion = Interlocked.Increment(ref _transportLoadVersion);
        _activeWordAlignment = null;
        _activeAlignmentRecord = null;
        _activeAlignmentRender = null;
        RenderRecord? render = GetSelectedRender();
        bool hasTake = render is not null;
        PreviousTakeButton.IsEnabled = TakeSelector.SelectedIndex >= 0 && TakeSelector.SelectedIndex < TakeSelector.Items.Count - 1;
        PickupButton.IsEnabled = hasTake;
        ApproveButton.IsEnabled = render?.Status == RenderStatus.Generated;
        AlignmentProgressRing.IsActive = false;
        AlignmentProgressRing.Visibility = Visibility.Collapsed;
        AlignWordsButton.IsEnabled = hasTake;
        FollowScriptToggle.IsEnabled = false;
        if (render is null)
        {
            AlignmentStatusText.Text = "WORDS NOT ALIGNED";
            AlignWordsButton.Visibility = Visibility.Collapsed;
            TakeTransport.ClearAudio("NO TAKE SELECTED");
            UpdateListenToTakeButton();
            return;
        }

        WordAlignmentRecord? latestAttempt = render.AlignmentHistory.LastOrDefault();
        WordAlignmentRecord? activeAttempt = render.AlignmentHistory.LastOrDefault(item =>
            item.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial
            && !string.IsNullOrWhiteSpace(item.ArtifactRelativePath));
        if (activeAttempt is not null)
        {
            try
            {
                WordAlignmentDocument alignment = await App.Services.LoadWordAlignmentAsync(activeAttempt);
                if (loadVersion != _transportLoadVersion) return;
                if (alignment.RenderId != render.RenderId
                    || !string.Equals(alignment.AudioSha256, activeAttempt.AudioSha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(alignment.SourceSha256, render.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The word-alignment artifact does not match this take.");
                }

                _activeWordAlignment = alignment;
                _activeAlignmentRecord = activeAttempt;
                _activeAlignmentRender = render;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                if (loadVersion != _transportLoadVersion) return;
                AlignmentStatusText.Text = "ALIGNMENT FILE UNAVAILABLE";
                AlignWordsButton.Content = "Retry alignment";
                AlignWordsButton.Visibility = Visibility.Visible;
            }
        }

        if (_activeWordAlignment is not null && _activeAlignmentRecord is not null)
        {
            AlignmentStatusText.Text = _activeAlignmentRecord.Status == WordAlignmentStatus.Aligned
                ? $"WORD FOLLOW READY  ·  {_activeAlignmentRecord.Coverage:P0}"
                : $"PARTIAL WORD FOLLOW  ·  {_activeAlignmentRecord.Coverage:P0}";
            AlignWordsButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            switch (latestAttempt?.Status)
            {
                case WordAlignmentStatus.Pending:
                    AlignmentStatusText.Text = "ALIGNING WORDS";
                    AlignmentProgressRing.Visibility = Visibility.Visible;
                    AlignmentProgressRing.IsActive = true;
                    AlignWordsButton.Visibility = Visibility.Collapsed;
                    break;
                case WordAlignmentStatus.Failed:
                    AlignmentStatusText.Text = latestAttempt.FailureCode == "alignment_cancelled"
                        ? "ALIGNMENT CANCELLED"
                        : "ALIGNMENT FAILED";
                    AlignWordsButton.Content = "Retry alignment";
                    AlignWordsButton.Visibility = Visibility.Visible;
                    break;
                case WordAlignmentStatus.Interrupted:
                    AlignmentStatusText.Text = "ALIGNMENT INTERRUPTED";
                    AlignWordsButton.Content = "Retry alignment";
                    AlignWordsButton.Visibility = Visibility.Visible;
                    break;
                case WordAlignmentStatus.Unsupported:
                    AlignmentStatusText.Text = "WORD FOLLOW UNSUPPORTED";
                    AlignWordsButton.Visibility = Visibility.Collapsed;
                    break;
                case WordAlignmentStatus.Aligned:
                case WordAlignmentStatus.Partial:
                    AlignmentStatusText.Text = "ALIGNMENT FILE UNAVAILABLE";
                    AlignWordsButton.Content = "Retry alignment";
                    AlignWordsButton.Visibility = Visibility.Visible;
                    break;
                default:
                    AlignmentStatusText.Text = "WORDS NOT ALIGNED";
                    AlignWordsButton.Content = "Align words";
                    AlignWordsButton.Visibility = Visibility.Visible;
                    break;
            }
        }

        UpdateFollowEligibility();
        string relativePath = _activeAlignmentRecord?.AudioRelativePath
            ?? render.ProcessedAudioRelativePath
            ?? render.RawAudioRelativePath;
        string path = App.Services.Session.ResolveProjectPath(relativePath);
        if (File.Exists(path))
        {
            TakeTransport.LoadAudio(path, $"Take {render.TakeNumber:00} · {render.Voice}", render.Duration);
        }
        else
        {
            TakeTransport.ClearAudio("TAKE AUDIO MISSING");
        }
        UpdateListenToTakeButton();
    }

    private async void AlignWords_Click(object sender, RoutedEventArgs e)
    {
        if (_alignmentCancellation is not null)
        {
            _alignmentCancellation.Cancel();
            AlignmentStatusText.Text = "CANCELLING ALIGNMENT";
            AlignWordsButton.IsEnabled = false;
            return;
        }

        if (_segment is null || GetSelectedRender() is not RenderRecord render) return;
        string segmentId = _segment.SegmentId;

        try
        {
            EndFollowSession();
            IssueLab.VoiceBench.App.Services.AlignmentProviderChoice? selectedAlignment = null;
            if (render.Origin == RenderOrigin.Imported)
            {
                ComboBox provider = new() { Header = "Transcription provider and model", ItemsSource = App.Services.AlignmentProviders, DisplayMemberPath = "Label", SelectedIndex = -1, HorizontalAlignment = HorizontalAlignment.Stretch };
                StackPanel panel = new() { Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = "This uploads the selected recording to the provider you choose. Importing and opening audio never uploads it.", TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(provider);
                ContentDialog choice = new() { XamlRoot = XamlRoot, Title = "Align imported recording", Content = panel, PrimaryButtonText = "Align with selected provider", CloseButtonText = "Cancel" };
                if (await choice.ShowAsync() != ContentDialogResult.Primary) return;
                selectedAlignment = provider.SelectedItem as IssueLab.VoiceBench.App.Services.AlignmentProviderChoice
                    ?? throw new InvalidOperationException("Select the provider and model explicitly.");
            }
            _alignmentCancellation = new CancellationTokenSource();
            AlignWordsButton.Content = "Cancel alignment";
            AlignWordsButton.IsEnabled = true;
            AlignmentProgressRing.Visibility = Visibility.Visible;
            AlignmentProgressRing.IsActive = true;
            AlignmentStatusText.Text = "ALIGNING WORDS";
            ShowStatus(
                InfoBarSeverity.Informational,
                "Aligning spoken words",
                $"VoiceBench is transcribing Take {render.TakeNumber:00} with its original provider. The take remains playable if alignment fails.");

            WordAlignmentRecord result = await App.Services.RunTrackedJobAsync<WordAlignmentRecord>(
                "alignment.start", new { segmentId, renderId = render.RenderId, providerId = selectedAlignment?.ProviderId, modelId = selectedAlignment?.ModelId }, _alignmentCancellation.Token);
            string message = result.Status switch
            {
                WordAlignmentStatus.Aligned => $"Word follow is ready for {result.MatchedWordCount} of {result.TotalWordCount} words ({result.Coverage:P0}).",
                WordAlignmentStatus.Partial => $"Partial word follow mapped {result.MatchedWordCount} of {result.TotalWordCount} words ({result.Coverage:P0}); uncertain gaps remain unselected.",
                WordAlignmentStatus.Unsupported => "This take's provider does not support word alignment. The audio is unchanged.",
                WordAlignmentStatus.Failed when result.FailureCode == "provider_credentials" => "The take is unchanged. Open Settings, add the original provider key, and retry alignment.",
                WordAlignmentStatus.Failed when result.FailureCode == "provider_authentication" => "The original provider rejected its saved key. Replace it in Settings and retry alignment.",
                WordAlignmentStatus.Failed when result.FailureCode == "provider_ratelimited" => "The original provider rate-limited transcription after bounded retries. Try again later.",
                WordAlignmentStatus.Failed when result.FailureCode == "provider_policy" => "The original provider rejected transcription under its content policy. The take is unchanged.",
                WordAlignmentStatus.Failed when result.FailureCode == "alignment_no_matches" => "No words could be matched reliably. Play the take to verify it, then retry or rerender it.",
                _ => "The audio is ready, but word alignment did not produce a usable map. You can retry without rerendering.",
            };
            ShowStatus(
                result.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                result.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial
                    ? "Word alignment complete"
                    : "Word alignment unavailable",
                message);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Word alignment cancelled", "The take is still playable. Retry alignment whenever you are ready.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Words could not be aligned", Actionable(exception));
        }
        finally
        {
            _alignmentCancellation?.Dispose();
            _alignmentCancellation = null;
            AlignmentProgressRing.IsActive = false;
            AlignmentProgressRing.Visibility = Visibility.Collapsed;
            if (App.Services.Session.HasProject && string.Equals(_segment?.SegmentId, segmentId, StringComparison.Ordinal))
            {
                _project = App.Services.Session.RequireProject();
                _segment = _project.Segments.SingleOrDefault(item => item.SegmentId == segmentId);
                if (_segment is not null)
                {
                    Guid renderId = render.RenderId;
                    PopulateTakeSelector(_segment);
                    TakeSelector.SelectedItem = TakeSelector.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(item => item.Tag is Guid id && id == renderId);
                }
            }
        }
    }

    private void FollowScriptToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = FollowScriptToggle.IsChecked == true;
        ApplicationData.Current.LocalSettings.Values[FollowScriptSetting] = enabled;
        if (!enabled)
        {
            EndFollowSession();
        }
        else
        {
            UpdateFollowEligibility();
        }
    }

    private void UpdateFollowEligibility(ParsedScriptDocument? parsed = null)
    {
        ParsedScriptDocument current = parsed ?? new ProductionScriptParser().Parse(GetEditorText());
        SyncMixFollowDraft(current);
        if (_activeWordAlignment is null || _activeAlignmentRender is null)
        {
            FollowScriptToggle.IsEnabled = App.Services.PlaybackTarget == "mix";
            return;
        }

        bool narrationCurrent = WordFollowPlaybackRules.IsNarrationCurrent(
            _activeAlignmentRender.SourceSha256,
            current.NarrationText);
        FollowScriptToggle.IsEnabled = narrationCurrent || App.Services.PlaybackTarget == "mix";
        if (!narrationCurrent)
        {
            EndFollowSession();
            AlignmentStatusText.Text = "SCRIPT CHANGED  ·  PLAYBACK ONLY";
            ToolTipService.SetToolTip(FollowScriptToggle, "Script changed—rerender to follow");
        }
        else
        {
            AlignmentStatusText.Text = _activeAlignmentRecord?.Status == WordAlignmentStatus.Partial
                ? $"PARTIAL WORD FOLLOW  ·  {_activeAlignmentRecord.Coverage:P0}"
                : $"WORD FOLLOW READY  ·  {_activeAlignmentRecord?.Coverage ?? 0:P0}";
            ToolTipService.SetToolTip(FollowScriptToggle, "Follow spoken words in the script editor");
        }
    }

    private void BeginFollowSession()
    {
        string editorText = GetEditorText();
        ParsedScriptDocument current = new ProductionScriptParser().Parse(editorText);
        UpdateFollowEligibility(current);
        if (FollowScriptToggle.IsChecked != true
            || !FollowScriptToggle.IsEnabled
            || _activeWordAlignment is null
            || _activeAlignmentRender is null)
        {
            return;
        }

        if (_followSessionActive) return;
        _cueFormatTimer?.Stop();
        ITextSelection selection = ScriptTextBox.Document.Selection;
        _followEditorSnapshot = new FollowEditorSnapshot(
            selection.StartPosition,
            selection.EndPosition,
            ScriptTextBox.IsReadOnly,
            ScriptTextBox.FocusState != FocusState.Unfocused);
        PlaybackScriptText.Blocks.Clear();
        Microsoft.UI.Xaml.Documents.Paragraph paragraph = new();
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = editorText });
        PlaybackScriptText.Blocks.Add(paragraph);
        PlaybackScriptSurface.Visibility = Visibility.Visible;
        ScriptTextBox.Visibility = Visibility.Collapsed;
        _followNarrationWords = current.NarrationWords;
        _followEditorTextLength = editorText.Length;
        _activeFollowWordOrdinal = -1;
        _followSessionActive = true;
    }

    private void TakeTransport_PlaybackPositionChanged(object? sender, AudioPositionChangedEventArgs e)
    {
        if (!_followSessionActive || _activeWordAlignment is null || _followNarrationWords is null) return;
        WordTiming? timing = WordFollowPlaybackRules.FindActiveWord(_activeWordAlignment.Words, e.Position);
        int ordinal = timing?.NarrationWordOrdinal ?? -1;
        if (ordinal == _activeFollowWordOrdinal) return;
        _activeFollowWordOrdinal = ordinal;

        PlaybackScriptText.TextHighlighters.Clear();
        if (ordinal < 0 || ordinal >= _followNarrationWords.Count) return;
        NarrationWord word = _followNarrationWords[ordinal];
        Microsoft.UI.Xaml.Documents.TextHighlighter highlight = new()
        {
            Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ActiveSignalBrush"],
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["BaseBrush"],
        };
        highlight.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = word.AuthoringStart, Length = word.AuthoringLength });
        PlaybackScriptText.TextHighlighters.Add(highlight);
        ScrollPlaybackWord(word.AuthoringStart);
    }
    private void TakeTransport_PlaybackStateChanged(object? sender, AudioTransportStateChangedEventArgs e)
    {
        UpdateListenToTakeButton();
        if (e.State is AudioTransportState.Paused or AudioTransportState.Ended
            or AudioTransportState.Failed or AudioTransportState.Cleared)
        {
            EndFollowSession();
        }
    }

    private void EndFollowSession()
    {
        if (!_followSessionActive)
        {
            _followEditorSnapshot = null;
            _followNarrationWords = null;
            _followEditorTextLength = 0;
            _activeFollowWordOrdinal = -1;
            return;
        }

        FollowEditorSnapshot? snapshot = _followEditorSnapshot;
        _followSessionActive = false;
        _followEditorSnapshot = null;
        _followNarrationWords = null;
        _followEditorTextLength = 0;
        _activeFollowWordOrdinal = -1;
        if (snapshot is null) return;

        _restoringPlaybackSurface = true;
        try
        {
            PlaybackScriptText.TextHighlighters.Clear();
            PlaybackScriptSurface.Visibility = Visibility.Collapsed;
            ScriptTextBox.Visibility = Visibility.Visible;
            if (snapshot.WasFocused) ScriptTextBox.Focus(FocusState.Programmatic);
        }
        finally { _restoringPlaybackSurface = false; }
    }

    private void AudioDock_DragDelta(object sender, DragDeltaEventArgs e) => AudioDock.ResizeDock(-e.VerticalChange);
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

    private ParsedScriptDocument UpdateEditorStats()
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(GetEditorText());
        TimeSpan estimate = SegmentRenderService.EstimateDuration(parsed.NarrationText);
        string timing = $"EST. {FormatDuration(estimate)}";
        if (_segment is not null)
        {
            string editorSourceHash = SourceTextHasher.Compute(parsed.NarrationText);
            SegmentDurationProjection projection = NarrationDurationProjection.Resolve(
                _segment with { SourceSha256 = editorSourceHash },
                estimate);
            if (projection.IsMeasured)
            {
                timing = $"LATEST TAKE {FormatDuration(projection.Duration)}  ·  EST. {FormatDuration(estimate)}";
            }

            TakeReviewStatusText.Text =
                $"{FormatStatus(_segment.Status)}  ·  {_segment.RenderHistory.Count} {(_segment.RenderHistory.Count == 1 ? "TAKE" : "TAKES")}  ·  {timing}";
        }

        string diagnostics = parsed.HasErrors ? $"  ·  {parsed.Diagnostics.Count} ISSUE{(parsed.Diagnostics.Count == 1 ? "" : "S")}" : string.Empty;
        SegmentStatsText.Text = $"{parsed.NarrationWordCount} SPOKEN WORDS  ·  {parsed.Cues.Count} CUES  ·  {timing}  ·  {(HasUnsavedSegmentChanges ? "UNSAVED" : "SAVED")}{diagnostics}";
        return parsed;
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

    private void QueueCueFormatting(bool resetFailures = true)
    {
        if (_cueFormatTimer is null || _restoringPlaybackSurface || _followSessionActive) return;
        if (resetFailures) _cueFormattingRetryCount = 0;
        _cueFormatTimer.Stop();
        _cueFormatTimer.Interval = resetFailures
            ? TimeSpan.FromMilliseconds(150)
            : TimeSpan.FromMilliseconds(250 * Math.Max(1, _cueFormattingRetryCount));
        _cueFormatTimer.Start();
    }

    private void ApplyCueFormatting()
    {
        if (_ignoreTextChanges || _followSessionActive || !ScriptTextBox.IsLoaded || !ScriptTextBox.IsEnabled) return;
        string text = GetEditorText();
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        ITextSelection selection = ScriptTextBox.Document.Selection;
        int selectionStart = selection.StartPosition;
        int selectionEnd = selection.EndPosition;
        bool wasReadOnly = ScriptTextBox.IsReadOnly;
        _ignoreTextChanges = true;
        try
        {
            // The WinUI text object model rejects display-format mutations while the
            // RichEditBox is read-only. Unlock only for this non-content operation.
            ScriptTextBox.IsReadOnly = false;
            ApplyEditorDefaultCharacterFormat();
            if (text.Length == 0) return;

            ITextRange all = ScriptTextBox.Document.GetRange(0, text.Length);
            ITextCharacterFormat baseFormat = all.CharacterFormat;
            ApplyNarrationCharacterFormat(baseFormat);
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
                format.Bold = FormatEffect.On;
                range.CharacterFormat = format;
            }

            _cueFormattingRetryCount = 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException)
        {
            _cueFormattingRetryCount++;
            if (_cueFormattingRetryCount <= MaxCueFormattingRetries)
            {
                QueueCueFormatting(resetFailures: false);
            }
            else
            {
                ShowStatus(
                    InfoBarSeverity.Informational,
                    "Cue colors unavailable",
                    "Editing and production cues still work. Switch segments or reopen the project to retry the colors.");
            }
        }
        finally
        {
            ScriptTextBox.IsReadOnly = wasReadOnly;
            try
            {
                ScriptTextBox.Document.Selection.SetRange(selectionStart, selectionEnd);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or COMException)
            {
                // Selection restoration is cosmetic and must never block editing.
            }
            _ignoreTextChanges = false;
        }
    }

    private void ApplyEditorDefaultCharacterFormat()
    {
        ITextCharacterFormat defaultFormat = ScriptTextBox.Document.GetDefaultCharacterFormat();
        ApplyNarrationCharacterFormat(defaultFormat);
        ScriptTextBox.Document.SetDefaultCharacterFormat(defaultFormat);
    }

    private void ApplyNarrationCharacterFormat(ITextCharacterFormat format)
    {
        format.ForegroundColor = ThemeColor("ReadingTextBrush", Color.FromArgb(255, 255, 255, 255));
        format.Bold = FormatEffect.Off;
        format.Name = ScriptTextBox.FontFamily.Source;
        format.Size = (float)ScriptTextBox.FontSize;
    }

    private static Color ThemeColor(string key, Color fallback) =>
        Microsoft.UI.Xaml.Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : fallback;

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
            ApplyEditorDefaultCharacterFormat();
            ScriptTextBox.Document.ClearUndoRedoHistory();
            _lastEditorText = GetEditorText();
        }
        finally
        {
            ScriptTextBox.IsReadOnly = wasReadOnly;
            ScriptTextBox.IsEnabled = wasEnabled;
            _ignoreTextChanges = false;
        }

        _isDirty = false;
        ResetEditorHistory();
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

    private static string? GetSelectedContentOrNull(ComboBox comboBox) =>
        comboBox.SelectedItem switch
        {
            ComboBoxItem { Tag: string tag } => tag,
            ComboBoxItem { Content: string content } => content,
            string content => content,
            _ => null,
        };

    private static void SetSelectedContent(ComboBox comboBox, string content)
    {
        ComboBoxItem? item = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, content, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ComboBoxItem { Content = content, Tag = content };
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
        WorkbenchInfoBar.Message = string.Empty;
        WorkbenchInfoBarMessage.Text = message;
        WorkbenchInfoBar.IsOpen = true;
    }
}
