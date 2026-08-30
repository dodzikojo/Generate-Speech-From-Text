using IssueLab.VoiceBench.Core;
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
    private WritingProposal? _pendingWritingProposal;
    private WritingAction _pendingWritingAction;
    private string? _pendingWritingProviderId;
    private string? _pendingWritingModelId;
    private string? _pendingWritingSourceHash;
    private bool _ignoreCompactPicker;

    private sealed record RailItem(
        string Title,
        string Metadata,
        string ChapterId,
        string? SegmentId,
        Brush StatusBrush);

    private sealed record RevisionChoice(Guid RevisionId, bool RestorePreviousText);

    public WorkbenchView()
    {
        InitializeComponent();
        SetSelectedContent(ProviderComboBox, "OpenAI");
        PopulateProviderControls("openai", "gpt-4o-mini-tts", "marin");
        PopulateWritingProviders();
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

    private void PopulateWritingProviders()
    {
        WritingProviderComboBox.Items.Clear();
        foreach (WritingProviderDescriptor provider in App.Services.WritingProviders)
        {
            WritingProviderComboBox.Items.Add(new ComboBoxItem
            {
                Content = provider.DisplayName,
                Tag = provider.ProviderId,
            });
        }

        SetSelectedContent(WritingProviderComboBox, "openai");
        PopulateWritingModels("openai", "gpt-5.6-terra");
    }

    private void WritingProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WritingModelComboBox is null || WritingProviderComboBox.SelectedItem is null) return;
        PopulateWritingModels(GetSelectedContent(WritingProviderComboBox), null);
    }

    private void PopulateWritingModels(string providerId, string? preferredModel)
    {
        WritingProviderDescriptor provider = App.Services.WritingProviders.Single(item =>
            item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        WritingModelComboBox.Items.Clear();
        foreach (WritingModelDescriptor model in provider.Models)
        {
            WritingModelComboBox.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.ModelId });
        }

        SetSelectedContent(WritingModelComboBox, preferredModel ?? provider.Models[0].ModelId);
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
            ChapterTree.RootNodes.Clear();
            CompactSegmentPicker.Items.Clear();
            TakeTransport.ClearAudio();
            SetEditorText(string.Empty);
            EmptyAuthoringState.Visibility = Visibility.Collapsed;
            ShowStatus(InfoBarSeverity.Informational, "Open a project", "Create or open a folder-based project from Projects.");
            return;
        }

        _project = App.Services.Session.RequireProject();
        ProjectHeaderText.Text = $"WORKBENCH  /  {_project.Name.ToUpperInvariant()}";
        ProjectTitleText.Text = _project.Name;
        int approved = _project.Segments.Count(item => item.Status == SegmentStatus.Approved);
        TakeRailSummaryText.Text = $"{_project.Chapters.Count(item => item.Status == ChapterStatus.Active)} CHAPTERS  ·  {_project.Segments.Count} SEGMENTS  ·  {approved} APPROVED";
        PopulateChapterTree(_project);
        PopulateWritingReferences(_project);
        LoadDefaultPreset(_project);

        string? segmentId = preferredSegmentId;
        if (segmentId is null || !_project.Segments.Any(item => item.SegmentId == segmentId))
        {
            segmentId = ProjectOrdering.GetPlaybackOrderedSegmentIds(_project).FirstOrDefault()
                ?? _project.Segments.FirstOrDefault()?.SegmentId;
        }

        if (segmentId is null)
        {
            _segment = null;
            TakeTransport.ClearAudio();
            SetEditorText(string.Empty);
            SegmentTitleText.Text = "NO SEGMENTS";
            SegmentStatsText.Text = "CREATE, DRAFT, OR IMPORT A CHAPTER TO BEGIN";
            EmptyAuthoringState.Visibility = Visibility.Visible;
            ShowStatus(InfoBarSeverity.Informational, "Start your first chapter", "Create a blank chapter, draft one with AI, or import one or more scripts here in VoiceBench.");
            return;
        }

        EmptyAuthoringState.Visibility = Visibility.Collapsed;
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

    private void PopulateChapterTree(VoiceBenchProjectDocument project)
    {
        ChapterTree.RootNodes.Clear();
        CompactSegmentPicker.Items.Clear();
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(
            item => item.SegmentId,
            StringComparer.Ordinal);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(
            item => item.ChapterId,
            StringComparer.Ordinal);
        int chapterNumber = 0;
        foreach (string chapterId in project.ChapterOrder)
        {
            if (!chapters.TryGetValue(chapterId, out ChapterDocument? chapter) || chapter.Status == ChapterStatus.Archived) continue;
            chapterNumber++;
            int approved = chapter.SegmentOrder.Count(id =>
                segments.TryGetValue(id, out SegmentDocument? segment) && segment.Status == SegmentStatus.Approved);
            TreeViewNode chapterNode = new()
            {
                IsExpanded = true,
                Content = new RailItem(
                    $"{chapterNumber:00}  {chapter.Title}",
                    $"{chapter.SegmentOrder.Count} segments  ·  {approved} approved  ·  {(chapter.IncludedInCompilation ? "included" : "excluded")}",
                    chapter.ChapterId,
                    null,
                    (Brush)Application.Current.Resources[chapter.IncludedInCompilation ? "ActiveSignalBrush" : "MutedTextBrush"]),
            };
            int segmentNumber = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                segmentNumber++;
                string takes = segment.RenderHistory.Count == 0
                    ? "no takes"
                    : $"take {segment.RenderHistory.Max(item => item.TakeNumber):00}";
                TimeSpan duration = segment.RenderHistory.OrderByDescending(item => item.TakeNumber)
                    .Select(item => item.Duration).FirstOrDefault();
                string durationText = duration > TimeSpan.Zero ? $"  ·  {FormatDuration(duration)}" : string.Empty;
                chapterNode.Children.Add(new TreeViewNode
                {
                    Content = new RailItem(
                        $"{segmentNumber:000}  {segment.Title}",
                        $"{FormatStatus(segment.Status)}  ·  {takes}{durationText}",
                        chapter.ChapterId,
                        segment.SegmentId,
                        GetSegmentStatusBrush(segment.Status)),
                });
                CompactSegmentPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{chapter.Title}  /  {segment.Title}",
                    Tag = segment.SegmentId,
                });
            }

            ChapterTree.RootNodes.Add(chapterNode);
        }
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

    private async void ChapterTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        RailItem? item = args.InvokedItem switch
        {
            TreeViewNode node => node.Content as RailItem,
            RailItem railItem => railItem,
            _ => null,
        };
        if (item?.SegmentId is not string segmentId ||
            _segment?.SegmentId == segmentId) return;
        if (_isDirty && !await SaveCurrentScriptAsync())
        {
            SelectTakeRailItem(_segment?.SegmentId);
            return;
        }

        await LoadSegmentAsync(segmentId);
    }

    private async void CompactSegmentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreCompactPicker || CompactSegmentPicker.SelectedItem is not ComboBoxItem { Tag: string segmentId } ||
            _segment?.SegmentId == segmentId) return;
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
        ChapterDocument? chapter = project.Chapters.SingleOrDefault(item => item.ChapterId == current.ChapterId);
        int chapterPosition = Math.Max(0, project.ChapterOrder.IndexOf(current.ChapterId)) + 1;
        int segmentPosition = Math.Max(0, chapter?.SegmentOrder.IndexOf(current.SegmentId) ?? 0) + 1;
        SegmentTitleText.Text = $"{chapterPosition:00}.{segmentPosition:000}  /  {current.Title.ToUpperInvariant()}";
        ScriptPathText.Text = current.ScriptRelativePath.Replace('/', '\\');
        SelectTakeRailItem(current.SegmentId);
        UpdateEditorStats();
        PopulateTakeSelector(current);
        PopulateWritingRevisions(current);
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
        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        try
        {
            VoiceBenchProjectDocument before = App.Services.Session.RequireProject();
            VoiceBenchProjectDocument after = await App.Services.ScriptImporter.ImportFilesAsync(
                App.Services.Session.RequireProjectRoot(),
                files.Select(file => file.Path).ToArray());
            await App.Services.Session.RefreshAsync();
            ShowStatus(
                InfoBarSeverity.Success,
                files.Count == 1 ? "Script imported" : "Scripts imported",
                $"Created {after.Chapters.Count - before.Chapters.Count} chapter(s) and {after.Segments.Count - before.Segments.Count} canonical segment(s).");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Script import failed", exception.Message);
        }
    }

    private void PopulateWritingReferences(VoiceBenchProjectDocument project)
    {
        HashSet<string> selected = WritingReferencesList.SelectedItems
            .OfType<ListViewItem>()
            .Select(item => item.Tag as string)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        WritingReferencesList.Items.Clear();
        foreach (SourceReferenceDocument reference in project.SourceReferences)
        {
            ListViewItem item = new()
            {
                Content = reference.Name,
                Tag = reference.ReferenceId,
            };
            WritingReferencesList.Items.Add(item);
            if (selected.Contains(reference.ReferenceId)) WritingReferencesList.SelectedItems.Add(item);
        }
    }

    private void PopulateWritingRevisions(SegmentDocument segment)
    {
        WritingRevisionComboBox.Items.Clear();
        foreach (WritingRevisionRecord revision in segment.WritingRevisions.OrderByDescending(item => item.CreatedAt))
        {
            WritingRevisionComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{revision.CreatedAt.LocalDateTime:g} · accepted {revision.Action}",
                Tag = new RevisionChoice(revision.RevisionId, false),
            });
            WritingRevisionComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{revision.CreatedAt.LocalDateTime:g} · before {revision.Action}",
                Tag = new RevisionChoice(revision.RevisionId, true),
            });
        }
        WritingRevisionComboBox.SelectedIndex = WritingRevisionComboBox.Items.Count > 0 ? 0 : -1;
    }

    private async void RestoreRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_segment is null || WritingRevisionComboBox.SelectedItem is not ComboBoxItem { Tag: RevisionChoice choice }) return;
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Restore this writing revision?",
            Content = "The current text remains in revision history. Existing takes become stale if the source changes.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            string segmentId = _segment.SegmentId;
            await App.Services.ChapterAuthoring.RestoreRevisionAsync(
                App.Services.Session.RequireProjectRoot(),
                segmentId,
                choice.RevisionId,
                choice.RestorePreviousText);
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(segmentId);
            ShowStatus(InfoBarSeverity.Success, "Writing revision restored", "The canonical source and assembly status were updated.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Revision could not be restored", exception.Message);
        }
    }

    private async void ImportReference_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        try
        {
            foreach (Windows.Storage.StorageFile file in files)
            {
                await App.Services.ChapterAuthoring.ImportReferenceAsync(
                    App.Services.Session.RequireProjectRoot(), file.Path);
            }
            await App.Services.Session.RefreshAsync();
            ShowStatus(InfoBarSeverity.Success, "References imported", "References stay local until you explicitly select them for a writing request.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Reference import failed", exception.Message);
        }
    }

    private async void NewChapter_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        (ContentDialogResult result, string title, string brief) = await ShowNameDialogAsync(
            "New blank chapter",
            "Chapter title",
            "Chapter goal or brief (optional)",
            "Create chapter");
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title)) return;
        try
        {
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.CreateBlankChapterAsync(
                App.Services.Session.RequireProjectRoot(),
                title,
                brief);
            await App.Services.Session.RefreshAsync();
            string segmentId = updated.Chapters.Single(chapter => chapter.ChapterId == updated.ChapterOrder.Last())
                .SegmentOrder.Single();
            await LoadProjectAsync(segmentId);
            ShowStatus(InfoBarSeverity.Success, "Chapter created", "The blank chapter and its opening segment are ready to edit.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Chapter could not be created", exception.Message);
        }
    }

    private async void NewSegment_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        string? chapterId = GetSelectedRailItem()?.ChapterId ?? _segment?.ChapterId ??
            _project?.ChapterOrder.FirstOrDefault();
        if (chapterId is null)
        {
            NewChapter_Click(sender, e);
            return;
        }

        string? title = await PromptForTextAsync("New segment", "Segment title", "Opening", "Add segment");
        if (string.IsNullOrWhiteSpace(title)) return;
        VoiceBenchProjectDocument before = App.Services.Session.RequireProject();
        try
        {
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.AddSegmentAsync(
                App.Services.Session.RequireProjectRoot(), chapterId, title);
            await App.Services.Session.RefreshAsync();
            string newId = updated.Chapters.Single(chapter => chapter.ChapterId == chapterId).SegmentOrder
                .Except(before.Chapters.Single(chapter => chapter.ChapterId == chapterId).SegmentOrder)
                .Single();
            await LoadProjectAsync(newId);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Segment could not be created", exception.Message);
        }
    }

    private async void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (_project is null || selected is null) return;
        string current = selected.SegmentId is null
            ? _project.Chapters.Single(chapter => chapter.ChapterId == selected.ChapterId).Title
            : _project.Segments.Single(segment => segment.SegmentId == selected.SegmentId).Title;
        string? title = await PromptForTextAsync("Rename", "Name", current, "Rename");
        if (string.IsNullOrWhiteSpace(title)) return;
        if (selected.SegmentId is null)
        {
            await App.Services.ChapterAuthoring.UpdateChapterAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId, title);
        }
        else
        {
            await App.Services.ChapterAuthoring.RenameSegmentAsync(
                App.Services.Session.RequireProjectRoot(), selected.SegmentId, title);
        }

        await App.Services.Session.RefreshAsync();
    }

    private async void DuplicateSelected_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        if (selected is null) return;
        if (selected.SegmentId is null)
        {
            await App.Services.ChapterAuthoring.DuplicateChapterAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId);
        }
        else
        {
            await App.Services.ChapterAuthoring.DuplicateSegmentAsync(
                App.Services.Session.RequireProjectRoot(), selected.SegmentId);
        }

        await App.Services.Session.RefreshAsync();
        ShowStatus(InfoBarSeverity.Success, "Duplicate created", "The copy has its own canonical source and stable ID.");
    }

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string offsetText } ||
            !int.TryParse(offsetText, out int offset) || GetSelectedRailItem() is not RailItem selected) return;
        if (selected.SegmentId is null)
        {
            await App.Services.ChapterAuthoring.MoveChapterAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId, offset);
        }
        else
        {
            await App.Services.ChapterAuthoring.MoveSegmentAsync(
                App.Services.Session.RequireProjectRoot(), selected.ChapterId, selected.SegmentId, offset);
        }

        await App.Services.Session.RefreshAsync();
    }

    private async void ToggleChapterInclusion_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        string? chapterId = selected?.ChapterId ?? _segment?.ChapterId;
        if (_project is null || chapterId is null) return;
        ChapterDocument chapter = _project.Chapters.Single(item => item.ChapterId == chapterId);
        await App.Services.ChapterAuthoring.UpdateChapterAsync(
            App.Services.Session.RequireProjectRoot(),
            chapterId,
            includedInCompilation: !chapter.IncludedInCompilation);
        await App.Services.Session.RefreshAsync();
    }

    private async void ArchiveChapter_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        string? chapterId = selected?.ChapterId ?? _segment?.ChapterId;
        if (chapterId is null) return;
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Archive this chapter?",
            Content = "Its files and history stay in the project, but it will leave the active rail and compilation.",
            PrimaryButtonText = "Archive",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await App.Services.ChapterAuthoring.UpdateChapterAsync(
            App.Services.Session.RequireProjectRoot(),
            chapterId,
            includedInCompilation: false,
            status: ChapterStatus.Archived);
        await App.Services.Session.RefreshAsync();
    }

    private async void DraftWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectForAuthoring()) return;
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        TextBox titleBox = new() { Header = "Chapter title", PlaceholderText = "Coordinating the first review" };
        TextBox goalBox = new() { Header = "Goal", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
        TextBox audienceBox = new() { Header = "Audience", Text = project.WritingProfile.Audience };
        TextBox toneBox = new() { Header = "Tone", Text = project.WritingProfile.Tone };
        NumberBox durationBox = new() { Header = "Desired duration (minutes)", Value = 5, Minimum = 1, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        ComboBox providerBox = CreateWritingProviderPicker(project.WritingProfile.DefaultProviderId);
        ComboBox modelBox = new() { Header = "Exact model", HorizontalAlignment = HorizontalAlignment.Stretch };
        void SetDraftModels()
        {
            string providerId = GetSelectedContent(providerBox);
            WritingProviderDescriptor descriptor = App.Services.WritingProviders.Single(item => item.ProviderId == providerId);
            modelBox.Items.Clear();
            foreach (WritingModelDescriptor model in descriptor.Models)
                modelBox.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.ModelId });
            SetSelectedContent(modelBox,
                descriptor.Models.Any(model => model.ModelId == project.WritingProfile.DefaultModelId)
                    ? project.WritingProfile.DefaultModelId
                    : descriptor.Models[0].ModelId);
        }
        providerBox.SelectionChanged += (_, _) => SetDraftModels();
        SetDraftModels();
        ListView references = CreateReferencePicker(project, []);
        StackPanel fields = new() { Spacing = 10 };
        foreach (UIElement item in new UIElement[] { titleBox, goalBox, audienceBox, toneBox, durationBox, providerBox, modelBox,
            new TextBlock { Text = "Only checked local references are sent", Style = (Style)Application.Current.Resources["UtilityLabelTextStyle"] }, references })
            fields.Children.Add(item);
        ContentDialog setup = new()
        {
            XamlRoot = XamlRoot,
            Title = "Draft a chapter with AI",
            Content = new ScrollViewer { Content = fields, MaxHeight = 560 },
            PrimaryButtonText = "Generate outline",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await setup.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(titleBox.Text) || string.IsNullOrWhiteSpace(goalBox.Text))
        {
            ShowStatus(InfoBarSeverity.Warning, "Chapter goal required", "Add a title and goal before generating an outline.");
            return;
        }

        string providerId = GetSelectedContent(providerBox);
        string modelId = GetSelectedContent(modelBox);
        string[] referenceIds = references.SelectedItems.OfType<ListViewItem>()
            .Select(item => item.Tag as string).OfType<string>().ToArray();
        WritingProfile profile = project.WritingProfile with
        {
            Audience = audienceBox.Text.Trim(),
            Tone = toneBox.Text.Trim(),
            DefaultProviderId = providerId,
            DefaultModelId = modelId,
        };
        project = project with { WritingProfile = profile };
        await App.Services.Session.SaveAsync(project);
        try
        {
            WritingContext context = await App.Services.WritingContexts.LoadAsync(
                App.Services.Session.RequireProjectRoot(), goalBox.Text.Trim(), referenceIds);
            int targetSeconds = (int)Math.Round(durationBox.Value * 60);
            WritingOutlineRequest request = new(
                modelId,
                titleBox.Text.Trim(),
                goalBox.Text.Trim(),
                targetSeconds,
                context,
                EstimateWritingTokens(titleBox.Text, goalBox.Text, context));
            ShowStatus(InfoBarSeverity.Informational, "Generating outline", $"Sending only the brief and {referenceIds.Length} selected reference(s) to {providerId} / {modelId}.");
            ChapterOutline outline = await App.Services.WritingAssistant.CreateOutlineAsync(providerId, request);
            ChapterOutline? editedOutline = await ReviewOutlineAsync(outline);
            if (editedOutline is null) return;
            ShowStatus(InfoBarSeverity.Informational, "Writing full chapter", "Generating from the approved outline. No project files exist until you accept the preview.");
            ChapterDraft draft = await App.Services.WritingAssistant.CreateDraftAsync(
                providerId,
                new WritingDraftRequest(modelId, editedOutline, context, EstimateWritingTokens(goalBox.Text, context) + 500));
            if (!await PreviewDraftAsync(draft)) return;
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.CreateChapterFromDraftAsync(
                App.Services.Session.RequireProjectRoot(), draft, goalBox.Text.Trim(), referenceIds);
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(updated.Chapters.Single(chapter => chapter.ChapterId == updated.ChapterOrder.Last()).SegmentOrder.First());
            ShowStatus(InfoBarSeverity.Success, "AI draft accepted", "The chapter and canonical segment files were created atomically.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "AI chapter draft stopped", Actionable(exception));
        }
    }

    private async void WritingAction_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _segment is null || sender is not Button { Tag: string actionName } ||
            !Enum.TryParse(actionName, out WritingAction action)) return;
        if (!await SaveCurrentScriptAsync()) return;
        string providerId = GetSelectedContent(WritingProviderComboBox);
        string modelId = GetSelectedContent(WritingModelComboBox);
        int selectionStart = ScriptTextBox.SelectionStart;
        int selectionLength = ScriptTextBox.SelectionLength;
        string originalWhole = ScriptTextBox.Text;
        string target = selectionLength > 0 ? originalWhole.Substring(selectionStart, selectionLength) : originalWhole;
        string[] referenceIds = WritingReferencesList.SelectedItems.OfType<ListViewItem>()
            .Select(item => item.Tag as string).OfType<string>().ToArray();
        ChapterDocument? chapter = _project.Chapters.SingleOrDefault(item => item.ChapterId == _segment.ChapterId);
        try
        {
            WritingContext context = await App.Services.WritingContexts.LoadAsync(
                App.Services.Session.RequireProjectRoot(), chapter?.Brief ?? string.Empty, referenceIds);
            WritingRevisionRequest request = new(
                modelId,
                action,
                target,
                action == WritingAction.Custom ? CustomWritingInstructionTextBox.Text.Trim() : null,
                context,
                EstimateWritingTokens(target, CustomWritingInstructionTextBox.Text, context));
            ShowStatus(InfoBarSeverity.Informational, "Generating writing proposal", $"Using {providerId} / {modelId}. The canonical script will not change without acceptance.");
            WritingProposal partial = await App.Services.WritingAssistant.ReviseAsync(providerId, request);
            string proposedWhole = selectionLength > 0
                ? string.Concat(originalWhole.AsSpan(0, selectionStart), partial.ProposedText, originalWhole.AsSpan(selectionStart + selectionLength))
                : partial.ProposedText;
            WritingProposal proposal = partial with { ProposedText = proposedWhole };
            bool accept = await ShowWritingDiffAsync(originalWhole, proposal, action != WritingAction.Critique);
            if (!accept || action == WritingAction.Critique) return;
            _pendingWritingProposal = proposal;
            _pendingWritingAction = action;
            _pendingWritingProviderId = providerId;
            _pendingWritingModelId = modelId;
            _pendingWritingSourceHash = _segment.SourceSha256;
            await App.Services.ChapterAuthoring.ApplyWritingProposalAsync(
                App.Services.Session.RequireProjectRoot(),
                _segment.SegmentId,
                _pendingWritingSourceHash,
                proposal,
                action,
                providerId,
                modelId);
            await App.Services.Session.RefreshAsync();
            await LoadProjectAsync(_segment.SegmentId);
            WritingProposalInfoBar.Message = $"Accepted {action}. A restorable local revision was saved.";
            WritingProposalInfoBar.IsOpen = true;
            ShowStatus(InfoBarSeverity.Success, "Writing proposal accepted", "Rendered or approved audio was marked stale and must be rerendered before Assembly.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Writing proposal stopped", Actionable(exception));
        }
    }

    private async Task<bool> ShowWritingDiffAsync(string original, WritingProposal proposal, bool allowAccept)
    {
        int originalWords = CountWords(original);
        int proposedWords = CountWords(proposal.ProposedText);
        Grid comparison = new() { ColumnSpacing = 12, RowSpacing = 12 };
        bool sideBySide = ActualWidth >= 900;
        comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        comparison.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        comparison.RowDefinitions.Add(new RowDefinition { Height = sideBySide ? new GridLength(0) : GridLength.Auto });
        TextBox originalBox = new() { Header = "Original", Text = original, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 260 };
        TextBox proposedBox = new() { Header = allowAccept ? "Proposal" : "Critique context", Text = proposal.ProposedText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 260 };
        comparison.Children.Add(originalBox);
        Grid.SetColumn(proposedBox, sideBySide ? 1 : 0);
        Grid.SetRow(proposedBox, sideBySide ? 0 : 1);
        comparison.Children.Add(proposedBox);
        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"{proposal.Summary}\n\nWords: {originalWords} → {proposedWords}  ·  Estimated duration: {FormatDuration(SegmentRenderService.EstimateDuration(original))} → {FormatDuration(SegmentRenderService.EstimateDuration(proposal.ProposedText))}" +
                (proposal.Warnings.Count == 0 ? string.Empty : $"\nWarnings: {string.Join("; ", proposal.Warnings)}"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(comparison);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = allowAccept ? "Review original and proposal" : "Writing critique",
            Content = new ScrollViewer { Content = content, MaxHeight = 620 },
            PrimaryButtonText = allowAccept ? "Accept proposal" : null,
            CloseButtonText = allowAccept ? "Reject" : "Close",
            DefaultButton = allowAccept ? ContentDialogButton.Close : ContentDialogButton.None,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<ChapterOutline?> ReviewOutlineAsync(ChapterOutline outline)
    {
        TextBox title = new() { Header = "Chapter title", Text = outline.Title };
        StackPanel fields = new() { Spacing = 10 };
        fields.Children.Add(title);
        List<(TextBox Title, TextBox Purpose, NumberBox Seconds)> editors = [];
        foreach ((ChapterOutlineSegment segment, int index) in outline.Segments.Select((item, index) => (item, index)))
        {
            TextBox segmentTitle = new() { Header = $"Segment {index + 1} title", Text = segment.Title };
            TextBox purpose = new() { Header = "Purpose", Text = segment.Purpose };
            NumberBox seconds = new() { Header = "Estimated seconds", Value = segment.TargetSeconds, Minimum = 1, Maximum = 600 };
            editors.Add((segmentTitle, purpose, seconds));
            fields.Children.Add(segmentTitle);
            fields.Children.Add(purpose);
            fields.Children.Add(seconds);
        }
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Approve or edit the outline",
            Content = new ScrollViewer { Content = fields, MaxHeight = 580 },
            PrimaryButtonText = "Approve and draft",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return outline with
        {
            Title = title.Text.Trim(),
            Segments = editors.Select(item => new ChapterOutlineSegment(
                item.Title.Text.Trim(), item.Purpose.Text.Trim(), Math.Max(1, (int)item.Seconds.Value))).ToArray(),
        };
    }

    private async Task<bool> PreviewDraftAsync(ChapterDraft draft)
    {
        string preview = string.Join("\n\n", draft.Segments.Select(segment => $"## {segment.Title}\n\n{segment.Text}"));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Preview complete chapter — {draft.Title}",
            Content = new ScrollViewer
            {
                MaxHeight = 620,
                Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap },
            },
            PrimaryButtonText = "Accept and create chapter",
            CloseButtonText = "Reject",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ComboBox CreateWritingProviderPicker(string selectedProvider)
    {
        ComboBox box = new() { Header = "Provider", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (WritingProviderDescriptor provider in App.Services.WritingProviders)
            box.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.ProviderId });
        SetSelectedContent(box, selectedProvider);
        return box;
    }

    private static ListView CreateReferencePicker(VoiceBenchProjectDocument project, IEnumerable<string> selectedIds)
    {
        HashSet<string> selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        ListView list = new() { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 160 };
        foreach (SourceReferenceDocument reference in project.SourceReferences)
        {
            ListViewItem item = new() { Content = reference.Name, Tag = reference.ReferenceId };
            list.Items.Add(item);
            if (selected.Contains(reference.ReferenceId)) list.SelectedItems.Add(item);
        }
        return list;
    }

    private async Task<(ContentDialogResult Result, string Title, string Brief)> ShowNameDialogAsync(
        string dialogTitle, string titleHeader, string briefHeader, string primaryText)
    {
        TextBox title = new() { Header = titleHeader };
        TextBox brief = new() { Header = briefHeader, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(title);
        panel.Children.Add(brief);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = dialogTitle,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        return (result, title.Text.Trim(), brief.Text.Trim());
    }

    private async Task<string?> PromptForTextAsync(
        string dialogTitle, string header, string current, string primaryText)
    {
        TextBox box = new() { Header = header, Text = current };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = dialogTitle,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private RailItem? GetSelectedRailItem() => ChapterTree.SelectedNode?.Content as RailItem;

    private bool EnsureProjectForAuthoring()
    {
        if (App.Services.Session.HasProject) return true;
        ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Chapters are stored inside the active project folder.");
        return false;
    }

    private static int EstimateWritingTokens(params object?[] values)
    {
        int characters = values.Sum(value => value switch
        {
            null => 0,
            string text => text.Length,
            WritingContext context => context.References.Sum(reference => reference.Text.Length) +
                context.ChapterBrief.Length + context.Profile.Audience.Length + context.Profile.Tone.Length + context.Profile.StyleNotes.Length,
            _ => value.ToString()?.Length ?? 0,
        });
        return Math.Max(1, (int)Math.Ceiling(characters / 3d));
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

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
        try
        {
            VoiceBenchProjectDocument updated = await App.Services.ChapterAuthoring.SplitSegmentAsync(
                App.Services.Session.RequireProjectRoot(),
                _segment.SegmentId,
                cursor,
                _segment.SourceSha256);
            await App.Services.Session.RefreshAsync();
            ChapterDocument chapter = updated.Chapters.Single(item => item.ChapterId == _segment.ChapterId);
            int originalIndex = chapter.SegmentOrder.IndexOf(_segment.SegmentId);
            string newSegmentId = chapter.SegmentOrder[originalIndex + 1];
            await LoadProjectAsync(newSegmentId);
            ShowStatus(InfoBarSeverity.Success, "Segment split", "Created a new segment immediately after the original.");
        }
        catch (Exception exception)
        {
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

    private void OpenVoiceLab_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("voice-lab");

    private void OpenAssembly_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("assembly");

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
        TreeViewNode? node = ChapterTree.RootNodes
            .SelectMany(chapter => chapter.Children)
            .FirstOrDefault(candidate => candidate.Content is RailItem item &&
                string.Equals(item.SegmentId, segmentId, StringComparison.Ordinal));
        if (node is not null) ChapterTree.SelectedNode = node;
        _ignoreCompactPicker = true;
        CompactSegmentPicker.SelectedItem = CompactSegmentPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, segmentId, StringComparison.Ordinal));
        _ignoreCompactPicker = false;
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
        WritingProviderException { Kind: WritingProviderFailureKind.Credentials } => $"{exception.Message} Open Settings and add the exact provider key.",
        WritingProviderException { Kind: WritingProviderFailureKind.Authentication } => $"{exception.Message} Replace the provider key in Settings and try again.",
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
