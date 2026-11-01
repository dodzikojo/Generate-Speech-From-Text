using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class WorkbenchView
{
    private readonly DispatcherTimer _mixFollowTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<Guid,MixFollowSource> _mixFollowSources = [];
    private readonly Dictionary<string,ParsedScriptDocument> _mixFollowDrafts = [];
    private Guid? _mixFollowClipId;
    private bool _mixFollowing;
    private int _mixFollowLoadVersion;
    private sealed record MixFollowSource(MixRenderClip Clip,string Text,ParsedScriptDocument Parsed,WordAlignmentDocument Alignment);

    private void InitializeMixFollow()
    {
        _mixFollowTimer.Tick += (_, _) => UpdateMixFollow();
        _mixFollowTimer.Start();
        App.Services.AudioTargetChanged += (_, _) =>
        {
            if (DispatcherQueue.HasThreadAccess) _ = PrepareMixFollowAsync();
            else DispatcherQueue.TryEnqueue(async () => await PrepareMixFollowAsync());
        };
        App.Services.MixParametersUpdated += (_, _) =>
        {
            if (DispatcherQueue.HasThreadAccess) RefreshMixFollowParameters();
            else DispatcherQueue.TryEnqueue(RefreshMixFollowParameters);
        };
    }

    private async Task PrepareMixFollowAsync()
    {
        int version = ++_mixFollowLoadVersion;
        _mixFollowSources.Clear(); _mixFollowClipId = null;
        if (_mixFollowing) EndFollowSession();
        _mixFollowing = false;
        if (App.Services.PlaybackTarget != "mix" || App.Services.ActiveMixPlan is not { } plan || App.Services.Session.Project is not { } project) return;
        string root = App.Services.Session.RequireProjectRoot();
        string liveDraftText = GetEditorText();
        if (_segment is not null) _mixFollowDrafts[_segment.SegmentId] = new ProductionScriptParser().Parse(liveDraftText);
        string? liveDraftHash = _segment is null ? null : SourceTextHasher.Compute(new ProductionScriptParser().Parse(liveDraftText).NarrationText);
        foreach (MixRenderClip clip in plan.Clips.Where(clip => clip.Track.Kind == AudioTrackKind.Narration))
        {
            AudioAssetRecord asset = clip.Asset;
            var segment = project.Segments.FirstOrDefault(item => item.SegmentId == asset.SegmentId);
            var render = segment?.RenderHistory.FirstOrDefault(item => item.RenderId == asset.RenderId);
            if (render is null || render.SourceSha256 != segment!.SourceSha256
                || (segment.SegmentId == _segment?.SegmentId && liveDraftHash != render.SourceSha256)) continue;
            var record = render.AlignmentHistory.LastOrDefault(item => item.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial && !string.IsNullOrWhiteSpace(item.ArtifactRelativePath));
            if (record is null) continue;
            try
            {
                string path = VoiceBenchProjectLayout.ResolveRelativePath(root,asset.CleanSourceRelativePath ?? segment.ScriptRelativePath);
                string snapshot = await File.ReadAllTextAsync(path);
                if (!WordFollowPlaybackRules.IsNarrationCurrent(render.SourceSha256,new ProductionScriptParser().Parse(snapshot).NarrationText)) continue;
                string text = segment.SegmentId == _segment?.SegmentId ? liveDraftText : await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root,segment.ScriptRelativePath));
                var parsed = new ProductionScriptParser().Parse(text);
                var alignment = await FileWordAlignmentService.LoadDocumentAsync(root,record);
                if (version != _mixFollowLoadVersion) return;
                if (_mixFollowDrafts.TryGetValue(segment.SegmentId,out var latestDraft))
                {
                    if (!TimelinePresentation.CanFollow(render.SourceSha256,segment.SourceSha256,latestDraft)) continue;
                    text = latestDraft.AuthoringText; parsed = latestDraft;
                }
                if (!WordFollowPlaybackRules.IsNarrationCurrent(render.SourceSha256,parsed.NarrationText)
                    || alignment.SourceSha256 != render.SourceSha256 || alignment.AudioSha256 != record.AudioSha256) continue;
                MixRenderClip? activeClip = App.Services.ActiveMixPlan?.Clips.FirstOrDefault(item => item.ClipId == clip.ClipId);
                if (activeClip is not null) _mixFollowSources[clip.ClipId] = new(activeClip,text,parsed,alignment);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    private void RefreshMixFollowParameters()
    {
        if (App.Services.ActiveMixPlan is not { } plan) return;
        var clips = plan.Clips.ToDictionary(clip => clip.ClipId);
        foreach (var source in _mixFollowSources.ToArray())
            if (clips.TryGetValue(source.Key,out var clip)) _mixFollowSources[source.Key] = source.Value with { Clip = clip };
            else _mixFollowSources.Remove(source.Key);
        if (_mixFollowing && _mixFollowClipId is Guid active && _mixFollowSources.TryGetValue(active,out var current)
            && (current.Clip.Muted || current.Clip.Track.Muted))
        { PlaybackScriptText.TextHighlighters.Clear(); _activeFollowWordOrdinal = -1; }
    }

    private void SyncMixFollowDraft(ParsedScriptDocument draft)
    {
        if (_segment is null) return;
        _mixFollowDrafts[_segment.SegmentId] = draft;
        foreach (var entry in _mixFollowSources.Where(entry => entry.Value.Clip.Asset.SegmentId == _segment.SegmentId).ToArray())
        {
            if (!TimelinePresentation.CanFollow(entry.Value.Clip.Asset.SourceSha256,_segment.SourceSha256,draft))
                _mixFollowSources.Remove(entry.Key);
            else _mixFollowSources[entry.Key] = entry.Value with { Text = draft.AuthoringText, Parsed = draft };
            if (_mixFollowClipId == entry.Key) _mixFollowClipId = null;
        }
        if (_mixFollowing && !_mixFollowSources.Values.Any(source => !source.Clip.Muted && !source.Clip.Track.Muted && App.Services.AudioPlayback is { } playback && playback.Position.TotalSeconds >= source.Clip.StartSeconds && playback.Position.TotalSeconds < source.Clip.StartSeconds + source.Clip.DurationSeconds))
        { _mixFollowing = false; EndFollowSession(); }
    }

    private void InvalidateMixFollowForProject()
    {
        var project = App.Services.Session.Project;
        if (project is null || App.Services.ActiveMixPlan?.ProjectId != project.ProjectId)
        {
            _mixFollowSources.Clear(); _mixFollowDrafts.Clear(); ++_mixFollowLoadVersion;
            if (_mixFollowing) { _mixFollowing = false; EndFollowSession(); }
            return;
        }
        foreach (var source in _mixFollowSources.ToArray())
        {
            var segment = project.Segments.FirstOrDefault(item => item.SegmentId == source.Value.Clip.Asset.SegmentId);
            if (segment is null || !TimelinePresentation.CanFollow(source.Value.Clip.Asset.SourceSha256,segment.SourceSha256)) _mixFollowSources.Remove(source.Key);
        }
        if (_mixFollowing && _mixFollowClipId.HasValue && !_mixFollowSources.ContainsKey(_mixFollowClipId.Value))
        { _mixFollowClipId = null; _mixFollowing = false; EndFollowSession(); }
    }

    private void UpdateMixFollow()
    {
        if (App.Services.PlaybackTarget != "mix" || App.Services.AudioPlayback?.IsPlaying != true || FollowScriptToggle.IsChecked != true)
        {
            if (_mixFollowing) { _mixFollowing = false; EndFollowSession(); }
            return;
        }
        double seconds = App.Services.AudioPlayback.Position.TotalSeconds;
        MixFollowSource? source = _mixFollowSources.Values.FirstOrDefault(item => !item.Clip.Muted && !item.Clip.Track.Muted && seconds >= item.Clip.StartSeconds && seconds < item.Clip.StartSeconds + item.Clip.DurationSeconds);
        if (source is null)
        {
            if (_mixFollowing) { PlaybackScriptText.TextHighlighters.Clear(); _activeFollowWordOrdinal = -1; }
            return;
        }
        if (!_followSessionActive)
        {
            _cueFormatTimer?.Stop();
            _followSessionActive = true;
            var selection = ScriptTextBox.Document.Selection;
            _followEditorSnapshot = new(selection.StartPosition,selection.EndPosition,ScriptTextBox.IsReadOnly,ScriptTextBox.FocusState != FocusState.Unfocused);
            ScriptTextBox.Visibility = Visibility.Collapsed;
            PlaybackScriptSurface.Visibility = Visibility.Visible;
        }
        _mixFollowing = true;
        if (_mixFollowClipId != source.Clip.ClipId)
        {
            _mixFollowClipId = source.Clip.ClipId;
            PlaybackScriptText.Blocks.Clear();
            Paragraph paragraph = new(); paragraph.Inlines.Add(new Run { Text = source.Text }); PlaybackScriptText.Blocks.Add(paragraph);
            _activeFollowWordOrdinal = -1;
        }
        TimeSpan sourcePosition = TimeSpan.FromSeconds(seconds-source.Clip.StartSeconds+source.Clip.SourceStartSeconds);
        var timing = WordFollowPlaybackRules.FindActiveWord(source.Alignment.Words,sourcePosition);
        int ordinal = timing?.NarrationWordOrdinal ?? -1;
        if (ordinal == _activeFollowWordOrdinal) return;
        _activeFollowWordOrdinal = ordinal;
        PlaybackScriptText.TextHighlighters.Clear();
        if (ordinal < 0 || ordinal >= source.Parsed.NarrationWords.Count
            || (source.Clip.RetainedWordRanges.Count > 0 && !source.Clip.RetainedWordRanges.Any(range => ordinal >= range.FirstWordOrdinal && ordinal <= range.LastWordOrdinal))) return;
        NarrationWord word = source.Parsed.NarrationWords[ordinal];
        TextHighlighter highlight = new() { Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["ActiveSignalBrush"], Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["BaseBrush"] };
        highlight.Ranges.Add(new TextRange { StartIndex = word.AuthoringStart,Length = word.AuthoringLength });
        PlaybackScriptText.TextHighlighters.Add(highlight);
        ScrollPlaybackWord(word.AuthoringStart);
    }

    private void ScrollPlaybackWord(int offset)
    {
        var pointer = PlaybackScriptText.ContentStart?.GetPositionAtOffset(offset,LogicalDirection.Forward);
        if (pointer is null) return;
        var bounds = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (bounds.Top < PlaybackScriptScroll.VerticalOffset + 25 || bounds.Bottom > PlaybackScriptScroll.VerticalOffset + PlaybackScriptScroll.ViewportHeight - 25)
            PlaybackScriptScroll.ChangeView(null,Math.Max(0,bounds.Top-50),null,true);
    }
}
