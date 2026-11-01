using System.Globalization;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Mastering;

public sealed record AudioPreviewResult(string RelativePath, string MimeType, string AudioBase64,
    double StartSeconds, double DurationSeconds);

public sealed class AudioPreviewService(IProcessRunner runner)
{
    public async Task<AudioPreviewResult> CreateAsync(string projectRoot, string relativePath,
        double startSeconds, double durationSeconds, ToolchainPaths tools, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0 || durationSeconds > 30)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Audio previews require a nonnegative start and a duration up to 30 seconds.");
        string normalized = relativePath.Replace('\\', '/');
        if (!normalized.StartsWith("04-generated-audio/", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("05-masters/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a known project audio artifact for preview.");
        string input = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, normalized);
        if (!File.Exists(input)) throw new FileNotFoundException("The project audio artifact does not exist.");
        string directory = Path.Combine(Path.GetTempPath(), "IssueLab-VoiceBench-Previews");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            ProcessResult result = await runner.RunAsync(new ProcessRequest(tools.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
                "-ss", startSeconds.ToString("R", CultureInfo.InvariantCulture), "-i", input,
                "-t", durationSeconds.ToString("R", CultureInfo.InvariantCulture),
                "-ac", "1", "-ar", "24000", "-codec:a", "libmp3lame", "-b:a", "96k", output,
            ], directory), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || !File.Exists(output)) throw new InvalidOperationException("The audio preview could not be generated. The original audio is unchanged.");
            FileInfo info = new(output);
            if (info.Length == 0 || info.Length > 384 * 1024) throw new InvalidDataException("The preview was empty or exceeded the 384 KiB audio limit.");
            byte[] bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            return new(normalized, "audio/mpeg", Convert.ToBase64String(bytes), startSeconds, durationSeconds);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
