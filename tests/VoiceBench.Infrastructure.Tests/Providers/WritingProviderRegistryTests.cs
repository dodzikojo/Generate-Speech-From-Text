using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Providers;

public sealed class WritingProviderRegistryTests
{
    [Fact]
    public void GetRequired_DoesNotFallBackToAnotherProvider()
    {
        WritingProviderRegistry registry = new(
            [new OpenAiWritingProvider(new HttpClient(new StubHttpMessageHandler()))]);

        KeyNotFoundException exception = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("gemini"));

        Assert.Contains("will not substitute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
