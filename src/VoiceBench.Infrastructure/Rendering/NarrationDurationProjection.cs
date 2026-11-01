using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Rendering;

public sealed record SegmentDurationProjection(TimeSpan Duration, bool IsMeasured);

public sealed record DurationProjectionSummary(
    TimeSpan Duration,
    int MeasuredCount,
    int EstimatedCount);

public static class NarrationDurationProjection
{
    public static TimeSpan EstimateAuthoringText(string authoringText)
    {
        ArgumentNullException.ThrowIfNull(authoringText);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(authoringText);
        return SegmentRenderService.EstimateDuration(parsed.NarrationText);
    }

    public static async Task<IReadOnlyDictionary<string, TimeSpan>> LoadEstimatesAsync(
        string projectRoot,
        IEnumerable<SegmentDocument> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(segments);
        Dictionary<string, TimeSpan> estimates = new(StringComparer.Ordinal);
        foreach (SegmentDocument segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string path = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath);
                string authoringText = await File.ReadAllTextAsync(path, cancellationToken);
                estimates[segment.SegmentId] = EstimateAuthoringText(authoringText);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                estimates[segment.SegmentId] = TimeSpan.Zero;
            }
        }

        return estimates;
    }

    public static SegmentDurationProjection Resolve(SegmentDocument segment, TimeSpan estimatedDuration)
    {
        ArgumentNullException.ThrowIfNull(segment);
        RenderRecord? currentTake = segment.RenderHistory
            .Where(render => render.Duration > TimeSpan.Zero &&
                string.Equals(render.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(render => render.TakeNumber)
            .FirstOrDefault();
        return currentTake is null
            ? new SegmentDurationProjection(estimatedDuration, false)
            : new SegmentDurationProjection(currentTake.Duration, true);
    }

    public static DurationProjectionSummary Summarize(
        IReadOnlyList<SegmentDocument> segments,
        IReadOnlyDictionary<string, TimeSpan> estimates,
        int pauseMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(estimates);
        TimeSpan duration = TimeSpan.Zero;
        int measured = 0;
        int estimated = 0;
        foreach (SegmentDocument segment in segments)
        {
            estimates.TryGetValue(segment.SegmentId, out TimeSpan estimate);
            SegmentDurationProjection projection = Resolve(segment, estimate);
            duration += projection.Duration;
            if (projection.IsMeasured) measured++;
            else estimated++;
        }

        if (segments.Count > 1)
        {
            duration += TimeSpan.FromMilliseconds(pauseMilliseconds * (segments.Count - 1));
        }

        return new DurationProjectionSummary(duration, measured, estimated);
    }
}
