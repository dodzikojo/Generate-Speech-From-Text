using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

internal sealed class WordAlignmentHttpClient(
    HttpClient httpClient,
    IRetryDelayStrategy? retryDelay = null)
{
    private const int MaximumRetries = 3;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IRetryDelayStrategy _retryDelay = retryDelay ?? new JitterRetryDelayStrategy();

    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string providerId,
        string displayName,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpRequestMessage request = requestFactory();
                HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;

                if (IsRetryable(response.StatusCode) && attempt < MaximumRetries)
                {
                    await _retryDelay.DelayAsync(response.Headers.RetryAfter, attempt + 1, cancellationToken)
                        .ConfigureAwait(false);
                    response.Dispose();
                    continue;
                }

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                WordAlignmentProviderException failure = CreateFailure(
                    providerId,
                    displayName,
                    response.StatusCode,
                    body,
                    response.Headers.RetryAfter?.Delta);
                response.Dispose();
                throw failure;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaximumRetries)
            {
                await _retryDelay.DelayAsync(null, attempt + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaximumRetries)
            {
                await _retryDelay.DelayAsync(null, attempt + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new WordAlignmentProviderException(
                    providerId,
                    WordAlignmentFailureKind.Transient,
                    $"{displayName} transcription could not be reached after bounded retries.",
                    innerException: exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WordAlignmentProviderException(
                    providerId,
                    WordAlignmentFailureKind.Transient,
                    $"{displayName} transcription timed out after bounded retries.");
            }
        }
    }

    private static WordAlignmentProviderException CreateFailure(
        string providerId,
        string displayName,
        HttpStatusCode statusCode,
        string body,
        TimeSpan? retryAfter)
    {
        WordAlignmentFailureKind kind = ContainsPolicyMarker(body)
            ? WordAlignmentFailureKind.Policy
            : body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                || body.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase)
                || body.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
                ? WordAlignmentFailureKind.Authentication
            : statusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => WordAlignmentFailureKind.Authentication,
                HttpStatusCode.TooManyRequests => WordAlignmentFailureKind.RateLimited,
                HttpStatusCode.RequestTimeout => WordAlignmentFailureKind.Transient,
                >= HttpStatusCode.InternalServerError => WordAlignmentFailureKind.Transient,
                _ => WordAlignmentFailureKind.Validation,
            };
        string message = kind switch
        {
            WordAlignmentFailureKind.Authentication => $"{displayName} rejected the configured credential.",
            WordAlignmentFailureKind.Policy => $"{displayName} rejected the transcription under its content policy.",
            WordAlignmentFailureKind.RateLimited => $"{displayName} rate-limited transcription after bounded retries.",
            WordAlignmentFailureKind.Transient => $"{displayName} transcription failed after bounded retries.",
            _ => $"{displayName} rejected the transcription request as invalid.",
        };
        return new WordAlignmentProviderException(providerId, kind, message, statusCode, retryAfter);
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || statusCode >= HttpStatusCode.InternalServerError;

    private static bool ContainsPolicyMarker(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ContainsPolicyMarker(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsPolicyMarker(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsPolicyMarker(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsPolicyMarker),
        JsonValueKind.String => element.GetString() is string value
            && (value.Contains("policy", StringComparison.OrdinalIgnoreCase)
                || value.Contains("safety", StringComparison.OrdinalIgnoreCase)
                || value.Contains("prohibited_content", StringComparison.OrdinalIgnoreCase)
                || value.Contains("blocklist", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
