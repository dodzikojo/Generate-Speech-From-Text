using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class WorkbenchView
{
    private readonly DispatcherTimer _mixFollowTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<Guid,MixFollowSource> _mixFollowSources = [];
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
        string? liveDraftHash = _segment is null ? null : SourceTextHasher.Compute(new ProductionScriptParser().Parse(liveDraftText).NarrationText);
        foreach (MixRenderClip clip in plan.Clips.Where(clip => clip.Track.Kind == AudioTrackKind.Narration && !clip.Muted && !clip.Track.Muted))
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
                if (!WordFollowPlaybackRules.IsNarrationCurrent(render.SourceSha256,parsed.NarrationText)
                    || alignment.SourceSha256 != render.SourceSha256 || alignment.AudioSha256 != record.AudioSha256) continue;
                _mixFollowSources[clip.ClipId] = new(clip,text,parsed,alignment);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
    }

    private void UpdateMixFollow()
    {
        if (App.Services.PlaybackTarget != "mix" || App.Services.AudioPlayback?.IsPlaying != true || FollowScriptToggle.IsChecked != true)
        {
            if (_mixFollowing) { _mixFollowing = false; EndFollowSession(); }
            return;
        }
        double seconds = App.Services.AudioPlayback.Position.TotalSeconds;
        MixFollowSource? source = _mixFollowSources.Values.FirstOrDefault(item => seconds >= item.Clip.StartSeconds && seconds < item.Clip.StartSeconds + item.Clip.DurationSeconds);
        if (source is null)
        {
            if (_mixFollowing) PlaybackScriptText.TextHighlighters.Clear();
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
