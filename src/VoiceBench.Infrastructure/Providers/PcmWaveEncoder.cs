using System.Buffers.Binary;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

internal static class PcmWaveEncoder
{
    public static byte[] Encode16BitMono(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        const int headerLength = 44;
        var output = new byte[headerLength + pcm.Length];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length - 8);
        "WAVE"u8.CopyTo(output.AsSpan(8));
        "fmt "u8.CopyTo(output.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(34), 16);
        "data"u8.CopyTo(output.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), pcm.Length);
        pcm.CopyTo(output.AsSpan(headerLength));
        return output;
    }
}
