using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

internal static class RecognizedWordTimingNormalizer
{
    private static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromMilliseconds(20);

    public static IReadOnlyList<RecognizedWordTiming> Normalize(
        IEnumerable<RecognizedWordTiming> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<RecognizedWordTiming> candidates = source
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Text)
                && item.Start >= TimeSpan.Zero
                && item.End >= item.Start)
            .ToList();
        List<RecognizedWordTiming> result = [];

        for (int index = 0; index < candidates.Count; index++)
        {
            RecognizedWordTiming candidate = candidates[index];
            if (result.Count > 0 && candidate.Start <= result[^1].Start)
            {
                // Equal or backwards starts cannot be disambiguated safely. Keep the
                // first provider word and let sequence alignment leave this one unmatched.
                continue;
            }

            if (result.Count > 0 && result[^1].End > candidate.Start)
            {
                RecognizedWordTiming previous = result[^1];
                result[^1] = previous with { End = candidate.Start };
            }

            TimeSpan end = candidate.End;
            if (end <= candidate.Start)
            {
                TimeSpan inferredEnd = candidate.Start + MinimumVisibleDuration;
                TimeSpan? nextStart = candidates
                    .Skip(index + 1)
                    .Select(item => (TimeSpan?)item.Start)
                    .FirstOrDefault(start => start > candidate.Start);
                end = nextStart is { } boundary && boundary < inferredEnd
                    ? boundary
                    : inferredEnd;
            }

            if (end > candidate.Start)
                result.Add(candidate with { End = end });
        }

        return result;
    }
}
