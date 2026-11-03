using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using IssueLab.VoiceBench.Audio;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Audio;

public sealed record PreparedAudioAsset(string PcmPath, string PeaksPath, long FrameCount, int OriginalChannels);
public sealed record WaveformPeak(float MinimumLeft, float MaximumLeft, float MinimumRight, float MaximumRight);
public sealed record WaveformPeakPage(long StartBucket, int FramesPerBucket, long TotalBuckets, IReadOnlyList<WaveformPeak> Peaks);

/// <summary>Imports original bytes and builds disposable, content-addressed decoded caches. Never uploads media.</summary>
public sealed class AudioAssetService(IProjectRepository repository, IProcessRunner runner, ToolchainPaths tools)
{
    public const int CacheVersion = 1;
    public const int PeakFrames = 256;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.OrdinalIgnoreCase);

    public Task<AudioAssetRecord> ImportAsync(string root, string sourcePath, string? segmentId = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCoreAsync(root, sourcePath, segmentId, cancellationToken), cancellationToken);

    private async Task<AudioAssetRecord> ImportCoreAsync(string root, string sourcePath, string? segmentId, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        var project = await repository.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        SegmentDocument? segment = segmentId is null ? null : project.Segments.Single(s => s.SegmentId == segmentId);
        Guid id = Guid.NewGuid();
        string directory = Path.Combine(root, "01-source", "audio");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{id:N}{Path.GetExtension(sourcePath).ToLowerInvariant()}");
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            string hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
            AudioProbe probe = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            AudioAssetRecord asset = new()
            {
                AssetId = id, Name = Path.GetFileName(sourcePath), RelativePath = Relative(root, path), Sha256 = hash,
                DurationSeconds = probe.Duration.TotalSeconds, Channels = probe.Channels, SampleRate = probe.SampleRate,
                CreatedAt = DateTimeOffset.UtcNow, SegmentId = segmentId,
                Origin = segment is null ? AudioAssetOrigin.ImportedMedia : AudioAssetOrigin.ImportedRecording,
            };
            RenderRecord? imported = null;
            if (segment is not null)
            {
                string scriptPath = VoiceBenchProjectLayout.ResolveRelativePath(root, segment.ScriptRelativePath);
                string authoring = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
                var parsed = new ProductionScriptParser().Parse(authoring);
                string sourceHash = SourceTextHasher.Compute(parsed.NarrationText);
                if (!string.Equals(sourceHash, segment.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The reference transcript changed outside VoiceBench. Refresh the project before linking a recording.");
                string snapshotRoot = Path.Combine(root, "03-render-copies", "imports", id.ToString("N"));
                Directory.CreateDirectory(snapshotRoot);
                string clean = Path.Combine(snapshotRoot, "clean.txt"), original = Path.Combine(snapshotRoot, "authoring.md");
                await File.WriteAllTextAsync(clean, parsed.NarrationText, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(original, authoring, cancellationToken).ConfigureAwait(false);
                imported = new RenderRecord
                {
                    RenderId = Guid.NewGuid(), Origin = RenderOrigin.Imported, ProviderId = "imported", ModelId = "recording",
                    Status = RenderStatus.Generated, SourceSha256 = sourceHash, AuthoringSourceSha256 = SourceTextHasher.Compute(authoring),
                    SubmittedTextSha256 = sourceHash, AudioSha256 = hash, RawAudioRelativePath = asset.RelativePath,
                    Duration = probe.Duration, SampleRate = probe.SampleRate, Channels = probe.Channels, CreatedAt = DateTimeOffset.UtcNow,
                    CleanSourceRelativePath = Relative(root, clean), AuthoringSourceRelativePath = Relative(root, original),
                    SubmittedTextRelativePath = Relative(root, clean),
                };
                asset = asset with { RenderId = imported.RenderId, SourceSha256 = sourceHash, CleanSourceRelativePath = imported.CleanSourceRelativePath,
                    AuthoringSourceRelativePath = imported.AuthoringSourceRelativePath, AuthoringSourceSha256 = imported.AuthoringSourceSha256 };
            }
            await ProjectDocumentUpdater.UpdateAsync(repository, root, latest =>
            {
                if (segmentId is null) return latest with { AudioAssets = [.. latest.AudioAssets, asset] };
                int index = latest.Segments.FindIndex(s => s.SegmentId == segmentId);
                if (index < 0 || !string.Equals(latest.Segments[index].SourceSha256, asset.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The reference transcript changed while importing audio.");
                var current = latest.Segments[index];
                var take = imported! with { TakeNumber = current.RenderHistory.Select(r => r.TakeNumber).DefaultIfEmpty().Max() + 1 };
                List<SegmentDocument> segments = [.. latest.Segments];
                segments[index] = current with { RenderHistory = [.. current.RenderHistory, take], Status = SegmentStatus.Rendered };
                return latest with { AudioAssets = [.. latest.AudioAssets, asset], Segments = segments };
            }, cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    public Task<PreparedAudioAsset> PrepareAsync(string root, AudioAssetRecord asset, CancellationToken cancellationToken = default) =>
        Task.Run(() => PrepareCoreAsync(root, asset, cancellationToken), cancellationToken);

    private async Task<PreparedAudioAsset> PrepareCoreAsync(string root, AudioAssetRecord asset, CancellationToken cancellationToken)
    {
        string input = VoiceBenchProjectLayout.ResolveRelativePath(root, asset.RelativePath);
        string hash = await HashAsync(input, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The audio source hash changed. Restore the original bytes or explicitly import a replacement.");
        string directory = Path.Combine(Path.GetFullPath(root), ".voicebench", "cache", "audio", $"v{CacheVersion}", hash);
        var gate = CacheGates.GetOrAdd(directory, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            string pcm = Path.Combine(directory, "stereo48.f32"), peaks = Path.Combine(directory, "peaks.bin"), metadata = Path.Combine(directory, "complete.json");
            if (File.Exists(metadata) && File.Exists(pcm) && File.Exists(peaks))
            {
                var cached = JsonSerializer.Deserialize<CacheMetadata>(await File.ReadAllTextAsync(metadata, cancellationToken).ConfigureAwait(false));
                if (cached is not null && cached.SourceHash == hash && new FileInfo(pcm).Length == cached.Frames * 8
                    && new FileInfo(peaks).Length == ((cached.Frames + PeakFrames - 1) / PeakFrames) * 16)
                    return new(pcm, peaks, cached.Frames, asset.Channels);
            }
            string suffix = $".{Guid.NewGuid():N}.tmp", pending = pcm + suffix, pendingPeaks = peaks + suffix;
            try
            {
                await RunAsync(tools.FfmpegPath, ["-hide_banner", "-nostdin", "-y", "-i", input, "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", pending], cancellationToken).ConfigureAwait(false);
                long frames = new FileInfo(pending).Length / 8;
                if (frames <= 0 || new FileInfo(pending).Length % 8 != 0) throw new InvalidDataException("The decoded audio is empty or truncated.");
                await Task.Run(() => WritePeaks(pending, pendingPeaks, cancellationToken), cancellationToken).ConfigureAwait(false);
                // A source replaced during decode cannot poison its previous content-addressed cache.
                if (!string.Equals(hash, await HashAsync(input, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The audio source changed while preparing it.");
                File.Move(pending, pcm, true); File.Move(pendingPeaks, peaks, true);
                string metaTemp = metadata + suffix;
                await File.WriteAllTextAsync(metaTemp, JsonSerializer.Serialize(new CacheMetadata(hash, frames)), cancellationToken).ConfigureAwait(false);
                File.Move(metaTemp, metadata, true);
                return new(pcm, peaks, frames, asset.Channels);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); if (File.Exists(pendingPeaks)) File.Delete(pendingPeaks); }
        }
        finally { gate.Release(); }
    }

    public Task<PreparedAudioAsset> PrepareFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => PrepareFileCoreAsync(path, cancellationToken), cancellationToken);

    private async Task<PreparedAudioAsset> PrepareFileCoreAsync(string path, CancellationToken cancellationToken)
    {
        // Isolated take/audition files use an operational cache outside authoring state.
        string full = Path.GetFullPath(path), hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        AudioProbe probe = await ProbeAsync(full, cancellationToken).ConfigureAwait(false);
        string root = Path.GetDirectoryName(full)!;
        for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, VoiceBenchProjectLayout.ManifestFileName))) { root = parent.FullName; break; }
        return await PrepareAsync(root, new AudioAssetRecord
        { RelativePath = Relative(root, full), Sha256 = hash, Channels = probe.Channels, SampleRate = probe.SampleRate, DurationSeconds = probe.Duration.TotalSeconds }, cancellationToken).ConfigureAwait(false);
    }

    public Task<WaveformPeakPage> ReadPeaksAsync(string root, AudioAssetRecord asset, long startBucket, int count, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadPeaksCoreAsync(root, asset, startBucket, count, cancellationToken), cancellationToken);

    /// <summary>Pages a previously hash-verified immutable cache without rehashing a long original for every viewport page.</summary>
    public Task<WaveformPeakPage> ReadPeaksAsync(PreparedAudioAsset prepared, long startBucket, int count, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadPreparedPeaksCoreAsync(prepared, startBucket, count, cancellationToken), cancellationToken);

    private async Task<WaveformPeakPage> ReadPeaksCoreAsync(string root, AudioAssetRecord asset, long startBucket, int count, CancellationToken cancellationToken)
    {
        if (startBucket < 0 || count is < 0 or > 8192) throw new ArgumentOutOfRangeException(nameof(count), "Request at most 8,192 peak buckets.");
        var prepared = await PrepareAsync(root, asset, cancellationToken).ConfigureAwait(false);
        return await ReadPreparedPeaksCoreAsync(prepared, startBucket, count, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WaveformPeakPage> ReadPreparedPeaksCoreAsync(PreparedAudioAsset prepared, long startBucket, int count, CancellationToken cancellationToken)
    {
        if (startBucket < 0 || count is < 0 or > 8192) throw new ArgumentOutOfRangeException(nameof(count), "Request at most 8,192 peak buckets.");
        long total = (prepared.FrameCount + PeakFrames - 1) / PeakFrames;
        int actual = (int)Math.Min(count, Math.Max(0, total - startBucket));
        byte[] bytes = new byte[actual * 16];
        await using var stream = new FileStream(prepared.PeaksPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = checked(startBucket * 16);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        WaveformPeak[] result = new WaveformPeak[actual];
        for (int i = 0; i < actual; i++) result[i] = new(BitConverter.ToSingle(bytes, i * 16), BitConverter.ToSingle(bytes, i * 16 + 4), BitConverter.ToSingle(bytes, i * 16 + 8), BitConverter.ToSingle(bytes, i * 16 + 12));
        return new(startBucket, PeakFrames, total, result);
    }

    public Task RelinkAsync(string root, AudioAssetRecord asset, string replacementPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => RelinkCoreAsync(root, asset, replacementPath, cancellationToken), cancellationToken);

    private static async Task RelinkCoreAsync(string root, AudioAssetRecord asset, string replacementPath, CancellationToken cancellationToken)
    {
        if (!string.Equals(await HashAsync(replacementPath, cancellationToken).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Relinking requires exactly matching original bytes. Import changed audio as a new asset.");
        string target = VoiceBenchProjectLayout.ResolveRelativePath(root, asset.RelativePath);
        if (File.Exists(target))
        {
            if (string.Equals(await HashAsync(target, cancellationToken).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("The managed source was changed. Preserve or remove that file before relinking the immutable original.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = File.OpenRead(replacementPath))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(await HashAsync(temp, cancellationToken).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Relink source changed during copy.");
            File.Move(temp, target, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WritePeaks(string pcm, string path, CancellationToken cancellationToken)
    {
        using var input = new DiskPcmSource(pcm, 2);
        using var output = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        float[] buffer = new float[PeakFrames * 2];
        for (long frame = 0; frame < input.FrameCount; frame += PeakFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.ReadFrames(frame, buffer);
            float minL = float.PositiveInfinity, maxL = float.NegativeInfinity, minR = float.PositiveInfinity, maxR = float.NegativeInfinity;
            for (int i = 0; i < count * 2; i += 2) { minL = Math.Min(minL, buffer[i]); maxL = Math.Max(maxL, buffer[i]); minR = Math.Min(minR, buffer[i + 1]); maxR = Math.Max(maxR, buffer[i + 1]); }
            output.Write(minL); output.Write(maxL); output.Write(minR); output.Write(maxR);
        }
    }
    private async Task<AudioProbe> ProbeAsync(string path, CancellationToken token) => AudioProbe.Parse((await RunAsync(tools.FfprobePath,
        ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels:format=duration", "-of", "json", path], token).ConfigureAwait(false)).StandardOutput);
    private async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var result = await runner.RunAsync(new(executable, arguments), token).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed with exit code {result.ExitCode}.");
        return result;
    }
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    internal static string Relative(string root, string path) => VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(root, path));
    private sealed record CacheMetadata(string SourceHash, long Frames);
}
