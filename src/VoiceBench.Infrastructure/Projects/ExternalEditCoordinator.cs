using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public enum ExternalEditChoice
{
    Reload,
    KeepCurrent,
    Compare,
}

public sealed record ExternalEditConflict(
    string SegmentId,
    string DiskText,
    string EditorText,
    string RecordedSourceHash,
    string DiskSourceHash);

public static class ExternalEditCoordinator
{
    public static ExternalEditConflict? Detect(
        SegmentDocument segment,
        string editorText,
        bool editorIsDirty,
        string diskText)
    {
        string diskHash = SourceTextHasher.Compute(diskText);
        if (string.Equals(diskHash, segment.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return editorIsDirty
            ? new ExternalEditConflict(
                segment.SegmentId,
                diskText,
                editorText,
                segment.SourceSha256,
                diskHash)
            : null;
    }
}
