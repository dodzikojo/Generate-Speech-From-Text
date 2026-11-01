using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Application.Bridge;
using Xunit;

namespace IssueLab.VoiceBench.Mcp.Tests;

public sealed class LocalControlBridgeTests
{
    private static LocalControlOptions Options() => new() { PipeName = "voicebench-test-" + Guid.NewGuid().ToString("N") };

    [Fact]
    public async Task RoundtripPreservesUnicodeRequestIdentityAndDesktopInstance()
    {
        var options = Options();
        await using var server = new LocalControlServer(new TestDesktopProgram.EchoDispatcher(), options);
        await server.StartAsync();
        await using var client = new LocalControlClient(options);
        var result = await client.ExecuteAsync(new("editor.replace", AutomationJson.Element(new { text = "Dodzi — 世界 🎙️ café\nline 2", expectedRevision = "r1" }), "write-1"));
        Assert.False(result.IsError, result.Message);
        Assert.Equal("Dodzi — 世界 🎙️ café\nline 2", result.Data.GetProperty("arguments").GetProperty("text").GetString());
        Assert.Equal("write-1", result.Data.GetProperty("requestId").GetString());
        Assert.Equal(server.InstanceId, client.InstanceId);
    }

    [Fact]
    public async Task DisabledEndpointReturnsActionableErrorWithoutDispatch()
    {
        var dispatcher = new CountingDispatcher();
        var options = Options() with { Enabled = false };
        await using var server = new LocalControlServer(dispatcher, options);
        await server.StartAsync();
        await using var client = new LocalControlClient(options);
        var result = await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })));
        Assert.True(result.IsError);
        Assert.Equal("local_control_disabled", result.Code);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task IndependentClientsCanOperateConcurrently()
    {
        var dispatcher = new CountingDispatcher();
        var options = Options();
        await using var server = new LocalControlServer(dispatcher, options);
        await server.StartAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            await using var client = new LocalControlClient(options);
            return await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { index })));
        }));
        Assert.All(results, result => Assert.False(result.IsError, result.Message));
        Assert.Equal(12, dispatcher.Calls);
    }

    [Fact]
    public async Task CancellationDisconnectsPipeAndCancelsDesktopRequest()
    {
        var dispatcher = new CancellableDispatcher();
        var options = Options();
        await using var server = new LocalControlServer(dispatcher, options);
        await server.StartAsync();
        await using var client = new LocalControlClient(options);
        using var cancellation = new CancellationTokenSource();
        var request = client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })), cancellation.Token);
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.ConnectedClientCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await dispatcher.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OversizedOrMalformedFrameCannotReachDispatcherAndServerRemainsUsable()
    {
        var dispatcher = new CountingDispatcher();
        var options = Options();
        await using var server = new LocalControlServer(dispatcher, options);
        await server.StartAsync();
        using (var attacker = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation))
        {
            await attacker.ConnectAsync(2000);
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, int.MaxValue);
            await attacker.WriteAsync(prefix);
            byte[] response = new byte[1];
            Assert.Equal(0, await attacker.ReadAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(0, dispatcher.Calls);
        await using var client = new LocalControlClient(options);
        Assert.False((await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })))).IsError);
    }

    [Fact]
    public async Task ServerDoesNotExposeInternalExceptionOrSubmittedText()
    {
        var options = Options();
        await using var server = new LocalControlServer(new ThrowingDispatcher(), options);
        await server.StartAsync();
        await using var client = new LocalControlClient(options);
        var result = await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })));
        Assert.Equal("internal_error", result.Code);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public async Task UnavailableDesktopWithoutLauncherReturnsError()
    {
        await using var client = new LocalControlClient(Options());
        Assert.Equal("desktop_unavailable", (await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })))).Code);
    }

    [Fact]
    public async Task LogicalClientRemainsVisibleBetweenRequestsAndDisconnectsOnDisposal()
    {
        var options = Options() with { ClientName = "Test automation client" };
        await using var server = new LocalControlServer(new CountingDispatcher(), options);
        await server.StartAsync();
        await using (var client = new LocalControlClient(options))
        {
            Assert.False((await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })))).IsError);
            Assert.Equal(1, server.ConnectedClientCount);
            Assert.Equal("Test automation client", Assert.Single(server.Connections).ClientName);
            Assert.False((await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })))).IsError);
            Assert.Equal(1, server.ConnectedClientCount);
        }
        Assert.Equal(0, server.ConnectedClientCount);
    }

    [Fact]
    public async Task SecondDesktopCannotClaimAnAlreadyOwnedPipeName()
    {
        var options = Options();
        await using var first = new LocalControlServer(new CountingDispatcher(), options);
        await first.StartAsync();
        await using var second = new LocalControlServer(new CountingDispatcher(), options);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => second.StartAsync());
    }

    [Fact]
    public async Task LocalControlCanBeDisabledAndReenabledWithoutReplacingServer()
    {
        var options = Options();
        await using var server = new LocalControlServer(new CountingDispatcher(), options);
        await server.StartAsync();
        await using var client = new LocalControlClient(options);
        server.Enabled = false;
        Assert.Equal("local_control_disabled", (await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })))).Code);
        server.Enabled = true;
        Assert.False((await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })))).IsError);
    }

    [Fact]
    public async Task OversizedResultReturnsSizeErrorInsteadOfLosingDesktopConnection()
    {
        var options = Options() with { MaximumMessageBytes = 1024 };
        await using var server = new LocalControlServer(new OversizedDispatcher(), options);
        await server.StartAsync();
        await using var client = new LocalControlClient(options);
        var result = await client.ExecuteAsync(new("workspace.get", AutomationJson.Element(new { })));
        Assert.True(result.IsError);
        Assert.Equal("message_too_large", result.Code);
    }

    private sealed class CountingDispatcher : IAutomationDispatcher
    {
        internal int Calls;
        public Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult(AutomationResult.Success(new { ok = true })); }
    }
    private sealed class ThrowingDispatcher : IAutomationDispatcher
    {
        public Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("secret submitted narration");
    }
    private sealed class OversizedDispatcher : IAutomationDispatcher
    {
        public Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutomationResult.Success(new { text = new string('x', 4096) }));
    }
    private sealed class CancellableDispatcher : IAutomationDispatcher
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
}
