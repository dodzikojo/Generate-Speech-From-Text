using System.Buffers.Binary;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed record WaveFileMetadata(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    TimeSpan Duration);

public static class WaveFileInspector
{
    public static WaveFileMetadata Read(ReadOnlySpan<byte> audio)
    {
        if (audio.Length < 12
            || !audio[..4].SequenceEqual("RIFF"u8)
            || !audio.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Audio is not a RIFF/WAVE file.");
        }

        int? sampleRate = null;
        int? channels = null;
        int? bitsPerSample = null;
        int? byteRate = null;
        int? dataLength = null;

        for (var offset = 12; offset <= audio.Length - 8;)
        {
            var chunkId = audio.Slice(offset, 4);
            var declaredChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(offset + 4, 4));
            var streamingDataChunk = chunkId.SequenceEqual("data"u8)
                && declaredChunkLength == uint.MaxValue;
            int chunkLength;
            if (streamingDataChunk)
            {
                // Streamed WAV responses cannot know the data length when the header is emitted.
                // Once the HTTP body has been buffered, the remaining payload is the complete data chunk.
                chunkLength = audio.Length - offset - 8;
            }
            else if (declaredChunkLength > int.MaxValue)
            {
                throw new InvalidDataException("WAVE chunk length exceeds the supported payload size.");
            }
            else
            {
                chunkLength = (int)declaredChunkLength;
            }

            if (offset + 8L + chunkLength > audio.Length)
            {
                throw new InvalidDataException("WAVE chunk length exceeds the audio payload.");
            }

            var chunk = audio.Slice(offset + 8, chunkLength);
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    throw new InvalidDataException("WAVE format chunk is incomplete.");
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4, 4));
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(8, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14, 2));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataLength = chunkLength;
            }

            offset = streamingDataChunk
                ? audio.Length
                : offset + 8 + chunkLength + (chunkLength & 1);
        }

        if (sampleRate is not > 0
            || channels is not > 0
            || bitsPerSample is not > 0
            || byteRate is not > 0
            || dataLength is null)
        {
            throw new InvalidDataException("WAVE audio is missing required format or data metadata.");
        }

        return new WaveFileMetadata(
            sampleRate.Value,
            channels.Value,
            bitsPerSample.Value,
            TimeSpan.FromSeconds((double)dataLength.Value / byteRate.Value));
    }
}
