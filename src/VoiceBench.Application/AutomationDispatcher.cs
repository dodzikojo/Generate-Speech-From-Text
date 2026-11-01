using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Application;

public sealed class AutomationProjectChangedEventArgs(string? projectRoot, VoiceBenchProjectDocument? project) : EventArgs
{
    public string? ProjectRoot { get; } = projectRoot;
    public VoiceBenchProjectDocument? Project { get; } = project;
}

public sealed partial class AutomationDispatcher : IAutomationDispatcher, IDisposable, IAsyncDisposable
{
    private readonly IDesktopAutomationAdapter? _desktop;
    private readonly IProjectRepository _projects;
    private readonly FileRecentProjectCatalog _recent;
    private readonly IAuthoringTransactionManager _history;
    private readonly ChapterAuthoringService _chapters;
    private readonly ProjectImportService _imports;
    private readonly IWritingAssistantService _writing;
    private readonly HttpClient? _httpClient;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly string _stateDirectory;
    private readonly object _lifetime = new();
    private Task _shutdownTask = Task.CompletedTask;
    private Task? _systemRecovery;
    private ProjectLease? _lease;
    private bool _disposed;

    public AutomationDispatcher(IDesktopAutomationAdapter? desktop = null, IProjectRepository? projects = null,
        FileRecentProjectCatalog? recentProjects = null, IAuthoringTransactionManager? authoringTransactions = null,
        IWritingAssistantService? writingAssistant = null, string? automationStateDirectory = null)
    {
        _desktop = desktop;
        _stateDirectory = Path.GetFullPath(automationStateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IssueLab", "VoiceBench", "Automation"));
        _projects = projects ?? new FileProjectRepository();
        _recent = recentProjects ?? new FileRecentProjectCatalog();
        _history = authoringTransactions ?? new FileAuthoringTransactionManager();
        _chapters = new(_projects);
        _imports = new(_projects);
        Coordinator = new(_projects);
        if (writingAssistant is null)
        {
            _httpClient = new();
            _writing = new WritingAssistantService(new WritingProviderRegistry(
                [new OpenAiWritingProvider(_httpClient), new GeminiWritingProvider(_httpClient)]),
                new ProviderCredentialResolver(new WindowsCredentialStore()));
        }
        else _writing = writingAssistant;
    }

    public ProjectCoordinator Coordinator { get; }
    public string? ProjectRoot { get; private set; }
    public VoiceBenchProjectDocument? Project { get; private set; }
    public event EventHandler<AutomationProjectChangedEventArgs>? ProjectChanged;

    public void BindProject(string? projectRoot, VoiceBenchProjectDocument? project)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((projectRoot is null) != (project is null)) throw new ArgumentException("Project root and document must both be set or cleared.");
        string? root = projectRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        bool changedRoot = !string.Equals(root, ProjectRoot, StringComparison.OrdinalIgnoreCase);
        if (changedRoot && HasActiveJobs) throw new AutomationException("project.jobs-active", "Finish or cancel active jobs before switching projects.");
        if (changedRoot)
        {
            ProjectLease? next = root is null ? null : ProjectLease.Acquire(root);
            _lease?.Dispose();
            _lease = next;
        }
        ProjectRoot = root;
        Project = project;
        ProjectChanged?.Invoke(this, new(root, project));
    }

    public async Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        if (_disposed) return AutomationResult.Error("automation.closed", "The automation dispatcher is closed.");
        AutomationCapability? capability = AutomationCapabilities.All.SingleOrDefault(item => item.Command == request.Command);
        if (capability is null) return AutomationResult.Error("command.unknown", "Unknown VoiceBench command.");
        try
        {
            Task recovery;
            lock (_lifetime) recovery = _systemRecovery ??= RecoverJobsAsync(_stateDirectory, CancellationToken.None);
            await recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
            ValidateArguments(capability, request.Arguments);
            // Job control must remain available while another operation is awaiting desktop/provider work.
            if (request.Command.StartsWith("jobs.", StringComparison.Ordinal))
                return AutomationResult.Success(await ExecuteJobsAsync(request.Command, request.Arguments, request.RequestId, cancellationToken).ConfigureAwait(false));
            await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string? receiptPath = !capability.ReadOnly && !string.IsNullOrWhiteSpace(request.RequestId)
                    ? ReceiptPath(_stateDirectory, request.RequestId) : null;
                if (receiptPath is not null && File.Exists(receiptPath))
                {
                    DurableReceipt receipt = await ReadJsonAsync<DurableReceipt>(receiptPath, cancellationToken).ConfigureAwait(false);
                    string fingerprint = Fingerprint(request);
                    if (receipt.Fingerprint != fingerprint) throw new AutomationException("request.id-conflict", "This request ID already belongs to different arguments.");
                    if (receipt.Result is { IsError: false } && request.Command is "project.create" or "project.open" && ProjectRoot is null)
                    {
                        string root = Required(request.Arguments, "projectRoot");
                        VoiceBenchProjectDocument document = await _projects.LoadAsync(root, cancellationToken).ConfigureAwait(false);
                        BindProject(root, document);
                        await RecoverJobsAsync(root, cancellationToken).ConfigureAwait(false);
                    }
                    return receipt.Result ?? AutomationResult.Error("request.interrupted", "The earlier request was interrupted; inspect project state before issuing a new request ID.");
                }
                if (receiptPath is not null) await WriteJsonAsync(receiptPath, new DurableReceipt(Fingerprint(request), null), cancellationToken).ConfigureAwait(false);
                AutomationResult result;
                try { result = AutomationResult.Success(await ExecuteCoreAsync(request.Command, request.Arguments, request.RequestId, cancellationToken).ConfigureAwait(false)); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { result = ToError(exception); }
                if (receiptPath is not null) await WriteJsonAsync(receiptPath, new DurableReceipt(Fingerprint(request), result), CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            finally { _requests.Release(); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return ToError(exception); }
    }

    private async Task<object?> ExecuteCoreAsync(string command, JsonElement args, string? requestId, CancellationToken ct)
    {
        if (command == "diagnostics.get") return new { transport = "stdio/named-pipe", localOnly = true, processId = Environment.ProcessId, hasProject = Project is not null, projectRoot = ProjectRoot, jobs = (await GetJobsAsync(ct).ConfigureAwait(false)).Select(job => new { job.JobId, job.Command, job.Status, job.Progress, job.CreatedAt, job.StartedAt, job.FinishedAt, job.FailureCode }).ToArray() };
        if (command == "project.recent") return await _recent.GetAsync(ct).ConfigureAwait(false);
        if (command == "project.recent.remove") { await _recent.RemoveAsync(Required(args, "projectRoot"), ct).ConfigureAwait(false); return new { removed = true }; }
        if (command == "project.get") return new { projectRoot = ProjectRoot, project = Project };
        if (command is "project.create" or "project.open")
        {
            if (HasActiveJobs) throw new AutomationException("project.jobs-active", "Finish or cancel active jobs before switching projects.");
            if (_desktop is not null && ProjectRoot is not null) await DesktopAsync("project.prepare", args, ct).ConfigureAwait(false);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Required(args, "projectRoot")));
            ProjectLease? acquired = string.Equals(root, ProjectRoot, StringComparison.OrdinalIgnoreCase) ? null : ProjectLease.Acquire(root);
            try
            {
                if (command == "project.open") await _history.RecoverInterruptedAsync(root, ct).ConfigureAwait(false);
                VoiceBenchProjectDocument document = command == "project.create"
                    ? await _projects.CreateAsync(root, Required(args, "name"), ct).ConfigureAwait(false)
                    : await _projects.LoadAsync(root, ct).ConfigureAwait(false);
                if (command == "project.create") document = await _projects.LoadAsync(root, ct).ConfigureAwait(false);
                if (acquired is not null)
                {
                    _lease?.Dispose();
                    _lease = acquired;
                    acquired = null;
                }
                ProjectRoot = root;
                Project = document;
                await RecoverJobsAsync(root, ct).ConfigureAwait(false);
                await _recent.RecordAsync(root, document.Name, ct).ConfigureAwait(false);
                ProjectChanged?.Invoke(this, new(root, document));
                return new { projectRoot = root, project = document };
            }
            finally { acquired?.Dispose(); }
        }
        if (command == "project.close")
        {
            if (HasActiveJobs) throw new AutomationException("project.jobs-active", "Finish or cancel active jobs before closing the project.");
            if (_desktop is not null) await DesktopAsync("project.prepare", args, ct).ConfigureAwait(false);
            BindProject(null, null);
            return new { closed = true };
        }
        if (IsDesktopCommand(command)) return await DesktopAsync(command, args, ct).ConfigureAwait(false);
        if (command == "toolchain.ensure") return await StartJobAsync(command, args, requestId, null, ct).ConfigureAwait(false);
        string projectRoot = RequireRoot();
        if (command is "segment.delete" or "chapter.delete" or "revisions.restore" or "segment.split")
            if (_desktop is not null) await DesktopAsync("project.prepare", args, ct).ConfigureAwait(false);
        if (command == "writing.revise" && _desktop is not null) await DesktopAsync("project.prepare", args, ct).ConfigureAwait(false);
        if (command == "render.batch")
        {
            VoiceBenchProjectDocument project = await ReloadAsync(ct).ConfigureAwait(false);
            string[] requested = Strings(args, "segmentIds");
            int selectors = (requested.Length > 0 ? 1 : 0) + (Optional(args, "chapterId") is not null ? 1 : 0) + (Bool(args, "ready") ? 1 : 0);
            if (selectors != 1) throw new AutomationException("render.targets-invalid", "Choose exactly one of segmentIds, chapterId or ready=true.");
            string[] ids = requested.Length > 0 ? requested.Distinct().ToArray() : Optional(args, "chapterId") is string chapter ? ProjectOrdering.RequireChapter(project, chapter).SegmentOrder.ToArray() : ProjectOrdering.GetPlaybackOrderedSegmentIds(project).Where(id => project.Segments.Any(segment => segment.SegmentId == id && segment.Status == SegmentStatus.Ready)).ToArray();
            if (ids.Any(id => project.Segments.All(segment => segment.SegmentId != id))) throw new AutomationException("segment.not-found", "A requested segment does not exist.");
            List<AutomationJobSnapshot> jobs = [];
            foreach (string id in ids) jobs.Add(await StartJobAsync("render.start", AutomationJson.Element(new { segmentId = id }), requestId is null ? null : requestId + ":" + id, null, ct).ConfigureAwait(false));
            return new { jobs };
        }
        if (IsLongCommand(command)) return await StartJobAsync(command, args, requestId, null, ct).ConfigureAwait(false);
        if (command == "project.validate") return await _projects.ValidateAsync(projectRoot, ct).ConfigureAwait(false);
        if (command == "chapter.list") return (await ReloadAsync(ct).ConfigureAwait(false)).Chapters;
        if (command == "segment.list") return (await ReloadAsync(ct).ConfigureAwait(false)).Segments.Where(item => Optional(args, "chapterId") is not string chapter || item.ChapterId == chapter).ToArray();
        if (command == "script.get")
        {
            SegmentDocument segment = RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args);
            string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath), ct).ConfigureAwait(false);
            TextPage page = PageText(text, args);
            ParsedScriptDocument parsed = new ProductionScriptParser().Parse(page.Text);
            return new { segment, authoringSha256 = SourceTextHasher.Compute(text), authoringText = page.Text, page.Offset, page.TotalUtf16Length, page.NextOffset,
                parsed, narrationAndCuesCoverPageOnly = page.Offset != 0 || page.NextOffset is not null };
        }
        if (command is "history.get" or "history.undo" or "history.redo")
        {
            if (command == "history.get") return await _history.GetStateAsync(projectRoot, ct).ConfigureAwait(false);
            if (_desktop is not null) await DesktopAsync("project.prepare", args, ct).ConfigureAwait(false);
            AuthoringTransactionRecord record = await Coordinator.ExecuteAsync(
                () => command == "history.undo" ? _history.UndoAsync(projectRoot, ct) : _history.RedoAsync(projectRoot, ct), ct).ConfigureAwait(false);
            await ReloadAsync(ct).ConfigureAwait(false);
            return record;
        }
        return await ExecuteWorkflowAsync(command, args, ct).ConfigureAwait(false);
    }

    private static bool IsLongCommand(string command) => command is "render.start" or "render.audition" or "render.process" or "alignment.start" or "master.start" or "exports.create" or "writing.outline" or "writing.draft" or "writing.revise" or "toolchain.ensure";
    private static bool IsDesktopCommand(string command) => command == "workspace.get" || command.StartsWith("editor.", StringComparison.Ordinal) || command.StartsWith("directions.", StringComparison.Ordinal) || command.StartsWith("cues.", StringComparison.Ordinal) || command.StartsWith("selection.", StringComparison.Ordinal) || command.StartsWith("playback.", StringComparison.Ordinal) || command.StartsWith("director.", StringComparison.Ordinal) || command.StartsWith("control.", StringComparison.Ordinal) || command is "navigation.show" or "panels.set" or "font.set" or "providers.list" or "credentials.status" or "credentials.open" or "toolchain.status" or "toolchain.select" or "artifacts.open" or "artifacts.reveal" or "file.open" or "file.reveal" or "follow.set" or "artifacts.audio-preview";
    private Task<object?> DesktopAsync(string command, JsonElement args, CancellationToken ct) => _desktop?.ExecuteAsync(command, args, ct)
        ?? throw new AutomationException("desktop.unavailable", "This command requires the running VoiceBench desktop.");
    private string RequireRoot() => ProjectRoot ?? throw new AutomationException("project.required", "Open or create a project first.");
    private async Task<VoiceBenchProjectDocument> ReloadAsync(CancellationToken ct)
    {
        string root = RequireRoot();
        VoiceBenchProjectDocument document = await _projects.LoadAsync(root, ct).ConfigureAwait(false);
        bool changed = Project is null || JsonSerializer.Serialize(Project, AutomationJson.Options) != JsonSerializer.Serialize(document, AutomationJson.Options);
        Project = document;
        if (changed) ProjectChanged?.Invoke(this, new(root, document));
        return document;
    }
    private async Task<VoiceBenchProjectDocument> MutateAsync(string label, Func<Task> action, CancellationToken ct)
    {
        string root = RequireRoot();
        await Coordinator.ExecuteAsync(async () => { await _history.ExecuteAsync(root, label, _ => action(), ct).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
        return await ReloadAsync(ct).ConfigureAwait(false);
    }
    private Task<VoiceBenchProjectDocument> UpdateAsync(string label, Func<VoiceBenchProjectDocument, VoiceBenchProjectDocument> update, CancellationToken ct) =>
        MutateAsync(label, async () => { await ProjectDocumentUpdater.UpdateAsync(_projects, RequireRoot(), update, ct).ConfigureAwait(false); }, ct);

    private static SegmentDocument RequireSegment(VoiceBenchProjectDocument project, JsonElement args) => project.Segments.SingleOrDefault(item => item.SegmentId == Required(args, "segmentId")) ?? throw new AutomationException("segment.not-found", "The segment does not exist.");
    private static ChapterDocument RequireChapter(VoiceBenchProjectDocument project, JsonElement args) => project.Chapters.SingleOrDefault(item => item.ChapterId == Required(args, "chapterId")) ?? throw new AutomationException("chapter.not-found", "The chapter does not exist.");
    private static string Required(JsonElement args, string name) => Optional(args, name) is string text && !string.IsNullOrWhiteSpace(text) ? text : throw new AutomationException("arguments.invalid", $"'{name}' must be a nonempty string.");
    private static string? Optional(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement args, string name, bool fallback = false) => args.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
    private static int Int(JsonElement args, string name, int fallback = 0) => args.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    private static T Deserialize<T>(JsonElement args, string name) => args.GetProperty(name).Deserialize<T>(AutomationJson.Options) ?? throw new AutomationException("arguments.invalid", $"'{name}' is required.");
    private static string[] Strings(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.Deserialize<string[]>(AutomationJson.Options) ?? [] : [];
    private static Guid Id(JsonElement args, string name) => Guid.TryParse(Required(args, name), out var id) ? id : throw new AutomationException("arguments.invalid", $"'{name}' must be a GUID.");

    private static void ValidateArguments(AutomationCapability capability, JsonElement args) => ValidateSchema(args, capability.InputSchema, capability.InputSchema, "arguments");

    private static void ValidateSchema(JsonElement value, JsonElement schema, JsonElement root, string path)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string target = reference.GetString()!;
            if (!target.StartsWith("#/", StringComparison.Ordinal)) throw new AutomationException("arguments.invalid", "Unsupported schema reference.");
            JsonElement resolved = root;
            foreach (string component in target[2..].Split('/')) resolved = resolved.GetProperty(component.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            ValidateSchema(value, resolved, root, path);
            return;
        }
        if (schema.TryGetProperty("type", out JsonElement types))
        {
            string[] choices = types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().Select(item => item.GetString()!).ToArray() : [types.GetString()!];
            bool valid = choices.Any(type => type switch { "null" => value.ValueKind == JsonValueKind.Null, "string" => value.ValueKind == JsonValueKind.String, "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), "number" => value.ValueKind == JsonValueKind.Number, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array, _ => false });
            if (!valid) throw new AutomationException("arguments.invalid", $"'{path}' must have type {string.Join(" or ", choices)}.");
        }
        if (schema.TryGetProperty("enum", out JsonElement options) && !options.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value))) throw new AutomationException("arguments.invalid", $"'{path}' is not an allowed value.");
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty argument in value.EnumerateObject())
            {
                // Contracts have a finite set of writable fields. Reject unknown fields rather than silently ignoring credentials or typos.
                if (!properties.TryGetProperty(argument.Name, out JsonElement property)) throw new AutomationException("arguments.invalid", $"Unexpected argument '{path}.{argument.Name}'.");
                ValidateSchema(argument.Value, property, root, path + "." + argument.Name);
            }
            if (schema.TryGetProperty("required", out JsonElement required)) foreach (JsonElement name in required.EnumerateArray())
                if (!value.TryGetProperty(name.GetString()!, out _)) throw new AutomationException("arguments.invalid", $"'{path}.{name.GetString()}' is required.");
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
            foreach (JsonElement element in value.EnumerateArray()) ValidateSchema(element, items, root, path + "[]");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            if (schema.TryGetProperty("minimum", out JsonElement minimum) && number < minimum.GetDouble() || schema.TryGetProperty("maximum", out JsonElement maximum) && number > maximum.GetDouble()) throw new AutomationException("arguments.invalid", $"'{path}' is outside the allowed range.");
        }
    }

    private static AutomationResult ToError(Exception exception) => exception switch
    {
        AutomationException failure => AutomationResult.Error(failure.Code, failure.Message, failure.Details),
        OperationCanceledException => AutomationResult.Error("operation.cancelled", "The operation was cancelled."),
        SpeechProviderException failure => AutomationResult.Error("provider." + failure.Kind.ToString().ToLowerInvariant(), "The speech provider rejected the operation. Check credentials, model limits and provider availability."),
        WritingProviderException failure => AutomationResult.Error("writing." + failure.Kind.ToString().ToLowerInvariant(), "The writing provider rejected the operation. Check credentials, model limits and provider availability."),
        IOException => AutomationResult.Error("filesystem.error", "The local file operation failed. Check the selected paths and project ownership."),
        UnauthorizedAccessException => AutomationResult.Error("filesystem.denied", "Access to the local path was denied."),
        JsonException or ArgumentException or InvalidOperationException or InvalidDataException or KeyNotFoundException => AutomationResult.Error("operation.invalid", exception.Message),
        _ => AutomationResult.Error("operation.failed", "The local operation failed. Inspect VoiceBench for recovery options."),
    };

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            ProjectLease? lease = _lease;
            _lease = null;
            _shutdownTask = FinishShutdownAsync(_jobTasks.ToArray(), _jobCancellation.Values.ToArray(), lease);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task shutdown;
        lock (_lifetime) shutdown = _shutdownTask;
        await shutdown.ConfigureAwait(false);
    }

    private async Task FinishShutdownAsync(Task[] jobs, CancellationTokenSource[] cancellations, ProjectLease? lease)
    {
        foreach (CancellationTokenSource cancellation in cancellations)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* The job completed while shutdown began. */ }
        }
        try { await Task.WhenAll(jobs).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* Failed jobs retain their journal; shutdown still releases ownership. */ }
        finally
        {
            lease?.Dispose();
            _httpClient?.Dispose();
        }
    }
}
