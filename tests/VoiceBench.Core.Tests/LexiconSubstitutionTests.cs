using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class LexiconSubstitutionTests
{
    [Fact]
    public void Longest_match_wins_without_cascading_replacements()
    {
        PronunciationEntry[] lexicon =
        [
            new() { Written = "Clash", Spoken = "clash" },
            new() { Written = "ClashWise", Spoken = "Clash Wise" },
            new() { Written = "Wise", Spoken = "Why z" },
        ];

        var result = LexiconSubstitution.Apply("ClashWise keeps teams clashwise.", lexicon);

        Assert.Equal("Clash Wise keeps teams Clash Wise.", result);
    }

    [Fact]
    public void Whole_word_entries_do_not_replace_inside_another_word()
    {
        PronunciationEntry[] lexicon = [new() { Written = "BIM", Spoken = "B I M" }];

        var result = LexiconSubstitution.Apply("BIM and BIMobject", lexicon);

        Assert.Equal("B I M and BIMobject", result);
    }

    [Fact]
    public void Match_case_is_honoured()
    {
        PronunciationEntry[] lexicon =
        [
            new() { Written = "AEC", Spoken = "A E C", MatchCase = true },
        ];

        Assert.Equal("A E C aec", LexiconSubstitution.Apply("AEC aec", lexicon));
    }

    [Fact]
    public void Substitution_is_a_pure_operation_on_the_render_copy()
    {
        const string canonical = "Use CDE.";
        PronunciationEntry[] lexicon = [new() { Written = "CDE", Spoken = "C D E" }];

        var submitted = LexiconSubstitution.Apply(canonical, lexicon);

        Assert.Equal("Use CDE.", canonical);
        Assert.Equal("Use C D E.", submitted);
    }

    [Fact]
    public void Prepare_maps_one_canonical_word_to_each_spoken_substitution_word()
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse("Use AEC coordination.");
        PronunciationEntry[] lexicon = [new() { Written = "AEC", Spoken = "A E C", MatchCase = true }];

        PreparedSpeechText prepared = LexiconSubstitution.Prepare(parsed, lexicon);

        Assert.Equal("Use A E C coordination.", prepared.Text);
        int aecOrdinal = parsed.NarrationWords.Single(word => word.Text == "AEC").Ordinal;
        Assert.All(prepared.Words.Where(word => word.Text is "A" or "E" or "C"), word =>
            Assert.Equal([aecOrdinal], word.NarrationWordOrdinals));
    }

    [Fact]
    public void Prepare_preserves_identity_mappings_without_a_lexicon()
    {
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse("First and second.");

        PreparedSpeechText prepared = LexiconSubstitution.Prepare(parsed, []);

        Assert.Equal(parsed.NarrationText, prepared.Text);
        Assert.Equal([0, 1, 2], prepared.Words.SelectMany(word => word.NarrationWordOrdinals));
    }
}
