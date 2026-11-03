using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace IssueLab.VoiceBench.App.Views;

/// <summary>
/// Chapter-rail row actions: hover buttons (play, approve, generate, more), approvals that wait for
/// the current render, and drag-and-drop reordering saved to the project.
/// </summary>
public sealed partial class WorkbenchView
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string StopGlyph = "";

    private readonly TakeApprovalQueue _approvalQueue = new();
    private FrameworkElement? _hoveredRailRow;
    private bool _drainingApprovals;
    private bool _approvalDrainScheduled;
    private string? _draggedRailId;

    /// <summary>True while a render, a batch, an agent job or queued approvals hold the studio.</summary>
    private bool StudioBusy =>
        _renderCancellation is not null || _renderRequestCancellation is not null || BatchGenerationActive || _drainingApprovals
        || _takeCleanupRunning;

    // ---- Hover actions --------------------------------------------------------------------------

    private void RailRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (_hoveredRailRow is not null && !ReferenceEquals(_hoveredRailRow, row)) SetRailRowActionsVisible(_hoveredRailRow, false);
        _hoveredRailRow = row;
        ShowRailRowActions(row);
    }

    private void RailRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        // Pressing a hover button captures the pointer and can raise a spurious exit while it is
        // still over the row; hiding the button then would swallow its click.
        Windows.Foundation.Point point = e.GetCurrentPoint(row).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < row.ActualWidth && point.Y < row.ActualHeight) return;
        SetRailRowActionsVisible(row, false);
        if (ReferenceEquals(_hoveredRailRow, row)) _hoveredRailRow = null;
    }

    /// <summary>Re-reads the hovered row's state, for example when playback starts or a take is approved.</summary>
    private void RefreshHoveredRailRow()
    {
        if (_hoveredRailRow is { } row && row.FindName("RailRowActions") is UIElement { Visibility: Visibility.Visible })
            ShowRailRowActions(row);
    }

    private static void SetRailRowActionsVisible(FrameworkElement row, bool visible)
    {
        if (row.FindName("RailRowActions") is UIElement strip)
            strip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowRailRowActions(FrameworkElement row)
    {
        if (RailSelecting || _project is null || row.DataContext is not TreeViewNode { Content: RailItem item }
            || row.FindName("RailRowPlayButton") is not Button play
            || row.FindName("RailRowPlayIcon") is not FontIcon playIcon
            || row.FindName("RailRowApproveButton") is not Button approve
            || row.FindName("RailRowGenerateButton") is not Button generate)
        {
            SetRailRowActionsVisible(row, false);
            return;
        }

        if (item.SegmentId is string segmentId)
        {
            SegmentDocument? segment = _project.Segments.FirstOrDefault(entry => entry.SegmentId == segmentId);
            if (segment is null)
            {
                SetRailRowActionsVisible(row, false);
                return;
            }

            RailTakeChoice? playTake = RailRowActions.TakeToPlay(segment);
            bool playing = playTake is not null && IsTakePlaying(segmentId, playTake.RenderId);
            ConfigureRailAction(play, playTake is not null,
                playing ? $"Pause {playTake!.Title}" : playTake is null ? "No take to play" : $"Play {playTake.Title} ({playTake.Description})");
            playIcon.Glyph = playing ? PauseGlyph : PlayGlyph;

            RailTakeChoice? approveTake = RailRowActions.TakeToApprove(segment);
            bool queued = approveTake is not null && _approvalQueue.Contains(approveTake.RenderId);
            ConfigureRailAction(approve, approveTake is not null,
                approveTake is null ? "Nothing to approve"
                : queued ? $"{approveTake.Title} is approved as soon as the current take finishes"
                : $"Approve {approveTake.Title}");

            string? blocked = RailRowActions.GenerateBlockedReason(segment, StudioBusy);
            ConfigureRailAction(generate, true, blocked is null ? "Generate a new take with the project voice…" : $"Generate a new take · {blocked}");
        }
        else
        {
            ChapterPlaylist? playlist = null;
            try { playlist = ChapterPlaylistBuilder.Build(_project, item.ChapterId); }
            catch (ArgumentException) { }
            bool playingChapter = ChapterPlaybackActive && _chapterPlaylist?.ChapterId == item.ChapterId;
            ConfigureRailAction(play, playingChapter || playlist?.Entries.Count > 0,
                playingChapter ? "Stop chapter playback" : playlist is null ? "Play chapter" : RailRowActions.ChapterPlayLabel(playlist));
            playIcon.Glyph = playingChapter ? StopGlyph : PlayGlyph;
            ConfigureRailAction(approve, false, "Approve take");
            ConfigureRailAction(generate, true, StudioBusy
                ? "Generate the chapter · wait for the current render to finish"
                : "Generate the chapter with the project voice…");
        }

        SetRailRowActionsVisible(row, true);
    }

    private static void ConfigureRailAction(Button button, bool available, string label)
    {
        button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private bool IsTakePlaying(string segmentId, Guid renderId) =>
        _segment?.SegmentId == segmentId && GetSelectedRender()?.RenderId == renderId && TakeTransport.IsPlaying;

    /// <summary>The rail item a hover button or rail menu item acts on.</summary>
    private RailItem? RailItemFor(object sender) =>
        sender is FrameworkElement { DataContext: TreeViewNode { Content: RailItem item } } ? item : GetSelectedRailItem();

    private async void RailRowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (RailItemFor(sender) is not RailItem item) return;
        if (item.SegmentId is string segmentId)
        {
            await PlaySegmentTakeFromRailAsync(segmentId);
        }
        else if (ChapterPlaybackActive && _chapterPlaylist?.ChapterId == item.ChapterId)
        {
            TakeTransport.Pause();
            StopChapterPlayback(announce: true);
        }
        else
        {
            await StartChapterPlaybackAsync(item.ChapterId);
        }

        RefreshHoveredRailRow();
    }

    /// <summary>Opens the segment (saving any draft, as a rail click does) and plays the take a reviewer needs.</summary>
    private async Task PlaySegmentTakeFromRailAsync(string segmentId)
    {
        SegmentDocument? segment = _project?.Segments.FirstOrDefault(item => item.SegmentId == segmentId);
        if (segment is null || RailRowActions.TakeToPlay(segment) is not { } take) return;
        if (IsTakePlaying(segmentId, take.RenderId))
        {
            TakeTransport.Pause();
            return;
        }

        try
        {
            if (_segment?.SegmentId != segmentId)
            {
                EndChapterPlaybackForManualChange();
                if (HasUnsavedSegmentChanges && !await SaveCurrentScriptAsync()) return;
                await LoadSegmentAsync(segmentId);
                if (_segment?.SegmentId != segmentId) return;
            }
            else
            {
                EndChapterPlaybackForManualChange(take.RenderId);
            }

            if (FindRender(take.RenderId) is null) return;
            SelectTake(take.RenderId);
            await UpdateTransportAsync();
            if (!TakeTransport.HasAudio)
            {
                ShowStatus(InfoBarSeverity.Warning, $"{take.Title} can't be played", "Its audio file is missing. Show the takes folder from the rail menu to check it.");
                return;
            }

            await PlaySelectedTakeAsync();
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "The take could not be played", Actionable(exception));
        }
    }

    private async void RailRowApprove_Click(object sender, RoutedEventArgs e)
    {
        if (RailItemFor(sender)?.SegmentId is not string segmentId) return;
        SegmentDocument? segment = App.Services.Session.Project?.Segments.FirstOrDefault(item => item.SegmentId == segmentId);
        if (segment is null || RailRowActions.TakeToApprove(segment) is not { } choice) return;
        RenderRecord? take = segment.RenderHistory.FirstOrDefault(item => item.RenderId == choice.RenderId);
        if (take is null) return;
        await RequestTakeApprovalAsync(segment, take);
        RefreshHoveredRailRow();
    }

    private async void RailRowGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (RailItemFor(sender) is not RailItem item || !App.Services.Session.HasProject) return;
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        if (item.SegmentId is string segmentId)
        {
            string title = project.Segments.FirstOrDefault(segment => segment.SegmentId == segmentId)?.Title ?? "segment";
            await StartBatchGenerationAsync([segmentId], $"“{title}”");
        }
        else
        {
            await StartChapterGenerationAsync(project, item.ChapterId);
        }
    }

    private void RailRowMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.FindName("RailRow") is not FrameworkElement row
            || row.DataContext is not TreeViewNode node || row.ContextFlyout is not FlyoutBase menu) return;
        _railContextNode = node;
        if (!RailSelecting) ChapterTree.SelectedNode = node;
        menu.ShowAt(button);
    }

    /// <summary>Names and shows the rail menu's Play and Approve items for the row it opened on.</summary>
    private void ConfigureRailMenuTakeItems(MenuFlyout flyout)
    {
        RailItem? target = GetSelectedRailItem();
        SegmentDocument? segment = target?.SegmentId is string segmentId
            ? _project?.Segments.FirstOrDefault(item => item.SegmentId == segmentId)
            : null;
        RailTakeChoice? play = segment is null ? null : RailRowActions.TakeToPlay(segment);
        RailTakeChoice? approve = segment is null ? null : RailRowActions.TakeToApprove(segment);
        foreach (MenuFlyoutItem item in flyout.Items.OfType<MenuFlyoutItem>())
        {
            switch (item.Tag as string)
            {
                case "segment-play":
                    item.Visibility = play is null ? Visibility.Collapsed : Visibility.Visible;
                    if (play is not null)
                        item.Text = IsTakePlaying(segment!.SegmentId, play.RenderId) ? $"Pause {play.Title}" : $"Play {play.Title} ({play.Description})";
                    break;
                case "segment-approve":
                    item.Visibility = approve is null ? Visibility.Collapsed : Visibility.Visible;
                    if (approve is not null) item.Text = $"Approve {approve.Title}";
                    break;
            }
        }
    }

    // ---- Approvals --------------------------------------------------------------------------------

    /// <summary>
    /// Approves a take now, or, while a render holds the studio, as soon as the current take
    /// finishes: approval converts the take with FFmpeg and saves the project, which must not
    /// overlap a render. Playback is never interrupted either way.
    /// </summary>
    private async Task RequestTakeApprovalAsync(SegmentDocument segment, RenderRecord take)
    {
        if (_approvalQueue.Contains(take.RenderId))
        {
            ShowStatus(InfoBarSeverity.Informational, $"Take {take.TakeNumber:00} is already waiting",
                $"“{segment.Title}” is approved as soon as the current take finishes.");
            return;
        }

        bool confirmedTranscript = false;
        if (take.Origin == RenderOrigin.Imported)
        {
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = "Confirm recording transcript",
                Content = "Listen to this recording and verify that the current reference transcript accurately represents its spoken words before approving it.",
                PrimaryButtonText = "Transcript is accurate · approve",
                CloseButtonText = "Cancel",
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            confirmedTranscript = true;
        }

        QueuedTakeApproval approval = new(segment.SegmentId, segment.Title, take.RenderId, take.TakeNumber, confirmedTranscript);
        if (StudioBusy)
        {
            _approvalQueue.Enqueue(approval);
            ShowStatus(InfoBarSeverity.Informational, $"Take {take.TakeNumber:00} will be approved next",
                $"“{segment.Title}” is approved as soon as the current take finishes. Keep listening meanwhile.");
            UpdateTakeCommandStates(GetSelectedRender());
            if (BatchGenerationActive) RefreshBatchGenerationUi();
            return;
        }

        _drainingApprovals = true;
        try { await ApproveTakeNowAsync(approval); }
        finally { _drainingApprovals = false; }
        // Anything queued while this one ran goes next.
        await DrainQueuedApprovalsAsync();
    }

    private async Task ApproveTakeNowAsync(QueuedTakeApproval approval)
    {
        try
        {
            ShowStatus(InfoBarSeverity.Informational, $"Approving take {approval.TakeNumber:00}",
                $"Converting and verifying “{approval.SegmentTitle}” with FFmpeg.");
            RenderRecord approved = await App.Services.ProcessAndApproveTakeAsync(
                approval.SegmentId, approval.RenderId, confirmedTranscript: approval.ConfirmedTranscript);
            await WaitForProjectRefreshAsync(CancellationToken.None);
            ShowStatus(InfoBarSeverity.Success, $"Take {approved.TakeNumber:00} approved",
                $"“{approval.SegmentTitle}” is approved against its current script.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, $"Take {approval.TakeNumber:00} could not be approved", Actionable(exception));
        }
        finally
        {
            UpdateTakeCommandStates(GetSelectedRender());
            RefreshHoveredRailRow();
        }
    }

    /// <summary>Approves every waiting take in order. The batch awaits this between takes.</summary>
    private async Task DrainQueuedApprovalsAsync()
    {
        if (_drainingApprovals || _approvalQueue.Count == 0) return;
        _drainingApprovals = true;
        try
        {
            while (_approvalQueue.TryDequeue(out QueuedTakeApproval approval))
            {
                if (BatchGenerationActive) RefreshBatchGenerationUi();
                await ApproveTakeNowAsync(approval);
            }
        }
        finally
        {
            _drainingApprovals = false;
            if (BatchGenerationActive) RefreshBatchGenerationUi();
        }
    }

    /// <summary>Runs waiting approvals once the studio goes idle outside a batch (which drains them itself).</summary>
    private void ScheduleQueuedApprovals()
    {
        if (_approvalQueue.Count == 0 || _approvalDrainScheduled || BatchGenerationActive) return;
        _approvalDrainScheduled = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            _approvalDrainScheduled = false;
            if (_renderCancellation is not null || _renderRequestCancellation is not null || BatchGenerationActive) return;
            await DrainQueuedApprovalsAsync();
        });
    }

    // ---- Drag and drop -------------------------------------------------------------------------

    private void ChapterTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        if (RailSelecting || _project is null)
        {
            args.Cancel = true;
            return;
        }

        RailItem? dragged = args.Items.FirstOrDefault() switch
        {
            TreeViewNode { Content: RailItem item } => item,
            RailItem item => item,
            _ => null,
        };
        _draggedRailId = dragged?.SegmentId ?? dragged?.ChapterId;
        if (_hoveredRailRow is not null) SetRailRowActionsVisible(_hoveredRailRow, false);
    }

    private void ChapterTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        string? draggedId = _draggedRailId;
        _draggedRailId = null;
        // Read the tree once the TreeView has finished moving its nodes.
        DispatcherQueue.TryEnqueue(async () => await ApplyRailDropAsync(draggedId));
    }

    private async Task ApplyRailDropAsync(string? draggedId)
    {
        if (_project is null || !App.Services.Session.HasProject) return;
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        RailReorderPlan plan = RailReorder.Plan(project, SnapshotRail(), draggedId);
        switch (plan.Kind)
        {
            case RailReorderKind.None:
                return;
            case RailReorderKind.Rejected:
                RestoreRail();
                ShowStatus(InfoBarSeverity.Warning, "That move isn't possible", plan.Reason ?? RailReorder.RailChanged);
                return;
        }

        string label = plan.Kind == RailReorderKind.Chapter ? "Reorder chapter" : "Reorder segment";
        try
        {
            await ExecuteAuthoringTransactionAsync(label, async cancellationToken =>
            {
                string root = App.Services.Session.RequireProjectRoot();
                if (plan.Kind == RailReorderKind.Chapter)
                    await App.Services.ChapterAuthoring.MoveChapterAsync(root, plan.ChapterId!, plan.Offset, cancellationToken);
                else
                    await App.Services.ChapterAuthoring.MoveSegmentAsync(root, plan.ChapterId!, plan.SegmentId!, plan.Offset, cancellationToken);
            });
            await App.Services.Session.RefreshAsync();
        }
        catch (Exception exception)
        {
            RestoreRail();
            ShowStatus(InfoBarSeverity.Error, "The new order could not be saved", Actionable(exception));
        }
    }

    private List<RailNodeSnapshot> SnapshotRail()
    {
        static RailNodeSnapshot Snapshot(TreeViewNode node)
        {
            RailItem? item = node.Content as RailItem;
            return new RailNodeSnapshot(item?.ChapterId ?? string.Empty, item?.SegmentId, node.Children.Select(Snapshot).ToList());
        }

        return ChapterTree.RootNodes.Select(Snapshot).ToList();
    }

    /// <summary>Puts the rail back to the project's order after a refused or failed drop.</summary>
    private void RestoreRail()
    {
        if (_project is null) return;
        PopulateChapterTree(_project);
        SelectTakeRailItem(_segment?.SegmentId);
    }
}
