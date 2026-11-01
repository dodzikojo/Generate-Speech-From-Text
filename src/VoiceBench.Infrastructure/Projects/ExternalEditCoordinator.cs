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
        string recordedAuthoringHash = string.IsNullOrWhiteSpace(segment.AuthoringSha256)
            ? segment.SourceSha256
            : segment.AuthoringSha256;
        if (string.Equals(diskHash, recordedAuthoringHash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return editorIsDirty
            ? new ExternalEditConflict(
                segment.SegmentId,
                diskText,
                editorText,
                recordedAuthoringHash,
                diskHash)
            : null;
    }
}
