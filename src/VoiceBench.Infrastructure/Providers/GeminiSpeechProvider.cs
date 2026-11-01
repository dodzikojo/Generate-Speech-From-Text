using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed partial class GeminiSpeechProvider : HttpSpeechProviderBase
{
    private static readonly SpeechVoiceDescriptor[] Voices =
    [
        new("Achernar", "Achernar", ["Soft"]),
        new("Achird", "Achird", ["Friendly"]),
        new("Algenib", "Algenib", ["Gravelly"]),
        new("Algieba", "Algieba", ["Smooth"]),
        new("Alnilam", "Alnilam", ["Firm"]),
        new("Aoede", "Aoede", ["Breezy"]),
        new("Autonoe", "Autonoe", ["Bright"]),
        new("Callirrhoe", "Callirrhoe", ["Easy-going"]),
        new("Charon", "Charon", ["Informative"]),
        new("Despina", "Despina", ["Smooth"]),
        new("Enceladus", "Enceladus", ["Breathy"]),
        new("Erinome", "Erinome", ["Clear"]),
        new("Fenrir", "Fenrir", ["Excitable"]),
        new("Gacrux", "Gacrux", ["Mature"]),
        new("Iapetus", "Iapetus", ["Clear"]),
        new("Kore", "Kore", ["Firm"]),
        new("Laomedeia", "Laomedeia", ["Upbeat"]),
        new("Leda", "Leda", ["Youthful"]),
        new("Orus", "Orus", ["Firm"]),
        new("Puck", "Puck", ["Upbeat"]),
        new("Pulcherrima", "Pulcherrima", ["Forward"]),
        new("Rasalgethi", "Rasalgethi", ["Informative"]),
        new("Sadachbia", "Sadachbia", ["Lively"]),
        new("Sadaltager", "Sadaltager", ["Knowledgeable"]),
        new("Schedar", "Schedar", ["Even"]),
        new("Sulafat", "Sulafat", ["Warm"]),
        new("Umbriel", "Umbriel", ["Easy-going"]),
        new("Vindemiatrix", "Vindemiatrix", ["Gentle"]),
        new("Zephyr", "Zephyr", ["Bright"]),
        new("Zubenelgenubi", "Zubenelgenubi", ["Casual"]),
    ];

    private static readonly SpeechProviderDescriptor ProviderDescriptor = new(
        "gemini",
        "Google Gemini",
        [
            new SpeechModelDescriptor("gemini-3.1-flash-tts-preview", "Gemini 3.1 Flash TTS Preview", 8_192, Voices),
            new SpeechModelDescriptor("gemini-2.5-flash-preview-tts", "Gemini 2.5 Flash TTS Preview", 8_192, Voices),
            new SpeechModelDescriptor("gemini-2.5-pro-preview-tts", "Gemini 2.5 Pro TTS Preview", 8_192, Voices),
        ]);

    public GeminiSpeechProvider(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
        : base(httpClient, retryDelay)
    {
    }

    public override SpeechProviderDescriptor Descriptor => ProviderDescriptor;

    protected override HttpRequestMessage CreateRequest(SpeechSynthesisRequest request, string apiKey)
    {
        var endpoint = new Uri(
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(request.ModelId)}:generateContent");
        var submittedText = string.IsNullOrWhiteSpace(request.Directions)
            ? request.Text
            : $"{request.Directions.Trim()}\n\n{request.Text}";

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Add("x-goog-api-key", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = JsonContent.Create(
            new GeminiRequest(
                [new GeminiContent([new GeminiTextPart(submittedText)])],
                new GeminiGenerationConfig(
                    ["AUDIO"],
                    new GeminiSpeechConfig(new GeminiVoiceConfig(new GeminiPrebuiltVoiceConfig(request.Voice))))),
            options: JsonOptions);
        return message;
    }

    protected override async Task<SpeechSynthesisResult> ParseSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        GeminiResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<GeminiResponse>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw Malformed($"invalid JSON ({exception.GetType().Name})");
        }

        if (!string.IsNullOrWhiteSpace(payload?.PromptFeedback?.BlockReason))
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Policy,
                "Google Gemini rejected the request under its content policy.");
        }

        var candidate = payload?.Candidates?.FirstOrDefault();
        if (candidate is not null && IsPolicyFinishReason(candidate.FinishReason))
        {
            throw new SpeechProviderException(
                Descriptor.ProviderId,
                SpeechProviderFailureKind.Policy,
                "Google Gemini rejected the request under its content policy.");
        }

        var part = candidate?.Content?.Parts?.FirstOrDefault(item => item.InlineData is not null);
        if (part?.InlineData?.Data is not { Length: > 0 } encodedAudio)
        {
            throw Malformed("the response did not contain an audio part");
        }

        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(encodedAudio);
        }
        catch (FormatException)
        {
            throw Malformed("the audio part was not valid base64");
        }

        if (pcm.Length == 0)
        {
            throw Malformed("the audio part was empty");
        }

        var sampleRate = ParseSampleRate(part.InlineData.MimeType) ?? 24_000;
        var wave = PcmWaveEncoder.Encode16BitMono(pcm, sampleRate);
        return new SpeechSynthesisResult(wave, "audio/wav", sampleRate, 1, payload?.ResponseId);
    }

    protected override bool IsAuthenticationFailure(HttpStatusCode statusCode, string responseBody) =>
        base.IsAuthenticationFailure(statusCode, responseBody)
        || responseBody.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
        || responseBody.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase);

    private static int? ParseSampleRate(string? mimeType)
    {
        var match = SampleRatePattern().Match(mimeType ?? string.Empty);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
            ? rate
            : null;
    }

    private static bool IsPolicyFinishReason(string? finishReason) => finishReason is not null
        && (finishReason.Equals("SAFETY", StringComparison.OrdinalIgnoreCase)
            || finishReason.Equals("RECITATION", StringComparison.OrdinalIgnoreCase)
            || finishReason.Equals("BLOCKLIST", StringComparison.OrdinalIgnoreCase)
            || finishReason.Equals("PROHIBITED_CONTENT", StringComparison.OrdinalIgnoreCase)
            || finishReason.Equals("SPII", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?:^|;)\s*rate=(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleRatePattern();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record GeminiRequest(IReadOnlyList<GeminiContent> Contents, GeminiGenerationConfig GenerationConfig);
    private sealed record GeminiContent(IReadOnlyList<GeminiTextPart> Parts);
    private sealed record GeminiTextPart(string Text);
    private sealed record GeminiGenerationConfig(IReadOnlyList<string> ResponseModalities, GeminiSpeechConfig SpeechConfig);
    private sealed record GeminiSpeechConfig(GeminiVoiceConfig VoiceConfig);
    private sealed record GeminiVoiceConfig(GeminiPrebuiltVoiceConfig PrebuiltVoiceConfig);
    private sealed record GeminiPrebuiltVoiceConfig(string VoiceName);

    private sealed record GeminiResponse(
        IReadOnlyList<GeminiCandidate>? Candidates,
        GeminiPromptFeedback? PromptFeedback,
        string? ResponseId);
    private sealed record GeminiCandidate(GeminiResponseContent? Content, string? FinishReason);
    private sealed record GeminiResponseContent(IReadOnlyList<GeminiResponsePart>? Parts);
    private sealed record GeminiResponsePart(GeminiInlineData? InlineData, string? Text);
    private sealed record GeminiInlineData(string? MimeType, string? Data);
    private sealed record GeminiPromptFeedback(string? BlockReason);
}
