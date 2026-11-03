using System.Globalization;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class TakeListPresentationTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public void Rows_are_newest_first_and_describe_voice_duration_and_review_state()
    {
        SegmentDocument segment = new()
        {
            SourceSha256 = "current",
            ApprovedRenderId = First,
            RenderHistory =
            [
                Render(First, 1, RenderStatus.Approved, "current") with { ProcessedAudioRelativePath = "04-generated-audio/segments/s/take-0001.wav" },
                Render(Second, 2, RenderStatus.Generated, "current"),
                Render(Third, 3, RenderStatus.Generated, "older-script"),
            ],
        };

        IReadOnlyList<TakeRow> rows = TakeListPresentation.Build(segment, CultureInfo.InvariantCulture);

        Assert.Equal([3, 2, 1], rows.Select(row => row.TakeNumber).ToArray());
        Assert.Equal(("Script changed", TakeTone.Outdated), (rows[0].StatusText, rows[0].Tone));
        Assert.Equal(("Awaiting review", TakeTone.Current), (rows[1].StatusText, rows[1].Tone));
        Assert.True(rows[1].CanApprove);
        Assert.Equal(("Approved", TakeTone.Approved), (rows[2].StatusText, rows[2].Tone));
        Assert.True(rows[2].IsApprovedTake);
        Assert.False(rows[2].CanApprove);
        Assert.Equal("04-generated-audio/segments/s/take-0001.wav", rows[2].AudioRelativePath);
        Assert.Equal("04-generated-audio/segments/s/take-0002.raw.wav", rows[1].AudioRelativePath);
        Assert.Equal("Take 02", rows[1].Title);
        Assert.Equal("Achernar · Gemini · gemini-3.1-flash-tts", rows[1].VoiceLine);
        Assert.Equal("00:28", rows[1].DurationText);
        Assert.Contains("Take 02", rows[1].AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void Approved_take_whose_script_changed_is_flagged_and_older_approvals_are_neutral()
    {
        SegmentDocument segment = new()
        {
            SourceSha256 = "edited",
            ApprovedRenderId = Second,
            RenderHistory = [Render(First, 1, RenderStatus.Approved, "edited"), Render(Second, 2, RenderStatus.Approved, "original")],
        };

        IReadOnlyList<TakeRow> rows = TakeListPresentation.Build(segment, CultureInfo.InvariantCulture);

        Assert.Equal(("Approved · script changed", TakeTone.Outdated), (rows[0].StatusText, rows[0].Tone));
        Assert.Equal(("Previously approved", TakeTone.Neutral), (rows[1].StatusText, rows[1].Tone));
    }

    [Fact]
    public void Failed_and_imported_takes_get_plain_labels()
    {
        SegmentDocument segment = new()
        {
            SourceSha256 = "current",
            RenderHistory =
            [
                Render(First, 1, RenderStatus.Failed, "current"),
                Render(Second, 2, RenderStatus.Generated, "current") with { Origin = RenderOrigin.Imported },
            ],
        };

        IReadOnlyList<TakeRow> rows = TakeListPresentation.Build(segment, CultureInfo.InvariantCulture);

        Assert.Equal(("Imported · awaiting review", TakeTone.Current), (rows[0].StatusText, rows[0].Tone));
        Assert.Equal("Imported recording", rows[0].VoiceLine);
        Assert.Equal(("Failed", TakeTone.Failed), (rows[1].StatusText, rows[1].Tone));
        Assert.False(rows[1].CanApprove);
    }

    [Theory]
    [InlineData(0, "--:--")]
    [InlineData(28.4, "00:28")]
    [InlineData(125, "02:05")]
    [InlineData(3725, "1:02:05")]
    public void Durations_are_compact(double seconds, string expected)
    {
        Assert.Equal(expected, TakeListPresentation.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    private static RenderRecord Render(Guid id, int take, RenderStatus status, string source) => new()
    {
        RenderId = id,
        TakeNumber = take,
        Status = status,
        SourceSha256 = source,
        ProviderId = "gemini",
        ModelId = "gemini-3.1-flash-tts",
        Voice = "Achernar",
        Duration = TimeSpan.FromSeconds(28.2),
        CreatedAt = new DateTimeOffset(2026, 11, 3, 15, 11, 0, TimeSpan.Zero),
        RawAudioRelativePath = $"04-generated-audio/segments/s/take-{take:0000}.raw.wav",
    };
}
