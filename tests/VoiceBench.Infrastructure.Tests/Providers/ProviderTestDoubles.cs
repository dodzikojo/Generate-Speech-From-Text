using System.Net;
using System.Net.Http.Headers;
using System.Buffers.Binary;
using System.Text;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? GoogleApiKey,
    string Body);

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = [];

    public List<CapturedRequest> Requests { get; } = [];

    public int SendCount => Requests.Count;

    public void Enqueue(HttpStatusCode statusCode, string body, string mediaType = "application/json") =>
        _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        }));

    public void Enqueue(HttpResponseMessage response) =>
        _responses.Enqueue((_, _) => Task.FromResult(response));

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var googleApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values)
            ? values.Single()
            : null;
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            googleApiKey,
            body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No HTTP response was configured for this request.");
        }

        return await _responses.Dequeue()(request, cancellationToken);
    }
}

internal sealed class RecordingRetryDelay : IRetryDelayStrategy
{
    public List<(RetryConditionHeaderValue? RetryAfter, int RetryNumber)> Calls { get; } = [];

    public Task DelayAsync(
        RetryConditionHeaderValue? retryAfter,
        int retryNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((retryAfter, retryNumber));
        return Task.CompletedTask;
    }
}

internal static class ProviderTestAudio
{
    public static byte[] Wave()
    {
        const int dataLength = 4;
        var bytes = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 24_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 48_000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), dataLength);
        return bytes;
    }
}
