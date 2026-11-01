using System.Globalization;
using IssueLab.VoiceBench.App.Services;
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
        double step = TimelinePresentation.GridStep(Workspace.VisibleSeconds);
        foreach (double second in TimelinePresentation.RulerTicks(Workspace.ViewStart,Workspace.VisibleSeconds))
        {
            double x = (second-Workspace.ViewStart)/Workspace.VisibleSeconds*width;
            TextBlock label = new() { Text = TimeSpan.FromSeconds(second).ToString(step < 1 ? @"mm\:ss\.fff" : second >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss"),
                FontSize = 11, FontFamily = (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["UtilityFont"], Foreground = Brush("MutedTextBrush"),
                Width = 76, TextAlignment = TextAlignment.Center };
            AutomationProperties.SetName(label,$"Timeline {second.ToString("0.###",CultureInfo.InvariantCulture)} seconds");
            Canvas.SetLeft(label,Math.Clamp(x-38,0,Math.Max(0,width-76))); Canvas.SetTop(label,0);
            TimeTicksCanvas.Children.Add(label);
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
