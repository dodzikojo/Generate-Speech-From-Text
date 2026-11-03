using System.ComponentModel;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace IssueLab.VoiceBench.App.Views;

/// <summary>A take row in the Workbench Takes tab.</summary>
public sealed class TakeListItem(TakeRow row, Brush toneBrush) : INotifyPropertyChanged
{
    private bool _isPlaying;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TakeRow Row { get; } = row;

    public object Tag => Row.RenderId;

    public string Title => Row.Title;

    public string StatusText => Row.StatusText;

    public string DurationText => Row.DurationText;

    public string DetailText => string.IsNullOrEmpty(Row.CreatedText) ? Row.VoiceLine : $"{Row.VoiceLine}  ·  {Row.CreatedText}";

    public Brush ToneBrush { get; } = toneBrush;

    public bool CanApprove => Row.CanApprove;

    public Visibility ApproveVisibility => Row.CanApprove ? Visibility.Visible : Visibility.Collapsed;

    public string AutomationName => Row.AutomationName;

    public string PlayAutomationName => $"Play or pause {Row.Title}";

    public string PlayGlyph => _isPlaying ? "" : "";

    private bool _isCurrent;

    /// <summary>Accent border for the take loaded in the take bar.</summary>
    public Brush CardBorderBrush => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[_isCurrent ? "ActiveSignalBrush" : "DividerBrush"];

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayGlyph)));
        }
    }
}

public sealed partial class WorkbenchView
{
    private const string InspectorTabSetting = "Workbench.InspectorTab";
    private const string TimelineDockVisibilitySetting = "Workbench.ShowTimelineDock";
    private bool _syncingTakeSelection;
    private bool _selectingInspectorTab;

    private void LoadInspectorTabPreference()
    {
        string tab = ApplicationData.Current.LocalSettings.Values[InspectorTabSetting] as string ?? "voice";
        ShowInspectorTab(tab, persist: false);
    }

    private void InspectorTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_selectingInspectorTab || sender.SelectedItem is not SelectorBarItem { Tag: string tab }) return;
        ShowInspectorTab(tab, persist: true);
    }

    private void ShowInspectorTab(string tab, bool persist)
    {
        if (tab is not ("voice" or "takes" or "write")) tab = "voice";
        VoiceDirectorPane.Visibility = tab == "voice" ? Visibility.Visible : Visibility.Collapsed;
        TakesPane.Visibility = tab == "takes" ? Visibility.Visible : Visibility.Collapsed;
        WritingAssistantPane.Visibility = tab == "write" ? Visibility.Visible : Visibility.Collapsed;
        SelectorBarItem item = tab switch { "takes" => TakesTabItem, "write" => WriteTabItem, _ => VoiceTabItem };
        if (!ReferenceEquals(InspectorTabs.SelectedItem, item))
        {
            _selectingInspectorTab = true;
            try { InspectorTabs.SelectedItem = item; }
            finally { _selectingInspectorTab = false; }
        }

        if (persist) ApplicationData.Current.LocalSettings.Values[InspectorTabSetting] = tab;
    }

    /// <summary>Opens the inspector on a tab, showing the inspector first if the operator had hidden it.</summary>
    private void RevealInspectorTab(string tab)
    {
        if (DirectorToggle.IsChecked != true)
        {
            DirectorToggle.IsChecked = true;
            SaveWorkspacePanelPreferences();
            ApplyWorkspacePanelLayout(ActualWidth, ActualHeight);
        }

        ShowInspectorTab(tab, persist: true);
    }

    private void ShowTakesTab_Click(object sender, RoutedEventArgs e) => RevealInspectorTab("takes");

    private void PopulateTakeList(SegmentDocument? segment)
    {
        List<TakeListItem> items = segment is null
            ? []
            : TakeListPresentation.Build(segment).Select(row => new TakeListItem(row, ToneBrush(row.Tone))).ToList();
        _syncingTakeSelection = true;
        try
        {
            TakeList.ItemsSource = items;
            TakeList.SelectedItem = items.FirstOrDefault(item => item.Row.RenderId == GetSelectedRender()?.RenderId);
        }
        finally { _syncingTakeSelection = false; }

        TakesEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TakeList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateTakeRowPlayStates();
    }

    private static Brush ToneBrush(TakeTone tone) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[tone switch
    {
        TakeTone.Approved => "ApprovalBrush",
        TakeTone.Current => "ActiveSignalBrush",
        TakeTone.Outdated => "PickupBrush",
        TakeTone.Failed => "FailureBrush",
        _ => "MutedTextBrush",
    }];

    private void SyncTakeListSelection()
    {
        if (TakeList.ItemsSource is not IEnumerable<TakeListItem> items) return;
        Guid? selected = GetSelectedRender()?.RenderId;
        _syncingTakeSelection = true;
        try { TakeList.SelectedItem = items.FirstOrDefault(item => item.Row.RenderId == selected); }
        finally { _syncingTakeSelection = false; }
        UpdateTakeRowPlayStates();
    }

    private void UpdateTakeRowPlayStates()
    {
        if (TakeList.ItemsSource is not IEnumerable<TakeListItem> items) return;
        Guid? selected = GetSelectedRender()?.RenderId;
        bool playing = TakeTransport.IsPlaying;
        foreach (TakeListItem item in items)
        {
            item.IsCurrent = item.Row.RenderId == selected;
            item.IsPlaying = playing && item.IsCurrent;
        }
    }

    private void TakeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTakeSelection || TakeList.SelectedItem is not TakeListItem item) return;
        SelectTake(item.Row.RenderId);
    }

    /// <summary>Selects a take in the take bar; returns true when the selection changed.</summary>
    private bool SelectTake(Guid renderId)
    {
        ComboBoxItem? match = TakeSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(entry => entry.Tag is Guid id && id == renderId);
        if (match is null || ReferenceEquals(TakeSelector.SelectedItem, match)) return false;
        TakeSelector.SelectedItem = match;
        return true;
    }

    private static Guid? TagRenderId(object sender) => sender is FrameworkElement { Tag: Guid id } ? id : null;

    private RenderRecord? FindRender(Guid renderId) =>
        _segment?.RenderHistory.SingleOrDefault(item => item.RenderId == renderId);

    private async void TakeRowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (TagRenderId(sender) is not Guid renderId) return;
        bool alreadySelected = GetSelectedRender()?.RenderId == renderId;
        if (alreadySelected && TakeTransport.IsPlaying)
        {
            TakeTransport.Pause();
            return;
        }

        if (!alreadySelected && SelectTake(renderId)) await UpdateTransportAsync();
        await PlaySelectedTakeAsync();
    }

    private async void TakeRowApprove_Click(object sender, RoutedEventArgs e)
    {
        if (TagRenderId(sender) is not Guid renderId) return;
        if (SelectTake(renderId)) await UpdateTransportAsync();
        Approve_Click(sender, e);
    }

    private async void TakeRowReveal_Click(object sender, RoutedEventArgs e)
    {
        if (TagRenderId(sender) is Guid renderId && FindRender(renderId) is RenderRecord render)
            await RevealTakeAsync(render);
    }

    private async void TakeRowOpen_Click(object sender, RoutedEventArgs e)
    {
        if (TagRenderId(sender) is Guid renderId && FindRender(renderId) is RenderRecord render)
            await OpenTakeAsync(render);
    }

    private void TakeRowCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (TagRenderId(sender) is Guid renderId && FindRender(renderId) is RenderRecord render)
            CopyTakePath(render);
    }

    private async void RevealTake_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedRender() is RenderRecord render) await RevealTakeAsync(render);
    }

    private async void OpenTake_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedRender() is RenderRecord render) await OpenTakeAsync(render);
    }

    private void CopyTakePath_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedRender() is RenderRecord render) CopyTakePath(render);
    }

    private string? ResolveTakeAudioPath(RenderRecord render)
    {
        if (!App.Services.Session.HasProject || ProjectLocations.TakeAudioRelativePath(render) is not string relative) return null;
        return ProjectLocations.ResolveInsideProject(App.Services.Session.RequireProjectRoot(), relative);
    }

    private async Task RevealTakeAsync(RenderRecord render)
    {
        try
        {
            string path = ResolveTakeAudioPath(render)
                ?? throw new InvalidOperationException($"Take {render.TakeNumber:00} has no audio file recorded.");
            await ShellLauncher.RevealFileAsync(path);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Take could not be shown", exception.Message);
        }
    }

    private async Task OpenTakeAsync(RenderRecord render)
    {
        try
        {
            string path = ResolveTakeAudioPath(render)
                ?? throw new InvalidOperationException($"Take {render.TakeNumber:00} has no audio file recorded.");
            await ShellLauncher.OpenFileAsync(path);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Take could not be opened", exception.Message);
        }
    }

    private void CopyTakePath(RenderRecord render)
    {
        try
        {
            string path = ResolveTakeAudioPath(render)
                ?? throw new InvalidOperationException($"Take {render.TakeNumber:00} has no audio file recorded.");
            ShellLauncher.CopyPath(path);
            ShowStatus(InfoBarSeverity.Success, $"Take {render.TakeNumber:00} path copied", path);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Path could not be copied", exception.Message);
        }
    }

    /// <summary>The folder holding this segment's takes: taken from a recorded take, else the conventional location.</summary>
    private string ResolveSegmentTakeFolder(SegmentDocument segment)
    {
        string root = App.Services.Session.RequireProjectRoot();
        string? recorded = segment.RenderHistory
            .OrderByDescending(item => item.TakeNumber)
            .Select(ProjectLocations.TakeAudioRelativePath)
            .FirstOrDefault(path => path is not null);
        if (recorded is not null)
        {
            string folder = Path.GetDirectoryName(ProjectLocations.ResolveInsideProject(root, recorded))!;
            if (Directory.Exists(folder)) return folder;
        }

        string conventional = ProjectLocations.ResolveInsideProject(root, ProjectLocations.SegmentTakeFolderRelativePath(segment.SegmentId));
        return Directory.Exists(conventional) ? conventional : ProjectLocations.Resolve(root, ProjectFolder.GeneratedAudio);
    }

    private async Task OpenSegmentTakeFolderAsync(SegmentDocument? segment)
    {
        try
        {
            if (segment is null) throw new InvalidOperationException("Select a segment first.");
            await ShellLauncher.OpenFolderAsync(ResolveSegmentTakeFolder(segment), create: true);
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Takes folder could not be opened", exception.Message);
        }
    }

    private async void OpenTakesFolder_Click(object sender, RoutedEventArgs e) => await OpenSegmentTakeFolderAsync(_segment);

    private async void ShowSelectedTakesFolder_Click(object sender, RoutedEventArgs e)
    {
        RailItem? selected = GetSelectedRailItem();
        SegmentDocument? segment = selected?.SegmentId is string id ? _project?.Segments.FirstOrDefault(item => item.SegmentId == id) : _segment;
        await OpenSegmentTakeFolderAsync(segment);
    }

    private async void OpenGeneratedAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        try { await ShellLauncher.OpenProjectFolderAsync(ProjectFolder.GeneratedAudio); }
        catch (Exception exception) { ShowStatus(InfoBarSeverity.Error, "Generated audio folder could not be opened", exception.Message); }
    }

    private async void RevealScript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_segment is null) throw new InvalidOperationException("Select a segment first.");
            await ShellLauncher.RevealFileAsync(App.Services.Session.ResolveProjectPath(_segment.ScriptRelativePath));
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Script could not be shown", exception.Message);
        }
    }

    private void NextTake_Click(object sender, RoutedEventArgs e)
    {
        if (TakeSelector.SelectedIndex > 0) TakeSelector.SelectedIndex--;
    }

    private async void PlayTakeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ListenToTakeButton.IsEnabled) return;
        args.Handled = true;
        await PlaySelectedTakeAsync(toggle: true);
    }

    private void ApplyTimelineDockVisibility()
    {
        TimelineDockHost.Visibility = TimelineDockToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Places the take bar's groups on one row when there is room, otherwise across two or three rows.</summary>
    private void ApplyTakeBarLayout(double width)
    {
        static void Place(FrameworkElement element, int row, int column, int span)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
        }

        if (width >= 1320)
        {
            Place(TakeSelectionControls, 0, 0, 1);
            Place(TakeTransport, 0, 1, 1);
            Place(TakeReviewContext, 0, 2, 1);
            Place(TakeDecisionGroup, 0, 3, 1);
            Place(AssemblyHandoff, 0, 4, 1);
            TakeDecisionGroup.BorderThickness = new Thickness(1, 0, 0, 0);
            TakeDecisionGroup.Padding = new Thickness(12, 0, 0, 0);
        }
        else if (width >= 860)
        {
            // Controls on the first row, a full-width seek bar beneath them.
            Place(TakeSelectionControls, 0, 0, 1);
            Place(TakeReviewContext, 0, 2, 1);
            Place(TakeDecisionGroup, 0, 3, 1);
            Place(AssemblyHandoff, 0, 4, 1);
            Place(TakeTransport, 1, 0, 5);
            TakeDecisionGroup.BorderThickness = new Thickness(1, 0, 0, 0);
            TakeDecisionGroup.Padding = new Thickness(12, 0, 0, 0);
        }
        else
        {
            Place(TakeSelectionControls, 0, 0, 4);
            Place(AssemblyHandoff, 0, 4, 1);
            Place(TakeTransport, 1, 0, 5);
            Place(TakeReviewContext, 2, 0, 3);
            Place(TakeDecisionGroup, 2, 3, 2);
            TakeDecisionGroup.BorderThickness = new Thickness(0);
            TakeDecisionGroup.Padding = new Thickness(0);
        }

        TakeDecisionGroup.HorizontalAlignment = width >= 860 ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        TakeReviewContext.HorizontalAlignment = width >= 860 && width < 1320 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
