namespace IssueLab.VoiceBench.Core;

public static class WordSequenceAligner
{
    public static WordSequenceAlignmentResult Align(
        IReadOnlyList<SubmittedWordMapping> submitted,
        IReadOnlyList<RecognizedWordTiming> recognized,
        int totalNarrationWordCount)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(recognized);
        if (totalNarrationWordCount < 0) throw new ArgumentOutOfRangeException(nameof(totalNarrationWordCount));

        IReadOnlyList<RecognizedWordTiming> recognition = CoalesceRecognizedPieces(submitted, recognized);

        int[,] lengths = new int[submitted.Count + 1, recognition.Count + 1];
        for (int left = submitted.Count - 1; left >= 0; left--)
        {
            for (int right = recognition.Count - 1; right >= 0; right--)
            {
                lengths[left, right] = TokensEqual(submitted[left].Normalized, recognition[right].Text)
                    ? lengths[left + 1, right + 1] + 1
                    : Math.Max(lengths[left + 1, right], lengths[left, right + 1]);
            }
        }

        int[,] prefixes = new int[submitted.Count + 1, recognition.Count + 1];
        for (int left = 1; left <= submitted.Count; left++)
        {
            for (int right = 1; right <= recognition.Count; right++)
            {
                prefixes[left, right] = TokensEqual(submitted[left - 1].Normalized, recognition[right - 1].Text)
                    ? prefixes[left - 1, right - 1] + 1
                    : Math.Max(prefixes[left - 1, right], prefixes[left, right - 1]);
            }
        }

        int optimalLength = lengths[0, 0];
        int[] submittedCandidateCounts = new int[submitted.Count];
        int[] recognizedCandidateCounts = new int[recognition.Count];
        for (int left = 0; left < submitted.Count; left++)
        {
            for (int right = 0; right < recognition.Count; right++)
            {
                if (!TokensEqual(submitted[left].Normalized, recognition[right].Text)) continue;
                if (prefixes[left, right] + 1 + lengths[left + 1, right + 1] != optimalLength) continue;
                submittedCandidateCounts[left]++;
                recognizedCandidateCounts[right]++;
            }
        }

        List<(SubmittedWordMapping Submitted, RecognizedWordTiming Recognized)> matches = [];
        int submittedIndex = 0;
        int recognizedIndex = 0;
        while (submittedIndex < submitted.Count && recognizedIndex < recognition.Count)
        {
            if (TokensEqual(submitted[submittedIndex].Normalized, recognition[recognizedIndex].Text))
            {
                if (submittedCandidateCounts[submittedIndex] == 1
                    && recognizedCandidateCounts[recognizedIndex] == 1)
                {
                    matches.Add((submitted[submittedIndex], recognition[recognizedIndex]));
                }
                submittedIndex++;
                recognizedIndex++;
            }
            else if (lengths[submittedIndex + 1, recognizedIndex] >= lengths[submittedIndex, recognizedIndex + 1])
            {
                submittedIndex++;
            }
            else
            {
                recognizedIndex++;
            }
        }

        List<(int NarrationOrdinal, int SubmittedOrdinal, string Text, TimeSpan Start, TimeSpan End)> contributions = [];
        foreach ((SubmittedWordMapping mapped, RecognizedWordTiming timing) in matches)
        {
            int[] ordinals = mapped.NarrationWordOrdinals.Distinct().Order().ToArray();
            if (ordinals.Length == 0) continue;
            TimeSpan duration = timing.End > timing.Start ? timing.End - timing.Start : TimeSpan.Zero;
            for (int index = 0; index < ordinals.Length; index++)
            {
                TimeSpan start = timing.Start + TimeSpan.FromTicks(duration.Ticks * index / ordinals.Length);
                TimeSpan end = index == ordinals.Length - 1
                    ? timing.End
                    : timing.Start + TimeSpan.FromTicks(duration.Ticks * (index + 1) / ordinals.Length);
                contributions.Add((ordinals[index], mapped.SubmittedOrdinal, timing.Text, start, end));
            }
        }

        List<WordTiming> words = contributions
            .GroupBy(item => item.NarrationOrdinal)
            .OrderBy(group => group.Key)
            .Select(group => new WordTiming(
                string.Join(' ', group.OrderBy(item => item.Start).Select(item => item.Text)),
                group.Min(item => item.Start),
                group.Max(item => item.End),
                group.Min(item => item.SubmittedOrdinal),
                group.Key))
            .ToList();
        int matched = words.Count;
        double coverage = totalNarrationWordCount == 0
            ? 0
            : Math.Min(1, matched / (double)totalNarrationWordCount);
        return new WordSequenceAlignmentResult(words, coverage, matched, totalNarrationWordCount);
    }

    private static IReadOnlyList<RecognizedWordTiming> CoalesceRecognizedPieces(
        IReadOnlyList<SubmittedWordMapping> submitted,
        IReadOnlyList<RecognizedWordTiming> recognized)
    {
        HashSet<string> submittedTokens = submitted
            .Select(item => CompoundKey(item.Normalized))
            .Where(item => !string.IsNullOrEmpty(item))
            .ToHashSet(StringComparer.Ordinal);
        List<RecognizedWordTiming> result = [];

        for (int index = 0; index < recognized.Count;)
        {
            int selectedCount = 1;
            string combined = WordTextNormalizer.Normalize(recognized[index].Text);
            for (int count = 2; count <= 3 && index + count <= recognized.Count; count++)
            {
                combined += CompoundKey(WordTextNormalizer.Normalize(recognized[index + count - 1].Text));
                if (submittedTokens.Contains(CompoundKey(combined))) selectedCount = count;
            }

            if (selectedCount == 1)
            {
                result.Add(recognized[index]);
            }
            else
            {
                IReadOnlyList<RecognizedWordTiming> pieces = recognized
                    .Skip(index)
                    .Take(selectedCount)
                    .ToArray();
                result.Add(new RecognizedWordTiming(
                    string.Concat(pieces.Select(item => item.Text)),
                    pieces[0].Start,
                    pieces[^1].End));
            }

            index += selectedCount;
        }

        return result;
    }

    private static bool TokensEqual(string submittedNormalized, string recognized)
    {
        if (string.IsNullOrEmpty(submittedNormalized)) return false;
        string recognizedNormalized = WordTextNormalizer.Normalize(recognized);
        return string.Equals(submittedNormalized, recognizedNormalized, StringComparison.Ordinal)
            || string.Equals(
                CompoundKey(submittedNormalized),
                CompoundKey(recognizedNormalized),
                StringComparison.Ordinal);
    }

    private static string CompoundKey(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
}
