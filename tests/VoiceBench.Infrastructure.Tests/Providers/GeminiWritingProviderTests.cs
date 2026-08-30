using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class GeminiWritingProviderTests
{
    [Fact]
    public async Task ReviseAsync_UsesInteractionsStructuredOutputAndStoreFalse()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output_text":"{\"proposedText\":\"Clear narration.\",\"summary\":\"Simplified\",\"warnings\":[]}"}
            """);
        GeminiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WritingProposal proposal = await provider.ReviseAsync(Request(), "google-key", CancellationToken.None);

        Assert.Equal("Clear narration.", proposal.ProposedText);
        CapturedRequest captured = Assert.Single(handler.Requests);
        Assert.Equal("google-key", captured.GoogleApiKey);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", captured.RequestUri?.ToString());
        using JsonDocument body = JsonDocument.Parse(captured.Body);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Empty(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        JsonElement format = body.RootElement.GetProperty("response_format");
        Assert.Equal("application/json", format.GetProperty("mime_type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReviseAsync_TransientFailureRetriesWithoutChangingModel()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{\"error\":{}}");
        handler.Enqueue(HttpStatusCode.OK, """
            {"output_text":"{\"proposedText\":\"Clear narration.\",\"summary\":\"Simplified\",\"warnings\":[]}"}
            """);
        RecordingRetryDelay delay = new();
        GeminiWritingProvider provider = new(new HttpClient(handler), delay);

        await provider.ReviseAsync(Request(), "key", CancellationToken.None);

        Assert.Equal(2, handler.SendCount);
        Assert.Single(delay.Calls);
        Assert.All(handler.Requests, request => Assert.Contains("gemini-3.7-flash", request.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviseAsync_MalformedResponseIsClassified()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, "{\"unexpected\":true}");
        GeminiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.ReviseAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.MalformedResponse, exception.Kind);
    }

    [Fact]
    public async Task ReviseAsync_InvalidApiKeyPayloadIsAuthenticationFailure()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.BadRequest, "{\"error\":{\"reason\":\"API_KEY_INVALID\"}}");
        GeminiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.ReviseAsync(Request(), "bad-key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.Authentication, exception.Kind);
        Assert.DoesNotContain("bad-key", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task ReviseAsync_CancellationDoesNotSendOrRetry()
    {
        StubHttpMessageHandler handler = new();
        RecordingRetryDelay delay = new();
        GeminiWritingProvider provider = new(new HttpClient(handler), delay);
        using CancellationTokenSource source = new();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ReviseAsync(Request(), "key", source.Token));

        Assert.Equal(0, handler.SendCount);
        Assert.Empty(delay.Calls);
    }

    private static WritingRevisionRequest Request() => new(
        "gemini-3.7-flash",
        WritingAction.ImproveClarity,
        "Original narration.",
        null,
        new WritingContext(new WritingProfile(), "", []),
        20);
}
