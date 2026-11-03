using IssueLab.VoiceBench.App.Controls;
using IssueLab.VoiceBench.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace IssueLab.VoiceBench.App.Views;

/// <summary>
/// Plays every approved take of a chapter in order through the take bar. The editor follows each
/// segment so word-follow keeps working, and any manual segment or take change ends the session.
/// </summary>
public sealed partial class WorkbenchView
{
    private const int MaxChapterGapMilliseconds = 2000;
    private ChapterPlaylist? _chapterPlaylist;
    private int _chapterPlaylistIndex = -1;
    private int _chapterPlaybackVersion;
    private int _chapterPlaybackDrivingDepth;

    /// <summary>True while chapter playback itself is changing the segment or take selection.</summary>
    private bool _chapterPlaybackDriving => _chapterPlaybackDrivingDepth > 0;

    private bool ChapterPlaybackActive => _chapterPlaylist is not null;

    /// <summary>Starts chapter playback; used by the rail, the take bar and Assembly.</summary>
    internal async Task StartChapterPlaybackAsync(string chapterId, string? fromSegmentId = null)
    {
        if (_project is null || !App.Services.Session.HasProject)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Chapter playback uses the approved takes of the active project.");
            return;
        }

        if (HasUnsavedSegmentChanges && !await SaveCurrentScriptAsync()) return;
        ChapterPlaylist playlist;
        try
        {
            playlist = ChapterPlaylistBuilder.Build(App.Services.Session.RequireProject(), chapterId);
        }
        catch (ArgumentException exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Chapter could not be played", exception.Message);
            return;
        }

        if (playlist.Entries.Count == 0)
        {
            ShowStatus(
                InfoBarSeverity.Warning,
                "Nothing approved to play",
                $"No segment in “{playlist.ChapterTitle}” has an approved take yet. Approve takes in the take bar or the Takes tab.");
            return;
        }

        int start = playlist.StartIndexFor(App.Services.Session.RequireProject(), fromSegmentId);
        if (start >= playlist.Entries.Count) start = 0;
        StopChapterPlayback(announce: false);
        _chapterPlaylist = playlist;
        int version = ++_chapterPlaybackVersion;
        if (playlist.SkippedSegmentTitles.Count > 0)
        {
            ShowStatus(
                InfoBarSeverity.Informational,
                $"Playing {playlist.Entries.Count} approved take{(playlist.Entries.Count == 1 ? "" : "s")}",
                $"Skipping {playlist.SkippedSegmentTitles.Count} segment(s) without an approved take: {string.Join(", ", playlist.SkippedSegmentTitles.Take(4))}{(playlist.SkippedSegmentTitles.Count > 4 ? "…" : "")}");
        }

        await PlayChapterEntryAsync(start, version);
    }

    private async Task PlayChapterEntryAsync(int index, int version)
    {
        if (version != _chapterPlaybackVersion || _chapterPlaylist is not { } playlist) return;
        if (index < 0 || index >= playlist.Entries.Count)
        {
            bool finished = index >= playlist.Entries.Count;
            StopChapterPlayback(announce: false);
            if (finished)
                ShowStatus(InfoBarSeverity.Success, $"Finished “{playlist.ChapterTitle}”",
                    $"Played {playlist.Entries.Count} approved take{(playlist.Entries.Count == 1 ? "" : "s")} · {TakeListPresentation.FormatDuration(playlist.TotalDuration)}.");
            return;
        }

        _chapterPlaylistIndex = index;
        ChapterPlaylistEntry entry = playlist.Entries[index];
        UpdateChapterPlaybackPanel();
        _chapterPlaybackDrivingDepth++;
        try
        {
            if (_segment?.SegmentId != entry.SegmentId)
            {
                // Never let playback discard an edited draft: save it exactly as a rail click would.
                if (HasUnsavedSegmentChanges && !await SaveCurrentScriptAsync())
                {
                    StopChapterPlayback(announce: false);
                    ShowStatus(InfoBarSeverity.Warning, "Chapter playback paused",
                        "Save or discard the script changes, then play the chapter again.");
                    return;
                }

                if (version != _chapterPlaybackVersion) return;
                SelectTakeRailItem(entry.SegmentId);
                await LoadSegmentAsync(entry.SegmentId);
            }

            if (version != _chapterPlaybackVersion) return;
            if (_segment?.SegmentId != entry.SegmentId || FindRender(entry.RenderId) is null)
                throw new InvalidOperationException($"“{entry.Title}” changed while the chapter was playing.");
            SelectTake(entry.RenderId);
            await UpdateTransportAsync();
        }
        catch (Exception exception)
        {
            StopChapterPlayback(announce: false);
            ShowStatus(InfoBarSeverity.Error, "Chapter playback stopped", Actionable(exception));
            return;
        }
        finally
        {
            _chapterPlaybackDrivingDepth--;
        }

        if (version != _chapterPlaybackVersion) return;
        if (!TakeTransport.HasAudio)
        {
            // The approved take's file is missing or unreadable: say so and carry on with the chapter.
            ShowStatus(InfoBarSeverity.Warning, $"Skipped “{entry.Title}”",
                $"Take {entry.TakeNumber:00} audio could not be found at {entry.AudioRelativePath}.");
            await PlayChapterEntryAsync(index + 1, version);
            return;
        }

        await PlaySelectedTakeAsync();
    }

    /// <summary>Advances when the current chapter take plays to its end; stops if playback fails.</summary>
    private void AdvanceChapterPlaybackIfEnded(AudioTransportState state)
    {
        if (state == AudioTransportState.Failed && ChapterPlaybackActive && !_chapterPlaybackDriving)
        {
            StopChapterPlayback(announce: false);
            ShowStatus(InfoBarSeverity.Error, "Chapter playback stopped", "The take could not be played. Check the audio output in Settings and try again.");
            return;
        }

        if (state != AudioTransportState.Ended || _chapterPlaylist is not { } playlist) return;
        if (_chapterPlaylistIndex < 0 || _chapterPlaylistIndex >= playlist.Entries.Count) return;
        if (GetSelectedRender()?.RenderId != playlist.Entries[_chapterPlaylistIndex].RenderId) return;
        int version = _chapterPlaybackVersion;
        int next = _chapterPlaylistIndex + 1;
        int gap = Math.Clamp(App.Services.Session.Project?.AudioStandard.PauseMilliseconds ?? 0, 0, MaxChapterGapMilliseconds);
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (gap > 0) await Task.Delay(gap);
            await PlayChapterEntryAsync(next, version);
        });
    }

    /// <summary>
    /// Ends the session when the operator picks a different segment or take themselves. A refresh that
    /// reselects the take already playing (for example after a project save) keeps the session.
    /// </summary>
    private void EndChapterPlaybackForManualChange(Guid? selectedRenderId = null)
    {
        if (_chapterPlaylist is not { } playlist || _chapterPlaybackDriving) return;
        if (selectedRenderId is Guid id && _chapterPlaylistIndex >= 0 && _chapterPlaylistIndex < playlist.Entries.Count
            && playlist.Entries[_chapterPlaylistIndex].RenderId == id) return;
        StopChapterPlayback(announce: false);
    }

    private void StopChapterPlayback(bool announce)
    {
        if (_chapterPlaylist is null) return;
        string title = _chapterPlaylist.ChapterTitle;
        _chapterPlaylist = null;
        _chapterPlaylistIndex = -1;
        _chapterPlaybackVersion++;
        UpdateChapterPlaybackPanel();
        if (announce) ShowStatus(InfoBarSeverity.Informational, "Chapter playback stopped", $"Stopped playing “{title}”.");
    }

    private void UpdateChapterPlaybackPanel()
    {
        if (_chapterPlaylist is not { } playlist || _chapterPlaylistIndex < 0 || _chapterPlaylistIndex >= playlist.Entries.Count)
        {
            ChapterPlaybackPanel.Visibility = Visibility.Collapsed;
            UpdateFooterVisibility();
            return;
        }

        ChapterPlaylistEntry entry = playlist.Entries[_chapterPlaylistIndex];
        ChapterPlaybackPanel.Visibility = Visibility.Visible;
        UpdateFooterVisibility();
        ChapterPlaybackTitleText.Text = $"PLAYING CHAPTER  ·  {playlist.ChapterTitle.ToUpperInvariant()}";
        ChapterPlaybackDetailText.Text =
            $"{_chapterPlaylistIndex + 1} / {playlist.Entries.Count}  ·  {entry.Title}  ·  take {entry.TakeNumber:00}" +
            (entry.ScriptChanged ? "  ·  script changed since approval" : string.Empty) +
            $"  ·  chapter {TakeListPresentation.FormatDuration(playlist.TotalDuration)}";
        ChapterPreviousButton.IsEnabled = _chapterPlaylistIndex > 0;
        ChapterNextButton.IsEnabled = _chapterPlaylistIndex < playlist.Entries.Count - 1;
    }

    private string? CurrentChapterId() =>
        GetSelectedRailItem()?.ChapterId ?? _segment?.ChapterId;

    /// <summary>Rail menus: the chapter of the selected rail item, from its first approved take.</summary>
    private async void PlayChapter_Click(object sender, RoutedEventArgs e) =>
        await StartChapterPlaybackForAsync(CurrentChapterId(), null);

    /// <summary>Rail segment menu: the segment's chapter, starting at that segment.</summary>
    private async void PlayChapterFromHere_Click(object sender, RoutedEventArgs e)
    {
        RailItem? rail = GetSelectedRailItem();
        await StartChapterPlaybackForAsync(rail?.ChapterId ?? _segment?.ChapterId, rail?.SegmentId ?? _segment?.SegmentId);
    }

    /// <summary>Take bar: the open segment's chapter, from the start.</summary>
    private async void PlayChapterTakeBar_Click(object sender, RoutedEventArgs e) =>
        await StartChapterPlaybackForAsync(_segment?.ChapterId, null);

    private async Task StartChapterPlaybackForAsync(string? chapterId, string? fromSegmentId)
    {
        if (chapterId is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Select a chapter", "Choose a chapter or segment in the chapter rail first.");
            return;
        }

        await StartChapterPlaybackAsync(chapterId, fromSegmentId);
    }

    // A new version cancels any pending auto-advance and any skip still loading.
    private async void ChapterPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (!ChapterPlaybackActive) return;
        await PlayChapterEntryAsync(_chapterPlaylistIndex - 1, ++_chapterPlaybackVersion);
    }

    private async void ChapterNext_Click(object sender, RoutedEventArgs e)
    {
        if (!ChapterPlaybackActive) return;
        await PlayChapterEntryAsync(_chapterPlaylistIndex + 1, ++_chapterPlaybackVersion);
    }

    private void ChapterStop_Click(object sender, RoutedEventArgs e)
    {
        TakeTransport.Pause();
        StopChapterPlayback(announce: true);
    }

    private async void PlayChapterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ChapterPlaybackActive)
        {
            TakeTransport.Pause();
            StopChapterPlayback(announce: true);
            return;
        }

        if (_segment?.ChapterId is string chapterId) await StartChapterPlaybackAsync(chapterId);
    }
}
