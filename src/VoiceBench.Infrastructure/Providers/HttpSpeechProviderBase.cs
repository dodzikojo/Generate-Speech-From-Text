using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public abstract class HttpSpeechProviderBase : ISpeechProvider
{
    private const int MaximumRetries = 3;
    private readonly HttpClient _httpClient;
    private readonly IRetryDelayStrategy _retryDelay;

    protected HttpSpeechProviderBase(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _retryDelay = retryDelay ?? new JitterRetryDelayStrategy();
    }

    public abstract SpeechProviderDescriptor Descriptor { get; }

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request, apiKey);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var message = CreateRequest(request, apiKey);
                using var response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return await ParseSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                }

                if (IsRetryable(response.StatusCode) && attempt < MaximumRetries)
                {
                    await _retryDelay.DelayAsync(
                        response.Headers.RetryAfter,
                        attempt + 1,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaximumRetries)
            {
                await _retryDelay.DelayAsync(null, attempt + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (attempt < MaximumRetries)
            {
                await _retryDelay.DelayAsync(null, attempt + 1, cancellationToken).ConfigureAwait(false);
                _ = exception;
            }
            catch (HttpRequestException exception)
            {
                throw new SpeechProviderException(
                    Descriptor.ProviderId,
                    SpeechProviderFailureKind.Transient,
                    $"{Descriptor.DisplayName} could not be reached after bounded retries.",
                    innerException: exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SpeechProviderException(
                    Descriptor.ProviderId,
                    SpeechProviderFailureKind.Transient,
                    $"{Descriptor.DisplayName} timed out after bounded retries.");
            }
        }
    }

    protected abstract HttpRequestMessage CreateRequest(SpeechSynthesisRequest request, string apiKey);

    protected abstract Task<SpeechSynthesisResult> ParseSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken);

    protected virtual async Task<SpeechProviderException> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var policyFailure = IsPolicyFailure(body);
        var kind = Classify(response.StatusCode, policyFailure, body);
        var retryAfter = GetRetryAfter(response);

        return new SpeechProviderException(
            Descriptor.ProviderId,
            kind,
            SafeFailureMessage(kind),
            response.StatusCode,
            retryAfter);
    }

    protected static bool LooksLikeWave(ReadOnlySpan<byte> audio) =>
        audio.Length >= 12
        && audio[..4].SequenceEqual("RIFF"u8)
        && audio.Slice(8, 4).SequenceEqual("WAVE"u8);

    protected SpeechProviderException Malformed(string detail) => new(
        Descriptor.ProviderId,
        SpeechProviderFailureKind.MalformedResponse,
        $"{Descriptor.DisplayName} returned malformed audio: {detail}");

    protected virtual bool IsAuthenticationFailure(HttpStatusCode statusCode, string responseBody) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private void Validate(SpeechSynthesisRequest request, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Credentials,
                $"No {Descriptor.DisplayName} API key is configured.");
        }

        var model = Descriptor.Models.FirstOrDefault(
            item => string.Equals(item.ModelId, request.ModelId, StringComparison.Ordinal));

        if (model is null)
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                $"Model '{request.ModelId}' is not selectable for {Descriptor.DisplayName}.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                "Speech input cannot be empty.");
        }

        if (request.EstimatedInputTokens < 1 || request.EstimatedInputTokens > model.MaxInputTokens)
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                $"Estimated input is outside the {model.MaxInputTokens:N0}-token limit for '{model.ModelId}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Voice)
            || !model.Voices.Contains(request.Voice, StringComparer.OrdinalIgnoreCase))
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                $"Voice '{request.Voice}' is not selectable for '{model.ModelId}'.");
        }
    }

    private SpeechProviderFailureKind Classify(
        HttpStatusCode statusCode,
        bool policyFailure,
        string responseBody)
    {
        if (policyFailure)
        {
            return SpeechProviderFailureKind.Policy;
        }

        if (IsAuthenticationFailure(statusCode, responseBody))
        {
            return SpeechProviderFailureKind.Authentication;
        }

        return statusCode switch
        {
            HttpStatusCode.TooManyRequests => SpeechProviderFailureKind.RateLimited,
            HttpStatusCode.RequestTimeout => SpeechProviderFailureKind.Transient,
            >= HttpStatusCode.InternalServerError => SpeechProviderFailureKind.Transient,
            _ => SpeechProviderFailureKind.Validation,
        };
    }

    private string SafeFailureMessage(SpeechProviderFailureKind kind) => kind switch
    {
        SpeechProviderFailureKind.Authentication => $"{Descriptor.DisplayName} rejected the configured credential.",
        SpeechProviderFailureKind.Policy => $"{Descriptor.DisplayName} rejected the request under its content policy.",
        SpeechProviderFailureKind.RateLimited => $"{Descriptor.DisplayName} rate-limited the request after bounded retries.",
        SpeechProviderFailureKind.Transient => $"{Descriptor.DisplayName} failed after bounded retries.",
        _ => $"{Descriptor.DisplayName} rejected the speech request as invalid.",
    };

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || statusCode >= HttpStatusCode.InternalServerError;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsPolicyFailure(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ContainsPolicyMarker(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsPolicyMarker(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsPolicyMarker(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsPolicyMarker);

            case JsonValueKind.String:
                var value = element.GetString();
                return value is not null
                    && (value.Contains("policy", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("safety", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("prohibited_content", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("blocklist", StringComparison.OrdinalIgnoreCase));

            default:
                return false;
        }
    }
}
