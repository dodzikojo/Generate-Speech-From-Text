using System.Net;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class OpenAiSpeechProviderTests
{
    [Fact]
    public void Descriptor_exposes_every_documented_gpt_4o_mini_tts_voice()
    {
        var provider = new OpenAiSpeechProvider(new HttpClient(new StubHttpMessageHandler()));

        Assert.Equal(
            ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse", "marin", "cedar"],
            provider.Descriptor.Models.Single().Voices);
    }

    [Fact]
    public async Task SynthesizeAsync_SendsExactModelDirectionsAndWavFormat()
    {
        var handler = new StubHttpMessageHandler();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ProviderTestAudio.Wave()),
        };
        response.Headers.Add("x-request-id", "request-42");
        handler.Enqueue(response);
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var result = await provider.SynthesizeAsync(Request(), "test-openai-key", CancellationToken.None);

        Assert.Equal("audio/wav", result.MediaType);
        Assert.Equal("request-42", result.ProviderResponseId);
        Assert.Equal("Bearer", handler.Requests.Single().AuthorizationScheme);
        Assert.Equal("test-openai-key", handler.Requests.Single().AuthorizationParameter);
        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal("gpt-4o-mini-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Read with calm authority.", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("wav", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal("Canonical narration.", body.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task SynthesizeAsync_AcceptsStreamingWaveWithUnknownLengths()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ProviderTestAudio.StreamingWave()),
        });
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var result = await provider.SynthesizeAsync(Request(), "key", CancellationToken.None);

        Assert.NotEmpty(result.Audio);
        Assert.Equal(24_000, result.SampleRate);
        Assert.Equal(1, result.Channels);
    }

    [Fact]
    public async Task SynthesizeAsync_AuthenticationFailureIsNotRetried()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"invalid_api_key\"}}");
        var delay = new RecordingRetryDelay();
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), delay);

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "bad-key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Authentication, exception.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.Empty(delay.Calls);
        Assert.DoesNotContain("bad-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynthesizeAsync_PolicyFailureIsNotRetried()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"content_policy_violation\"}}");
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Policy, exception.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SynthesizeAsync_ProviderValidationFailureIsNotRetried()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"invalid_value\"}}");
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Validation, exception.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SynthesizeAsync_RateLimitHonorsThreeBoundedRetries()
    {
        var handler = new StubHttpMessageHandler();
        for (var index = 0; index < 4; index++)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":\"rate_limit\"}}"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            handler.Enqueue(response);
        }

        var delay = new RecordingRetryDelay();
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), delay);

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.RateLimited, exception.Kind);
        Assert.Equal(4, handler.SendCount);
        Assert.Equal(3, delay.Calls.Count);
        Assert.All(delay.Calls, call => Assert.Equal(TimeSpan.FromSeconds(2), call.RetryAfter?.Delta));
    }

    [Fact]
    public async Task SynthesizeAsync_RetriesTransientResponseThenReturnsAudio()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{\"error\":{}}");
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ProviderTestAudio.Wave()),
        });
        var delay = new RecordingRetryDelay();
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), delay);

        var result = await provider.SynthesizeAsync(Request(), "key", CancellationToken.None);

        Assert.NotEmpty(result.Audio);
        Assert.Equal(2, handler.SendCount);
        Assert.Single(delay.Calls);
    }

    [Fact]
    public async Task SynthesizeAsync_CancellationIsNotWrappedOrRetried()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var delay = new RecordingRetryDelay();
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), delay);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SynthesizeAsync(Request(), "key", source.Token));

        Assert.Equal(0, handler.SendCount);
        Assert.Empty(delay.Calls);
    }

    [Fact]
    public async Task SynthesizeAsync_MalformedSuccessIsClassified()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not-wave")),
        });
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(
            () => provider.SynthesizeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.MalformedResponse, exception.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SynthesizeAsync_RejectsAdvertisedInputLimitBeforeHttp()
    {
        var handler = new StubHttpMessageHandler();
        var provider = new OpenAiSpeechProvider(new HttpClient(handler), new RecordingRetryDelay());

        var exception = await Assert.ThrowsAsync<SpeechProviderException>(() => provider.SynthesizeAsync(
            Request() with { EstimatedInputTokens = 2_001 },
            "key",
            CancellationToken.None));

        Assert.Equal(SpeechProviderFailureKind.Validation, exception.Kind);
        Assert.Equal(0, handler.SendCount);
    }

    private static SpeechSynthesisRequest Request() => new(
        "gpt-4o-mini-tts",
        "marin",
        "Canonical narration.",
        "Read with calm authority.",
        20);
}
