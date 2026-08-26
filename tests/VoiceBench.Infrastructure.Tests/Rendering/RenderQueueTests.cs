using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Rendering;

public sealed class RenderQueueTests
{
    [Fact]
    public async Task RecoverInterruptedAsync_RequiresExplicitRetryAfterReload()
    {
        using var directory = new TemporaryDirectory();
        var journal = Path.Combine(directory.Root, "07-reports", "render-queue.json");
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        using (var queue = new FileRenderQueue(journal, new FixedTimeProvider(), () => id))
        {
            await queue.EnqueueAsync(Request());
            var claimed = await queue.ClaimNextAsync();
            Assert.Equal(RenderJobStatus.InProgress, claimed?.Status);
        }

        using (var reloaded = new FileRenderQueue(journal, new FixedTimeProvider()))
        {
            Assert.Equal(1, await reloaded.RecoverInterruptedAsync());
            var interrupted = Assert.Single(await reloaded.GetJobsAsync());
            Assert.Equal(RenderJobStatus.Interrupted, interrupted.Status);
            Assert.Null(await reloaded.ClaimNextAsync());

            await reloaded.RetryAsync(id);
            var retried = await reloaded.ClaimNextAsync();
            Assert.Equal(RenderJobStatus.InProgress, retried?.Status);
        }
    }

    [Fact]
    public async Task CompleteAsync_PersistsNormalizedRelativeRawPath()
    {
        using var directory = new TemporaryDirectory();
        using var queue = new FileRenderQueue(Path.Combine(directory.Root, "queue.json"));
        var job = await queue.EnqueueAsync(Request());
        await queue.ClaimNextAsync();

        await queue.CompleteAsync(job.JobId, "04-generated-audio\\segments\\segment-1\\take.raw.wav");

        var completed = Assert.Single(await queue.GetJobsAsync());
        Assert.Equal(RenderJobStatus.Completed, completed.Status);
        Assert.Equal("04-generated-audio/segments/segment-1/take.raw.wav", completed.RawAudioRelativePath);
        await Assert.ThrowsAsync<ArgumentException>(
            () => queue.CompleteAsync(job.JobId, "..\\outside.raw.wav"));
    }

    [Fact]
    public async Task JournalContainsNoTemporaryFileAfterAtomicSave()
    {
        using var directory = new TemporaryDirectory();
        using var queue = new FileRenderQueue(Path.Combine(directory.Root, "reports", "queue.json"));

        await queue.EnqueueAsync(Request());

        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Root, "reports"), "*.tmp"));
        Assert.True(File.Exists(Path.Combine(directory.Root, "reports", "queue.json")));
    }

    private static RenderQueueRequest Request() => new(
        "C:\\projects\\demo",
        "segment-1",
        "openai",
        "gpt-4o-mini-tts",
        "marin",
        "03-render-copies/segment-1--openai.txt",
        1);
}
