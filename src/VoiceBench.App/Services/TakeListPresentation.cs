using System.Globalization;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>How a take relates to the segment's current approved source.</summary>
public enum TakeTone
{
    Neutral,
    Current,
    Approved,
    Outdated,
    Failed,
}

/// <summary>One row of the Workbench take list.</summary>
public sealed record TakeRow(
    Guid RenderId,
    int TakeNumber,
    string Title,
    string VoiceLine,
    string DurationText,
    string CreatedText,
    string StatusText,
    TakeTone Tone,
    bool IsApprovedTake,
    bool CanApprove,
    string? AudioRelativePath)
{
    public string AutomationName => $"{Title}, {StatusText}, {DurationText}, {VoiceLine}";
}

/// <summary>Builds take-list rows (newest first) without touching UI types so it can be unit tested.</summary>
public static class TakeListPresentation
{
    public static IReadOnlyList<TakeRow> Build(SegmentDocument segment, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(segment);
        culture ??= CultureInfo.CurrentCulture;
        return segment.RenderHistory
            .OrderByDescending(render => render.TakeNumber)
            .Select(render => BuildRow(segment, render, culture))
            .ToList();
    }

    public static TakeRow BuildRow(SegmentDocument segment, RenderRecord render, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(render);
        culture ??= CultureInfo.CurrentCulture;
        bool isApprovedTake = segment.ApprovedRenderId == render.RenderId && render.Status == RenderStatus.Approved;
        bool matchesSource = string.IsNullOrEmpty(segment.SourceSha256)
            || string.Equals(render.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase);
        (string status, TakeTone tone) = render.Status switch
        {
            RenderStatus.Failed => ("Failed", TakeTone.Failed),
            RenderStatus.Cancelled => ("Cancelled", TakeTone.Failed),
            RenderStatus.Stale => ("Script changed", TakeTone.Outdated),
            RenderStatus.Approved when isApprovedTake && matchesSource => ("Approved", TakeTone.Approved),
            RenderStatus.Approved when isApprovedTake => ("Approved · script changed", TakeTone.Outdated),
            RenderStatus.Approved => ("Previously approved", TakeTone.Neutral),
            _ when !matchesSource => ("Script changed", TakeTone.Outdated),
            _ when render.Origin == RenderOrigin.Imported => ("Imported · awaiting review", TakeTone.Current),
            _ => ("Awaiting review", TakeTone.Current),
        };

        string voice = render.Origin == RenderOrigin.Imported
            ? "Imported recording"
            : string.Join(" · ", new[] { render.Voice, ProviderName(render.ProviderId), render.ModelId }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        return new TakeRow(
            render.RenderId,
            render.TakeNumber,
            $"Take {render.TakeNumber:00}",
            voice.Length == 0 ? "Unknown voice" : voice,
            FormatDuration(render.Duration),
            render.CreatedAt == default
                ? string.Empty
                : render.CreatedAt.ToLocalTime().ToString("MMM d · HH:mm", culture),
            status,
            tone,
            isApprovedTake,
            render.Status == RenderStatus.Generated,
            ProjectLocations.TakeAudioRelativePath(render));
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "--:--";
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:0}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
    }

    private static string ProviderName(string providerId) => providerId.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "gemini" => "Gemini",
        "" => string.Empty,
        _ => providerId,
    };
}
