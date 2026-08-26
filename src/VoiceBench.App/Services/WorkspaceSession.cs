using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.App.Services;

public sealed class WorkspaceSession(IProjectRepository projects)
{
    public string? ProjectRoot { get; private set; }

    public VoiceBenchProjectDocument? Project { get; private set; }

    public bool HasProject => ProjectRoot is not null && Project is not null;

    public event EventHandler? ProjectChanged;

    public event EventHandler? ProjectActivated;

    public async Task CreateAsync(
        string parentFolder,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string folderName = CreateSafeFolderName(name);
        string projectRoot = Path.Combine(Path.GetFullPath(parentFolder), folderName);
        VoiceBenchProjectDocument project = await projects.CreateAsync(
            projectRoot,
            name,
            cancellationToken);
        Activate(projectRoot, project);
    }

    public async Task OpenAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);
        VoiceBenchProjectDocument project = await projects.LoadAsync(root, cancellationToken);
        Activate(root, project);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        string root = RequireProjectRoot();
        Project = await projects.LoadAsync(root, cancellationToken);
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(
        VoiceBenchProjectDocument project,
        CancellationToken cancellationToken = default)
    {
        string root = RequireProjectRoot();
        await projects.SaveAsync(root, project, cancellationToken);
        Project = await projects.LoadAsync(root, cancellationToken);
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public string RequireProjectRoot() => ProjectRoot
        ?? throw new InvalidOperationException("Open or create a VoiceBench project first.");

    public VoiceBenchProjectDocument RequireProject() => Project
        ?? throw new InvalidOperationException("Open or create a VoiceBench project first.");

    public string ResolveProjectPath(string relativePath) =>
        VoiceBenchProjectLayout.ResolveRelativePath(RequireProjectRoot(), relativePath);

    private void Activate(string projectRoot, VoiceBenchProjectDocument project)
    {
        ProjectRoot = projectRoot;
        Project = project;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        ProjectActivated?.Invoke(this, EventArgs.Empty);
    }

    private static string CreateSafeFolderName(string name)
    {
        string safe = string.Concat(name.Trim().Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        safe = safe.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "VoiceBench project" : safe;
    }
}
