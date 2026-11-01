using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Application;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IssueLab.VoiceBench.Mcp;

/// <summary>The MCP boundary only delegates to the visible desktop's application services.</summary>
public sealed class VoiceBenchMcpServer(IAutomationDispatcher dispatcher) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, Subscription> subscriptions = new(StringComparer.Ordinal);
    private Task? pollLoop;
    private McpServer? server;
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"isError":{"type":"boolean"},"code":{"type":["string","null"]},"message":{"type":["string","null"]},"data":{}},"required":["isError","code","message","data"],"additionalProperties":false}
        """).RootElement.Clone();

    private static readonly (string Path, string Command, string Description)[] Resources =
    [
        ("workspace", "workspace.get", "Live workspace, selection and editor state"),
        ("project", "project.get", "Active project manifest and root"),
        ("editor", "editor.get", "Live editor text and revision token"),
        ("providers", "providers.list", "Provider, model, voice and tag catalog"),
        ("jobs", "jobs.list", "Durable job status and progress"),
        ("proposals", "writing.proposals.list", "Local writing proposals"),
        ("exports", "exports.list", "Project exports and artifacts"),
        ("mix", "mix.get", "Compilation arrangement and revision"),
        ("assets", "assets.list", "Bounded immutable audio asset catalog"),
        ("mix-playback", "mix.playback.get", "Shared mix playback target, clock and monitor state"),
    ];

    public McpServerOptions CreateOptions() => new()
    {
        ServerInfo = new() { Name = "VoiceBench", Version = "1.1.0" },
        // Pin the mature session protocol so resource subscriptions work with generic clients.
        ProtocolVersion = "2025-11-25",
        ServerInstructions = "VoiceBench controls the visible local Windows desktop. Discovery never launches it. The first tool call or resource read attaches or launches it. Read current editor revisions before edits; text ranges are UTF-16. Supply requestId for durable writes. Select providers/models/voices explicitly; credentials are only entered in the desktop secure UI.",
        Capabilities = new()
        {
            Tools = new() { ListChanged = false },
            Resources = new() { ListChanged = true, Subscribe = true },
        },
        Handlers = new()
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = AutomationCapabilities.All.Select(capability => new Tool
                {
                    Name = capability.ToolName,
                    Description = capability.Description,
                    InputSchema = WithRequestId(capability),
                    OutputSchema = OutputSchema,
                    Annotations = new()
                    {
                        ReadOnlyHint = capability.ReadOnly,
                        DestructiveHint = capability.Destructive,
                        IdempotentHint = capability.ReadOnly,
                        OpenWorldHint = !capability.ReadOnly && (capability.Command.StartsWith("writing.", StringComparison.Ordinal) || capability.Command.StartsWith("render.", StringComparison.Ordinal)),
                    },
                }).ToArray(),
            }),
            CallToolHandler = CallToolAsync,
            ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = Resources.Select(resource => new Resource
                {
                    Uri = "voicebench:///" + resource.Path,
                    Name = resource.Path,
                    Description = resource.Description,
                    MimeType = "application/json",
                }).ToArray(),
            }),
            ListResourceTemplatesHandler = (_, _) => ValueTask.FromResult(new ListResourceTemplatesResult
            {
                ResourceTemplates = [new ResourceTemplate
                {
                    UriTemplate = "voicebench:///segments/{segmentId}/text{?offset,count}",
                    Name = "segment-text",
                    Description = "Saved segment text in bounded UTF-16 pages. Follow nextOffset; optional count is 1–65536.",
                    MimeType = "application/json",
                }, new ResourceTemplate
                {
                    UriTemplate = "voicebench:///artifacts/{relativePath}",
                    Name = "artifact-metadata",
                    Description = "Original project artifact metadata. Read small text artifacts with artifacts.read; request bounded audio previews with artifacts.audio-preview.",
                    MimeType = "application/json",
                }, new ResourceTemplate
                {
                    UriTemplate = "voicebench:///mixes/chapters/{chapterId}", Name = "chapter-mix",
                    Description = "Chapter audio arrangement and current optimistic revision.", MimeType = "application/json",
                }, new ResourceTemplate
                {
                    UriTemplate = "voicebench:///assets/{assetId}/waveform{?offset,count}", Name = "audio-waveform",
                    Description = "A bounded page of source waveform min/max buckets; count 1–4096.", MimeType = "application/json",
                }],
            }),
            ReadResourceHandler = async (request, cancellationToken) => await ReadResourceAsync(request.Params.Uri, cancellationToken),
            SubscribeToResourcesHandler = async (request, cancellationToken) =>
            {
                var uri = request.Params.Uri;
                var resource = await ReadResourceAsync(uri, cancellationToken);
                subscriptions[uri] = new(request.Server, JsonSerializer.Serialize(resource, AutomationJson.Options));
                EnsurePolling();
                return new EmptyResult();
            },
            UnsubscribeFromResourcesHandler = (request, cancellationToken) =>
            {
                subscriptions.TryRemove(request.Params.Uri, out _);
                return ValueTask.FromResult(new EmptyResult());
            },
        },
    };

    public async Task RunStdioAsync(CancellationToken cancellationToken = default)
    {
        var options = CreateOptions();
        await using var transport = new StdioServerTransport(options);
        server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken);
    }

    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var capability = AutomationCapabilities.All.FirstOrDefault(candidate => candidate.ToolName == context.Params.Name)
            ?? throw new McpProtocolException("Unknown VoiceBench tool.", McpErrorCode.InvalidParams);
        var arguments = context.Params.Arguments is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(context.Params.Arguments);
        var schema = WithRequestId(capability);
        var validationError = ValidateValue(AutomationJson.Element(arguments), schema, "arguments");
        if (validationError is not null) return ToolResult(AutomationResult.Error("invalid_arguments", validationError));
        string? requestId = null;
        if (arguments.Remove("requestId", out var id))
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                return ToolResult(AutomationResult.Error("invalid_arguments", "requestId must be a nonempty string."));
            requestId = id.GetString();
        }
        AutomationResult result;
        try { result = await dispatcher.ExecuteAsync(new(capability.Command, AutomationJson.Element(arguments), requestId), cancellationToken); }
        catch (AutomationException exception) { result = AutomationResult.Error(exception.Code, exception.Message, exception.Details); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { result = AutomationResult.Error("internal_error", "VoiceBench could not complete the operation. Inspect the desktop app for details."); }
        return capability.Command == "artifacts.audio-preview" && !result.IsError ? AudioPreviewResult(result) : ToolResult(result);
    }

    private static CallToolResult ToolResult(AutomationResult result)
    {
        var structured = AutomationJson.Element(result);
        return new() { IsError = result.IsError, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static CallToolResult AudioPreviewResult(AutomationResult result)
    {
        try
        {
            var data = result.Data;
            var encoded = data.GetProperty("audioBase64").GetString()!;
            if (encoded.Length > 512 * 1024) return ToolResult(AutomationResult.Error("preview_too_large", "Request a shorter audio preview."));
            var mimeType = data.GetProperty("mimeType").GetString()!;
            var relativePath = data.GetProperty("relativePath").GetString()!;
            var uri = "voicebench:///artifacts/" + Uri.EscapeDataString(relativePath);
            var metadata = JsonNode.Parse(data.GetRawText())!.AsObject();
            metadata.Remove("audioBase64");
            metadata["originalResourceUri"] = uri;
            var preview = ToolResult(result with { Data = JsonSerializer.SerializeToElement(metadata) });
            preview.Content.Add(AudioContentBlock.FromBytes(Convert.FromBase64String(encoded), mimeType));
            preview.Content.Add(new ResourceLinkBlock
            {
                Uri = uri, Name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar)),
                Description = "Original project audio artifact metadata; the embedded audio is a bounded preview.",
                MimeType = "application/json",
            });
            return preview;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        { return ToolResult(AutomationResult.Error("invalid_preview", "The desktop returned an invalid audio preview.")); }
    }

    private async Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        const string artifactPrefix = "voicebench:///artifacts/";
        if (uri.StartsWith(artifactPrefix, StringComparison.Ordinal))
        {
            var relativePath = Uri.UnescapeDataString(uri[artifactPrefix.Length..]).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\') || relativePath.Contains(':') || relativePath.Split('/', '\\').Contains(".."))
                throw new McpProtocolException("Invalid project artifact path.", McpErrorCode.InvalidParams);
            var directory = relativePath.Contains('/') ? relativePath[..relativePath.LastIndexOf('/')] : ".";
            var listing = await dispatcher.ExecuteAsync(new("artifacts.list", AutomationJson.Element(new { relativeDirectory = directory })), cancellationToken);
            if (listing.IsError) throw new McpException(listing.Code + ": " + listing.Message);
            var artifact = listing.Data.ValueKind == JsonValueKind.Array ? listing.Data.EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("relativePath", out var path) && string.Equals(path.GetString()?.Replace('\\', '/'), relativePath, StringComparison.OrdinalIgnoreCase)) : default;
            if (artifact.ValueKind == JsonValueKind.Undefined) throw new McpException("artifact.not-found: The project artifact does not exist.");
            return new() { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = artifact.GetRawText() }] };
        }
        var request = ResourceRequest(uri);
        var result = await dispatcher.ExecuteAsync(request, cancellationToken);
        if (result.IsError) throw new McpException(result.Code + ": " + result.Message);
        return new() { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = result.Data.GetRawText() }] };
    }

    private static AutomationRequest ResourceRequest(string uri)
    {
        foreach (var resource in Resources)
            if (uri == "voicebench:///" + resource.Path) return new(resource.Command, AutomationJson.Element(new { }));
        const string mixPrefix="voicebench:///mixes/chapters/";
        if(uri.StartsWith(mixPrefix,StringComparison.Ordinal))
        {
            string chapter=Uri.UnescapeDataString(uri[mixPrefix.Length..]);
            if(string.IsNullOrWhiteSpace(chapter)||chapter.IndexOfAny(['/', '\\', '?', '#'])>=0)throw new McpProtocolException("Invalid chapter mix identifier.",McpErrorCode.InvalidParams);
            return new("mix.get",AutomationJson.Element(new {scope="Chapter",chapterId=chapter}));
        }
        const string assetPrefix="voicebench:///assets/";
        if(uri.StartsWith(assetPrefix,StringComparison.Ordinal))
        {
            string[] parts=uri[assetPrefix.Length..].Split('?',2);
            string[] path=parts[0].Split('/');
            if(path.Length!=2||path[1]!="waveform"||!Guid.TryParse(path[0],out var asset))throw new McpProtocolException("Invalid asset waveform identifier.",McpErrorCode.InvalidParams);
            int offset=0,count=512;
            var seen=new HashSet<string>();
            if(parts.Length==2)foreach(string item in parts[1].Split('&'))
            {
                string[] p=item.Split('=',2);
                if(p.Length!=2||!seen.Add(p[0])||p[0] is not("offset" or "count")||!int.TryParse(p[1],out int value)||value<0||p[0]=="count"&&value is <1 or >4096)throw new McpProtocolException("Use nonnegative offset and count 1–4096.",McpErrorCode.InvalidParams);
                if(p[0]=="offset")offset=value;else count=value;
            }
            return new("assets.waveform",AutomationJson.Element(new {assetId=asset,offset,count}));
        }
        const string prefix = "voicebench:///segments/";
        const string suffix = "/text";
        var queryIndex = uri.IndexOf('?');
        var resourcePath = queryIndex < 0 ? uri : uri[..queryIndex];
        if (resourcePath.StartsWith(prefix, StringComparison.Ordinal) && resourcePath.EndsWith(suffix, StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(resourcePath[prefix.Length..^suffix.Length]);
            if (!string.IsNullOrWhiteSpace(id) && !id.Contains('/') && !id.Contains('\\'))
            {
                Dictionary<string, object> arguments = new() { ["segmentId"] = id };
                if (queryIndex >= 0)
                {
                    foreach (var entry in uri[(queryIndex + 1)..].Split('&'))
                    {
                        var pair = entry.Split('=', 2);
                        if (pair.Length != 2 || pair[0] is not ("offset" or "count") || arguments.ContainsKey(pair[0]) ||
                            !int.TryParse(pair[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                            value < 0 || (pair[0] == "count" && value is < 1 or > 65536))
                            throw new McpProtocolException("Invalid segment text pagination. Use a nonnegative offset and count 1–65536.", McpErrorCode.InvalidParams);
                        arguments.Add(pair[0], value);
                    }
                }
                return new("script.get", AutomationJson.Element(arguments));
            }
        }
        throw new McpProtocolException("Unknown VoiceBench resource.", McpErrorCode.InvalidParams);
    }

    private void EnsurePolling()
    {
        lock (subscriptions) pollLoop ??= PollAsync(lifetime.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var (uri, subscription) in subscriptions.ToArray())
                {
                    try
                    {
                        string current;
                        try { current = JsonSerializer.Serialize(await ReadResourceAsync(uri, cancellationToken), AutomationJson.Options); }
                        catch (McpException exception) { current = "error:" + exception.Message; }
                        if (current == subscription.Snapshot) continue;
                        if (!subscriptions.TryUpdate(uri, subscription with { Snapshot = current }, subscription)) continue;
                        await subscription.Server.SendNotificationAsync("notifications/resources/updated", new ResourceUpdatedNotificationParams { Uri = uri }, AutomationJson.Options, cancellationToken);
                        if (uri == "voicebench:///project")
                            await subscription.Server.SendNotificationAsync("notifications/resources/list_changed", cancellationToken);
                    }
                    catch (McpException) { }
                    catch (IOException) { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static bool RequiresRequestId(string command) => command is "writing.outline" or "writing.draft" or "writing.revise"
        or "render.start" or "render.batch" or "render.audition" or "render.process" or "alignment.start" or "master.start" or "jobs.retry";

    private static JsonElement WithRequestId(AutomationCapability capability)
    {
        var document = JsonNode.Parse(capability.InputSchema.GetRawText())!.AsObject();
        var properties = document["properties"] as JsonObject ?? new JsonObject();
        if (document["properties"] is null) document["properties"] = properties;
        properties["requestId"] = new JsonObject { ["type"] = "string", ["description"] = "Stable unique ID for deduplicating a durable write. Reuse only when retrying the same request." };
        if (RequiresRequestId(capability.Command))
        {
            var required = document["required"] as JsonArray ?? new JsonArray();
            if (document["required"] is null) document["required"] = required;
            required.Add("requestId");
        }
        return JsonSerializer.SerializeToElement(document);
    }

    private static string? ValidateValue(JsonElement value, JsonElement schema, string path, JsonElement? rootSchema = null, int depth = 0)
    {
        if (depth > 64) return path + " exceeds the schema nesting limit.";
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind == JsonValueKind.False) return path + " is not allowed.";
        var root = rootSchema ?? schema;
        if (schema.TryGetProperty("$ref", out var reference) && reference.GetString() is string pointer && pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            var target = root;
            foreach (var component in pointer[2..].Split('/'))
            {
                var name = component.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (!target.TryGetProperty(name, out target)) return path + " has an unresolved schema reference.";
            }
            var referenceError = ValidateValue(value, target, path, root, depth + 1);
            if (referenceError is not null) return referenceError;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            bool MatchesType(string? typeName) => typeName switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false,
            };
            var valid = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(item => MatchesType(item.GetString())) : MatchesType(type.GetString());
            if (!valid) return path + " must match type " + type.GetRawText() + ".";
        }
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            return path + " is not an allowed value.";
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) return path + " must equal its declared value.";
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
            if (schema.TryGetProperty(keyword, out var alternatives))
            {
                var branches = alternatives.EnumerateArray().ToArray();
                var matches = branches.Count(branch => ValidateValue(value, branch, path, root, depth + 1) is null);
                if ((keyword == "anyOf" && matches == 0) || (keyword == "oneOf" && matches != 1) || (keyword == "allOf" && matches != branches.Length))
                    return path + " does not match its allowed schema.";
            }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var property in required.EnumerateArray())
                    if (!value.TryGetProperty(property.GetString()!, out _)) return path + "." + property.GetString() + " is required.";
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            foreach (var property in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(property.Name, out var childSchema))
                {
                    var error = ValidateValue(property.Value, childSchema, path + "." + property.Name, root, depth + 1);
                    if (error is not null) return error;
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    return path + " contains an unsupported property: " + property.Name + ".";
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var error = ValidateValue(item, items, path + "[" + index++ + "]", root, depth + 1);
                if (error is not null) return error;
            }
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var minimum) && value.GetDouble() < minimum.GetDouble()) return path + " is below its minimum value.";
            if (schema.TryGetProperty("maximum", out var maximum) && value.GetDouble() > maximum.GetDouble()) return path + " is above its maximum value.";
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minimumLength) && text.Length < minimumLength.GetInt32()) return path + " is too short.";
            if (schema.TryGetProperty("maxLength", out var maximumLength) && text.Length > maximumLength.GetInt32()) return path + " is too long.";
            if (schema.TryGetProperty("pattern", out var pattern))
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(text, pattern.GetString()!,
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)))
                        return path + " does not match its required format.";
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return path + " exceeds its format validation limit."; }
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (pollLoop is not null) await pollLoop;
        if (server is not null) await server.DisposeAsync();
        lifetime.Dispose();
    }

    private sealed record Subscription(McpServer Server, string Snapshot);
}
