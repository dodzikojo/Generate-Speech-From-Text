using IssueLab.VoiceBench.Audio;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Audio;

public sealed class AudioGraphFactory(AudioAssetService assets)
{
    public async Task<AudioGraph> CreateAsync(string root, MixRenderPlan plan, bool monitor = false, Guid? stemTrackId = null, CancellationToken cancellationToken = default)
    {
        List<AudioGraphClip> clips = [];
        List<DiskPcmSource> sources = [];
        Dictionary<(string Path, string Hash), DiskPcmSource> preparedSources = [];
        try
        {
            foreach (var clip in plan.Clips)
            {
                var key = (clip.Asset.RelativePath, clip.Asset.Sha256);
                if (!preparedSources.TryGetValue(key, out var source))
                {
                    var prepared = await assets.PrepareAsync(root, clip.Asset, cancellationToken).ConfigureAwait(false);
                    source = new DiskPcmSource(prepared.PcmPath, prepared.OriginalChannels);
                    sources.Add(source); preparedSources.Add(key, source);
                }
                clips.Add(new()
                {
                    Source = source, ClipId = clip.ClipId, TrackId = clip.Track.TrackId, StartFrame = clip.StartFrame, SourceStartFrame = clip.SourceStartFrame,
                    DurationFrames = clip.DurationFrames, GainDb = clip.GainDb + clip.Track.GainDb,
                    Pan = Math.Clamp(clip.Pan + clip.Track.Pan, -1, 1),
                    FadeInFrames = TimelineTime.ToFrames(clip.FadeInSeconds), FadeOutFrames = TimelineTime.ToFrames(clip.FadeOutSeconds),
                    FadeInOffsetFrames = TimelineTime.ToFrames(clip.FadeInOffsetSeconds), FadeOutOffsetFrames = TimelineTime.ToFrames(clip.FadeOutOffsetSeconds),
                    Muted = clip.Muted || clip.Track.Muted,
                    SuppressOutput = stemTrackId.HasValue && clip.Track.TrackId != stemTrackId,
                    Solo = clip.Track.Solo, Loop = clip.Loop,
                    Narration = clip.Track.Kind == AudioTrackKind.Narration,
                    Duck = clip.Track.DuckUnderNarration && clip.Track.Kind != AudioTrackKind.Narration,
                    DuckDepthDb = clip.Track.DuckDepthDb,
                    DuckAttackFrames = TimelineTime.ToFrames(clip.Track.DuckAttackSeconds),
                    DuckReleaseFrames = TimelineTime.ToFrames(clip.Track.DuckReleaseSeconds),
                    Envelope = clip.Envelope.Select(p => new AudioEnvelopePoint(TimelineTime.ToFrames(p.OffsetSeconds), p.GainDb)).ToArray(),
                });
            }
            return new(clips, TimelineTime.ToFrames(plan.DurationSeconds), monitor);
        }
        catch { foreach (var source in sources) source.Dispose(); throw; }
    }
}
