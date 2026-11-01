using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using System.Text.Json;

namespace IssueLab.VoiceBench.App.Services;

public sealed class WorkspaceSession(
    IProjectRepository projects,
    FileRecentProjectCatalog recentProjects,
    IAuthoringTransactionManager? authoringTransactions = null)
{
    public string? ProjectRoot { get; private set; }

    public VoiceBenchProjectDocument? Project { get; private set; }

    public bool HasProject => ProjectRoot is not null && Project is not null;

    public event EventHandler? ProjectChanged;

    public event EventHandler? ProjectActivated;

    // Desktop composition supplies ownership checks without coupling this session to WinUI.
    public Action<string, VoiceBenchProjectDocument>? BeforeActivate { get; set; }

    public Action<string>? BeforeProjectChange { get; set; }

    public void ApplyAutomationProject(string? projectRoot, VoiceBenchProjectDocument? project)
    {
        bool changedRoot = !string.Equals(ProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase);
        ProjectRoot = projectRoot;
        Project = project;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        if (changedRoot && project is not null) ProjectActivated?.Invoke(this, EventArgs.Empty);
    }

    public async Task CreateAsync(
        string parentFolder,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string folderName = CreateSafeFolderName(name);
        string projectRoot = Path.Combine(Path.GetFullPath(parentFolder), folderName);
        BeforeProjectChange?.Invoke(projectRoot);
        VoiceBenchProjectDocument project = await projects.CreateAsync(
            projectRoot,
            name,
            cancellationToken);
        project = await projects.LoadAsync(projectRoot, cancellationToken);
        await TryRecordRecentAsync(projectRoot, project.Name, cancellationToken);
        Activate(projectRoot, project);
    }

    public async Task OpenAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);
        BeforeProjectChange?.Invoke(root);
        if (authoringTransactions is not null)
        {
            await authoringTransactions.RecoverInterruptedAsync(root, cancellationToken);
        }
        VoiceBenchProjectDocument project = await projects.LoadAsync(root, cancellationToken);
        await TryRecordRecentAsync(root, project.Name, cancellationToken);
        Activate(root, project);
    }

    public async Task<bool> RestoreMostRecentAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RecentProjectEntry> entries;
        try
        {
            entries = await recentProjects.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return false;
        }

        foreach (RecentProjectEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(entry.ProjectRoot, VoiceBenchProjectLayout.ManifestFileName)))
            {
                continue;
            }

            try
            {
                if (authoringTransactions is not null)
                {
                    await authoringTransactions.RecoverInterruptedAsync(entry.ProjectRoot, cancellationToken);
                }
                VoiceBenchProjectDocument project = await projects.LoadAsync(entry.ProjectRoot, cancellationToken);
                Activate(entry.ProjectRoot, project, notifyActivated: false);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ProjectSchemaException)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
        }

        return false;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        string root = RequireProjectRoot();
        if (authoringTransactions is not null)
        {
            await authoringTransactions.RecoverInterruptedAsync(root, cancellationToken);
        }
        Project = await projects.LoadAsync(root, cancellationToken);
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(
        VoiceBenchProjectDocument project,
        CancellationToken cancellationToken = default)
    {
        string root = RequireProjectRoot();
        if (projects is FileProjectRepository fileRepository)
        {
            Project = await fileRepository.UpdateAsync(root, current =>
            {
                if (current.UpdatedAt != project.UpdatedAt)
                    throw new InvalidOperationException("The project changed while this action was being prepared. Refresh the project and try again; the newer manifest was preserved.");
                return project;
            }, cancellationToken);
        }
        else
        {
            await projects.SaveAsync(root, project, cancellationToken);
            Project = await projects.LoadAsync(root, cancellationToken);
        }
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public string RequireProjectRoot() => ProjectRoot
        ?? throw new InvalidOperationException("Open or create a VoiceBench project first.");

    public VoiceBenchProjectDocument RequireProject() => Project
        ?? throw new InvalidOperationException("Open or create a VoiceBench project first.");

    public string ResolveProjectPath(string relativePath) =>
        VoiceBenchProjectLayout.ResolveRelativePath(RequireProjectRoot(), relativePath);

    private void Activate(
        string projectRoot,
        VoiceBenchProjectDocument project,
        bool notifyActivated = true)
    {
        BeforeActivate?.Invoke(projectRoot, project);
        ProjectRoot = projectRoot;
        Project = project;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        if (notifyActivated)
        {
            ProjectActivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task TryRecordRecentAsync(
        string projectRoot,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            await recentProjects.RecordAsync(projectRoot, name, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private static string CreateSafeFolderName(string name)
    {
        string safe = string.Concat(name.Trim().Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        safe = safe.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "VoiceBench project" : safe;
    }
}
