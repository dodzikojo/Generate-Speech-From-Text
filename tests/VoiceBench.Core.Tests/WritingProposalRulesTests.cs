using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class WritingProposalRulesTests
{
    [Fact]
    public void Cue_action_preserves_narration_but_may_add_guidance()
    {
        WritingProposalRules.Validate(
            "Keep this narration.",
            "@visual: Show the dashboard.\n\nKeep this narration.",
            WritingAction.GenerateProductionCues);
    }

    [Fact]
    public void Cue_action_rejects_narration_changes()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => WritingProposalRules.Validate(
            "Keep this narration.",
            "Change this narration.\n@visual: Show the dashboard.",
            WritingAction.RefineProductionCues));

        Assert.Contains("preserve narration", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Narration_action_rejects_cue_changes()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => WritingProposalRules.Validate(
            "@note: Keep this cue.\nOriginal narration.",
            "@note: Changed cue.\nClearer narration.",
            WritingAction.ImproveClarity));

        Assert.Contains("preserve production cues", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
