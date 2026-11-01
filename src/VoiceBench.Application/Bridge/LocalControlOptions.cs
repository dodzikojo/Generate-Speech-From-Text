using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace IssueLab.VoiceBench.Application.Bridge;

public sealed record LocalControlOptions
{
    public string PipeName { get; init; } = DefaultPipeName;
    public bool Enabled { get; init; } = true;
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
    public int MaximumMessageBytes { get; init; } = 4 * 1024 * 1024;
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public string ClientName { get; init; } = "VoiceBench automation";
    public static string DefaultPipeName => "IssueLab.VoiceBench.v1." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..20] +
        (LocalControlSecurity.IsElevated() ? ".elevated" : ".standard");

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 200 || PipeName.IndexOfAny(['\\', '/', ':']) >= 0)
            throw new ArgumentException("A local pipe name is required.", nameof(PipeName));
        if (MaximumMessageBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumMessageBytes));
    }
}

public sealed record LocalControlConnection(string ConnectionId, string InstanceId, DateTimeOffset ConnectedAt,
    string? ClientName = null, DateTimeOffset? LastSeenAt = null);

internal sealed record BridgeHello(string Protocol, int Version, string? ClientId = null, string? ClientName = null, string Mode = "request");
internal sealed record BridgeWelcome(string Protocol, int Version, string InstanceId, int MaximumMessageBytes, bool Enabled);
