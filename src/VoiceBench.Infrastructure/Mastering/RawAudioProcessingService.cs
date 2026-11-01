using System.Security.Cryptography;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Mastering;

public sealed class RawAudioProcessingService(
    IProjectRepository repository,
    IProcessRunner processRunner)
{
    public async Task<RenderRecord> ProcessAsync(
        string projectRoot,
        string segmentId,
        Guid renderId,
        ToolchainPaths toolchain,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int segmentIndex = project.Segments.FindIndex(segment => segment.SegmentId == segmentId);
        if (segmentIndex < 0)
        {
            throw new KeyNotFoundException($"Segment '{segmentId}' was not found.");
        }

        SegmentDocument segment = project.Segments[segmentIndex];
        int renderIndex = segment.RenderHistory.FindIndex(render => render.RenderId == renderId);
        if (renderIndex < 0)
        {
            throw new KeyNotFoundException($"Render '{renderId}' was not found on segment '{segmentId}'.");
        }

        RenderRecord render = segment.RenderHistory[renderIndex];
        if (!string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath))
        {
            return render;
        }

        string rawPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, render.RawAudioRelativePath);
        string outputRelativePath = await ReserveOutputPathAsync(
            projectRoot,
            segmentId,
            render.TakeNumber,
            cancellationToken).ConfigureAwait(false);
        string outputPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, outputRelativePath);
        ProcessResult conversion = await processRunner.RunAsync(
            new ProcessRequest(
                toolchain.FfmpegPath,
                [
                    "-hide_banner", "-nostdin", "-n", "-i", rawPath,
                    "-ac", project.AudioStandard.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-ar", project.AudioStandard.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-c:a", project.AudioStandard.PcmBitsPerSample == 24 ? "pcm_s24le" : "pcm_s16le",
                    outputPath,
                ],
                Path.GetDirectoryName(outputPath)),
            cancellationToken).ConfigureAwait(false);
        if (!conversion.Succeeded || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"FFmpeg could not create the processed take. Raw audio remains at '{render.RawAudioRelativePath}'. " +
                conversion.StandardError);
        }

        ProcessResult probeResult = await processRunner.RunAsync(
            new ProcessRequest(
                toolchain.FfprobePath,
                ["-v", "error", "-show_entries", "stream=sample_rate,channels:format=duration", "-of", "json", outputPath],
                Path.GetDirectoryName(outputPath)),
            cancellationToken).ConfigureAwait(false);
        if (!probeResult.Succeeded)
        {
            throw new InvalidOperationException($"ffprobe could not verify the processed take: {probeResult.StandardError}");
        }

        AudioProbe probe = AudioProbe.Parse(probeResult.StandardOutput);
        string audioHash = await FfmpegToolchainManager.ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
        RenderRecord updatedRender = render with
        {
            ProcessedAudioRelativePath = outputRelativePath,
            SampleRate = probe.SampleRate,
            Channels = probe.Channels,
            Duration = probe.Duration,
            AudioSha256 = audioHash,
        };
        await ProjectDocumentUpdater.UpdateAsync(repository, projectRoot, latest =>
        {
            int index = latest.Segments.FindIndex(item => item.SegmentId == segmentId);
            if (index < 0) throw new InvalidOperationException("The segment was removed while processing; audio remains on disk.");
            SegmentDocument current = latest.Segments[index];
            int takeIndex = current.RenderHistory.FindIndex(item => item.RenderId == renderId);
            if (takeIndex < 0) throw new InvalidOperationException("The take was removed while processing.");
            RenderRecord currentTake = current.RenderHistory[takeIndex];
            updatedRender = currentTake with
            {
                ProcessedAudioRelativePath = outputRelativePath,
                SampleRate = probe.SampleRate, Channels = probe.Channels,
                Duration = probe.Duration, AudioSha256 = audioHash,
            };
            List<RenderRecord> history = [.. current.RenderHistory];
            history[takeIndex] = updatedRender;
            List<SegmentDocument> segments = [.. latest.Segments];
            segments[index] = current with { RenderHistory = history };
            return latest with { Segments = segments };
        }, cancellationToken).ConfigureAwait(false);
        return updatedRender;
    }

    private static Task<string> ReserveOutputPathAsync(
        string projectRoot,
        string segmentId,
        int takeNumber,
        CancellationToken cancellationToken)
    {
        string safeId = string.Concat(segmentId.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)).Trim('-', '.');
        string directoryRelative = VoiceBenchProjectLayout.ToManifestPath(
            Path.Combine(VoiceBenchProjectLayout.SegmentsDirectory, safeId));
        string directory = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, directoryRelative);
        Directory.CreateDirectory(directory);

        for (int version = 1; version <= 9999; version++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = $"{directoryRelative}/{safeId}--take-{takeNumber:0000}--processed-{version:000}.wav";
            string fullPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                return Task.FromResult(relativePath);
            }
        }

        throw new IOException($"No processed-audio version slot remains for segment '{segmentId}'.");
    }
}
