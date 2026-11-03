using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

public sealed partial class AppServices
{
    /// <summary>Desktop buttons and MCP submit to the same durable queue; backend methods remain raw.</summary>
    public async Task<T> RunTrackedJobAsync<T>(string command, object arguments, CancellationToken cancellationToken = default)
    {
        AutomationDispatcher automation = Automation ?? throw new InvalidOperationException("The studio is still starting. Try again when local control is ready.");
        AutomationResult started = await automation.ExecuteAsync(new(command, AutomationJson.Element(arguments), "desktop-" + Guid.NewGuid().ToString("N")), cancellationToken);
        // The dispatcher reports cancellation as an error result; surface it as cancellation.
        if (started.IsError) { cancellationToken.ThrowIfCancellationRequested(); throw new AutomationException(started.Code!, started.Message!); }
        if (!started.Data.TryGetProperty("jobId", out JsonElement jobIdProperty))
            return started.Data.Deserialize<T>(AutomationJson.Options) ?? throw new InvalidDataException("The studio operation returned no result.");
        Guid jobId = jobIdProperty.GetGuid();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AutomationResult waited = await automation.ExecuteAsync(new("jobs.wait", AutomationJson.Element(new { jobId, timeoutMilliseconds = 1000 })), cancellationToken);
                // A cancelled wait comes back as an error result, so check the token to reach the
                // catch below, which cancels the running job instead of leaving it to finish.
                if (waited.IsError) { cancellationToken.ThrowIfCancellationRequested(); throw new AutomationException(waited.Code!, waited.Message!); }
                AutomationJobSnapshot job = waited.Data.Deserialize<AutomationJobSnapshot>(AutomationJson.Options)
                    ?? throw new InvalidDataException("The tracked studio job is unavailable.");
                switch (job.Status)
                {
                    case RenderJobStatus.Queued: case RenderJobStatus.InProgress: continue;
                    case RenderJobStatus.Completed:
                        if (job.Result is not JsonElement result) throw new InvalidDataException("The completed job returned no result.");
                        return result.Deserialize<T>(AutomationJson.Options) ?? throw new InvalidDataException("The completed job returned no result.");
                    case RenderJobStatus.Cancelled: throw new OperationCanceledException("The studio operation was cancelled.", cancellationToken);
                    default: throw new AutomationException(
                        job.FailureCode ?? "operation.failed",
                        string.IsNullOrWhiteSpace(job.FailureMessage)
                            ? $"The studio job stopped ({job.FailureCode ?? job.Status.ToString()}). Check its job record and the studio message before retrying."
                            : job.FailureMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await automation.ExecuteAsync(new("jobs.cancel", AutomationJson.Element(new { jobId })), CancellationToken.None);
            throw;
        }
    }
}
