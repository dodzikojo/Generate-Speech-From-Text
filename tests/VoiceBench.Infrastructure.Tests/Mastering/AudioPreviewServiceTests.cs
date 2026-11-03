using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using IssueLab.VoiceBench.Infrastructure.Toolchain;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using Xunit;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Mastering;

public sealed class AudioPreviewServiceTests
{
    [Fact]
    public async Task Preview_is_bounded_preserves_original_and_cleans_its_derived_file()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Root, "04-generated-audio", "take.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var runner = new PreviewRunner();
        var result = await new AudioPreviewService(runner).CreateAsync(directory.Root,
            "04-generated-audio/take.wav", 2, 10, new ToolchainPaths("ffmpeg.exe", "ffprobe.exe"));
        Assert.Equal("audio/mpeg", result.MimeType);
        Assert.Equal(new byte[] { 4, 5, 6 }, Convert.FromBase64String(result.AudioBase64));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(runner.Output));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 31)]
    [InlineData(0, 0)]
    public async Task Invalid_clip_ranges_never_invoke_ffmpeg(double start, double duration)
    {
        var runner = new PreviewRunner();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AudioPreviewService(runner)
            .CreateAsync(Path.GetTempPath(), "04-generated-audio/take.wav", start, duration, new ToolchainPaths("ffmpeg.exe", "ffprobe.exe")));
        Assert.Null(runner.Output);
    }

    [Fact]
    public async Task Imported_audio_preview_requires_a_registered_asset_and_keeps_original_bytes()
    {
        using var directory=new TemporaryDirectory();
        var repository=new FileProjectRepository();
        var project=await repository.CreateAsync(directory.Root,"Imported preview");
        string relative="01-source/audio/recording.ogg";
        string path=Path.Combine(directory.Root,relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path,[1,2,3]);
        var runner=new PreviewRunner();
        var previews=new AudioPreviewService(runner);
        await Assert.ThrowsAsync<InvalidDataException>(()=>previews.CreateAsync(directory.Root,relative,0,1,new("ffmpeg.exe","ffprobe.exe")));
        Assert.Null(runner.Output);
        await repository.SaveAsync(directory.Root,project with {AudioAssets=[new AudioAssetRecord {Name="Recording",RelativePath=relative}]});
        var preview=await previews.CreateAsync(directory.Root,relative,0,1,new("ffmpeg.exe","ffprobe.exe"));
        Assert.Equal(relative,preview.RelativePath);
        Assert.Equal(new byte[]{1,2,3},await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(runner.Output));
    }

    private sealed class PreviewRunner : IProcessRunner
    {
        public string? Output { get; private set; }
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Output = request.Arguments.Last();
            await File.WriteAllBytesAsync(Output, [4, 5, 6], cancellationToken);
            return new(0, string.Empty, string.Empty);
        }
    }
}
