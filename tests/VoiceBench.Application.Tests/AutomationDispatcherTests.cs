using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using Xunit;

namespace IssueLab.VoiceBench.Application.Tests;

public sealed class AutomationDispatcherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "voicebench-automation-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FileProjectRepository _repository = new();
    private string ProjectRoot => Path.Combine(_directory, "project");
    private string StateRoot => Path.Combine(_directory, "state");
    private AutomationDispatcher Create(IDesktopAutomationAdapter? desktop = null, IWritingAssistantService? writing = null) => new(desktop, _repository,
        new FileRecentProjectCatalog(catalogPath: Path.Combine(_directory, "recent.json")), writingAssistant: writing, automationStateDirectory: StateRoot);
    private static Task<AutomationResult> Call(AutomationDispatcher dispatcher, string command, object? arguments = null, string? requestId = null) => dispatcher.ExecuteAsync(new(command, AutomationJson.Element(arguments ?? new { }), requestId));
    private async Task CreateProject(AutomationDispatcher dispatcher) => Assert.False((await Call(dispatcher, "project.create", new { projectRoot = ProjectRoot, name = "Test" })).IsError);

    [Fact]
    public async Task CreateRequestIsDurableDeduplicatedAndRestoresProjectAfterRestart()
    {
        AutomationResult first;
        using (AutomationDispatcher dispatcher = Create())
        {
            first = await Call(dispatcher, "project.create", new { projectRoot = ProjectRoot, name = "Test" }, "create-request");
            Assert.False(first.IsError);
        }
        using AutomationDispatcher restarted = Create();
        var repeated = await Call(restarted, "project.create", new { name = "Test", projectRoot = ProjectRoot }, "create-request");
        Assert.False(repeated.IsError);
        Assert.True(JsonElement.DeepEquals(first.Data, repeated.Data));
        Assert.Equal(ProjectRoot, restarted.ProjectRoot);
        Assert.Equal("request.id-conflict", (await Call(restarted, "project.create", new { name = "Different", projectRoot = ProjectRoot }, "create-request")).Code);
    }

    [Fact]
    public async Task ChapterSegmentChangesAndDeleteAreUndoable()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        Assert.False((await Call(dispatcher, "chapter.create", new { title = "Opening" })).IsError);
        string chapter = dispatcher.Project!.Chapters.Single().ChapterId;
        Assert.False((await Call(dispatcher, "segment.create", new { chapterId = chapter, title = "Part two", text = "Narration\n\n@visual: Show the page" })).IsError);
        SegmentDocument segment = dispatcher.Project!.Segments.Last();
        var script = await Call(dispatcher, "script.get", new { segmentId = segment.SegmentId });
        Assert.Equal("Narration", script.Data.GetProperty("parsed").GetProperty("narrationText").GetString());
        Assert.Single(script.Data.GetProperty("parsed").GetProperty("cues").EnumerateArray());
        Assert.False((await Call(dispatcher, "segment.delete", new { segmentId = segment.SegmentId })).IsError);
        Assert.Single(dispatcher.Project!.Segments);
        Assert.False((await Call(dispatcher, "history.undo")).IsError);
        Assert.Equal(2, dispatcher.Project!.Segments.Count);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(ProjectRoot, segment.ScriptRelativePath)));
        Assert.False((await Call(dispatcher, "project.validate")).IsError);
    }

    [Fact]
    public async Task LeaseBlocksOtherProcessesUntilClose()
    {
        using AutomationDispatcher owner = Create();
        await CreateProject(owner);
        Assert.Equal("project.in-use", Assert.Throws<AutomationException>(() => ProjectLease.Acquire(ProjectRoot)).Code);
        Assert.False((await Call(owner, "project.close")).IsError);
        using ProjectLease next = ProjectLease.Acquire(ProjectRoot);
        Assert.Equal(ProjectRoot, next.ProjectRoot);
    }

    [Fact]
    public async Task ShutdownRetainsLeaseUntilAnIgnoringCancellationJobActuallyExits()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeDesktop desktop = new() { Handler = async (_, _, _) => { entered.TrySetResult(); await finish.Task; return new { audioPreserved = true }; } };
        AutomationDispatcher dispatcher = Create(desktop);
        try
        {
            await CreateProject(dispatcher);
            Assert.False((await Call(dispatcher, "render.start", new { segmentId = "slow" }, "slow-job")).IsError);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Dispose();
            Task shutdown = dispatcher.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.Equal("project.in-use", Assert.Throws<AutomationException>(() => ProjectLease.Acquire(ProjectRoot)).Code);
            finish.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            using ProjectLease acquired = ProjectLease.Acquire(ProjectRoot);
        }
        finally
        {
            finish.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpeningSameRootWithTrailingSeparatorKeepsOwnership()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        var reopened = await Call(dispatcher, "project.open", new { projectRoot = ProjectRoot + Path.DirectorySeparatorChar });
        Assert.False(reopened.IsError);
        Assert.Equal(ProjectRoot, dispatcher.ProjectRoot);
        dispatcher.BindProject(ProjectRoot, dispatcher.Project);
        Assert.Equal("project.in-use", Assert.Throws<AutomationException>(() => ProjectLease.Acquire(ProjectRoot)).Code);
    }

    [Fact]
    public async Task RequestDeduplicationDoesNotRepeatAuthoringTransaction()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        var one = await Call(dispatcher, "chapter.create", new { title = "Single" }, "one");
        var two = await Call(dispatcher, "chapter.create", new { title = "Single" }, "one");
        Assert.False(one.IsError);
        Assert.True(JsonElement.DeepEquals(one.Data, two.Data));
        Assert.Single(dispatcher.Project!.Chapters);
        var history = await Call(dispatcher, "history.get");
        Assert.Single(history.Data.GetProperty("transactions").EnumerateArray());
    }

    [Fact]
    public async Task EditorCommandsUseTheLiveDesktopAndRejectRawCredentials()
    {
        FakeDesktop desktop = new();
        using AutomationDispatcher dispatcher = Create(desktop);
        var result = await Call(dispatcher, "editor.replace", new { expectedRevision = "revision", text = "content" });
        Assert.False(result.IsError);
        Assert.Equal("editor.replace", desktop.LastCommand);
        Assert.Equal("content", desktop.LastArguments.GetProperty("text").GetString());
        Assert.Equal("arguments.invalid", (await Call(dispatcher, "credentials.open", new { providerId = "openai", secret = "never-accepted" })).Code);
        Assert.Equal("command.unknown", (await Call(dispatcher, "credentials.get")).Code);
        Assert.Equal("arguments.invalid", (await Call(dispatcher, "presets.save", new { preset = new { presetId = "p", name = "p", providerId = "openai", modelId = "gpt-4o-mini-tts", voice = "alloy", secret = "never-accepted" } })).Code);
    }

    [Fact]
    public async Task JobsRemainCancellableAndBlockProjectSwitches()
    {
        FakeDesktop desktop = new() { Handler = async (_, _, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; } };
        using AutomationDispatcher dispatcher = Create(desktop);
        await CreateProject(dispatcher);
        var started = await Call(dispatcher, "render.start", new { segmentId = "s" }, "render-one");
        Assert.False(started.IsError);
        string jobId = started.Data.GetProperty("jobId").GetString()!;
        Assert.Equal("project.jobs-active", (await Call(dispatcher, "project.close")).Code);
        Assert.False((await Call(dispatcher, "jobs.cancel", new { jobId })).IsError);
        var done = await Call(dispatcher, "jobs.wait", new { jobId, timeoutMilliseconds = 2000 });
        Assert.Equal("Cancelled", done.Data.GetProperty("status").GetString());
        Assert.Equal(1, desktop.Calls);
    }

    [Fact]
    public async Task FailedJobNeedsExplicitRetryAndRetainsItsOriginalIdentity()
    {
        FakeDesktop desktop = new() { Handler = (_, _, _) => throw new AutomationException("test.failure", "failure") };
        using AutomationDispatcher dispatcher = Create(desktop);
        await CreateProject(dispatcher);
        var started = await Call(dispatcher, "render.start", new { segmentId = "s" }, "render-failure");
        string id = started.Data.GetProperty("jobId").GetString()!;
        var failed = await Call(dispatcher, "jobs.wait", new { jobId = id, timeoutMilliseconds = 2000 });
        Assert.Equal("Failed", failed.Data.GetProperty("status").GetString());
        Assert.Equal("test.failure", failed.Data.GetProperty("failureCode").GetString());
        var duplicate = await Call(dispatcher, "render.start", new { segmentId = "s" }, "render-failure");
        Assert.Equal(id, duplicate.Data.GetProperty("jobId").GetString());
        Assert.Equal(1, desktop.Calls);
        var retry = await Call(dispatcher, "jobs.retry", new { jobId = id }, "retry-one");
        string retryId = retry.Data.GetProperty("jobId").GetString()!;
        Assert.NotEqual(id, retryId);
        Assert.Equal(id, retry.Data.GetProperty("retryOf").GetString());
        var retried = await Call(dispatcher, "jobs.wait", new { jobId = retryId, timeoutMilliseconds = 2000 });
        Assert.Equal("Failed", retried.Data.GetProperty("status").GetString());
        Assert.Equal("test.failure", retried.Data.GetProperty("failureCode").GetString());
        Assert.Equal(2, desktop.Calls);
        var duplicateRetry = await Call(dispatcher, "jobs.retry", new { jobId = id }, "retry-one");
        Assert.Equal(retryId, duplicateRetry.Data.GetProperty("jobId").GetString());
        Assert.Equal(2, desktop.Calls);
    }

    [Fact]
    public async Task ProductionExportUsesTheSharedDurableJobQueue()
    {
        FakeDesktop desktop = new();
        using AutomationDispatcher dispatcher = Create(desktop);
        await CreateProject(dispatcher);
        var started = await Call(dispatcher, "exports.create", new { name = "production" }, "export-once");
        Assert.False(started.IsError);
        string jobId = started.Data.GetProperty("jobId").GetString()!;
        var done = await Call(dispatcher, "jobs.wait", new { jobId, timeoutMilliseconds = 2000 });
        Assert.Equal("Completed", done.Data.GetProperty("status").GetString());
        Assert.Equal("exports.create", done.Data.GetProperty("result").GetProperty("command").GetString());
        var replay = await Call(dispatcher, "exports.create", new { name = "production" }, "export-once");
        Assert.Equal(jobId, replay.Data.GetProperty("jobId").GetString());
        Assert.Equal(1, desktop.Calls);
    }

    [Fact]
    public async Task ToolchainInstallWithoutProjectUsesDurableSystemJobs()
    {
        FakeDesktop desktop = new();
        string jobId;
        using (AutomationDispatcher dispatcher = Create(desktop))
        {
            var started = await Call(dispatcher, "toolchain.ensure", requestId: "install-tools");
            Assert.False(started.IsError);
            jobId = started.Data.GetProperty("jobId").GetString()!;
            var done = await Call(dispatcher, "jobs.wait", new { jobId, timeoutMilliseconds = 2000 });
            Assert.Equal("Completed", done.Data.GetProperty("status").GetString());
            Assert.Equal("toolchain.ensure", done.Data.GetProperty("result").GetProperty("command").GetString());
            Assert.Null(dispatcher.ProjectRoot);
            Assert.Single((await Call(dispatcher, "jobs.list")).Data.EnumerateArray());
            Assert.Equal("project.required", (await Call(dispatcher, "render.start", new { segmentId = "s" })).Code);
        }
        using AutomationDispatcher restarted = Create(desktop);
        var replay = await Call(restarted, "toolchain.ensure", requestId: "install-tools");
        Assert.Equal(jobId, replay.Data.GetProperty("jobId").GetString());
        Assert.Equal("Completed", (await Call(restarted, "jobs.get", new { jobId })).Data.GetProperty("status").GetString());
        Assert.Equal(1, desktop.Calls);
    }

    [Fact]
    public async Task AbandonedSystemJobsRecoverOnFirstRequestWithoutRunning()
    {
        Guid id = Guid.NewGuid();
        string directory = Path.Combine(StateRoot, ".voicebench", "automation", "jobs");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id.ToString("N") + ".json"), JsonSerializer.Serialize(new AutomationJobSnapshot(id, "toolchain.ensure", RenderJobStatus.InProgress, DateTimeOffset.UtcNow), AutomationJson.Options));
        FakeDesktop desktop = new();
        using AutomationDispatcher dispatcher = Create(desktop);
        var job = await Call(dispatcher, "jobs.get", new { jobId = id });
        Assert.Equal("Interrupted", job.Data.GetProperty("status").GetString());
        Assert.Equal(0, desktop.Calls);
    }

    [Fact]
    public async Task AbandonedJobsRecoverAsInterruptedAndNeverRunAutomatically()
    {
        using AutomationDispatcher dispatcher = Create(new FakeDesktop());
        await CreateProject(dispatcher);
        Guid id = Guid.NewGuid();
        string directory = Path.Combine(ProjectRoot, ".voicebench", "automation", "jobs");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id.ToString("N") + ".json"), JsonSerializer.Serialize(new AutomationJobSnapshot(id, "render.start", RenderJobStatus.InProgress, DateTimeOffset.UtcNow), AutomationJson.Options));
        Assert.Equal(1, await dispatcher.RecoverJobsAsync(ProjectRoot));
        var job = await Call(dispatcher, "jobs.get", new { jobId = id });
        Assert.Equal("Interrupted", job.Data.GetProperty("status").GetString());
        Assert.False(dispatcher.HasActiveJobs);
    }

    [Fact]
    public async Task ArtifactReadsRejectTraversalAndCanonicalScripts()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        Assert.True((await Call(dispatcher, "artifacts.read", new { relativePath = "07-reports/../../outside.txt" })).IsError);
        Assert.Equal("artifact.path-invalid", (await Call(dispatcher, "artifacts.read", new { relativePath = "02-scripts/file.md" })).Code);
        await File.WriteAllTextAsync(Path.Combine(ProjectRoot, "07-reports", "result.json"), "{\"safe\":true}");
        var artifact = await Call(dispatcher, "artifacts.read", new { relativePath = "07-reports/result.json" });
        Assert.False(artifact.IsError);
        Assert.Equal("{\"safe\":true}", artifact.Data.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ProjectDefaultVoiceUsesSharedOverrideResetRules()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        await Call(dispatcher, "chapter.create", new { title = "Chapter" });
        foreach (string id in new[] { "a", "b" }) Assert.False((await Call(dispatcher, "presets.save", new { preset = new VoicePreset { PresetId = id, Name = id, ProviderId = "openai", ModelId = "gpt-4o-mini-tts", Voice = "alloy" } })).IsError);
        await Call(dispatcher, "presets.assign", new { segmentId = dispatcher.Project!.Segments[0].SegmentId, presetId = "a" });
        Assert.False((await Call(dispatcher, "presets.default", new { presetId = "b" })).IsError);
        Assert.Equal("b", dispatcher.Project!.DefaultVoicePresetId);
        Assert.Null(dispatcher.Project.Segments[0].VoicePresetId);
    }

    [Fact]
    public async Task RefreshDoesNotReissueProjectChangesForIdenticalDocuments()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        int events = 0;
        dispatcher.ProjectChanged += (_, _) => events++;
        await Call(dispatcher, "chapter.list");
        await Call(dispatcher, "segment.list");
        Assert.InRange(events, 0, 1); // Create returns its initial timestamp; first reload may observe committed UpdatedAt.
    }

    [Fact]
    public async Task TextPagesReportContinuationAndPreserveSurrogatePairs()
    {
        using AutomationDispatcher dispatcher = Create();
        await CreateProject(dispatcher);
        await Call(dispatcher, "chapter.from-draft", new { draft = new ChapterDraft("Chapter", [new("Text", "A😀BC")], "Local") });
        string segmentId = dispatcher.Project!.Segments.Single().SegmentId;
        var first = await Call(dispatcher, "script.get", new { segmentId, offset = 0, count = 2 });
        Assert.False(first.IsError);
        Assert.Equal("A", first.Data.GetProperty("authoringText").GetString());
        Assert.Equal(5, first.Data.GetProperty("totalUtf16Length").GetInt32());
        Assert.Equal(1, first.Data.GetProperty("nextOffset").GetInt32());
        var next = await Call(dispatcher, "script.get", new { segmentId, offset = 1, count = 1 });
        Assert.Equal("😀", next.Data.GetProperty("authoringText").GetString());
        Assert.Equal(3, next.Data.GetProperty("nextOffset").GetInt32());
        Assert.Equal("editor.invalid-range", (await Call(dispatcher, "script.get", new { segmentId, offset = 2, count = 2 })).Code);
    }

    [Fact]
    public async Task AiProposalIsExplicitSourceGuardedAndDiagnosticsOmitItsText()
    {
        FakeWriting writing = new();
        using AutomationDispatcher dispatcher = Create(writing: writing);
        await CreateProject(dispatcher);
        await Call(dispatcher, "chapter.from-draft", new { draft = new ChapterDraft("Chapter", [new("Text", "Original narration")], "Local") });
        string segmentId = dispatcher.Project!.Segments.Single().SegmentId;
        var started = await Call(dispatcher, "writing.revise", new { providerId = "fake", modelId = "exact", segmentId, action = "ImproveClarity" }, "write-one");
        string jobId = started.Data.GetProperty("jobId").GetString()!;
        var done = await Call(dispatcher, "jobs.wait", new { jobId, timeoutMilliseconds = 2000 });
        Assert.Equal("Completed", done.Data.GetProperty("status").GetString());
        string proposalId = done.Data.GetProperty("result").GetProperty("proposalId").GetString()!;
        Assert.Equal("Original narration", await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(ProjectRoot, dispatcher.Project.Segments.Single().ScriptRelativePath)));
        Assert.DoesNotContain("Improved narration", (await Call(dispatcher, "diagnostics.get")).Data.GetRawText());
        var accepted = await Call(dispatcher, "writing.proposals.accept", new { proposalId });
        Assert.False(accepted.IsError);
        Assert.Equal("Improved narration", await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(ProjectRoot, dispatcher.Project!.Segments.Single().ScriptRelativePath)));
        Assert.Single(dispatcher.Project.Segments.Single().WritingRevisions);
        Assert.Equal("proposal.resolved", (await Call(dispatcher, "writing.proposals.accept", new { proposalId })).Code);
        Assert.Equal(1, writing.Calls);
    }

    [Fact]
    public async Task AiProposalCannotOverwriteAChangedSource()
    {
        using AutomationDispatcher dispatcher = Create(writing: new FakeWriting());
        await CreateProject(dispatcher);
        await Call(dispatcher, "chapter.from-draft", new { draft = new ChapterDraft("Chapter", [new("Text", "Original")], "Local") });
        SegmentDocument segment = dispatcher.Project!.Segments.Single();
        var started = await Call(dispatcher, "writing.revise", new { providerId = "fake", modelId = "exact", segmentId = segment.SegmentId, action = "Custom" }, "guarded-write");
        var done = await Call(dispatcher, "jobs.wait", new { jobId = started.Data.GetProperty("jobId").GetString(), timeoutMilliseconds = 2000 });
        string proposalId = done.Data.GetProperty("result").GetProperty("proposalId").GetString()!;
        string path = VoiceBenchProjectLayout.ResolveRelativePath(ProjectRoot, segment.ScriptRelativePath);
        await File.WriteAllTextAsync(path, "External change");
        Assert.True((await Call(dispatcher, "writing.proposals.accept", new { proposalId })).IsError);
        Assert.Equal("External change", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void CatalogNamesAreUniqueAndEveryArrayHasTypedItems()
    {
        Assert.Equal(AutomationCapabilities.All.Count, AutomationCapabilities.All.Select(item => item.ToolName).Distinct().Count());
        foreach (AutomationCapability capability in AutomationCapabilities.All)
            foreach (JsonProperty property in capability.InputSchema.GetProperty("properties").EnumerateObject())
                if (property.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "array") Assert.True(property.Value.TryGetProperty("items", out _), capability.Command);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class FakeDesktop : IDesktopAutomationAdapter
    {
        public Func<string, JsonElement, CancellationToken, Task<object?>>? Handler { get; init; }
        public string? LastCommand { get; private set; }
        public JsonElement LastArguments { get; private set; }
        public int Calls { get; private set; }
        public Task<object?> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            LastCommand = command; LastArguments = arguments.Clone(); Calls++;
            return Handler?.Invoke(command, arguments, cancellationToken) ?? Task.FromResult<object?>(new { command });
        }
    }

    private sealed class FakeWriting : IWritingAssistantService
    {
        public int Calls { get; private set; }
        public IReadOnlyCollection<WritingProviderDescriptor> Providers => [new("fake", "Fake", [new("exact", "Exact", 10000)])];
        public Task<ChapterOutline> CreateOutlineAsync(string providerId, WritingOutlineRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ChapterOutline(request.ChapterTitle, "Outline", [new("Opening", "Intro", 60)]));
        public Task<ChapterDraft> CreateDraftAsync(string providerId, WritingDraftRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ChapterDraft(request.Outline.Title, [new("Opening", "Draft narration")], "Draft"));
        public Task<WritingProposal> ReviseAsync(string providerId, WritingRevisionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal("fake", providerId);
            Assert.Equal("exact", request.ModelId);
            return Task.FromResult(new WritingProposal("Improved narration", "Improved", []));
        }
    }
}
