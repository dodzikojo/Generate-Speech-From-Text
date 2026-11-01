using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Application.Bridge;
using System.Text.Json;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Cli;

public sealed class CliApplication(
    IProjectRepository projects,
    TextWriter output,
    TextWriter error)
{
    public static CliApplication CreateDefault() => new(
        new FileProjectRepository(),
        Console.Out,
        Console.Error);

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            using ProjectLease? lease = await AcquireOrRouteAsync(args, cancellationToken).ConfigureAwait(false);
            if (_routedExitCode is int routed) return routed;
            return args switch
            {
                ["doctor"] => await DoctorAsync(cancellationToken).ConfigureAwait(false),
                ["project", "create", var folder, ..] => await CreateProjectAsync(folder, args[3..], cancellationToken).ConfigureAwait(false),
                ["project", "validate", var folder] => await ValidateProjectAsync(folder, cancellationToken).ConfigureAwait(false),
                ["render", var folder, ..] => await RenderAsync(folder, args[2..], cancellationToken).ConfigureAwait(false),
                ["master", var folder, ..] => await MasterAsync(folder, args[2..], cancellationToken).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (SpeechProviderException exception)
        {
            await error.WriteLineAsync($"{exception.ProviderId}: {exception.Message}").ConfigureAwait(false);
            return (int)(exception.Kind == SpeechProviderFailureKind.Credentials
                ? CliExitCode.CredentialFailure
                : CliExitCode.ProviderFailure);
        }
        catch (ProjectSchemaException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return (int)CliExitCode.ValidationFailure;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return (int)CliExitCode.ValidationFailure;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return (int)CliExitCode.UnexpectedFailure;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return (int)CliExitCode.UnexpectedFailure;
        }
    }

    private int? _routedExitCode;

    private async Task<ProjectLease?> AcquireOrRouteAsync(string[] args, CancellationToken cancellationToken)
    {
        _routedExitCode = null;
        string? root = args switch
        {
            ["project", "create" or "validate", var folder, ..] => folder,
            ["render" or "master", var folder, ..] => folder,
            _ => null,
        };
        if (root is null) return null;
        try { return ProjectLease.Acquire(root); }
        catch (AutomationException exception) when (exception.Code == "project.in-use")
        {
            await using var client = new LocalControlClient();
            AutomationResult active = await client.ExecuteAsync(new("project.get", AutomationJson.Element(new { })), cancellationToken).ConfigureAwait(false);
            if (active.IsError || !active.Data.TryGetProperty("projectRoot", out var path)
                || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), path.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The project is owned by another VoiceBench process. Connect to that desktop instance or close the project before using the CLI.");
            if (args[0] == "project")
            {
                if (args[1] != "validate") throw new IOException("A VoiceBench project already exists and is open at this location.");
                var validation = await client.ExecuteAsync(new("project.validate", AutomationJson.Element(new { })), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(validation.Data.GetRawText()).ConfigureAwait(false);
                _routedExitCode = validation.IsError || !validation.Data.TryGetProperty("isValid", out var valid) || !valid.GetBoolean()
                    ? (int)CliExitCode.ValidationFailure : (int)CliExitCode.Success;
                return null;
            }
            if (args[0] == "render")
            {
                string? segment = GetOption(args[2..], "--segment");
                bool ready = args.Contains("--ready", StringComparer.Ordinal);
                if ((segment is null) == !ready) { _routedExitCode = Usage(); return null; }
                var project = active.Data.GetProperty("project").Deserialize<VoiceBenchProjectDocument>(AutomationJson.Options)!;
                var ids = segment is not null ? new[] { segment } : ProjectOrdering.GetPlaybackOrderedSegmentIds(project)
                    .Where(id => project.Segments.Single(item => item.SegmentId == id).Status == SegmentStatus.Ready).ToArray();
                foreach (string id in ids)
                {
                    JsonElement result = await RunDesktopJobAsync(client, "render.start", new { segmentId = id }, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(result.GetRawText()).ConfigureAwait(false);
                    if (result.TryGetProperty("render", out var take) && take.TryGetProperty("renderId", out var renderId))
                        _ = await RunDesktopJobAsync(client, "render.process", new { segmentId = id, renderId = renderId.GetString() }, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                string? name = GetOption(args[2..], "--name");
                string? chapter = GetOption(args[2..], "--chapter");
                if (string.IsNullOrWhiteSpace(name) || (chapter is not null && args.Contains("--compilation", StringComparer.Ordinal)))
                { _routedExitCode = Usage(); return null; }
                JsonElement result = args.Contains("--mix", StringComparer.Ordinal)
                    ? await RunDesktopJobAsync(client, "mix.export", new { name, scope = chapter is null ? "Compilation" : "Chapter", chapterId = chapter, mp3 = args.Contains("--mp3",StringComparer.Ordinal), stems = args.Contains("--stems",StringComparer.Ordinal) }, cancellationToken).ConfigureAwait(false)
                    : await RunDesktopJobAsync(client, "master.start", new
                {
                    name, scope = chapter is null ? "Compilation" : "Chapter", chapterId = chapter,
                }, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(result.GetRawText()).ConfigureAwait(false);
            }
            _routedExitCode = (int)CliExitCode.Success;
            return null;
        }
    }

    private static async Task<JsonElement> RunDesktopJobAsync(LocalControlClient client, string command, object arguments, CancellationToken cancellationToken)
    {
        AutomationResult started = await client.ExecuteAsync(new(command, AutomationJson.Element(arguments), Guid.NewGuid().ToString("N")), cancellationToken).ConfigureAwait(false);
        if (started.IsError) throw new IOException(started.Message);
        string jobId = started.Data.GetProperty("jobId").GetString()!;
        while (true)
        {
            AutomationResult state = await client.ExecuteAsync(new("jobs.get", AutomationJson.Element(new { jobId })), cancellationToken).ConfigureAwait(false);
            if (state.IsError) throw new IOException(state.Message);
            string? status = state.Data.GetProperty("status").GetString();
            if (status == "Completed") return state.Data.GetProperty("result").Clone();
            if (status is "Failed" or "Cancelled" or "Interrupted") throw new IOException($"VoiceBench operation {jobId}: {status}. {state.Data.GetProperty("failureCode")}");
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> DoctorAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("VoiceBench doctor").ConfigureAwait(false);
        await output.WriteLineAsync($"[ok] Windows {Environment.OSVersion.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})").ConfigureAwait(false);
        await output.WriteLineAsync($"[ok] .NET {Environment.Version}").ConfigureAwait(false);

        WindowsCredentialStore store = new();
        foreach (string providerId in new[] { "openai", "gemini" })
        {
            string environmentName = ProviderCredentialResolver.GetEnvironmentVariableName(providerId)!;
            bool inEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentName));
            bool inCredentialManager = !inEnvironment &&
                !string.IsNullOrWhiteSpace(await store.GetAsync(providerId, cancellationToken).ConfigureAwait(false));
            string status = inEnvironment
                ? $"configured through {environmentName}"
                : inCredentialManager
                    ? "configured in Windows Credential Manager"
                    : "not configured (required only for live rendering)";
            await output.WriteLineAsync($"[{(inEnvironment || inCredentialManager ? "ok" : "warn")}] {providerId}: {status}").ConfigureAwait(false);
        }

        string managedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IssueLab",
            "VoiceBench",
            "Tools");
        string? ffmpeg = Directory.Exists(managedRoot)
            ? Directory.EnumerateFiles(managedRoot, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        await output.WriteLineAsync(ffmpeg is null
            ? "[warn] FFmpeg: not installed (VoiceBench downloads the pinned build on first processing/mastering run)"
            : $"[ok] FFmpeg: {ffmpeg}").ConfigureAwait(false);
        return (int)CliExitCode.Success;
    }

    private async Task<int> CreateProjectAsync(
        string folder,
        string[] options,
        CancellationToken cancellationToken)
    {
        string? name = GetOption(options, "--name");
        if (string.IsNullOrWhiteSpace(name))
        {
            await error.WriteLineAsync("project create requires --name <name>.").ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        VoiceBenchProjectDocument project = await projects.CreateAsync(folder, name, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Created '{project.Name}' at {Path.GetFullPath(folder)}").ConfigureAwait(false);
        return (int)CliExitCode.Success;
    }

    private async Task<int> ValidateProjectAsync(string folder, CancellationToken cancellationToken)
    {
        ProjectValidationResult result = await projects.ValidateAsync(folder, cancellationToken).ConfigureAwait(false);
        if (result.IsValid)
        {
            await output.WriteLineAsync("Project is valid.").ConfigureAwait(false);
            return (int)CliExitCode.Success;
        }

        foreach (ProjectValidationIssue issue in result.Issues)
        {
            await error.WriteLineAsync($"{issue.Code}: {issue.Message}").ConfigureAwait(false);
        }

        return (int)CliExitCode.ValidationFailure;
    }

    private async Task<int> RenderAsync(
        string folder,
        string[] options,
        CancellationToken cancellationToken)
    {
        string? segmentId = GetOption(options, "--segment");
        bool renderReady = options.Contains("--ready", StringComparer.Ordinal);
        if ((segmentId is null) == !renderReady)
        {
            await error.WriteLineAsync("render requires exactly one of --segment <id> or --ready.").ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        using HttpClient httpClient = new();
        SpeechProviderRegistry providers = new(
            [new OpenAiSpeechProvider(httpClient), new GeminiSpeechProvider(httpClient)]);
        ProviderCredentialResolver credentials = new(new WindowsCredentialStore());
        WordAlignmentProviderRegistry alignmentProviders = new(
            [new OpenAiWordAlignmentProvider(httpClient), new GeminiWordAlignmentProvider(httpClient)]);
        FileWordAlignmentService alignments = new(projects, alignmentProviders, credentials);
        _ = await alignments.RecoverInterruptedAsync(folder, cancellationToken).ConfigureAwait(false);
        string journalPath = Path.Combine(Path.GetFullPath(folder), ".voicebench", "render-queue.json");
        using FileRenderQueue queue = new(journalPath);
        _ = await queue.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);
        SegmentRenderService renderer = new(
            projects,
            providers,
            credentials,
            queue,
            new ImmutableRenderStore(),
            wordAlignment: alignments);

        IReadOnlyList<string> segmentIds;
        if (segmentId is not null)
        {
            segmentIds = [segmentId];
        }
        else
        {
            VoiceBenchProjectDocument project = await projects.LoadAsync(folder, cancellationToken).ConfigureAwait(false);
            segmentIds = ProjectOrdering.GetPlaybackOrderedSegmentIds(project)
                .Where(id => project.Segments.Single(segment => segment.SegmentId == id).Status == SegmentStatus.Ready)
                .ToArray();
        }

        if (segmentIds.Count == 0)
        {
            await output.WriteLineAsync("No Ready segments to render.").ConfigureAwait(false);
            return (int)CliExitCode.Success;
        }

        foreach (string id in segmentIds)
        {
            SegmentRenderResult rendered = await renderer.RenderAsync(folder, id, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Rendered {id} take {rendered.Render.TakeNumber}: {rendered.Render.RawAudioRelativePath}").ConfigureAwait(false);
            if (rendered.Alignment is not null)
            {
                await output.WriteLineAsync(
                    rendered.Alignment.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial
                        ? $"Word alignment: {rendered.Alignment.Status} ({rendered.Alignment.Coverage:P0})"
                        : $"Word alignment: {rendered.Alignment.Status} ({rendered.Alignment.FailureCode})")
                    .ConfigureAwait(false);
            }
            if (rendered.ExceedsFourMinuteGuidance)
            {
                await output.WriteLineAsync(
                    $"Warning: estimated duration {rendered.EstimatedDuration:mm\\:ss} exceeds four minutes; split the segment before the next take.").ConfigureAwait(false);
            }

            await ProcessTakeAsync(folder, id, rendered.Render.RenderId, httpClient, cancellationToken).ConfigureAwait(false);
        }

        return (int)CliExitCode.Success;
    }

    private async Task ProcessTakeAsync(
        string folder,
        string segmentId,
        Guid renderId,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        FfmpegToolchainManager manager = new(httpClient);
        ToolchainPaths tools = await manager.EnsureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        RawAudioProcessingService processor = new(projects, new ProcessRunner());
        RenderRecord processed = await processor.ProcessAsync(
            folder,
            segmentId,
            renderId,
            tools,
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Processed take: {processed.ProcessedAudioRelativePath}").ConfigureAwait(false);
    }

    private async Task<int> MasterAsync(
        string folder,
        string[] options,
        CancellationToken cancellationToken)
    {
        string? name = GetOption(options, "--name");
        if (string.IsNullOrWhiteSpace(name))
        {
            await error.WriteLineAsync("master requires --name <name>.").ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        string? chapterId = GetOption(options, "--chapter");
        bool compilation = options.Contains("--compilation", StringComparer.Ordinal);
        if (chapterId is not null && compilation)
        {
            await error.WriteLineAsync("master accepts either --chapter <id> or --compilation, not both.")
                .ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        try
        {
            using HttpClient httpClient = new();
            ToolchainPaths tools = await new FfmpegToolchainManager(httpClient)
                .EnsureAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if(options.Contains("--mix",StringComparer.Ordinal))
            {
                var timeline=new AudioTimelineService(projects);
                MixRenderPlan plan=await timeline.BuildPlanAsync(folder,chapterId is null?AudioTimelineScope.Compilation:AudioTimelineScope.Chapter,chapterId,cancellationToken).ConfigureAwait(false);
                var assets=new AudioAssetService(projects,new ProcessRunner(),tools);
                MixExportResult mix=await new MixExportService(assets,new ProcessRunner(),tools).ExportAsync(folder,plan,name,
                    options.Contains("--mp3",StringComparer.Ordinal),options.Contains("--stems",StringComparer.Ordinal),cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Master: {mix.MasterRelativePath}").ConfigureAwait(false);
                await output.WriteLineAsync($"Report: {mix.ReportRelativePath}").ConfigureAwait(false);
                return (int)CliExitCode.Success;
            }
            FfmpegMasteringService mastering = new(projects, new ProcessRunner(), tools);
            MasteringResult result = await mastering.BuildAsync(
                new MasteringRequest(
                    folder,
                    name,
                    chapterId is null ? MasterScope.Compilation : MasterScope.Chapter,
                    chapterId),
                cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Master: {result.MasterRelativePath}").ConfigureAwait(false);
            await output.WriteLineAsync($"Report: {result.ReportRelativePath}").ConfigureAwait(false);
            return (int)CliExitCode.Success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return (int)CliExitCode.MasteringFailure;
        }
    }

    private int Usage()
    {
        output.WriteLine("""
            VoiceBench narration workstation

              voicebench doctor
              voicebench project create <folder> --name <name>
              voicebench project validate <project>
              voicebench render <project> --segment <id>
              voicebench render <project> --ready
              voicebench master <project> --name <name> [--compilation]
              voicebench master <project> --name <name> --chapter <id>
              voicebench master <project> --mix --name <name> [--chapter <id> | --compilation] [--mp3] [--stems]
            """);
        return (int)CliExitCode.Usage;
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
