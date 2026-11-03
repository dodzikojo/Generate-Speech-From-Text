using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>A take a chapter-rail row can play or approve, with the words its button and menu item use.</summary>
public sealed record RailTakeChoice(Guid RenderId, int TakeNumber, string Description)
{
    public string Title => $"Take {TakeNumber:00}";
}

/// <summary>
/// Decides what the hover buttons on a chapter-rail row act on, so a row can be reviewed without
/// opening the Takes tab: play the take that needs listening to, then approve it.
/// </summary>
public static class RailRowActions
{
    /// <summary>
    /// The take a reviewer approves next: the newest generated take made from the current script.
    /// None while the segment renders, so an approval never races the take being written.
    /// </summary>
    public static RailTakeChoice? TakeToApprove(SegmentDocument segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Status == SegmentStatus.Rendering ? null : NewestAwaitingReview(segment);
    }

    /// <summary>
    /// The take hover Play plays: the one awaiting review, else the approved take, else the newest
    /// take that still has audio. Failed and cancelled attempts have nothing to play.
    /// </summary>
    public static RailTakeChoice? TakeToPlay(SegmentDocument segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (NewestAwaitingReview(segment) is { } review) return review;

        RenderRecord? approved = segment.RenderHistory.FirstOrDefault(take =>
            take.RenderId == segment.ApprovedRenderId && take.Status == RenderStatus.Approved && HasAudio(take));
        if (approved is not null)
            return new(approved.RenderId, approved.TakeNumber, MatchesSource(segment, approved) ? "approved" : "approved, script changed since");

        RenderRecord? newest = segment.RenderHistory
            .Where(take => take.Status is RenderStatus.Generated or RenderStatus.Approved or RenderStatus.Stale && HasAudio(take))
            .OrderByDescending(take => take.TakeNumber)
            .FirstOrDefault();
        if (newest is null) return null;
        bool current = newest.Status != RenderStatus.Stale && MatchesSource(segment, newest);
        return new(newest.RenderId, newest.TakeNumber, current ? "latest take" : "script changed since");
    }

    /// <summary>Why Generate cannot start for this segment right now, or null when it can.</summary>
    public static string? GenerateBlockedReason(SegmentDocument segment, bool studioBusy)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Status == SegmentStatus.Rendering) return "Rendering now";
        return studioBusy ? "Wait for the current render to finish" : null;
    }

    /// <summary>Tooltip for a chapter row's Play button.</summary>
    public static string ChapterPlayLabel(ChapterPlaylist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        int count = playlist.Entries.Count;
        return count == 0
            ? "No approved takes to play yet"
            : $"Play chapter · {count} approved take{(count == 1 ? "" : "s")} · {TakeListPresentation.FormatDuration(playlist.TotalDuration)}";
    }

    private static RailTakeChoice? NewestAwaitingReview(SegmentDocument segment)
    {
        RenderRecord? render = segment.RenderHistory
            .Where(take => take.Status == RenderStatus.Generated && MatchesSource(segment, take) && HasAudio(take))
            .OrderByDescending(take => take.TakeNumber)
            .FirstOrDefault();
        if (render is null) return null;
        return new(render.RenderId, render.TakeNumber, render.Origin == RenderOrigin.Imported ? "imported, awaiting review" : "awaiting review");
    }

    private static bool HasAudio(RenderRecord take) => ProjectLocations.TakeAudioRelativePath(take) is not null;

    private static bool MatchesSource(SegmentDocument segment, RenderRecord take) =>
        string.IsNullOrEmpty(segment.SourceSha256)
        || string.Equals(take.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An approval asked for while the studio was busy; it runs as soon as the current take finishes.</summary>
public sealed record QueuedTakeApproval(string SegmentId, string SegmentTitle, Guid RenderId, int TakeNumber, bool ConfirmedTranscript);

/// <summary>
/// Approvals wait here while a render runs: approving processes the take with FFmpeg and saves the
/// project, which must not overlap the render. Each take is queued once, first asked first approved.
/// </summary>
public sealed class TakeApprovalQueue
{
    private readonly List<QueuedTakeApproval> _items = [];

    public int Count => _items.Count;

    public bool Contains(Guid renderId) => _items.Any(item => item.RenderId == renderId);

    /// <summary>Adds the approval; false when that take is already waiting.</summary>
    public bool Enqueue(QueuedTakeApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (Contains(approval.RenderId)) return false;
        _items.Add(approval);
        return true;
    }

    /// <summary>Forgets every waiting approval, for example when another project opens.</summary>
    public void Clear() => _items.Clear();

    public bool TryDequeue(out QueuedTakeApproval approval)
    {
        if (_items.Count == 0)
        {
            approval = null!;
            return false;
        }

        approval = _items[0];
        _items.RemoveAt(0);
        return true;
    }
}
