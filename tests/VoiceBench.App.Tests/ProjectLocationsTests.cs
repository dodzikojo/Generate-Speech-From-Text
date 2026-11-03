using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class ProjectLocationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "voicebench-locations-" + Guid.NewGuid().ToString("N"));

    public ProjectLocationsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(ProjectFolder.Scripts, "02-scripts")]
    [InlineData(ProjectFolder.GeneratedAudio, "04-generated-audio")]
    [InlineData(ProjectFolder.Masters, "05-masters")]
    [InlineData(ProjectFolder.Exports, "06-exports")]
    [InlineData(ProjectFolder.Reports, "07-reports")]
    public void Known_folders_resolve_inside_the_project(ProjectFolder folder, string relative)
    {
        Assert.Equal(Path.Combine(_root, relative), ProjectLocations.Resolve(_root, folder));
        Assert.False(string.IsNullOrWhiteSpace(ProjectLocations.DisplayName(folder)));
    }

    [Fact]
    public void Project_root_resolves_without_a_trailing_separator()
    {
        Assert.Equal(_root, ProjectLocations.Resolve(_root + Path.DirectorySeparatorChar, ProjectFolder.Root));
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("04-generated-audio/../../outside.wav")]
    [InlineData("C:\\Windows\\notepad.exe")]
    public void Locations_that_escape_the_project_are_rejected(string relative)
    {
        Assert.Throws<ArgumentException>(() => ProjectLocations.ResolveInsideProject(_root, relative));
    }

    [Fact]
    public void A_project_at_a_drive_root_still_resolves_paths_inside_it()
    {
        string driveRoot = Path.GetPathRoot(_root)!;

        Assert.Equal(Path.Combine(driveRoot, "05-masters"), ProjectLocations.Resolve(driveRoot, ProjectFolder.Masters));
        Assert.Equal(
            Path.Combine(driveRoot, "04-generated-audio", "take.wav"),
            ProjectLocations.ResolveInsideProject(driveRoot, "04-generated-audio/take.wav"));
    }

    [Fact]
    public void Forward_slash_relative_paths_resolve_to_native_paths()
    {
        string resolved = ProjectLocations.ResolveInsideProject(_root, "04-generated-audio/segments/a/take.wav");
        Assert.Equal(Path.Combine(_root, "04-generated-audio", "segments", "a", "take.wav"), resolved);
    }

    [Fact]
    public void Segment_take_folder_rejects_path_tricks()
    {
        Assert.Equal("04-generated-audio/segments/abc", ProjectLocations.SegmentTakeFolderRelativePath("abc"));
        Assert.Throws<ArgumentException>(() => ProjectLocations.SegmentTakeFolderRelativePath("../abc"));
        Assert.Throws<ArgumentException>(() => ProjectLocations.SegmentTakeFolderRelativePath("a/b"));
    }

    [Fact]
    public void Take_audio_prefers_the_processed_file_then_the_raw_render()
    {
        RenderRecord raw = new() { RawAudioRelativePath = "04-generated-audio/segments/s/take-0001.raw.wav" };
        RenderRecord processed = raw with { ProcessedAudioRelativePath = "04-generated-audio/segments/s/take-0001.wav" };
        RenderRecord missing = new();

        Assert.Equal(raw.RawAudioRelativePath, ProjectLocations.TakeAudioRelativePath(raw));
        Assert.Equal(processed.ProcessedAudioRelativePath, ProjectLocations.TakeAudioRelativePath(processed));
        Assert.Null(ProjectLocations.TakeAudioRelativePath(missing));
        Assert.Null(ProjectLocations.TakeAudioRelativePath(processed with { AudioDeletedAt = DateTimeOffset.UnixEpoch }));
    }

    [Theory]
    [InlineData("Training-compilation-v3.master.wav", "Training-compilation", 3)]
    [InlineData("Mix-v001.master.mp3", "Mix", 1)]
    [InlineData("Untitled.master.wav", "Untitled", null)]
    [InlineData("Chapter-version-two.master.wav", "Chapter-version-two", null)]
    public void Master_file_names_split_into_name_and_version(string file, string name, int? version)
    {
        Assert.Equal((name, version), ProjectLocations.ParseMasterFileName(file));
    }

    [Fact]
    public void Masters_list_narration_masters_and_mix_exports_newest_first_and_skip_work_folders()
    {
        string masters = Path.Combine(_root, "05-masters");
        Directory.CreateDirectory(Path.Combine(masters, "Mix-v001"));
        Directory.CreateDirectory(Path.Combine(masters, ".Mix-v002-work-123"));
        string older = Path.Combine(masters, "Course-compilation-v1.master.wav");
        string newer = Path.Combine(masters, "Mix-v001", "Mix-v001.master.wav");
        string mp3 = Path.Combine(masters, "Mix-v001", "Mix-v001.master.mp3");
        File.WriteAllBytes(older, [1, 2, 3]);
        File.WriteAllBytes(newer, [1]);
        File.WriteAllBytes(mp3, [1]);
        File.WriteAllBytes(Path.Combine(masters, "Mix-v001", "Mix-v001.cues.csv"), [1]);
        File.WriteAllBytes(Path.Combine(masters, ".Mix-v002-work-123", "Mix-v002.master.wav"), [1]);
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(mp3, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        IReadOnlyList<BuiltMaster> list = ProjectLocations.ListMasters(_root);

        Assert.Equal(
            ["05-masters/Mix-v001/Mix-v001.master.mp3", "05-masters/Mix-v001/Mix-v001.master.wav", "05-masters/Course-compilation-v1.master.wav"],
            list.Select(item => item.RelativePath).ToArray());
        Assert.Equal("Course-compilation · v1", list[2].DisplayName);
        Assert.Equal(3, list[2].SizeBytes);
        Assert.True(list[0].IsMp3);
    }

    [Fact]
    public void Masters_list_is_empty_before_anything_is_built()
    {
        Assert.Empty(ProjectLocations.ListMasters(_root));
    }
}
