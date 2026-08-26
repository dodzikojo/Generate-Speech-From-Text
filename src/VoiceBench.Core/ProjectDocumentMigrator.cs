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
