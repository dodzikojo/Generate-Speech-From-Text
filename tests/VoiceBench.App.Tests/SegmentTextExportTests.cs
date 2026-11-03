using IssueLab.VoiceBench.App.Services;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class SegmentTextExportTests
{
    private const string Script = "@visual: Show the dashboard.\n\nThis matrix brings every clash into one view.\n\n@action: Select the Level 02 filter.\n";

    [Fact]
    public void Script_export_keeps_production_cues_exactly()
    {
        Assert.Equal(Script, SegmentTextExport.Text(Script, SegmentTextKind.Script));
    }

    [Fact]
    public void Narration_export_keeps_only_the_spoken_words()
    {
        Assert.Equal("This matrix brings every clash into one view.", SegmentTextExport.Text(Script, SegmentTextKind.Narration));
    }

    [Fact]
    public void Exported_text_uses_windows_line_endings_without_doubling_existing_ones()
    {
        Assert.Equal("One\r\nTwo\r\n\r\nThree", SegmentTextExport.WithWindowsLineEndings("One\nTwo\r\n\nThree"));
    }

    [Theory]
    [InlineData("Intro: why <clashes> matter?", SegmentTextKind.Script, ".md", "Intro- why -clashes- matter-.md")]
    [InlineData("Opening", SegmentTextKind.Narration, ".md", "Opening - narration.txt")]
    [InlineData("Opening", SegmentTextKind.Script, ".txt", "Opening.txt")]
    [InlineData("   ", SegmentTextKind.Script, ".md", "segment.md")]
    public void Suggested_file_names_are_safe_and_say_what_they_hold(string title, SegmentTextKind kind, string scriptExtension, string expected)
    {
        Assert.Equal(expected, SegmentTextExport.SuggestedFileName(title, kind, scriptExtension));
    }

    [Theory]
    [InlineData("This matrix brings every clash into one view.", 8)]
    [InlineData("  ", 0)]
    [InlineData("One\ntwo\tthree", 3)]
    public void Word_count_ignores_whitespace(string text, int expected)
    {
        Assert.Equal(expected, SegmentTextExport.CountWords(text));
    }
}
