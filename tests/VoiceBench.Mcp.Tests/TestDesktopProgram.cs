using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Application.Bridge;

namespace IssueLab.VoiceBench.Mcp.Tests;

/// <summary>Separate fake desktop executable used only by isolated stdio/launch integration tests.</summary>
public static class TestDesktopProgram
{
    public static async Task Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--pipe-name") return;
        // The companion must isolate child output from protocol stdout.
        Console.WriteLine("Fake desktop startup output must not reach MCP stdout.");
        await using var server = new LocalControlServer(new EchoDispatcher(), new() { PipeName = args[1] });
        await server.StartAsync();
        await Task.Delay(TimeSpan.FromMinutes(2));
    }

    internal sealed class EchoDispatcher : IAutomationDispatcher
    {
        internal int Revision { get; set; }
        public Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command == "editor.replace" && request.Arguments.GetProperty("expectedRevision").GetString() == "stale")
                return Task.FromResult(AutomationResult.Error("revision_conflict", "Read the live editor revision before editing."));
            return Task.FromResult(AutomationResult.Success(new { request.Command, request.Arguments, request.RequestId, processId = Environment.ProcessId, revision = Revision }));
        }
    }
}
