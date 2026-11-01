using System.Globalization;
using System.Net;
using System.Text;

namespace IssueLab.VoiceBench.Core;

public enum WordAlignmentStatus
{
    Pending,
    Aligned,
    Partial,
    Failed,
    Interrupted,
    Unsupported,
}

public sealed record NarrationWord(
    int Ordinal,
    string Text,
    string Normalized,
    int NarrationStart,
    int NarrationLength,
    int AuthoringStart,
    int AuthoringLength);

public sealed record SubmittedWordMapping(
    int SubmittedOrdinal,
    string Text,
    string Normalized,
    int SubmittedStart,
    int SubmittedLength,
    IReadOnlyList<int> NarrationWordOrdinals);

public sealed record PreparedSpeechText(
    string Text,
    IReadOnlyList<SubmittedWordMapping> Words);

public sealed record SubmittedTextMapDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid RenderId { get; init; }

    public string SourceSha256 { get; init; } = string.Empty;

    public string SubmittedTextSha256 { get; init; } = string.Empty;

    public string SubmittedText { get; init; } = string.Empty;

    public int TotalNarrationWordCount { get; init; }

    public List<SubmittedWordMapping> Words { get; init; } = [];
}

public sealed record RecognizedWordTiming(
    string Text,
    TimeSpan Start,
    TimeSpan End);

public sealed record WordTiming(
    string RecognizedText,
    TimeSpan Start,
    TimeSpan End,
    int SubmittedWordOrdinal,
    int NarrationWordOrdinal);

public sealed record WordAlignmentDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid AlignmentId { get; init; }

    public Guid RenderId { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string AudioRelativePath { get; init; } = string.Empty;

    public string AudioSha256 { get; init; } = string.Empty;

    public string SourceSha256 { get; init; } = string.Empty;

    public string SubmittedTextSha256 { get; init; } = string.Empty;

    public TimeSpan AudioDuration { get; init; }

    public double Coverage { get; init; }

    public int MatchedWordCount { get; init; }

    public int TotalWordCount { get; init; }

    public List<WordTiming> Words { get; init; } = [];
}

public sealed record WordAlignmentRecord
{
    public Guid AlignmentId { get; init; } = Guid.NewGuid();

    public WordAlignmentStatus Status { get; init; } = WordAlignmentStatus.Pending;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string AudioRelativePath { get; init; } = string.Empty;

    public string AudioSha256 { get; init; } = string.Empty;

    public string? ArtifactRelativePath { get; init; }

    public double Coverage { get; init; }

    public int MatchedWordCount { get; init; }

    public int TotalWordCount { get; init; }

    public string? FailureCode { get; init; }
}

public sealed record WordAlignmentProviderRequest(
    byte[] Audio,
    string FileName,
    string MediaType,
    string SubmittedText);

public sealed record WordAlignmentProviderResult(
    IReadOnlyList<RecognizedWordTiming> Words,
    TimeSpan Duration,
    string? ProviderResponseId = null);

public sealed record WordSequenceAlignmentResult(
    IReadOnlyList<WordTiming> Words,
    double Coverage,
    int MatchedWordCount,
    int TotalWordCount);

public enum WordAlignmentFailureKind
{
    Credentials,
    Authentication,
    Validation,
    Policy,
    RateLimited,
    Transient,
    MalformedResponse,
    Unsupported,
}

public sealed class WordAlignmentProviderException : Exception
{
    public WordAlignmentProviderException(
        string providerId,
        WordAlignmentFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderId = providerId;
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public string ProviderId { get; }

    public WordAlignmentFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}

public interface IWordAlignmentProvider
{
    string ProviderId { get; }

    string ModelId { get; }

    Task<WordAlignmentProviderResult> TranscribeAsync(
        WordAlignmentProviderRequest request,
        string apiKey,
        CancellationToken cancellationToken);
}

public interface IWordAlignmentService
{
    Task<WordAlignmentRecord> AlignAsync(
        string projectRoot,
        string segmentId,
        Guid renderId,
        CancellationToken cancellationToken = default);

    Task<int> RecoverInterruptedAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);
}

public static class WordTextNormalizer
{
    private static readonly IReadOnlyDictionary<string, string> NumericWords =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zero"] = "0",
            ["one"] = "1",
            ["two"] = "2",
            ["three"] = "3",
            ["four"] = "4",
            ["five"] = "5",
            ["six"] = "6",
            ["seven"] = "7",
            ["eight"] = "8",
            ["nine"] = "9",
            ["ten"] = "10",
        };

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder builder = new(value.Length);
        string compatible = value.Normalize(NormalizationForm.FormKC);
        for (int index = 0; index < compatible.Length; index++)
        {
            char character = compatible[index];
            char normalized = character switch
            {
                '’' or '‘' => '\'',
                '‐' or '‑' or '–' or '—' => '-',
                _ => char.ToLower(character, CultureInfo.InvariantCulture),
            };
            if (char.IsLetterOrDigit(normalized) || normalized is '\'' or '-' or '_')
            {
                builder.Append(normalized);
            }
            else if (normalized == '.'
                && index > 0
                && index + 1 < compatible.Length
                && char.IsDigit(compatible[index - 1])
                && char.IsDigit(compatible[index + 1]))
            {
                builder.Append('.');
            }
        }

        string token = builder.ToString().Trim('\'', '-', '_');
        return NumericWords.GetValueOrDefault(token, token);
    }
}
