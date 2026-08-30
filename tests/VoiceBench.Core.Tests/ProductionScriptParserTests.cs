using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ProductionScriptParserTests
{
    private readonly ProductionScriptParser parser = new();

    [Fact]
    public void Parse_projects_clean_narration_and_ordered_built_in_cues()
    {
        ParsedScriptDocument parsed = parser.Parse(
            """
            @visual: Show the dashboard.

            This matrix brings every clash into one view.

            @action: Select the Level 02 filter.
            @screen: Filter clashes by level
            """);

        Assert.Equal("This matrix brings every clash into one view.", parsed.NarrationText);
        Assert.Equal(8, parsed.NarrationWordCount);
        Assert.Collection(
            parsed.Cues,
            cue =>
            {
                Assert.Equal(ProductionCueKind.Visual, cue.Kind);
                Assert.Equal("visual", cue.Type);
                Assert.Equal(0, cue.PrecedingNarrationWords);
                Assert.Equal(1, cue.SourceLine);
            },
            cue =>
            {
                Assert.Equal(ProductionCueKind.Action, cue.Kind);
                Assert.Equal(8, cue.PrecedingNarrationWords);
            },
            cue =>
            {
                Assert.Equal(ProductionCueKind.Screen, cue.Kind);
                Assert.Equal(8, cue.PrecedingNarrationWords);
            });
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void Parse_accepts_custom_kebab_case_and_preserves_colons_in_content()
    {
        ParsedScriptDocument parsed = parser.Parse(
            "Narration first.\n@camera-note: Crop 16:9 and hide the project name.");

        ProductionCue cue = Assert.Single(parsed.Cues);
        Assert.Equal(ProductionCueKind.Custom, cue.Kind);
        Assert.Equal("camera-note", cue.Type);
        Assert.Equal("Crop 16:9 and hide the project name.", cue.Content);
        Assert.Equal(2, cue.PrecedingNarrationWords);
    }

    [Fact]
    public void Parse_unescapes_a_literal_cue_like_narration_line()
    {
        ParsedScriptDocument parsed = parser.Parse("\\@visual: Say this exact line aloud.");

        Assert.Equal("@visual: Say this exact line aloud.", parsed.NarrationText);
        Assert.Empty(parsed.Cues);
        Assert.Empty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData("@Visual: Wrong case", "cue.type-invalid")]
    [InlineData("@camera_note: Wrong separator", "cue.type-invalid")]
    [InlineData("@note:", "cue.content-empty")]
    [InlineData("@visual Show the dashboard", "cue.syntax-invalid")]
    [InlineData("@camera-note Crop the recording", "cue.syntax-invalid")]
    [InlineData("@CameraNote Crop the recording", "cue.type-invalid")]
    public void Parse_reports_malformed_cues_without_leaking_them_into_narration(string source, string code)
    {
        ParsedScriptDocument parsed = parser.Parse(source);

        ScriptDocumentDiagnostic diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(1, diagnostic.SourceLine);
        Assert.Empty(parsed.NarrationText);
        Assert.True(parsed.HasErrors);
    }

    [Fact]
    public void Parse_keeps_authoring_text_and_normalizes_blank_lines_in_narration_projection()
    {
        const string source = "\r\n@note: Prepare the browser.\r\n\r\nFirst paragraph.\r\n\r\n\r\n@visual: Zoom in.\r\n\r\nSecond paragraph.\r\n";

        ParsedScriptDocument parsed = parser.Parse(source);

        Assert.Equal(source, parsed.AuthoringText);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", parsed.NarrationText);
        Assert.Equal(2, parsed.Cues.Count);
    }
}
