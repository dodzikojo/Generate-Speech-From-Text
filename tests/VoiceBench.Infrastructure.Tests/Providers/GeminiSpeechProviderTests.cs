using System.Net;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class GeminiSpeechProviderTests
{
    [Fact]
    public void Descriptor_exposes_all_thirty_documented_gemini_tts_voices_for_each_model()
    {
        var provider = new GeminiSpeechProvider(new HttpClient(new StubHttpMessageHandler()));

        Assert.All(provider.Descriptor.Models, model => Assert.Equal(30, model.Voices.Count));
        Assert.All(provider.Descriptor.Models, model => Assert.Contains("Zephyr", model.Voices));
        Assert.All(provider.Descriptor.Models, model => Assert.Contains("Zubenelgenubi", model.Voices));
    }

    [Fact]
    public async Task SynthesizeAsync_SendsHeaderAndConvertsPcmToWave()
    {
        var handler = new StubHttpMessageHandler();
        var pcm = new byte[] { 0, 1, 2, 3 };
        handler.Enqueue(
            HttpStatusCode.OK,
            $$"""
            {
              "responseId": "gemini-response",
              "candidates": [{
                "finishReason": "STOP",
                "content": { "parts": [{
                  "inlineData": {
                    "mimeType": "audio/L16;codec=pcm;rate=24000",
                    "data": "{{Convert.ToBase64String(pcm)}}"
                  }
                }]}
              }]
            }
            """);
        var provider = new GeminiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var result = await provider.SynthesizeAsync(Request(), "google-key", CancellationToken.None);

        Assert.Equal("audio/wav", result.MediaType);
        Assert.Equal(24_000, result.SampleRate);
        Assert.Equal(48, result.Audio.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(result.Audio, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(result.Audio, 8, 4));
        Assert.Equal("google-key", handler.Requests.Single().GoogleApiKey);
        Assert.DoesNotContain("google-key", handler.Requests.Single().RequestUri?.ToString(), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        var configuration = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("AUDIO", configuration.GetProperty("responseModalities")[0].GetString());
        Assert.Equal(
            "Kore",
            configuration.GetProperty("speechConfig")
                .GetProperty("voiceConfig")
                .GetProperty("prebuiltVoiceConfig")
                .GetProperty("voiceName")
                .GetString());
        var submitted = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString();
        Assert.Equal("Warm, measured delivery.\n\nCanonical narration.", submitted);
    }

    [Fact]
    public async Task SynthesizeAsync_InvalidApiKeyPayloadIsAuthenticationFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}");
        var provider = new GeminiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "bad-google-key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Authentication, exception.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain("bad-google-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynthesizeAsync_PromptBlockIsPolicyFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}");
        var provider = new GeminiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Policy, exception.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SynthesizeAsync_MissingAudioIsMalformedResponse()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.OK,
            "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"unexpected\"}]}}]}");
        var provider = new GeminiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.MalformedResponse, exception.Kind);
    }

    [Fact]
    public async Task SynthesizeAsync_RateLimitRetriesThreeTimesWithoutChangingModel()
    {
        var handler = new StubHttpMessageHandler();
        for (var index = 0; index < 4; index++)
        {
            handler.Enqueue(HttpStatusCode.TooManyRequests, "{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\"}}");
        }

        var delay = new RecordingRetryDelay();
        var provider = new GeminiSpeechProvider(new HttpClient(handler), delay);

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.RateLimited, exception.Kind);
        Assert.Equal(4, handler.SendCount);
        Assert.Equal(3, delay.Calls.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                "gemini-3.1-flash-tts-preview",
                request.RequestUri?.ToString(),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task SynthesizeAsync_TransientFailureRetriesThenReturnsAudio()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "{\"error\":{\"status\":\"INTERNAL\"}}");
        var pcm = Convert.ToBase64String(new byte[] { 0, 1, 2, 3 });
        handler.Enqueue(
            HttpStatusCode.OK,
            "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"data\":\""
            + pcm
            + "\"}}]}}]}");
        var delay = new RecordingRetryDelay();
        var provider = new GeminiSpeechProvider(new HttpClient(handler), delay);

        var result = await provider.SynthesizeAsync(Request(), "key", CancellationToken.None);

        Assert.Equal("audio/wav", result.MediaType);
        Assert.Equal(2, handler.SendCount);
        Assert.Single(delay.Calls);
    }

    [Fact]
    public async Task SynthesizeAsync_CancellationDoesNotRetry()
    {
        var handler = new StubHttpMessageHandler();
        var delay = new RecordingRetryDelay();
        var provider = new GeminiSpeechProvider(new HttpClient(handler), delay);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SynthesizeAsync(Request(), "key", source.Token));

        Assert.Equal(0, handler.SendCount);
        Assert.Empty(delay.Calls);
    }

    [Theory]
    [InlineData("gemini-3.1-flash-tts-preview")]
    [InlineData("gemini-2.5-flash-preview-tts")]
    [InlineData("gemini-2.5-pro-preview-tts")]
    public async Task SynthesizeAsync_RejectsGeminiInputOver8192Tokens(string modelId)
    {
        var handler = new StubHttpMessageHandler();
        var provider = new GeminiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(() => provider.SynthesizeAsync(
            Request() with { ModelId = modelId, EstimatedInputTokens = 8_193 },
            "key",
            CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Validation, exception.Kind);
        Assert.Equal(0, handler.SendCount);
    }

    private static SpeechSynthesisRequest Request() => new(
        "gemini-3.1-flash-tts-preview",
        "Kore",
        "Canonical narration.",
        "Warm, measured delivery.",
        20);
}
