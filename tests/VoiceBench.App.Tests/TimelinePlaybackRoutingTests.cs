using IssueLab.VoiceBench.App.Services;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class TimelinePlaybackRoutingTests
{
    private static SelectedTakePlaybackState Take(bool active=false,bool playing=false) => new(true,active,playing,"Take 22 · Achernar",TimeSpan.Zero,TimeSpan.FromSeconds(16.52));

    [Fact]
    public void Listening_to_a_take_switches_from_a_playing_mix_in_one_click() =>
        Assert.Equal(TimelinePlaybackAction.PlaySelectedTake,TimelinePlaybackRouting.Resolve(true,"mix",true,Take()));

    [Fact]
    public void Playing_a_mix_switches_from_a_playing_take_in_one_click() =>
        Assert.Equal(TimelinePlaybackAction.LoadMix,TimelinePlaybackRouting.Resolve(false,"take",true,Take(true,true)));

    [Fact]
    public void Changing_selected_take_plays_the_new_file_instead_of_pausing_the_old_one() =>
        Assert.Equal(TimelinePlaybackAction.PlaySelectedTake,TimelinePlaybackRouting.Resolve(true,"take",true,Take()));

    [Fact]
    public void Only_the_requested_active_audio_is_paused()
    {
        Assert.Equal(TimelinePlaybackAction.Pause,TimelinePlaybackRouting.Resolve(true,"take",true,Take(true,true)));
        Assert.Equal(TimelinePlaybackAction.Pause,TimelinePlaybackRouting.Resolve(false,"mix",true,Take()));
    }

    [Fact]
    public void An_unavailable_take_does_not_fall_back_to_the_mix() =>
        Assert.Equal(TimelinePlaybackAction.Unavailable,TimelinePlaybackRouting.Resolve(true,"mix",true,null));

    [Fact]
    public void A_paused_take_remains_listenable_before_it_is_placed_or_approved()
    {
        Assert.Equal(TimelinePlaybackAction.PlaySelectedTake,TimelinePlaybackRouting.Resolve(true,"none",false,Take()));
        Assert.Equal(TimelinePlaybackAction.ResumeMix,TimelinePlaybackRouting.Resolve(false,"mix",false,Take()));
    }
}
