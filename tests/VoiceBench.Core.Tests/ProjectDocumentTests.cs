using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ProjectDocumentTests
{
    [Fact]
    public void New_document_uses_schema_five_and_narration_audio_standard()
    {
        var document = new VoiceBenchProjectDocument();

        Assert.Equal(5, document.SchemaVersion);
        Assert.Equal(48_000, document.AudioStandard.SampleRate);
        Assert.Equal(1, document.AudioStandard.Channels);
        Assert.Equal(-16, document.AudioStandard.IntegratedLufs);
        Assert.Equal(-1, document.AudioStandard.TruePeakDbtp);
        Assert.Equal(1_000, document.AudioStandard.ChapterPauseMilliseconds);
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
        var migrator = new ProjectDocumentMigrator(
            [
                new ZeroToOneMigration(),
                new OneToTwoProjectDocumentMigration(),
                new TwoToThreeProjectDocumentMigration(),
                new ThreeToFourProjectDocumentMigration(),
                new FourToFiveProjectDocumentMigration(),
            ]);

        var document = migrator.Deserialize(source);

        Assert.Equal(5, document.SchemaVersion);
        Assert.Equal("Training narration", document.Name);
        Assert.Equal(750, document.AudioStandard.PauseMilliseconds);
        Assert.Equal(0, source["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Schema_one_migration_wraps_existing_segments_without_changing_their_paths_or_takes()
    {
        JsonObject source = JsonNode.Parse(
            """
            {
              "schemaVersion": 1,
              "projectId": "dd8a4b35-2fb2-4f88-b284-44a2bd650985",
              "name": "Existing narration",
              "audioStandard": {
                "sampleRate": 48000,
                "channels": 1,
                "pcmBitsPerSample": 24,
                "integratedLufs": -16,
                "truePeakDbtp": -1,
                "pauseMilliseconds": 500
              },
              "segmentOrder": ["intro"],
              "segments": [{
                "segmentId": "intro",
                "title": "Introduction",
                "scriptRelativePath": "02-scripts/001-introduction--abcdef.md",
                "sourceSha256": "hash",
                "renderHistory": [{
                  "renderId": "c01bfa56-135f-42c5-84c9-6b796ad0fb82",
                  "rawAudioRelativePath": "04-generated-audio/segments/intro.raw.wav"
                }]
              }]
            }
            """)!.AsObject();
        ProjectDocumentMigrator migrator = new([
            new OneToTwoProjectDocumentMigration(),
            new TwoToThreeProjectDocumentMigration(),
            new ThreeToFourProjectDocumentMigration(),
            new FourToFiveProjectDocumentMigration(),
        ]);

        VoiceBenchProjectDocument document = migrator.Deserialize(source);

        ChapterDocument chapter = Assert.Single(document.Chapters);
        Assert.Equal("Existing narration", chapter.Title);
        Assert.Equal(["intro"], chapter.SegmentOrder);
        Assert.Equal([chapter.ChapterId], document.ChapterOrder);
        SegmentDocument segment = Assert.Single(document.Segments);
        Assert.Equal(chapter.ChapterId, segment.ChapterId);
        Assert.Equal("02-scripts/001-introduction--abcdef.md", segment.ScriptRelativePath);
        Assert.Equal("04-generated-audio/segments/intro.raw.wav", Assert.Single(segment.RenderHistory).RawAudioRelativePath);
        Assert.Equal(1_000, document.AudioStandard.ChapterPauseMilliseconds);
    }

    [Fact]
    public void Schema_one_migration_keeps_an_empty_project_empty()
    {
        JsonObject source = JsonNode.Parse(
            """{"schemaVersion":1,"name":"Empty","segmentOrder":[],"segments":[]}""")!.AsObject();
        ProjectDocumentMigrator migrator = new([
            new OneToTwoProjectDocumentMigration(),
            new TwoToThreeProjectDocumentMigration(),
            new ThreeToFourProjectDocumentMigration(),
            new FourToFiveProjectDocumentMigration(),
        ]);

        VoiceBenchProjectDocument document = migrator.Deserialize(source);

        Assert.Empty(document.Chapters);
        Assert.Empty(document.ChapterOrder);
        Assert.Empty(document.Segments);
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
    public void Schema_two_migration_initializes_authoring_hashes_and_ai_cue_opt_in()
    {
        JsonObject source = JsonNode.Parse(
            """
            {
              "schemaVersion": 2,
              "writingProfile": { "tone": "Clear" },
              "segments": [{
                "segmentId": "intro",
                "sourceSha256": "narration-hash",
                "renderHistory": [{
                  "renderId": "c01bfa56-135f-42c5-84c9-6b796ad0fb82",
                  "sourceSha256": "narration-hash",
                  "cleanSourceRelativePath": "03-render-copies/intro.clean.md"
                }]
              }]
            }
            """)!.AsObject();
        ProjectDocumentMigrator migrator = new([
            new TwoToThreeProjectDocumentMigration(),
            new ThreeToFourProjectDocumentMigration(),
            new FourToFiveProjectDocumentMigration(),
        ]);

        VoiceBenchProjectDocument document = migrator.Deserialize(source);

        SegmentDocument segment = Assert.Single(document.Segments);
        Assert.Equal("narration-hash", segment.AuthoringSha256);
        RenderRecord render = Assert.Single(segment.RenderHistory);
        Assert.Equal("narration-hash", render.AuthoringSourceSha256);
        Assert.Equal(render.CleanSourceRelativePath, render.AuthoringSourceRelativePath);
        Assert.False(document.WritingProfile.GenerateProductionCues);
    }

    [Fact]
    public void Schema_three_migration_initializes_empty_alignment_history_without_network_state()
    {
        JsonObject source = JsonNode.Parse(
            """
            {
              "schemaVersion": 3,
              "segments": [{
                "segmentId": "intro",
                "renderHistory": [{
                  "renderId": "c01bfa56-135f-42c5-84c9-6b796ad0fb82",
                  "rawAudioRelativePath": "04-generated-audio/segments/intro.raw.wav"
                }]
              }]
            }
            """)!.AsObject();

        VoiceBenchProjectDocument document = new ProjectDocumentMigrator(
            [new ThreeToFourProjectDocumentMigration(), new FourToFiveProjectDocumentMigration()]).Deserialize(source);

        Assert.Equal(5, document.SchemaVersion);
        Assert.Empty(Assert.Single(Assert.Single(document.Segments).RenderHistory).AlignmentHistory);
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
