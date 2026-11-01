using System.Text.Json;
using IssueLab.VoiceBench.Application;
using Microsoft.UI.Dispatching;

namespace IssueLab.VoiceBench.App.Automation;

internal sealed class DesktopAutomationAdapter(MainWindow window) : IDesktopAutomationAdapter
{
    public Task<object?> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken = default) =>
        OnDispatcherAsync(window.DispatcherQueue,
            () => window.ExecuteDesktopAutomationAsync(command, arguments, cancellationToken), cancellationToken);

    internal static Task<T> OnDispatcherAsync<T>(DispatcherQueue queue, Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        if (queue.HasThreadAccess) return action();
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await action());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new AutomationException("desktop_unavailable", "VoiceBench is closing. Reopen it and retry."));
        return completion.Task;
    }
}
