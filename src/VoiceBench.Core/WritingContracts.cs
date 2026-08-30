using System.Net;

namespace IssueLab.VoiceBench.Core;

public sealed record WritingModelDescriptor(
    string ModelId,
    string DisplayName,
    int MaxInputTokens);

public sealed record WritingProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<WritingModelDescriptor> Models);

public sealed record WritingReference(string Name, string Text, string Sha256);

public sealed record ChapterOutlineSegment(
    string Title,
    string Purpose,
    int TargetSeconds);

public sealed record ChapterOutline(
    string Title,
    string Summary,
    IReadOnlyList<ChapterOutlineSegment> Segments);

public sealed record ChapterDraftSegment(string Title, string Text);

public sealed record ChapterDraft(
    string Title,
    IReadOnlyList<ChapterDraftSegment> Segments,
    string Summary);

public sealed record WritingProposal(
    string ProposedText,
    string Summary,
    IReadOnlyList<string> Warnings);

public sealed record WritingContext(
    WritingProfile Profile,
    string ChapterBrief,
    IReadOnlyList<WritingReference> References);

public sealed record WritingOutlineRequest(
    string ModelId,
    string ChapterTitle,
    string Goal,
    int TargetDurationSeconds,
    WritingContext Context,
    int EstimatedInputTokens);

public sealed record WritingDraftRequest(
    string ModelId,
    ChapterOutline Outline,
    WritingContext Context,
    int EstimatedInputTokens,
    bool GenerateProductionCues = false);

public sealed record WritingRevisionRequest(
    string ModelId,
    WritingAction Action,
    string Text,
    string? CustomInstruction,
    WritingContext Context,
    int EstimatedInputTokens);

public enum WritingAction
{
    ImproveClarity,
    MakeConversational,
    Shorten,
    Expand,
    FixGrammar,
    Critique,
    GenerateProductionCues,
    RefineProductionCues,
    Custom,
}

public static class WritingProposalRules
{
    public static void Validate(string originalText, string proposedText, WritingAction action)
    {
        ProductionScriptParser parser = new();
        ParsedScriptDocument original = parser.Parse(originalText);
        ParsedScriptDocument proposed = parser.Parse(proposedText);
        if (proposed.HasErrors)
        {
            throw new InvalidDataException(string.Join(" ", proposed.Diagnostics.Select(item => item.Message)));
        }

        if (action is WritingAction.GenerateProductionCues or WritingAction.RefineProductionCues)
        {
            if (!string.Equals(original.NarrationText, proposed.NarrationText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Production-cue proposals must preserve narration exactly.");
            }
            return;
        }

        if (action is WritingAction.ImproveClarity or WritingAction.MakeConversational or WritingAction.Shorten
            or WritingAction.Expand or WritingAction.FixGrammar or WritingAction.Critique)
        {
            string[] originalCues = original.Cues.Select(item => $"{item.Type}\u001f{item.Content}").ToArray();
            string[] proposedCues = proposed.Cues.Select(item => $"{item.Type}\u001f{item.Content}").ToArray();
            if (!originalCues.SequenceEqual(proposedCues, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Narration proposals must preserve production cues verbatim.");
            }
        }
    }
}

public enum WritingProviderFailureKind
{
    Credentials,
    Authentication,
    Validation,
    Policy,
    RateLimited,
    Transient,
    MalformedResponse,
}

public sealed class WritingProviderException : Exception
{
    public WritingProviderException(
        string providerId,
        WritingProviderFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderId = providerId;
        Kind = kind;
        StatusCode = statusCode;
    }

    public string ProviderId { get; }

    public WritingProviderFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }
}

public interface IWritingProvider
{
    WritingProviderDescriptor Descriptor { get; }

    Task<ChapterOutline> CreateOutlineAsync(
        WritingOutlineRequest request,
        string apiKey,
        CancellationToken cancellationToken);

    Task<ChapterDraft> CreateDraftAsync(
        WritingDraftRequest request,
        string apiKey,
        CancellationToken cancellationToken);

    Task<WritingProposal> ReviseAsync(
        WritingRevisionRequest request,
        string apiKey,
        CancellationToken cancellationToken);
}

public interface IWritingAssistantService
{
    IReadOnlyCollection<WritingProviderDescriptor> Providers { get; }

    Task<ChapterOutline> CreateOutlineAsync(
        string providerId,
        WritingOutlineRequest request,
        CancellationToken cancellationToken = default);

    Task<ChapterDraft> CreateDraftAsync(
        string providerId,
        WritingDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<WritingProposal> ReviseAsync(
        string providerId,
        WritingRevisionRequest request,
        CancellationToken cancellationToken = default);
}
