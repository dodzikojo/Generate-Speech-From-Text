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

    [Fact]
    public async Task CreateDraftAsync_CueOptInAcceptsProductionGuidanceAndStatesTheConstraint()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output":[{"content":[{"type":"output_text","text":"{\"title\":\"Welcome\",\"segments\":[{\"title\":\"Opening\",\"text\":\"@visual: Show the dashboard.\\n\\nWelcome to VoiceBench.\"}],\"summary\":\"Drafted with guidance\"}"}]}]}
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        ChapterDraft result = await provider.CreateDraftAsync(DraftRequest(generateProductionCues: true), "key", CancellationToken.None);

        Assert.Contains("@visual:", result.Segments[0].Text, StringComparison.Ordinal);
        CapturedRequest captured = Assert.Single(handler.Requests);
        using JsonDocument body = JsonDocument.Parse(captured.Body);
        string prompt = body.RootElement.GetProperty("input").GetString()!;
        Assert.Contains("standalone @visual:", prompt, StringComparison.Ordinal);
        Assert.Contains("Cue lines are not narration", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDraftAsync_CueOptOutRejectsUnexpectedGuidance()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output":[{"content":[{"type":"output_text","text":"{\"title\":\"Welcome\",\"segments\":[{\"title\":\"Opening\",\"text\":\"Narration.\\n\\n@action: Click continue.\"}],\"summary\":\"Drafted\"}"}]}]}
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.CreateDraftAsync(DraftRequest(generateProductionCues: false), "key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.MalformedResponse, exception.Kind);
        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviseAsync_CueFocusedActionRejectsNarrationChanges()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output":[{"content":[{"type":"output_text","text":"{\"proposedText\":\"Changed narration.\\n\\n@visual: Show the dashboard.\",\"summary\":\"Added guidance\",\"warnings\":[]}"}]}]}
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());
        WritingRevisionRequest request = RevisionRequest() with
        {
            Action = WritingAction.GenerateProductionCues,
            Text = "Original narration.",
        };

        WritingProviderException exception = await Assert.ThrowsAsync<WritingProviderException>(
            () => provider.ReviseAsync(request, "key", CancellationToken.None));

        Assert.Equal(WritingProviderFailureKind.MalformedResponse, exception.Kind);
        Assert.Contains("preserve narration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviseAsync_ApplyCritiqueIncludesEditorialNotesAndPreservesCues()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.OK, """
            {"output":[{"content":[{"type":"output_text","text":"{\"proposedText\":\"@visual: Keep this cue.\\n\\nClearer narration.\",\"summary\":\"Applied the critique\",\"warnings\":[]}"}]}]}
            """);
        OpenAiWritingProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());
        WritingRevisionRequest request = RevisionRequest() with
        {
            Action = WritingAction.ApplyCritique,
            Text = "@visual: Keep this cue.\n\nOriginal narration.",
            CustomInstruction = "Replace the vague management-layer phrase with a concrete benefit.",
        };

        WritingProposal proposal = await provider.ReviseAsync(request, "key", CancellationToken.None);

        Assert.Equal("Applied the critique", proposal.Summary);
        CapturedRequest captured = Assert.Single(handler.Requests);
        using JsonDocument body = JsonDocument.Parse(captured.Body);
        string prompt = body.RootElement.GetProperty("input").GetString()!;
        Assert.Contains("Replace the vague management-layer phrase", prompt, StringComparison.Ordinal);
        Assert.Contains("Preserve every existing production cue line verbatim", prompt, StringComparison.Ordinal);
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

    private static WritingDraftRequest DraftRequest(bool generateProductionCues) => new(
        "gpt-5.6-terra",
        new ChapterOutline(
            "Welcome",
            "Introduction",
            [new ChapterOutlineSegment("Opening", "Orient the viewer", 45)]),
        new WritingContext(new WritingProfile(), "Start with the value", []),
        100,
        generateProductionCues);
}
