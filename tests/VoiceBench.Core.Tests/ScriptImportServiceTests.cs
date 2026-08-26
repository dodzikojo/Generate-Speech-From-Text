using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ScriptImportServiceTests
{
    private readonly ScriptImportService service = new();

    [Fact]
    public void Plain_text_becomes_one_segment_without_trimming_content()
    {
        var segments = service.Split("lesson.txt", "Opening line.\n\nClosing line.\n");

        var segment = Assert.Single(segments);
        Assert.Equal(1, segment.Ordinal);
        Assert.Equal("lesson", segment.Title);
        Assert.Equal("Opening line.\n\nClosing line.\n", segment.Content);
    }

    [Fact]
    public void Level_two_headings_create_named_segments()
    {
        var segments = service.Split(
            "lesson.md",
            "## Introduction\r\nWelcome.\r\n\r\n## Main idea ##\r\nDetails.");

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal("Introduction", first.Title);
                Assert.Equal("Welcome.", first.Content);
            },
            second =>
            {
                Assert.Equal("Main idea", second.Title);
                Assert.Equal("Details.", second.Content);
            });
    }

    [Fact]
    public void Isolated_markers_split_but_inline_rules_do_not()
    {
        var segments = service.Split(
            "lesson.txt",
            "First --- still first\n---\nSecond section");

        Assert.Collection(
            segments,
            first => Assert.Equal("First --- still first", first.Content),
            second => Assert.Equal("Second section", second.Content));
    }

    [Fact]
    public void Level_three_headings_do_not_split()
    {
        var segments = service.Split("lesson.md", "### Detail\nBody");

        Assert.Equal("### Detail\nBody", Assert.Single(segments).Content);
    }

    [Fact]
    public void Splitting_is_deterministic_across_windows_and_unix_line_endings()
    {
        var windows = service.Split("lesson.md", "## One\r\nA\r\n---\r\nB");
        var unix = service.Split("lesson.md", "## One\nA\n---\nB");

        Assert.Equal(unix, windows);
    }

    [Theory]
    [InlineData(1, "Introduction", "A1B2C3D4", "001-introduction--a1b2c3.md")]
    [InlineData(12, "Café & setup", "12345678", "012-cafe-setup--123456.md")]
    public void Stable_file_name_uses_ordinal_slug_and_short_id(
        int ordinal,
        string title,
        string stableId,
        string expected)
    {
        Assert.Equal(expected, ScriptSegmentNaming.CreateFileName(ordinal, title, stableId));
    }
}
