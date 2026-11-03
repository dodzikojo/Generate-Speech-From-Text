namespace IssueLab.VoiceBench.App.Services;

public sealed record SelectedTakePlaybackState(bool HasAudio,bool IsActive,bool IsPlaying,string Label,TimeSpan Position,TimeSpan Duration);
public enum TimelinePlaybackAction { Unavailable,Pause,PlaySelectedTake,ResumeMix,LoadMix }

public static class TimelinePlaybackRouting
{
    public static TimelinePlaybackAction Resolve(bool selectedTake,string activeTarget,bool isPlaying,SelectedTakePlaybackState? take)
    {
        if (selectedTake)
        {
            if (take?.HasAudio != true) return TimelinePlaybackAction.Unavailable;
            return take.IsActive && take.IsPlaying ? TimelinePlaybackAction.Pause : TimelinePlaybackAction.PlaySelectedTake;
        }
        if (activeTarget == "mix" && isPlaying) return TimelinePlaybackAction.Pause;
        return activeTarget == "mix" ? TimelinePlaybackAction.ResumeMix : TimelinePlaybackAction.LoadMix;
    }
}
