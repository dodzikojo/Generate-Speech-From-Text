using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ProjectVoiceSelectionRulesTests
{
    [Fact]
    public void Setting_project_voice_makes_every_segment_inherit_the_new_default()
    {
        VoiceBenchProjectDocument project = new()
        {
            DefaultVoicePresetId = "old-default",
            VoicePresets =
            [
                new VoicePreset { PresetId = "old-default" },
                new VoicePreset { PresetId = "new-default" },
            ],
            Segments =
            [
                new SegmentDocument { SegmentId = "inherited" },
                new SegmentDocument { SegmentId = "old-override", VoicePresetId = "old-default" },
                new SegmentDocument { SegmentId = "other-override", VoicePresetId = "another-preset" },
            ],
        };

        VoiceBenchProjectDocument updated = ProjectVoiceSelectionRules.SetProjectVoice(project, "new-default");

        Assert.Equal("new-default", updated.DefaultVoicePresetId);
        Assert.All(updated.Segments, segment => Assert.Null(segment.VoicePresetId));
        Assert.All(project.Segments.Skip(1), segment => Assert.NotNull(segment.VoicePresetId));
    }

    [Fact]
    public void Setting_project_voice_rejects_an_unknown_preset()
    {
        VoiceBenchProjectDocument project = new()
        {
            VoicePresets = [new VoicePreset { PresetId = "known" }],
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProjectVoiceSelectionRules.SetProjectVoice(project, "missing"));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }
}
