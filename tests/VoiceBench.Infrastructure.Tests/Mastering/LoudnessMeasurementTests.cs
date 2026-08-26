using IssueLab.VoiceBench.Infrastructure.Mastering;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Mastering;

public sealed class LoudnessMeasurementTests
{
    [Fact]
    public void Parse_RepresentsFfmpegNegativeInfinityAsUnmeasurable()
    {
        LoudnessMeasurement result = LoudnessMeasurement.Parse(
            "{\"input_i\":\"-inf\",\"input_tp\":\"-10.2\",\"input_lra\":\"0.0\",\"input_thresh\":\"-70.0\",\"target_offset\":\"inf\"}");

        Assert.False(result.IsMeasurable);
        Assert.True(double.IsNegativeInfinity(result.IntegratedLufs));
        Assert.True(double.IsPositiveInfinity(result.TargetOffset));
    }
}
