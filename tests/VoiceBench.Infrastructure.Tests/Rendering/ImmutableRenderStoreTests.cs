using System.Text;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Rendering;

public sealed class ImmutableRenderStoreTests
{
    [Fact]
    public async Task SaveRawAsync_CreatesANewVersionWithoutOverwritingEarlierTake()
    {
        using var directory = new TemporaryDirectory();
        var store = new ImmutableRenderStore();
        var firstAudio = Encoding.UTF8.GetBytes("first provider response");
        var secondAudio = Encoding.UTF8.GetBytes("second provider response");

        var first = await store.SaveRawAsync(directory.Root, "segment-a", firstAudio);
        var second = await store.SaveRawAsync(directory.Root, "segment-a", secondAudio);

        Assert.Equal("04-generated-audio/segments/segment-a/segment-a--take-0001.raw.wav", first);
        Assert.Equal("04-generated-audio/segments/segment-a/segment-a--take-0002.raw.wav", second);
        Assert.Equal(firstAudio, await File.ReadAllBytesAsync(Path.Combine(directory.Root, first)));
        Assert.Equal(secondAudio, await File.ReadAllBytesAsync(Path.Combine(directory.Root, second)));
    }

    [Fact]
    public async Task SaveRawAsync_ConcurrentWritersReceiveDifferentTakePaths()
    {
        using var directory = new TemporaryDirectory();
        var store = new ImmutableRenderStore();

        var paths = await Task.WhenAll(
            store.SaveRawAsync(directory.Root, "segment-a", new byte[] { 1 }),
            store.SaveRawAsync(directory.Root, "segment-a", new byte[] { 2 }));

        Assert.Equal(2, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(paths, path => Assert.True(File.Exists(Path.Combine(directory.Root, path))));
    }
}
