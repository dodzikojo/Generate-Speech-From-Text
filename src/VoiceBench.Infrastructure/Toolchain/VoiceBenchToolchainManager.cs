using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Toolchain;

public sealed class VoiceBenchToolchainManager(
    HttpClient httpClient,
    string? toolsRoot = null) : IToolchainManager
{
    private readonly string _toolsRoot = toolsRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IssueLab",
        "VoiceBench",
        "Tools");
    private readonly FfmpegToolchainManager _ffmpeg = new(httpClient, toolsRoot);

    public Task<ToolchainResolution> ResolveAsync(
        string toolName,
        CancellationToken cancellationToken = default)
    {
        RequireFfmpeg(toolName);
        cancellationToken.ThrowIfCancellationRequested();
        string? executable = Directory.Exists(_toolsRoot)
            ? Directory.EnumerateFiles(_toolsRoot, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        ToolchainResolution result = new(
            "ffmpeg",
            executable,
            executable is null ? null : PinnedFfmpegRelease.WindowsX64.Version,
            IsManaged: executable is not null,
            IsAvailable: executable is not null);
        return Task.FromResult(result);
    }

    public async Task<ToolchainResolution> EnsureAsync(
        string toolName,
        string? existingExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        RequireFfmpeg(toolName);
        ToolchainPaths paths;
        bool managed;
        if (!string.IsNullOrWhiteSpace(existingExecutablePath))
        {
            paths = FfmpegToolchainManager.FromExistingExecutable(existingExecutablePath);
            managed = false;
        }
        else
        {
            paths = await _ffmpeg.EnsureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            managed = true;
        }

        return new ToolchainResolution(
            "ffmpeg",
            paths.FfmpegPath,
            PinnedFfmpegRelease.WindowsX64.Version,
            managed,
            paths.Exists);
    }

    private static void RequireFfmpeg(string toolName)
    {
        if (!string.Equals(toolName, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"VoiceBench has no managed toolchain named '{toolName}'.");
        }
    }
}
