namespace IssueLab.VoiceBench.Infrastructure.Toolchain;

public sealed record PinnedFfmpegRelease(
    string Version,
    Uri DownloadUri,
    string Sha256,
    string ArchiveFileName)
{
    public static PinnedFfmpegRelease WindowsX64 { get; } = new(
        "9.0.2",
        new Uri("https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-9.0.zip"),
        "9d37c564e0d01c18cde184a079fa6ef04d7d2783ada41da81d04165cc8f1a650",
        "ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-9.0.zip");
}
