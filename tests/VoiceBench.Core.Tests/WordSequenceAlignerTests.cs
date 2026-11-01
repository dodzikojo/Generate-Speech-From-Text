using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class WordSequenceAlignerTests
{
    [Fact]
    public void Align_matches_normalized_words_and_leaves_omissions_as_partial_gaps()
    {
        SubmittedWordMapping[] submitted =
        [
            Word(0, "Hello", 0),
            Word(1, "brave", 1),
            Word(2, "world", 2),
        ];
        RecognizedWordTiming[] recognized =
        [
            Heard("hello,", 0, 0.4),
            Heard("world", 0.8, 1.2),
        ];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 3);

        Assert.Equal(2 / 3d, result.Coverage, 6);
        Assert.Equal([0, 2], result.Words.Select(word => word.NarrationWordOrdinal));
    }

    [Fact]
    public void Align_combines_one_to_many_pronunciation_tokens_into_one_canonical_interval()
    {
        SubmittedWordMapping[] submitted =
        [
            Word(0, "A", 0),
            Word(1, "E", 0),
            Word(2, "C", 0),
        ];
        RecognizedWordTiming[] recognized =
        [
            Heard("A", 0, 0.2),
            Heard("E", 0.2, 0.4),
            Heard("C", 0.4, 0.6),
        ];

        WordTiming word = Assert.Single(WordSequenceAligner.Align(submitted, recognized, 1).Words);

        Assert.Equal(0, word.NarrationWordOrdinal);
        Assert.Equal(TimeSpan.Zero, word.Start);
        Assert.Equal(TimeSpan.FromSeconds(0.6), word.End);
    }

    [Fact]
    public void Align_keeps_repeated_words_in_monotonic_order()
    {
        SubmittedWordMapping[] submitted = [Word(0, "go", 0), Word(1, "go", 1), Word(2, "now", 2)];
        RecognizedWordTiming[] recognized = [Heard("go", 0, 0.2), Heard("go", 0.2, 0.4), Heard("now", 0.4, 0.7)];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 3);

        Assert.Equal([0, 1, 2], result.Words.Select(word => word.NarrationWordOrdinal));
        Assert.Equal(1, result.Coverage);
    }

    [Fact]
    public void Align_omits_a_repeated_word_when_context_cannot_identify_its_occurrence()
    {
        SubmittedWordMapping[] submitted = [Word(0, "go", 0), Word(1, "later", 1), Word(2, "go", 2)];
        RecognizedWordTiming[] recognized = [Heard("go", 0, 0.2)];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 3);

        Assert.Empty(result.Words);
        Assert.Equal(0, result.Coverage);
    }

    [Fact]
    public void Align_uses_surrounding_context_to_resolve_a_repeated_word()
    {
        SubmittedWordMapping[] submitted = [Word(0, "go", 0), Word(1, "later", 1), Word(2, "go", 2)];
        RecognizedWordTiming[] recognized = [Heard("later", 0, 0.2), Heard("go", 0.2, 0.4)];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 3);

        Assert.Equal([1, 2], result.Words.Select(word => word.NarrationWordOrdinal));
    }

    [Fact]
    public void Align_uses_neighboring_anchors_to_resolve_an_equal_repeated_token_gap()
    {
        SubmittedWordMapping[] submitted =
        [
            Word(0, "act", 0),
            Word(1, "on", 1),
            Word(2, "the", 2),
            Word(3, "on", 3),
            Word(4, "results", 4),
        ];
        RecognizedWordTiming[] recognized =
        [
            Heard("act", 0, 0.2),
            Heard("on", 0.2, 0.35),
            Heard("the", 0.35, 0.5),
            Heard("on", 0.5, 0.65),
            Heard("results", 0.65, 0.9),
        ];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 5);

        Assert.Equal([0, 1, 2, 3, 4], result.Words.Select(word => word.NarrationWordOrdinal));
        Assert.Equal(1, result.Coverage);
    }

    [Fact]
    public void Align_coalesces_recognizer_word_pieces_for_one_submitted_token()
    {
        SubmittedWordMapping[] submitted =
        [
            Word(0, "important", 0),
            Word(1, "Nav-is-works", 1),
            Word(2, "Clash", 2),
        ];
        RecognizedWordTiming[] recognized =
        [
            Heard("important", 0, 0.4),
            Heard("Nav", 0.4, 0.55),
            Heard("is", 0.55, 0.7),
            Heard("works", 0.7, 0.9),
            Heard("Clash", 0.9, 1.2),
        ];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 3);

        Assert.Equal([0, 1, 2], result.Words.Select(word => word.NarrationWordOrdinal));
        Assert.Equal(TimeSpan.FromSeconds(0.4), result.Words[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(0.9), result.Words[1].End);
    }

    [Fact]
    public void Align_splits_a_many_to_one_pronunciation_interval_across_canonical_words()
    {
        SubmittedWordMapping[] submitted = [Word(0, "AEC", 0, 1)];
        RecognizedWordTiming[] recognized = [Heard("AEC", 0, 0.8)];

        WordSequenceAlignmentResult result = WordSequenceAligner.Align(submitted, recognized, 2);

        Assert.Collection(
            result.Words,
            first =>
            {
                Assert.Equal(0, first.NarrationWordOrdinal);
                Assert.Equal(TimeSpan.FromSeconds(0.4), first.End);
            },
            second =>
            {
                Assert.Equal(1, second.NarrationWordOrdinal);
                Assert.Equal(TimeSpan.FromSeconds(0.4), second.Start);
            });
    }

    [Theory]
    [InlineData("One", "1")]
    [InlineData("1,000", "1000")]
    [InlineData("3.5", "3.5")]
    [InlineData("team’s", "team's")]
    public void Normalization_handles_conservative_common_forms(string left, string right)
    {
        Assert.Equal(WordTextNormalizer.Normalize(left), WordTextNormalizer.Normalize(right));
    }

    private static SubmittedWordMapping Word(int ordinal, string text, params int[] narrationOrdinals) =>
        new(ordinal, text, WordTextNormalizer.Normalize(text), ordinal, text.Length, narrationOrdinals);

    private static RecognizedWordTiming Heard(string text, double start, double end) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
}
