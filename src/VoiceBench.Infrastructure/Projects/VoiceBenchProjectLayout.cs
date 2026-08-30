namespace IssueLab.VoiceBench.Infrastructure.Projects;

public static class VoiceBenchProjectLayout
{
    public const string ManifestFileName = "voicebench.project.json";
    public const string SourceDirectory = "01-source";
    public const string SourceReferencesDirectory = "01-source/references";
    public const string ScriptsDirectory = "02-scripts";
    public const string ScriptRevisionsDirectory = "02-scripts/.revisions";
    public const string ScriptTrashDirectory = "02-scripts/.trash";
    public const string RenderCopiesDirectory = "03-render-copies";
    public const string GeneratedAudioDirectory = "04-generated-audio";
    public const string AuditionsDirectory = "04-generated-audio/auditions";
    public const string SegmentsDirectory = "04-generated-audio/segments";
    public const string MastersDirectory = "05-masters";
    public const string TranscriptsDirectory = "06-exports/transcripts";
    public const string ProductionScriptsDirectory = "06-exports/production-scripts";
    public const string CueSheetsDirectory = "06-exports/cue-sheets";
    public const string ReportsDirectory = "07-reports";
    public const string InternalDirectory = ".voicebench";
    public const string AuthoringTransactionsDirectory = ".voicebench/authoring-transactions";

    public static IReadOnlyList<string> RequiredDirectories { get; } =
    [
        SourceDirectory,
        SourceReferencesDirectory,
        ScriptsDirectory,
        ScriptRevisionsDirectory,
        ScriptTrashDirectory,
        RenderCopiesDirectory,
        AuditionsDirectory,
        SegmentsDirectory,
        MastersDirectory,
        TranscriptsDirectory,
        ProductionScriptsDirectory,
        CueSheetsDirectory,
        ReportsDirectory,
    ];

    public static void Ensure(string projectRoot)
    {
        foreach (string relativePath in RequiredDirectories)
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, relativePath));
        }
    }

    public static string ResolveRelativePath(string projectRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Project path must be relative: {relativePath}");
        }

        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Project path escapes its root: {relativePath}");
        }

        return fullPath;
    }

    public static string ToManifestPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
