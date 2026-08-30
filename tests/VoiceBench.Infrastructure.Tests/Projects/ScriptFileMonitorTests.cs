using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class ScriptFileMonitorTests
{
    [Fact]
    public async Task Monitor_reports_changes_inside_chapter_directories()
    {
        using TemporaryDirectory directory = new();
        using ScriptFileMonitor monitor = new(directory.Root);
        TaskCompletionSource<string> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ScriptChanged += (_, path) => changed.TrySetResult(path);
        string chapterDirectory = Path.Combine(
            directory.Root,
            VoiceBenchProjectLayout.ScriptsDirectory,
            "001-introduction--chapter");
        Directory.CreateDirectory(chapterDirectory);
        string scriptPath = Path.Combine(chapterDirectory, "001-opening--segment.md");

        await File.WriteAllTextAsync(scriptPath, "Edited outside VoiceBench");
        string reportedPath = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Path.GetFullPath(scriptPath), Path.GetFullPath(reportedPath));
    }
}
