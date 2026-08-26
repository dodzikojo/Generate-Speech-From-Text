using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Mastering;

public sealed class FfmpegMasteringService(
    IProjectRepository repository,
    IProcessRunner processRunner,
    ToolchainPaths toolchain,
    TimeProvider? timeProvider = null) : IAudioMasteringService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<MasteringResult> BuildAsync(
        MasteringRequest request,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(
            request.ProjectRoot,
            cancellationToken).ConfigureAwait(false);
        MasterEligibility eligibility = MasterEligibilityEvaluator.Evaluate(project);
        if (!eligibility.CanBuild)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                eligibility.Issues.Select(issue => issue.Message)));
        }

        string projectRoot = Path.GetFullPath(request.ProjectRoot);
        string safeName = CreateSafeName(request.Name);
        int version = NextVersion(projectRoot, safeName);
        string stem = $"{safeName}-v{version:000}";
        string workRoot = Path.Combine(
            projectRoot,
            VoiceBenchProjectLayout.ReportsDirectory,
            $".{stem}-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        IReadOnlyDictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(
            segment => segment.SegmentId,
            StringComparer.Ordinal);
        List<MasteredSegment> masteredSegments = [];
        List<string> concatInputs = [];
        TimeSpan cursor = TimeSpan.Zero;

        foreach ((string segmentId, int index) in project.SegmentOrder.Select((id, index) => (id, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentDocument segment = segments[segmentId];
            RenderRecord render = segment.RenderHistory.Single(item => item.RenderId == segment.ApprovedRenderId);
            string inputPath = VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                render.ProcessedAudioRelativePath!);
            string normalizedPath = Path.Combine(workRoot, $"{index + 1:000}-{segmentId}.normalized.wav");

            LoudnessMeasurement measurement = await AnalyzeAsync(
                inputPath,
                project.AudioStandard,
                cancellationToken).ConfigureAwait(false);
            await NormalizeAsync(
                inputPath,
                normalizedPath,
                project.AudioStandard,
                measurement,
                cancellationToken).ConfigureAwait(false);

            masteredSegments.Add(new MasteredSegment(
                segmentId,
                segment.Title,
                cursor,
                render.Duration,
                segment.ScriptRelativePath,
                render.ProcessedAudioRelativePath!));
            concatInputs.Add(normalizedPath);
            cursor += render.Duration;

            if (index < project.SegmentOrder.Count - 1 && project.AudioStandard.PauseMilliseconds > 0)
            {
                string pausePath = Path.Combine(workRoot, $"{index + 1:000}-pause.wav");
                await CreatePauseAsync(pausePath, project.AudioStandard, cancellationToken).ConfigureAwait(false);
                concatInputs.Add(pausePath);
                cursor += TimeSpan.FromMilliseconds(project.AudioStandard.PauseMilliseconds);
            }
        }

        string concatFile = Path.Combine(workRoot, "concat.txt");
        await File.WriteAllLinesAsync(
            concatFile,
            concatInputs.Select(path => $"file '{EscapeConcatPath(path)}'"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        string candidatePath = Path.Combine(workRoot, $"{stem}.candidate.wav");
        await RunRequiredAsync(
            toolchain.FfmpegPath,
            ["-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", concatFile, "-c", "copy", candidatePath],
            workRoot,
            cancellationToken).ConfigureAwait(false);

        AudioProbe probe = await ProbeAsync(candidatePath, cancellationToken).ConfigureAwait(false);
        LoudnessMeasurement completedLoudness = await AnalyzeAsync(
            candidatePath,
            project.AudioStandard,
            cancellationToken).ConfigureAwait(false);
        Verify(project.AudioStandard, probe, completedLoudness);

        string masterRelativePath = VoiceBenchProjectLayout.ToManifestPath(
            Path.Combine(VoiceBenchProjectLayout.MastersDirectory, $"{stem}.master.wav"));
        string masterPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, masterRelativePath);
        File.Move(candidatePath, masterPath, overwrite: false);

        MasteringReport report = new(
            project.ProjectId,
            project.Name,
            stem,
            _timeProvider.GetUtcNow(),
            project.AudioStandard,
            probe,
            completedLoudness,
            masteredSegments,
            masterRelativePath);
        string reportRelativePath = await WriteExportsAsync(
            projectRoot,
            stem,
            report,
            segments,
            cancellationToken).ConfigureAwait(false);

        Directory.Delete(workRoot, recursive: true);
        return new MasteringResult(
            masterRelativePath,
            reportRelativePath,
            probe.Duration,
            probe.SampleRate,
            probe.Channels,
            completedLoudness.IntegratedLufs,
            completedLoudness.TruePeakDbtp);
    }

    private async Task<LoudnessMeasurement> AnalyzeAsync(
        string inputPath,
        AudioStandard standard,
        CancellationToken cancellationToken)
    {
        string filter = FormattableString.Invariant(
            $"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11:print_format=json");
        ProcessResult result = await RunRequiredAsync(
            toolchain.FfmpegPath,
            ["-hide_banner", "-nostdin", "-i", inputPath, "-af", filter, "-f", "null", "NUL"],
            Path.GetDirectoryName(inputPath),
            cancellationToken).ConfigureAwait(false);
        return LoudnessMeasurement.Parse(result.StandardError);
    }

    private async Task NormalizeAsync(
        string inputPath,
        string outputPath,
        AudioStandard standard,
        LoudnessMeasurement measurement,
        CancellationToken cancellationToken)
    {
        string filter = measurement.IsMeasurable
            ? FormattableString.Invariant($"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11:measured_I={measurement.IntegratedLufs}:measured_TP={measurement.TruePeakDbtp}:measured_LRA={measurement.LoudnessRange}:measured_thresh={measurement.Threshold}:offset={measurement.TargetOffset}:linear=true:print_format=summary")
            : FormattableString.Invariant($"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11:print_format=summary");
        _ = await RunRequiredAsync(
            toolchain.FfmpegPath,
            [
                "-hide_banner", "-nostdin", "-y", "-i", inputPath,
                "-af", filter,
                "-ac", standard.Channels.ToString(CultureInfo.InvariantCulture),
                "-ar", standard.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-c:a", standard.PcmBitsPerSample == 24 ? "pcm_s24le" : "pcm_s16le",
                outputPath,
            ],
            Path.GetDirectoryName(outputPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CreatePauseAsync(
        string outputPath,
        AudioStandard standard,
        CancellationToken cancellationToken)
    {
        string seconds = (standard.PauseMilliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
        _ = await RunRequiredAsync(
            toolchain.FfmpegPath,
            [
                "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
                $"anullsrc=r={standard.SampleRate}:cl=mono", "-t", seconds,
                "-c:a", standard.PcmBitsPerSample == 24 ? "pcm_s24le" : "pcm_s16le",
                outputPath,
            ],
            Path.GetDirectoryName(outputPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AudioProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunRequiredAsync(
            toolchain.FfprobePath,
            ["-v", "error", "-show_entries", "stream=sample_rate,channels:format=duration", "-of", "json", path],
            Path.GetDirectoryName(path),
            cancellationToken).ConfigureAwait(false);
        return AudioProbe.Parse(result.StandardOutput);
    }

    private async ValueTask<ProcessResult> RunRequiredAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            new ProcessRequest(executable, arguments, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private static void Verify(
        AudioStandard standard,
        AudioProbe probe,
        LoudnessMeasurement loudness)
    {
        if (probe.Channels != standard.Channels || probe.SampleRate != standard.SampleRate)
        {
            throw new InvalidDataException(
                $"Master verification failed: expected {standard.Channels} channel(s) at {standard.SampleRate} Hz, " +
                $"received {probe.Channels} channel(s) at {probe.SampleRate} Hz.");
        }

        if (!loudness.IsMeasurable)
        {
            throw new InvalidDataException(
                "Master verification failed: the completed audio is too short or too quiet for an integrated loudness measurement.");
        }

        if (Math.Abs(loudness.IntegratedLufs - standard.IntegratedLufs) > 1.0)
        {
            throw new InvalidDataException(
                $"Master verification failed: integrated loudness {loudness.IntegratedLufs:0.0} LUFS is outside tolerance.");
        }

        if (loudness.TruePeakDbtp > standard.TruePeakDbtp + 0.05)
        {
            throw new InvalidDataException(
                $"Master verification failed: true peak {loudness.TruePeakDbtp:0.00} dBTP exceeds {standard.TruePeakDbtp:0.00} dBTP.");
        }
    }

    private static async Task<string> WriteExportsAsync(
        string projectRoot,
        string stem,
        MasteringReport report,
        IReadOnlyDictionary<string, SegmentDocument> segments,
        CancellationToken cancellationToken)
    {
        List<string> transcriptMarkdown = [$"# {report.ProjectName}", ""];
        List<string> transcriptText = [];
        List<string> cueCsv = ["segment_id,title,start_seconds,duration_seconds,script_path"];
        List<string> cueMarkdown =
        [
            $"# {report.ProjectName} cue sheet",
            "",
            "| # | Segment | Start | Duration |",
            "|---:|---|---:|---:|",
        ];

        foreach ((MasteredSegment item, int index) in report.Segments.Select((item, index) => (item, index)))
        {
            string scriptPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, item.ScriptRelativePath);
            string script = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
            transcriptMarkdown.Add($"## {item.Title}");
            transcriptMarkdown.Add(string.Empty);
            transcriptMarkdown.Add(script);
            transcriptMarkdown.Add(string.Empty);
            transcriptText.Add(script);
            transcriptText.Add(string.Empty);
            cueCsv.Add(string.Join(",",
                Csv(item.SegmentId),
                Csv(item.Title),
                item.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                item.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(item.ScriptRelativePath)));
            cueMarkdown.Add(
                $"| {index + 1} | {item.Title.Replace("|", "\\|", StringComparison.Ordinal)} | " +
                $"{FormatTime(item.Start)} | {FormatTime(item.Duration)} |");
        }

        await File.WriteAllLinesAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                $"{VoiceBenchProjectLayout.TranscriptsDirectory}/{stem}.transcript.md"),
            transcriptMarkdown,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                $"{VoiceBenchProjectLayout.TranscriptsDirectory}/{stem}.transcript.txt"),
            transcriptText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                $"{VoiceBenchProjectLayout.CueSheetsDirectory}/{stem}.cues.csv"),
            cueCsv,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllLinesAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                $"{VoiceBenchProjectLayout.CueSheetsDirectory}/{stem}.cues.md"),
            cueMarkdown,
            cancellationToken).ConfigureAwait(false);

        string reportRelativePath = $"{VoiceBenchProjectLayout.ReportsDirectory}/{stem}.mastering.json";
        string reportPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, reportRelativePath);
        await using (FileStream stream = File.Create(reportPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                report,
                ProjectJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        string reportMarkdown = $"""
            # Mastering report — {report.ProjectName}

            - Master: `{report.MasterRelativePath}`
            - Created: {report.CreatedAt:O}
            - Duration: {FormatTime(report.Probe.Duration)}
            - Format: {report.Probe.Channels} channel, {report.Probe.SampleRate} Hz PCM
            - Integrated loudness: {report.Loudness.IntegratedLufs:0.00} LUFS
            - True peak: {report.Loudness.TruePeakDbtp:0.00} dBTP
            - Segments: {report.Segments.Count}
            """;
        await File.WriteAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                $"{VoiceBenchProjectLayout.ReportsDirectory}/{stem}.mastering.md"),
            reportMarkdown,
            cancellationToken).ConfigureAwait(false);
        return reportRelativePath;
    }

    private static int NextVersion(string projectRoot, string safeName)
    {
        string masters = Path.Combine(projectRoot, VoiceBenchProjectLayout.MastersDirectory);
        Directory.CreateDirectory(masters);
        return Directory.EnumerateFiles(masters, $"{safeName}-v*.master.wav")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Select(name => name.Split("-v", StringSplitOptions.None).Last().Split('.')[0])
            .Select(value => int.TryParse(value, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private static string CreateSafeName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string result = string.Concat(value.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-'));
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-", StringComparison.Ordinal);
        }

        return result.Trim('-') is { Length: > 0 } safe ? safe : "master";
    }

    private static string EscapeConcatPath(string path) =>
        path.Replace("'", "'\\''", StringComparison.Ordinal).Replace('\\', '/');

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
}

public sealed record LoudnessMeasurement(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRange,
    double Threshold,
    double TargetOffset)
{
    public bool IsMeasurable =>
        double.IsFinite(IntegratedLufs) &&
        double.IsFinite(TruePeakDbtp) &&
        double.IsFinite(LoudnessRange) &&
        double.IsFinite(Threshold) &&
        double.IsFinite(TargetOffset);

    public static LoudnessMeasurement Parse(string ffmpegOutput)
    {
        int start = ffmpegOutput.LastIndexOf('{');
        int end = ffmpegOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidDataException("FFmpeg loudness analysis did not return JSON.");
        }

        JsonObject json = JsonNode.Parse(ffmpegOutput[start..(end + 1)])?.AsObject()
            ?? throw new InvalidDataException("FFmpeg loudness analysis returned malformed JSON.");
        return new LoudnessMeasurement(
            Read(json, "input_i"),
            Read(json, "input_tp"),
            Read(json, "input_lra"),
            Read(json, "input_thresh"),
            Read(json, "target_offset"));
    }

    private static double Read(JsonObject json, string name)
    {
        string value = json[name]?.GetValue<string>()
            ?? throw new InvalidDataException($"Missing loudness field '{name}'.");
        if (string.Equals(value, "-inf", StringComparison.OrdinalIgnoreCase))
        {
            return double.NegativeInfinity;
        }

        if (string.Equals(value, "inf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "+inf", StringComparison.OrdinalIgnoreCase))
        {
            return double.PositiveInfinity;
        }

        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}

public sealed record AudioProbe(int SampleRate, int Channels, TimeSpan Duration)
{
    public static AudioProbe Parse(string ffprobeJson)
    {
        JsonObject root = JsonNode.Parse(ffprobeJson)?.AsObject()
            ?? throw new InvalidDataException("ffprobe returned malformed JSON.");
        JsonObject stream = root["streams"]?.AsArray().FirstOrDefault()?.AsObject()
            ?? throw new InvalidDataException("ffprobe returned no audio stream.");
        string durationText = root["format"]?["duration"]?.GetValue<string>()
            ?? throw new InvalidDataException("ffprobe returned no duration.");
        return new AudioProbe(
            int.Parse(stream["sample_rate"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture),
            stream["channels"]?.GetValue<int>() ?? 0,
            TimeSpan.FromSeconds(double.Parse(durationText, CultureInfo.InvariantCulture)));
    }
}

public sealed record MasteredSegment(
    string SegmentId,
    string Title,
    TimeSpan Start,
    TimeSpan Duration,
    string ScriptRelativePath,
    string ProcessedAudioRelativePath);

public sealed record MasteringReport(
    Guid ProjectId,
    string ProjectName,
    string MasterName,
    DateTimeOffset CreatedAt,
    AudioStandard Standard,
    AudioProbe Probe,
    LoudnessMeasurement Loudness,
    IReadOnlyList<MasteredSegment> Segments,
    string MasterRelativePath);
