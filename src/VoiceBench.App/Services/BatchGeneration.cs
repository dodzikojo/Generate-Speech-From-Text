using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>A segment offered for batch generation, with the reason it is (or is not) checked by default.</summary>
public sealed record BatchCandidate(
    string SegmentId,
    string ChapterId,
    string ChapterTitle,
    int Number,
    string Title,
    SegmentStatus Status,
    int TakeCount,
    TimeSpan Estimate,
    bool UsesOtherVoice,
    string? Problem)
{
    public bool CanGenerate => Problem is null && Status != SegmentStatus.Rendering;

    /// <summary>
    /// Approved segments start unchecked: a new take replaces the segment's approved state until the
    /// operator approves again, which would silently drop it from chapter playback and mastering.
    /// </summary>
    public bool CheckedByDefault => CanGenerate && Status != SegmentStatus.Approved;

    public string DisplayTitle => $"{Number:000}  {Title}";

    public string Detail
    {
        get
        {
            string state = Problem ?? Status switch
            {
                SegmentStatus.Approved => "Approved · a new take will need approval again",
                SegmentStatus.Rendering => "Rendering now",
                SegmentStatus.Rendered => $"Take {TakeCount:00} awaiting review",
                SegmentStatus.Stale => "Script changed since approval",
                SegmentStatus.PickupRequired => "Pickup requested",
                SegmentStatus.Ready => "Ready",
                _ => "Draft",
            };
            string voice = UsesOtherVoice && CanGenerate ? " · switches to the project voice" : string.Empty;
            string estimate = Estimate > TimeSpan.Zero ? $" · {TakeListPresentation.FormatDuration(Estimate)} est." : string.Empty;
            return state + voice + estimate;
        }
    }
}

/// <summary>Chooses which segments a batch generation offers, in the same order as the chapter rail.</summary>
public static class BatchGenerationPlanner
{
    /// <summary>The segment and every segment after it in its chapter.</summary>
    public static IReadOnlyList<string> FromHereToChapterEnd(VoiceBenchProjectDocument project, string segmentId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ChapterDocument chapter = project.Chapters.FirstOrDefault(item => item.SegmentOrder.Contains(segmentId, StringComparer.Ordinal))
            ?? throw new ArgumentException("The segment is not part of any chapter in this project.", nameof(segmentId));
        int start = chapter.SegmentOrder.IndexOf(segmentId);
        return chapter.SegmentOrder.Skip(start).ToArray();
    }

    public static IReadOnlyList<string> WholeChapter(VoiceBenchProjectDocument project, string chapterId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ChapterDocument chapter = project.Chapters.FirstOrDefault(item => item.ChapterId == chapterId)
            ?? throw new ArgumentException("The chapter is not part of this project.", nameof(chapterId));
        return chapter.SegmentOrder.ToArray();
    }

    /// <summary>
    /// Builds the candidate list in rail order (chapter order, then segment order). Unknown segments and
    /// segments in archived chapters are dropped; duplicates collapse.
    /// </summary>
    public static IReadOnlyList<BatchCandidate> Candidates(
        VoiceBenchProjectDocument project,
        IEnumerable<string> segmentIds,
        IReadOnlyDictionary<string, TimeSpan>? estimates = null,
        Func<SegmentDocument, string?>? scriptProblem = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        HashSet<string> requested = new(segmentIds, StringComparer.Ordinal);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(item => item.ChapterId, StringComparer.Ordinal);
        List<BatchCandidate> candidates = [];
        foreach (string chapterId in project.ChapterOrder)
        {
            if (!chapters.TryGetValue(chapterId, out ChapterDocument? chapter) || chapter.Status == ChapterStatus.Archived) continue;
            int number = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                number++;
                if (!requested.Remove(segmentId)) continue;
                TimeSpan estimate = estimates is not null && estimates.TryGetValue(segmentId, out TimeSpan value) ? value : TimeSpan.Zero;
                bool otherVoice = segment.VoicePresetId is not null
                    && !string.Equals(segment.VoicePresetId, project.DefaultVoicePresetId, StringComparison.Ordinal);
                candidates.Add(new BatchCandidate(
                    segment.SegmentId,
                    chapter.ChapterId,
                    chapter.Title,
                    number,
                    segment.Title,
                    segment.Status,
                    segment.RenderHistory.Count == 0 ? 0 : segment.RenderHistory.Max(render => render.TakeNumber),
                    estimate,
                    otherVoice,
                    scriptProblem?.Invoke(segment)));
            }
        }

        return candidates;
    }

    public static TimeSpan TotalEstimate(IEnumerable<BatchCandidate> candidates) =>
        candidates.Aggregate(TimeSpan.Zero, (total, candidate) => total + candidate.Estimate);

    /// <summary>Why a script cannot be rendered, or null when it can.</summary>
    public static string? ScriptProblem(string scriptText)
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(scriptText ?? string.Empty);
        if (parsed.HasErrors)
        {
            ScriptDocumentDiagnostic first = parsed.Diagnostics[0];
            return $"Fix the production cue on line {first.SourceLine}: {first.Message}";
        }

        return string.IsNullOrWhiteSpace(parsed.NarrationText) ? "No narration to speak" : null;
    }
}

public static class BatchGenerationRules
{
    /// <summary>Stop after this many failures in a row even when each looks segment-specific.</summary>
    public const int MaxConsecutiveFailures = 3;

    private static readonly HashSet<string> SegmentSpecificFailures = new(StringComparer.Ordinal)
    {
        "provider.validation",
        "provider.policy",
        "provider.malformedresponse",
        "operation.invalid",
        "render_invalid",
        "segment_not_found",
        "segment.not-found",
    };

    /// <summary>
    /// A problem with one script (too long, empty, refused) skips to the next segment. Anything that
    /// would fail again for every segment (keys, rate limits, network, files, an unresolved edit)
    /// stops the batch so no further provider requests are spent.
    /// </summary>
    public static bool ShouldStop(string? failureCode, int consecutiveFailures) =>
        failureCode is null
        || !SegmentSpecificFailures.Contains(failureCode)
        || consecutiveFailures >= MaxConsecutiveFailures;

    /// <summary>Provider failures arrive with a generic job message; name the fix instead.</summary>
    public static string FailureMessage(string? failureCode, string jobMessage) => failureCode switch
    {
        "provider.credentials" => "The provider key is missing. Add it in Settings, then generate the rest.",
        "provider.authentication" => "The provider rejected the key. Replace it in Settings, then generate the rest.",
        "provider.ratelimited" => "The provider is rate limiting requests. Wait a minute, then generate the rest.",
        "provider.transient" => "The provider could not be reached. Check the connection, then generate the rest.",
        _ => jobMessage,
    };
}

public sealed record BatchGenerationFailure(string SegmentTitle, string Message);

public enum BatchGenerationSeverity
{
    Success,
    Warning,
    Error,
}

public sealed record BatchGenerationSummary(BatchGenerationSeverity Severity, string Title, string Message);

public sealed record BatchGenerationOutcome(
    int Requested,
    int Generated,
    IReadOnlyList<BatchGenerationFailure> Failures,
    int NotStarted,
    bool Cancelled,
    BatchGenerationFailure? StopFailure)
{
    public BatchGenerationSummary Summarize()
    {
        string notStarted = NotStarted == 0
            ? string.Empty
            : $" {NotStarted} {(NotStarted == 1 ? "segment was" : "segments were")} not generated.";
        if (Cancelled)
        {
            return new(
                BatchGenerationSeverity.Warning,
                "Generation stopped",
                $"Generated {Generated} of {Requested} {Takes(Requested)}.{notStarted} Takes already generated are kept.");
        }

        if (StopFailure is not null)
        {
            return new(
                BatchGenerationSeverity.Error,
                $"Generation stopped after {Generated} of {Requested} {Takes(Requested)}",
                $"{StopFailure.SegmentTitle}: {StopFailure.Message}{notStarted}");
        }

        if (Failures.Count > 0)
        {
            string failed = string.Join(" ", Failures.Select(item => $"{item.SegmentTitle}: {item.Message}"));
            return new(
                BatchGenerationSeverity.Warning,
                $"Generated {Generated} of {Requested} {Takes(Requested)}",
                $"Not generated: {failed}");
        }

        return new(
            BatchGenerationSeverity.Success,
            $"Generated {Generated} {Takes(Generated)}",
            "Listen in the Takes tab or the take bar, then approve the takes you keep.");
    }

    private static string Takes(int count) => count == 1 ? "take" : "takes";
}
