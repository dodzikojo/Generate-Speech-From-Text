using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class FileAuthoringTransactionManager(
    TimeProvider? timeProvider = null,
    int historyLimit = 100) : IAuthoringTransactionManager
{
    private const string HistoryFileName = "history.json";
    private const string PendingFileName = "pending.json";
    private const string SnapshotMetadataFileName = "snapshot.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _historyLimit = historyLimit > 0 ? historyLimit : throw new ArgumentOutOfRangeException(nameof(historyLimit));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record JournalDocument(
        int SchemaVersion,
        List<AuthoringTransactionRecord> Transactions,
        int Cursor,
        Guid? LastOperationId);

    private sealed record PendingOperation(
        Guid OperationId,
        string RecoverySnapshotRelativePath,
        DateTimeOffset StartedAt);

    private sealed record SnapshotFile(string RelativePath, string Sha256);

    private sealed record SnapshotDocument(string StateSha256, List<SnapshotFile> Files);

    public async Task<AuthoringTransactionState> GetStateAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JournalDocument journal = await LoadJournalAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            return ToState(projectRoot, journal);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExecuteAsync(
        string projectRoot,
        string label,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverInterruptedCoreAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            JournalDocument journal = await LoadJournalAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            Guid transactionId = Guid.NewGuid();
            Guid operationId = Guid.NewGuid();
            string transactionRelativeDirectory = TransactionRelativeDirectory(transactionId);
            string transactionDirectory = ResolveInternalPath(projectRoot, transactionRelativeDirectory);
            string beforeRelativePath = $"{transactionRelativeDirectory}/before";
            string afterRelativePath = $"{transactionRelativeDirectory}/after";
            Directory.CreateDirectory(transactionDirectory);
            SnapshotDocument before = await CaptureSnapshotAsync(
                projectRoot,
                ResolveInternalPath(projectRoot, beforeRelativePath),
                cancellationToken).ConfigureAwait(false);
            await SavePendingAsync(
                projectRoot,
                new PendingOperation(operationId, beforeRelativePath, _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            bool committed = false;
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                SnapshotDocument after = await CaptureSnapshotAsync(
                    projectRoot,
                    ResolveInternalPath(projectRoot, afterRelativePath),
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(before.StateSha256, after.StateSha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryDeletePending(projectRoot)) TryDeleteDirectory(transactionDirectory);
                    return;
                }

                List<AuthoringTransactionRecord> abandoned = journal.Transactions.Skip(journal.Cursor).ToList();
                List<AuthoringTransactionRecord> transactions = journal.Transactions.Take(journal.Cursor).ToList();
                transactions.Add(new AuthoringTransactionRecord(
                    transactionId,
                    label.Trim(),
                    _timeProvider.GetUtcNow(),
                    before.StateSha256,
                    after.StateSha256));
                int cursor = transactions.Count;
                while (transactions.Count > _historyLimit)
                {
                    DeleteTransactionDirectory(projectRoot, transactions[0].TransactionId);
                    transactions.RemoveAt(0);
                    cursor--;
                }

                JournalDocument committedJournal = new(
                    AuthoringTransactionState.CurrentSchemaVersion,
                    transactions,
                    cursor,
                    operationId);
                await SaveJournalAsync(projectRoot, committedJournal, cancellationToken).ConfigureAwait(false);
                committed = true;
                TryDeletePending(projectRoot);
                foreach (AuthoringTransactionRecord record in abandoned)
                {
                    DeleteTransactionDirectory(projectRoot, record.TransactionId);
                }
            }
            catch
            {
                if (!committed)
                {
                    await RestoreAfterFailureAsync(projectRoot, beforeRelativePath).ConfigureAwait(false);
                    TryDeletePending(projectRoot);
                    TryDeleteDirectory(transactionDirectory);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AuthoringTransactionRecord> UndoAsync(
        string projectRoot,
        CancellationToken cancellationToken = default) =>
        MoveCursorAsync(projectRoot, undo: true, cancellationToken);

    public Task<AuthoringTransactionRecord> RedoAsync(
        string projectRoot,
        CancellationToken cancellationToken = default) =>
        MoveCursorAsync(projectRoot, undo: false, cancellationToken);

    public async Task<bool> RecoverInterruptedAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecoverInterruptedCoreAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AuthoringTransactionRecord> MoveCursorAsync(
        string projectRoot,
        bool undo,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverInterruptedCoreAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            JournalDocument journal = await LoadJournalAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            if (undo && journal.Cursor == 0) throw new InvalidOperationException("There is no saved authoring action to undo.");
            if (!undo && journal.Cursor >= journal.Transactions.Count) throw new InvalidOperationException("There is no saved authoring action to redo.");

            int transactionIndex = undo ? journal.Cursor - 1 : journal.Cursor;
            AuthoringTransactionRecord transaction = journal.Transactions[transactionIndex];
            string targetRelativePath = $"{TransactionRelativeDirectory(transaction.TransactionId)}/{(undo ? "before" : "after")}";
            string recoveryRelativePath = $"{VoiceBenchProjectLayout.AuthoringTransactionsDirectory}/recovery/{Guid.NewGuid():N}";
            SnapshotDocument current = await CaptureSnapshotAsync(
                projectRoot,
                ResolveInternalPath(projectRoot, recoveryRelativePath),
                cancellationToken).ConfigureAwait(false);
            string expectedHash = undo ? transaction.AfterStateSha256 : transaction.BeforeStateSha256;
            if (!string.Equals(current.StateSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(ResolveInternalPath(projectRoot, recoveryRelativePath), recursive: true);
                throw new InvalidOperationException(
                    "The project changed outside VoiceBench's saved transaction history. Reload or save the current state before undoing or redoing.");
            }

            Guid operationId = Guid.NewGuid();
            await SavePendingAsync(
                projectRoot,
                new PendingOperation(operationId, recoveryRelativePath, _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            bool committed = false;
            try
            {
                await ApplySnapshotWithRollbackAsync(
                    projectRoot,
                    ResolveInternalPath(projectRoot, targetRelativePath),
                    preserveOperationalManifest: true,
                    cancellationToken).ConfigureAwait(false);
                JournalDocument committedJournal = journal with
                {
                    Cursor = undo ? journal.Cursor - 1 : journal.Cursor + 1,
                    LastOperationId = operationId,
                };
                await SaveJournalAsync(projectRoot, committedJournal, cancellationToken).ConfigureAwait(false);
                committed = true;
                if (TryDeletePending(projectRoot))
                {
                    TryDeleteDirectory(ResolveInternalPath(projectRoot, recoveryRelativePath));
                }
                return transaction;
            }
            catch
            {
                if (!committed)
                {
                    await RestoreAfterFailureAsync(projectRoot, recoveryRelativePath).ConfigureAwait(false);
                    if (TryDeletePending(projectRoot))
                    {
                        TryDeleteDirectory(ResolveInternalPath(projectRoot, recoveryRelativePath));
                    }
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RecoverInterruptedCoreAsync(string projectRoot, CancellationToken cancellationToken)
    {
        string pendingPath = PendingPath(projectRoot);
        if (!File.Exists(pendingPath)) return false;
        PendingOperation pending = await ReadJsonAsync<PendingOperation>(pendingPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The authoring transaction recovery marker is empty.");
        JournalDocument journal = await LoadJournalAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (journal.LastOperationId != pending.OperationId)
        {
            await ApplySnapshotWithRollbackAsync(
                projectRoot,
                ResolveInternalPath(projectRoot, pending.RecoverySnapshotRelativePath),
                preserveOperationalManifest: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (!TryDeletePending(projectRoot)) return true;
        string recoveryPath = ResolveInternalPath(projectRoot, pending.RecoverySnapshotRelativePath);
        if (pending.RecoverySnapshotRelativePath.Contains("/recovery/", StringComparison.Ordinal))
        {
            TryDeleteDirectory(recoveryPath);
        }
        else if (pending.RecoverySnapshotRelativePath.Contains("/entries/", StringComparison.Ordinal))
        {
            TryDeleteDirectory(Directory.GetParent(recoveryPath)?.FullName);
        }
        return true;
    }

    private static async Task<SnapshotDocument> CaptureSnapshotAsync(
        string projectRoot,
        string destination,
        CancellationToken cancellationToken)
    {
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            List<SnapshotFile> files = [];
            foreach (string source in EnumerateManagedFiles(projectRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, source));
                string target = ResolveSnapshotPath(temporary, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
                string sha256 = relativePath.Equals(VoiceBenchProjectLayout.ManifestFileName, StringComparison.OrdinalIgnoreCase)
                    ? await ManifestAuthoringSha256Async(source, cancellationToken).ConfigureAwait(false)
                    : await FileSha256Async(source, cancellationToken).ConfigureAwait(false);
                files.Add(new SnapshotFile(relativePath, sha256));
            }

            files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            SnapshotDocument snapshot = new(ComputeStateHash(files), files);
            await WriteJsonAtomicAsync(
                Path.Combine(temporary, SnapshotMetadataFileName),
                snapshot,
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(temporary, destination);
            return snapshot;
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static async Task ApplySnapshotAsync(
        string projectRoot,
        string snapshotDirectory,
        bool preserveOperationalManifest,
        CancellationToken cancellationToken)
    {
        string metadataPath = Path.Combine(snapshotDirectory, SnapshotMetadataFileName);
        SnapshotDocument snapshot = await ReadJsonAsync<SnapshotDocument>(metadataPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The authoring transaction snapshot is empty.");
        HashSet<string> targetPaths = snapshot.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string current in EnumerateManagedFiles(projectRoot))
        {
            string relativePath = VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, current));
            if (!targetPaths.Contains(relativePath)) File.Delete(current);
        }

        foreach (SnapshotFile file in snapshot.Files.Where(file =>
                     !file.RelativePath.Equals(VoiceBenchProjectLayout.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopySnapshotFile(projectRoot, snapshotDirectory, file);
        }
        SnapshotFile manifest = snapshot.Files.Single(file =>
            file.RelativePath.Equals(VoiceBenchProjectLayout.ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (preserveOperationalManifest)
        {
            await ApplyAuthoringManifestAsync(
                projectRoot,
                ResolveSnapshotPath(snapshotDirectory, manifest.RelativePath),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            CopySnapshotFile(projectRoot, snapshotDirectory, manifest);
        }
        RemoveEmptyManagedDirectories(projectRoot);
    }

    private static void CopySnapshotFile(string projectRoot, string snapshotDirectory, SnapshotFile file)
    {
        string source = ResolveSnapshotPath(snapshotDirectory, file.RelativePath);
        string target = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        File.Copy(source, temporary, overwrite: false);
        File.Move(temporary, target, overwrite: true);
    }

    private static IEnumerable<string> EnumerateManagedFiles(string projectRoot)
    {
        string manifest = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, VoiceBenchProjectLayout.ManifestFileName);
        if (!File.Exists(manifest)) throw new FileNotFoundException("VoiceBench project manifest was not found.", manifest);
        yield return manifest;
        foreach (string relativeRoot in new[]
                 {
                     VoiceBenchProjectLayout.SourceReferencesDirectory,
                     VoiceBenchProjectLayout.ScriptsDirectory,
                 })
        {
            string root = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativeRoot);
            if (!Directory.Exists(root)) continue;
            foreach (string file in EnumerateFilesWithoutReparsePoints(root)) yield return file;
        }
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current)) yield return file;
            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
            }
        }
    }

    private static void RemoveEmptyManagedDirectories(string projectRoot)
    {
        foreach (string relativeRoot in new[]
                 {
                     VoiceBenchProjectLayout.SourceReferencesDirectory,
                     VoiceBenchProjectLayout.ScriptsDirectory,
                 })
        {
            string root = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativeRoot);
            if (!Directory.Exists(root)) continue;
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
    }

    private static string ComputeStateHash(IEnumerable<SnapshotFile> files)
    {
        StringBuilder state = new();
        foreach (SnapshotFile file in files)
        {
            state.Append(file.RelativePath).Append('\0').Append(file.Sha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString()))).ToLowerInvariant();
    }

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<string> ManifestAuthoringSha256Async(string path, CancellationToken cancellationToken)
    {
        VoiceBenchProjectDocument document = await ReadProjectDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        VoiceBenchProjectDocument authoring = document with
        {
            UpdatedAt = default,
            AudioStandard = AudioStandard.NarrationDefault,
            VoicePresets = [],
            DefaultVoicePresetId = null,
            Segments = document.Segments.Select(segment => segment with
            {
                Status = SegmentStatus.Draft,
                VoicePresetId = null,
                Directions = null,
                ApprovedRenderId = null,
                RenderHistory = [],
            }).ToList(),
        };
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(authoring, ProjectJson.SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(serialized)).ToLowerInvariant();
    }

    private static async Task ApplyAuthoringManifestAsync(
        string projectRoot,
        string targetManifestPath,
        CancellationToken cancellationToken)
    {
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument current = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        VoiceBenchProjectDocument target = await ReadProjectDocumentAsync(targetManifestPath, cancellationToken).ConfigureAwait(false);
        Dictionary<string, SegmentDocument> currentSegments = current.Segments.ToDictionary(
            segment => segment.SegmentId,
            StringComparer.Ordinal);
        List<SegmentDocument> mergedSegments = [];
        foreach (SegmentDocument targetSegment in target.Segments)
        {
            if (!currentSegments.TryGetValue(targetSegment.SegmentId, out SegmentDocument? currentSegment))
            {
                mergedSegments.Add(targetSegment);
                continue;
            }

            SegmentDocument currentWithTargetAuthoring = currentSegment with
            {
                ChapterId = targetSegment.ChapterId,
                Title = targetSegment.Title,
                ScriptRelativePath = targetSegment.ScriptRelativePath,
                WritingRevisions = targetSegment.WritingRevisions,
            };
            mergedSegments.Add(SegmentStateRules.WithSourceHashes(
                currentWithTargetAuthoring,
                targetSegment.SourceSha256,
                targetSegment.AuthoringSha256));
        }

        VoiceBenchProjectDocument merged = target with
        {
            UpdatedAt = current.UpdatedAt,
            AudioStandard = current.AudioStandard,
            VoicePresets = current.VoicePresets,
            DefaultVoicePresetId = current.DefaultVoicePresetId,
            Segments = mergedSegments,
        };
        await repository.SaveAsync(projectRoot, merged, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VoiceBenchProjectDocument> ReadProjectDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<VoiceBenchProjectDocument>(
                   stream,
                   ProjectJson.SerializerOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("The transaction manifest snapshot is empty.");
    }

    private static async Task ApplySnapshotWithRollbackAsync(
        string projectRoot,
        string targetSnapshotDirectory,
        bool preserveOperationalManifest,
        CancellationToken cancellationToken)
    {
        string rollbackRelativePath = $"{VoiceBenchProjectLayout.AuthoringTransactionsDirectory}/recovery/{Guid.NewGuid():N}";
        string rollbackPath = ResolveInternalPath(projectRoot, rollbackRelativePath);
        await CaptureSnapshotAsync(projectRoot, rollbackPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplySnapshotAsync(
                projectRoot,
                targetSnapshotDirectory,
                preserveOperationalManifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception applyException)
        {
            try
            {
                await ApplySnapshotAsync(
                    projectRoot,
                    rollbackPath,
                    preserveOperationalManifest: false,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The authoring snapshot failed and VoiceBench could not fully restore the state present before the attempt.",
                    applyException,
                    rollbackException);
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(rollbackPath);
        }
    }

    private async Task RestoreAfterFailureAsync(string projectRoot, string snapshotRelativePath)
    {
        await ApplySnapshotWithRollbackAsync(
            projectRoot,
            ResolveInternalPath(projectRoot, snapshotRelativePath),
            preserveOperationalManifest: false,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static AuthoringTransactionState ToState(string projectRoot, JournalDocument journal) => new(
        journal.SchemaVersion,
        journal.Transactions,
        journal.Cursor,
        File.Exists(PendingPath(projectRoot)));

    private static async Task<JournalDocument> LoadJournalAsync(string projectRoot, CancellationToken cancellationToken)
    {
        string path = HistoryPath(projectRoot);
        if (!File.Exists(path))
        {
            return new JournalDocument(AuthoringTransactionState.CurrentSchemaVersion, [], 0, null);
        }

        JournalDocument journal = await ReadJsonAsync<JournalDocument>(path, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The authoring transaction history is empty.");
        if (journal.SchemaVersion != AuthoringTransactionState.CurrentSchemaVersion ||
            journal.Cursor < 0 || journal.Cursor > journal.Transactions.Count)
        {
            throw new InvalidDataException("The authoring transaction history is invalid or unsupported.");
        }
        return journal;
    }

    private static Task SaveJournalAsync(string projectRoot, JournalDocument journal, CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(HistoryPath(projectRoot), journal, cancellationToken);

    private static Task SavePendingAsync(string projectRoot, PendingOperation pending, CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(PendingPath(projectRoot), pending, cancellationToken);

    private static bool TryDeletePending(string projectRoot)
    {
        try
        {
            string path = PendingPath(projectRoot);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(value, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveInternalPath(string projectRoot, string relativePath)
    {
        if (!relativePath.StartsWith($"{VoiceBenchProjectLayout.InternalDirectory}/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transaction paths must remain inside VoiceBench's internal project directory.");
        }
        return VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativePath);
    }

    private static string ResolveSnapshotPath(string snapshotRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Snapshot paths must be relative.");
        string root = Path.GetFullPath(snapshotRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A transaction snapshot path escapes its root.");
        }
        return fullPath;
    }

    private static string TransactionRelativeDirectory(Guid transactionId) =>
        $"{VoiceBenchProjectLayout.AuthoringTransactionsDirectory}/entries/{transactionId:N}";

    private static string HistoryPath(string projectRoot) => ResolveInternalPath(
        projectRoot,
        $"{VoiceBenchProjectLayout.AuthoringTransactionsDirectory}/{HistoryFileName}");

    private static string PendingPath(string projectRoot) => ResolveInternalPath(
        projectRoot,
        $"{VoiceBenchProjectLayout.AuthoringTransactionsDirectory}/{PendingFileName}");

    private static void DeleteTransactionDirectory(string projectRoot, Guid transactionId)
    {
        string path = ResolveInternalPath(projectRoot, TransactionRelativeDirectory(transactionId));
        TryDeleteDirectory(path);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
