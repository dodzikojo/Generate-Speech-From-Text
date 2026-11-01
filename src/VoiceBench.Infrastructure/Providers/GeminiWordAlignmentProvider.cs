using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class GeminiWordAlignmentProvider : IWordAlignmentProvider
{
    private static readonly Uri StartUploadEndpoint = new("https://generativelanguage.googleapis.com/upload/v1beta/files");
    private static readonly Uri InteractionEndpoint = new("https://generativelanguage.googleapis.com/v1beta/interactions");
    private readonly HttpClient _httpClient;
    private readonly WordAlignmentHttpClient _client;

    public GeminiWordAlignmentProvider(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _client = new WordAlignmentHttpClient(httpClient, retryDelay);
    }

    public string ProviderId => "gemini";

    public string ModelId => "gemini-3.5-transcribe";

    public async Task<WordAlignmentProviderResult> TranscribeAsync(
        WordAlignmentProviderRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        Validate(request, apiKey);
        GeminiFile? uploaded = null;
        try
        {
            Uri uploadUri = await StartUploadAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
            uploaded = await UploadAsync(uploadUri, request, apiKey, cancellationToken).ConfigureAwait(false);
            return await CreateInteractionAsync(uploaded, request, apiKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(uploaded?.Name))
            {
                await DeleteBestEffortAsync(uploaded.Name, apiKey, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Uri> StartUploadAsync(
        WordAlignmentProviderRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.SendAsync(
            () =>
            {
                HttpRequestMessage message = new(HttpMethod.Post, StartUploadEndpoint);
                message.Headers.Add("x-goog-api-key", apiKey);
                message.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                message.Headers.Add("X-Goog-Upload-Command", "start");
                message.Headers.Add("X-Goog-Upload-Header-Content-Length", request.Audio.Length.ToString(CultureInfo.InvariantCulture));
                message.Headers.Add("X-Goog-Upload-Header-Content-Type", request.MediaType);
                message.Content = JsonContent.Create(new GeminiFileMetadata(new GeminiDisplayFile(request.FileName)), options: JsonOptions);
                return message;
            },
            ProviderId,
            "Google Gemini",
            cancellationToken).ConfigureAwait(false);
        if (!response.Headers.TryGetValues("X-Goog-Upload-URL", out IEnumerable<string>? values)
            || !Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out Uri? uploadUri))
        {
            throw Malformed("the Files API did not return an upload URL");
        }

        return uploadUri;
    }

    private async Task<GeminiFile> UploadAsync(
        Uri uploadUri,
        WordAlignmentProviderRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.SendAsync(
            () =>
            {
                HttpRequestMessage message = new(HttpMethod.Post, uploadUri);
                message.Headers.Add("x-goog-api-key", apiKey);
                message.Headers.Add("X-Goog-Upload-Offset", "0");
                message.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
                ByteArrayContent content = new(request.Audio);
                content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
                message.Content = content;
                return message;
            },
            ProviderId,
            "Google Gemini",
            cancellationToken).ConfigureAwait(false);
        GeminiFileResponse? payload = await ReadJsonAsync<GeminiFileResponse>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload?.File?.Uri) || string.IsNullOrWhiteSpace(payload.File.Name))
            throw Malformed("the Files API response contained no file URI");
        return payload.File with { MimeType = payload.File.MimeType ?? request.MediaType };
    }

    private async Task<WordAlignmentProviderResult> CreateInteractionAsync(
        GeminiFile file,
        WordAlignmentProviderRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.SendAsync(
            () =>
            {
                HttpRequestMessage message = new(HttpMethod.Post, InteractionEndpoint);
                message.Headers.Add("x-goog-api-key", apiKey);
                message.Content = JsonContent.Create(
                    new GeminiInteractionRequest(
                        ModelId,
                        [new GeminiAudioInput("audio", file.Uri!, file.MimeType ?? request.MediaType)],
                        new GeminiGenerationConfig(
                            new GeminiTranscriptionConfig(new GeminiTranscriptionMode("verbatim", ["word"])))),
                    options: JsonOptions);
                return message;
            },
            ProviderId,
            "Google Gemini",
            cancellationToken).ConfigureAwait(false);
        GeminiInteractionResponse? payload = await ReadJsonAsync<GeminiInteractionResponse>(response, cancellationToken).ConfigureAwait(false);
        List<RecognizedWordTiming> rawWords = [];
        foreach (GeminiStep step in payload?.Steps ?? [])
        foreach (GeminiContent content in step.Content ?? [])
        foreach (GeminiAnnotation annotation in content.Annotations ?? [])
        {
            if (!string.Equals(annotation.Type, "word_info", StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(annotation.Text)
                || !TryParseOffset(annotation.StartOffset, out TimeSpan start)
                || !TryParseOffset(annotation.EndOffset, out TimeSpan end)
                || end < start)
                continue;
            rawWords.Add(new RecognizedWordTiming(annotation.Text.Trim(), start, end));
        }

        IReadOnlyList<RecognizedWordTiming> words = RecognizedWordTimingNormalizer.Normalize(rawWords);
        if (words.Count == 0) throw Malformed("the response contained no word annotations");
        return new WordAlignmentProviderResult(words, words.Max(word => word.End), payload?.Id);
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw Malformed("the response was not valid JSON", exception);
        }
    }

    private async Task DeleteBestEffortAsync(string name, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cleanup.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpRequestMessage message = new(
                HttpMethod.Delete,
                new Uri($"https://generativelanguage.googleapis.com/v1beta/{name.TrimStart('/')}"));
            message.Headers.Add("x-goog-api-key", apiKey);
            using HttpResponseMessage response = await _httpClient.SendAsync(message, cleanup.Token).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup must never turn a completed transcription into a failed take.
        }
    }

    private static bool TryParseOffset(string? value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('s')) return false;
        return double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && seconds >= 0
            && (offset = TimeSpan.FromSeconds(seconds)) >= TimeSpan.Zero;
    }

    private void Validate(WordAlignmentProviderRequest request, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new WordAlignmentProviderException(ProviderId, WordAlignmentFailureKind.Credentials, "No Google Gemini API key is configured.");
        if (request.Audio.Length == 0)
            throw new WordAlignmentProviderException(ProviderId, WordAlignmentFailureKind.Validation, "Audio cannot be empty.");
    }

    private WordAlignmentProviderException Malformed(string detail, Exception? inner = null) => new(
        ProviderId,
        WordAlignmentFailureKind.MalformedResponse,
        $"Google Gemini transcription returned malformed timestamps: {detail}.",
        innerException: inner);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record GeminiFileMetadata(GeminiDisplayFile File);
    private sealed record GeminiDisplayFile([property: JsonPropertyName("display_name")] string DisplayName);
    private sealed record GeminiFileResponse(GeminiFile? File);
    private sealed record GeminiFile(string? Name, string? Uri, [property: JsonPropertyName("mime_type")] string? MimeType);
    private sealed record GeminiInteractionRequest(
        string Model,
        IReadOnlyList<GeminiAudioInput> Input,
        [property: JsonPropertyName("generation_config")] GeminiGenerationConfig GenerationConfig);
    private sealed record GeminiAudioInput(
        string Type,
        string Uri,
        [property: JsonPropertyName("mime_type")] string MimeType);
    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("transcription_config")] GeminiTranscriptionConfig TranscriptionConfig);
    private sealed record GeminiTranscriptionConfig(GeminiTranscriptionMode Mode);
    private sealed record GeminiTranscriptionMode(
        string Type,
        [property: JsonPropertyName("timestamp_granularities")] IReadOnlyList<string> TimestampGranularities);
    private sealed record GeminiInteractionResponse(string? Id, IReadOnlyList<GeminiStep>? Steps);
    private sealed record GeminiStep(IReadOnlyList<GeminiContent>? Content);
    private sealed record GeminiContent(IReadOnlyList<GeminiAnnotation>? Annotations);
    private sealed record GeminiAnnotation(
        string? Type,
        string? Text,
        [property: JsonPropertyName("start_offset")] string? StartOffset,
        [property: JsonPropertyName("end_offset")] string? EndOffset);
}
