using System.Net.Http.Headers;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class JitterRetryDelayStrategy : IRetryDelayStrategy
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, int> _nextJitterMilliseconds;

    public JitterRetryDelayStrategy(
        TimeProvider? timeProvider = null,
        Func<int, int>? nextJitterMilliseconds = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextJitterMilliseconds = nextJitterMilliseconds ?? Random.Shared.Next;
    }

    public Task DelayAsync(
        RetryConditionHeaderValue? retryAfter,
        int retryNumber,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var advertisedDelay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } retryDate ? retryDate - now : TimeSpan.Zero);

        if (advertisedDelay < TimeSpan.Zero)
        {
            advertisedDelay = TimeSpan.Zero;
        }

        var exponentialDelay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, retryNumber - 1));
        var jitter = TimeSpan.FromMilliseconds(_nextJitterMilliseconds(251));
        var delay = (advertisedDelay > exponentialDelay ? advertisedDelay : exponentialDelay) + jitter;

        return Task.Delay(delay, _timeProvider, cancellationToken);
    }
}
