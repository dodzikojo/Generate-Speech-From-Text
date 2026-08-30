using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public sealed class WritingAssistantService(
    WritingProviderRegistry providers,
    ProviderCredentialResolver credentials) : IWritingAssistantService
{
    public IReadOnlyCollection<WritingProviderDescriptor> Providers =>
        providers.Providers.Select(provider => provider.Descriptor).ToArray();

    public async Task<ChapterOutline> CreateOutlineAsync(
        string providerId,
        WritingOutlineRequest request,
        CancellationToken cancellationToken = default)
    {
        IWritingProvider provider = providers.GetRequired(providerId);
        string apiKey = await ResolveKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return await provider.CreateOutlineAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChapterDraft> CreateDraftAsync(
        string providerId,
        WritingDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        IWritingProvider provider = providers.GetRequired(providerId);
        string apiKey = await ResolveKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return await provider.CreateDraftAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WritingProposal> ReviseAsync(
        string providerId,
        WritingRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        IWritingProvider provider = providers.GetRequired(providerId);
        string apiKey = await ResolveKeyAsync(provider, cancellationToken).ConfigureAwait(false);
        return await provider.ReviseAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private static int EstimateInputTokens(string text) => Math.Max(1, (int)Math.Ceiling(text.Length / 3.0));

    private async Task<string> ResolveKeyAsync(IWritingProvider provider, CancellationToken cancellationToken)
    {
        string? apiKey = await credentials.ResolveAsync(provider.Descriptor.ProviderId, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new WritingProviderException(
                provider.Descriptor.ProviderId,
                WritingProviderFailureKind.Credentials,
                $"No {provider.Descriptor.DisplayName} API key is configured.");
        }

        return apiKey;
    }
}
