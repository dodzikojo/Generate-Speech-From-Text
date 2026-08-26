using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class OpenAiSpeechProvider : HttpSpeechProviderBase
{
    private static readonly Uri SpeechEndpoint = new("https://api.openai.com/v1/audio/speech");
    private static readonly string[] Voices =
    [
        "alloy", "ash", "ballad", "coral", "echo", "fable", "nova",
        "onyx", "sage", "shimmer", "verse", "cedar", "marin",
    ];

    private static readonly SpeechProviderDescriptor ProviderDescriptor = new(
        "openai",
        "OpenAI",
        [new SpeechModelDescriptor("gpt-4o-mini-tts", "GPT-4o mini TTS", 2_000, Voices)]);

    public OpenAiSpeechProvider(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
        : base(httpClient, retryDelay)
    {
    }

    public override SpeechProviderDescriptor Descriptor => ProviderDescriptor;

    protected override HttpRequestMessage CreateRequest(SpeechSynthesisRequest request, string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        message.Content = JsonContent.Create(new OpenAiSpeechRequest(
            request.ModelId,
            request.Text,
            request.Voice,
            request.Directions,
            "wav"),
            options: JsonOptions);
        return message;
    }

    protected override async Task<SpeechSynthesisResult> ParseSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        WaveFileMetadata metadata;
        try
        {
            metadata = WaveFileInspector.Read(audio);
        }
        catch (InvalidDataException)
        {
            throw Malformed("the response was not a complete RIFF/WAVE file");
        }

        var responseId = response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;

        return new SpeechSynthesisResult(
            audio,
            "audio/wav",
            metadata.SampleRate,
            metadata.Channels,
            responseId);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record OpenAiSpeechRequest(
        string Model,
        string Input,
        string Voice,
        string? Instructions,
        [property: JsonPropertyName("response_format")] string ResponseFormat);
}
