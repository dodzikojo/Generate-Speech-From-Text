using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Application;

/// <summary>Held for the entire active-project lifetime, including provider waits.</summary>
public sealed class ProjectLease : IDisposable
{
    private readonly FileStream _stream;
    public string ProjectRoot { get; }

    private ProjectLease(string root, FileStream stream) { ProjectRoot = root; _stream = stream; }

    public static ProjectLease Acquire(string projectRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string directory = Path.Combine(root, ".voicebench");
        Directory.CreateDirectory(directory);
        try
        {
            return new(root, new FileStream(Path.Combine(directory, "project.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            throw new AutomationException("project.in-use", "This project is owned by another VoiceBench process.");
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Serializes short mutations and reloads before each merge; never saves a caller's old manifest.</summary>
public sealed class ProjectCoordinator(IProjectRepository repository) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<VoiceBenchProjectDocument> UpdateAsync(string root,
        Func<VoiceBenchProjectDocument, VoiceBenchProjectDocument> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProjectDocumentUpdater.UpdateAsync(repository, root, update, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
