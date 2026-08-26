namespace IssueLab.VoiceBench.Infrastructure.Tests.Rendering;

internal sealed class FixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
}
