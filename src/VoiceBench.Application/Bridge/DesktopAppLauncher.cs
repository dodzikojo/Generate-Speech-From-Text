using System.Diagnostics;

namespace IssueLab.VoiceBench.Application.Bridge;

public static class DesktopAppLauncher
{
    public const string ApplicationUserModelId = "IssueLab.VoiceBench_vmmh22nn7s1w8!App";

    public static Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = "shell:AppsFolder\\" + ApplicationUserModelId,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
        return Task.CompletedTask;
    }

    public static Task LaunchExecutableAsync(string executablePath, string pipeName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.GetFullPath(executablePath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--pipe-name");
        start.ArgumentList.Add(pipeName);
        var process = Process.Start(start) ?? throw new IOException("The desktop executable could not start.");
        // Drain separately: a development child must never write into MCP's protocol stdout.
        _ = DrainAndDisposeAsync(process);
        return Task.CompletedTask;
    }

    private static async Task DrainAndDisposeAsync(Process process)
    {
        using (process)
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                process.StandardError.BaseStream.CopyToAsync(Stream.Null));
            await process.WaitForExitAsync();
        }
    }
}
