using System.Net.Http.Headers;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

/// <summary>Controls provider retry delays and provides a deterministic test seam.</summary>
public interface IRetryDelayStrategy
{
    Task DelayAsync(RetryConditionHeaderValue? retryAfter, int retryNumber, CancellationToken cancellationToken);
}
