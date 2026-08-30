using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.App.Services;

public sealed record AuditionRequest(
    string Slot,
    string ProviderId,
    string ModelId,
    string Voice,
    string Directions,
    string Text);

public sealed record AuditionResult(
    Guid RenderId,
    string AudioRelativePath,
    TimeSpan Duration,
    int SampleRate,
    int Channels);

public sealed class AppServices : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly ProviderCredentialResolver _credentialResolver;
    private readonly SpeechProviderRegistry _providers;
    private readonly ProcessRunner _processRunner = new();
    private readonly FfmpegToolchainManager _toolchainManager;
    private ToolchainPaths? _toolchain;

    public AppServices()
    {
        Projects = new FileProjectRepository();
        RecentProjects = new FileRecentProjectCatalog();
        Session = new WorkspaceSession(Projects, RecentProjects);
        _credentialResolver = new ProviderCredentialResolver(_credentialStore);
        _providers = new SpeechProviderRegistry(
        [
            new OpenAiSpeechProvider(_httpClient),
            new GeminiSpeechProvider(_httpClient),
        ]);
        _toolchainManager = new FfmpegToolchainManager(_httpClient);
    }

    public FileProjectRepository Projects { get; }

    public FileRecentProjectCatalog RecentProjects { get; }

    public WorkspaceSession Session { get; }

    public IReadOnlyCollection<ISpeechProvider> SpeechProviders => _providers.Providers;

    public Task<string?> GetCredentialAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        _credentialStore.GetAsync(providerId, cancellationToken);

    public Task SaveCredentialAsync(
        string providerId,
        string secret,
        CancellationToken cancellationToken = default) =>
        _credentialStore.SetAsync(providerId, secret, cancellationToken);

    public async Task<SegmentRenderResult> RenderSegmentAsync(
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        string root = Session.RequireProjectRoot();
        string queuePath = VoiceBenchProjectLayout.ResolveRelativePath(
            root,
            $"{VoiceBenchProjectLayout.ReportsDirectory}/render-queue.json");
        using FileRenderQueue queue = new(queuePath);
        await queue.RecoverInterruptedAsync(cancellationToken);
        SegmentRenderService service = new(
            Projects,
            _providers,
            _credentialResolver,
            queue,
            new ImmutableRenderStore());
        SegmentRenderResult result = await service.RenderAsync(root, segmentId, cancellationToken);
        await Session.RefreshAsync(cancellationToken);
        return result;
    }

    public async Task<RenderRecord> ProcessAndApproveLatestAsync(
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        string root = Session.RequireProjectRoot();
        VoiceBenchProjectDocument project = await Projects.LoadAsync(root, cancellationToken);
        SegmentDocument segment = project.Segments.Single(item => item.SegmentId == segmentId);
        RenderRecord render = segment.RenderHistory
            .Where(item => item.Status == RenderStatus.Generated)
            .OrderByDescending(item => item.TakeNumber)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Render this segment before approving a take.");

        ToolchainPaths toolchain = await EnsureFfmpegAsync(cancellationToken);
        RawAudioProcessingService processing = new(Projects, _processRunner);
        RenderRecord processed = await processing.ProcessAsync(
            root,
            segmentId,
            render.RenderId,
            toolchain,
            cancellationToken);

        project = await Projects.LoadAsync(root, cancellationToken);
        int segmentIndex = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        segment = project.Segments[segmentIndex];
        SegmentDocument approved = SegmentStateRules.ApproveRender(
            segment,
            processed.RenderId,
            DateTimeOffset.UtcNow);
        List<SegmentDocument> segments = [.. project.Segments];
        segments[segmentIndex] = approved;
        await Session.SaveAsync(project with { Segments = segments }, cancellationToken);
        return approved.RenderHistory.Single(item => item.RenderId == processed.RenderId);
    }

    public async Task<MasteringResult> BuildMasterAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string root = Session.RequireProjectRoot();
        ToolchainPaths toolchain = await EnsureFfmpegAsync(cancellationToken);
        FfmpegMasteringService mastering = new(Projects, _processRunner, toolchain);
        return await mastering.BuildAsync(new MasteringRequest(root, name), cancellationToken);
    }

    public async Task<ToolchainPaths> EnsureFfmpegAsync(CancellationToken cancellationToken = default)
    {
        if (_toolchain?.Exists == true)
        {
            return _toolchain;
        }

        _toolchain = await _toolchainManager.EnsureAsync(
            PinnedFfmpegRelease.WindowsX64,
            cancellationToken);
        return _toolchain;
    }

    public ToolchainPaths SelectExistingFfmpeg(string executablePath)
    {
        _toolchain = FfmpegToolchainManager.FromExistingExecutable(executablePath);
        return _toolchain;
    }

    public ToolchainPaths? FindInstalledFfmpeg()
    {
        if (_toolchain?.Exists == true) return _toolchain;
        _toolchain = _toolchainManager.FindInstalled(PinnedFfmpegRelease.WindowsX64);
        return _toolchain;
    }

    public async Task<AuditionResult> RenderAuditionAsync(
        AuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        string root = Session.RequireProjectRoot();
        ISpeechProvider provider = _providers.GetRequired(request.ProviderId);
        SpeechModelDescriptor model = provider.Descriptor.Models.SingleOrDefault(
            item => item.ModelId == request.ModelId)
            ?? throw new InvalidOperationException(
                $"Model '{request.ModelId}' is not available from {provider.Descriptor.DisplayName}.");
        if (!model.Voices.Contains(request.Voice, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Voice '{request.Voice}' is not available for model '{request.ModelId}'.");
        }

        int tokens = SegmentRenderService.EstimateInputTokens(request.Text, request.Directions);
        if (tokens > model.MaxInputTokens)
        {
            throw new InvalidOperationException(
                $"The audition exceeds the {model.MaxInputTokens:N0}-token limit for '{model.ModelId}'.");
        }

        string? apiKey = await _credentialResolver.ResolveAsync(request.ProviderId, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Add the {provider.Descriptor.DisplayName} API key in Settings before auditioning.");
        }

        SpeechSynthesisResult synthesis = await provider.SynthesizeAsync(
            new SpeechSynthesisRequest(
                request.ModelId,
                request.Voice,
                request.Text,
                request.Directions,
                tokens),
            apiKey,
            cancellationToken);

        Guid renderId = Guid.NewGuid();
        string slot = request.Slot.ToLowerInvariant() is "a" ? "a" : "b";
        string stem = $"audition-{slot}--{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}--{renderId:N}";
        string audioRelativePath = $"{VoiceBenchProjectLayout.AuditionsDirectory}/{stem}.raw.wav";
        string audioPath = VoiceBenchProjectLayout.ResolveRelativePath(root, audioRelativePath);
        await WriteNewFileAsync(audioPath, synthesis.Audio, cancellationToken);

        string copyRelativePath = $"{VoiceBenchProjectLayout.RenderCopiesDirectory}/{stem}.md";
        string copyPath = VoiceBenchProjectLayout.ResolveRelativePath(root, copyRelativePath);
        string renderCopy = $$"""
            # VoiceBench audition {{request.Slot.ToUpperInvariant()}}

            - Provider: `{{request.ProviderId}}`
            - Model: `{{request.ModelId}}`
            - Voice: `{{request.Voice}}`
            - Text SHA-256: `{{SourceTextHasher.Compute(request.Text)}}`

            ## Directions

            {{request.Directions}}

            ## Exact submitted text

            {{request.Text}}
            """;
        await WriteNewFileAsync(copyPath, Encoding.UTF8.GetBytes(renderCopy), cancellationToken);

        WaveFileMetadata wave = WaveFileInspector.Read(synthesis.Audio);
        return new AuditionResult(renderId, audioRelativePath, wave.Duration, wave.SampleRate, wave.Channels);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static async Task WriteNewFileAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
