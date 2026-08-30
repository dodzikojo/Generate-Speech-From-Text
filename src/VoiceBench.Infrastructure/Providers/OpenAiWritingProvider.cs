using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class OpenAiWritingProvider : HttpWritingProviderBase
{
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private static readonly WritingProviderDescriptor ProviderDescriptor = new(
        "openai",
        "OpenAI",
        [
            new WritingModelDescriptor("gpt-5.6-terra", "GPT-5.6 Terra", 128_000),
            new WritingModelDescriptor("gpt-5.6-luna", "GPT-5.6 Luna", 128_000),
            new WritingModelDescriptor("gpt-5.6-sol", "GPT-5.6 Sol", 128_000),
        ]);

    public OpenAiWritingProvider(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
        : base(httpClient, retryDelay)
    {
    }

    public override WritingProviderDescriptor Descriptor => ProviderDescriptor;

    protected override HttpRequestMessage CreateRequest(
        string modelId,
        string prompt,
        string schemaName,
        JsonElement schema,
        string apiKey)
    {
        HttpRequestMessage message = new(HttpMethod.Post, Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            model = modelId,
            input = prompt,
            store = false,
            tools = Array.Empty<object>(),
            reasoning = new { effort = "low" },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = schemaName,
                    strict = true,
                    schema,
                },
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
            if (root.TryGetProperty("output", out JsonElement output))
            {
                foreach (JsonElement item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out JsonElement content)) continue;
                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        string? type = part.TryGetProperty("type", out JsonElement typeElement)
                            ? typeElement.GetString()
                            : null;
                        if (string.Equals(type, "refusal", StringComparison.OrdinalIgnoreCase)) throw PolicyRejected();
                        if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase) &&
                            part.TryGetProperty("text", out JsonElement text))
                        {
                            return text.GetString() ?? throw Malformed("the output text was empty");
                        }
                    }
                }
            }

            throw Malformed("the response contained no output_text item");
        }
    }
}
