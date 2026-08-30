using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class OpenAiWritingProviderTests
{
    [Fact]
    public async Task CreateOutlineAsync_UsesStatelessStructuredResponseWithoutTools()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "output": [{
                "type": "message",
                "content": [{
                  "type": "output_text",
                  "text": "{\"title\":\"Welcome\",\"summary\":\"Intro\",\"segments\":[{\"title\":\"Opening\",\"purpose\":\"Orient\",\"targetSeconds\":45}]}"
                }]
              }]
            }
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        ChapterOutline result = await provider.CreateOutlineAsync(OutlineRequest(), "secret-key", CancellationToken.None);

        Assert.Equal("Welcome", result.Title);
        CapturedRequest captured = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("secret-key", captured.AuthorizationParameter);
        using JsonDocument body = JsonDocument.Parse(captured.Body);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Empty(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("gpt-5.6-terra", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        string prompt = body.RootElement.GetProperty("input").GetString()!;
        Assert.Contains("Selected reference text", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Unselected reference", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviseAsync_RefusalIsPolicyFailure()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output":[{"content":[{"type":"refusal","refusal":"Cannot assist"}]}]}
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.ReviseAsync(RevisionRequest(), "key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.Policy, exception.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task CreateOutlineAsync_AuthenticationFailureDoesNotRetry()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"invalid_api_key\"}}");
        RecordingRetryDelay delay = new();
        OpenAiWritingProvider provider = new(new HttpClient(handler), delay);

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.CreateOutlineAsync(OutlineRequest(), "bad-key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.Authentication, exception.Kind);
        Assert.Empty(delay.Calls);
        Assert.DoesNotContain("bad-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateOutlineAsync_RateLimitUsesThreeBoundedRetries()
    {
        StubHttpMessageHandler handler = new();
        for (int index = 0; index < 4; index++)
        {
            handler.Enqueue(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit\"}}");
        }
        RecordingRetryDelay delay = new();
        OpenAiWritingProvider provider = new(new HttpClient(handler), delay);

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.CreateOutlineAsync(OutlineRequest(), "key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.RateLimited, exception.Kind);
        Assert.Equal(4, handler.SendCount);
        Assert.Equal(3, delay.Calls.Count);
        Assert.All(handler.Requests, request => Assert.Contains("gpt-5.6-terra", request.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOutlineAsync_InvalidStructuredOutputIsMalformed()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output":[{"content":[{"type":"output_text","text":"{\"title\":\"Empty\",\"summary\":\"\",\"segments\":[]}"}]}]}
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.CreateOutlineAsync(OutlineRequest(), "key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.MalformedResponse, exception.Kind);
    }

    private static WritingOutlineRequest OutlineRequest() => new(
        "gpt-5.6-terra",
        "Welcome",
        "Introduce the course",
        180,
        new WritingContext(
            new WritingProfile { Audience = "New operators", Tone = "Warm" },
            "Start with the value",
            [new WritingReference("selected.md", "Selected reference text", "abc")]),
        100);

    private static WritingRevisionRequest RevisionRequest() => new(
        "gpt-5.6-terra",
        WritingAction.ImproveClarity,
        "Original narration.",
        null,
        new WritingContext(new WritingProfile(), "", []),
        20);
}
