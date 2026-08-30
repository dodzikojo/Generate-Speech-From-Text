using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class OpenAiWordAlignmentProviderTests
{
    [Fact]
    public async Task TranscribeAsync_requests_verbose_word_timestamps_and_parses_them()
    {
        StubHttpMessageHandler handler = new();
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"duration":1.2,"words":[{"word":"Hello","start":0.1,"end":0.5},{"word":"world","start":0.6,"end":1.1}]}"""),
        };
        response.Headers.Add("x-request-id", "alignment-42");
        handler.Enqueue(response);
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WordAlignmentProviderResult result = await provider.TranscribeAsync(Request(), "secret-key", CancellationToken.None);

        Assert.Equal("whisper-1", provider.ModelId);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.1), result.Words[0].Start);
        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", sent.AuthorizationScheme);
        Assert.Equal("secret-key", sent.AuthorizationParameter);
        Assert.Contains("name=model", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whisper-1", sent.Body, StringComparison.Ordinal);
        Assert.Contains("verbose_json", sent.Body, StringComparison.Ordinal);
        Assert.Contains("timestamp_granularities", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_does_not_retry_authentication_failures()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"invalid_api_key\"}}");
        RecordingRetryDelay delay = new();
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), delay);

        WordAlignmentProviderException exception = await Assert.ThrowsAsync<WordAlignmentProviderException>(
            () => provider.TranscribeAsync(Request(), "bad-key", CancellationToken.None));

        Assert.Equal(WordAlignmentFailureKind.Authentication, exception.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.Empty(delay.Calls);
        Assert.DoesNotContain("bad-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_classifies_malformed_word_payload()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, "{\"duration\":1.0,\"words\":[{\"word\":\"Hello\",\"end\":0.5}]}");
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WordAlignmentProviderException exception = await Assert.ThrowsAsync<WordAlignmentProviderException>(
            () => provider.TranscribeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(WordAlignmentFailureKind.MalformedResponse, exception.Kind);
    }

    [Fact]
    public async Task TranscribeAsync_keeps_usable_words_when_provider_timestamps_touch_or_overlap()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(
            HttpStatusCode.OK,
            """{"duration":1.0,"words":[{"word":"Hello","start":0.0,"end":0.55},{"word":"world","start":0.5,"end":0.9},{"word":"again","start":0.9,"end":0.9}]}""");
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WordAlignmentProviderResult result = await provider.TranscribeAsync(Request(), "key", CancellationToken.None);

        Assert.Equal(3, result.Words.Count);
        Assert.Equal(result.Words[1].Start, result.Words[0].End);
        Assert.True(result.Words[2].End > result.Words[2].Start);
        Assert.All(result.Words.Zip(result.Words.Skip(1)), pair => Assert.True(pair.First.End <= pair.Second.Start));
    }

    [Fact]
    public async Task TranscribeAsync_retries_rate_limits_then_uses_the_same_provider()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "{}");
        handler.Enqueue(HttpStatusCode.OK, "{\"duration\":0.5,\"words\":[{\"word\":\"Hello\",\"start\":0,\"end\":0.5}]}");
        RecordingRetryDelay delay = new();
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), delay);

        WordAlignmentProviderResult result = await provider.TranscribeAsync(Request(), "key", CancellationToken.None);

        Assert.Single(result.Words);
        Assert.Equal(2, handler.SendCount);
        Assert.Single(delay.Calls);
        Assert.All(handler.Requests, request => Assert.Equal("api.openai.com", request.RequestUri?.Host));
    }

    [Fact]
    public async Task TranscribeAsync_reports_policy_rejection_without_retry()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"content_policy_violation\"}}");
        RecordingRetryDelay delay = new();
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), delay);

        WordAlignmentProviderException exception = await Assert.ThrowsAsync<WordAlignmentProviderException>(
            () => provider.TranscribeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(WordAlignmentFailureKind.Policy, exception.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.Empty(delay.Calls);
    }

    [Fact]
    public async Task TranscribeAsync_honors_cancellation_without_retrying_or_falling_back()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        RecordingRetryDelay delay = new();
        OpenAiWordAlignmentProvider provider = new(new HttpClient(handler), delay);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(Request(), "key", cancellation.Token));

        Assert.Equal(1, handler.SendCount);
        Assert.Empty(delay.Calls);
    }

    private static WordAlignmentProviderRequest Request() => new(
        ProviderTestAudio.Wave(),
        "take.wav",
        "audio/wav",
        "Hello world");
}
