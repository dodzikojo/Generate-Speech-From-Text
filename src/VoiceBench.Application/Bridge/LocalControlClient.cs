using System.IO.Pipes;
using System.ComponentModel;
using System.Security.Principal;

namespace IssueLab.VoiceBench.Application.Bridge;

/// <summary>Connects on demand; construction and capability discovery never launch the desktop.</summary>
public sealed class LocalControlClient(LocalControlOptions? options = null, Func<CancellationToken, Task>? launchDesktop = null) : IAutomationDispatcher, IAsyncDisposable
{
    private readonly LocalControlOptions options = options ?? new();
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string clientId = Guid.NewGuid().ToString("N");
    private Task? heartbeat;
    private bool disposed;
    public string? InstanceId { get; private set; }

    public async Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        options.Validate();
        try
        {
            using var pipe = await ConnectAsync(cancellationToken);
            LocalControlSecurity.VerifyServer(pipe);
            await LocalControlFraming.WriteAsync(pipe, Hello(), 1024, cancellationToken);
            var welcome = await LocalControlFraming.ReadAsync<BridgeWelcome>(pipe, 1024, cancellationToken);
            if (welcome.Protocol != LocalControlFraming.Protocol || welcome.Version != LocalControlFraming.Version)
                return AutomationResult.Error("protocol_mismatch", "The installed VoiceBench app uses an incompatible local-control protocol.");
            InstanceId = welcome.InstanceId;
            lock (lifetime) heartbeat ??= HeartbeatAsync(lifetime.Token);
            // Closing this one-operation pipe propagates MCP request cancellation to the desktop dispatcher.
            using var closeOnCancel = cancellationToken.Register(pipe.Dispose);
            await LocalControlFraming.WriteAsync(pipe, request, Math.Min(options.MaximumMessageBytes, welcome.MaximumMessageBytes), cancellationToken);
            return await LocalControlFraming.ReadAsync<AutomationResult>(pipe, options.MaximumMessageBytes, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (UnauthorizedAccessException) { return AutomationResult.Error("access_denied", "Run VoiceBench and the MCP companion as the same Windows user and at the same elevation level."); }
        catch (InvalidDataException) { return AutomationResult.Error("invalid_message", "The local-control message is invalid or exceeds its size limit."); }
        catch (IOException) { return AutomationResult.Error("desktop_unavailable", "VoiceBench is not connected. Open the desktop app and enable local control in Settings."); }
        catch (TimeoutException) { return AutomationResult.Error("desktop_unavailable", "VoiceBench did not become ready. Open the desktop app and enable local control in Settings."); }
        catch (Win32Exception) { return AutomationResult.Error("desktop_unavailable", "VoiceBench could not launch. Verify the installed desktop app or configured executable."); }
    }

    private NamedPipeClientStream NewPipe() => new(".", options.PipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Impersonation);

    private async Task<NamedPipeClientStream?> TryConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = NewPipe();
        try { await pipe.ConnectAsync(200, cancellationToken); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); return null; }
        catch { pipe.Dispose(); throw; }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = await TryConnectAsync(cancellationToken);
        if (pipe is not null) return pipe;
        if (launchDesktop is null) throw new TimeoutException();
        await launchGate.WaitAsync(cancellationToken);
        try
        {
            pipe = await TryConnectAsync(cancellationToken);
            if (pipe is not null) return pipe;
            await launchDesktop(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.LaunchTimeout);
            try
            {
                while (true)
                {
                    pipe = await TryConnectAsync(timeout.Token);
                    if (pipe is not null) return pipe;
                    await Task.Delay(100, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException(); }
        }
        finally { launchGate.Release(); }
    }

    private BridgeHello Hello(string mode = "request") => new(LocalControlFraming.Protocol, LocalControlFraming.Version, clientId, options.ClientName, mode);

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await SignalAsync("heartbeat", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SignalAsync(string mode, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var pipe = await TryConnectAsync(timeout.Token);
            if (pipe is null) return;
            LocalControlSecurity.VerifyServer(pipe);
            await LocalControlFraming.WriteAsync(pipe, Hello(mode), 1024, timeout.Token);
            _ = await LocalControlFraming.ReadAsync<BridgeWelcome>(pipe, 1024, timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        if (heartbeat is not null) { await heartbeat; await SignalAsync("disconnect", CancellationToken.None); }
        lifetime.Dispose();
        launchGate.Dispose();
    }
}
