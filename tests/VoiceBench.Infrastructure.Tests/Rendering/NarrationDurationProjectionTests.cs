using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Rendering;

public sealed class NarrationDurationProjectionTests
{
    [Fact]
    public void EstimateAuthoringText_ExcludesProductionCues()
    {
        string narration = string.Join(' ', Enumerable.Repeat("spoken", 150));
        string authoring = $"@visual: Show this for several minutes.\n\n{narration}\n\n@action: Click save now.";

        TimeSpan estimate = NarrationDurationProjection.EstimateAuthoringText(authoring);

        Assert.Equal(TimeSpan.FromMinutes(1), estimate);
    }

    [Fact]
    public void Resolve_UsesLatestCurrentTakeAndRejectsStaleMeasurements()
    {
        SegmentDocument current = new()
        {
            SegmentId = "segment",
            SourceSha256 = "current",
            RenderHistory =
            [
                new RenderRecord { TakeNumber = 1, SourceSha256 = "old", Duration = TimeSpan.FromSeconds(40) },
                new RenderRecord { TakeNumber = 2, SourceSha256 = "current", Duration = TimeSpan.FromSeconds(32) },
            ],
        };

        SegmentDurationProjection measured = NarrationDurationProjection.Resolve(current, TimeSpan.FromSeconds(30));
        SegmentDurationProjection estimated = NarrationDurationProjection.Resolve(
            current with { SourceSha256 = "newer" },
            TimeSpan.FromSeconds(29));

        Assert.True(measured.IsMeasured);
        Assert.Equal(TimeSpan.FromSeconds(32), measured.Duration);
        Assert.False(estimated.IsMeasured);
        Assert.Equal(TimeSpan.FromSeconds(29), estimated.Duration);
    }

    [Fact]
    public void Summarize_AddsPausesAndReportsDurationBasis()
    {
        SegmentDocument measured = new()
        {
            SegmentId = "measured",
            SourceSha256 = "same",
            RenderHistory = [new RenderRecord { TakeNumber = 1, SourceSha256 = "same", Duration = TimeSpan.FromSeconds(20) }],
        };
        SegmentDocument estimated = new() { SegmentId = "estimated", SourceSha256 = "draft" };
        Dictionary<string, TimeSpan> estimates = new()
        {
            ["measured"] = TimeSpan.FromSeconds(18),
            ["estimated"] = TimeSpan.FromSeconds(30),
        };

        DurationProjectionSummary summary = NarrationDurationProjection.Summarize(
            [measured, estimated],
            estimates,
            pauseMilliseconds: 500);

        Assert.Equal(TimeSpan.FromSeconds(50.5), summary.Duration);
        Assert.Equal(1, summary.MeasuredCount);
        Assert.Equal(1, summary.EstimatedCount);
    }
}
