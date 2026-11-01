using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class OpenAiWordAlignmentProvider : IWordAlignmentProvider
{
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/transcriptions");
    private readonly WordAlignmentHttpClient _client;

    public OpenAiWordAlignmentProvider(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
    {
        _client = new WordAlignmentHttpClient(httpClient, retryDelay);
    }

    public string ProviderId => "openai";

    public string ModelId => "whisper-1";

    public async Task<WordAlignmentProviderResult> TranscribeAsync(
        WordAlignmentProviderRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        Validate(request, apiKey);
        using HttpResponseMessage response = await _client.SendAsync(
            () => CreateRequest(request, apiKey),
            ProviderId,
            "OpenAI",
            cancellationToken).ConfigureAwait(false);

        OpenAiTranscription? payload;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            payload = await JsonSerializer.DeserializeAsync<OpenAiTranscription>(
                stream,
                ProjectJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw Malformed("the response was not valid JSON", exception);
        }

        if (payload?.Words is not { Count: > 0 }) throw Malformed("the response contained no word timestamps");
        List<RecognizedWordTiming> rawWords = [];
        foreach (OpenAiWord word in payload.Words)
        {
            if (string.IsNullOrWhiteSpace(word.Word)
                || word.Start is not double start
                || word.End is not double end
                || start < 0
                || end < start)
                continue;
            rawWords.Add(new RecognizedWordTiming(
                word.Word.Trim(),
                TimeSpan.FromSeconds(start),
                TimeSpan.FromSeconds(end)));
        }

        IReadOnlyList<RecognizedWordTiming> words = RecognizedWordTimingNormalizer.Normalize(rawWords);
        if (words.Count == 0) throw Malformed("the response contained no usable word timestamps");

        string? responseId = response.Headers.TryGetValues("x-request-id", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;
        double duration = Math.Max(payload.Duration ?? 0, words.Max(word => word.End.TotalSeconds));
        return new WordAlignmentProviderResult(words, TimeSpan.FromSeconds(duration), responseId);
    }

    private HttpRequestMessage CreateRequest(WordAlignmentProviderRequest request, string apiKey)
    {
        HttpRequestMessage message = new(HttpMethod.Post, Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        MultipartFormDataContent content = new();
        ByteArrayContent audio = new(request.Audio);
        audio.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        content.Add(audio, "file", request.FileName);
        content.Add(new StringContent(ModelId), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("word"), "timestamp_granularities[]");
        message.Content = content;
        return message;
    }

    private void Validate(WordAlignmentProviderRequest request, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new WordAlignmentProviderException(ProviderId, WordAlignmentFailureKind.Credentials, "No OpenAI API key is configured.");
        if (request.Audio.Length == 0)
            throw new WordAlignmentProviderException(ProviderId, WordAlignmentFailureKind.Validation, "Audio cannot be empty.");
    }

    private WordAlignmentProviderException Malformed(string detail, Exception? inner = null) => new(
        ProviderId,
        WordAlignmentFailureKind.MalformedResponse,
        $"OpenAI transcription returned malformed timestamps: {detail}.",
        innerException: inner);

    private sealed record OpenAiTranscription(double? Duration, IReadOnlyList<OpenAiWord>? Words);
    private sealed record OpenAiWord(string? Word, double? Start, double? End);
}
