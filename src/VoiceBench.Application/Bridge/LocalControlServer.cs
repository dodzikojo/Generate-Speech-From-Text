using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace IssueLab.VoiceBench.Application.Bridge;

/// <summary>Async, same-user desktop endpoint. No application work runs on the pipe accept loop.</summary>
public sealed class LocalControlServer(IAutomationDispatcher dispatcher, LocalControlOptions? options = null) : IAsyncDisposable
{
    private readonly LocalControlOptions options = options ?? new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, LocalControlConnection> connections = new();
    private readonly ConcurrentDictionary<string, byte> activeRequests = new();
    private readonly ConcurrentDictionary<string, Task> handlers = new();
    private Task? acceptLoop;
    private bool disposed;
    private int enabled = (options?.Enabled ?? true) ? 1 : 0;

    public string InstanceId => options.InstanceId;
    public string PipeName => options.PipeName;
    public bool Enabled { get => Volatile.Read(ref enabled) != 0; set => Volatile.Write(ref enabled, value ? 1 : 0); }
    public int ConnectedClientCount => Connections.Count;
    public int ActiveRequestCount => activeRequests.Count;
    public IReadOnlyList<LocalControlConnection> Connections
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-60);
            foreach (var connection in connections)
                if (connection.Value.LastSeenAt < cutoff) connections.TryRemove(connection.Key, out _);
            return connections.Values.ToArray();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (acceptLoop is not null) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var first = LocalControlSecurity.CreateServer(options.PipeName, firstInstance: true);
        acceptLoop = AcceptAsync(first, lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(NamedPipeServerStream first, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? listener = first;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(cancellationToken);
                while (handlers.Count >= 14) await Task.Delay(20, cancellationToken);
                var connected = listener;
                // Keep a native instance continuously alive so another desktop cannot claim the name
                // between two short requests, even if a handler completes synchronously.
                listener = LocalControlSecurity.CreateServer(options.PipeName);
                var id = Guid.NewGuid().ToString("N");
                // Register before processing so disposal can await every accepted connection.
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                handlers[id] = completion.Task;
                _ = HandleAndCompleteAsync(connected, id, completion, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { listener?.Dispose(); }
    }

    private async Task HandleAndCompleteAsync(NamedPipeServerStream pipe, string id, TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try { await HandleAsync(pipe, id, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
        finally { activeRequests.TryRemove(id, out _); handlers.TryRemove(id, out _); completion.TrySetResult(); pipe.Dispose(); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, string id, CancellationToken cancellationToken)
    {
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var hello = await LocalControlFraming.ReadAsync<BridgeHello>(pipe, 1024, handshakeTimeout.Token);
        LocalControlSecurity.VerifyClient(pipe);
        if (hello.Protocol != LocalControlFraming.Protocol || hello.Version != LocalControlFraming.Version)
            throw new InvalidDataException("Incompatible local-control protocol.");
        if (hello.ClientId?.Length > 100 || hello.ClientName?.Length > 100) throw new InvalidDataException("Invalid client identity.");
        var clientId = hello.ClientId ?? id;
        if (hello.Mode == "disconnect") connections.TryRemove(clientId, out _);
        else
        {
            var now = DateTimeOffset.UtcNow;
            connections.AddOrUpdate(clientId, new LocalControlConnection(clientId, options.InstanceId, now, hello.ClientName, now),
                (_, previous) => previous with { LastSeenAt = now, ClientName = hello.ClientName });
        }
        await LocalControlFraming.WriteAsync(pipe, new BridgeWelcome(LocalControlFraming.Protocol,
            LocalControlFraming.Version, options.InstanceId, options.MaximumMessageBytes, Enabled), 1024, handshakeTimeout.Token);
        if (hello.Mode is "heartbeat" or "disconnect") return;
        if (hello.Mode != "request") throw new InvalidDataException("Unknown local-control connection mode.");
        var request = await LocalControlFraming.ReadAsync<AutomationRequest>(pipe, options.MaximumMessageBytes, handshakeTimeout.Token);
        activeRequests[id] = 0;
        if (!Enabled)
        {
            await LocalControlFraming.WriteAsync(pipe, AutomationResult.Error("local_control_disabled", "Local control is disabled in VoiceBench Settings."), options.MaximumMessageBytes, cancellationToken);
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnect = MonitorDisconnectAsync(pipe, operation, monitor.Token);
        AutomationResult result;
        try { result = await dispatcher.ExecuteAsync(request, operation.Token); }
        catch (AutomationException exception) { result = AutomationResult.Error(exception.Code, exception.Message, exception.Details); }
        catch (OperationCanceledException) { result = AutomationResult.Error("cancelled", "The local-control request was cancelled."); }
        catch (Exception) { result = AutomationResult.Error("internal_error", "VoiceBench could not complete the request. Inspect the desktop app for details."); }
        try
        {
            try { await LocalControlFraming.WriteAsync(pipe, result, options.MaximumMessageBytes, cancellationToken); }
            catch (InvalidDataException)
            {
                await LocalControlFraming.WriteAsync(pipe, AutomationResult.Error("message_too_large",
                    "The result exceeds the local-control message limit. Use a paginated text or artifact read, or request a smaller scope."), options.MaximumMessageBytes, cancellationToken);
            }
        }
        finally { await monitor.CancelAsync(); await disconnect; }
    }

    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource operation, CancellationToken cancellationToken)
    {
        try
        {
            byte[] probe = new byte[1];
            _ = await pipe.ReadAsync(probe, cancellationToken);
            await operation.CancelAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { await operation.CancelAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        if (acceptLoop is not null) await acceptLoop;
        await Task.WhenAll(handlers.Values);
        lifetime.Dispose();
    }
}
