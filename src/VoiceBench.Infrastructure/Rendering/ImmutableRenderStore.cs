namespace IssueLab.VoiceBench.Infrastructure.Rendering;

public sealed class ImmutableRenderStore
{
    public async Task<string> SaveRawAsync(
        string projectRoot,
        string segmentId,
        ReadOnlyMemory<byte> audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (audio.IsEmpty)
        {
            throw new ArgumentException("Raw provider audio cannot be empty.", nameof(audio));
        }

        var root = Path.GetFullPath(projectRoot);
        var safeSegmentId = MakeSafeFileName(segmentId);
        var directory = Path.GetFullPath(Path.Combine(
            root,
            "04-generated-audio",
            "segments",
            safeSegmentId));
        EnsureWithinRoot(root, directory);
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".voicebench-raw-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(audio, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            for (var takeNumber = 1; takeNumber <= 999_999; takeNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = $"{safeSegmentId}--take-{takeNumber:0000}.raw.wav";
                var absolutePath = Path.Combine(directory, fileName);

                try
                {
                    File.Move(temporaryPath, absolutePath);
                    return Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
                }
                catch (IOException) when (File.Exists(absolutePath))
                {
                    // Another render already owns this take number. Advance without overwriting it.
                }
            }

            throw new IOException($"No version slot is available for segment '{segmentId}'.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray();
        var safe = new string(characters).Trim('-', '.');
        return safe.Length > 0
            ? safe
            : throw new ArgumentException("Segment identifier does not contain a valid file-name character.", nameof(value));
    }

    private static void EnsureWithinRoot(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Render path escaped the project root.");
        }
    }
}
