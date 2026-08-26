using System.Text.Json;
using System.Text.Json.Serialization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Rendering;

public sealed class FileRenderQueue : IRenderQueue, IDisposable
{
    private readonly string _journalPath;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _createId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRenderQueue(
        string journalPath,
        TimeProvider? timeProvider = null,
        Func<Guid>? createId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        _journalPath = Path.GetFullPath(journalPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createId = createId ?? Guid.NewGuid;
    }

    public async Task<RenderQueueJob> EnqueueAsync(
        RenderQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        return await MutateAsync(jobs =>
        {
            var job = new RenderQueueJob(
                _createId(),
                request,
                RenderJobStatus.Queued,
                _timeProvider.GetUtcNow());
            jobs.Add(job);
            return job;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<RenderQueueJob?> ClaimNextAsync(CancellationToken cancellationToken = default) =>
        MutateAsync<RenderQueueJob?>(jobs =>
        {
            var index = jobs.FindIndex(job => job.Status == RenderJobStatus.Queued);
            if (index < 0)
            {
                return null;
            }

            var claimed = jobs[index] with
            {
                Status = RenderJobStatus.InProgress,
                StartedAt = _timeProvider.GetUtcNow(),
                FinishedAt = null,
                FailureCode = null,
            };
            jobs[index] = claimed;
            return claimed;
        }, cancellationToken);

    public Task CompleteAsync(
        Guid jobId,
        string rawAudioRelativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateRelativePath(rawAudioRelativePath, nameof(rawAudioRelativePath));
        return MutateAsync<object?>(jobs =>
        {
            var index = GetRequiredJobIndex(jobs, jobId);
            EnsureStatus(jobs[index], RenderJobStatus.InProgress);
            jobs[index] = jobs[index] with
            {
                Status = RenderJobStatus.Completed,
                FinishedAt = _timeProvider.GetUtcNow(),
                RawAudioRelativePath = NormalizeRelativePath(rawAudioRelativePath),
                FailureCode = null,
            };
            return null;
        }, cancellationToken);
    }

    public Task FailAsync(
        Guid jobId,
        string failureCode,
        bool cancelled = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return MutateAsync<object?>(jobs =>
        {
            var index = GetRequiredJobIndex(jobs, jobId);
            EnsureStatus(jobs[index], RenderJobStatus.InProgress);
            jobs[index] = jobs[index] with
            {
                Status = cancelled ? RenderJobStatus.Cancelled : RenderJobStatus.Failed,
                FinishedAt = _timeProvider.GetUtcNow(),
                FailureCode = failureCode,
            };
            return null;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RenderQueueJob>> GetJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return jobs.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(jobs =>
        {
            var recovered = 0;
            for (var index = 0; index < jobs.Count; index++)
            {
                if (jobs[index].Status != RenderJobStatus.InProgress)
                {
                    continue;
                }

                jobs[index] = jobs[index] with
                {
                    Status = RenderJobStatus.Interrupted,
                    FinishedAt = _timeProvider.GetUtcNow(),
                    FailureCode = "process_interrupted",
                };
                recovered++;
            }

            return recovered;
        }, cancellationToken);

    public Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        MutateAsync<object?>(jobs =>
        {
            var index = GetRequiredJobIndex(jobs, jobId);
            var job = jobs[index];
            if (job.Status is not (RenderJobStatus.Interrupted or RenderJobStatus.Failed or RenderJobStatus.Cancelled))
            {
                throw new InvalidOperationException(
                    $"Render job '{jobId}' cannot be retried from {job.Status}.");
            }

            jobs[index] = job with
            {
                Status = RenderJobStatus.Queued,
                StartedAt = null,
                FinishedAt = null,
                RawAudioRelativePath = null,
                FailureCode = null,
            };
            return null;
        }, cancellationToken);

    public void Dispose() => _gate.Dispose();

    private async Task<T> MutateAsync<T>(Func<List<RenderQueueJob>, T> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var result = mutation(jobs);
            await SaveAsync(jobs, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<RenderQueueJob>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                _journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<RenderQueueJob>>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The render queue journal is not valid JSON.", exception);
        }
    }

    private async Task SaveAsync(List<RenderQueueJob> jobs, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_journalPath)
            ?? throw new InvalidOperationException("The render queue journal must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_journalPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, jobs, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _journalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static int GetRequiredJobIndex(IReadOnlyList<RenderQueueJob> jobs, Guid jobId)
    {
        for (var index = 0; index < jobs.Count; index++)
        {
            if (jobs[index].JobId == jobId)
            {
                return index;
            }
        }

        throw new KeyNotFoundException($"Render job '{jobId}' was not found.");
    }

    private static void EnsureStatus(RenderQueueJob job, RenderJobStatus required)
    {
        if (job.Status != required)
        {
            throw new InvalidOperationException(
                $"Render job '{job.JobId}' must be {required}, but is {job.Status}.");
        }
    }

    private static void ValidateRequest(RenderQueueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SegmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Voice);
        ValidateRelativePath(request.RenderCopyRelativePath, nameof(request.RenderCopyRelativePath));

        if (request.TakeNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Take number must be positive.");
        }
    }

    private static void ValidateRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (Path.IsPathRooted(path)
            || path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("Queue paths must be project-relative and cannot traverse upward.", parameterName);
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
