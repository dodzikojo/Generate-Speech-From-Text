namespace IssueLab.VoiceBench.Infrastructure.Toolchain;

public sealed record ToolchainPaths(string FfmpegPath, string FfprobePath)
{
    public bool Exists => File.Exists(FfmpegPath) && File.Exists(FfprobePath);
}
