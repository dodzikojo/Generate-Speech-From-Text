using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class WorkbenchView
{
    private string _automationRevision = Guid.NewGuid().ToString("N");
    private string? _automationFingerprint;

    private void InvalidateAutomationRevision()
    {
        _automationRevision = Guid.NewGuid().ToString("N");
        _automationFingerprint = null;
    }

    private object ReadAutomationEditor()
    {
        string text = GetEditorText();
        string fingerprint = SourceTextHasher.Compute(string.Join('\0',
            App.Services.Session.ProjectRoot, _segment?.SegmentId, text, DirectionTextBox.Text,
            _loadedDiskHash, HasUnsavedSegmentChanges.ToString(), ExternalEditBar.IsOpen.ToString(),
            GetSelectedProviderId(), GetSelectedContentOrNull(ModelComboBox), GetSelectedContentOrNull(VoiceComboBox)));
        if (_automationFingerprint != fingerprint)
        {
            _automationRevision = Guid.NewGuid().ToString("N");
            _automationFingerprint = fingerprint;
        }
        (int start, int length, _) = GetEditorSelection();
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        return new
        {
            segmentId = _segment?.SegmentId, text, directions = DirectionTextBox.Text,
            revision = _automationRevision, isDirty = HasUnsavedSegmentChanges,
            externalConflict = ExternalEditBar.IsOpen, savedAuthoringSha256 = _loadedDiskHash,
            selection = new { start, length, encoding = "UTF-16" },
            narrationText = parsed.NarrationText, cues = parsed.Cues, diagnostics = parsed.Diagnostics,
        };
    }

    private void RequireAutomationRevision(JsonElement arguments)
    {
        ReadAutomationEditor();
        if (!string.Equals(RequiredString(arguments, "expectedRevision"), _automationRevision, StringComparison.Ordinal))
            throw new AutomationException("revision_conflict", "The shared editor changed. Read editor.get and retry with its current revision.", ReadAutomationEditor());
        if (_segment is null) throw new AutomationException("no_segment", "Select a script segment first.");
    }

    private void ApplyAutomationText(string text, int selectionStart, int selectionEnd)
    {
        EndFollowSession();
        TakeTransport.Pause();
        CommitPendingEditorUndo();
        _editorUndoHistory.Add(CaptureEditorSnapshot());
        if (_editorUndoHistory.Count > EditorHistoryLimit) _editorUndoHistory.RemoveAt(0);
        _editorRedoHistory.Clear();
        ApplyEditorSnapshot(new EditorHistorySnapshot(text, selectionStart, selectionEnd, DirectionTextBox.Text));
        UpdateFollowEligibility();
        ApplyCueFormatting();
    }

    internal object ReadAutomationWorkspace() => new
    {
        projectRoot = App.Services.Session.ProjectRoot,
        projectId = _project?.ProjectId,
        selection = ReadAutomationSelection(), editor = ReadAutomationEditor(),
        playback = TakeTransport.GetPlaybackStatus(),
        panels = new { chapterRail = ChapterRailToggle.IsChecked == true, director = DirectorToggle.IsChecked == true, takeReview = TakeReviewToggle.IsChecked == true },
        fontSize = ScriptTextBox.FontSize, followScript = FollowScriptToggle.IsChecked == true,
    };

    internal async Task EnsureAutomationSavedAsync()
    {
        if (HasUnsavedSegmentChanges && !await SaveCurrentScriptAsync())
            throw new AutomationException("external_conflict", "Resolve the external edit before continuing.", ReadAutomationEditor());
    }

    internal async Task PrepareAutomationRenderAsync(string segmentId)
    {
        await EnsureAutomationSavedAsync();
        if (_segment?.SegmentId != segmentId)
        {
            _project = App.Services.Session.RequireProject();
            if (!_project.Segments.Any(item => item.SegmentId == segmentId))
                throw new AutomationException("segment_not_found", "The requested segment does not exist.");
            await LoadSegmentAsync(segmentId);
        }
        if (!await PrepareCurrentRenderAsync())
            throw new AutomationException("render_invalid", "Fix the script or external edit before rendering.");
    }

    internal async Task<T> RunAutomationOperationAsync<T>(string label, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (_renderCancellation is not null) throw new AutomationException("operation_busy", "Finish or cancel the active studio operation first.");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renderCancellation = cancellation;
        SetRenderQueueBusy(true);
        RenderQueueStatusText.Text = $"1 ACTIVE · {label.ToUpperInvariant()}";
        ShowStatus(InfoBarSeverity.Informational, label, "This agent operation shares the studio queue. Use Cancel to stop it.");
        try
        {
            T result = await operation(cancellation.Token);
            ShowStatus(InfoBarSeverity.Success, $"{label} complete", "The result is available in the project's history and agent job record.");
            return result;
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, $"{label} cancelled", "Earlier audio and project artifacts were preserved.");
            throw;
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, $"{label} stopped", Actionable(exception));
            throw;
        }
        finally
        {
            _renderCancellation = null;
            SetRenderQueueBusy(false);
        }
    }

    private object ReadAutomationSelection()
    {
        (int start, int length, _) = GetEditorSelection();
        return new { chapterId = GetSelectedRailItem()?.ChapterId ?? _segment?.ChapterId,
            segmentId = _segment?.SegmentId, renderId = GetSelectedRender()?.RenderId,
            start, length, encoding = "UTF-16" };
    }

    internal async Task<object?> ExecuteAutomationAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (command)
        {
            case "workspace.get": return ReadAutomationWorkspace();
            case "editor.get": case "directions.get": return ReadAutomationEditor();
            case "director.get":
                ReadAutomationEditor();
                return new { providerId = GetSelectedProviderId(), modelId = GetSelectedContent(ModelComboBox), voice = GetSelectedContentOrNull(VoiceComboBox), directions = DirectionTextBox.Text, revision = _automationRevision };
            case "director.set":
                RequireAutomationRevision(arguments);
                string requestedProvider = OptionalString(arguments, "providerId") ?? GetSelectedProviderId();
                string requestedModel = OptionalString(arguments, "modelId") ?? GetSelectedContent(ModelComboBox);
                string requestedVoice = OptionalString(arguments, "voice") ?? GetSelectedContent(VoiceComboBox);
                MainWindow.ValidateAutomationVoice(requestedProvider, requestedModel, requestedVoice);
                SetSelectedContent(ProviderComboBox, requestedProvider == "openai" ? "OpenAI" : "Google Gemini");
                PopulateProviderControls(requestedProvider, requestedModel, requestedVoice);
                if (OptionalString(arguments, "directions") is string requestedDirections)
                {
                    CommitPendingEditorUndo();
                    DirectionTextBox.Text = requestedDirections;
                    CommitPendingEditorUndo();
                }
                InvalidateAutomationRevision();
                return await ExecuteAutomationAsync("director.get", AutomationJson.Element(new { }), cancellationToken);
            case "editor.replace":
                RequireAutomationRevision(arguments);
                string replacement = NormalizeAutomationText(RequiredString(arguments, "text"));
                ApplyAutomationText(replacement, 0, 0);
                return ReadAutomationEditor();
            case "editor.edit":
                RequireAutomationRevision(arguments);
                string original = GetEditorText();
                Utf16TextEdit[] edits = arguments.GetProperty("edits").Deserialize<Utf16TextEdit[]>(AutomationJson.Options)
                    ?? throw new AutomationException("invalid_arguments", "Supply an edits array.");
                int previousEnd = 0;
                foreach (Utf16TextEdit edit in edits.OrderBy(item => item.Start))
                {
                    ValidateAutomationRange(original, edit.Start, edit.Length);
                    if (edit.Start < previousEnd) throw new AutomationException("invalid_range", "Text edits must not overlap.");
                    previousEnd = checked(edit.Start + edit.Length);
                }
                foreach (Utf16TextEdit edit in edits.OrderByDescending(item => item.Start))
                    original = original.Remove(edit.Start, edit.Length).Insert(edit.Start, NormalizeAutomationText(edit.Text));
                ApplyAutomationText(original, 0, 0);
                return ReadAutomationEditor();
            case "directions.set":
                RequireAutomationRevision(arguments);
                CommitPendingEditorUndo();
                DirectionTextBox.Text = RequiredString(arguments, "directions");
                CommitPendingEditorUndo();
                return ReadAutomationEditor();
            case "editor.save":
                RequireAutomationRevision(arguments);
                if (!await SaveCurrentScriptAsync())
                    throw new AutomationException("external_conflict", "Resolve the external edit with editor.conflict before saving.", ReadAutomationEditor());
                return ReadAutomationEditor();
            case "editor.reload":
                RequireAutomationRevision(arguments);
                await LoadSegmentAsync(_segment!.SegmentId, discardDraft: true);
                return ReadAutomationEditor();
            case "editor.undo": case "editor.redo":
                RequireAutomationRevision(arguments);
                EndFollowSession();
                TakeTransport.Pause();
                if (command == "editor.undo") await UndoAuthoringAsync(); else await RedoAuthoringAsync();
                return ReadAutomationEditor();
            case "editor.conflict":
                RequireAutomationRevision(arguments);
                string choice = RequiredString(arguments, "choice");
                string diskText = await File.ReadAllTextAsync(App.Services.Session.ResolveProjectPath(_segment!.ScriptRelativePath), cancellationToken);
                if (choice == "compare") return new { editor = ReadAutomationEditor(), externalText = diskText, externalAuthoringSha256 = SourceTextHasher.Compute(diskText) };
                if (choice == "reload") await LoadSegmentAsync(_segment.SegmentId, discardDraft: true);
                else if (choice == "keep")
                {
                    _loadedDiskHash = SourceTextHasher.Compute(diskText);
                    _externalOverwriteApproved = true;
                    if (!await SaveCurrentScriptAsync()) throw new AutomationException("external_conflict", "The external edit could not be replaced.");
                }
                else throw new AutomationException("invalid_arguments", "Conflict choice must be reload, keep or compare.");
                return ReadAutomationEditor();
            case "cues.insert": case "cues.update": case "cues.delete":
                RequireAutomationRevision(arguments);
                string cueText = GetEditorText();
                ParsedScriptDocument parsed = new ProductionScriptParser().Parse(cueText);
                int cueStart, cueLength;
                string cueReplacement = string.Empty;
                if (command == "cues.insert")
                {
                    cueStart = arguments.GetProperty("position").GetInt32();
                    cueLength = 0;
                    ValidateAutomationRange(cueText, cueStart, 0);
                }
                else
                {
                    int ordinal = arguments.GetProperty("ordinal").GetInt32();
                    if (ordinal < 0 || ordinal >= parsed.Cues.Count) throw new AutomationException("invalid_range", "Cue ordinal is zero-based and must identify an existing cue.");
                    ProductionCue cue = parsed.Cues[ordinal];
                    cueStart = cue.SourceStart;
                    cueLength = cue.SourceLength;
                }
                if (command != "cues.delete")
                {
                    string type = RequiredString(arguments, "type");
                    string content = RequiredString(arguments, "content");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(type, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$") || content.Contains('\n') || content.Contains('\r'))
                        throw new AutomationException("invalid_arguments", "Use a lowercase cue type and one line of cue content.");
                    cueReplacement = $"@{type}: {content}";
                    if (command == "cues.insert")
                        cueReplacement = (cueStart > 0 && cueText[cueStart - 1] != '\n' ? "\n" : "") + cueReplacement + "\n";
                }
                string newCueText = cueText.Remove(cueStart, cueLength).Insert(cueStart, cueReplacement);
                ApplyAutomationText(newCueText, cueStart, cueStart + cueReplacement.Length);
                return ReadAutomationEditor();
            case "selection.get": case "editor.selection.get": return ReadAutomationSelection();
            case "selection.set": case "editor.selection.set":
                if (arguments.TryGetProperty("start", out _) || arguments.TryGetProperty("length", out _)) RequireAutomationRevision(arguments);
                string? segmentId = OptionalString(arguments, "segmentId");
                string? chapterId = OptionalString(arguments, "chapterId");
                if (segmentId is null && chapterId is not null)
                    segmentId = App.Services.Session.RequireProject().Chapters.Single(item => item.ChapterId == chapterId).SegmentOrder.FirstOrDefault();
                if (segmentId is not null && segmentId != _segment?.SegmentId)
                {
                    await EnsureAutomationSavedAsync();
                    _project = App.Services.Session.RequireProject();
                    if (!_project.Segments.Any(item => item.SegmentId == segmentId)) throw new AutomationException("segment_not_found", "The requested segment does not exist.");
                    await LoadSegmentAsync(segmentId);
                }
                if (OptionalString(arguments, "renderId") is string renderId)
                {
                    Guid id = Guid.Parse(renderId);
                    ComboBoxItem? take = TakeSelector.Items.OfType<ComboBoxItem>().SingleOrDefault(item => item.Tag is Guid candidate && candidate == id);
                    if (take is null) throw new AutomationException("render_not_found", "The requested take does not belong to the selected segment.");
                    TakeSelector.SelectedItem = take;
                    await UpdateTransportAsync();
                }
                if (arguments.TryGetProperty("start", out JsonElement startProperty))
                {
                    int start = startProperty.GetInt32();
                    int length = arguments.TryGetProperty("length", out JsonElement lengthProperty) ? lengthProperty.GetInt32() : 0;
                    ValidateAutomationRange(GetEditorText(), start, length);
                    EndFollowSession();
                    ScriptTextBox.Document.Selection.SetRange(start, start + length);
                    CaptureEditorSelection();
                    ScriptTextBox.Focus(FocusState.Programmatic);
                }
                return ReadAutomationSelection();
            case "panels.set":
                string panel = RequiredString(arguments, "panel");
                bool visible = arguments.GetProperty("visible").GetBoolean();
                switch (panel)
                {
                    case "chapterRail": case "chapter-rail": ChapterRailToggle.IsChecked = visible; break;
                    case "director": DirectorToggle.IsChecked = visible; break;
                    case "takeReview": case "take-review": TakeReviewToggle.IsChecked = visible; break;
                    default: throw new AutomationException("invalid_arguments", "Panel must be chapterRail, director or takeReview.");
                }
                SaveWorkspacePanelPreferences();
                ApplyWorkspacePanelLayout(ActualWidth, ActualHeight);
                return ReadAutomationWorkspace();
            case "font.set":
                double size = arguments.GetProperty("size").GetDouble();
                if (!double.IsFinite(size) || size < 12 || size > 24) throw new AutomationException("invalid_arguments", "Editor font size must be between 12 and 24.");
                ApplyEditorFontSize(size, persist: true);
                return new { fontSize = ScriptTextBox.FontSize };
            case "playback.get": return TakeTransport.GetPlaybackStatus();
            case "playback.play":
                if (arguments.TryGetProperty("segmentId", out _) || arguments.TryGetProperty("renderId", out _))
                    await ExecuteAutomationAsync("selection.set", arguments, cancellationToken);
                if (OptionalString(arguments, "relativePath") is string audioPath)
                {
                    string path = ResolveAutomationArtifact(audioPath, audioOnly: true);
                    EndFollowSession();
                    _activeWordAlignment = null;
                    TakeTransport.LoadAudio(path, Path.GetFileName(path), TimeSpan.Zero);
                }
                TakeTransport.Play();
                return TakeTransport.GetPlaybackStatus();
            case "playback.pause": TakeTransport.Pause(); return TakeTransport.GetPlaybackStatus();
            case "playback.stop": TakeTransport.Stop(); EndFollowSession(); return TakeTransport.GetPlaybackStatus();
            case "playback.seek":
                double seconds = arguments.GetProperty("seconds").GetDouble();
                if (!double.IsFinite(seconds)) throw new AutomationException("invalid_arguments", "Seek seconds must be finite.");
                TakeTransport.Seek(TimeSpan.FromSeconds(seconds));
                return TakeTransport.GetPlaybackStatus();
            case "playback.follow": case "follow.set":
                FollowScriptToggle.IsChecked = arguments.GetProperty("enabled").GetBoolean();
                FollowScriptToggle_Click(FollowScriptToggle, new RoutedEventArgs());
                return new { enabled = FollowScriptToggle.IsChecked == true };
            case "artifacts.open": case "artifacts.reveal": case "file.open": case "file.reveal":
                string artifact = ResolveAutomationArtifact(RequiredString(arguments, "relativePath"));
                StorageFile file = await StorageFile.GetFileFromPathAsync(artifact);
                bool launched;
                if (command is "artifacts.open" or "file.open") launched = await Launcher.LaunchFileAsync(file);
                else
                {
                    FolderLauncherOptions options = new();
                    options.ItemsToSelect.Add(file);
                    launched = await Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
                }
                return new { launched };
            default: throw new AutomationException("unsupported_command", $"The desktop does not support '{command}'.");
        }
    }

    private static string NormalizeAutomationText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void ValidateAutomationRange(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start > text.Length || length > text.Length - start ||
            (start > 0 && start < text.Length && char.IsHighSurrogate(text[start - 1]) && char.IsLowSurrogate(text[start])) ||
            (start + length > 0 && start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length])))
            throw new AutomationException("invalid_range", "The UTF-16 range must be within the draft and cannot split a surrogate pair.");
    }

    internal string ResolveAutomationArtifact(string relativePath, bool audioOnly = false)
    {
        string normalized = relativePath.Replace('\\', '/');
        string[] allowed = audioOnly ? ["04-generated-audio/", "05-masters/"] : ["01-source/", "02-scripts/", "03-render-copies/", "04-generated-audio/", "05-masters/", "06-exports/", "07-reports/"];
        if (!allowed.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new AutomationException("invalid_artifact", "Select a known project script or output artifact.");
        string path = App.Services.Session.ResolveProjectPath(normalized);
        string current = path;
        string root = Path.TrimEndingDirectorySeparator(App.Services.Session.RequireProjectRoot());
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) != 0)
                throw new AutomationException("invalid_artifact", "Project files cannot traverse filesystem links.");
            current = Path.GetDirectoryName(current) ?? throw new AutomationException("invalid_artifact", "Invalid project file path.");
        }
        if (!File.Exists(path)) throw new AutomationException("artifact_not_found", "The requested project file does not exist.");
        if (audioOnly && !new[] { ".wav", ".mp3", ".m4a", ".flac" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new AutomationException("invalid_artifact", "Playback requires a project audio artifact.");
        return path;
    }

    private static string RequiredString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new AutomationException("invalid_arguments", $"Supply '{name}'.");

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
