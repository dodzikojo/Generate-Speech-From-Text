using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.App.Layout;
using IssueLab.VoiceBench.App.Controls;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class VoiceLabView : UserControl
{
    private AuditionResult? _auditionA;
    private AuditionResult? _auditionB;
    private Guid? _loadedProjectId;
    private bool _busy;

    public VoiceLabView()
    {
        InitializeComponent();
        PopulateProviderControls(ModelAComboBox, VoiceAComboBox, "openai", "gpt-4o-mini-tts", "marin");
        PopulateProviderControls(ModelBComboBox, VoiceBComboBox, "gemini", "gemini-3.1-flash-tts-preview", "Kore");
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
        Loaded += VoiceLabView_Loaded;
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
        AuditionATransport.PlaybackStarted += (_, _) => AuditionBTransport.Pause();
        AuditionBTransport.PlaybackStarted += (_, _) => AuditionATransport.Pause();
    }

    private void ApplyResponsiveState(double width)
    {
        bool isComfortable = width >= 900;
        VisualStateManager.GoToState(
            this,
            isComfortable ? "ComfortableVoiceLab" : "NarrowVoiceLab",
            false);
        StudioPageFrame.Resize(VoiceLabRoot, width, 1180, isComfortable ? 32 : 16);
    }

    private void ModelAComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PopulateVoiceControls(ModelAComboBox, VoiceAComboBox, "openai", null);

    private void ModelBComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PopulateVoiceControls(ModelBComboBox, VoiceBComboBox, "gemini", null);

    private static void PopulateProviderControls(
        ComboBox modelBox,
        ComboBox voiceBox,
        string providerId,
        string preferredModel,
        string preferredVoice)
    {
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
            item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
        modelBox.Items.Clear();
        foreach (SpeechModelDescriptor model in provider.Models)
        {
            modelBox.Items.Add(new ComboBoxItem
            {
                Content = model.DisplayName,
                Tag = model.ModelId,
            });
        }

        SelectTaggedValue(modelBox, preferredModel);
        PopulateVoiceControls(modelBox, voiceBox, providerId, preferredVoice);
    }

    private static void PopulateVoiceControls(
        ComboBox modelBox,
        ComboBox voiceBox,
        string providerId,
        string? preferredVoice)
    {
        if (modelBox.SelectedItem is not ComboBoxItem modelItem) return;
        string modelId = modelItem.Tag as string ?? modelItem.Content?.ToString() ?? string.Empty;
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
            item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
        SpeechModelDescriptor model = provider.Models.Single(item => item.ModelId == modelId);
        string? currentVoice = preferredVoice ?? (voiceBox.SelectedItem as ComboBoxItem)?.Tag as string;
        voiceBox.Items.Clear();
        foreach (SpeechVoiceDescriptor voice in model.VoiceDescriptors)
        {
            voiceBox.Items.Add(VoiceComboBoxItemFactory.Create(voice));
        }

        SelectTaggedValue(voiceBox, currentVoice ?? model.Voices[0]);
    }

    private static void SelectTaggedValue(ComboBox comboBox, string value)
    {
        ComboBoxItem? item = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, value, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedItem = item ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private async void VoiceLabView_Loaded(object sender, RoutedEventArgs e) => await LoadProjectAsync();

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        _ = DispatcherQueue.TryEnqueue(async () => await LoadProjectAsync());

    private async Task LoadProjectAsync()
    {
        VoiceBenchProjectDocument? project = App.Services.Session.Project;
        if (project is null)
        {
            _loadedProjectId = null;
            _auditionA = null;
            _auditionB = null;
            AuditionATransport.ClearAudio();
            AuditionBTransport.ClearAudio();
            AuditionTextBox.Text = string.Empty;
            UseAButton.IsEnabled = false;
            UseBButton.IsEnabled = false;
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Audition audio and presets are stored inside the active project folder.");
            return;
        }

        if (_loadedProjectId != project.ProjectId)
        {
            _loadedProjectId = project.ProjectId;
            _auditionA = null;
            _auditionB = null;
            AuditionATransport.ClearAudio();
            AuditionBTransport.ClearAudio();
        }

        if (string.IsNullOrWhiteSpace(AuditionTextBox.Text))
        {
            SegmentDocument? first = ProjectOrdering.GetPlaybackOrderedSegmentIds(project)
                .Select(id => project.Segments.FirstOrDefault(item => item.SegmentId == id))
                .FirstOrDefault(item => item is not null)
                ?? project.Segments.FirstOrDefault();
            if (first is not null)
            {
                string text = await File.ReadAllTextAsync(App.Services.Session.ResolveProjectPath(first.ScriptRelativePath));
                AuditionTextBox.Text = text.Length <= 900 ? text : text[..900];
            }
        }

        VoicePreset? presetA = project.VoicePresets.FirstOrDefault(item => item.PresetId == "voice-lab-a");
        VoicePreset? presetB = project.VoicePresets.FirstOrDefault(item => item.PresetId == "voice-lab-b");
        bool savedAudition = presetA is not null && presetB is not null
            && presetA.AuditionRenderIds.Distinct().Take(2).Count() >= 2
            && presetB.AuditionRenderIds.Distinct().Take(2).Count() >= 2;
        if (savedAudition)
        {
            _auditionA ??= TryRestoreAudition(presetA!, "A");
            _auditionB ??= TryRestoreAudition(presetB!, "B");
            if (_auditionA is not null) LoadTransport("A", _auditionA);
            if (_auditionB is not null) LoadTransport("B", _auditionB);
        }

        bool complete = savedAudition && _auditionA is not null && _auditionB is not null;
        UseAButton.IsEnabled = !_busy;
        UseBButton.IsEnabled = !_busy;
        if (complete)
        {
            AuditionAStatusText.Text = "Audition A rendered";
            AuditionBStatusText.Text = "Audition B rendered";
            ShowStatus(InfoBarSeverity.Success, "A/B audition complete", "Listen to the immutable samples, or choose either configuration directly as the project voice.");
        }
        else
        {
            ShowStatus(InfoBarSeverity.Informational, $"Voice choices for {project.Name}", "Choose either configuration now. Render one or both only when you want to compare the audio first.");
        }
    }

    private async void RenderBoth_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            SetBusy(true);
            _auditionA = await RenderSlotAsync("A");
            _auditionB = await RenderSlotAsync("B");
            await SaveCompletedAuditionAsync();
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "A/B audition stopped", $"VoiceBench did not switch providers. {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RenderSingle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string slot }) return;
        try
        {
            SetBusy(true);
            AuditionResult result = await RenderSlotAsync(slot);
            if (slot == "A") _auditionA = result; else _auditionB = result;
            if (_auditionA is not null && _auditionB is not null)
                await SaveCompletedAuditionAsync();
            else
                ShowStatus(InfoBarSeverity.Informational, $"Audition {slot} rendered", "You can use these settings now or render the other configuration for comparison.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, $"Audition {slot} failed", $"VoiceBench did not fall back to another provider or model. {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<AuditionResult> RenderSlotAsync(string slot)
    {
        if (!App.Services.Session.HasProject) throw new InvalidOperationException("Open a project before auditioning voices.");
        if (string.IsNullOrWhiteSpace(AuditionTextBox.Text)) throw new InvalidOperationException("Enter audition text first.");
        bool isA = slot == "A";
        string providerId = isA ? "openai" : "gemini";
        string model = GetSelectedContent(isA ? ModelAComboBox : ModelBComboBox);
        string voice = GetSelectedContent(isA ? VoiceAComboBox : VoiceBComboBox);
        string directions = (isA ? DirectionATextBox : DirectionBTextBox).Text.Trim();
        ShowStatus(InfoBarSeverity.Informational, $"Rendering audition {slot}", $"Submitting {providerId} / {model} / {voice} exactly as selected.");
        AuditionResult result = await App.Services.RunTrackedJobAsync<AuditionResult>("render.audition",
            new { slot, providerId, modelId = model, voice, directions, text = AuditionTextBox.Text });
        LoadTransport(slot, result);
        (isA ? AuditionAStatusText : AuditionBStatusText).Text = $"Rendered · {FormatDuration(result.Duration)}";
        return result;
    }

    private void LoadTransport(string slot, AuditionResult result)
    {
        bool isA = slot.Equals("A", StringComparison.OrdinalIgnoreCase);
        string provider = isA ? "OpenAI" : "Gemini";
        string voice = GetSelectedContent(isA ? VoiceAComboBox : VoiceBComboBox);
        string path = App.Services.Session.ResolveProjectPath(result.AudioRelativePath);
        (isA ? AuditionATransport : AuditionBTransport).LoadAudio(
            path,
            $"Audition {slot.ToUpperInvariant()} · {provider} · {voice}",
            result.Duration);
    }

    private static AuditionResult? TryRestoreAudition(VoicePreset preset, string slot)
    {
        string root = App.Services.Session.RequireProjectRoot();
        string directory = VoiceBenchProjectLayout.ResolveRelativePath(
            root,
            VoiceBenchProjectLayout.AuditionsDirectory);
        if (!Directory.Exists(directory)) return null;

        foreach (Guid renderId in preset.AuditionRenderIds)
        {
            string pattern = $"audition-{slot.ToLowerInvariant()}--*--{renderId:N}.raw.wav";
            string? path = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (path is null) continue;

            try
            {
                WaveFileMetadata wave = WaveFileInspector.Read(File.ReadAllBytes(path));
                string relativePath = VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(root, path));
                return new AuditionResult(renderId, relativePath, wave.Duration, wave.SampleRate, wave.Channels);
            }
            catch (IOException)
            {
                continue;
            }
            catch (InvalidDataException)
            {
                continue;
            }
        }

        return null;
    }

    private async Task SaveCompletedAuditionAsync()
    {
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        Guid[] ids = [_auditionA!.RenderId, _auditionB!.RenderId];
        VoicePreset presetA = CreatePreset("voice-lab-a", "Audition A", "openai", ModelAComboBox, VoiceAComboBox, DirectionATextBox, ids);
        VoicePreset presetB = CreatePreset("voice-lab-b", "Audition B", "gemini", ModelBComboBox, VoiceBComboBox, DirectionBTextBox, ids);
        List<VoicePreset> presets = project.VoicePresets
            .Where(item => item.PresetId is not ("voice-lab-a" or "voice-lab-b"))
            .ToList();
        presets.Add(presetA);
        presets.Add(presetB);
        await App.Services.Session.SaveAsync(project with { VoicePresets = presets });
        UseAButton.IsEnabled = true;
        UseBButton.IsEnabled = true;
        ShowStatus(InfoBarSeverity.Success, "A/B audition complete", "Both immutable samples are saved. Choose either configuration when ready.");
    }

    private async void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot }) return;
        try
        {
            VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
            bool isA = slot == "A";
            string presetId = slot == "A" ? "voice-lab-a" : "voice-lab-b";
            Guid[] renderedIds = new[] { _auditionA?.RenderId, _auditionB?.RenderId }
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToArray();
            VoicePreset preset = CreatePreset(
                presetId,
                $"Voice Lab {slot}",
                isA ? "openai" : "gemini",
                isA ? ModelAComboBox : ModelBComboBox,
                isA ? VoiceAComboBox : VoiceBComboBox,
                isA ? DirectionATextBox : DirectionBTextBox,
                renderedIds);
            List<VoicePreset> presets = project.VoicePresets.Where(item => item.PresetId != presetId).ToList();
            presets.Add(preset);
            VoiceBenchProjectDocument updated = ProjectVoiceSelectionRules.SetProjectVoice(
                project with { VoicePresets = presets },
                presetId);
            await App.Services.Session.SaveAsync(updated);
            ShowStatus(InfoBarSeverity.Success, $"Voice {slot} selected", $"{preset.ProviderId} / {preset.ModelId} / {preset.Voice} is now the project voice. No audition was required.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Default voice was not changed", exception.Message);
        }
    }

    private static VoicePreset CreatePreset(
        string id,
        string name,
        string provider,
        ComboBox model,
        ComboBox voice,
        TextBox directions,
        IEnumerable<Guid> ids) => new()
        {
            PresetId = id,
            Name = name,
            ProviderId = provider,
            ModelId = GetSelectedContent(model),
            Voice = GetSelectedContent(voice),
            Directions = directions.Text.Trim(),
            AuditionRenderIds = ids.Distinct().ToList(),
        };

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RenderBothButton.IsEnabled = !busy;
        UseAButton.IsEnabled = !busy && App.Services.Session.HasProject;
        UseBButton.IsEnabled = !busy && App.Services.Session.HasProject;
    }

    private static string GetSelectedContent(ComboBox box) =>
        box.SelectedItem is ComboBoxItem item
            ? item.Tag as string ?? item.Content?.ToString()
                ?? throw new InvalidOperationException("The selected voice option has no identifier.")
            : throw new InvalidOperationException($"Choose {box.Header?.ToString()?.ToLowerInvariant() ?? "a value"}.");

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        AuditionInfoBar.Severity = severity;
        AuditionInfoBar.Title = title;
        AuditionInfoBar.Message = message;
        AuditionInfoBar.IsOpen = true;
    }
}
