using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Credentials;

public sealed class ProviderCredentialResolverTests
{
    [Fact]
    public async Task ResolveAsync_PrefersEnvironmentToCredentialManager()
    {
        var store = new RecordingCredentialStore("stored-key");
        var resolver = new ProviderCredentialResolver(
            store,
            variable => variable == "OPENAI_API_KEY" ? "environment-key" : null);

        var result = await resolver.ResolveAsync("openai");

        Assert.Equal("environment-key", result);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToCredentialManager()
    {
        var store = new RecordingCredentialStore("stored-key");
        var resolver = new ProviderCredentialResolver(store, _ => null);

        var result = await resolver.ResolveAsync("gemini");

        Assert.Equal("stored-key", result);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public void TargetName_IsStableAndRejectsPathLikeProviderIds()
    {
        Assert.Equal("IssueLab.VoiceBench/openai/ApiKey", WindowsCredentialStore.GetTargetName("OpenAI"));
        Assert.Throws<ArgumentException>(() => WindowsCredentialStore.GetTargetName("../openai"));
    }

    private sealed class RecordingCredentialStore(string? value) : ICredentialStore
    {
        public int ReadCount { get; private set; }

        public Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(value);
        }

        public Task SetAsync(string providerId, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
