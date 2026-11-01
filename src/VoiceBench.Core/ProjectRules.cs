using System.Security.Cryptography;
using System.Text;

namespace IssueLab.VoiceBench.Core;

public static class SourceTextHasher
{
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

public static class SegmentStateRules
{
    public static SegmentDocument WithSourceHash(SegmentDocument segment, string sourceSha256)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        return string.Equals(segment.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
            ? segment
            : WithSourceHashes(segment, sourceSha256, sourceSha256);
    }

    public static SegmentDocument WithSourceHashes(
        SegmentDocument segment,
        string sourceSha256,
        string authoringSha256)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoringSha256);

        bool narrationUnchanged = string.Equals(segment.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase);
        bool authoringUnchanged = string.Equals(segment.AuthoringSha256, authoringSha256, StringComparison.OrdinalIgnoreCase);
        if (narrationUnchanged && authoringUnchanged)
        {
            return segment;
        }

        if (string.IsNullOrEmpty(segment.SourceSha256))
        {
            return segment with { SourceSha256 = sourceSha256, AuthoringSha256 = authoringSha256 };
        }

        if (narrationUnchanged)
        {
            return segment with { AuthoringSha256 = authoringSha256 };
        }

        var renders = segment.RenderHistory
            .Select(render => IsCurrent(render, sourceSha256)
                ? render
                : render.Status is RenderStatus.Generated or RenderStatus.Approved
                    ? render with
                    {
                        Status = RenderStatus.Stale,
                        ApprovedAt = null,
                        ApprovalSourceSha256 = null,
                    }
                    : render)
            .ToList();

        return segment with
        {
            SourceSha256 = sourceSha256,
            AuthoringSha256 = authoringSha256,
            Status = renders.Count == 0 ? SegmentStatus.Draft : SegmentStatus.Stale,
            ApprovedRenderId = null,
            RenderHistory = renders,
        };
    }

    public static SegmentDocument ApproveRender(
        SegmentDocument segment,
        Guid renderId,
        DateTimeOffset approvedAt,
        bool explicitlyApproveStaleSource = false)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var index = segment.RenderHistory.FindIndex(render => render.RenderId == renderId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Render '{renderId}' does not belong to segment '{segment.SegmentId}'.");
        }

        var selected = segment.RenderHistory[index];
        var isStale = !string.Equals(selected.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase);
        if (isStale && !explicitlyApproveStaleSource)
        {
            throw new InvalidOperationException("A stale render requires explicit approval against the current source.");
        }

        if (selected.Status is RenderStatus.Failed or RenderStatus.Cancelled)
        {
            throw new InvalidOperationException("A failed or cancelled render cannot be approved.");
        }

        var renders = segment.RenderHistory
            .Select(render => render.RenderId == renderId
                ? render with
                {
                    Status = RenderStatus.Approved,
                    ApprovedAt = approvedAt,
                    ApprovalSourceSha256 = segment.SourceSha256,
                }
                : render.Status == RenderStatus.Approved
                    ? render with
                    {
                        Status = RenderStatus.Generated,
                        ApprovedAt = null,
                        ApprovalSourceSha256 = null,
                    }
                    : render)
            .ToList();

        return segment with
        {
            Status = SegmentStatus.Approved,
            ApprovedRenderId = renderId,
            RenderHistory = renders,
        };
    }

    private static bool IsCurrent(RenderRecord render, string sourceSha256) =>
        string.Equals(render.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase);
}

public static class VoicePresetRules
{
    public static bool CanSelectAsProjectDefault(VoicePreset preset) =>
        !string.IsNullOrWhiteSpace(preset.ProviderId)
        && !string.IsNullOrWhiteSpace(preset.ModelId)
        && !string.IsNullOrWhiteSpace(preset.Voice);
}

public sealed record MasterEligibility(bool CanBuild, IReadOnlyList<ProjectValidationIssue> Issues);

public static class MasterEligibilityEvaluator
{
    public static MasterEligibility Evaluate(
        VoiceBenchProjectDocument project,
        MasterScope scope = MasterScope.Compilation,
        string? chapterId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var byId = project.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var issues = new List<ProjectValidationIssue>();
        IReadOnlyList<string> orderedSegmentIds;
        try
        {
            orderedSegmentIds = ProjectOrdering.GetOrderedSegmentIds(project, scope, chapterId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            issues.Add(new ProjectValidationIssue("master.chapter-invalid", exception.Message));
            return new MasterEligibility(false, issues);
        }

        if (orderedSegmentIds.Count == 0)
        {
            issues.Add(new ProjectValidationIssue(
                "master.no-segments",
                scope == MasterScope.Chapter
                    ? "The chapter has no ordered segments."
                    : "The compilation has no included, ordered segments."));
        }

        foreach (var segmentId in orderedSegmentIds)
        {
            if (!byId.TryGetValue(segmentId, out var segment))
            {
                issues.Add(new ProjectValidationIssue(
                    "master.segment-missing",
                    $"Ordered segment '{segmentId}' is missing from the manifest.",
                    segmentId));
                continue;
            }

            var approved = segment.ApprovedRenderId is Guid renderId
                ? segment.RenderHistory.SingleOrDefault(render => render.RenderId == renderId)
                : null;
            if (segment.Status != SegmentStatus.Approved || approved is null)
            {
                issues.Add(new ProjectValidationIssue(
                    "master.segment-unapproved",
                    $"Segment '{segment.Title}' has no approved render.",
                    segmentId));
                continue;
            }

            if (approved.Status != RenderStatus.Approved ||
                !string.Equals(approved.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    approved.ApprovalSourceSha256,
                    segment.SourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ProjectValidationIssue(
                    "master.segment-stale",
                    $"Segment '{segment.Title}' is not approved against its current source.",
                    segmentId));
            }

            if (string.IsNullOrWhiteSpace(approved.ProcessedAudioRelativePath))
            {
                issues.Add(new ProjectValidationIssue(
                    "master.processed-audio-missing",
                    $"Segment '{segment.Title}' has no processed audio.",
                    segmentId));
            }
        }

        return new MasterEligibility(issues.Count == 0, issues);
    }
}
