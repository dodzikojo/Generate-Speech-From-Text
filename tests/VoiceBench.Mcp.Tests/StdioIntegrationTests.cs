using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Application.Bridge;
using IssueLab.VoiceBench.Core;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace IssueLab.VoiceBench.Mcp.Tests;

public sealed class StdioIntegrationTests
{
    private static string Companion => Path.Combine(AppContext.BaseDirectory, "voicebench-mcp.dll");
    private static string Desktop => Path.Combine(AppContext.BaseDirectory, "VoiceBench.Mcp.Tests.exe");
    private static string UniquePipe() => "voicebench-mcp-test-" + Guid.NewGuid().ToString("N");

    private static Task<McpClient> ClientAsync(string pipeName, string? executable = null) => McpClient.CreateAsync(
        new StdioClientTransport(new()
        {
            Name = "Isolated VoiceBench test",
            Command = "dotnet",
            Arguments = [Companion, "--pipe-name", pipeName, "--app-executable", executable ?? Desktop],
            WorkingDirectory = Path.GetTempPath(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        }), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

    [Fact]
    public async Task InitializeAndDiscoveryDoNotLaunchDesktopAndAdvertiseEverySchema()
    {
        await using var client = await ClientAsync(UniquePipe(), Path.Combine(Path.GetTempPath(), "does-not-exist.exe"));
        var tools = await client.ListToolsAsync();
        Assert.Equal(AutomationCapabilities.All.Count, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "voicebench_editor_edit");
        var raw = await client.ListToolsAsync(new ListToolsRequestParams());
        Assert.All(raw.Tools, tool =>
        {
            Assert.NotNull(tool.Description);
            Assert.NotNull(tool.OutputSchema);
            Assert.NotNull(tool.Annotations);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        });
        Assert.Equal(10, (await client.ListResourcesAsync()).Count);
        Assert.Equal(4, (await client.ListResourceTemplatesAsync()).Count);
        Assert.Contains(tools, tool => tool.Name == "voicebench_mix_clip_split");
        Assert.Contains(tools, tool => tool.Name == "voicebench_assets_import");
    }

    [Fact]
    public async Task FirstCallLaunchesFakeDesktopAndPreservesUnicodeWithoutStdoutContamination()
    {
        await using var client = await ClientAsync(UniquePipe());
        var result = await client.CallToolAsync("voicebench_editor_replace", new Dictionary<string, object?>
        {
            ["expectedRevision"] = "r1", ["text"] = "Dodzi — 世界 🎙️ café\nline 2", ["requestId"] = "stable-write-1",
        });
        var data = result.StructuredContent!.Value.GetProperty("data");
        var processId = data.GetProperty("processId").GetInt32();
        try
        {
            Assert.False(result.IsError);
            Assert.Equal("Dodzi — 世界 🎙️ café\nline 2", data.GetProperty("arguments").GetProperty("text").GetString());
            Assert.Equal("stable-write-1", data.GetProperty("requestId").GetString());
            Assert.Single(result.Content.OfType<TextContentBlock>());
            var resource = await client.ReadResourceAsync("voicebench:///workspace");
            Assert.Contains("workspace.get", Assert.IsType<TextResourceContents>(resource.Contents.Single()).Text);
            var segment = await client.ReadResourceAsync("voicebench:///segments/test-segment/text");
            Assert.Contains("test-segment", Assert.IsType<TextResourceContents>(segment.Contents.Single()).Text);
            var page = await client.ReadResourceAsync("voicebench:///segments/test-segment/text?offset=10&count=20");
            var pageData = JsonDocument.Parse(Assert.IsType<TextResourceContents>(page.Contents.Single()).Text).RootElement;
            Assert.Equal(10, pageData.GetProperty("arguments").GetProperty("offset").GetInt32());
            Assert.Equal(20, pageData.GetProperty("arguments").GetProperty("count").GetInt32());
        }
        finally { KillTestDesktop(processId); }
    }

    [Fact]
    public async Task DesktopBusinessFailureIsStructuredToolError()
    {
        var pipe = UniquePipe();
        await using var server = new LocalControlServer(new TestDesktopProgram.EchoDispatcher(), new() { PipeName = pipe });
        await server.StartAsync();
        await using var client = await ClientAsync(pipe);
        var result = await client.CallToolAsync("voicebench_editor_replace", new Dictionary<string, object?> { ["expectedRevision"] = "stale", ["text"] = "draft" });
        Assert.True(result.IsError);
        Assert.Equal("revision_conflict", result.StructuredContent!.Value.GetProperty("code").GetString());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("voicebench_unknown").AsTask());
    }

    [Theory]
    [InlineData("voicebench_mix_clip_add","clip")]
    [InlineData("voicebench_mix_marker_set","marker")]
    [InlineData("voicebench_mix_track_create","track")]
    [InlineData("voicebench_mix_envelope_set","envelope")]
    public async Task TypedMixContractsKeepNestedSchemasAfterToolFieldFiltering(string tool,string field)
    {
        var pipe=UniquePipe();
        await using var server=new LocalControlServer(new TestDesktopProgram.EchoDispatcher(),new(){PipeName=pipe});
        await server.StartAsync();
        await using var client=await ClientAsync(pipe);
        object value=field switch
        {
            "clip"=>new AudioClipDocument { Anchor=new(){Kind=TimelineAnchorKind.ChapterStart,ChapterId="chapter"},Envelope=[new(0,-4),new(1,0)] },
            "marker"=>new TimelineMarker { Anchor=new(){Kind=TimelineAnchorKind.ChapterStart,ChapterId="chapter"} },
            "track"=>new AudioTrackDocument { Kind=AudioTrackKind.Music },
            _=>new[]{new VolumeEnvelopePoint(0,-4),new VolumeEnvelopePoint(1,0)},
        };
        var args=new Dictionary<string,object?>{["scope"]="Compilation",["expectedRevision"]=0,[field]=AutomationJson.Element(value)};
        if(field=="envelope")args["clipId"]=Guid.NewGuid();
        var result=await client.CallToolAsync(tool,args);
        Assert.False(result.IsError,result.StructuredContent?.GetRawText());
        Assert.Equal(JsonValueKind.Object,result.StructuredContent!.Value.GetProperty("data").GetProperty("arguments").ValueKind);
    }

    [Fact]
    public async Task ResourcesNotifyOnlyAfterObservedContentChangesAndUnsubscribe()
    {
        var pipe = UniquePipe();
        var dispatcher = new TestDesktopProgram.EchoDispatcher();
        await using var server = new LocalControlServer(dispatcher, new() { PipeName = pipe });
        await server.StartAsync();
        await using var client = await ClientAsync(pipe);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var subscription = await client.SubscribeToResourceAsync("voicebench:///workspace", (notification, cancellationToken) =>
        {
            if (notification.Uri == "voicebench:///workspace") changed.TrySetResult();
            return ValueTask.CompletedTask;
        }))
        {
            Assert.False(changed.Task.IsCompleted);
            dispatcher.Revision++;
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("voicebench_editor_replace", "{\"text\":\"draft\"}")]
    [InlineData("voicebench_editor_replace", "{\"expectedRevision\":\"r1\",\"text\":42}")]
    [InlineData("voicebench_editor_replace", "{\"expectedRevision\":\"r1\",\"text\":\"draft\",\"unknown\":true}")]
    [InlineData("voicebench_editor_edit", "{\"expectedRevision\":\"r1\",\"edits\":[{\"start\":-1,\"length\":0,\"text\":\"a\"}]}")]
    [InlineData("voicebench_editor_edit", "{\"expectedRevision\":\"r1\",\"edits\":[{\"start\":0,\"length\":1.5,\"text\":\"a\"}]}")]
    [InlineData("voicebench_render_start", "{\"segmentId\":\"s1\"}")]
    [InlineData("voicebench_workspace_get", "{\"requestId\":\"\"}")]
    public async Task MalformedToolParametersFailBeforeDesktopIsLaunched(string tool, string json)
    {
        await using var client = await ClientAsync(UniquePipe(), Path.Combine(Path.GetTempPath(), "does-not-exist.exe"));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError);
        Assert.Equal("invalid_arguments", result.StructuredContent!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task StdioCancellationPropagatesToDesktopDispatcher()
    {
        var pipe = UniquePipe();
        var dispatcher = new WaitingDispatcher();
        await using var server = new LocalControlServer(dispatcher, new() { PipeName = pipe });
        await server.StartAsync();
        await using var client = await ClientAsync(pipe);
        using var cancellation = new CancellationTokenSource();
        var requestId = new RequestId("cancel-isolated-request");
        var call = client.SendRequestAsync(new JsonRpcRequest
        {
            Id = requestId, Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new { name = "voicebench_workspace_get", arguments = new { } }),
        }, cancellation.Token);
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams { RequestId = requestId }, AutomationJson.Options);
        await dispatcher.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try { await call; } catch (Exception exception) when (exception is OperationCanceledException or McpProtocolException) { }
    }

    private sealed class WaitingDispatcher : IAutomationDispatcher
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return AutomationResult.Success(null);
        }
    }

    [Fact]
    public async Task AudioPreviewUsesAudioContentOnceAndLinksOriginalArtifactMetadata()
    {
        var pipe = UniquePipe();
        await using var server = new LocalControlServer(new PreviewDispatcher(), new() { PipeName = pipe });
        await server.StartAsync();
        await using var client = await ClientAsync(pipe);
        var result = await client.CallToolAsync("voicebench_artifacts_audio-preview", new Dictionary<string, object?>
        { ["relativePath"] = "04-generated-audio/segments/take 1.raw.wav", ["durationSeconds"] = 2 });
        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("data").TryGetProperty("audioBase64", out _));
        Assert.DoesNotContain("audioBase64", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.Single(result.Content.OfType<AudioContentBlock>()).DecodedData.ToArray());
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        var original = await client.ReadResourceAsync(link.Uri);
        Assert.Contains("take 1.raw.wav", Assert.IsType<TextResourceContents>(original.Contents.Single()).Text);
        await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("voicebench:///artifacts/..%2Fprivate.txt").AsTask());
    }

    private sealed class PreviewDispatcher : IAutomationDispatcher
    {
        public Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(
            request.Command == "artifacts.audio-preview"
                ? AutomationResult.Success(new { relativePath = "04-generated-audio/segments/take 1.raw.wav", mimeType = "audio/mpeg", audioBase64 = "AQIDBA==", startSeconds = 0, durationSeconds = 2 })
                : AutomationResult.Success(new[] { new { relativePath = "04-generated-audio/segments/take 1.raw.wav", bytes = 123456 } }));
    }

    [Fact]
    public async Task NestedTypedContractsAcceptNullableFieldsAndRejectWrongTypes()
    {
        var pipe = UniquePipe();
        await using var server = new LocalControlServer(new TestDesktopProgram.EchoDispatcher(), new() { PipeName = pipe });
        await server.StartAsync();
        await using var client = await ClientAsync(pipe);
        var valid = await client.CallToolAsync("voicebench_presets_save", new Dictionary<string, object?>
        { ["preset"] = new VoicePreset { PresetId = "p1", Name = "Narrator", ProviderId = "openai", ModelId = "test", Voice = "coral", Directions = null } });
        Assert.False(valid.IsError);
        var invalid = await client.CallToolAsync("voicebench_assembly_configure", new Dictionary<string, object?>
        { ["audioStandard"] = new { sampleRate = "not-a-number" } });
        Assert.True(invalid.IsError, AutomationCapabilities.All.Single(item => item.Command == "assembly.configure").InputSchema.GetRawText());
        Assert.Equal("invalid_arguments", invalid.StructuredContent!.Value.GetProperty("code").GetString());
    }

    private static void KillTestDesktop(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
