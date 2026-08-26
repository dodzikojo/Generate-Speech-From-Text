using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class ExternalEditCoordinatorTests
{
    [Fact]
    public void Detect_ReturnsConflictWhenEditorAndDiskBothChanged()
    {
        SegmentDocument segment = new()
        {
            SegmentId = "segment-1",
            SourceSha256 = SourceTextHasher.Compute("Original"),
        };

        ExternalEditConflict? conflict = ExternalEditCoordinator.Detect(
            segment,
            "Unsaved editor change",
            editorIsDirty: true,
            "External disk change");

        Assert.NotNull(conflict);
        Assert.Equal("Unsaved editor change", conflict.EditorText);
        Assert.Equal("External disk change", conflict.DiskText);
    }

    [Fact]
    public void Detect_ReturnsNoConflictWhenCleanEditorCanReloadAutomatically()
    {
        SegmentDocument segment = new()
        {
            SegmentId = "segment-1",
            SourceSha256 = SourceTextHasher.Compute("Original"),
        };

        Assert.Null(ExternalEditCoordinator.Detect(segment, "Original", false, "External disk change"));
    }
}
