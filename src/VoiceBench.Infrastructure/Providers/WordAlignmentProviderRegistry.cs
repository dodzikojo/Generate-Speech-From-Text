using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class WordAlignmentProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IWordAlignmentProvider> _providers;

    public WordAlignmentProviderRegistry(IEnumerable<IWordAlignmentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    public IWordAlignmentProvider GetRequired(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out IWordAlignmentProvider? provider)
            ? provider
            : throw new WordAlignmentProviderException(
                providerId,
                WordAlignmentFailureKind.Unsupported,
                $"Word alignment is not supported for provider '{providerId}'. VoiceBench will not substitute another provider.");
    }
}
