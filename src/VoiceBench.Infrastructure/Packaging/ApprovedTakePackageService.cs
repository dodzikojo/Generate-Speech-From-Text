using System.Security.Cryptography;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Packaging;

public sealed record ApprovedTakePackageResult(
    string PackageRoot,
    int PackagedTakes,
    int Copied,
    int Moved,
    int Removed,
    int Unchanged,
    long PackagedBytes,
    IReadOnlyList<string> LeftUntouched);

public sealed record PackageProgress(int Done, int Total, string CurrentFile);

/// <summary>Raised when a folder holds a package of a different project.</summary>
public sealed class PackageFolderConflictException(string message) : IOException(message);

/// <summary>
/// Copies approved takes into a package folder and keeps it in sync on later runs. Files are copied
/// to a temporary name, verified, then moved into place; the manifest is written last.
/// </summary>
public sealed class ApprovedTakePackageService(TimeProvider? timeProvider = null)
{
    public const string DefaultFolderSuffix = " - approved takes";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The package folder for a chosen location: the location itself when it already holds a
    /// package, otherwise a "&lt;project&gt; - approved takes" folder inside it.
    /// </summary>
    public static string ResolvePackageRoot(string chosenFolder, string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chosenFolder);
        string chosen = Path.GetFullPath(chosenFolder);
        if (File.Exists(Path.Combine(chosen, ApprovedTakePackageManifest.FileName))) return chosen;
        return Path.Combine(chosen, ApprovedTakePackageRules.SafeName(projectName) + DefaultFolderSuffix);
    }

    /// <summary>Reads what the package folder holds, hashing only files a package could touch.</summary>
    public async Task<PackageFolderState> ReadFolderAsync(
        string projectRoot,
        string packageRoot,
        VoiceBenchProjectDocument project,
        IEnumerable<PackageCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(project);
        string root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root)) return PackageFolderState.Empty;
        ApprovedTakePackageManifest? manifest = await ReadManifestAsync(root, cancellationToken).ConfigureAwait(false);
        if (manifest is not null && manifest.ProjectId != project.ProjectId)
            throw new PackageFolderConflictException(
                $"This folder holds approved takes from “{manifest.ProjectName}”. Choose an empty folder or this project's package folder.");

        List<PackageCandidate> packageable = candidates.Where(candidate => candidate.CanPackage).ToList();
        IEnumerable<string> paths = (manifest?.Files.Select(file => file.RelativePath) ?? [])
            .Concat(packageable.Select(candidate => candidate.TargetRelativePath))
            .Select(ApprovedTakePackageRules.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full = Resolve(root, path);
            if (File.Exists(full)) hashes[path] = await HashAsync(full, cancellationToken).ConfigureAwait(false);
        }

        // A file already at a take's name that isn't recorded (an earlier run stopped part-way) is
        // adopted when it is exactly the approved take, rather than treated as someone else's file.
        HashSet<string> recorded = (manifest?.Files ?? []).Select(file => ApprovedTakePackageRules.NormalizePath(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageCandidate candidate in packageable)
        {
            string target = ApprovedTakePackageRules.NormalizePath(candidate.TargetRelativePath);
            if (!hashes.ContainsKey(target) || recorded.Contains(target)) continue;
            string source = VoiceBenchProjectLayout.ResolveRelativePath(Path.GetFullPath(projectRoot), candidate.SourceRelativePath!);
            if (File.Exists(source))
                sources[ApprovedTakePackageRules.NormalizePath(candidate.SourceRelativePath!)] = await HashAsync(source, cancellationToken).ConfigureAwait(false);
        }

        return new PackageFolderState(manifest, hashes, sources);
    }

    public async Task<ApprovedTakePackageResult> ApplyAsync(
        string projectRoot,
        string packageRoot,
        VoiceBenchProjectDocument project,
        ApprovedTakePackagePlan plan,
        PackageFolderState folder,
        IProgress<PackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(folder);
        string projectFull = Path.GetFullPath(projectRoot);
        string root = Path.GetFullPath(packageRoot);
        Directory.CreateDirectory(root);
        StringComparer paths = StringComparer.OrdinalIgnoreCase;
        List<string> leftUntouched = plan.Skipped.Select(step => $"{step.RelativePath}: {step.Reason}").ToList();
        HashSet<string> touchedFolders = new(paths);
        int total = plan.Steps.Count(step => step.Action != PackageAction.Skip);
        int done = 0;
        int copied = 0, moved = 0, removed = 0, unchanged = 0;
        void Report(string file) => progress?.Report(new PackageProgress(++done, total, file));

        // What the package holds, by path: every file VoiceBench wrote that still matches. Each step
        // updates it as it succeeds, and it becomes the manifest however the run ends.
        Dictionary<string, PackagedTakeFile> state = new(paths);
        foreach (PackagedTakeFile file in folder.Manifest?.Files ?? [])
            if (folder.Matches(file)) state[ApprovedTakePackageRules.NormalizePath(file.RelativePath)] = file;

        // Names another step needs: removals that free them run first, the rest only at the end.
        HashSet<string> targets = plan.Steps.Where(step => step.Action is PackageAction.Move or PackageAction.Copy)
            .Select(step => step.RelativePath).ToHashSet(paths);
        List<(PackageStep Step, string Parked)> parked = [];
        HashSet<string> placed = new(paths);
        try
        {
            // 1. Park files that move, so two segments can swap names.
            foreach (PackageStep step in plan.Steps.Where(step => step.Action == PackageAction.Move))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string from = Resolve(root, step.FromRelativePath!);
                if (!StillOurs(from, step.Previous!))
                {
                    leftUntouched.Add($"{step.FromRelativePath}: changed while packaging, so it was left as it is.");
                    continue;
                }

                string temporary = Path.Combine(root, $".voicebench-move-{Guid.NewGuid():N}.tmp");
                File.Move(from, temporary);
                touchedFolders.Add(Path.GetDirectoryName(from)!);
                parked.Add((step, temporary));
            }

            // 2. Free names that moved or copied takes are about to use.
            foreach (PackageStep step in plan.Steps.Where(step => step.Action == PackageAction.Remove && targets.Contains(step.RelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Remove(step)) removed++;
                Report(step.RelativePath);
            }

            // 3. Put moved files under their new names.
            foreach ((PackageStep step, string temporary) in parked)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Resolve(root, step.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    leftUntouched.Add($"{step.RelativePath}: a file appeared there while packaging; the take kept its old name.");
                }
                else
                {
                    File.Move(temporary, target);
                    placed.Add(temporary);
                    state.Remove(ApprovedTakePackageRules.NormalizePath(step.FromRelativePath!));
                    state[step.RelativePath] = step.Previous! with { RelativePath = step.RelativePath, ChapterId = step.Candidate!.ChapterId };
                    moved++;
                }

                Report(step.RelativePath);
            }

            // 4. Keep and copy.
            foreach (PackageStep step in plan.Steps.Where(step => step.Action is PackageAction.Keep or PackageAction.Copy))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageCandidate candidate = step.Candidate!;
                string target = Resolve(root, step.RelativePath);
                if (step.Action == PackageAction.Keep)
                {
                    PackagedTakeFile kept = step.Previous! with { RelativePath = step.RelativePath, ChapterId = candidate.ChapterId };
                    if (kept.Bytes == 0 && File.Exists(target)) kept = kept with { Bytes = new FileInfo(target).Length };
                    state[step.RelativePath] = kept;
                    unchanged++;
                    Report(step.RelativePath);
                    continue;
                }

                // Overwrite only a file VoiceBench wrote and that still matches what it wrote.
                if (File.Exists(target) && !(state.TryGetValue(step.RelativePath, out PackagedTakeFile? existing) && StillOurs(target, existing)))
                {
                    leftUntouched.Add($"{step.RelativePath}: a file VoiceBench didn't write is in the way.");
                    Report(step.RelativePath);
                    continue;
                }

                string source = VoiceBenchProjectLayout.ResolveRelativePath(projectFull, candidate.SourceRelativePath!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".voicebench-copy-{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(source, temporary);
                    string sourceHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
                    string copyHash = await HashAsync(temporary, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(sourceHash, copyHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"The copy of “{candidate.SegmentTitle}” did not match the approved take.");
                    File.Move(temporary, target, overwrite: true);
                    state[step.RelativePath] = new PackagedTakeFile
                    {
                        RelativePath = step.RelativePath,
                        ChapterId = candidate.ChapterId,
                        SegmentId = candidate.SegmentId,
                        RenderId = candidate.ApprovedTake!.RenderId,
                        TakeNumber = candidate.ApprovedTake.TakeNumber,
                        SourceRelativePath = candidate.SourceRelativePath!,
                        Sha256 = copyHash,
                        Bytes = new FileInfo(target).Length,
                        DurationSeconds = candidate.ApprovedTake.Duration.TotalSeconds,
                    };
                    copied++;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }

                Report(step.RelativePath);
            }

            // 5. Only now that every replacement is in place, remove old copies and files of
            //    segments that left the package.
            foreach (PackageStep step in plan.Steps.Where(step => step.Action == PackageAction.Remove && !targets.Contains(step.RelativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Remove(step)) removed++;
                Report(step.RelativePath);
            }
        }
        finally
        {
            // A run that stopped part-way puts parked files back and still records what it holds,
            // so the next run picks up from here instead of treating its own files as foreign.
            // A parked file goes to its new name when that is free (in a swap its old name may already
            // be taken by the other file), otherwise back to its old name.
            foreach ((PackageStep step, string temporary) in parked.Where(item => !placed.Contains(item.Parked) && File.Exists(item.Parked)))
            {
                string target = Resolve(root, step.RelativePath);
                string from = Resolve(root, step.FromRelativePath!);
                try
                {
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Move(temporary, target);
                        state.Remove(ApprovedTakePackageRules.NormalizePath(step.FromRelativePath!));
                        state[step.RelativePath] = step.Previous! with { RelativePath = step.RelativePath, ChapterId = step.Candidate!.ChapterId };
                        moved++;
                    }
                    else if (!File.Exists(from))
                    {
                        File.Move(temporary, from);
                    }
                    else
                    {
                        leftUntouched.Add($"{step.FromRelativePath}: kept as {Path.GetFileName(temporary)} because neither name was free.");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    leftUntouched.Add($"{step.FromRelativePath}: kept as {Path.GetFileName(temporary)} because it couldn't be put back ({exception.Message}).");
                }
            }

            foreach (string directory in touchedFolders.OrderByDescending(path => path.Length))
            {
                if (!string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }

            ApprovedTakePackageManifest manifest = new()
            {
                ProjectId = project.ProjectId,
                ProjectName = project.Name,
                PackagedAt = _timeProvider.GetUtcNow(),
                Files = state.Values
                    .Where(file => File.Exists(Resolve(root, file.RelativePath)))
                    .OrderBy(file => file.RelativePath, paths)
                    .ToList(),
            };
            await WriteManifestAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
        }

        List<PackagedTakeFile> files = state.Values.Where(file => File.Exists(Resolve(root, file.RelativePath))).ToList();
        return new ApprovedTakePackageResult(root, files.Count, copied, moved, removed, unchanged, files.Sum(file => file.Bytes), leftUntouched);

        bool Remove(PackageStep step)
        {
            string full = Resolve(root, step.RelativePath);
            if (!File.Exists(full) || !StillOurs(full, step.Previous!)) return false;
            File.Delete(full);
            touchedFolders.Add(Path.GetDirectoryName(full)!);
            state.Remove(step.RelativePath);
            return true;
        }
    }

    public static async Task<ApprovedTakePackageManifest?> ReadManifestAsync(string packageRoot, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(packageRoot, ApprovedTakePackageManifest.FileName);
        if (!File.Exists(path)) return null;
        ApprovedTakePackageManifest? manifest;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            manifest = await JsonSerializer.DeserializeAsync<ApprovedTakePackageManifest>(stream, ProjectJson.SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new PackageFolderConflictException($"The package record {ApprovedTakePackageManifest.FileName} in this folder can't be read ({exception.Message}). Move it aside or choose another folder.");
        }

        if (manifest is null) return null;
        List<PackagedTakeFile> files = (manifest.Files ?? []).Where(file => file is not null).ToList();
        foreach (PackagedTakeFile file in files)
        {
            bool inside;
            try { inside = !string.IsNullOrWhiteSpace(file.RelativePath) && Resolve(Path.GetFullPath(packageRoot), file.RelativePath).Length > 0; }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException) { inside = false; }
            if (!inside)
                throw new PackageFolderConflictException($"The package record {ApprovedTakePackageManifest.FileName} lists a file outside the package folder. Move it aside or choose another folder.");
        }

        return manifest with { Files = files };
    }

    private async Task WriteManifestAsync(string root, ApprovedTakePackageManifest manifest, CancellationToken cancellationToken)
    {
        string path = Path.Combine(root, ApprovedTakePackageManifest.FileName);
        string temporary = Path.Combine(root, $".voicebench-package-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, manifest, ProjectJson.SerializerOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool StillOurs(string fullPath, PackagedTakeFile file) =>
        File.Exists(fullPath) && string.Equals(HashSync(fullPath), file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static string Resolve(string root, string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(root, ApprovedTakePackageRules.NormalizePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Package path escapes the package folder: {relativePath}");
        return full;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string HashSync(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
