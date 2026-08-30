using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class GeminiWordAlignmentProviderTests
{
    [Fact]
    public async Task TranscribeAsync_uploads_audio_requests_verbatim_word_annotations_and_cleans_up()
    {
        StubHttpMessageHandler handler = new();
        HttpResponseMessage start = new(HttpStatusCode.OK);
        start.Headers.Add("X-Goog-Upload-URL", "https://upload.example/session");
        handler.Enqueue(start);
        handler.Enqueue(HttpStatusCode.OK, "{\"file\":{\"name\":\"files/audio-1\",\"uri\":\"https://files.example/audio-1\",\"mime_type\":\"audio/wav\"}}");
        handler.Enqueue(
            HttpStatusCode.OK,
            """
            {
              "id": "interactions/word-1",
              "steps": [{"content": [{"annotations": [
                {"type":"word_info","text":"Hello","start_offset":"0.100s","end_offset":"0.450s"},
                {"type":"word_info","text":"world","start_offset":"0.500s","end_offset":"0.850s"}
              ]}]}]
            }
            """);
        handler.Enqueue(HttpStatusCode.OK, "{}");
        GeminiWordAlignmentProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WordAlignmentProviderResult result = await provider.TranscribeAsync(Request(), "google-key", CancellationToken.None);

        Assert.Equal(2, result.Words.Count);
        Assert.Equal("interactions/word-1", result.ProviderResponseId);
        Assert.Equal(4, handler.SendCount);
        Assert.All(handler.Requests, request => Assert.Equal("google-key", request.GoogleApiKey));
        CapturedRequest interaction = handler.Requests[2];
        using JsonDocument body = JsonDocument.Parse(interaction.Body);
        Assert.Equal("gemini-3.5-transcribe", body.RootElement.GetProperty("model").GetString());
        JsonElement mode = body.RootElement
            .GetProperty("generation_config")
            .GetProperty("transcription_config")
            .GetProperty("mode");
        Assert.Equal("verbatim", mode.GetProperty("type").GetString());
        Assert.Equal("word", mode.GetProperty("timestamp_granularities")[0].GetString());
        Assert.Equal(HttpMethod.Delete, handler.Requests[3].Method);
    }

    [Fact]
    public async Task TranscribeAsync_classifies_invalid_google_key_without_retry()
    {
        StubHttpMessageHandler handler = new();
        handler.Enqueue(HttpStatusCode.BadRequest, "{\"error\":{\"reason\":\"API_KEY_INVALID\"}}");
        RecordingRetryDelay delay = new();
        GeminiWordAlignmentProvider provider = new(new HttpClient(handler), delay);

        WordAlignmentProviderException exception = await Assert.ThrowsAsync<WordAlignmentProviderException>(
            () => provider.TranscribeAsync(Request(), "bad-key", CancellationToken.None));

        Assert.Equal(WordAlignmentFailureKind.Authentication, exception.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.Empty(delay.Calls);
    }

    [Fact]
    public async Task TranscribeAsync_rejects_malformed_word_annotations_and_still_cleans_up()
    {
        StubHttpMessageHandler handler = new();
        HttpResponseMessage start = new(HttpStatusCode.OK);
        start.Headers.Add("X-Goog-Upload-URL", "https://upload.example/session");
        handler.Enqueue(start);
        handler.Enqueue(HttpStatusCode.OK, "{\"file\":{\"name\":\"files/audio-1\",\"uri\":\"https://files.example/audio-1\",\"mime_type\":\"audio/wav\"}}");
        handler.Enqueue(HttpStatusCode.OK, "{\"steps\":[{\"content\":[{\"annotations\":[{\"type\":\"word_info\",\"text\":\"Hello\",\"start_offset\":\"later\",\"end_offset\":\"0.5s\"}]}]}]}");
        handler.Enqueue(HttpStatusCode.OK, "{}");
        GeminiWordAlignmentProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WordAlignmentProviderException exception = await Assert.ThrowsAsync<WordAlignmentProviderException>(
            () => provider.TranscribeAsync(Request(), "key", CancellationToken.None));

        Assert.Equal(WordAlignmentFailureKind.MalformedResponse, exception.Kind);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task TranscribeAsync_keeps_usable_annotations_when_provider_timestamps_overlap()
    {
        StubHttpMessageHandler handler = new();
        HttpResponseMessage start = new(HttpStatusCode.OK);
        start.Headers.Add("X-Goog-Upload-URL", "https://upload.example/session");
        handler.Enqueue(start);
        handler.Enqueue(HttpStatusCode.OK, "{\"file\":{\"name\":\"files/audio-1\",\"uri\":\"https://files.example/audio-1\",\"mime_type\":\"audio/wav\"}}");
        handler.Enqueue(
            HttpStatusCode.OK,
            """
            {"steps":[{"content":[{"annotations":[
              {"type":"word_info","text":"Hello","start_offset":"0s","end_offset":"0.55s"},
              {"type":"word_info","text":"world","start_offset":"0.50s","end_offset":"0.90s"}
            ]}]}]}
            """);
        handler.Enqueue(HttpStatusCode.OK, "{}");
        GeminiWordAlignmentProvider provider = new(new HttpClient(handler), new RecordingRetryDelay());

        WordAlignmentProviderResult result = await provider.TranscribeAsync(Request(), "key", CancellationToken.None);

        Assert.Equal(2, result.Words.Count);
        Assert.Equal(result.Words[1].Start, result.Words[0].End);
    }

    [Fact]
    public void Registry_never_substitutes_another_provider()
    {
        WordAlignmentProviderRegistry registry = new(
            [new OpenAiWordAlignmentProvider(new HttpClient(new StubHttpMessageHandler()))]);

        WordAlignmentProviderException exception = Assert.Throws<WordAlignmentProviderException>(
            () => registry.GetRequired("gemini"));

        Assert.Equal(WordAlignmentFailureKind.Unsupported, exception.Kind);
        Assert.Contains("will not substitute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WordAlignmentProviderRequest Request() => new(
        ProviderTestAudio.Wave(),
        "take.wav",
        "audio/wav",
        "Hello world");
}
