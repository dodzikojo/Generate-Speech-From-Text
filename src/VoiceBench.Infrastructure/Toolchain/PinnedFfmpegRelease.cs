namespace IssueLab.VoiceBench.Infrastructure.Toolchain;

public sealed record PinnedFfmpegRelease(
    string Version,
    Uri DownloadUri,
    string Sha256,
    string ArchiveFileName)
{
    public static PinnedFfmpegRelease WindowsX64 { get; } = new(
        "9.0.1",
        new Uri("https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-20-13-45/ffmpeg-n9.0.1-6-g9d4ca21220-win64-lgpl-9.0.zip"),
        "ccaa910db95451121482a5ebcb8c682123cb4f8a5bd5170fde4a814d7b437772",
        "ffmpeg-n9.0.1-6-g9d4ca21220-win64-lgpl-9.0.zip");
}
