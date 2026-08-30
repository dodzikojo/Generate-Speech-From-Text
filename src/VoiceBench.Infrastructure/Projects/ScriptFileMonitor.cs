namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class ScriptFileMonitor : IDisposable
{
    private readonly FileSystemWatcher _watcher;

    public ScriptFileMonitor(string projectRoot)
    {
        string scriptsPath = Path.Combine(Path.GetFullPath(projectRoot), VoiceBenchProjectLayout.ScriptsDirectory);
        Directory.CreateDirectory(scriptsPath);
        _watcher = new FileSystemWatcher(scriptsPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
    }

    public event EventHandler<string>? ScriptChanged;

    public void Dispose() => _watcher.Dispose();

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (IsScript(eventArgs.FullPath))
        {
            ScriptChanged?.Invoke(this, eventArgs.FullPath);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) => OnChanged(sender, eventArgs);

    private static bool IsScript(string path) =>
        string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);
}
