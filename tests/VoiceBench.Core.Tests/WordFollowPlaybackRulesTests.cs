using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class WordFollowPlaybackRulesTests
{
    private static readonly IReadOnlyList<WordTiming> Words =
    [
        new("First", TimeSpan.Zero, TimeSpan.FromMilliseconds(300), 0, 0),
        new("third", TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(900), 2, 2),
    ];

    [Fact]
    public void FindActiveWord_selects_a_word_and_leaves_partial_alignment_gaps_empty()
    {
        Assert.Equal(0, WordFollowPlaybackRules.FindActiveWord(Words, TimeSpan.FromMilliseconds(150))?.NarrationWordOrdinal);
        Assert.Null(WordFollowPlaybackRules.FindActiveWord(Words, TimeSpan.FromMilliseconds(450)));
        Assert.Equal(2, WordFollowPlaybackRules.FindActiveWord(Words, TimeSpan.FromMilliseconds(600))?.NarrationWordOrdinal);
        Assert.Null(WordFollowPlaybackRules.FindActiveWord(Words, TimeSpan.FromMilliseconds(900)));
    }

    [Fact]
    public void IsNarrationCurrent_ignores_authoring_changes_outside_clean_narration()
    {
        const string narration = "Follow these exact words.";
        string sourceHash = SourceTextHasher.Compute(narration);

        Assert.True(WordFollowPlaybackRules.IsNarrationCurrent(sourceHash, narration));
        Assert.False(WordFollowPlaybackRules.IsNarrationCurrent(sourceHash, narration + " Changed."));
    }
}
