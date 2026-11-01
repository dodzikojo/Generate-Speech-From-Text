using System.IO.Compression;
using System.Net;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Toolchain;

public sealed class FfmpegToolchainManagerTests
{
    [Fact]
    public void PinnedRelease_UsesPublishedBtbNBuildAndChecksum()
    {
        PinnedFfmpegRelease release = PinnedFfmpegRelease.WindowsX64;

        Assert.Equal("9.0.2", release.Version);
        Assert.Contains("BtbN/FFmpeg-Builds", release.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(64, release.Sha256.Length);
        Assert.Equal(
            "9d37c564e0d01c18cde184a079fa6ef04d7d2783ada41da81d04165cc8f1a650",
            release.Sha256);
    }

    [Fact]
    public async Task EnsureAsync_VerifiesAndInstallsBothExecutables()
    {
        using TemporaryDirectory directory = new();
        byte[] archive = CreateArchive(("payload/bin/ffmpeg.exe", "ffmpeg"), ("payload/bin/ffprobe.exe", "ffprobe"));
        string checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archive));
        PinnedFfmpegRelease release = new(
            "test",
            new Uri("https://example.invalid/ffmpeg.zip"),
            checksum,
            "ffmpeg.zip");
        using HttpClient client = new(new BytesHandler(archive));
        FfmpegToolchainManager manager = new(client, directory.Root);

        ToolchainPaths result = await manager.EnsureAsync(release);

        Assert.True(result.Exists);
        Assert.Equal("ffmpeg", await File.ReadAllTextAsync(result.FfmpegPath));
        Assert.Equal("ffprobe", await File.ReadAllTextAsync(result.FfprobePath));
        Assert.Equal(result, manager.FindInstalled(release));
    }

    [Fact]
    public async Task EnsureAsync_RejectsChecksumMismatch()
    {
        using TemporaryDirectory directory = new();
        byte[] archive = CreateArchive(("payload/bin/ffmpeg.exe", "ffmpeg"), ("payload/bin/ffprobe.exe", "ffprobe"));
        PinnedFfmpegRelease release = new(
            "test",
            new Uri("https://example.invalid/ffmpeg.zip"),
            new string('0', 64),
            "ffmpeg.zip");
        using HttpClient client = new(new BytesHandler(archive));
        FfmpegToolchainManager manager = new(client, directory.Root);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.EnsureAsync(release));

        Assert.Contains("checksum mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public void SecureZipExtractor_RejectsTraversal()
    {
        using TemporaryDirectory directory = new();
        string archivePath = Path.Combine(directory.Root, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry("../escape.txt").Open());
            writer.Write("unsafe");
        }

        string destination = Path.Combine(directory.Root, "destination");

        Assert.Throws<InvalidDataException>(() => SecureZipExtractor.Extract(archivePath, destination));
        Assert.False(File.Exists(Path.Combine(directory.Root, "escape.txt")));
    }

    private static byte[] CreateArchive(params (string Path, string Contents)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string contents) in entries)
            {
                using StreamWriter writer = new(archive.CreateEntry(path).Open());
                writer.Write(contents);
            }
        }

        return stream.ToArray();
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = request,
            });
        }
    }
}
