using System.Text;
using System.Text.RegularExpressions;

namespace IssueLab.VoiceBench.Core;

public enum ProductionCueKind
{
    Visual,
    Action,
    Screen,
    Note,
    Custom,
}

public enum ScriptDocumentDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record ProductionCue(
    int Ordinal,
    ProductionCueKind Kind,
    string Type,
    string Content,
    int SourceLine,
    int SourceStart,
    int SourceLength,
    int PrecedingNarrationWords);

public sealed record ScriptDocumentDiagnostic(
    string Code,
    string Message,
    int SourceLine,
    ScriptDocumentDiagnosticSeverity Severity = ScriptDocumentDiagnosticSeverity.Error);

public sealed record ParsedScriptDocument(
    string AuthoringText,
    string NarrationText,
    IReadOnlyList<ProductionCue> Cues,
    IReadOnlyList<ScriptDocumentDiagnostic> Diagnostics,
    int NarrationWordCount,
    IReadOnlyList<NarrationWord> NarrationWords)
{
    public bool HasErrors => Diagnostics.Any(item => item.Severity == ScriptDocumentDiagnosticSeverity.Error);
}

public sealed partial class ProductionScriptParser
{
    public ParsedScriptDocument Parse(string authoringText)
    {
        ArgumentNullException.ThrowIfNull(authoringText);

        IReadOnlyList<SourceLine> lines = ReadLines(authoringText);
        List<NarrationSourceLine> narrationLines = [];
        List<ProductionCue> cues = [];
        List<ScriptDocumentDiagnostic> diagnostics = [];
        int narrationWords = 0;

        for (int index = 0; index < lines.Count; index++)
        {
            SourceLine sourceLine = lines[index];
            string line = sourceLine.Text;
            string trimmed = line.TrimStart();
            int lineNumber = index + 1;

            if (trimmed.StartsWith("\\@", StringComparison.Ordinal))
            {
                string narrationLine = line.Remove(line.Length - trimmed.Length, 1);
                narrationLines.Add(new NarrationSourceLine(
                    narrationLine,
                    sourceLine.Start,
                    line.Length - trimmed.Length));
                narrationWords += CountWords(narrationLine);
            }
            else if (TryParseCue(line, out string? type, out string? content))
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    diagnostics.Add(new ScriptDocumentDiagnostic(
                        "cue.content-empty",
                        $"Production cue '@{type}:' needs guidance text.",
                        lineNumber));
                }
                else
                {
                    cues.Add(new ProductionCue(
                        cues.Count + 1,
                        ResolveKind(type),
                        type,
                        content.Trim(),
                        lineNumber,
                        sourceLine.Start,
                        line.Length,
                        narrationWords));
                }
            }
            else if (LooksLikeInvalidCue(line, out string diagnosticCode, out string message))
            {
                diagnostics.Add(new ScriptDocumentDiagnostic(diagnosticCode, message, lineNumber));
            }
            else
            {
                narrationLines.Add(new NarrationSourceLine(line, sourceLine.Start, null));
                narrationWords += CountWords(line);
            }

        }

        (string narration, IReadOnlyList<NarrationWord> words) = NormalizeNarration(narrationLines);
        return new ParsedScriptDocument(
            authoringText,
            narration,
            cues,
            diagnostics,
            words.Count,
            words);
    }

    public static bool IsCueLine(string line) =>
        TryParseCue(line, out _, out _) || LooksLikeInvalidCue(line, out _, out _);

    public static int CountWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WordPattern().Matches(text).Count;
    }

    private static bool TryParseCue(string line, out string type, out string content)
    {
        Match match = CuePattern().Match(line);
        if (!match.Success)
        {
            type = string.Empty;
            content = string.Empty;
            return false;
        }

        type = match.Groups["type"].Value;
        content = match.Groups["content"].Value;
        return true;
    }

    private static bool LooksLikeInvalidCue(string line, out string code, out string message)
    {
        string trimmed = line.TrimStart();
        Match colon = CueLikePattern().Match(trimmed);
        if (colon.Success)
        {
            string type = colon.Groups["type"].Value;
            code = "cue.type-invalid";
            message = $"Production cue type '{type}' must use lowercase letters, numbers, and single hyphens.";
            return true;
        }

        Match missingColon = CueWithoutColonPattern().Match(trimmed);
        if (missingColon.Success)
        {
            string type = missingColon.Groups["type"].Value;
            if (!ValidTypePattern().IsMatch(type))
            {
                code = "cue.type-invalid";
                message = $"Production cue type '{type}' must use lowercase letters, numbers, and single hyphens.";
            }
            else
            {
                code = "cue.syntax-invalid";
                message = $"Production cue '@{type}' must be followed by a colon.";
            }
            return true;
        }

        code = string.Empty;
        message = string.Empty;
        return false;
    }

    private static ProductionCueKind ResolveKind(string type) => type switch
    {
        "visual" => ProductionCueKind.Visual,
        "action" => ProductionCueKind.Action,
        "screen" => ProductionCueKind.Screen,
        "note" => ProductionCueKind.Note,
        _ => ProductionCueKind.Custom,
    };

    private static (string Text, IReadOnlyList<NarrationWord> Words) NormalizeNarration(
        IEnumerable<NarrationSourceLine> lines)
    {
        StringBuilder normalized = new();
        List<NarrationWord> words = [];
        bool pendingBlank = false;
        bool hasLine = false;
        foreach (NarrationSourceLine sourceLine in lines)
        {
            string trimmedEnd = sourceLine.Text.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmedEnd))
            {
                if (hasLine) pendingBlank = true;
                continue;
            }

            if (hasLine)
            {
                normalized.Append(pendingBlank ? "\n\n" : "\n");
            }

            int narrationLineStart = normalized.Length;
            normalized.Append(trimmedEnd);
            foreach (Match match in WordPattern().Matches(trimmedEnd))
            {
                int authoringStart = sourceLine.SourceStart + match.Index;
                if (sourceLine.EscapeMarkerIndex is int markerIndex && match.Index >= markerIndex)
                {
                    authoringStart++;
                }

                words.Add(new NarrationWord(
                    words.Count,
                    match.Value,
                    WordTextNormalizer.Normalize(match.Value),
                    narrationLineStart + match.Index,
                    match.Length,
                    authoringStart,
                    match.Length));
            }

            hasLine = true;
            pendingBlank = false;
        }

        return (normalized.ToString(), words);
    }

    private static IReadOnlyList<SourceLine> ReadLines(string source)
    {
        List<SourceLine> lines = [];
        int start = 0;
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] is not ('\r' or '\n')) continue;
            lines.Add(new SourceLine(source[start..index], start));
            if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n') index++;
            start = index + 1;
        }

        lines.Add(new SourceLine(source[start..], start));
        return lines;
    }

    private sealed record SourceLine(string Text, int Start);

    private sealed record NarrationSourceLine(string Text, int SourceStart, int? EscapeMarkerIndex);

    [GeneratedRegex(@"^\s*@(?<type>[a-z][a-z0-9]*(?:-[a-z0-9]+)*):[ \t]*(?<content>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CuePattern();

    [GeneratedRegex(@"^@(?<type>[^\s:]+):", RegexOptions.CultureInvariant)]
    private static partial Regex CueLikePattern();

    [GeneratedRegex(@"^@(?<type>[^\s:]+)(?:\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CueWithoutColonPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidTypePattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:\p{N}+(?:[.,]\p{N}+)*|[\p{L}\p{N}_]+(?:[’'-][\p{L}\p{N}_]+)*)(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
