using System.Net;

namespace IssueLab.VoiceBench.Core;

public sealed record SpeechModelDescriptor(
    string ModelId,
    string DisplayName,
    int MaxInputTokens,
    IReadOnlyList<string> Voices);

public sealed record SpeechProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<SpeechModelDescriptor> Models);

public sealed record SpeechSynthesisRequest(
    string ModelId,
    string Voice,
    string Text,
    string? Directions,
    int EstimatedInputTokens);

public sealed record SpeechSynthesisResult(
    byte[] Audio,
    string MediaType,
    int SampleRate,
    int Channels,
    string? ProviderResponseId);

public enum SpeechProviderFailureKind
{
    Credentials,
    Authentication,
    Validation,
    Policy,
    RateLimited,
    Transient,
    MalformedResponse,
}

public sealed class SpeechProviderException : Exception
{
    public SpeechProviderException(
        string providerId,
        SpeechProviderFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderId = providerId;
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public string ProviderId { get; }

    public SpeechProviderFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}

public interface ISpeechProvider
{
    SpeechProviderDescriptor Descriptor { get; }

    Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        string apiKey,
        CancellationToken cancellationToken);
}

public interface ICredentialStore
{
    Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default);

    Task SetAsync(string providerId, string secret, CancellationToken cancellationToken = default);

    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}
