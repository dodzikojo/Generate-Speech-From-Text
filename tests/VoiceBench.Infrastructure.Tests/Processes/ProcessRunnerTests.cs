using System.Diagnostics;
using IssueLab.VoiceBench.Infrastructure.Processes;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Processes;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancellationTerminatesProcessTreePromptly()
    {
        ProcessRunner runner = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150));
        Stopwatch elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProcessRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"]),
            cancellation.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {elapsed.Elapsed}.");
    }
}
