using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IssueLab.VoiceBench.Core;

public sealed partial class ScriptImportService
{
    public IReadOnlyList<ImportedScriptSegment> Split(string sourceName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(content);

        var fallbackTitle = Path.GetFileNameWithoutExtension(sourceName).Trim();
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var hasBoundary = lines.Any(IsBoundary);
        if (!hasBoundary)
        {
            return [new ImportedScriptSegment(1, fallbackTitle, normalized)];
        }

        var segments = new List<ImportedScriptSegment>();
        var buffer = new StringBuilder();
        string? currentTitle = null;

        void Flush()
        {
            var body = buffer.ToString().Trim('\n');
            buffer.Clear();
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            var ordinal = segments.Count + 1;
            var title = string.IsNullOrWhiteSpace(currentTitle)
                ? ordinal == 1 ? fallbackTitle : $"{fallbackTitle} {ordinal.ToString(CultureInfo.InvariantCulture)}"
                : currentTitle;
            segments.Add(new ImportedScriptSegment(ordinal, title, body));
        }

        foreach (var line in lines)
        {
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                Flush();
                currentTitle = heading.Groups[1].Value.Trim().TrimEnd('#').TrimEnd();
                continue;
            }

            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                Flush();
                currentTitle = null;
                continue;
            }

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }

            buffer.Append(line);
        }

        Flush();
        return segments;
    }

    private static bool IsBoundary(string line) =>
        string.Equals(line.Trim(), "---", StringComparison.Ordinal) || HeadingPattern().IsMatch(line);

    [GeneratedRegex(@"^\s*##(?!#)\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();
}

public sealed record ImportedScriptSegment(int Ordinal, string Title, string Content);

public static class ScriptSegmentNaming
{
    public static string CreateFileName(int ordinal, string title, string stableId)
    {
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);

        var slug = Slugify(title);
        var suffix = new string(stableId.Where(char.IsLetterOrDigit).Take(6).ToArray()).ToLowerInvariant();
        if (suffix.Length < 6)
        {
            throw new ArgumentException("A stable ID must contain at least six letters or digits.", nameof(stableId));
        }

        return $"{ordinal:000}-{slug}--{suffix}.md";
    }

    public static string CreateDirectoryName(int ordinal, string title, string stableId)
    {
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        string suffix = new(stableId.Where(char.IsLetterOrDigit).Take(6).ToArray());
        if (suffix.Length < 6)
        {
            throw new ArgumentException("A stable ID must contain at least six letters or digits.", nameof(stableId));
        }

        return $"{ordinal:000}-{Slugify(title)}--{suffix.ToLowerInvariant()}";
    }

    private static string Slugify(string value)
    {
        var result = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return result.Length == 0 ? "segment" : result.ToString();
    }
}
