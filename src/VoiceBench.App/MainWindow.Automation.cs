using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;

namespace IssueLab.VoiceBench.App;

public sealed partial class MainWindow
{
    internal object ReadAutomationWorkspace() => WorkbenchSurface.ReadAutomationWorkspace();
    internal async Task<object?> ExecuteDesktopAutomationAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
    {
        await WorkbenchSurface.WaitForProjectRefreshAsync(cancellationToken);
        string Required(string name) => arguments.GetProperty(name).GetString() ?? throw new AutomationException("invalid_arguments", $"Supply '{name}'.");
        string? Optional(string name) => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (command == "mix.export.execute") await WorkbenchSurface.EnsureAutomationSavedAsync();
        if (command.StartsWith("mix.playback.",StringComparison.Ordinal) || command is "assets.import.execute" or "assets.prepare.execute" or "assets.peaks" or "assets.waveform" or "assets.relink.execute" or "mix.export.execute")
            return await App.Services.ExecuteAudioAutomationAsync(command,arguments,cancellationToken);
        switch (command)
        {
            case "project.prepare":
                if (arguments.TryGetProperty("saveDraft", out JsonElement saveDraftValue) && saveDraftValue.GetBoolean())
                {
                    await WorkbenchSurface.ExecuteAutomationAsync("editor.save", arguments, cancellationToken);
                    return new { ready = true };
                }
                JsonElement workspace = AutomationJson.Element(WorkbenchSurface.ReadAutomationWorkspace());
                bool dirty = workspace.GetProperty("editor").GetProperty("isDirty").GetBoolean();
                bool discard = arguments.TryGetProperty("discardDraft", out JsonElement discardValue) && discardValue.GetBoolean();
                if (dirty && !discard) throw new AutomationException("unsaved_draft", "Save or reload the live draft before changing the project or saved authoring history.");
                return new { ready = true };
            case "presets.validate":
                VoicePreset preset = arguments.GetProperty("preset").Deserialize<VoicePreset>(AutomationJson.Options)
                    ?? throw new AutomationException("invalid_arguments", "Supply a voice preset.");
                ValidateAutomationVoice(preset.ProviderId, preset.ModelId, preset.Voice);
                return new { valid = true };
            case "control.get": return LocalControl is null ? new { ready = false } : await LocalControl.GetStatusAsync(cancellationToken);
            case "control.set":
                if (LocalControl is null) throw new AutomationException("desktop_unavailable", "Local control is still starting.");
                await LocalControl.SetEnabledAsync(arguments.GetProperty("enabled").GetBoolean());
                return await LocalControl.GetStatusAsync(cancellationToken);
            case "navigation.show":
                string page = Required("page").ToLowerInvariant();
                if (page == "voicelab") page = "voice-lab";
                if (page is not ("projects" or "workbench" or "voice-lab" or "audio" or "assembly" or "settings"))
                    throw new AutomationException("invalid_arguments", "Page must be projects, workbench, voice-lab, audio, assembly or settings.");
                NavigateTo(page);
                return new { page };
            case "providers.list":
                return new { speech = App.Services.SpeechProviders.Select(item => item.Descriptor).ToArray(), writing = App.Services.WritingProviders };
            case "credentials.status":
                return new
                {
                    openai = !string.IsNullOrWhiteSpace(await App.Services.GetCredentialAsync("openai", cancellationToken)),
                    gemini = !string.IsNullOrWhiteSpace(await App.Services.GetCredentialAsync("gemini", cancellationToken)),
                };
            case "credentials.open":
                string? providerId = Optional("providerId");
                if (providerId is not (null or "openai" or "gemini")) throw new AutomationException("invalid_arguments", "Provider must be openai or gemini.");
                NavigateTo("settings");
                Activate();
                // The secure dialog owns the secret. The bridge returns immediately and never sees it.
                if (providerId is not null) SettingsSurface.OpenCredentialEntry(providerId);
                return new { opened = true, providerId };
            case "toolchain.status":
                var installed = App.Services.FindInstalledFfmpeg();
                return new { installed = installed is not null, ffmpegPath = installed?.FfmpegPath, ffprobePath = installed?.FfprobePath };
            case "toolchain.ensure": return await App.Services.EnsureFfmpegAsync(cancellationToken);
            case "toolchain.select": return App.Services.SelectExistingFfmpeg(Required("executablePath"));
            case "artifacts.audio-preview":
                string previewRelativePath = Required("relativePath");
                WorkbenchSurface.ResolveAutomationArtifact(previewRelativePath, audioOnly: true);
                double previewStart = arguments.TryGetProperty("startSeconds", out JsonElement startSeconds) ? startSeconds.GetDouble() : 0;
                double previewDuration = arguments.TryGetProperty("durationSeconds", out JsonElement durationSeconds) ? durationSeconds.GetDouble() : 15;
                return await new AudioPreviewService(new ProcessRunner()).CreateAsync(App.Services.Session.RequireProjectRoot(),
                    previewRelativePath, previewStart, previewDuration, await App.Services.EnsureFfmpegAsync(cancellationToken), cancellationToken);
            case "render.start":
                await WorkbenchSurface.PrepareAutomationRenderAsync(Required("segmentId"));
                return await WorkbenchSurface.RunAutomationOperationAsync("Render narration",
                    token => App.Services.RenderSegmentAsync(Required("segmentId"), token), cancellationToken);
            case "render.audition":
                return await WorkbenchSurface.RunAutomationOperationAsync("Render audition", token =>
                    App.Services.RenderAuditionAsync(new AuditionRequest(Required("slot"), Required("providerId"),
                    Required("modelId"), Required("voice"), Optional("directions") ?? string.Empty, Required("text")), token), cancellationToken);
            case "render.process":
                await WorkbenchSurface.EnsureAutomationSavedAsync();
                string root = App.Services.Session.RequireProjectRoot();
                var toolchain = await App.Services.EnsureFfmpegAsync(cancellationToken);
                var processed = await WorkbenchSurface.RunAutomationOperationAsync("Process take", token =>
                    new RawAudioProcessingService(App.Services.Projects, new ProcessRunner())
                    .ProcessAsync(root, Required("segmentId"), Guid.Parse(Required("renderId")), toolchain, token), cancellationToken);
                await App.Services.Session.RefreshAsync(cancellationToken);
                return processed;
            case "alignment.start":
                await WorkbenchSurface.EnsureAutomationSavedAsync();
                var alignmentTake = App.Services.Session.RequireProject().Segments.Single(item => item.SegmentId == Required("segmentId"))
                    .RenderHistory.Single(item => item.RenderId == Guid.Parse(Required("renderId")));
                if (alignmentTake.Origin == RenderOrigin.Imported)
                {
                    if (Optional("providerId") is not string alignmentProvider || Optional("modelId") is not string alignmentModel)
                        throw new AutomationException("invalid_arguments","Choose an explicit providerId and modelId for imported recording alignment.");
                    var importedAlignment = await WorkbenchSurface.RunAutomationOperationAsync("Align imported recording",token =>
                        ((IssueLab.VoiceBench.Infrastructure.Rendering.FileWordAlignmentService)App.Services.WordAlignments)
                        .AlignImportedAsync(App.Services.Session.RequireProjectRoot(),Required("segmentId"),Guid.Parse(Required("renderId")),alignmentProvider,alignmentModel,token),cancellationToken);
                    await App.Services.Session.RefreshAsync(cancellationToken);
                    return importedAlignment;
                }
                return await WorkbenchSurface.RunAutomationOperationAsync("Align words", token =>
                    App.Services.AlignTakeAsync(Required("segmentId"), Guid.Parse(Required("renderId")), token), cancellationToken);
            case "master.start":
                await WorkbenchSurface.EnsureAutomationSavedAsync();
                MasterScope scope = Enum.TryParse(Optional("scope") ?? "Compilation", true, out MasterScope parsedScope) ? parsedScope
                    : throw new AutomationException("invalid_arguments", "Scope must be Compilation or Chapter.");
                return await WorkbenchSurface.RunAutomationOperationAsync("Build master", token =>
                    App.Services.BuildMasterAsync(Required("name"), scope, Optional("chapterId"), token), cancellationToken);
            case "exports.create":
                await WorkbenchSurface.EnsureAutomationSavedAsync();
                MasterScope exportScope = Enum.TryParse(Optional("scope") ?? "Compilation", true, out MasterScope parsedExportScope) ? parsedExportScope
                    : throw new AutomationException("invalid_arguments", "Scope must be Compilation or Chapter.");
                return await WorkbenchSurface.RunAutomationOperationAsync("Export production pack", token =>
                    App.Services.ExportProductionPackAsync(Required("name"), exportScope, Optional("chapterId"), token), cancellationToken);
            default: return await WorkbenchSurface.ExecuteAutomationAsync(command, arguments, cancellationToken);
        }
    }

    internal static void ValidateAutomationVoice(string providerId, string modelId, string voice)
    {
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.SingleOrDefault(item => item.Descriptor.ProviderId == providerId)?.Descriptor
            ?? throw new AutomationException("invalid_voice", "Select a known speech provider explicitly.");
        SpeechModelDescriptor model = provider.Models.SingleOrDefault(item => item.ModelId == modelId)
            ?? throw new AutomationException("invalid_voice", "The requested model does not belong to this provider.");
        if (!model.Voices.Contains(voice, StringComparer.Ordinal))
            throw new AutomationException("invalid_voice", "The requested voice does not belong to this model.");
    }
}
