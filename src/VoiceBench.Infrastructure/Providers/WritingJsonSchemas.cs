using System.Text.Json;

namespace IssueLab.VoiceBench.Infrastructure.Providers;

internal static class WritingJsonSchemas
{
    public static JsonElement Outline { get; } = Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "title": { "type": "string" },
            "summary": { "type": "string" },
            "segments": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "title": { "type": "string" },
                  "purpose": { "type": "string" },
                  "targetSeconds": { "type": "integer", "minimum": 1 }
                },
                "required": ["title", "purpose", "targetSeconds"]
              }
            }
          },
          "required": ["title", "summary", "segments"]
        }
        """);

    public static JsonElement Draft { get; } = Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "title": { "type": "string" },
            "summary": { "type": "string" },
            "segments": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "title": { "type": "string" },
                  "text": { "type": "string" }
                },
                "required": ["title", "text"]
              }
            }
          },
          "required": ["title", "summary", "segments"]
        }
        """);

    public static JsonElement Proposal { get; } = Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "proposedText": { "type": "string" },
            "summary": { "type": "string" },
            "warnings": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["proposedText", "summary", "warnings"]
        }
        """);

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
