using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IssueLab.VoiceBench.Core;

public interface IProjectDocumentMigration
{
    int FromSchemaVersion { get; }

    int ToSchemaVersion { get; }

    JsonObject Migrate(JsonObject source);
}

public sealed class ProjectDocumentMigrator
{
    private readonly IReadOnlyDictionary<int, IProjectDocumentMigration> migrations;

    public ProjectDocumentMigrator(IEnumerable<IProjectDocumentMigration>? migrations = null)
    {
        this.migrations = (migrations ?? [])
            .ToDictionary(migration => migration.FromSchemaVersion);
    }

    public JsonObject MigrateToCurrent(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var document = (JsonObject)source.DeepClone();
        var version = ReadSchemaVersion(document);
        if (version > VoiceBenchProjectDocument.CurrentSchemaVersion)
        {
            throw new ProjectSchemaException(
                $"Project schema {version} is newer than supported schema " +
                $"{VoiceBenchProjectDocument.CurrentSchemaVersion}.");
        }

        while (version < VoiceBenchProjectDocument.CurrentSchemaVersion)
        {
            if (!migrations.TryGetValue(version, out var migration) ||
                migration.ToSchemaVersion <= version)
            {
                throw new ProjectSchemaException(
                    $"No migration is registered from project schema {version}.");
            }

            document = migration.Migrate(document) ??
                throw new ProjectSchemaException($"Migration from schema {version} returned no document.");
            version = migration.ToSchemaVersion;
            document["schemaVersion"] = version;
        }

        return document;
    }

    public VoiceBenchProjectDocument Deserialize(
        JsonObject source,
        JsonSerializerOptions? serializerOptions = null)
    {
        var migrated = MigrateToCurrent(source);
        return migrated.Deserialize<VoiceBenchProjectDocument>(
                   serializerOptions ?? ProjectJson.SerializerOptions) ??
               throw new ProjectSchemaException("The project manifest did not contain a document.");
    }

    private static int ReadSchemaVersion(JsonObject source)
    {
        var node = source["schemaVersion"] ?? source["SchemaVersion"];
        if (node is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 0)
        {
            throw new ProjectSchemaException("The project manifest has no valid schemaVersion.");
        }

        return version;
    }
}

public sealed class OneToTwoProjectDocumentMigration : IProjectDocumentMigration
{
    public int FromSchemaVersion => 1;

    public int ToSchemaVersion => 2;

    public JsonObject Migrate(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        JsonObject result = (JsonObject)source.DeepClone();
        JsonArray segments = result["segments"] as JsonArray ?? [];
        JsonArray legacyOrder = result["segmentOrder"] as JsonArray ?? [];
        JsonArray orderedIds = legacyOrder.Count > 0
            ? new JsonArray(legacyOrder.Select(item => item?.DeepClone()).ToArray())
            : new JsonArray(segments
                .OfType<JsonObject>()
                .Select(item => item["segmentId"]?.DeepClone())
                .Where(item => item is not null)
                .ToArray());

        JsonArray chapters = [];
        JsonArray chapterOrder = [];
        if (segments.Count > 0)
        {
            const string chapterId = "existing-narration";
            foreach (JsonObject segment in segments.OfType<JsonObject>())
            {
                segment["chapterId"] = chapterId;
            }

            chapters.Add(new JsonObject
            {
                ["chapterId"] = chapterId,
                ["title"] = "Existing narration",
                ["status"] = "active",
                ["includedInCompilation"] = true,
                ["referenceIds"] = new JsonArray(),
                ["segmentOrder"] = orderedIds,
            });
            chapterOrder.Add(chapterId);
        }

        result.Remove("segmentOrder");
        result["chapters"] = chapters;
        result["chapterOrder"] = chapterOrder;
        result["writingProfile"] = JsonSerializer.SerializeToNode(new WritingProfile(), ProjectJson.SerializerOptions);
        result["sourceReferences"] = new JsonArray();

        if (result["audioStandard"] is JsonObject audioStandard &&
            audioStandard["chapterPauseMilliseconds"] is null)
        {
            audioStandard["chapterPauseMilliseconds"] = AudioStandard.NarrationDefault.ChapterPauseMilliseconds;
        }

        return result;
    }
}

public sealed class TwoToThreeProjectDocumentMigration : IProjectDocumentMigration
{
    public int FromSchemaVersion => 2;

    public int ToSchemaVersion => 3;

    public JsonObject Migrate(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        JsonObject result = (JsonObject)source.DeepClone();
        JsonArray segments = result["segments"] as JsonArray ?? [];
        foreach (JsonObject segment in segments.OfType<JsonObject>())
        {
            JsonNode? sourceHash = segment["sourceSha256"]?.DeepClone();
            segment["authoringSha256"] ??= sourceHash;
            JsonArray renders = segment["renderHistory"] as JsonArray ?? [];
            foreach (JsonObject render in renders.OfType<JsonObject>())
            {
                render["authoringSourceSha256"] ??= render["sourceSha256"]?.DeepClone();
                render["authoringSourceRelativePath"] ??= render["cleanSourceRelativePath"]?.DeepClone();
            }
        }

        JsonObject writingProfile = result["writingProfile"] as JsonObject ?? new JsonObject();
        writingProfile["generateProductionCues"] ??= false;
        result["writingProfile"] = writingProfile;
        return result;
    }
}

public static class ProjectJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class ProjectSchemaException(string message) : Exception(message);
