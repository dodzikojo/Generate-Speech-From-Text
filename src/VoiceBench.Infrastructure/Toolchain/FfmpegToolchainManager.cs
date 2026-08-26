using System.Security.Cryptography;

namespace IssueLab.VoiceBench.Infrastructure.Toolchain;

public sealed class FfmpegToolchainManager(HttpClient httpClient, string? toolsRoot = null)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string _toolsRoot = toolsRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IssueLab",
        "VoiceBench",
        "Tools");

    public static ToolchainPaths FromExistingExecutable(string ffmpegPath)
    {
        string fullFfmpegPath = Path.GetFullPath(ffmpegPath);
        string? directory = Path.GetDirectoryName(fullFfmpegPath);
        string ffprobePath = Path.Combine(directory ?? string.Empty, "ffprobe.exe");
        ToolchainPaths result = new(fullFfmpegPath, ffprobePath);
        if (!result.Exists)
        {
            throw new FileNotFoundException("Both ffmpeg.exe and ffprobe.exe must exist in the selected directory.");
        }

        return result;
    }

    public async Task<ToolchainPaths> EnsureAsync(
        PinnedFfmpegRelease? release = null,
        CancellationToken cancellationToken = default)
    {
        release ??= PinnedFfmpegRelease.WindowsX64;
        string installRoot = GetInstallRoot(release);
        ToolchainPaths? installed = FindInstalled(release);
        if (installed is not null)
        {
            return installed;
        }

        Directory.CreateDirectory(_toolsRoot);
        string operationId = Guid.NewGuid().ToString("N");
        string archivePath = Path.Combine(_toolsRoot, $"{operationId}-{release.ArchiveFileName}.tmp");
        string stagingRoot = Path.Combine(_toolsRoot, $".install-{operationId}");

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                release.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (FileStream output = new(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            string actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(release.Sha256)))
            {
                throw new InvalidDataException(
                    $"FFmpeg checksum mismatch. Expected {release.Sha256}; received {actualHash}.");
            }

            SecureZipExtractor.Extract(archivePath, stagingRoot);
            ToolchainPaths extracted = FindExecutables(stagingRoot)
                ?? throw new InvalidDataException("The verified FFmpeg archive did not contain ffmpeg.exe and ffprobe.exe.");

            string payloadRoot = FindSharedParent(extracted.FfmpegPath, extracted.FfprobePath);
            Directory.Move(payloadRoot, installRoot);
            return FindExecutables(installRoot)
                ?? throw new InvalidDataException("FFmpeg installation completed without usable executables.");
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingRoot);
        }
    }

    public ToolchainPaths? FindInstalled(PinnedFfmpegRelease? release = null)
    {
        release ??= PinnedFfmpegRelease.WindowsX64;
        return FindExecutables(GetInstallRoot(release));
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static ToolchainPaths? FindExecutables(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        string? ffmpeg = Directory.EnumerateFiles(root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        string? ffprobe = Directory.EnumerateFiles(root, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
        return ffmpeg is not null && ffprobe is not null ? new ToolchainPaths(ffmpeg, ffprobe) : null;
    }

    private string GetInstallRoot(PinnedFfmpegRelease release) =>
        Path.Combine(_toolsRoot, $"ffmpeg-{release.Version}-win64-lgpl");

    private static string FindSharedParent(string first, string second)
    {
        DirectoryInfo? current = new(Path.GetDirectoryName(first)!);
        string secondFullPath = Path.GetFullPath(second);
        while (current is not null)
        {
            string prefix = current.FullName + Path.DirectorySeparatorChar;
            if (secondFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidDataException("FFmpeg executables did not share an archive directory.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
