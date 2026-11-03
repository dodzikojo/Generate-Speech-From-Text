using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Application;

public sealed record AutomationJobSnapshot(Guid JobId, string Command, RenderJobStatus Status,
    DateTimeOffset CreatedAt, double Progress = 0, DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null, string? FailureCode = null, JsonElement? Result = null, Guid? RetryOf = null,
    string? FailureMessage = null);

public sealed partial class AutomationDispatcher
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellation = new();
    private readonly ConcurrentDictionary<Guid, AutomationJobSnapshot> _activeJobs = new();
    private readonly ConcurrentBag<Task> _jobTasks = [];
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly SemaphoreSlim _jobFiles = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Fingerprint, AutomationJobSnapshot Job)> _memoryRequests = new();
    public bool HasActiveJobs => _activeJobs.Values.Any(item => item.Status is RenderJobStatus.Queued or RenderJobStatus.InProgress);

    private static string JobDirectory(string root) => Path.Combine(root, ".voicebench", "automation", "jobs");
    private static string JobPath(string root, Guid id) => Path.Combine(JobDirectory(root), id.ToString("N") + ".json");
    // Request payloads are durable operation inputs, separate from progress/status journals and diagnostics.
    private static string JobRequestPath(string root, Guid id) => Path.Combine(root, ".voicebench", "automation", "requests", id.ToString("N") + ".json");
    private static string ReceiptPath(string root, string id) => Path.Combine(root, ".voicebench", "automation", "receipts", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    private sealed record DurableReceipt(string Fingerprint, AutomationResult? Result);
    private sealed record JobInput(string Command, JsonElement Arguments);
    private static string Fingerprint(AutomationRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Command + "\n" + Canonical(request.Arguments))));
    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => JsonSerializer.Serialize(item.Name) + ":" + Canonical(item.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };

    private async Task<AutomationJobSnapshot> StartJobAsync(string command, JsonElement args, string? requestId, Guid? retryOf, CancellationToken ct, string? jobRoot = null)
    {
        string root = jobRoot ?? ProjectRoot ?? (command == "toolchain.ensure" ? _stateDirectory : RequireRoot());
        string fingerprint = Fingerprint(new(command, args));
        if (requestId is not null && _memoryRequests.TryGetValue(root + ":" + requestId, out var previous))
        {
            if (previous.Fingerprint != fingerprint) throw new AutomationException("request.id-conflict", "This request ID belongs to different job arguments.");
            return await LoadJobAsync(JobPath(root, previous.Job.JobId), ct).ConfigureAwait(false);
        }
        Guid id = Guid.NewGuid();
        AutomationJobSnapshot job = new(id, command, RenderJobStatus.Queued, DateTimeOffset.UtcNow, RetryOf: retryOf);
        await WriteJsonAsync(JobRequestPath(root, id), new JobInput(command, args.Clone()), ct).ConfigureAwait(false);
        await SaveJobAsync(root, job, ct).ConfigureAwait(false);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeJobs[id] = job;
            if (requestId is not null) _memoryRequests[root + ":" + requestId] = (fingerprint, job);
            CancellationTokenSource cancellation = new();
            _jobCancellation[id] = cancellation;
            JsonElement durableArgs = args.Clone();
            _jobTasks.Add(Task.Run(() => RunJobAsync(root, job, durableArgs, cancellation), CancellationToken.None));
        }
        return job;
    }

    private async Task RunJobAsync(string root, AutomationJobSnapshot job, JsonElement args, CancellationTokenSource cancellation)
    {
        bool claimed = false;
        try
        {
            await _worker.WaitAsync(cancellation.Token).ConfigureAwait(false);
            claimed = true;
            job = job with { Status = RenderJobStatus.InProgress, StartedAt = DateTimeOffset.UtcNow, Progress = 0.05 };
            _activeJobs[job.JobId] = job;
            await SaveJobAsync(root, job, CancellationToken.None).ConfigureAwait(false);
            bool systemJob = job.Command == "toolchain.ensure" && string.Equals(root, _stateDirectory, StringComparison.OrdinalIgnoreCase);
            if (!systemJob && !string.Equals(root, ProjectRoot, StringComparison.OrdinalIgnoreCase)) throw new AutomationException("project.changed", "The job's project is no longer active.");
            object? result = job.Command.StartsWith("writing.", StringComparison.Ordinal)
                ? await ExecuteWritingAsync(job.Command, args, cancellation.Token).ConfigureAwait(false)
                : await DesktopAsync(job.Command is "assets.import" or "assets.prepare" or "assets.relink" or "mix.export" ? job.Command + ".execute" : job.Command, args, cancellation.Token).ConfigureAwait(false);
            // Synthesis may successfully retain audio while cancellation stops secondary word alignment.
            job = job with { Status = RenderJobStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, Progress = 1, Result = AutomationJson.Element(result) };
        }
        catch (OperationCanceledException)
        {
            job = job with { Status = RenderJobStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow, FailureCode = "operation.cancelled" };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AutomationResult failure = ToError(exception);
            job = job with { Status = RenderJobStatus.Failed, FinishedAt = DateTimeOffset.UtcNow, FailureCode = failure.Code, FailureMessage = failure.Message };
        }
        finally
        {
            if (claimed) _worker.Release();
            try { await SaveJobAsync(root, job, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Durable running state recovers as interrupted. */ }
            _activeJobs.TryRemove(job.JobId, out _);
            _jobCancellation.TryRemove(job.JobId, out _);
            cancellation.Dispose();
        }
    }

    private async Task<object> ExecuteJobsAsync(string command, JsonElement args, string? requestId, CancellationToken ct)
    {
        if (command == "jobs.list") return await GetJobsAsync(ct).ConfigureAwait(false);
        Guid id = Id(args, "jobId");
        string root = ProjectRoot is not null && File.Exists(JobPath(ProjectRoot, id)) ? ProjectRoot : _stateDirectory;
        string path = JobPath(root, id);
        if (!File.Exists(path)) throw new AutomationException("job.not-found", "This job does not exist in the active project.");
        AutomationJobSnapshot job = await LoadJobAsync(path, ct).ConfigureAwait(false);
        if (command == "jobs.get") return job;
        if (command == "jobs.wait")
        {
            int milliseconds = Math.Clamp(Int(args, "timeoutMilliseconds", 30000), 0, 30000);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            while (job.Status is RenderJobStatus.Queued or RenderJobStatus.InProgress && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds))), ct).ConfigureAwait(false);
                job = await LoadJobAsync(path, ct).ConfigureAwait(false);
            }
            return job;
        }
        if (command == "jobs.cancel")
        {
            if (_jobCancellation.TryGetValue(id, out var cancellation)) cancellation.Cancel();
            else if (job.Status is RenderJobStatus.Queued or RenderJobStatus.InProgress) throw new AutomationException("job.interrupted", "Reopen the project to recover this abandoned job.");
            return new { jobId = id, cancellationRequested = job.Status is RenderJobStatus.Queued or RenderJobStatus.InProgress, job };
        }
        if (job.Status is not (RenderJobStatus.Failed or RenderJobStatus.Interrupted or RenderJobStatus.Cancelled)) throw new AutomationException("job.not-retryable", "Only failed, interrupted or cancelled jobs can be explicitly retried.");
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? receiptPath = requestId is null ? null : ReceiptPath(_stateDirectory, requestId);
            string fingerprint = Fingerprint(new(command, args, requestId));
            if (receiptPath is not null && File.Exists(receiptPath))
            {
                DurableReceipt receipt = await ReadJsonAsync<DurableReceipt>(receiptPath, ct).ConfigureAwait(false);
                if (receipt.Fingerprint != fingerprint) throw new AutomationException("request.id-conflict", "This request ID belongs to different arguments.");
                if (receipt.Result is null) throw new AutomationException("request.interrupted", "The earlier retry was interrupted; inspect jobs before issuing a new request ID.");
                if (receipt.Result.IsError) throw new AutomationException(receipt.Result.Code!, receipt.Result.Message!);
                return receipt.Result.Data;
            }
            if (receiptPath is not null) await WriteJsonAsync(receiptPath, new DurableReceipt(fingerprint, null), ct).ConfigureAwait(false);
            JobInput input = await ReadJsonAsync<JobInput>(JobRequestPath(root, id), ct).ConfigureAwait(false);
            AutomationJobSnapshot retry = await StartJobAsync(input.Command, input.Arguments, null, id, ct, root).ConfigureAwait(false);
            if (receiptPath is not null) await WriteJsonAsync(receiptPath, new DurableReceipt(fingerprint, AutomationResult.Success(retry)), CancellationToken.None).ConfigureAwait(false);
            return retry;
        }
        finally { _requests.Release(); }
    }

    public async Task<IReadOnlyList<AutomationJobSnapshot>> GetJobsAsync(CancellationToken cancellationToken = default)
    {
        List<AutomationJobSnapshot> jobs = [];
        string[] roots = ProjectRoot is null ? [_stateDirectory] : [ProjectRoot, _stateDirectory];
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string directory = JobDirectory(root);
            if (Directory.Exists(directory)) foreach (string path in Directory.EnumerateFiles(directory, "*.json")) jobs.Add(await LoadJobAsync(path, cancellationToken).ConfigureAwait(false));
        }
        return jobs.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<int> RecoverJobsAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        string directory = JobDirectory(projectRoot);
        int count = 0;
        if (!Directory.Exists(directory)) return count;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            AutomationJobSnapshot job = await LoadJobAsync(path, cancellationToken).ConfigureAwait(false);
            if (job.Status is not (RenderJobStatus.Queued or RenderJobStatus.InProgress) || _activeJobs.ContainsKey(job.JobId)) continue;
            await SaveJobAsync(projectRoot, job with { Status = RenderJobStatus.Interrupted, FinishedAt = DateTimeOffset.UtcNow, FailureCode = "process.interrupted" }, cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private async Task SaveJobAsync(string root, AutomationJobSnapshot job, CancellationToken ct)
    {
        await _jobFiles.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteJsonAsync(JobPath(root, job.JobId), job, ct).ConfigureAwait(false); }
        finally { _jobFiles.Release(); }
    }

    private async Task<AutomationJobSnapshot> LoadJobAsync(string path, CancellationToken ct)
    {
        await _jobFiles.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadJsonAsync<AutomationJobSnapshot>(path, ct).ConfigureAwait(false); }
        finally { _jobFiles.Release(); }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(stream, AutomationJson.Options, ct).ConfigureAwait(false) ?? throw new InvalidDataException("The local automation document is invalid.");
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, AutomationJson.Options, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
