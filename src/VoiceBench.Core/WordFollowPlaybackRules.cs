namespace IssueLab.VoiceBench.Core;

public static class WordFollowPlaybackRules
{
    public static bool IsNarrationCurrent(string renderSourceSha256, string currentNarrationText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderSourceSha256);
        ArgumentNullException.ThrowIfNull(currentNarrationText);
        return string.Equals(
            renderSourceSha256,
            SourceTextHasher.Compute(currentNarrationText),
            StringComparison.OrdinalIgnoreCase);
    }

    public static WordTiming? FindActiveWord(
        IReadOnlyList<WordTiming> words,
        TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (position < TimeSpan.Zero) return null;

        // Alignment words are persisted in chronological order. Binary search keeps the
        // 100 ms playback callback independent of script or take length.
        int low = 0;
        int high = words.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            WordTiming word = words[middle];
            if (position < word.Start)
            {
                high = middle - 1;
            }
            else if (position >= word.End)
            {
                low = middle + 1;
            }
            else
            {
                return word;
            }
        }

        return null;
    }
}
