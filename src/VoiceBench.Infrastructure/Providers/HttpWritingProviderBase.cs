using System.Net;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

public abstract class HttpWritingProviderBase : IWritingProvider
{
    private const int MaximumRetries = 3;
    private readonly HttpClient _httpClient;
    private readonly IRetryDelayStrategy _retryDelay;

    protected HttpWritingProviderBase(HttpClient httpClient, IRetryDelayStrategy? retryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _retryDelay = retryDelay ?? new JitterRetryDelayStrategy();
    }

    public abstract WritingProviderDescriptor Descriptor { get; }

    public Task<ChapterOutline> CreateOutlineAsync(
        WritingOutlineRequest request,
        string apiKey,
        CancellationToken cancellationToken) =>
        SendStructuredAsync<ChapterOutline>(
            request.ModelId,
            request.EstimatedInputTokens,
            BuildOutlinePrompt(request),
            "chapter_outline",
            WritingJsonSchemas.Outline,
            ValidateOutline,
            apiKey,
            cancellationToken);

    public Task<ChapterDraft> CreateDraftAsync(
        WritingDraftRequest request,
        string apiKey,
        CancellationToken cancellationToken) =>
        SendStructuredAsync<ChapterDraft>(
            request.ModelId,
            request.EstimatedInputTokens,
            BuildDraftPrompt(request),
            "chapter_draft",
            WritingJsonSchemas.Draft,
            draft => ValidateDraft(request, draft),
            apiKey,
            cancellationToken);

    public Task<WritingProposal> ReviseAsync(
        WritingRevisionRequest request,
        string apiKey,
        CancellationToken cancellationToken) =>
        SendStructuredAsync<WritingProposal>(
            request.ModelId,
            request.EstimatedInputTokens,
            BuildRevisionPrompt(request),
            "writing_proposal",
            WritingJsonSchemas.Proposal,
            proposal => ValidateProposal(request, proposal),
            apiKey,
            cancellationToken);

    protected abstract HttpRequestMessage CreateRequest(
        string modelId,
        string prompt,
        string schemaName,
        JsonElement schema,
        string apiKey);

    protected abstract Task<string> ReadOutputTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken);

    protected WritingProviderException Malformed(string detail, Exception? innerException = null) => new(
        Descriptor.ProviderId,
        WritingProviderFailureKind.MalformedResponse,
        $"{Descriptor.DisplayName} returned malformed structured writing output: {detail}",
        innerException: innerException);

    protected WritingProviderException PolicyRejected() => new(
        Descriptor.ProviderId,
        WritingProviderFailureKind.Policy,
        $"{Descriptor.DisplayName} rejected the writing request under its content policy.");

    private async Task<T> SendStructuredAsync<T>(
        string modelId,
        int estimatedInputTokens,
        string prompt,
        string schemaName,
        JsonElement schema,
        Func<T, T> validate,
        string apiKey,
        CancellationToken cancellationToken)
    {
        Validate(modelId, estimatedInputTokens, prompt, apiKey);
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpRequestMessage message = CreateRequest(modelId, prompt, schemaName, schema, apiKey);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string output = await ReadOutputTextAsync(response, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        T? value = JsonSerializer.Deserialize<T>(output, JsonOptions);
                        return value is null ? throw Malformed("the JSON payload was empty") : validate(value);
                    }
                    catch (WritingProviderException)
                    {
                        throw;
                    }
                    catch (JsonException exception)
                    {
                        throw Malformed("the JSON payload did not match the required schema", exception);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw Malformed(exception.Message, exception);
                    }
                }

                if (IsRetryable(response.StatusCode) && attempt < MaximumRetries)
                {
                    await _retryDelay.DelayAsync(response.Headers.RetryAfter, attempt + 1, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaximumRetries)
            {
                await _retryDelay.DelayAsync(null, attempt + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaximumRetries)
            {
                await _retryDelay.DelayAsync(null, attempt + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new WritingProviderException(
                    Descriptor.ProviderId,
                    WritingProviderFailureKind.Transient,
                    $"{Descriptor.DisplayName} could not be reached after bounded retries.",
                    innerException: exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WritingProviderException(
                    Descriptor.ProviderId,
                    WritingProviderFailureKind.Transient,
                    $"{Descriptor.DisplayName} timed out after bounded retries.");
            }
        }
    }

    private void Validate(string modelId, int estimatedInputTokens, string prompt, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new WritingProviderException(
                Descriptor.ProviderId,
                WritingProviderFailureKind.Credentials,
                $"No {Descriptor.DisplayName} API key is configured.");
        }

        WritingModelDescriptor? model = Descriptor.Models.SingleOrDefault(
            item => string.Equals(item.ModelId, modelId, StringComparison.Ordinal));
        if (model is null)
        {
            throw new WritingProviderException(
                Descriptor.ProviderId,
                WritingProviderFailureKind.Validation,
                $"Model '{modelId}' is not selectable for {Descriptor.DisplayName} writing.");
        }

        if (string.IsNullOrWhiteSpace(prompt) || estimatedInputTokens < 1 || estimatedInputTokens > model.MaxInputTokens)
        {
            throw new WritingProviderException(
                Descriptor.ProviderId,
                WritingProviderFailureKind.Validation,
                $"Writing input is empty or exceeds the {model.MaxInputTokens:N0}-token limit for '{modelId}'.");
        }
    }

    private async Task<WritingProviderException> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        WritingProviderFailureKind kind = ContainsPolicyMarker(body)
            ? WritingProviderFailureKind.Policy
            : IsAuthenticationFailure(response.StatusCode, body)
                ? WritingProviderFailureKind.Authentication
                : response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => WritingProviderFailureKind.RateLimited,
                HttpStatusCode.RequestTimeout => WritingProviderFailureKind.Transient,
                >= HttpStatusCode.InternalServerError => WritingProviderFailureKind.Transient,
                _ => WritingProviderFailureKind.Validation,
            };
        string message = kind switch
        {
            WritingProviderFailureKind.Authentication => $"{Descriptor.DisplayName} rejected the configured credential.",
            WritingProviderFailureKind.Policy => $"{Descriptor.DisplayName} rejected the writing request under its content policy.",
            WritingProviderFailureKind.RateLimited => $"{Descriptor.DisplayName} rate-limited the writing request after bounded retries.",
            WritingProviderFailureKind.Transient => $"{Descriptor.DisplayName} failed after bounded retries.",
            _ => $"{Descriptor.DisplayName} rejected the writing request as invalid.",
        };
        return new WritingProviderException(Descriptor.ProviderId, kind, message, response.StatusCode);
    }

    protected virtual bool IsAuthenticationFailure(HttpStatusCode statusCode, string responseBody) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || statusCode >= HttpStatusCode.InternalServerError;

    private static bool ContainsPolicyMarker(string value) =>
        value.Contains("policy", StringComparison.OrdinalIgnoreCase)
        || value.Contains("safety", StringComparison.OrdinalIgnoreCase)
        || value.Contains("refusal", StringComparison.OrdinalIgnoreCase)
        || value.Contains("prohibited", StringComparison.OrdinalIgnoreCase);

    private static ChapterOutline ValidateOutline(ChapterOutline outline)
    {
        if (string.IsNullOrWhiteSpace(outline.Title) || outline.Segments.Count == 0 ||
            outline.Segments.Any(segment => string.IsNullOrWhiteSpace(segment.Title) || segment.TargetSeconds < 1))
        {
            throw new InvalidDataException("The outline must contain a title and at least one timed segment.");
        }

        return outline;
    }

    private static ChapterDraft ValidateDraft(WritingDraftRequest request, ChapterDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Segments.Count == 0 ||
            draft.Segments.Any(segment => string.IsNullOrWhiteSpace(segment.Title) || string.IsNullOrWhiteSpace(segment.Text)))
        {
            throw new InvalidDataException("The draft must contain a title and at least one non-empty segment.");
        }

        ProductionScriptParser parser = new();
        foreach (ChapterDraftSegment segment in draft.Segments)
        {
            ParsedScriptDocument parsed = parser.Parse(segment.Text);
            if (parsed.HasErrors || string.IsNullOrWhiteSpace(parsed.NarrationText))
            {
                throw new InvalidDataException($"Draft segment '{segment.Title}' has invalid production cues or no narration.");
            }
            if (!request.GenerateProductionCues && parsed.Cues.Count > 0)
            {
                throw new InvalidDataException("The draft added production cues even though cue generation was disabled.");
            }
        }

        return draft;
    }

    private static WritingProposal ValidateProposal(WritingRevisionRequest request, WritingProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.ProposedText))
        {
            throw new InvalidDataException("The proposal text is empty.");
        }

        WritingProposalRules.Validate(request.Text, proposal.ProposedText, request.Action);

        return proposal;
    }

    private static string BuildOutlinePrompt(WritingOutlineRequest request)
    {
        StringBuilder builder = BeginPrompt(
            "Create a narration chapter outline. Return only the requested structured result.",
            request.Context);
        builder.AppendLine($"Chapter title: {request.ChapterTitle}");
        builder.AppendLine($"Chapter goal: {request.Goal}");
        builder.AppendLine($"Desired duration: {request.TargetDurationSeconds} seconds");
        builder.AppendLine("Divide the chapter into natural, render-sized segments and estimate each duration.");
        return builder.ToString();
    }

    private static string BuildDraftPrompt(WritingDraftRequest request)
    {
        StringBuilder builder = BeginPrompt(
            "Write a complete narration chapter from the approved outline. Return only the requested structured result.",
            request.Context);
        builder.AppendLine("Approved outline:");
        builder.AppendLine(JsonSerializer.Serialize(request.Outline, JsonOptions));
        builder.AppendLine("Keep provider-neutral source text clean: do not add TTS directions, SSML, or pronunciation substitutions.");
        builder.AppendLine(request.GenerateProductionCues
            ? "Add concise production guidance where useful using standalone @visual:, @action:, @screen:, or @note: lines. Cue lines are not narration. Use no custom cue types unless the brief explicitly names them."
            : "Do not add production cue lines. Return narration only because production-cue generation is disabled.");
        return builder.ToString();
    }

    private static string BuildRevisionPrompt(WritingRevisionRequest request)
    {
        string action = request.Action switch
        {
            WritingAction.ImproveClarity => "Improve clarity without changing meaning.",
            WritingAction.MakeConversational => "Make the narration more conversational.",
            WritingAction.Shorten => "Shorten the narration while preserving essential meaning.",
            WritingAction.Expand => "Expand the narration with useful connective explanation.",
            WritingAction.FixGrammar => "Correct grammar and mechanics without unnecessary rewriting.",
            WritingAction.Critique => "Critique the narration. Preserve the original in proposedText and put advice in summary and warnings.",
            WritingAction.GenerateProductionCues => "Add concise production cues using standalone @visual:, @action:, @screen:, or @note: lines. Preserve every narration character exactly.",
            WritingAction.RefineProductionCues => "Improve the existing production cues for clarity and usefulness. Preserve every narration character exactly.",
            WritingAction.Custom => request.CustomInstruction ?? "Apply the requested revision.",
            _ => throw new ArgumentOutOfRangeException(nameof(request.Action)),
        };
        StringBuilder builder = BeginPrompt(
            "Revise a narration selection or segment. Return only the requested structured result.",
            request.Context);
        builder.AppendLine($"Instruction: {action}");
        if (request.Action is not WritingAction.GenerateProductionCues and not WritingAction.RefineProductionCues and not WritingAction.Custom)
        {
            builder.AppendLine("Preserve every existing production cue line verbatim while revising narration.");
        }
        builder.AppendLine("Text to review:");
        builder.AppendLine(request.Text);
        return builder.ToString();
    }

    private static StringBuilder BeginPrompt(string task, WritingContext context)
    {
        StringBuilder builder = new();
        builder.AppendLine(task);
        builder.AppendLine($"Purpose: {context.Profile.Purpose}");
        builder.AppendLine($"Audience: {context.Profile.Audience}");
        builder.AppendLine($"Tone: {context.Profile.Tone}");
        builder.AppendLine($"Style notes: {context.Profile.StyleNotes}");
        builder.AppendLine($"Chapter brief: {context.ChapterBrief}");
        if (context.References.Count > 0)
        {
            builder.AppendLine("Explicitly selected local references:");
            foreach (WritingReference reference in context.References)
            {
                builder.AppendLine($"--- {reference.Name} [{reference.Sha256}] ---");
                builder.AppendLine(reference.Text);
            }
        }

        return builder;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
