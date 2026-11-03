using System.Security.Cryptography;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Audio;

public sealed class AudioAssetServiceTests
{
    [Fact]
    public async Task ImportCopiesOriginalBytesAndBuildsBoundedHashVersionedCaches()
    {
        using TemporaryDirectory dir = new();
        var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Audio");
        var runner = new AudioProcessFake(); var service = new AudioAssetService(repository, runner, Tools);
        string original = Path.Combine(dir.Root, "original.wav"); await File.WriteAllBytesAsync(original, [1, 2, 3, 4]);
        var asset = await service.ImportAsync(dir.Root, original);
        Assert.StartsWith("01-source/audio/", asset.RelativePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), asset.Sha256);
        await File.WriteAllBytesAsync(original, [99]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, asset.RelativePath)));
        var prepared = await service.PrepareAsync(dir.Root, asset);
        Assert.Equal(960, prepared.FrameCount); Assert.Contains($"v{AudioAssetService.CacheVersion}", prepared.PcmPath);
        int calls = runner.Requests.Count;
        Assert.Equal(prepared, await service.PrepareAsync(dir.Root, asset)); Assert.Equal(calls, runner.Requests.Count);
        var peaks = await service.ReadPeaksAsync(dir.Root, asset, 1, 2);
        Assert.Equal(4, peaks.TotalBuckets); Assert.Equal(2, peaks.Peaks.Count);
        Assert.All(peaks.Peaks, p => { Assert.Equal(.25f, p.MaximumLeft); Assert.Equal(-.5f, p.MinimumRight); });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadPeaksAsync(dir.Root, asset, 0, 8193));
    }

    [Fact]
    public async Task ChangedSourceCannotReuseOldCacheAndRelinkRequiresExactOriginalBytes()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Audio");
        var service = new AudioAssetService(repository, new AudioProcessFake(), Tools);
        string source = Path.Combine(dir.Root, "original.wav"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var asset = await service.ImportAsync(dir.Root, source); await service.PrepareAsync(dir.Root, asset);
        string managed = VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, asset.RelativePath);
        await File.WriteAllBytesAsync(managed, [9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(dir.Root, asset));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RelinkAsync(dir.Root, asset, source));
        File.Delete(managed);
        await service.RelinkAsync(dir.Root, asset, source);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(managed));
        File.Delete(managed); await File.WriteAllBytesAsync(source, [3, 2, 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RelinkAsync(dir.Root, asset, source));
        Assert.False(File.Exists(managed));
    }

    [Fact]
    public async Task ImportedTakePinsTranscriptAndRequiresExplicitAlignmentChoice()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); var project = await repository.CreateAsync(dir.Root, "Imported");
        const string authoring = "Hello world\n@visual: Show the screen";
        string script = "02-scripts/intro.md"; await File.WriteAllTextAsync(Path.Combine(dir.Root, script), authoring);
        await repository.SaveAsync(dir.Root, project with
        {
            Lexicon = [new PronunciationEntry { Written = "world", Spoken = "wurld" }],
            Segments = [new SegmentDocument { SegmentId = "intro", ScriptRelativePath = script, SourceSha256 = SourceTextHasher.Compute("Hello world") }],
        });
        var service = new AudioAssetService(repository, new AudioProcessFake(), Tools);
        string source = Path.Combine(dir.Root, "recording.wav"); await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var asset = await service.ImportAsync(dir.Root, source, "intro");
        var loaded = await repository.LoadAsync(dir.Root);
        var take = Assert.Single(Assert.Single(loaded.Segments).RenderHistory);
        Assert.Equal(RenderOrigin.Imported, take.Origin); Assert.False(take.ReferenceTranscriptConfirmed); Assert.Equal(asset.RenderId, take.RenderId);
        Assert.Equal("Hello world", await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, take.CleanSourceRelativePath!)));
        var provider = new AlignmentFake();
        var align = new FileWordAlignmentService(repository, new WordAlignmentProviderRegistry([provider]), new ProviderCredentialResolver(new CredentialsFake(), _ => null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => align.AlignAsync(dir.Root, "intro", take.RenderId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => align.AlignImportedAsync(dir.Root, "intro", take.RenderId, "openai", "other-model"));
        Assert.Equal(0, provider.Calls);
        var aligned = await align.AlignImportedAsync(dir.Root, "intro", take.RenderId, "openai", "whisper-1");
        Assert.Equal(WordAlignmentStatus.Aligned, aligned.Status); Assert.Equal("Hello world", provider.SubmittedText);
        Assert.Equal("imported", Assert.Single(Assert.Single((await repository.LoadAsync(dir.Root)).Segments).RenderHistory).ProviderId);
        await File.WriteAllBytesAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, asset.RelativePath), [9, 9, 9, 9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => align.AlignImportedAsync(dir.Root, "intro", take.RenderId, "openai", "whisper-1"));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task FailedImportNeverRegistersAnAssetOrChangesExistingTakes()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Audio");
        var service = new AudioAssetService(repository, new AudioProcessFake { FailProbe = true }, Tools);
        string source = Path.Combine(dir.Root, "bad.wav"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(dir.Root, source));
        Assert.Empty((await repository.LoadAsync(dir.Root)).AudioAssets);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Root, "01-source", "audio")));
    }

    internal static ToolchainPaths Tools { get; } = new("ffmpeg", "ffprobe");
    internal sealed class AudioProcessFake : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public bool FailProbe { get; init; }
        public bool FailNormalization { get; set; }
        public List<float[]> PremasterBlocks { get; } = [];
        private readonly Dictionary<string, (string SampleRate, string Channels, int Bits)> _outputFormats = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            if (request.FileName == "ffprobe")
            {
                var format = _outputFormats.GetValueOrDefault(request.Arguments[^1], ("48000", "2", 24));
                return Task.FromResult(FailProbe ? new ProcessResult(1, "", "invalid") : new ProcessResult(0,
                    $"{{\"streams\":[{{\"sample_rate\":\"{format.Item1}\",\"channels\":{format.Item2},\"bits_per_sample\":{format.Item3}}}],\"format\":{{\"duration\":\"0.02\"}}}}", ""));
            }
            if (request.Arguments[^1] == "NUL") return Task.FromResult(new ProcessResult(0, "", "{\"input_i\":\"-16\",\"input_tp\":\"-2\",\"input_lra\":\"1\",\"input_thresh\":\"-26\",\"target_offset\":\"0\"}"));
            string output = request.Arguments[^1];
            if ((request.Arguments.Contains("pcm_s24le") || request.Arguments.Contains("pcm_s16le")) && FailNormalization) return Task.FromResult(new ProcessResult(1, "", "failed"));
            if (request.Arguments.Contains("pcm_s24le") || request.Arguments.Contains("pcm_s16le"))
            {
                int inputIndex = request.Arguments.ToList().IndexOf("-i") + 1;
                byte[] raw = File.ReadAllBytes(request.Arguments[inputIndex]);
                PremasterBlocks.Add([BitConverter.ToSingle(raw), BitConverter.ToSingle(raw, 4)]);
                _outputFormats[output] = (request.Arguments[request.Arguments.ToList().LastIndexOf("-ar") + 1],
                    request.Arguments[request.Arguments.ToList().LastIndexOf("-ac") + 1], request.Arguments.Contains("pcm_s16le") ? 16 : 24);
            }
            using (var file = new BinaryWriter(File.Open(output, FileMode.Create, FileAccess.Write)))
            {
                if (request.Arguments.Contains("f32le") && request.Arguments.Contains("pcm_f32le"))
                    for (int i = 0; i < 960; i++) { file.Write(.25f); file.Write(-.5f); }
                else file.Write(new byte[] { 0, 1, 2, 3 });
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    private sealed class AlignmentFake : IWordAlignmentProvider
    {
        public string ProviderId => "openai"; public string ModelId => "whisper-1";
        public int Calls { get; private set; } public string? SubmittedText { get; private set; }
        public Task<WordAlignmentProviderResult> TranscribeAsync(WordAlignmentProviderRequest request, string apiKey, CancellationToken cancellationToken)
        { Calls++; SubmittedText = request.SubmittedText; return Task.FromResult(new WordAlignmentProviderResult([
            new("Hello", TimeSpan.Zero, TimeSpan.FromSeconds(.005)), new("world", TimeSpan.FromSeconds(.005), TimeSpan.FromSeconds(.015))], TimeSpan.FromSeconds(.02))); }
    }
    private sealed class CredentialsFake : ICredentialStore
    {
        public Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake-key");
        public Task SetAsync(string providerId, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
