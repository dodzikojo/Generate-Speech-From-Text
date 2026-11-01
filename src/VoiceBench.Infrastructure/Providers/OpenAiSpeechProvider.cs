using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class OpenAiSpeechProvider : HttpSpeechProviderBase
{
    private static readonly Uri SpeechEndpoint = new("https://api.openai.com/v1/audio/speech");
    // OpenAI publishes the voice names and recommends Marin and Cedar for best quality,
    // but does not publish per-voice adjectives. The remaining tags are VoiceBench
    // listening profiles intended to make auditioning and selection faster.
    private static readonly SpeechVoiceDescriptor[] Voices =
    [
        new("alloy", "Alloy", ["Neutral", "Balanced"]),
        new("ash", "Ash", ["Clear", "Conversational"]),
        new("ballad", "Ballad", ["Warm", "Emotive"]),
        new("coral", "Coral", ["Bright", "Friendly"]),
        new("echo", "Echo", ["Smooth", "Resonant"]),
        new("fable", "Fable", ["Expressive", "Narrative"]),
        new("nova", "Nova", ["Energetic", "Polished"]),
        new("onyx", "Onyx", ["Deep", "Authoritative"]),
        new("sage", "Sage", ["Calm", "Measured"]),
        new("shimmer", "Shimmer", ["Light", "Gentle"]),
        new("verse", "Verse", ["Natural", "Versatile"]),
        new("marin", "Marin", ["Natural", "Recommended"]),
        new("cedar", "Cedar", ["Clear", "Recommended"]),
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
