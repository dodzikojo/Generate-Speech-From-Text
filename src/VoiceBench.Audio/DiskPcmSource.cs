using System.Runtime.InteropServices;

namespace IssueLab.VoiceBench.Audio;

/// <summary>Decoded 48 kHz stereo f32le cache, streamed using bounded render blocks.</summary>
public sealed class DiskPcmSource : IAudioFrameSource
{
    private readonly FileStream _file;
    public long FrameCount { get; }
    public int OriginalChannels { get; }
    public DiskPcmSource(string path, int originalChannels)
    {
        _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        if (_file.Length % 8 != 0) { _file.Dispose(); throw new InvalidDataException("The float cache has an incomplete audio frame."); }
        FrameCount = _file.Length / 8;
        OriginalChannels = originalChannels;
    }
    public int ReadFrames(long frame, Span<float> stereo)
    {
        if (frame < 0 || stereo.Length % 2 != 0) throw new ArgumentOutOfRangeException(nameof(frame));
        int frames = (int)Math.Min(stereo.Length / 2, Math.Max(0, FrameCount - frame));
        var bytes = MemoryMarshal.AsBytes(stereo[..(frames * 2)]);
        _file.Position = checked(frame * 8);
        _file.ReadExactly(bytes);
        return frames;
    }
    public void Dispose() => _file.Dispose();
}
