using System.Text;

namespace IssueLab.VoiceBench.Core;

public static class LexiconSubstitution
{
    public static string Apply(string source, IReadOnlyList<PronunciationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);
        if (source.Length == 0 || entries.Count == 0)
        {
            return source;
        }

        var candidates = entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(candidate => !string.IsNullOrEmpty(candidate.Entry.Written))
            .OrderByDescending(candidate => candidate.Entry.Written.Length)
            .ThenBy(candidate => candidate.Index)
            .ToArray();
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
}
