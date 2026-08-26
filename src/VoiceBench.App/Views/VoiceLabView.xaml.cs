using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class VoiceLabView : UserControl
{
    private AuditionResult? _auditionA;
    private AuditionResult? _auditionB;
    private bool _busy;

    public VoiceLabView()
    {
        InitializeComponent();
        Loaded += VoiceLabView_Loaded;
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    private async void VoiceLabView_Loaded(object sender, RoutedEventArgs e) => await LoadProjectAsync();

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        _ = DispatcherQueue.TryEnqueue(async () => await LoadProjectAsync());

    private async Task LoadProjectAsync()
    {
        VoiceBenchProjectDocument? project = App.Services.Session.Project;
        if (project is null)
        {
            AuditionTextBox.Text = string.Empty;
            UseAButton.IsEnabled = false;
            UseBButton.IsEnabled = false;
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Audition audio and presets are stored inside the active project folder.");
            return;
        }

        if (string.IsNullOrWhiteSpace(AuditionTextBox.Text))
        {
            SegmentDocument? first = project.SegmentOrder
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
        bool complete = presetA is not null && presetB is not null
            && VoicePresetRules.CanSelectAsProjectDefault(presetA)
            && VoicePresetRules.CanSelectAsProjectDefault(presetB);
        UseAButton.IsEnabled = complete && !_busy;
        UseBButton.IsEnabled = complete && !_busy;
        if (complete)
        {
            AuditionAStatusText.Text = "Audition A rendered";
            AuditionBStatusText.Text = "Audition B rendered";
            ShowStatus(InfoBarSeverity.Success, "A/B audition complete", "Listen to the immutable samples, then explicitly choose A or B as the project default.");
        }
        else
        {
            ShowStatus(InfoBarSeverity.Informational, $"Auditioning for {project.Name}", "Render both exact configurations before choosing the default project voice.");
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
                ShowStatus(InfoBarSeverity.Informational, $"Audition {slot} rendered", "Render the other exact configuration before either can become the project default.");
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
        AuditionResult result = await App.Services.RenderAuditionAsync(
            new AuditionRequest(slot, providerId, model, voice, directions, AuditionTextBox.Text));
        (isA ? AuditionAStatusText : AuditionBStatusText).Text = $"Rendered · {FormatDuration(result.Duration)}";
        return result;
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
        ShowStatus(InfoBarSeverity.Success, "A/B audition complete", "Both immutable samples are saved. Choose the project default explicitly.");
    }

    private async void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot }) return;
        try
        {
            VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
            string presetId = slot == "A" ? "voice-lab-a" : "voice-lab-b";
            VoicePreset preset = project.VoicePresets.Single(item => item.PresetId == presetId);
            if (!VoicePresetRules.CanSelectAsProjectDefault(preset))
                throw new InvalidOperationException("Render both A and B before selecting a project default.");
            await App.Services.Session.SaveAsync(project with { DefaultVoicePresetId = presetId });
            ShowStatus(InfoBarSeverity.Success, $"Audition {slot} is the project default", $"{preset.ProviderId} / {preset.ModelId} / {preset.Voice} will be used unless a segment explicitly selects another auditioned preset.");
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
        UseAButton.IsEnabled = !busy && _auditionA is not null && _auditionB is not null;
        UseBButton.IsEnabled = UseAButton.IsEnabled;
    }

    private static string GetSelectedContent(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? throw new InvalidOperationException($"Choose {box.Header?.ToString()?.ToLowerInvariant() ?? "a value"}.");

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        AuditionInfoBar.Severity = severity;
        AuditionInfoBar.Title = title;
        AuditionInfoBar.Message = message;
        AuditionInfoBar.IsOpen = true;
    }
}
