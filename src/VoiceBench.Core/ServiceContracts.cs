namespace IssueLab.VoiceBench.Core;

public interface IProjectRepository
{
    Task<VoiceBenchProjectDocument> CreateAsync(
        string projectRoot,
        string name,
        CancellationToken cancellationToken = default);

    Task<VoiceBenchProjectDocument> LoadAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string projectRoot,
        VoiceBenchProjectDocument document,
        CancellationToken cancellationToken = default);

    Task<ProjectValidationResult> ValidateAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectValidationIssue(string Code, string Message, string? SegmentId = null);

public sealed record ProjectValidationResult(IReadOnlyList<ProjectValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ProjectValidationResult Valid { get; } = new([]);
}

public enum RenderJobStatus
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Interrupted,
    Cancelled,
}

public sealed record RenderQueueRequest(
    string ProjectRoot,
    string SegmentId,
    string ProviderId,
    string ModelId,
    string Voice,
    string RenderCopyRelativePath,
    int TakeNumber);

public sealed record RenderQueueJob(
    Guid JobId,
    RenderQueueRequest Request,
    RenderJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? RawAudioRelativePath = null,
    string? FailureCode = null);

public interface IRenderQueue
{
    Task<RenderQueueJob> EnqueueAsync(
        RenderQueueRequest request,
        CancellationToken cancellationToken = default);

    Task<RenderQueueJob?> ClaimNextAsync(CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid jobId,
        string rawAudioRelativePath,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        Guid jobId,
        string failureCode,
        bool cancelled = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RenderQueueJob>> GetJobsAsync(CancellationToken cancellationToken = default);

    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);

    Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public enum MasterScope
{
    Chapter,
    Compilation,
}

public sealed record MasteringRequest(
    string ProjectRoot,
    string Name,
    MasterScope Scope = MasterScope.Compilation,
    string? ChapterId = null);

public sealed record MasteringResult(
    string MasterRelativePath,
    string ReportRelativePath,
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    double IntegratedLufs,
    double TruePeakDbtp);

public interface IAudioMasteringService
{
    Task<MasteringResult> BuildAsync(
        MasteringRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ToolchainResolution(
    string ToolName,
    string? ExecutablePath,
    string? Version,
    bool IsManaged,
    bool IsAvailable);

public interface IToolchainManager
{
    Task<ToolchainResolution> ResolveAsync(
        string toolName,
        CancellationToken cancellationToken = default);

    Task<ToolchainResolution> EnsureAsync(
        string toolName,
        string? existingExecutablePath = null,
        CancellationToken cancellationToken = default);
}
