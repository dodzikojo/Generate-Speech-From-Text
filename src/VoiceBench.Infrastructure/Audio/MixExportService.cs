using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Audio;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Audio;

public sealed record MixExportResult(string MasterRelativePath, string CompletionRelativePath, string ReportRelativePath,
    string TranscriptRelativePath, string ClipSheetRelativePath, string CueSheetRelativePath, string? Mp3RelativePath,
    IReadOnlyList<string> StemRelativePaths, double DurationSeconds, double? IntegratedLufs, double? TruePeakDbtp);

/// <summary>Renders the frozen graph first, normalizes its flattened result, and publishes completion last.</summary>
public sealed class MixExportService(AudioAssetService assets, IProcessRunner runner, ToolchainPaths tools)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MixExportResult> ExportAsync(string root, MixRenderPlan plan, string name, bool includeMp3 = false,
        bool includeStems = false, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        root = Path.GetFullPath(root);
        string safe = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Give this mix export a name.", nameof(name));
        if (plan.DurationSeconds <= 0 || plan.Clips.Count == 0) throw new InvalidOperationException("An empty timeline cannot be exported.");
        if (plan.OutputStandard.SampleRate is < 8000 or > 192000 || plan.OutputStandard.Channels is < 1 or > 2
            || plan.OutputStandard.PcmBitsPerSample is not (16 or 24)
            || !double.IsFinite(plan.OutputStandard.IntegratedLufs) || plan.OutputStandard.IntegratedLufs is < -36 or > -9
            || !double.IsFinite(plan.OutputStandard.TruePeakDbtp) || plan.OutputStandard.TruePeakDbtp is < -9 or > 0)
            throw new InvalidOperationException("Use 8–192 kHz, mono/stereo, 16/24-bit PCM, -36 to -9 LUFS and -9 to 0 dBTP.");
        string outputRoot = Path.Combine(root, "05-masters");
        Directory.CreateDirectory(outputRoot);
        var gate = Gates.GetOrAdd(Path.Combine(outputRoot, safe), _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int version = 1;
            while (Directory.Exists(Path.Combine(outputRoot, $"{safe}-v{version:000}"))) version++;
            string stem = $"{safe}-v{version:000}", final = Path.Combine(outputRoot, stem);
            string work = Path.Combine(outputRoot, $".{stem}-work-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            progress?.Report(0);
            try
            {
                string raw = Path.Combine(work, "premaster.f32"), master = Path.Combine(work, $"{stem}.master.wav");
                using (var graph = await new AudioGraphFactory(assets).CreateAsync(root, plan, cancellationToken: cancellationToken).ConfigureAwait(false))
                    await RenderAsync(graph, raw, cancellationToken, progress).ConfigureAwait(false);
                var standard = plan.OutputStandard;
                string sampleRate = standard.SampleRate.ToString(CultureInfo.InvariantCulture);
                string channels = standard.Channels.ToString(CultureInfo.InvariantCulture);
                // Analyze and normalize the requested channel layout so a final stereo-to-mono
                // downmix cannot change loudness after the measured normalization pass.
                string conversion = $"aresample={sampleRate},aformat=channel_layouts={(standard.Channels == 1 ? "mono" : "stereo")}";
                var measurement = LoudnessMeasurement.Parse((await RunAsync([
                    "-hide_banner", "-nostdin", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", raw,
                    "-af", conversion + "," + FormattableString.Invariant($"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11:print_format=json"), "-f", "null", "NUL"], cancellationToken).ConfigureAwait(false)).StandardError);
                bool silent = double.IsNegativeInfinity(measurement.IntegratedLufs) && double.IsNegativeInfinity(measurement.TruePeakDbtp);
                string filter = silent ? "anull" : measurement.IsMeasurable
                    ? FormattableString.Invariant($"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11:measured_I={measurement.IntegratedLufs}:measured_TP={measurement.TruePeakDbtp}:measured_LRA={measurement.LoudnessRange}:measured_thresh={measurement.Threshold}:offset={measurement.TargetOffset}:linear=true:print_format=summary")
                    : FormattableString.Invariant($"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11");
                await RunAsync(["-hide_banner", "-nostdin", "-y", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", raw,
                    "-af", conversion + "," + filter, "-ar", sampleRate, "-ac", channels, "-c:a", standard.PcmBitsPerSample == 16 ? "pcm_s16le" : "pcm_s24le", "-rf64", "auto", master], cancellationToken).ConfigureAwait(false);
                var finalLoudness = LoudnessMeasurement.Parse((await RunAsync(["-hide_banner", "-nostdin", "-i", master, "-af",
                    FormattableString.Invariant($"loudnorm=I={standard.IntegratedLufs}:TP={standard.TruePeakDbtp}:LRA=11:print_format=json"), "-f", "null", "NUL"], cancellationToken).ConfigureAwait(false)).StandardError);
                var probeResult = await runner.RunAsync(new(tools.FfprobePath,
                    ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels,bits_per_sample:format=duration", "-of", "json", master]), cancellationToken).ConfigureAwait(false);
                if (!probeResult.Succeeded) throw new InvalidDataException("The completed mix could not be probed.");
                var probe = AudioProbe.Parse(probeResult.StandardOutput);
                using var probeJson = JsonDocument.Parse(probeResult.StandardOutput);
                int bits = probeJson.RootElement.GetProperty("streams")[0].GetProperty("bits_per_sample").GetInt32();
                if (!finalLoudness.IsMeasurable && !(silent && double.IsNegativeInfinity(finalLoudness.IntegratedLufs) && double.IsNegativeInfinity(finalLoudness.TruePeakDbtp)))
                    throw new InvalidDataException("The completed mix did not return valid loudness measurements.");
                if (probe.Channels != standard.Channels || probe.SampleRate != standard.SampleRate || bits != standard.PcmBitsPerSample
                    || Math.Abs(probe.Duration.TotalSeconds - plan.DurationSeconds) > .1
                    || (finalLoudness.IsMeasurable && (Math.Abs(finalLoudness.IntegratedLufs - standard.IntegratedLufs) > 1 || finalLoudness.TruePeakDbtp > standard.TruePeakDbtp + .1)))
                    throw new InvalidDataException("The completed mix failed its format, duration, or loudness checks.");
                progress?.Report(.8);
                string? mp3 = null;
                if (includeMp3)
                {
                    mp3 = $"{stem}.master.mp3";
                    await RunAsync(["-hide_banner", "-nostdin", "-y", "-i", master, "-ar", "48000", "-ac", channels, "-c:a", "libmp3lame", "-b:a", "320k", Path.Combine(work, mp3)], cancellationToken).ConfigureAwait(false);
                }
                List<string> stems = [];
                if (includeStems)
                {
                    foreach (var track in plan.Clips.Select(c => c.Track).DistinctBy(t => t.TrackId).OrderBy(t => t.Order))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string trackRaw = Path.Combine(work, $"{track.TrackId:N}.f32"), file = $"{stem}.stem-{track.TrackId:N}.wav";
                        using (var graph = await new AudioGraphFactory(assets).CreateAsync(root, plan, stemTrackId: track.TrackId, cancellationToken: cancellationToken).ConfigureAwait(false))
                            await RenderAsync(graph, trackRaw, cancellationToken).ConfigureAwait(false);
                        // Float stems retain pre-master headroom and exactly the same duck/automation processing.
                        await RunAsync(["-hide_banner", "-nostdin", "-y", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", trackRaw, "-c:a", "pcm_f32le", "-rf64", "auto", Path.Combine(work, file)], cancellationToken).ConfigureAwait(false);
                        stems.Add(file); File.Delete(trackRaw);
                    }
                }
                string transcript = $"{stem}.transcript.txt", clips = $"{stem}.clips.csv", cues = $"{stem}.cues.csv", report = $"{stem}.report.json";
                var ordered = plan.Transcript.OrderBy(t => t.StartSeconds).ToArray();
                await File.WriteAllTextAsync(Path.Combine(work, transcript), string.Join(Environment.NewLine + Environment.NewLine, ordered.Select(t => t.Text)), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(work, $"{stem}.transcript.md"), "# Mix narration\n\n" + string.Join("\n\n", ordered.Select(t => t.Text)), cancellationToken).ConfigureAwait(false);
                var clipLines = new List<string> { "clip_id,track_id,asset_id,source_sha256,start_frame,source_start_frame,duration_frames,loop,muted,retained_word_ranges" };
                clipLines.AddRange(plan.Clips.OrderBy(c => c.StartFrame).Select(c => string.Join(",", c.ClipId, c.Track.TrackId, c.Asset.AssetId, c.Asset.Sha256,
                    c.StartFrame.ToString(CultureInfo.InvariantCulture), c.SourceStartFrame.ToString(CultureInfo.InvariantCulture), c.DurationFrames.ToString(CultureInfo.InvariantCulture), c.Loop, c.Muted || c.Track.Muted,
                    Quote(string.Join(";", c.RetainedWordRanges.Select(r => $"{r.FirstWordOrdinal}-{r.LastWordOrdinal}"))))));
                await File.WriteAllLinesAsync(Path.Combine(work, clips), clipLines, cancellationToken).ConfigureAwait(false);
                await WriteCuesAsync(work, stem, cues, plan, cancellationToken).ConfigureAwait(false);
                var reportDocument = new { schemaVersion = 1, plan, createdAt = DateTimeOffset.UtcNow, probe, silent,
                    outputFormat = new { probe.SampleRate, probe.Channels, pcmBitsPerSample = bits },
                    mp3Format = includeMp3 ? new { sampleRate = 48000, standard.Channels, codec = "mp3", bitrateKbps = 320 } : null,
                    stemFormat = includeStems ? new { sampleRate = 48000, channels = 2, pcmBitsPerSample = 32, sampleFormat = "float", premaster = true } : null,
                    warning = silent ? "Silent audio has no LUFS measurement; normalization was skipped." : null,
                    loudness = new { integratedLufs = Finite(finalLoudness.IntegratedLufs), truePeakDbtp = Finite(finalLoudness.TruePeakDbtp),
                        loudnessRange = Finite(finalLoudness.LoudnessRange), threshold = Finite(finalLoudness.Threshold), targetOffset = Finite(finalLoudness.TargetOffset) },
                    premasterStems = stems, masterSha256 = await AudioAssetService.HashAsync(master, cancellationToken).ConfigureAwait(false) };
                await File.WriteAllTextAsync(Path.Combine(work, report), JsonSerializer.Serialize(reportDocument, ProjectJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
                File.Delete(raw);
                await Task.Run(() =>
                {
                    foreach (string artifact in Directory.EnumerateFiles(work))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var durable = new FileStream(artifact, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                        durable.Flush(true);
                    }
                }, cancellationToken).ConfigureAwait(false);
                // No previous export is overwritten. Folder rename publishes all artifacts together; completion is the final commit marker.
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(work, final);
                string completion = Path.Combine(final, "complete.json");
                string masterRelative = AudioAssetService.Relative(root, Path.Combine(final, Path.GetFileName(master)));
                string completionTemp = completion + $".{Guid.NewGuid():N}.tmp";
                await using (var stream = new FileStream(completionTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, new { plan.PlanId, plan.Revision, masterRelativePath = masterRelative, completedAt = DateTimeOffset.UtcNow }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false); stream.Flush(true);
                }
                File.Move(completionTemp, completion, false);
                progress?.Report(1);
                return new(masterRelative, AudioAssetService.Relative(root, completion), AudioAssetService.Relative(root, Path.Combine(final, report)),
                    AudioAssetService.Relative(root, Path.Combine(final, transcript)), AudioAssetService.Relative(root, Path.Combine(final, clips)), AudioAssetService.Relative(root, Path.Combine(final, cues)),
                    mp3 is null ? null : AudioAssetService.Relative(root, Path.Combine(final, mp3)), stems.Select(s => AudioAssetService.Relative(root, Path.Combine(final, s))).ToArray(),
                    probe.Duration.TotalSeconds, Finite(finalLoudness.IntegratedLufs), Finite(finalLoudness.TruePeakDbtp));
            }
            catch
            {
                // Work files are retained for diagnosis; their absence of complete.json prevents discovery as an export.
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private static async Task RenderAsync(AudioGraph graph, string path, CancellationToken token, IProgress<double>? progress = null)
    {
        await Task.Run(() =>
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            float[] buffer = new float[AudioGraph.BlockFrames * 2];
            long nextReport = 0;
            for (long frame = 0; frame < graph.DurationFrames; frame += AudioGraph.BlockFrames)
            {
                token.ThrowIfCancellationRequested();
                int frames = graph.Render(frame, buffer);
                output.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, frames * 2)));
                if (frame >= nextReport) { progress?.Report(frame / (double)graph.DurationFrames * .6); nextReport = frame + 48_000; }
            }
            output.Flush(true);
        }, token).ConfigureAwait(false);
    }
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var result = await runner.RunAsync(new(tools.FfmpegPath, arguments), token).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidOperationException($"FFmpeg mix export failed with exit code {result.ExitCode}.");
        return result;
    }
    private static async Task WriteCuesAsync(string work, string stem, string csv, MixRenderPlan plan, CancellationToken token)
    {
        var rows = new List<string> { "clip_id,source_word,placed,start_seconds,type,guidance" };
        var guidance = new StringBuilder("# Mix production guidance\n\n");
        foreach (var clip in plan.Clips.Where(c => c.Track.Kind == AudioTrackKind.Narration && !c.Muted && !c.Track.Muted).OrderBy(c => c.StartFrame))
        {
            foreach (var cue in clip.ProductionCues)
            {
                int word = cue.PrecedingNarrationWords;
                bool retained = clip.RetainedWordRanges.Any(r => word >= r.FirstWordOrdinal && word <= r.LastWordOrdinal);
                var timing = clip.AlignedWords.FirstOrDefault(w => w.NarrationWordOrdinal == word);
                double? placed = retained && timing is not null && timing.Start.TotalSeconds >= clip.SourceStartSeconds
                    && timing.Start.TotalSeconds < clip.SourceStartSeconds + clip.DurationSeconds
                    ? clip.StartSeconds + timing.Start.TotalSeconds - clip.SourceStartSeconds : null;
                rows.Add($"{clip.ClipId},{word},{placed.HasValue.ToString().ToLowerInvariant()},{placed?.ToString("0.######", CultureInfo.InvariantCulture)},{Quote(cue.Type)},{Quote(cue.Content)}");
                guidance.AppendLine($"- {(placed.HasValue ? placed.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s" : retained ? "Unplaced" : "Removed anchor")}: @{cue.Type}: {cue.Content}");
            }
        }
        foreach (var marker in plan.Markers)
            rows.Add($",,{marker.Anchor.Placed.ToString().ToLowerInvariant()},{(marker.Anchor.Placed ? marker.PositionSeconds.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty)},marker,{Quote(marker.Name)}");
        await File.WriteAllLinesAsync(Path.Combine(work, csv), rows, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(work, $"{stem}.production.md"), guidance.ToString(), token).ConfigureAwait(false);
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
