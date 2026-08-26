using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Credentials;

public sealed class ProviderCredentialResolver
{
    private static readonly IReadOnlyDictionary<string, string> EnvironmentVariables =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini"] = "GEMINI_API_KEY",
            ["openai"] = "OPENAI_API_KEY",
        };

    private readonly ICredentialStore _credentialStore;
    private readonly Func<string, string?> _readEnvironmentVariable;

    public ProviderCredentialResolver(
        ICredentialStore credentialStore,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _readEnvironmentVariable = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public async Task<string?> ResolveAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();

        if (EnvironmentVariables.TryGetValue(providerId, out var environmentVariable))
        {
            var environmentValue = _readEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue;
            }
        }

        return await _credentialStore.GetAsync(providerId, cancellationToken).ConfigureAwait(false);
    }

    public static string? GetEnvironmentVariableName(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return EnvironmentVariables.GetValueOrDefault(providerId);
    }
}
