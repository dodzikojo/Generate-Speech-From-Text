using System.Globalization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>Well-known folders of a movable VoiceBench project that the operator can open from the desktop.</summary>
public enum ProjectFolder
{
    Root,
    Source,
    Scripts,
    GeneratedAudio,
    Masters,
    Exports,
    Reports,
}

/// <summary>A narration master or mix export found beneath <c>05-masters</c>.</summary>
public sealed record BuiltMaster(
    string Name,
    int? Version,
    string RelativePath,
    string FullPath,
    DateTimeOffset ModifiedAt,
    long SizeBytes)
{
    public string DisplayName => Version is int version ? $"{Name} · v{version}" : Name;

    public bool IsMp3 => FullPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves project-relative output locations. All results stay inside the project root so that
/// desktop "show in folder" actions can never be pointed outside the operator's project.
/// </summary>
public static class ProjectLocations
{
    private const string MasterSuffix = ".master";

    public static string RelativePath(ProjectFolder folder) => folder switch
    {
        ProjectFolder.Root => string.Empty,
        ProjectFolder.Source => "01-source",
        ProjectFolder.Scripts => "02-scripts",
        ProjectFolder.GeneratedAudio => "04-generated-audio",
        ProjectFolder.Masters => "05-masters",
        ProjectFolder.Exports => "06-exports",
        ProjectFolder.Reports => "07-reports",
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "Unknown project folder."),
    };

    public static string DisplayName(ProjectFolder folder) => folder switch
    {
        ProjectFolder.Root => "Project folder",
        ProjectFolder.Source => "Source and references",
        ProjectFolder.Scripts => "Scripts",
        ProjectFolder.GeneratedAudio => "Generated audio",
        ProjectFolder.Masters => "Masters and mix exports",
        ProjectFolder.Exports => "Exports",
        ProjectFolder.Reports => "Reports",
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "Unknown project folder."),
    };

    public static string Resolve(string projectRoot, ProjectFolder folder)
    {
        string relative = RelativePath(folder);
        return relative.Length == 0 ? NormalizeRoot(projectRoot) : ResolveInsideProject(projectRoot, relative);
    }

    /// <summary>Folder that holds every take of one segment.</summary>
    public static string SegmentTakeFolderRelativePath(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (segmentId.IndexOfAny(['/', '\\']) >= 0 || segmentId.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("A segment id cannot contain path separators.", nameof(segmentId));
        return $"04-generated-audio/segments/{segmentId}";
    }

    /// <summary>The audio file an operator expects for a take: the processed file once approved, otherwise the raw render.</summary>
    public static string? TakeAudioRelativePath(RenderRecord render)
    {
        ArgumentNullException.ThrowIfNull(render);
        // A cleaned-up take's audio is in the Recycle Bin; nothing in the project should point at it.
        if (render.AudioDeletedAt is not null) return null;
        if (!string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath)) return render.ProcessedAudioRelativePath;
        return string.IsNullOrWhiteSpace(render.RawAudioRelativePath) ? null : render.RawAudioRelativePath;
    }

    /// <summary>Resolves a project-relative path and rejects anything that escapes the project root.</summary>
    public static string ResolveInsideProject(string projectRoot, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string root = NormalizeRoot(projectRoot);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Project locations must be relative to the project folder.", nameof(relativePath));
        string combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string trimmed = Path.TrimEndingDirectorySeparator(combined);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        bool inside = string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (!inside) throw new ArgumentException("The location is outside the project folder.", nameof(relativePath));
        return trimmed;
    }

    /// <summary>
    /// Lists narration masters (<c>name-vN.master.wav</c>) and mix exports (<c>name-v001/name-v001.master.wav|mp3</c>),
    /// newest first. Hidden work folders (starting with '.') are skipped.
    /// </summary>
    public static IReadOnlyList<BuiltMaster> ListMasters(string projectRoot)
    {
        string root = NormalizeRoot(projectRoot);
        string masters = Path.Combine(root, RelativePath(ProjectFolder.Masters));
        if (!Directory.Exists(masters)) return [];

        List<BuiltMaster> results = [];
        AddMasters(results, root, masters);
        foreach (string directory in Directory.EnumerateDirectories(masters))
        {
            if (Path.GetFileName(directory).StartsWith('.')) continue;
            AddMasters(results, root, directory);
        }

        return results
            .OrderByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Splits <c>Project-compilation-v3.master.wav</c> into its name and version.</summary>
    public static (string Name, int? Version) ParseMasterFileName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith(MasterSuffix, StringComparison.OrdinalIgnoreCase)) stem = stem[..^MasterSuffix.Length];
        int marker = stem.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        if (marker > 0 && int.TryParse(stem[(marker + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
            return (stem[..marker], version);
        return (stem, null);
    }

    private static void AddMasters(List<BuiltMaster> results, string root, string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            bool isMaster = name.EndsWith(".master.wav", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".master.mp3", StringComparison.OrdinalIgnoreCase);
            if (!isMaster) continue;
            FileInfo info = new(path);
            (string displayName, int? version) = ParseMasterFileName(name);
            results.Add(new BuiltMaster(
                displayName,
                version,
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                info.FullName,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length));
        }
    }

    private static string NormalizeRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
    }
}
