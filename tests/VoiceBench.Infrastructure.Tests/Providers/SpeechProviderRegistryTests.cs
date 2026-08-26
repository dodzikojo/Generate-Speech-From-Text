using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class SpeechProviderRegistryTests
{
    [Fact]
    public void GetRequired_UnknownProviderDoesNotFallback()
    {
        var openAi = new OpenAiSpeechProvider(new HttpClient(new StubHttpMessageHandler()));
        var registry = new SpeechProviderRegistry([openAi]);

        var exception = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("gemini"));

        Assert.Contains("will not substitute", exception.Message, StringComparison.Ordinal);
    }
}
