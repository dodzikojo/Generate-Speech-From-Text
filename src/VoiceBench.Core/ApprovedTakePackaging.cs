using System.Text;

namespace IssueLab.VoiceBench.Core;

/// <summary>
/// Written into a package folder so a later package of the same project can tell the files
/// VoiceBench wrote (and still match) from anything the operator put there.
/// </summary>
public sealed record ApprovedTakePackageManifest
{
    public const string FileName = "voicebench-package.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid ProjectId { get; init; }

    public string ProjectName { get; init; } = string.Empty;

    public DateTimeOffset PackagedAt { get; init; }

    public List<PackagedTakeFile> Files { get; init; } = [];
}

/// <summary>One approved take VoiceBench copied into a package.</summary>
public sealed record PackagedTakeFile
{
    /// <summary>Path inside the package folder, with forward slashes.</summary>
    public string RelativePath { get; init; } = string.Empty;

    public string ChapterId { get; init; } = string.Empty;

    public string SegmentId { get; init; } = string.Empty;

    public Guid RenderId { get; init; }

    public int TakeNumber { get; init; }

    /// <summary>The project file it was copied from, so a re-processed take is copied again.</summary>
    public string SourceRelativePath { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>A segment as the package dialog lists it, numbered as in the chapter rail.</summary>
public sealed record PackageCandidate(
    string ChapterId,
    string ChapterTitle,
    int ChapterNumber,
    bool ChapterIncludedInCompilation,
    string SegmentId,
    string SegmentTitle,
    int SegmentNumber,
    RenderRecord? ApprovedTake,
    string? Problem)
{
    public bool CanPackage => ApprovedTake is not null && Problem is null;

    /// <summary>The approved audio to copy: the processed file, which approval always produces.</summary>
    public string? SourceRelativePath => ApprovedTake is null
        ? null
        : string.IsNullOrWhiteSpace(ApprovedTake.ProcessedAudioRelativePath) ? ApprovedTake.RawAudioRelativePath : ApprovedTake.ProcessedAudioRelativePath;

    public string TargetRelativePath => ApprovedTakePackageRules.TargetPath(this);
}

public enum PackageAction
{
    /// <summary>Copy the approved take (new segment, new approved take, or a missing file).</summary>
    Copy,

    /// <summary>The file already matches; nothing to do.</summary>
    Keep,

    /// <summary>Same take, new name: the segment moved or was renamed.</summary>
    Move,

    /// <summary>A file VoiceBench wrote for a segment that is no longer approved or selected.</summary>
    Remove,

    /// <summary>Something the operator changed or put in the way; left untouched.</summary>
    Skip,
}

public sealed record PackageStep(
    PackageAction Action,
    string RelativePath,
    string? FromRelativePath = null,
    PackageCandidate? Candidate = null,
    PackagedTakeFile? Previous = null,
    string? Reason = null);

public sealed record ApprovedTakePackagePlan(IReadOnlyList<PackageStep> Steps)
{
    public int Count(PackageAction action) => Steps.Count(step => step.Action == action);

    /// <summary>Takes that will be in the package afterwards.</summary>
    public int PackagedTakes => Steps.Count(step => step.Candidate is not null && step.Action is PackageAction.Copy or PackageAction.Keep or PackageAction.Move);

    public IEnumerable<PackageStep> Skipped => Steps.Where(step => step.Action == PackageAction.Skip);

    public string Summary
    {
        get
        {
            List<string> parts = [];
            int copies = Steps.Count(step => step.Action == PackageAction.Copy && step.Previous is null);
            int replaced = Steps.Count(step => step.Action == PackageAction.Copy && step.Previous is not null);
            if (copies > 0) parts.Add($"{copies} new");
            if (replaced > 0) parts.Add($"{replaced} updated");
            if (Count(PackageAction.Move) > 0) parts.Add($"{Count(PackageAction.Move)} renamed");
            if (Count(PackageAction.Remove) > 0) parts.Add($"{Count(PackageAction.Remove)} removed");
            if (Count(PackageAction.Keep) > 0) parts.Add($"{Count(PackageAction.Keep)} unchanged");
            if (Count(PackageAction.Skip) > 0) parts.Add($"{Count(PackageAction.Skip)} left untouched");
            return parts.Count == 0 ? "Nothing to package" : string.Join(" · ", parts);
        }
    }
}

/// <summary>What a package folder holds now: the recorded manifest and the hash of each relevant file.</summary>
public sealed record PackageFolderState(
    ApprovedTakePackageManifest? Manifest,
    IReadOnlyDictionary<string, string> FileHashes,
    IReadOnlyDictionary<string, string>? SourceHashes = null)
{
    public static PackageFolderState Empty { get; } = new(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public bool Exists(string relativePath) => FileHashes.ContainsKey(ApprovedTakePackageRules.NormalizePath(relativePath));

    /// <summary>True when the file is still exactly what VoiceBench wrote.</summary>
    public bool Matches(PackagedTakeFile file) =>
        FileHashes.TryGetValue(ApprovedTakePackageRules.NormalizePath(file.RelativePath), out string? hash)
        && string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Approved-take packaging: one WAV per approved segment, in chapter order, named as the rail
/// numbers them (<c>01 Introduction/01-003 Observe.wav</c>). Packaging again syncs the folder.
/// </summary>
public static class ApprovedTakePackageRules
{
    private const int MaxNameLength = 80;

    /// <summary>Every segment of every active chapter, in rail order, with its approved take if packageable.</summary>
    public static IReadOnlyList<PackageCandidate> Candidates(VoiceBenchProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        List<PackageCandidate> result = [];
        int chapterNumber = 0;
        foreach (ChapterDocument chapter in ProjectOrdering.GetOrderedChapters(project))
        {
            chapterNumber++;
            int segmentNumber = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                segmentNumber++;
                (RenderRecord? take, string? problem) = ApprovedTake(segment);
                result.Add(new PackageCandidate(
                    chapter.ChapterId, chapter.Title, chapterNumber, chapter.IncludedInCompilation,
                    segment.SegmentId, segment.Title, segmentNumber, take, problem));
            }
        }

        return result;
    }

    /// <summary>The take a package uses for a segment, or why there is none.</summary>
    public static (RenderRecord? Take, string? Problem) ApprovedTake(SegmentDocument segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        RenderRecord? approved = segment.RenderHistory.FirstOrDefault(take => take.RenderId == segment.ApprovedRenderId);
        if (segment.Status == SegmentStatus.Stale) return (null, "Script changed since approval");
        if (segment.Status != SegmentStatus.Approved || approved is null || approved.Status != RenderStatus.Approved)
            return (null, segment.Status == SegmentStatus.Rendering ? "Rendering now" : "Not approved yet");
        if (approved.AudioDeletedAt is not null) return (null, "Approved take audio was deleted");
        if (string.IsNullOrWhiteSpace(approved.ProcessedAudioRelativePath) && string.IsNullOrWhiteSpace(approved.RawAudioRelativePath))
            return (null, "Approved take has no audio file");
        return (approved, null);
    }

    public static string TargetPath(PackageCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return $"{candidate.ChapterNumber:00} {SafeName(candidate.ChapterTitle)}/" +
               $"{candidate.ChapterNumber:00}-{candidate.SegmentNumber:000} {SafeName(candidate.SegmentTitle)}.wav";
    }

    /// <summary>A Windows-safe file or folder name: invalid characters become '-', no trailing dots or spaces.</summary>
    public static string SafeName(string title)
    {
        char[] invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
        StringBuilder builder = new();
        bool lastWasSpace = false;
        foreach (char character in title ?? string.Empty)
        {
            char next = char.IsControl(character) || invalid.Contains(character) ? '-' : char.IsWhiteSpace(character) ? ' ' : character;
            if (next == ' ' && lastWasSpace) continue;
            lastWasSpace = next == ' ';
            builder.Append(next);
        }

        string name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd('.', ' ');
        return name.Length == 0 ? "Untitled" : name;
    }

    public static string NormalizePath(string relativePath) => relativePath.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Plans a package of <paramref name="selected"/> into a folder in <paramref name="folder"/> state.
    /// Files VoiceBench wrote earlier are kept, moved, replaced or removed so the folder matches the
    /// selection; files it didn't write, or that changed since, are never overwritten or deleted.
    /// </summary>
    public static ApprovedTakePackagePlan Plan(IEnumerable<PackageCandidate> selected, PackageFolderState folder)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(folder);
        StringComparer paths = StringComparer.OrdinalIgnoreCase;
        List<PackageCandidate> chosen = selected.Where(candidate => candidate.CanPackage)
            .GroupBy(candidate => candidate.SegmentId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        List<PackagedTakeFile> previous = folder.Manifest?.Files ?? [];
        Dictionary<string, PackagedTakeFile> previousBySegment = previous
            .GroupBy(file => file.SegmentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> chosenSegments = chosen.Select(candidate => candidate.SegmentId).ToHashSet(StringComparer.Ordinal);

        // Paths that stay exactly as they are; every other file VoiceBench wrote (and that still
        // matches) is free to be moved, replaced or removed.
        HashSet<string> kept = new(paths);
        foreach (PackageCandidate candidate in chosen)
        {
            if (previousBySegment.TryGetValue(candidate.SegmentId, out PackagedTakeFile? file) && IsSameTake(file, candidate)
                && folder.Matches(file) && paths.Equals(NormalizePath(file.RelativePath), NormalizePath(candidate.TargetRelativePath)))
                kept.Add(NormalizePath(file.RelativePath));
        }

        HashSet<string> ours = previous.Where(folder.Matches).Select(file => NormalizePath(file.RelativePath)).ToHashSet(paths);
        bool Free(string path) => !folder.Exists(path) || (ours.Contains(path) && !kept.Contains(path));

        List<PackageStep> steps = [];
        HashSet<string> claimed = new(paths);
        foreach (PackageCandidate candidate in chosen)
        {
            string target = NormalizePath(candidate.TargetRelativePath);
            previousBySegment.TryGetValue(candidate.SegmentId, out PackagedTakeFile? file);
            bool fileIsOurs = file is not null && folder.Matches(file);
            if (!claimed.Add(target))
            {
                steps.Add(new PackageStep(PackageAction.Skip, target, Candidate: candidate, Previous: file, Reason: "Another segment has the same file name."));
                continue;
            }

            if (file is not null && fileIsOurs && IsSameTake(file, candidate))
            {
                string from = NormalizePath(file.RelativePath);
                if (paths.Equals(from, target))
                {
                    steps.Add(new PackageStep(PackageAction.Keep, target, Candidate: candidate, Previous: file));
                    continue;
                }

                steps.Add(Free(target)
                    ? new PackageStep(PackageAction.Move, target, from, candidate, file)
                    : new PackageStep(PackageAction.Skip, target, from, candidate, file, "A file VoiceBench didn't write is in the way."));
                continue;
            }

            if (file is not null && !fileIsOurs && folder.Exists(file.RelativePath)
                && paths.Equals(NormalizePath(file.RelativePath), target))
            {
                steps.Add(new PackageStep(PackageAction.Skip, target, Candidate: candidate, Previous: file,
                    Reason: "The packaged file was changed outside VoiceBench, so it was left as it is."));
                continue;
            }

            if (!Free(target))
            {
                // Exactly the approved take already sits there (an earlier run stopped before
                // recording it): take it over instead of calling it someone else's file.
                if (Adoptable(folder, candidate, target) is PackagedTakeFile adopted)
                {
                    steps.Add(new PackageStep(PackageAction.Keep, target, Candidate: candidate, Previous: adopted));
                    continue;
                }

                steps.Add(new PackageStep(PackageAction.Skip, target, Candidate: candidate, Previous: file,
                    Reason: "A file VoiceBench didn't write is in the way."));
                continue;
            }

            steps.Add(new PackageStep(PackageAction.Copy, target, Candidate: candidate, Previous: file));
            // The take changed and its name too: the old file goes, once the new one is in place.
            if (file is not null && !paths.Equals(NormalizePath(file.RelativePath), target) && folder.Exists(file.RelativePath))
            {
                steps.Add(fileIsOurs
                    ? new PackageStep(PackageAction.Remove, NormalizePath(file.RelativePath), Previous: file)
                    : new PackageStep(PackageAction.Skip, NormalizePath(file.RelativePath), Previous: file,
                        Reason: "Changed outside VoiceBench, so it was left in place."));
            }
        }

        foreach (PackagedTakeFile file in previous.Where(file => !chosenSegments.Contains(file.SegmentId)))
        {
            string path = NormalizePath(file.RelativePath);
            // Removals run before anything is moved or copied in, so a freed name can be reused.
            if (!folder.Exists(path)) continue;
            steps.Add(folder.Matches(file)
                ? new PackageStep(PackageAction.Remove, path, Previous: file)
                : new PackageStep(PackageAction.Skip, path, Previous: file, Reason: "Changed outside VoiceBench, so it was left in place."));
        }

        return new ApprovedTakePackagePlan(steps);
    }

    private static PackagedTakeFile? Adoptable(PackageFolderState folder, PackageCandidate candidate, string target)
    {
        if (folder.SourceHashes is null || candidate.SourceRelativePath is null
            || !folder.FileHashes.TryGetValue(target, out string? onDisk)
            || !folder.SourceHashes.TryGetValue(NormalizePath(candidate.SourceRelativePath), out string? source)
            || !string.Equals(onDisk, source, StringComparison.OrdinalIgnoreCase))
            return null;
        return new PackagedTakeFile
        {
            RelativePath = target,
            ChapterId = candidate.ChapterId,
            SegmentId = candidate.SegmentId,
            RenderId = candidate.ApprovedTake!.RenderId,
            TakeNumber = candidate.ApprovedTake.TakeNumber,
            SourceRelativePath = candidate.SourceRelativePath,
            Sha256 = onDisk,
            DurationSeconds = candidate.ApprovedTake.Duration.TotalSeconds,
        };
    }

    private static bool IsSameTake(PackagedTakeFile file, PackageCandidate candidate) =>
        file.RenderId == candidate.ApprovedTake?.RenderId
        && string.Equals(NormalizePath(file.SourceRelativePath), NormalizePath(candidate.SourceRelativePath ?? string.Empty), StringComparison.OrdinalIgnoreCase);
}
