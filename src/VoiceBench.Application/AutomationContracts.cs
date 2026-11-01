using System.Text.Json;

namespace IssueLab.VoiceBench.Application;

public static class AutomationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Options);
}

public sealed record AutomationRequest(string Command, JsonElement Arguments, string? RequestId = null);

public sealed record AutomationResult(bool IsError, string? Code, string? Message, JsonElement Data)
{
    public static AutomationResult Success(object? value) => new(false, null, null, AutomationJson.Element(value));
    public static AutomationResult Error(string code, string message, object? details = null) =>
        new(true, code, message, AutomationJson.Element(details));
}

public interface IAutomationDispatcher
{
    Task<AutomationResult> ExecuteAsync(AutomationRequest request, CancellationToken cancellationToken = default);
}

public interface IDesktopAutomationAdapter
{
    Task<object?> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken = default);
}

public sealed class AutomationException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}
