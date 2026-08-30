using System.Text;

namespace IssueLab.VoiceBench.Core;

public static partial class LexiconSubstitution
{
    public static PreparedSpeechText Prepare(
        ParsedScriptDocument parsed,
        IReadOnlyList<PronunciationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(entries);
        string source = parsed.NarrationText;
        int?[] sourceWordOrdinals = new int?[source.Length];
        foreach (NarrationWord word in parsed.NarrationWords)
        {
            for (int index = word.NarrationStart; index < word.NarrationStart + word.NarrationLength; index++)
            {
                sourceWordOrdinals[index] = word.Ordinal;
            }
        }

        var candidates = CreateCandidates(entries);
        StringBuilder output = new(source.Length);
        List<IReadOnlyList<int>> outputMappings = [];
        for (int offset = 0; offset < source.Length;)
        {
            var match = candidates.FirstOrDefault(candidate => Matches(source, offset, candidate.Entry));
            if (match.Entry is not null)
            {
                int[] ordinals = sourceWordOrdinals
                    .Skip(offset)
                    .Take(match.Entry.Written.Length)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .Distinct()
                    .ToArray();
                output.Append(match.Entry.Spoken);
                for (int index = 0; index < match.Entry.Spoken.Length; index++) outputMappings.Add(ordinals);
                offset += match.Entry.Written.Length;
            }
            else
            {
                output.Append(source[offset]);
                outputMappings.Add(sourceWordOrdinals[offset] is int ordinal ? [ordinal] : []);
                offset++;
            }
        }

        string text = output.ToString();
        List<SubmittedWordMapping> words = [];
        foreach (System.Text.RegularExpressions.Match word in SubmittedWordPattern().Matches(text))
        {
            int[] ordinals = outputMappings
                .Skip(word.Index)
                .Take(word.Length)
                .SelectMany(values => values)
                .Distinct()
                .Order()
                .ToArray();
            words.Add(new SubmittedWordMapping(
                words.Count,
                word.Value,
                WordTextNormalizer.Normalize(word.Value),
                word.Index,
                word.Length,
                ordinals));
        }

        return new PreparedSpeechText(text, words);
    }

    public static string Apply(string source, IReadOnlyList<PronunciationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);
        if (source.Length == 0 || entries.Count == 0)
        {
            return source;
        }

        var candidates = CreateCandidates(entries);
        var output = new StringBuilder(source.Length);

        for (var offset = 0; offset < source.Length;)
        {
            var match = candidates.FirstOrDefault(candidate => Matches(source, offset, candidate.Entry));
            if (match.Entry is not null)
            {
                output.Append(match.Entry.Spoken);
                offset += match.Entry.Written.Length;
            }
            else
            {
                output.Append(source[offset]);
                offset++;
            }
        }

        return output.ToString();
    }

    private static (PronunciationEntry Entry, int Index)[] CreateCandidates(
        IReadOnlyList<PronunciationEntry> entries) => entries
        .Select((entry, index) => (Entry: entry, Index: index))
        .Where(candidate => !string.IsNullOrEmpty(candidate.Entry.Written))
        .OrderByDescending(candidate => candidate.Entry.Written.Length)
        .ThenBy(candidate => candidate.Index)
        .ToArray();

    private static bool Matches(string source, int offset, PronunciationEntry entry)
    {
        if (offset + entry.Written.Length > source.Length)
        {
            return false;
        }

        var comparison = entry.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!source.AsSpan(offset, entry.Written.Length).Equals(entry.Written.AsSpan(), comparison))
        {
            return false;
        }

        if (!entry.WholeWord)
        {
            return true;
        }

        var startsWithWordCharacter = IsWordCharacter(entry.Written[0]);
        var endsWithWordCharacter = IsWordCharacter(entry.Written[^1]);
        var validStart = !startsWithWordCharacter || offset == 0 || !IsWordCharacter(source[offset - 1]);
        var end = offset + entry.Written.Length;
        var validEnd = !endsWithWordCharacter || end == source.Length || !IsWordCharacter(source[end]);
        return validStart && validEnd;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:\p{N}+(?:[.,]\p{N}+)*|[\p{L}\p{N}_]+(?:[’'-][\p{L}\p{N}_]+)*)(?![\p{L}\p{N}_])", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SubmittedWordPattern();
}
