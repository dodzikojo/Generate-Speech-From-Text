using IssueLab.VoiceBench.Application.Bridge;

namespace IssueLab.VoiceBench.Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string? executable = null;
            string? pipeName = null;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] is not ("--app-executable" or "--pipe-name") || index + 1 >= args.Length)
                    throw new ArgumentException("Usage: voicebench-mcp [--app-executable <desktop.exe>] [--pipe-name <local-name>]");
                var option = args[index++];
                if (option == "--app-executable") executable = args[index]; else pipeName = args[index];
            }
            var options = pipeName is null ? new LocalControlOptions { ClientName = "VoiceBench MCP" } : new LocalControlOptions { PipeName = pipeName, ClientName = "VoiceBench MCP" };
            await using var client = new LocalControlClient(options, executable is null
                ? DesktopAppLauncher.LaunchAsync
                : cancellationToken => DesktopAppLauncher.LaunchExecutableAsync(executable, options.PipeName, cancellationToken));
            await using var host = new VoiceBenchMcpServer(client);
            await host.RunStdioAsync();
            return 0;
        }
        catch (Exception)
        {
            // No arguments, submitted text, credentials or exception payloads enter diagnostics.
            await Console.Error.WriteLineAsync("VoiceBench MCP could not start. Verify its configuration and installed desktop app.");
            return 1;
        }
    }
}
