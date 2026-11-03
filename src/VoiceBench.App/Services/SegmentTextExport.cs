using System.Text;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

public enum SegmentTextKind
{
    /// <summary>The canonical script exactly as written, including production cues.</summary>
    Script,

    /// <summary>Only the words that are spoken: production cues removed.</summary>
    Narration,
}

/// <summary>Text the operator copies or saves from a segment.</summary>
public static class SegmentTextExport
{
    public static string Text(string script, SegmentTextKind kind)
    {
        ArgumentNullException.ThrowIfNull(script);
        return kind == SegmentTextKind.Script ? script : new ProductionScriptParser().Parse(script).NarrationText;
    }

    /// <summary>Notepad and most Windows editors expect CRLF; the editor works in LF.</summary>
    public static string WithWindowsLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <param name="scriptExtension">Extension of the canonical script file, used for full-script exports.</param>
    public static string SuggestedFileName(string segmentTitle, SegmentTextKind kind, string scriptExtension)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        StringBuilder name = new();
        foreach (char character in (segmentTitle ?? string.Empty).Trim())
            name.Append(invalid.Contains(character) ? '-' : character);
        string stem = name.ToString().Trim().TrimEnd('.');
        if (stem.Length == 0) stem = "segment";
        if (stem.Length > 120) stem = stem[..120].TrimEnd();
        return kind == SegmentTextKind.Narration
            ? $"{stem} - narration.txt"
            : stem + (string.IsNullOrWhiteSpace(scriptExtension) ? ".txt" : scriptExtension);
    }

    public static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
