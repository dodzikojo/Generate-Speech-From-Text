namespace IssueLab.VoiceBench.Core;

public static class ProjectVoiceSelectionRules
{
    public static VoiceBenchProjectDocument SetProjectVoice(
        VoiceBenchProjectDocument project,
        string presetId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);

        if (!project.VoicePresets.Any(preset =>
                preset.PresetId.Equals(presetId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Voice preset '{presetId}' does not exist in this project.");
        }

        return project with
        {
            DefaultVoicePresetId = presetId,
            Segments = project.Segments
                .Select(segment => segment with { VoicePresetId = null })
                .ToList(),
        };
    }
}
