using System.Text.RegularExpressions;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Packaging;

/// <summary>Finds every audio file on disk that belongs to one take.</summary>
public static partial class TakeAudioFiles
{
    /// <summary>
    /// The take's raw and processed files, every earlier processed version of it (re-processing
    /// writes <c>--processed-002</c> and so on), and any audio its word alignments were made from.
    /// Paths are project-relative with forward slashes; only files that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> List(string projectRoot, RenderRecord take)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(take);
        string root = Path.GetFullPath(projectRoot);
        List<string> candidates =
        [
            take.RawAudioRelativePath,
            take.ProcessedAudioRelativePath ?? string.Empty,
            .. take.AlignmentHistory.Select(alignment => alignment.AudioRelativePath),
        ];

        foreach (string known in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).ToList())
        {
            Match match = TakeFilePattern().Match(Path.GetFileName(known));
            if (!match.Success) continue;
            string? directory = Path.GetDirectoryName(known.Replace('/', Path.DirectorySeparatorChar));
            string fullDirectory = directory is null ? root : SafeResolve(root, directory) ?? string.Empty;
            if (fullDirectory.Length == 0 || !Directory.Exists(fullDirectory)) continue;
            foreach (string file in Directory.EnumerateFiles(fullDirectory, $"{match.Groups["stem"].Value}--processed-*.wav"))
                candidates.Add(Path.GetRelativePath(root, file));
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => SafeResolve(root, path) is string full && File.Exists(full))
            .ToList();
    }

    /// <summary>Size of a project-relative file, or null when it is missing or outside the project.</summary>
    public static long? Size(string projectRoot, string relativePath)
    {
        string? full = SafeResolve(Path.GetFullPath(projectRoot), relativePath);
        if (full is null) return null;
        FileInfo info = new(full);
        return info.Exists ? info.Length : null;
    }

    internal static string? SafeResolve(string root, string relativePath)
    {
        try { return VoiceBenchProjectLayout.ResolveRelativePath(root, relativePath); }
        catch (InvalidDataException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // "<segment>--take-0003.raw.wav", "<segment>--take-0003--processed-001.wav".
    [GeneratedRegex(@"^(?<stem>.+--take-\d{4})(\.raw\.wav|--processed-\d{3}\.wav)$", RegexOptions.IgnoreCase)]
    private static partial Regex TakeFilePattern();
}
