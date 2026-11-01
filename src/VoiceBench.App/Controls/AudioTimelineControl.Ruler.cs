using System.Globalization;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage;

namespace IssueLab.VoiceBench.App.Controls;

public sealed partial class AudioTimelineControl
{
    private void TimelineCanvasHost_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        if (TrackRows is null || TimeTicksCanvas is null) return;
        if (TrackRows.Items.Count == 0) _timelineWidth = Math.Max(1,e.NewSize.Width-(_compact ? 120 : 155));
        RefreshTimeAxis();
    }

    private void RefreshTimeAxis()
    {
        if (TimeTicksCanvas is null || CueLaneCanvas is null) return;
        double width = Math.Max(1,_timelineWidth);
        TimeTicksCanvas.Width = width;
        CueLaneCanvas.Width = width;
        CueLaneCanvas.Clip = new RectangleGeometry { Rect = new Rect(0,0,width,32) };
        TimeTicksCanvas.Children.Clear(); CueLaneCanvas.Children.Clear();
        RefreshChapterRegions(width);
        RefreshPendingNarration();
        double step = TimelinePresentation.GridStep(Workspace.VisibleSeconds,width);
        double previousLabelRight = -8;
        foreach (double second in TimelinePresentation.RulerTicks(Workspace.ViewStart,Workspace.VisibleSeconds,width))
        {
            double x = (second-Workspace.ViewStart)/Workspace.VisibleSeconds*width;
            double left = Math.Clamp(x-38,0,Math.Max(0,width-76));
            TextBlock label = new() { Text = TimeSpan.FromSeconds(second).ToString(step < 1 ? @"mm\:ss\.fff" : second >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss"),
                FontSize = 11, FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["UtilityFont"], Foreground = Brush("MutedTextBrush"),
                Width = 76, TextAlignment = TextAlignment.Center };
            AutomationProperties.SetName(label,$"Timeline {second.ToString("0.###",CultureInfo.InvariantCulture)} seconds");
            if (left >= previousLabelRight+8)
            {
                Canvas.SetLeft(label,left); Canvas.SetTop(label,0);
                TimeTicksCanvas.Children.Add(label);
                previousLabelRight = left+76;
            }
            Border tick = new() { Width = 1,Height = 7,Background = Brush("DividerBrush") };
            Canvas.SetLeft(tick,x); Canvas.SetTop(tick,21); TimeTicksCanvas.Children.Add(tick);
        }
        int unplaced = Workspace.CuePlacements.Count(cue => !cue.PositionSeconds.HasValue);
        CuePlacementStatus.Content = $"CUES · {unplaced} unplaced";
        AutomationProperties.SetName(CuePlacementStatus,$"{Workspace.CuePlacements.Count} production cues, {unplaced} unplaced. Open cue placement details.");
        foreach (var cue in Workspace.CuePlacements.Where(cue => cue.PositionSeconds >= Workspace.ViewStart && cue.PositionSeconds <= Workspace.ViewStart+Workspace.VisibleSeconds))
            AddCueChip(cue.Type,cue.Content,cue.PositionSeconds!.Value,$"{cue.Status} · source word {cue.SourceWordOrdinal} · {cue.SegmentId}");
        if (Workspace.Timeline is { } timeline)
            foreach (var marker in timeline.Markers.Where(marker => marker.Anchor.Placed && marker.PositionSeconds >= Workspace.ViewStart && marker.PositionSeconds <= Workspace.ViewStart+Workspace.VisibleSeconds))
                AddCueChip("marker",marker.Name,marker.PositionSeconds,"Placed timeline marker");
    }

    private void RefreshChapterRegions(double width)
    {
        ChapterRegionCanvas.Width = width;
        ChapterRegionCanvas.Clip = new RectangleGeometry { Rect = new Rect(0,0,width,32) };
        ChapterRegionCanvas.Children.Clear();
        ChapterRegionLane.Visibility = Workspace.Scope == AudioTimelineScope.Compilation ? Visibility.Visible : Visibility.Collapsed;
        foreach (var region in Workspace.ChapterRegions)
        {
            if (TimelinePresentation.ClipChapterRegion(region,Workspace.ViewStart,Workspace.VisibleSeconds,width) is not { } bounds) continue;
            string title = string.IsNullOrWhiteSpace(region.Title) ? region.ChapterId : region.Title;
            string duration = TimelinePresentation.Time(region.DurationSeconds);
            TextBlock label = new() { Text = $"{title} · {duration}",FontSize = 11,TextTrimming = TextTrimming.CharacterEllipsis };
            Button chapter = new() { Content = label,Width = bounds.Width,MinWidth = 0,Height = 28,MinHeight = 0,Padding = new Thickness(5,1,5,1),
                Background = Brush("PanelRaisedBrush"),Foreground = Brush("ReadingTextBrush"),BorderBrush = Brush("ActiveSignalBrush"),BorderThickness = new Thickness(1),HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(chapter,$"Chapter {title}, start {TimelinePresentation.Time(region.StartSeconds)}, duration {duration}. Open chapter narration arrangement.");
            ToolTipService.SetToolTip(chapter,$"{title} · {duration}\nStarts {TimelinePresentation.Time(region.StartSeconds)}. Open this chapter to edit narration; compilation overlays remain here.");
            Canvas.SetLeft(chapter,bounds.Left); Canvas.SetTop(chapter,2);
            chapter.Click += async (_, _) => await RunAsync(async () =>
            {
                Workspace.ViewStart = 0;
                await Workspace.LoadAsync(AudioTimelineScope.Chapter,region.ChapterId);
                Workspace.PlayheadSeconds = 0;
                Workspace.VisibleSeconds = Math.Max(2,Math.Min(Workspace.VisibleSeconds,Workspace.ResolvedDurationSeconds));
                Workspace.Notify();
            });
            ChapterRegionCanvas.Children.Add(chapter);
        }
    }

    private void RefreshPendingNarration()
    {
        PendingNarrationCards.Children.Clear();
        PendingNarrationTray.Visibility = Workspace.PendingNarration.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in Workspace.PendingNarration)
        {
            string title = string.IsNullOrWhiteSpace(item.Title) ? item.SegmentId : item.Title;
            string text = $"{title} · ~{TimelinePresentation.Time(item.EstimatedDurationSeconds)} · {item.Status}";
            TextBlock label = new() { Text = text,FontSize = 11,Foreground = Brush("ReadingTextBrush"),TextTrimming = TextTrimming.CharacterEllipsis,MaxWidth = 420 };
            AutomationProperties.SetName(label,text+". Spoken estimate only; no audio position is assigned.");
            ToolTipService.SetToolTip(label,text+"\nEstimate uses spoken narration only. Render or place a take, or explicitly omit this segment.");
            PendingNarrationCards.Children.Add(new Border { Child = label,Padding = new Thickness(6,5,6,5),Background = Brush("PanelRaisedBrush"),BorderBrush = Brush("PickupBrush"),BorderThickness = new Thickness(1) });
        }
    }

    private void FitArrangement_Click(object sender,RoutedEventArgs e)
    {
        Workspace.ViewStart = 0;
        Workspace.VisibleSeconds = Math.Max(2,Workspace.ResolvedDurationSeconds > 0 ? Workspace.ResolvedDurationSeconds : TimelinePresentation.Duration(Workspace.Timeline));
        Workspace.Notify();
    }

    private void AddCueChip(string type,string content,double seconds,string source)
    {
        double x = (seconds-Workspace.ViewStart)/Workspace.VisibleSeconds*_timelineWidth;
        Button cue = new() { Content = $"@{type}: {content}",FontSize = 11,Height = 28,MinHeight = 0,Width = Math.Clamp((content.Length+type.Length)*6+20,72,190),
            Padding = new Thickness(5,1,4,1),Background = Brush("PanelRaisedBrush"),Foreground = Brush("ReadingTextBrush"),BorderBrush = Brush("PickupBrush"),BorderThickness = new Thickness(2,0,0,0),
            HorizontalContentAlignment = HorizontalAlignment.Left };
        Canvas.SetLeft(cue,x); Canvas.SetTop(cue,2);
        AutomationProperties.SetName(cue,$"{type} cue at {TimelinePresentation.Time(seconds)}: {content}. {source}");
        ToolTipService.SetToolTip(cue,$"{TimelinePresentation.Time(seconds)} · @{type}: {content}\n{source}");
        cue.Click += async (_, _) => await RunAsync(async () =>
        {
            Workspace.PlayheadSeconds = seconds;
            if (App.Services.PlaybackTarget == "mix" && App.Services.AudioPlayback is { } playback) await playback.SeekAsync(TimeSpan.FromSeconds(seconds));
            Workspace.Notify();
        });
        CueLaneCanvas.Children.Add(cue);
    }

    private async void CuePlacementStatus_Click(object sender,RoutedEventArgs e) => await RunAsync(async () =>
    {
        string[] rows = Workspace.CuePlacements.OrderBy(cue => cue.PositionSeconds ?? double.MaxValue)
            .Select(cue => $"{(cue.PositionSeconds.HasValue ? TimelinePresentation.Time(cue.PositionSeconds.Value) : cue.Status)} · @{cue.Type}: {cue.Content}\n{cue.SegmentId} · source word {cue.SourceWordOrdinal}").ToArray();
        ListView list = new() { ItemsSource = rows.Length > 0 ? rows : ["No production cues in this arrangement."],FontSize = 12,SelectionMode = ListViewSelectionMode.None,MaxHeight = 430 };
        ContentDialog dialog = new() { XamlRoot = XamlRoot,Title = "Production cue placement",Content = list,CloseButtonText = "Close" };
        await dialog.ShowAsync();
    });

    private void TrackHeight_Click(object sender,RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string value }) return;
        ApplicationData.Current.LocalSettings.Values[LayoutSettingPrefix + ".TrackHeight"] = double.Parse(value,CultureInfo.InvariantCulture);
        _drawnStart = -1;
        Refresh();
    }
}
