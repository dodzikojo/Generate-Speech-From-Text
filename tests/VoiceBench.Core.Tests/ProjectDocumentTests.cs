using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ProjectDocumentTests
{
    [Fact]
    public void New_document_uses_schema_one_and_narration_audio_standard()
    {
        var document = new VoiceBenchProjectDocument();

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(48_000, document.AudioStandard.SampleRate);
        Assert.Equal(1, document.AudioStandard.Channels);
        Assert.Equal(-16, document.AudioStandard.IntegratedLufs);
        Assert.Equal(-1, document.AudioStandard.TruePeakDbtp);
    }

    [Fact]
    public void Segment_manifest_contains_a_path_but_no_duplicate_script_content()
    {
        var segment = new SegmentDocument
        {
            SegmentId = "intro",
            ScriptRelativePath = "02-scripts/001-introduction--a1b2c3.md",
        };

        var json = JsonSerializer.Serialize(segment, ProjectJson.SerializerOptions);

        Assert.Contains("scriptRelativePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"text\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migrator_runs_registered_step_and_deserializes_current_document()
    {
        var source = JsonNode.Parse(
            """
            {
              "schemaVersion": 0,
              "name": "Training narration",
              "segments": [],
              "segmentOrder": []
            }
            """)!.AsObject();
        var migrator = new ProjectDocumentMigrator([new ZeroToOneMigration()]);

        var document = migrator.Deserialize(source);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal("Training narration", document.Name);
        Assert.Equal(750, document.AudioStandard.PauseMilliseconds);
        Assert.Equal(0, source["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Migrator_rejects_unknown_older_schema()
    {
        var source = JsonNode.Parse("""{"schemaVersion":0}""")!.AsObject();
        var migrator = new ProjectDocumentMigrator();

        var exception = Assert.Throws<ProjectSchemaException>(() => migrator.MigrateToCurrent(source));

        Assert.Contains("No migration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrator_rejects_future_schema()
    {
        var source = JsonNode.Parse("""{"schemaVersion":99}""")!.AsObject();
        var migrator = new ProjectDocumentMigrator();

        var exception = Assert.Throws<ProjectSchemaException>(() => migrator.MigrateToCurrent(source));

        Assert.Contains("newer", exception.Message, StringComparison.Ordinal);
    }

    private sealed class ZeroToOneMigration : IProjectDocumentMigration
    {
        public int FromSchemaVersion => 0;

        public int ToSchemaVersion => 1;

        public JsonObject Migrate(JsonObject source)
        {
            var result = (JsonObject)source.DeepClone();
            result["audioStandard"] = JsonSerializer.SerializeToNode(
                AudioStandard.NarrationDefault with { PauseMilliseconds = 750 },
                ProjectJson.SerializerOptions);
            return result;
        }
    }
}
