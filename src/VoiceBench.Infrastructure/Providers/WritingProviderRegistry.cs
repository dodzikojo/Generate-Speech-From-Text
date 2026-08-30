using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class WritingProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IWritingProvider> _providers;

    public WritingProviderRegistry(IEnumerable<IWritingProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(
            provider => provider.Descriptor.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IWritingProvider> Providers => _providers.Values.ToArray();

    public IWritingProvider GetRequired(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out IWritingProvider? provider)
            ? provider
            : throw new KeyNotFoundException(
                $"Writing provider '{providerId}' is not registered. VoiceBench will not substitute another provider.");
    }
}
