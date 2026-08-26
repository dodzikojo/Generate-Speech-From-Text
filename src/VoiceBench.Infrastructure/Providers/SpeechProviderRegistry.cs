using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class SpeechProviderRegistry
{
    private readonly IReadOnlyDictionary<string, ISpeechProvider> _providers;

    public SpeechProviderRegistry(IEnumerable<ISpeechProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(
            provider => provider.Descriptor.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ISpeechProvider> Providers => _providers.Values.ToArray();

    public ISpeechProvider GetRequired(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException(
                $"Speech provider '{providerId}' is not registered. VoiceBench will not substitute another provider.");
    }
}
