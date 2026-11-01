using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IssueLab.VoiceBench.App.Controls;

public sealed partial class AudioTimelineControl
{
    private async void Markers_Click(object sender,RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Workspace.Timeline is not { } timeline) return;
        ComboBox selected = new() { Header = "Existing marker · leave blank to add", ItemsSource = timeline.Markers, DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        TextBox name = new() { Header = "Name / production guidance", Text = "Marker" };
        NumberBox position = new() { Header = "Timeline position · seconds", Value = Playhead, Minimum = 0 };
        ComboBox kind = new() { Header = "Anchor", ItemsSource = Enum.GetValues<TimelineAnchorKind>(), SelectedItem = TimelineAnchorKind.ProjectTime, HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBox chapter = new() { Header = "Chapter", ItemsSource = App.Services.Session.Project?.Chapters, DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBox segment = new() { Header = "Reference segment", ItemsSource = App.Services.Session.Project?.Segments, DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        NumberBox ordinal = new() { Header = "Source word ordinal · zero based", Value = 0, Minimum = 0 };
        NumberBox offset = new() { Header = "Anchor offset · seconds", Value = 0 };
        CheckBox placed = new() { Content = "Anchor is placed", IsChecked = true };
        selected.SelectionChanged += (_, _) =>
        {
            if (selected.SelectedItem is not TimelineMarker marker) return;
            name.Text = marker.Name; position.Value = marker.PositionSeconds; kind.SelectedItem = marker.Anchor.Kind;
            chapter.SelectedItem = App.Services.Session.Project?.Chapters.FirstOrDefault(item => item.ChapterId == marker.Anchor.ChapterId);
            segment.SelectedItem = App.Services.Session.Project?.Segments.FirstOrDefault(item => item.SegmentId == marker.Anchor.SegmentId);
            ordinal.Value = marker.Anchor.WordOrdinal ?? 0; offset.Value = marker.Anchor.OffsetSeconds; placed.IsChecked = marker.Anchor.Placed;
        };
        StackPanel fields = new() { Spacing = 8 };
        foreach (var field in new FrameworkElement[] { selected,name,position,kind,chapter,segment,ordinal,offset,placed }) fields.Children.Add(field);
        ContentDialog dialog = new() { XamlRoot = XamlRoot, Title = "Markers and cue anchors", Content = new ScrollViewer { Content = fields,MaxHeight = 510 }, PrimaryButtonText = "Save marker", SecondaryButtonText = "Delete selected", CloseButtonText = "Cancel" };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && selected.SelectedItem is TimelineMarker removed)
            await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.DeleteMarker, MarkerId = removed.MarkerId });
        else if (result == ContentDialogResult.Primary)
        {
            TimelineMarker updated = new() { MarkerId = (selected.SelectedItem as TimelineMarker)?.MarkerId ?? Guid.NewGuid(), Name = name.Text, PositionSeconds = position.Value,
                Anchor = new() { Kind = (TimelineAnchorKind)kind.SelectedItem, ChapterId = (chapter.SelectedItem as ChapterDocument)?.ChapterId,
                    SegmentId = (segment.SelectedItem as SegmentDocument)?.SegmentId, WordOrdinal = (int)ordinal.Value, OffsetSeconds = offset.Value, Placed = placed.IsChecked == true } };
            await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.SetMarker, Marker = updated });
        }
    });

    private async void PendingSegments_Click(object sender,RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Workspace.Timeline is not { } timeline) return;
        ComboBox pending = new() { Header = "Unplaced narration", ItemsSource = App.Services.Session.Project?.Segments.Where(segment => timeline.PlaceholderSegmentIds.Contains(segment.SegmentId)).ToArray(), DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBox omitted = new() { Header = "Explicitly omitted narration", ItemsSource = App.Services.Session.Project?.Segments.Where(segment => timeline.OmittedSegmentIds.Contains(segment.SegmentId)).ToArray(), DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        StackPanel panel = new() { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Place a rendered take from the library or explicitly omit its segment. Existing manual placement remains unchanged.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(pending); panel.Children.Add(omitted);
        ContentDialog dialog = new() { XamlRoot = XamlRoot, Title = "Narration placement", Content = panel, PrimaryButtonText = "Omit selected unplaced", SecondaryButtonText = "Include selected omitted", CloseButtonText = "Close" };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && pending.SelectedItem is SegmentDocument omit)
            await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.OmitSegment, SegmentId = omit.SegmentId });
        else if (result == ContentDialogResult.Secondary && omitted.SelectedItem is SegmentDocument include)
            await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.IncludeSegment, SegmentId = include.SegmentId });
    });

    private async void Replacements_Click(object sender,RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Workspace.Timeline is not { } timeline) return;
        ComboBox proposals = new() { Header = "Replacement proposals", ItemsSource = timeline.ReplacementProposals.Select(proposal => new ReplacementChoice(proposal,$"{timeline.Clips.FirstOrDefault(clip => clip.ClipId == proposal.ClipId)?.Name} · {proposal.ProposedRenderId.ToString()[..8]} · {(proposal.CanTransferCuts ? "cuts can transfer" : "full take review")}")).ToArray(), DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
        TextBlock reason = new() { Text = "Listen before accepting. Source pins change only after this explicit review.", TextWrapping = TextWrapping.Wrap };
        proposals.SelectionChanged += (_, _) => { if (proposals.SelectedItem is ReplacementChoice choice) reason.Text = choice.Proposal.Reason ?? "Review the proposed take before accepting it."; };
        Button listen = new() { Content = "▶ Audition proposed take" };
        listen.Click += async (_, _) => await RunAsync(async () =>
        {
            if (proposals.SelectedItem is not ReplacementChoice choice) throw new InvalidOperationException("Select a replacement first.");
            var project = AudioTimelineRules.RegisterRenderAssets(App.Services.Session.RequireProject());
            var asset = project.AudioAssets.Single(item => item.AssetId == choice.Proposal.ProposedAssetId);
            await App.Services.LoadPlaybackFileAsync(App.Services.Session.ResolveProjectPath(asset.RelativePath),"replacement");
            App.Services.AudioPlayback!.Play();
        });
        StackPanel panel = new() { Spacing = 12 }; panel.Children.Add(proposals); panel.Children.Add(reason); panel.Children.Add(listen);
        ContentDialog dialog = new() { XamlRoot = XamlRoot, Title = "Review replacement takes", Content = panel, PrimaryButtonText = "Accept reviewed replacement", SecondaryButtonText = "Keep current take", CloseButtonText = "Close" };
        var result = await dialog.ShowAsync();
        if (proposals.SelectedItem is not ReplacementChoice selected || result == ContentDialogResult.None) return;
        await Workspace.EditAsync(new TimelineEdit { Operation = result == ContentDialogResult.Primary ? TimelineEditOperation.AcceptReplacement : TimelineEditOperation.RejectReplacement, ProposalId = selected.Proposal.ProposalId, ConfirmedReview = result == ContentDialogResult.Primary });
    });

    private sealed record ReplacementChoice(TakeReplacementProposal Proposal,string Label);
    private async void AutoLayout_Click(object sender,RoutedEventArgs e) => await RunAsync(() => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.SetLayoutMode, LayoutMode = TimelineLayoutMode.Automatic }));
}
