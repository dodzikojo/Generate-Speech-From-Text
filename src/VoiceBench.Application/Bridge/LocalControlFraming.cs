using System.Buffers.Binary;
using System.Text.Json;

namespace IssueLab.VoiceBench.Application.Bridge;

internal static class LocalControlFraming
{
    internal const string Protocol = "VoiceBench.LocalControl";
    internal const int Version = 2;

    internal static async Task WriteAsync<T>(Stream stream, T value, int maximumBytes, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AutomationJson.Options);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Local-control message exceeds the maximum size.");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maximumBytes) throw new InvalidDataException("Invalid local-control message size.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, AutomationJson.Options) ?? throw new InvalidDataException("Empty local-control message.");
    }
}
