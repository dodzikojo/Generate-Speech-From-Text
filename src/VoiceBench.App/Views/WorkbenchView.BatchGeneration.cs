using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IssueLab.VoiceBench.App.Views;

/// <summary>
/// Generates a take for several segments, one provider request at a time, with the project voice.
/// Segments come from rail ticks, "from here to chapter end" or a whole chapter; a confirmation lists
/// every candidate so the operator can untick any before money is spent. Each take goes through the
/// same tracked <c>render.start</c> job as a single render, so the editor follows along and agents see
/// the jobs.
/// </summary>
public sealed partial class WorkbenchView
{
    private sealed class BatchGenerationRun(IReadOnlyList<BatchCandidate> entries)
    {
        public IReadOnlyList<BatchCandidate> Entries { get; } = entries;

        public CancellationTokenSource Cancellation { get; } = new();

        public int Index { get; set; }

        public int Completed { get; set; }

        public bool StopRequested { get; set; }

        public double Fraction => Entries.Count == 0 ? 0 : (double)Completed / Entries.Count;

        public void CancelNow()
        {
            StopRequested = true;
            if (!Cancellation.IsCancellationRequested) Cancellation.Cancel();
        }
    }

    private sealed record BatchVoiceDescription(string Line, string? Note);

    private BatchGenerationRun? _batchRun;
    private readonly HashSet<string> _railCheckedSegmentIds = new(StringComparer.Ordinal);
    private TreeViewNode? _railContextNode;
    private bool _populatingRail;

    private bool BatchGenerationActive => _batchRun is not null;

    private bool RailSelecting => ChapterTree.SelectionMode == TreeViewSelectionMode.Multiple;

    private void UpdateFooterVisibility()
    {
        bool showTakeBar = TakeReviewToggle.IsChecked == true;
        TakeBarGrid.Visibility = showTakeBar ? Visibility.Visible : Visibility.Collapsed;
        WorkbenchFooter.Visibility = showTakeBar || BatchGenerationActive || ChapterPlaybackActive
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---- Entry points -------------------------------------------------------------------------

    private async void GenerateFromHere_Click(object sender, RoutedEventArgs e)
    {
        (string? segmentId, string? chapterId) = ResolveGenerationTarget(sender);
        VoiceBenchProjectDocument? project = App.Services.Session.HasProject ? App.Services.Session.RequireProject() : null;
        if (project is null || (segmentId is null && chapterId is null))
        {
            ShowStatus(InfoBarSeverity.Warning, "Select a segment first", "Choose the segment to start from in the chapter rail or the segment picker.");
            return;
        }

        if (segmentId is null)
        {
            await StartChapterGenerationAsync(project, chapterId!);
            return;
        }

        string title = project.Segments.FirstOrDefault(item => item.SegmentId == segmentId)?.Title ?? "this segment";
        await StartBatchGenerationAsync(BatchGenerationPlanner.FromHereToChapterEnd(project, segmentId), $"from “{title}” to chapter end");
    }

    private async void GenerateChapter_Click(object sender, RoutedEventArgs e)
    {
        (_, string? chapterId) = ResolveGenerationTarget(sender);
        if (!App.Services.Session.HasProject || chapterId is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Select a chapter first", "Choose a chapter or one of its segments in the chapter rail.");
            return;
        }

        await StartChapterGenerationAsync(App.Services.Session.RequireProject(), chapterId);
    }

    private Task StartChapterGenerationAsync(VoiceBenchProjectDocument project, string chapterId)
    {
        string title = project.Chapters.FirstOrDefault(item => item.ChapterId == chapterId)?.Title ?? "chapter";
        return StartBatchGenerationAsync(BatchGenerationPlanner.WholeChapter(project, chapterId), $"chapter “{title}”");
    }

    private void ChooseSegmentsToGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Session.HasProject) return;
        (string? segmentId, string? chapterId) = ResolveGenerationTarget(sender);
        if (ChapterRailToggle.IsChecked != true)
        {
            ChapterRailToggle.IsChecked = true;
            SaveWorkspacePanelPreferences();
            ApplyWorkspacePanelLayout(ActualWidth, ActualHeight);
        }

        if (TakeRailPanel.Visibility != Visibility.Visible)
        {
            ShowStatus(
                InfoBarSeverity.Informational,
                "Widen the window to tick segments",
                "The chapter rail needs more room. Batch › From this segment or Whole chapter also lets you untick segments before generating.");
            return;
        }

        IEnumerable<string> seed = segmentId is not null
            ? [segmentId]
            : chapterId is not null
                ? BatchGenerationPlanner.WholeChapter(App.Services.Session.RequireProject(), chapterId)
                : [];
        SetRailSelectionMode(true, seed);
    }

    private async void GenerateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_railCheckedSegmentIds.Count == 0) return;
        await StartBatchGenerationAsync(_railCheckedSegmentIds.ToArray(), "selected segments");
    }

    /// <summary>Director menu items act on the open segment; rail menus act on the row they were opened from.</summary>
    private (string? SegmentId, string? ChapterId) ResolveGenerationTarget(object sender)
    {
        bool fromDirector = sender is FrameworkElement { Tag: "director" };
        RailItem? item = fromDirector || TakeRailPanel.Visibility != Visibility.Visible ? null : GetSelectedRailItem();
        return item is null ? (_segment?.SegmentId, _segment?.ChapterId) : (item.SegmentId, item.ChapterId);
    }

    // ---- Rail selection mode -------------------------------------------------------------------

    private void RailSelectToggle_Click(object sender, RoutedEventArgs e) =>
        SetRailSelectionMode(RailSelectToggle.IsChecked == true, _segment?.SegmentId is string id ? [id] : []);

    private void CloseRailSelection_Click(object sender, RoutedEventArgs e) => SetRailSelectionMode(false);

    private void SetRailSelectionMode(bool on, IEnumerable<string>? seed = null)
    {
        _populatingRail = true;
        try
        {
            ChapterTree.SelectedNodes.Clear();
            ChapterTree.SelectionMode = on ? TreeViewSelectionMode.Multiple : TreeViewSelectionMode.Single;
            // Dragging ticked rows would reorder the project by accident; reorder outside selection mode.
            ChapterTree.CanDragItems = !on;
            ChapterTree.CanReorderItems = !on;
            if (!on)
            {
                _railCheckedSegmentIds.Clear();
                _railContextNode = null;
            }
        }
        finally
        {
            _populatingRail = false;
        }

        if (on)
        {
            if (seed is not null) _railCheckedSegmentIds.UnionWith(seed);
            ApplyRailChecks();
        }
        else
        {
            SelectTakeRailItem(_segment?.SegmentId);
        }

        RailSelectToggle.IsChecked = on;
        RailSelectionBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateRailSelectionBar();
    }

    /// <summary>Re-applies the remembered ticks after the rail is rebuilt (every save or render rebuilds it).</summary>
    private void ApplyRailChecks()
    {
        HashSet<string> present = new(StringComparer.Ordinal);
        _populatingRail = true;
        try
        {
            ChapterTree.SelectedNodes.Clear();
            foreach (TreeViewNode child in ChapterTree.RootNodes.SelectMany(chapter => chapter.Children))
            {
                if (child.Content is not RailItem { SegmentId: string segmentId }) continue;
                present.Add(segmentId);
                if (_railCheckedSegmentIds.Contains(segmentId)) ChapterTree.SelectedNodes.Add(child);
            }
        }
        finally
        {
            _populatingRail = false;
        }

        _railCheckedSegmentIds.IntersectWith(present);
        UpdateRailSelectionBar();
    }

    /// <summary>The right-clicked row only targets that menu's actions; later actions use the open segment.</summary>
    private void RailContextMenu_Closed(object sender, object e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _railContextNode = null);

    private void ChapterTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_populatingRail || !RailSelecting) return;
        ReadRailChecks();
        // Ticking a chapter selects its children as part of the same gesture; read again once it settles.
        DispatcherQueue.TryEnqueue(ReadRailChecks);
    }

    private void ReadRailChecks()
    {
        if (_populatingRail || !RailSelecting) return;
        _railCheckedSegmentIds.Clear();
        foreach (TreeViewNode node in ChapterTree.SelectedNodes)
        {
            if (node.Content is not RailItem item) continue;
            if (item.SegmentId is string segmentId)
            {
                _railCheckedSegmentIds.Add(segmentId);
                continue;
            }

            foreach (TreeViewNode child in node.Children)
                if (child.Content is RailItem { SegmentId: string childId }) _railCheckedSegmentIds.Add(childId);
        }

        UpdateRailSelectionBar();
    }

    private void UpdateRailSelectionBar()
    {
        int count = _railCheckedSegmentIds.Count;
        RailSelectionCountText.Text = count == 0
            ? "Tick segments, or a chapter for all of its segments"
            : $"{count} {(count == 1 ? "segment" : "segments")} selected";
        GenerateSelectedButton.IsEnabled = count > 0 && !BatchGenerationActive;
    }

    private TreeViewNode? FindRailNode(string? segmentId) => segmentId is null
        ? null
        : ChapterTree.RootNodes
            .SelectMany(chapter => chapter.Children)
            .FirstOrDefault(candidate => candidate.Content is RailItem item &&
                string.Equals(item.SegmentId, segmentId, StringComparison.Ordinal));

    // ---- Planning and confirmation -------------------------------------------------------------

    private async Task StartBatchGenerationAsync(IReadOnlyList<string> segmentIds, string scope)
    {
        if (_project is null || !App.Services.Session.HasProject)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Batch generation uses the segments and project voice of the active project.");
            return;
        }

        if (StudioBusy)
        {
            ShowStatus(InfoBarSeverity.Warning, "A render is already active", "Cancel the current job or wait for it to finish.");
            return;
        }

        if (HasUnsavedSegmentChanges && !await SaveCurrentScriptAsync()) return;
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        IReadOnlyDictionary<string, string?> problems = await LoadScriptProblemsAsync(project, segmentIds);
        IReadOnlyList<BatchCandidate> candidates = BatchGenerationPlanner.Candidates(
            project,
            segmentIds,
            _estimatedSegmentDurations,
            segment => problems.TryGetValue(segment.SegmentId, out string? problem) ? problem : null);
        if (candidates.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Nothing to generate", "None of those segments is in an active chapter.");
            return;
        }

        IReadOnlyList<BatchCandidate>? chosen = await ConfirmBatchGenerationAsync(scope, candidates, DescribeBatchVoice(project));
        if (chosen is null || chosen.Count == 0) return;
        if (BatchGenerationActive || _renderCancellation is not null || _renderRequestCancellation is not null)
        {
            ShowStatus(InfoBarSeverity.Warning, "A render is already active", "Another job started while the confirmation was open. Try again when it finishes.");
            return;
        }

        if (!await EnsureProjectVoiceAsync()) return;
        if (RailSelecting) SetRailSelectionMode(false);
        await RunBatchGenerationAsync(chosen);
    }

    private static async Task<IReadOnlyDictionary<string, string?>> LoadScriptProblemsAsync(
        VoiceBenchProjectDocument project,
        IReadOnlyList<string> segmentIds)
    {
        HashSet<string> wanted = new(segmentIds, StringComparer.Ordinal);
        Dictionary<string, string?> problems = new(StringComparer.Ordinal);
        foreach (SegmentDocument segment in project.Segments.Where(item => wanted.Contains(item.SegmentId)))
        {
            try
            {
                string text = await File.ReadAllTextAsync(App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath));
                problems[segment.SegmentId] = BatchGenerationPlanner.ScriptProblem(text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems[segment.SegmentId] = "The script file could not be read";
            }
        }

        return problems;
    }

    private BatchVoiceDescription DescribeBatchVoice(VoiceBenchProjectDocument project)
    {
        string providerId = GetSelectedProviderId();
        string shown = $"{ProviderDisplayName(providerId)} · {GetSelectedContent(ModelComboBox)} · {GetSelectedContent(VoiceComboBox)}";
        VoicePreset? projectVoice = project.DefaultVoicePresetId is string id
            ? project.VoicePresets.FirstOrDefault(item => item.PresetId == id)
            : null;
        if (projectVoice is null)
            return new($"Voice: {shown}", "There is no project voice yet, so the voice shown in the Voice tab becomes the project voice.");

        string line = $"Project voice: {ProviderDisplayName(projectVoice.ProviderId)} · {projectVoice.ModelId} · {projectVoice.Voice}";
        bool sameAsShown = projectVoice.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            && projectVoice.ModelId.Equals(GetSelectedContent(ModelComboBox), StringComparison.Ordinal)
            && projectVoice.Voice.Equals(GetSelectedContent(VoiceComboBox), StringComparison.OrdinalIgnoreCase);
        return new(line, sameAsShown
            ? null
            : $"The Voice tab shows {shown}. To generate with that voice instead, cancel and click Use as project voice first.");
    }

    private static string ProviderDisplayName(string providerId) =>
        App.Services.SpeechProviders
            .FirstOrDefault(item => item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))?
            .Descriptor.DisplayName ?? providerId;

    private async Task<IReadOnlyList<BatchCandidate>?> ConfirmBatchGenerationAsync(
        string scope,
        IReadOnlyList<BatchCandidate> candidates,
        BatchVoiceDescription voice)
    {
        Brush muted = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["MutedTextBrush"];
        StackPanel root = new() { Spacing = 10, MinWidth = 440 };
        root.Children.Add(new TextBlock { Text = voice.Line, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        if (voice.Note is not null)
            root.Children.Add(new TextBlock { Text = voice.Note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = muted });

        List<(CheckBox Box, BatchCandidate Candidate)> boxes = [];
        StackPanel list = new() { Spacing = 2 };
        bool severalChapters = candidates.Select(item => item.ChapterId).Distinct(StringComparer.Ordinal).Count() > 1;
        string? lastChapterId = null;
        foreach (BatchCandidate candidate in candidates)
        {
            if (severalChapters && candidate.ChapterId != lastChapterId)
            {
                TextBlock header = new() { Text = candidate.ChapterTitle.ToUpperInvariant(), Margin = new Thickness(0, lastChapterId is null ? 0 : 8, 0, 2) };
                header.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"];
                list.Children.Add(header);
                lastChapterId = candidate.ChapterId;
            }

            StackPanel label = new() { Spacing = 1 };
            label.Children.Add(new TextBlock { Text = candidate.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = candidate.Detail, FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.WrapWholeWords });
            CheckBox box = new()
            {
                Content = label,
                IsChecked = candidate.CheckedByDefault,
                IsEnabled = candidate.CanGenerate,
            };
            AutomationProperties.SetName(box, $"{candidate.DisplayTitle}. {candidate.Detail}");
            boxes.Add((box, candidate));
            list.Children.Add(box);
        }

        int approvedUnticked = candidates.Count(item => item.CanGenerate && !item.CheckedByDefault);
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        HyperlinkButton selectAll = new() { Content = "Tick all", Padding = new Thickness(4, 2, 4, 2) };
        HyperlinkButton selectNone = new() { Content = "Untick all", Padding = new Thickness(4, 2, 4, 2) };
        tools.Children.Add(selectAll);
        tools.Children.Add(selectNone);
        root.Children.Add(tools);
        root.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 14, 0),
        });
        if (approvedUnticked > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Approved segments start unticked so they keep their approval. Tick one to replace its take.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = muted,
            });
        }

        TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        root.Children.Add(summary);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Generate audio · {scope}",
            Content = root,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        List<BatchCandidate> Picked() => boxes.Where(item => item.Box.IsChecked == true).Select(item => item.Candidate).ToList();
        void Refresh()
        {
            List<BatchCandidate> picked = Picked();
            dialog.IsPrimaryButtonEnabled = picked.Count > 0;
            dialog.PrimaryButtonText = picked.Count == 0 ? "Generate" : $"Generate {picked.Count} {(picked.Count == 1 ? "take" : "takes")}";
            TimeSpan estimate = BatchGenerationPlanner.TotalEstimate(picked);
            summary.Text = picked.Count == 0
                ? "Tick at least one segment."
                : $"Segments are generated one at a time{(estimate > TimeSpan.Zero ? $", about {TakeListPresentation.FormatDuration(estimate)} of narration" : string.Empty)}. Each sends a speech request, then a word-timing request to the same provider where it supports one. You can stop after any take.";
        }

        foreach ((CheckBox box, _) in boxes)
        {
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
        }

        selectAll.Click += (_, _) => { foreach ((CheckBox box, BatchCandidate candidate) in boxes) if (candidate.CanGenerate) box.IsChecked = true; };
        selectNone.Click += (_, _) => { foreach ((CheckBox box, _) in boxes) box.IsChecked = false; };
        Refresh();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? Picked() : null;
    }

    /// <summary>Creates the project voice from the Voice tab when the project has none yet.</summary>
    private async Task<bool> EnsureProjectVoiceAsync()
    {
        try
        {
            VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
            if (project.DefaultVoicePresetId is string presetId && project.VoicePresets.Any(item => item.PresetId == presetId))
                return true;
            GetOrCreateSelectedVoicePreset(project, setAsProjectDefault: true, out VoiceBenchProjectDocument updated);
            await SaveAndReloadProjectAsync(updated);
            return true;
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "The project voice could not be set", Actionable(exception));
            return false;
        }
    }

    /// <summary>
    /// Points one segment at the project voice just before its render, so a batch that stops early
    /// leaves the segments it never reached with their own voice.
    /// </summary>
    private async Task UseProjectVoiceForSegmentAsync(string segmentId)
    {
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        int index = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        if (index < 0 || project.Segments[index].VoicePresetId is null) return;
        List<SegmentDocument> segments = [.. project.Segments];
        segments[index] = segments[index] with { VoicePresetId = null };
        await SaveAndReloadProjectAsync(project with { Segments = segments });
    }

    private async Task SaveAndReloadProjectAsync(VoiceBenchProjectDocument updated)
    {
        await App.Services.Session.SaveAsync(updated);
        await WaitForProjectRefreshAsync(CancellationToken.None);
        _project = App.Services.Session.RequireProject();
        if (_segment is not null)
            _segment = _project.Segments.FirstOrDefault(item => item.SegmentId == _segment.SegmentId) ?? _segment;
    }

    // ---- Running -------------------------------------------------------------------------------

    private async Task RunBatchGenerationAsync(IReadOnlyList<BatchCandidate> chosen)
    {
        // Playback carries on: segments that aren't open render in the background, so the operator can
        // keep listening to (and approving) finished takes while the batch works through the rest.
        BatchGenerationRun run = new(chosen);
        _batchRun = run;
        List<BatchGenerationFailure> failures = [];
        BatchGenerationFailure? stopFailure = null;
        bool cancelled = false;
        int generated = 0;
        int consecutiveFailures = 0;
        WorkbenchInfoBar.IsOpen = false;
        RefreshBatchGenerationUi();
        try
        {
            for (int index = 0; index < chosen.Count; index++)
            {
                if (run.StopRequested)
                {
                    cancelled = true;
                    break;
                }

                run.Index = index;
                RefreshBatchGenerationUi();
                BatchCandidate entry = chosen[index];
                try
                {
                    await UseProjectVoiceForSegmentAsync(entry.SegmentId);
                    // The render applies the Voice tab's selection for the open segment, so make sure it
                    // shows that segment's (now project) voice rather than an unapplied choice.
                    if (_project is not null && _segment?.SegmentId == entry.SegmentId) LoadSegmentPreset(_project, _segment);
                    await App.Services.RunTrackedJobAsync<SegmentRenderResult>(
                        "render.start",
                        new { segmentId = entry.SegmentId },
                        run.Cancellation.Token);
                    await WaitForProjectRefreshAsync(CancellationToken.None);
                    generated++;
                    consecutiveFailures = 0;
                    // Approvals asked for during this take run now, before the next take starts.
                    await DrainQueuedApprovalsAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (AutomationException exception) when (exception.Code == "operation.cancelled")
                {
                    cancelled = true;
                    break;
                }
                catch (AutomationException exception)
                {
                    consecutiveFailures++;
                    BatchGenerationFailure failure = new(entry.DisplayTitle, BatchGenerationRules.FailureMessage(exception.Code, exception.Message));
                    failures.Add(failure);
                    if (BatchGenerationRules.ShouldStop(exception.Code, consecutiveFailures))
                    {
                        stopFailure = failure;
                        break;
                    }
                }
                catch (Exception exception)
                {
                    BatchGenerationFailure failure = new(entry.DisplayTitle, Actionable(exception));
                    failures.Add(failure);
                    stopFailure = failure;
                    break;
                }
                finally
                {
                    run.Completed = generated + failures.Count;
                }
            }
        }
        finally
        {
            _batchRun = null;
            run.Cancellation.Dispose();
            RefreshBatchGenerationUi();
        }

        BatchGenerationSummary summary = new BatchGenerationOutcome(
            chosen.Count,
            generated,
            failures,
            chosen.Count - generated - failures.Count,
            cancelled,
            stopFailure).Summarize();
        ShowStatus(
            summary.Severity switch
            {
                BatchGenerationSeverity.Success => InfoBarSeverity.Success,
                BatchGenerationSeverity.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Error,
            },
            summary.Title,
            summary.Message);
    }

    private void BatchStop_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRun is not { } run) return;
        if (!run.StopRequested)
        {
            run.StopRequested = true;
            RefreshBatchGenerationUi();
            return;
        }

        // Cancel the provider request itself, as Cancel render does, not only the wait for it.
        run.CancelNow();
        _renderCancellation?.Cancel();
    }

    private void RefreshBatchGenerationUi()
    {
        SetRenderQueueBusy(BatchGenerationActive || _renderCancellation is not null || _renderRequestCancellation is not null);
        UpdateRailSelectionBar();
        if (_batchRun is not { } run || run.Entries.Count == 0)
        {
            BatchGenerationPanel.Visibility = Visibility.Collapsed;
            UpdateFooterVisibility();
            return;
        }

        BatchCandidate entry = run.Entries[Math.Clamp(run.Index, 0, run.Entries.Count - 1)];
        BatchGenerationPanel.Visibility = Visibility.Visible;
        BatchGenerationTitleText.Text = run.StopRequested
            ? $"STOPPING AFTER TAKE {run.Index + 1} OF {run.Entries.Count}"
            : $"GENERATING {run.Index + 1} OF {run.Entries.Count}  ·  PROJECT VOICE";
        int queuedApprovals = _approvalQueue.Count;
        BatchGenerationDetailText.Text = queuedApprovals == 0
            ? $"{entry.ChapterTitle}  /  {entry.DisplayTitle}"
            : $"{entry.ChapterTitle}  /  {entry.DisplayTitle}  ·  {queuedApprovals} approval{(queuedApprovals == 1 ? "" : "s")} waiting";
        BatchGenerationProgress.Value = run.Fraction;
        BatchStopText.Text = run.StopRequested ? "Cancel this take too" : "Stop after this take";
        AutomationProperties.SetName(BatchStopButton, run.StopRequested ? "Cancel the take in progress" : "Stop batch generation after this take");
        UpdateFooterVisibility();
    }
}
