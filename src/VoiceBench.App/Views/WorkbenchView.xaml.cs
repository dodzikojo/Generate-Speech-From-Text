using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class WorkbenchView : UserControl
{
    private VoiceBenchProjectDocument? _project;
    private SegmentDocument? _segment;
    private string _loadedText = string.Empty;
    private string _loadedDiskHash = string.Empty;
    private bool _ignoreTextChanges;
    private bool _isDirty;
    private bool _externalOverwriteApproved;
    private CancellationTokenSource? _renderCancellation;

    public WorkbenchView()
    {
        InitializeComponent();
        SetSelectedContent(ProviderComboBox, "OpenAI");
        PopulateProviderControls("openai", "gpt-4o-mini-tts", "marin");
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
        Loaded += WorkbenchView_Loaded;
        App.Services.Session.ProjectChanged += Session_ProjectChanged;
    }

    private void ApplyResponsiveState(double width) => VisualStateManager.GoToState(
        this,
        width >= 1180 ? "WideWorkbench" : width >= 860 ? "CompactWorkbench" : "NarrowWorkbench",
        false);

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelComboBox is null || VoiceComboBox is null || ProviderComboBox.SelectedItem is null) return;
        PopulateProviderControls(GetSelectedProviderId(), null, null);
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox is null || ModelComboBox.SelectedItem is null || ProviderComboBox.SelectedItem is null) return;
        PopulateVoiceControls(GetSelectedProviderId(), null);
    }

    private void PopulateProviderControls(string providerId, string? preferredModel, string? preferredVoice)
    {
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
            item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
        ModelComboBox.Items.Clear();
        foreach (SpeechModelDescriptor model in provider.Models)
        {
            ModelComboBox.Items.Add(new ComboBoxItem
            {
                Content = model.DisplayName,
                Tag = model.ModelId,
            });
        }

        SetSelectedContent(ModelComboBox, preferredModel ?? provider.Models[0].ModelId);
        PopulateVoiceControls(providerId, preferredVoice);
    }

    private void PopulateVoiceControls(string providerId, string? preferredVoice)
    {
        if (ModelComboBox.SelectedItem is not ComboBoxItem) return;
        string modelId = GetSelectedContent(ModelComboBox);
        SpeechProviderDescriptor provider = App.Services.SpeechProviders.Single(item =>
            item.Descriptor.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Descriptor;
        SpeechModelDescriptor model = provider.Models.Single(item => item.ModelId == modelId);
        string? currentVoice = preferredVoice ??
            ((VoiceComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? VoiceComboBox.SelectedItem?.ToString());
        VoiceComboBox.Items.Clear();
        foreach (string voice in model.Voices)
        {
            VoiceComboBox.Items.Add(new ComboBoxItem { Content = voice, Tag = voice });
        }

        SetSelectedContent(VoiceComboBox, currentVoice ?? model.Voices[0]);
    }

    private async void WorkbenchView_Loaded(object sender, RoutedEventArgs e) => await LoadProjectAsync();

    private void Session_ProjectChanged(object? sender, EventArgs e) =>
        _ = DispatcherQueue.TryEnqueue(async () => await LoadProjectAsync(_segment?.SegmentId));

    private async Task LoadProjectAsync(string? preferredSegmentId = null)
    {
        if (!App.Services.Session.HasProject)
        {
            _project = null;
            _segment = null;
            TakeRailList.Items.Clear();
            TakeTransport.ClearAudio();
            SetEditorText(string.Empty);
            ShowStatus(InfoBarSeverity.Informational, "Open a project", "Create or open a folder-based project from Projects.");
            return;
        }

        _project = App.Services.Session.RequireProject();
        ProjectHeaderText.Text = $"WORKBENCH  /  {_project.Name.ToUpperInvariant()}";
        ProjectTitleText.Text = _project.Name;
        int approved = _project.Segments.Count(item => item.Status == SegmentStatus.Approved);
        TakeRailSummaryText.Text = $"{_project.Segments.Count} SEGMENTS  ·  {approved} APPROVED";
        PopulateTakeRail(_project);
        LoadDefaultPreset(_project);

        string? segmentId = preferredSegmentId;
        if (segmentId is null || !_project.Segments.Any(item => item.SegmentId == segmentId))
        {
            segmentId = _project.SegmentOrder.FirstOrDefault() ?? _project.Segments.FirstOrDefault()?.SegmentId;
        }

        if (segmentId is null)
        {
            _segment = null;
            TakeTransport.ClearAudio();
            SetEditorText(string.Empty);
            SegmentTitleText.Text = "NO SEGMENTS";
            SegmentStatsText.Text = "IMPORT A MARKDOWN OR TEXT SCRIPT TO BEGIN";
            ShowStatus(InfoBarSeverity.Informational, "This project has no script segments", "Import a script with the CLI, then return to Workbench.");
            return;
        }

        SelectTakeRailItem(segmentId);
        await LoadSegmentAsync(segmentId);
        if (_project.DefaultVoicePresetId is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Choose a project voice", "Complete an A/B audition in Voice Lab before rendering a segment.");
        }
        else
        {
            WorkbenchInfoBar.IsOpen = false;
        }
    }

    private void PopulateTakeRail(VoiceBenchProjectDocument project)
    {
        TakeRailList.Items.Clear();
        IReadOnlyList<string> order = project.SegmentOrder.Count > 0
            ? project.SegmentOrder
            : project.Segments.Select(item => item.SegmentId).ToArray();
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        int number = 0;
        foreach (string id in order)
        {
            if (!segments.TryGetValue(id, out SegmentDocument? segment)) continue;
            number++;
            string status = FormatStatus(segment.Status);
            string takes = segment.RenderHistory.Count == 0
                ? "NO TAKES"
                : $"TAKE {segment.RenderHistory.Max(item => item.TakeNumber):00}";
            TimeSpan duration = segment.RenderHistory.OrderByDescending(item => item.TakeNumber).Select(item => item.Duration).FirstOrDefault();
            Grid content = CreateTakeRailItemContent(number, segment, status, takes, duration);
            ListViewItem item = new()
            {
                Tag = segment.SegmentId,
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(item, $"Segment {number:000}, {segment.Title}, {status}, {segment.RenderHistory.Count} takes");
            TakeRailList.Items.Add(item);
        }
    }

    private static Grid CreateTakeRailItemContent(
        int number,
        SegmentDocument segment,
        string status,
        string takes,
        TimeSpan duration)
    {
        Grid content = new()
        {
            ColumnSpacing = 10,
            Margin = new Thickness(10, 9, 10, 9),
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TextBlock numberLabel = new()
        {
            Text = number.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 2, 0, 0),
        };
        numberLabel.Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"];

        TextBlock title = new()
        {
            Text = segment.Title,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };
        title.Style = (Style)Application.Current.Resources["ReadingTextStyle"];

        Brush statusBrush = GetSegmentStatusBrush(segment.Status);
        Microsoft.UI.Xaml.Shapes.Ellipse statusDot = new()
        {
            Width = 6,
            Height = 6,
            Fill = statusBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock statusText = new() { Text = status, Foreground = statusBrush };
        statusText.Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"];
        statusText.Foreground = statusBrush;
        TextBlock takeText = new()
        {
            Text = duration > TimeSpan.Zero ? $"{takes}  {FormatDuration(duration)}" : takes,
        };
        takeText.Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"];

        StackPanel metadata = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        metadata.Children.Add(statusDot);
        metadata.Children.Add(statusText);
        metadata.Children.Add(takeText);

        StackPanel body = new() { Spacing = 5 };
        body.Children.Add(title);
        body.Children.Add(metadata);
        Grid.SetColumn(body, 1);
        content.Children.Add(numberLabel);
        content.Children.Add(body);
        return content;
    }

    private static Brush GetSegmentStatusBrush(SegmentStatus status)
    {
        string resourceKey = status switch
        {
            SegmentStatus.Approved => "ApprovalBrush",
            SegmentStatus.Stale or SegmentStatus.PickupRequired => "PickupBrush",
            SegmentStatus.Ready or SegmentStatus.Rendering or SegmentStatus.Rendered => "ActiveSignalBrush",
            _ => "MutedTextBrush",
        };
        return (Brush)Application.Current.Resources[resourceKey];
    }

    private async void TakeRailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TakeRailList.SelectedItem is not ListViewItem { Tag: string segmentId } || _segment?.SegmentId == segmentId) return;
        if (_isDirty && !await SaveCurrentScriptAsync())
        {
            SelectTakeRailItem(_segment?.SegmentId);
            return;
        }

        await LoadSegmentAsync(segmentId);
    }

    private async Task LoadSegmentAsync(string segmentId)
    {
        VoiceBenchProjectDocument project = _project ?? App.Services.Session.RequireProject();
        int index = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        if (index < 0) return;
        SegmentDocument segment = project.Segments[index];
        string path = App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath);
        string text = await File.ReadAllTextAsync(path);
        string sourceHash = SourceTextHasher.Compute(text);
        SegmentDocument current = SegmentStateRules.WithSourceHash(segment, sourceHash);
        if (current != segment)
        {
            List<SegmentDocument> segments = [.. project.Segments];
            segments[index] = current;
            project = project with { Segments = segments };
            await App.Services.Projects.SaveAsync(App.Services.Session.RequireProjectRoot(), project);
            _project = project;
        }

        _segment = current;
        _loadedText = text;
        _loadedDiskHash = sourceHash;
        _externalOverwriteApproved = false;
        ExternalEditBar.IsOpen = false;
        SetEditorText(text);
        SegmentTitleText.Text = $"{index + 1:000}  /  {current.Title.ToUpperInvariant()}";
        ScriptPathText.Text = current.ScriptRelativePath.Replace('/', '\\');
        CompactSegmentTitleText.Text = $"{index + 1:000}  {current.Title}";
        CompactSegmentStatusText.Text = $"{FormatStatus(current.Status)}  ·  {index + 1} OF {project.Segments.Count}";
        UpdateEditorStats();
        PopulateTakeSelector(current);
        LoadSegmentPreset(project, current);
    }

    private async void ImportScript_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Session.HasProject)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Imported scripts are stored in the active project's 02-scripts folder.");
            return;
        }

        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            ProjectImportService importer = new(App.Services.Projects);
            VoiceBenchProjectDocument before = App.Services.Session.RequireProject();
            VoiceBenchProjectDocument after = await importer.ImportAsync(App.Services.Session.RequireProjectRoot(), file.Path);
            await App.Services.Session.RefreshAsync();
            ShowStatus(InfoBarSeverity.Success, "Script imported", $"Created {after.Segments.Count - before.Segments.Count} canonical segment(s) from {file.Name}.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Script import failed", exception.Message);
        }
    }

    private void ScriptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ignoreTextChanges) return;
        _isDirty = !string.Equals(ScriptTextBox.Text, _loadedText, StringComparison.Ordinal);
        UpdateEditorStats();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveCurrentScriptAsync())
            ShowStatus(InfoBarSeverity.Success, "Script saved", "The canonical script and manifest source state are current.");
    }

    private async Task<bool> SaveCurrentScriptAsync()
    {
        if (_project is null || _segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Open a project with at least one script segment.");
            return false;
        }

        string path = App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath);
        string externalHash = SourceTextHasher.Compute(await File.ReadAllTextAsync(path));
        if (!string.Equals(externalHash, _loadedDiskHash, StringComparison.OrdinalIgnoreCase) && !_externalOverwriteApproved)
        {
            ExternalEditBar.IsOpen = true;
            ShowStatus(InfoBarSeverity.Warning, "External edit needs a decision", "Reload, compare, or explicitly keep the current editor text before saving.");
            return false;
        }

        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, ScriptTextBox.Text);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        string newHash = SourceTextHasher.Compute(ScriptTextBox.Text);
        SegmentDocument updated = SegmentStateRules.WithSourceHash(_segment, newHash);
        int index = _project.Segments.FindIndex(item => item.SegmentId == _segment.SegmentId);
        List<SegmentDocument> segments = [.. _project.Segments];
        segments[index] = updated;
        await App.Services.Session.SaveAsync(_project with { Segments = segments });
        _project = App.Services.Session.RequireProject();
        _segment = _project.Segments.Single(item => item.SegmentId == updated.SegmentId);
        _loadedText = ScriptTextBox.Text;
        _loadedDiskHash = newHash;
        _externalOverwriteApproved = false;
        _isDirty = false;
        ExternalEditBar.IsOpen = false;
        UpdateEditorStats();
        return true;
    }

    private async void ReloadExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        await LoadSegmentAsync(_segment.SegmentId);
        ShowStatus(InfoBarSeverity.Informational, "External edit loaded", "The editor now shows the canonical file on disk.");
    }

    private async void KeepCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        string path = App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath);
        _loadedDiskHash = SourceTextHasher.Compute(await File.ReadAllTextAsync(path));
        _externalOverwriteApproved = true;
        if (await SaveCurrentScriptAsync())
            ShowStatus(InfoBarSeverity.Success, "Current editor text kept", "The explicit choice replaced the external version.");
    }

    private async void CompareExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null) return;
        string external = await File.ReadAllTextAsync(App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Editor and external versions",
            CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock { Text = $"EDITOR\n\n{ScriptTextBox.Text}\n\n────────\nEXTERNAL FILE\n\n{external}", TextWrapping = TextWrapping.Wrap },
            },
        };
        await dialog.ShowAsync();
    }

    private async void Pronunciation_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Pronunciations are stored in the project lexicon.");
            return;
        }

        TextBox writtenBox = new()
        {
            Header = "Written text",
            Text = ScriptTextBox.SelectedText.Trim(),
            PlaceholderText = "Navisworks",
        };
        TextBox spokenBox = new()
        {
            Header = "Speak as",
            PlaceholderText = "Nav-is-works",
        };
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(writtenBox);
        content.Children.Add(spokenBox);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Project pronunciation",
            PrimaryButtonText = "Save pronunciation",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(writtenBox.Text) || string.IsNullOrWhiteSpace(spokenBox.Text))
        {
            ShowStatus(InfoBarSeverity.Warning, "Pronunciation was not saved", "Both the written and spoken forms are required.");
            return;
        }

        List<PronunciationEntry> lexicon = [.. _project.Lexicon];
        PronunciationEntry entry = new()
        {
            Written = writtenBox.Text.Trim(),
            Spoken = spokenBox.Text.Trim(),
            WholeWord = true,
        };
        int existingIndex = lexicon.FindIndex(item => item.Written.Equals(entry.Written, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0) lexicon[existingIndex] = entry;
        else lexicon.Add(entry);
        await App.Services.Session.SaveAsync(_project with { Lexicon = lexicon });
        _project = App.Services.Session.RequireProject();
        ShowStatus(InfoBarSeverity.Success, "Pronunciation saved", $"Render copies will submit '{entry.Spoken}' wherever '{entry.Written}' appears as a whole word.");
    }

    private async void SplitAtCursor_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Choose a script segment before splitting it.");
            return;
        }

        int cursor = ScriptTextBox.SelectionStart;
        string editorText = ScriptTextBox.Text;
        if (cursor <= 0 || cursor >= editorText.Length)
        {
            ShowStatus(InfoBarSeverity.Warning, "Place the cursor inside the script", "Both halves must contain narration text.");
            return;
        }

        string first = editorText[..cursor].TrimEnd();
        string second = editorText[cursor..].TrimStart();
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            ShowStatus(InfoBarSeverity.Warning, "That split would create an empty segment", "Move the cursor between two narration passages and try again.");
            return;
        }
        if (!await SaveCurrentScriptAsync()) return;

        VoiceBenchProjectDocument project = _project;
        SegmentDocument segment = _segment;
        int segmentIndex = project.Segments.FindIndex(item => item.SegmentId == segment.SegmentId);
        string originalPath = App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath);
        string originalText = await File.ReadAllTextAsync(originalPath);
        string newSegmentId = Guid.NewGuid().ToString("N");
        string newTitle = $"{segment.Title} (part 2)";
        string newFileName = ScriptSegmentNaming.CreateFileName(segmentIndex + 2, newTitle, newSegmentId);
        string newRelativePath = VoiceBenchProjectLayout.ToManifestPath(Path.Combine(VoiceBenchProjectLayout.ScriptsDirectory, newFileName));
        string newPath = App.Services.Session.ResolveProjectPath(newRelativePath);
        string originalTemporaryPath = $"{originalPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(originalTemporaryPath, first);
            File.Move(originalTemporaryPath, originalPath, overwrite: true);
            await using (FileStream stream = new(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await using (StreamWriter writer = new(stream))
            {
                await writer.WriteAsync(second);
            }

            SegmentDocument firstSegment = SegmentStateRules.WithSourceHash(segment, SourceTextHasher.Compute(first));
            SegmentDocument secondSegment = new()
            {
                SegmentId = newSegmentId,
                Title = newTitle,
                ScriptRelativePath = newRelativePath,
                SourceSha256 = SourceTextHasher.Compute(second),
                Status = SegmentStatus.Draft,
                VoicePresetId = segment.VoicePresetId,
                Directions = segment.Directions,
            };
            List<SegmentDocument> segments = [.. project.Segments];
            segments[segmentIndex] = firstSegment;
            segments.Insert(segmentIndex + 1, secondSegment);
            List<string> order = [.. project.SegmentOrder];
            int orderIndex = order.IndexOf(segment.SegmentId);
            order.Insert(orderIndex >= 0 ? orderIndex + 1 : order.Count, newSegmentId);
            await App.Services.Session.SaveAsync(project with { Segments = segments, SegmentOrder = order });
            await LoadProjectAsync(newSegmentId);
            ShowStatus(InfoBarSeverity.Success, "Segment split", $"Created '{newTitle}' immediately after the original segment.");
        }
        catch (Exception exception)
        {
            if (File.Exists(originalTemporaryPath)) File.Delete(originalTemporaryPath);
            await File.WriteAllTextAsync(originalPath, originalText);
            if (File.Exists(newPath)) File.Delete(newPath);
            ShowStatus(InfoBarSeverity.Error, "Segment could not be split", exception.Message);
        }
    }

    private async void MarkReady_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveCurrentScriptAsync() || _project is null || _segment is null) return;
        await SetSegmentStatusAsync(SegmentStatus.Ready);
        ShowStatus(InfoBarSeverity.Success, "Segment marked Ready", "It can now be rendered with the exact selected preset.");
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Open a project and select a script segment.");
            return;
        }

        if (_renderCancellation is not null)
        {
            ShowStatus(InfoBarSeverity.Warning, "A render is already active", "Cancel the current job or wait for it to finish.");
            return;
        }

        try
        {
            if (!await SaveCurrentScriptAsync()) return;
            string providerId = GetSelectedProviderId();
            string modelId = GetSelectedContent(ModelComboBox);
            string voice = GetSelectedContent(VoiceComboBox);
            VoicePreset preset = _project.VoicePresets.SingleOrDefault(item =>
                item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) && item.ModelId == modelId && item.Voice.Equals(voice, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("That exact provider, model, and voice has not completed the project A/B audition. Choose an auditioned preset in Voice Lab.");
            int index = _project.Segments.FindIndex(item => item.SegmentId == _segment.SegmentId);
            List<SegmentDocument> segments = [.. _project.Segments];
            segments[index] = _segment with { VoicePresetId = preset.PresetId, Directions = DirectionTextBox.Text.Trim(), Status = SegmentStatus.Ready };
            await App.Services.Session.SaveAsync(_project with { Segments = segments });
            ShowStatus(InfoBarSeverity.Informational, "Rendering segment", "VoiceBench is submitting the exact auditioned settings.");
            _renderCancellation = new CancellationTokenSource();
            SetRenderQueueBusy(true);
            SegmentRenderResult result = await App.Services.RenderSegmentAsync(_segment.SegmentId, _renderCancellation.Token);
            string guidance = result.ExceedsFourMinuteGuidance ? " The estimated duration exceeds four minutes; split it before the next take." : string.Empty;
            ShowStatus(InfoBarSeverity.Success, $"Take {result.Render.TakeNumber:00} rendered", $"Immutable raw audio was saved at {result.Render.RawAudioRelativePath}.{guidance}");
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Render cancelled", "The queued job is journaled as cancelled; existing takes were not changed.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Render failed", Actionable(exception));
        }
        finally
        {
            _renderCancellation?.Dispose();
            _renderCancellation = null;
            SetRenderQueueBusy(false);
        }
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e) => _renderCancellation?.Cancel();

    private void SetRenderQueueBusy(bool busy)
    {
        RenderQueueStatusText.Text = busy ? "1 ACTIVE" : "IDLE";
        RenderProgressBar.IsIndeterminate = busy;
        RenderProgressBar.Value = busy ? 50 : 0;
        CancelRenderButton.IsEnabled = busy;
    }

    private async void Pickup_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null) return;
        await SetSegmentStatusAsync(SegmentStatus.PickupRequired);
        ShowStatus(InfoBarSeverity.Warning, "Pickup requested", "Existing takes remain immutable while you prepare a replacement.");
    }

    private async Task SetSegmentStatusAsync(SegmentStatus status)
    {
        VoiceBenchProjectDocument project = _project ?? throw new InvalidOperationException("No project is open.");
        SegmentDocument segment = _segment ?? throw new InvalidOperationException("No segment is selected.");
        int index = project.Segments.FindIndex(item => item.SegmentId == segment.SegmentId);
        List<SegmentDocument> segments = [.. project.Segments];
        segments[index] = segment with { Status = status };
        await App.Services.Session.SaveAsync(project with { Segments = segments });
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No segment selected", "Select a rendered segment first.");
            return;
        }

        try
        {
            ShowStatus(InfoBarSeverity.Informational, "Preparing take", "VoiceBench is converting and verifying the latest generated take with FFmpeg.");
            RenderRecord approved = await App.Services.ProcessAndApproveLatestAsync(_segment.SegmentId);
            ShowStatus(InfoBarSeverity.Success, $"Take {approved.TakeNumber:00} approved", "The processed take is approved against the current canonical source.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Take could not be approved", Actionable(exception));
        }
    }

    private async void BuildMaster_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No project is open", "Open a project before building a master.");
            return;
        }

        try
        {
            ShowStatus(InfoBarSeverity.Informational, "Building master", "VoiceBench is normalizing, joining, probing, and exporting the approved sequence.");
            MasteringResult result = await App.Services.BuildMasterAsync(_project.Name);
            ShowStatus(InfoBarSeverity.Success, "Master verified", $"Created {result.MasterRelativePath} and {result.ReportRelativePath} at {result.IntegratedLufs:0.0} LUFS.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Master is blocked", Actionable(exception));
        }
    }

    private void OpenVoiceLab_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("voice-lab");

    private void LoadDefaultPreset(VoiceBenchProjectDocument project)
    {
        VoicePreset? preset = project.DefaultVoicePresetId is null ? null : project.VoicePresets.SingleOrDefault(item => item.PresetId == project.DefaultVoicePresetId);
        VoicePresetInfoBar.IsOpen = preset is null;
        if (preset is not null) SetPresetControls(preset);
    }

    private void LoadSegmentPreset(VoiceBenchProjectDocument project, SegmentDocument segment)
    {
        string? presetId = segment.VoicePresetId ?? project.DefaultVoicePresetId;
        VoicePreset? preset = project.VoicePresets.SingleOrDefault(item => item.PresetId == presetId);
        if (preset is null) return;
        SetPresetControls(preset);
        DirectionTextBox.Text = segment.Directions ?? preset.Directions ?? string.Empty;
    }

    private void SetPresetControls(VoicePreset preset)
    {
        SetSelectedContent(ProviderComboBox, preset.ProviderId == "openai" ? "OpenAI" : "Google Gemini");
        SetSelectedContent(ModelComboBox, preset.ModelId);
        SetSelectedContent(VoiceComboBox, preset.Voice);
    }

    private void PopulateTakeSelector(SegmentDocument segment)
    {
        TakeSelector.Items.Clear();
        foreach (RenderRecord render in segment.RenderHistory.OrderByDescending(item => item.TakeNumber))
            TakeSelector.Items.Add(new ComboBoxItem { Content = $"Take {render.TakeNumber:00}", Tag = render.RenderId });
        TakeSelector.SelectedIndex = TakeSelector.Items.Count > 0 ? 0 : -1;
        UpdateTransport();
    }

    private void TakeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTransport();

    private void UpdateTransport()
    {
        RenderRecord? render = GetSelectedRender();
        bool hasTake = render is not null;
        PreviousTakeButton.IsEnabled = TakeSelector.SelectedIndex >= 0 && TakeSelector.SelectedIndex < TakeSelector.Items.Count - 1;
        PickupButton.IsEnabled = hasTake;
        ApproveButton.IsEnabled = render?.Status == RenderStatus.Generated;
        if (render is null)
        {
            TakeTransport.ClearAudio("NO TAKE SELECTED");
            return;
        }

        string relativePath = render.ProcessedAudioRelativePath ?? render.RawAudioRelativePath;
        string path = App.Services.Session.ResolveProjectPath(relativePath);
        if (File.Exists(path))
        {
            TakeTransport.LoadAudio(path, $"Take {render.TakeNumber:00} · {render.Voice}", render.Duration);
        }
        else
        {
            TakeTransport.ClearAudio("TAKE AUDIO MISSING");
        }
    }

    private RenderRecord? GetSelectedRender()
    {
        if (_segment is null || TakeSelector.SelectedItem is not ComboBoxItem { Tag: Guid renderId }) return null;
        return _segment.RenderHistory.SingleOrDefault(item => item.RenderId == renderId);
    }

    private void PreviousTake_Click(object sender, RoutedEventArgs e)
    {
        if (TakeSelector.SelectedIndex >= 0 && TakeSelector.SelectedIndex < TakeSelector.Items.Count - 1)
            TakeSelector.SelectedIndex++;
    }

    private void UpdateEditorStats()
    {
        int words = ScriptTextBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        TimeSpan duration = SegmentRenderService.EstimateDuration(ScriptTextBox.Text);
        SegmentStatsText.Text = $"{words} WORDS  ·  EST. {FormatDuration(duration)}  ·  {(_isDirty ? "UNSAVED" : "SAVED")}";
    }

    private void SetEditorText(string text)
    {
        _ignoreTextChanges = true;
        ScriptTextBox.Text = text;
        _ignoreTextChanges = false;
        _isDirty = false;
    }

    private void SelectTakeRailItem(string? segmentId)
    {
        if (segmentId is null) return;
        TakeRailList.SelectedItem = TakeRailList.Items.OfType<ListViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, segmentId, StringComparison.Ordinal));
    }

    private string GetSelectedProviderId() =>
        (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string
        ?? throw new InvalidOperationException("Choose an exact speech provider.");

    private static string GetSelectedContent(ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem item
            ? item.Tag as string ?? item.Content?.ToString()
                ?? throw new InvalidOperationException("The selected option has no identifier.")
            : throw new InvalidOperationException($"Choose {comboBox.Header?.ToString()?.ToLowerInvariant() ?? "a value"}.");

    private static void SetSelectedContent(ComboBox comboBox, string content)
    {
        ComboBoxItem? item = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, content, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new ComboBoxItem { Content = content };
            comboBox.Items.Add(item);
        }
        comboBox.SelectedItem = item;
    }

    private static string FormatStatus(SegmentStatus status) => status == SegmentStatus.PickupRequired ? "PICKUP REQUIRED" : status.ToString().ToUpperInvariant();

    private static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private static string Actionable(Exception exception) => exception switch
    {
        SpeechProviderException { Kind: SpeechProviderFailureKind.Credentials } => $"{exception.Message} Open Settings and add the exact provider key.",
        SpeechProviderException { Kind: SpeechProviderFailureKind.Authentication } => $"{exception.Message} Replace the provider key in Settings and try again.",
        FileNotFoundException => $"{exception.Message} Open Settings to download and verify the pinned FFmpeg toolchain.",
        _ => exception.Message,
    };

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        WorkbenchInfoBar.Severity = severity;
        WorkbenchInfoBar.Title = title;
        WorkbenchInfoBar.Message = message;
        WorkbenchInfoBar.IsOpen = true;
    }
}
