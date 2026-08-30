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
    int NarrationWordCount)
{
    public bool HasErrors => Diagnostics.Any(item => item.Severity == ScriptDocumentDiagnosticSeverity.Error);
}

public sealed partial class ProductionScriptParser
{
    public ParsedScriptDocument Parse(string authoringText)
    {
        ArgumentNullException.ThrowIfNull(authoringText);

        string normalized = authoringText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        List<string> narrationLines = [];
        List<ProductionCue> cues = [];
        List<ScriptDocumentDiagnostic> diagnostics = [];
        int sourceStart = 0;
        int narrationWords = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.TrimStart();
            int lineNumber = index + 1;

            if (trimmed.StartsWith("\\@", StringComparison.Ordinal))
            {
                string narrationLine = line.Remove(line.Length - trimmed.Length, 1);
                narrationLines.Add(narrationLine);
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
                        sourceStart,
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
                narrationLines.Add(line);
                narrationWords += CountWords(line);
            }

            sourceStart += line.Length + 1;
        }

        string narration = NormalizeNarration(narrationLines);
        return new ParsedScriptDocument(
            authoringText,
            narration,
            cues,
            diagnostics,
            CountWords(narration));
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

    private static string NormalizeNarration(IEnumerable<string> lines)
    {
        List<string> normalized = [];
        bool pendingBlank = false;
        foreach (string line in lines)
        {
            string trimmedEnd = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmedEnd))
            {
                if (normalized.Count > 0) pendingBlank = true;
                continue;
            }

            if (pendingBlank && normalized.Count > 0) normalized.Add(string.Empty);
            normalized.Add(trimmedEnd);
            pendingBlank = false;
        }

        return string.Join('\n', normalized);
    }

    [GeneratedRegex(@"^\s*@(?<type>[a-z][a-z0-9]*(?:-[a-z0-9]+)*):[ \t]*(?<content>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CuePattern();

    [GeneratedRegex(@"^@(?<type>[^\s:]+):", RegexOptions.CultureInvariant)]
    private static partial Regex CueLikePattern();

    [GeneratedRegex(@"^@(?<type>[^\s:]+)(?:\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CueWithoutColonPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidTypePattern();

    [GeneratedRegex(@"\b[\p{L}\p{N}_]+(?:[’'-][\p{L}\p{N}_]+)*\b", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
