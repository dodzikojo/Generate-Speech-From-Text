using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class GeminiWritingProvider : HttpWritingProviderBase
{
    private static readonly Uri Endpoint = new("https://generativelanguage.googleapis.com/v1beta/interactions");
    private static readonly WritingProviderDescriptor ProviderDescriptor = new(
        "gemini",
        "Google Gemini",
        [new WritingModelDescriptor("gemini-3.7-flash", "Gemini 3.7 Flash", 1_000_000)]);

    public GeminiWritingProvider(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
        : base(httpClient, retryDelay)
    {
    }

    public override WritingProviderDescriptor Descriptor => ProviderDescriptor;

    protected override bool IsAuthenticationFailure(HttpStatusCode statusCode, string responseBody) =>
        base.IsAuthenticationFailure(statusCode, responseBody)
        || responseBody.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
        || responseBody.Contains("API key not valid", StringComparison.OrdinalIgnoreCase);

    protected override HttpRequestMessage CreateRequest(
        string modelId,
        string prompt,
        string schemaName,
        JsonElement schema,
        string apiKey)
    {
        _ = schemaName;
        HttpRequestMessage message = new(HttpMethod.Post, Endpoint);
        message.Headers.Add("x-goog-api-key", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = JsonContent.Create(new
        {
            model = modelId,
            input = prompt,
            store = false,
            tools = Array.Empty<object>(),
            response_format = new
            {
                type = "text",
                mime_type = "application/json",
                schema,
            },
        });
        return message;
    }

    protected override async Task<string> ReadOutputTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw Malformed("the response was not valid JSON", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("output_text", out JsonElement outputText))
            {
                return outputText.GetString() ?? throw Malformed("the output text was empty");
            }

            if (root.ToString().Contains("refusal", StringComparison.OrdinalIgnoreCase) ||
                root.ToString().Contains("safety", StringComparison.OrdinalIgnoreCase))
            {
                throw PolicyRejected();
            }

            throw Malformed("the response contained no output_text value");
        }
    }
}
