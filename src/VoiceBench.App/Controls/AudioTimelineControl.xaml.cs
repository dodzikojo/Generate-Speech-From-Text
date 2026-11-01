using System.Globalization;
using System.Numerics;
using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace IssueLab.VoiceBench.App.Controls;

public sealed partial class AudioTimelineControl : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<CanvasControl> _canvases = [];
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private readonly Dictionary<Guid,Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> _soloButtons = [];
    private AudioTimelineWorkspace Workspace => App.Services.AudioWorkspace;
    private bool _refreshing;
    private bool _compact;
    private bool _collapsed;
    private long _drawnRevision = -1;
    private Guid _drawnTimeline;
    private double _drawnStart = -1;
    private double _drawnVisible;
    private Guid? _dragClip;
    private double _dragStartX;
    private double _dragDelta;
    private int _dragEdge;
    private double _timelineWidth = 400;
    private Guid? _inspectedClip;
    private long _inspectedRevision = -1;
    private CancellationTokenSource _peakCancellation = new();
    private readonly SemaphoreSlim _peakSlots = new(2,2);
    private readonly Dictionary<Guid, WaveStrip> _peakStrips = [];
    private sealed record WaveStrip(double TimelineStart, double TimelineDuration, float[] Minimum, float[] Maximum);
    private sealed record ScopeChoice(string Label, AudioTimelineScope Scope, string? ChapterId);

    public AudioTimelineControl()
    {
        InitializeComponent();
        Loaded += Control_Loaded;
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => UpdateTransport();
        SizeChanged += (_, _) => ApplyLayout();
        App.Services.AudioWorkspace.Changed += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        App.Services.Session.ProjectChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshScopes);
    }

    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            CollapseToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            LibraryToggle.IsChecked = !value && ReadSetting("Audio.Library", true);
            InspectorToggle.IsChecked = !value && ReadSetting("Audio.Inspector", true);
            ApplyLayout();
        }
    }

    private async void Control_Loaded(object sender, RoutedEventArgs e)
    {
        LibraryToggle.IsChecked = !_compact && ReadSetting("Audio.Library", true);
        InspectorToggle.IsChecked = !_compact && ReadSetting("Audio.Inspector", true);
        RefreshScopes();
        await RunAsync(() => Workspace.LoadAsync());
        _timer.Start();
    }

    private static bool ReadSetting(string key, bool fallback) => ApplicationData.Current.LocalSettings.Values[key] is bool value ? value : fallback;

    private void ApplyLayout()
    {
        bool narrow = ActualWidth < 820;
        LibraryPanel.Visibility = !_collapsed && LibraryToggle.IsChecked == true && !narrow ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = !_collapsed && InspectorToggle.IsChecked == true && ActualWidth >= 700 ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.Width = new GridLength(LibraryPanel.Visibility == Visibility.Visible ? 185 : 0);
        InspectorColumn.Width = new GridLength(InspectorPanel.Visibility == Visibility.Visible ? 220 : 0);
        ArrangementBody.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_compact) ArrangementBody.MaxHeight = ReadDockHeight();
        TimelineRoot.RowDefinitions[1].Height = _collapsed ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        _drawnStart = -1;
        Refresh();
    }

    private static double ReadDockHeight() => ApplicationData.Current.LocalSettings.Values["Audio.DockHeight"] is double height ? Math.Clamp(height, 110, 400) : 170;

    public void ResizeDock(double delta)
    {
        double height = Math.Clamp(ReadDockHeight() + delta, 110, 400);
        ApplicationData.Current.LocalSettings.Values["Audio.DockHeight"] = height;
        ArrangementBody.MaxHeight = height;
        ArrangementBody.Height = height;
    }

    private void Panels_Click(object sender, RoutedEventArgs e)
    {
        if (!_compact)
        {
            ApplicationData.Current.LocalSettings.Values["Audio.Library"] = LibraryToggle.IsChecked == true;
            ApplicationData.Current.LocalSettings.Values["Audio.Inspector"] = InspectorToggle.IsChecked == true;
        }
        ApplyLayout();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) { _collapsed = CollapseToggle.IsChecked == true; ApplyLayout(); }

    private void RefreshScopes()
    {
        _refreshing = true;
        List<ScopeChoice> choices = [new("Compilation · continuous overlays", AudioTimelineScope.Compilation, null)];
        if (App.Services.Session.Project is { } project)
            choices.AddRange(project.Chapters.Where(chapter => chapter.Status == ChapterStatus.Active).Select(chapter => new ScopeChoice(chapter.Title, AudioTimelineScope.Chapter, chapter.ChapterId)));
        ScopePicker.DisplayMemberPath = nameof(ScopeChoice.Label);
        ScopePicker.ItemsSource = choices;
        ScopePicker.SelectedItem = choices.FirstOrDefault(choice => choice.Scope == Workspace.Scope && choice.ChapterId == Workspace.ChapterId) ?? choices[0];
        _refreshing = false;
        Refresh();
    }

    private async void ScopePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && ScopePicker.SelectedItem is ScopeChoice choice)
            await RunAsync(() => Workspace.LoadAsync(choice.Scope, choice.ChapterId));
    }

    private void Refresh()
    {
        if (_refreshing || TimelineNotice is null) return;
        _refreshing = true;
        TimelineNotice.Text = Workspace.Timeline is { } summary ? $"{summary.Tracks.Count} tracks · {summary.Clips.Count} clips · {summary.PlaceholderSegmentIds.Count} unplaced · {summary.ReplacementProposals.Count} replacements · {Workspace.Notice}" : Workspace.Notice;
        ToolTipService.SetToolTip(TimelineNotice, Workspace.Notice);
        var timeline = Workspace.Timeline;
        Guid? selectedAsset = (AssetsList.SelectedItem as AudioAssetRecord)?.AssetId;
        AssetsList.ItemsSource = App.Services.Session.Project is { } project ? AudioTimelineRules.RegisterRenderAssets(project).AudioAssets : null;
        AssetsList.SelectedItem = (AssetsList.ItemsSource as IEnumerable<AudioAssetRecord>)?.FirstOrDefault(asset => asset.AssetId == selectedAsset);
        if (_drawnRevision != timeline?.Revision || _drawnTimeline != timeline?.TimelineId) ClipTrack.ItemsSource = timeline?.Tracks.OrderBy(track => track.Order).ToArray();
        Zoom.Value = Workspace.VisibleSeconds;
        ScrollTime.Maximum = Math.Max(1, TimelinePresentation.Duration(timeline) - Workspace.VisibleSeconds + 2);
        ScrollTime.Value = Math.Clamp(Workspace.ViewStart, 0, ScrollTime.Maximum);
        Ruler.Maximum = Math.Max(1, TimelinePresentation.Duration(timeline));
        foreach (var solo in _soloButtons) solo.Value.IsChecked = Workspace.MonitorSoloTrackIds.Contains(solo.Key);
        if (timeline is not null && (_drawnRevision != timeline.Revision || _drawnTimeline != timeline.TimelineId || _drawnStart != Workspace.ViewStart || _drawnVisible != Workspace.VisibleSeconds))
        {
            BuildRows(timeline);
            _drawnRevision = timeline.Revision; _drawnTimeline = timeline.TimelineId;
            _drawnStart = Workspace.ViewStart; _drawnVisible = Workspace.VisibleSeconds;
        }
        else if (timeline is null) TrackRows.Items.Clear();
        UpdateInspector();
        InvalidateWaves();
        _refreshing = false;
    }

    private void BuildRows(AudioTimelineDocument timeline)
    {
        _peakCancellation.Cancel(); _peakCancellation.Dispose(); _peakCancellation = new();
        _peakStrips.Clear();
        foreach (CanvasControl canvas in _canvases) canvas.RemoveFromVisualTree();
        _canvases.Clear(); _soloButtons.Clear(); TrackRows.Items.Clear();
        foreach (AudioTrackDocument track in timeline.Tracks.OrderBy(track => track.Order))
        {
            Grid row = new() { Height = _compact ? 62 : 88, BorderBrush = Brush("DividerBrush"), BorderThickness = new Thickness(0,0,0,1) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(_compact ? 120 : 155) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            StackPanel header = new() { Spacing = 3, Padding = new Thickness(5) };
            Button title = new() { Content = track.Name, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2), FontSize = 12 };
            title.Click += (_, _) => { Workspace.SelectedTrackId = track.TrackId; Workspace.Notify(); };
            title.ContextFlyout = TrackMenu(track);
            header.Children.Add(title);
            StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
            AddTrackToggle(toggles, track, "M", "Mute", track.Muted, value => track with { Muted = value });
            Microsoft.UI.Xaml.Controls.Primitives.ToggleButton solo = new() { Content = "S", IsChecked = Workspace.MonitorSoloTrackIds.Contains(track.TrackId), Width = 28, MinWidth = 0, Height = 24, MinHeight = 0, Padding = new Thickness(0), FontSize = 10 };
            AutomationProperties.SetName(solo,$"Monitor solo track {track.Name}");
            solo.Click += async (_, _) => await RunAsync(async () =>
            {
                if (solo.IsChecked == true) Workspace.MonitorSoloTrackIds.Add(track.TrackId); else Workspace.MonitorSoloTrackIds.Remove(track.TrackId);
                if (App.Services.PlaybackTarget == "mix" && App.Services.AudioPlayback is { } playback) await playback.SetSoloTracksAsync(Workspace.MonitorSoloTrackIds);
                Workspace.Notify();
            });
            toggles.Children.Add(solo);
            _soloButtons[track.TrackId] = solo;
            AddTrackToggle(toggles, track, "L", "Lock", track.Locked, value => track with { Locked = value });
            header.Children.Add(toggles); row.Children.Add(header);
            Grid lane = new() { Background = Brush("BaseBrush") };
            Grid.SetColumn(lane, 1); row.Children.Add(lane);
            CanvasControl waves = new();
            waves.Draw += (_, args) => DrawTrack(args, track, lane.ActualWidth, row.Height);
            _canvases.Add(waves); lane.Children.Add(waves);
            Canvas hits = new(); lane.Children.Add(hits);
            lane.SizeChanged += (_, args) =>
            {
                _timelineWidth = Math.Max(1, args.NewSize.Width);
                lane.Clip = new RectangleGeometry { Rect = new Rect(0,0,args.NewSize.Width,args.NewSize.Height) };
                PositionClips(hits, track, _timelineWidth);
                waves.Invalidate();
            };
            PositionClips(hits, track, _timelineWidth);
            TrackRows.Items.Add(row);
        }
        QueueVisiblePeaks(timeline,_peakCancellation.Token);
    }

    private static Brush Brush(string key) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private void AddTrackToggle(StackPanel panel, AudioTrackDocument track, string caption, string name, bool initial, Func<bool, AudioTrackDocument> update)
    {
        Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button = new() { Content = caption, IsChecked = initial, Width = 28, MinWidth = 0, Height = 24, MinHeight = 0, Padding = new Thickness(0), FontSize = 10 };
        AutomationProperties.SetName(button, $"{name} track {track.Name}");
        button.Click += async (_, _) => await RunAsync(() => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.UpdateTrack, TrackId = track.TrackId, Track = update(button.IsChecked == true) }));
        panel.Children.Add(button);
    }

    private MenuFlyout TrackMenu(AudioTrackDocument track)
    {
        MenuFlyout menu = new();
        void Item(string text, Func<Task> action) { MenuFlyoutItem item = new() { Text = text }; item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item); }
        Item("Rename / mix track…", () => EditTrackAsync(track));
        Item("Move track up", () => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.ReorderTrack, TrackId = track.TrackId, Order = Math.Max(0, track.Order - 1) }));
        Item("Move track down", () => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.ReorderTrack, TrackId = track.TrackId, Order = track.Order + 1 }));
        Item("Delete track and its clips", () => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.DeleteTrack, TrackId = track.TrackId }));
        return menu;
    }

    private async Task EditTrackAsync(AudioTrackDocument track)
    {
        TextBox name = new() { Header = "Track name", Text = track.Name };
        NumberBox gain = new() { Header = "Track gain · dB", Value = track.GainDb, Minimum = -96, Maximum = 24 };
        NumberBox pan = new() { Header = "Pan / balance", Value = track.Pan, Minimum = -1, Maximum = 1 };
        CheckBox duck = new() { Content = "Duck beneath narration · 8 dB / 50 ms / 300 ms", IsChecked = track.DuckUnderNarration };
        StackPanel fields = new() { Spacing = 10 }; fields.Children.Add(name); fields.Children.Add(gain); fields.Children.Add(pan); fields.Children.Add(duck);
        ContentDialog dialog = new() { XamlRoot = XamlRoot, Title = "Track settings", Content = fields, PrimaryButtonText = "Apply", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.UpdateTrack, TrackId = track.TrackId, Track = track with { Name = name.Text, GainDb = gain.Value, Pan = pan.Value, DuckUnderNarration = duck.IsChecked == true } });
    }

    private void PositionClips(Canvas hits, AudioTrackDocument track, double width)
    {
        hits.Children.Clear();
        if (Workspace.Timeline is not { } timeline) return;
        double scale = width / Workspace.VisibleSeconds;
        foreach (AudioClipDocument clip in timeline.Clips.Where(clip => clip.TrackId == track.TrackId && TimelinePresentation.Intersects(clip, Workspace.ViewStart, Workspace.VisibleSeconds)))
        {
            double left = (clip.StartSeconds - Workspace.ViewStart) * scale;
            Button hit = new() { Content = clip.Name, Width = Math.Max(8, clip.DurationSeconds * scale), Height = _compact ? 50 : 76,
                Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(1), Padding = new Thickness(7,2,7,35),
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, FontSize = 11, Tag = clip.ClipId };
            AutomationProperties.SetName(hit, $"{clip.Name}, {track.Name}, start {TimelinePresentation.Time(clip.StartSeconds)}, duration {TimelinePresentation.Time(clip.DurationSeconds)}");
            AutomationProperties.SetHelpText(hit, "Control-click to select multiple clips. Drag to move; drag an edge to trim. Arrow keys nudge; S splits; Delete lifts; Escape cancels.");
            Canvas.SetLeft(hit, left); Canvas.SetTop(hit, 6);
            hit.Click += (_, _) => Workspace.Select(clip.ClipId, ControlDown());
            hit.PointerPressed += (sender, args) => Clip_PointerPressed(hit, clip, args);
            hit.PointerMoved += (sender, args) => Clip_PointerMoved(hit, args);
            hit.PointerReleased += async (sender, args) => await Clip_PointerReleasedAsync(hit, clip, args);
            hit.PointerCaptureLost += (_, _) => { if (_dragClip is not null) { _dragClip = null; _dragDelta = 0; InvalidateWaves(); } };
            hits.Children.Add(hit);
        }
    }

    private static bool ControlDown() => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    private void Clip_PointerPressed(Button hit, AudioClipDocument clip, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(hit);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (!Workspace.SelectedClipIds.Contains(clip.ClipId))
        {
            if (!ControlDown()) Workspace.SelectedClipIds.Clear();
            Workspace.SelectedClipIds.Add(clip.ClipId);
        }
        Workspace.SelectedTrackId = clip.TrackId;
        _dragClip = clip.ClipId;
        _dragStartX = args.GetCurrentPoint(TimelineRoot).Position.X;
        _dragDelta = 0;
        _dragEdge = point.Position.X < 7 ? -1 : point.Position.X > hit.ActualWidth - 7 ? 1 : 0;
        hit.CapturePointer(args.Pointer);
        hit.Focus(FocusState.Pointer);
        args.Handled = true;
        UpdateInspector(); InvalidateWaves();
    }

    private void Clip_PointerMoved(Button hit, PointerRoutedEventArgs args)
    {
        if (_dragClip is null || hit.Tag is not Guid id || id != _dragClip) return;
        double scale = _timelineWidth / Workspace.VisibleSeconds;
        double delta = (args.GetCurrentPoint(TimelineRoot).Position.X - _dragStartX) / scale;
        AudioClipDocument clip = Workspace.Timeline!.Clips.Single(item => item.ClipId == id);
        double anchor = _dragEdge == 1 ? clip.StartSeconds + clip.DurationSeconds : clip.StartSeconds;
        _dragDelta = Workspace.Snap(anchor + delta, scale, Workspace.SelectedClipIds) - anchor;
        InvalidateWaves();
    }

    private async Task Clip_PointerReleasedAsync(Button hit, AudioClipDocument clip, PointerRoutedEventArgs args)
    {
        if (_dragClip != clip.ClipId) return;
        double delta = _dragDelta; int edge = _dragEdge;
        _dragClip = null; _dragDelta = 0;
        hit.ReleasePointerCapture(args.Pointer); args.Handled = true;
        if (Math.Abs(delta) < .00002) { Workspace.Notify(); return; }
        await RunAsync(async () =>
        {
            if (edge == 0) await Workspace.EditAsync(TimelinePresentation.MoveSelection(Workspace.Timeline!, Workspace.SelectedClipIds, delta).ToArray());
            else await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.TrimClip, ClipId = clip.ClipId,
                StartSeconds = edge < 0 ? clip.StartSeconds + delta : clip.StartSeconds,
                SourceStartSeconds = edge < 0 ? clip.SourceStartSeconds + delta : clip.SourceStartSeconds,
                DurationSeconds = clip.DurationSeconds + (edge < 0 ? -delta : delta) });
        });
        Workspace.Notify();
    }

    private void DrawTrack(CanvasDrawEventArgs args, AudioTrackDocument track, double width, double height)
    {
        var draw = args.DrawingSession;
        double scale = width / Workspace.VisibleSeconds;
        Color color = track.Kind switch { AudioTrackKind.Music => Color.FromArgb(255,179,157,219), AudioTrackKind.Effects => Color.FromArgb(255,244,173,104), _ => Color.FromArgb(255,53,199,232) };
        if (_accessibility.HighContrast) color = ((SolidColorBrush)Brush("ActiveSignalBrush")).Color;
        Color muted = Color.FromArgb(60, color.R, color.G, color.B);
        double grid = Math.Max(.1, Math.Pow(10, Math.Floor(Math.Log10(Workspace.VisibleSeconds / 8))));
        for (double second = Math.Ceiling(Workspace.ViewStart / grid) * grid; second < Workspace.ViewStart + Workspace.VisibleSeconds; second += grid)
        {
            float x = (float)((second - Workspace.ViewStart) * scale);
            draw.DrawLine(x, 0, x, (float)height, Color.FromArgb(55,128,128,128));
        }
        if (Workspace.Timeline is not { } timeline) return;
        foreach (AudioClipDocument clip in timeline.Clips.Where(clip => clip.TrackId == track.TrackId && TimelinePresentation.Intersects(clip, Workspace.ViewStart, Workspace.VisibleSeconds)))
        {
            double delta = Workspace.SelectedClipIds.Contains(clip.ClipId) && _dragClip.HasValue ? _dragDelta : 0;
            double start = clip.StartSeconds + (_dragEdge <= 0 ? delta : 0);
            double duration = clip.DurationSeconds + (_dragEdge < 0 ? -delta : _dragEdge > 0 ? delta : 0);
            Rect rect = new((start - Workspace.ViewStart) * scale, 6, Math.Max(4, duration * scale), height - 12);
            draw.FillRoundedRectangle(rect, 3,3, muted);
            draw.DrawRoundedRectangle(rect,3,3, Workspace.SelectedClipIds.Contains(clip.ClipId) ? color : Color.FromArgb(70,color.R,color.G,color.B), Workspace.SelectedClipIds.Contains(clip.ClipId) ? 2 : 1);
            if (_peakStrips.TryGetValue(clip.ClipId,out var strip))
            {
                float mid = (float)(height * .61), amplitude = (float)(height * .25);
                for (int bin = 0; bin < strip.Minimum.Length; bin++)
                {
                    float x = (float)((strip.TimelineStart + delta + bin * strip.TimelineDuration / strip.Minimum.Length - Workspace.ViewStart) * scale);
                    draw.DrawLine(x,mid-strip.Maximum[bin]*amplitude,x,mid-strip.Minimum[bin]*amplitude,color);
                }
            }
            else draw.DrawLine((float)rect.X, (float)(height * .62), (float)rect.Right, (float)(height * .62), color);
            float yTop = 25, yBottom = (float)height - 9;
            draw.DrawLine((float)rect.Left,yBottom,(float)(rect.Left + clip.FadeInSeconds * scale),yTop,color);
            draw.DrawLine((float)(rect.Right - clip.FadeOutSeconds * scale),yTop,(float)rect.Right,yBottom,color);
            if (clip.Envelope.Count > 1)
                for (int index = 1; index < clip.Envelope.Count; index++)
                {
                    var a = clip.Envelope[index-1]; var b = clip.Envelope[index];
                    draw.DrawLine((float)(rect.Left+a.OffsetSeconds*scale), (float)(height-12-Math.Clamp((a.GainDb+48)/48,0,1)*24),
                        (float)(rect.Left+b.OffsetSeconds*scale), (float)(height-12-Math.Clamp((b.GainDb+48)/48,0,1)*24),color,1.5f);
                }
        }
        foreach (TimelineMarker marker in timeline.Markers.Where(marker => marker.Anchor.Placed))
        {
            float x = (float)((marker.PositionSeconds - Workspace.ViewStart) * scale);
            draw.DrawLine(x,0,x,(float)height,Colors.Orange);
        }
        if (App.Services.PlaybackTarget == "mix" && App.Services.AudioPlayback is { } playback)
        {
            float x = (float)((playback.Position.TotalSeconds - Workspace.ViewStart) * scale);
            draw.DrawLine(x,0,x,(float)height,Colors.White,1.5f);
        }
    }

    private void InvalidateWaves() { foreach (CanvasControl canvas in _canvases) canvas.Invalidate(); }

    private void QueueVisiblePeaks(AudioTimelineDocument timeline,CancellationToken token)
    {
        if (App.Services.FindInstalledFfmpeg() is null || App.Services.Session.Project is not { } project) return;
        string root = App.Services.Session.RequireProjectRoot();
        var assets = AudioTimelineRules.RegisterRenderAssets(project).AudioAssets.ToDictionary(asset => asset.AssetId);
        double viewStart = Workspace.ViewStart, visible = Workspace.VisibleSeconds;
        foreach (AudioClipDocument clip in timeline.Clips.Where(clip => TimelinePresentation.Intersects(clip,viewStart,visible)).Take(512))
            if (assets.TryGetValue(clip.AssetId,out var asset))
                _ = LoadPeakStripAsync(root,clip,asset,viewStart,visible,token);
    }

    private async Task LoadPeakStripAsync(string root,AudioClipDocument clip,AudioAssetRecord asset,double viewStart,double visible,CancellationToken token)
    {
        try
        {
            await _peakSlots.WaitAsync(token);
            try
            {
                WaveStrip strip = await Task.Run(async () =>
                {
                    var service = await App.Services.GetAudioAssetsAsync(token);
                    double start = Math.Max(clip.StartSeconds,viewStart), end = Math.Min(clip.StartSeconds+clip.DurationSeconds,viewStart+visible);
                    const int bins = 640;
                    float[] min = new float[bins], max = new float[bins];
                    double sourceStart = clip.SourceStartSeconds + start - clip.StartSeconds;
                    long first = (long)(sourceStart * 48000 / AudioAssetService.PeakFrames);
                    long last = (long)Math.Ceiling((sourceStart + end-start)*48000/AudioAssetService.PeakFrames);
                    long sourceBuckets = Math.Max(1,(long)Math.Ceiling(asset.DurationSeconds*48000/AudioAssetService.PeakFrames));
                    for (long offset = first; offset < last;)
                    {
                        token.ThrowIfCancellationRequested();
                        long mapped = clip.Loop ? offset % sourceBuckets : offset;
                        int count = (int)Math.Min(8192,Math.Min(last-offset,sourceBuckets-mapped));
                        if (count <= 0) break;
                        var page = await service.ReadPeaksAsync(root,asset,mapped,count,token);
                        for (int index = 0; index < page.Peaks.Count; index++)
                        {
                            int bin = Math.Clamp((int)((offset+index-first)/(double)Math.Max(1,last-first)*bins),0,bins-1);
                            var peak = page.Peaks[index];
                            min[bin] = Math.Min(min[bin],Math.Min(peak.MinimumLeft,peak.MinimumRight));
                            max[bin] = Math.Max(max[bin],Math.Max(peak.MaximumLeft,peak.MaximumRight));
                        }
                        if (page.Peaks.Count == 0) break;
                        offset += count;
                    }
                    return new WaveStrip(start,end-start,min,max);
                },token);
                if (!token.IsCancellationRequested) DispatcherQueue.TryEnqueue(() => { if (token.IsCancellationRequested) return; _peakStrips[clip.ClipId] = strip; InvalidateWaves(); });
            }
            finally { _peakSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { DispatcherQueue.TryEnqueue(() => Workspace.SetNotice($"Waveform unavailable: {error.Message}")); }
    }

    private void UpdateInspector()
    {
        AudioClipDocument? clip = Workspace.Timeline?.Clips.FirstOrDefault(item => Workspace.SelectedClipIds.Contains(item.ClipId));
        SelectionLabel.Text = clip is null ? "Select a clip" : $"{Workspace.SelectedClipIds.Count} selected · {clip.Name}";
        if (clip is null) return;
        if (_inspectedClip == clip.ClipId && _inspectedRevision == Workspace.Timeline?.Revision) return;
        _inspectedClip = clip.ClipId; _inspectedRevision = Workspace.Timeline!.Revision;
        ClipName.Text = clip.Name; ClipStart.Value = clip.StartSeconds; ClipSourceStart.Value = clip.SourceStartSeconds; ClipDuration.Value = clip.DurationSeconds;
        ClipGain.Value = clip.GainDb; ClipPan.Value = clip.Pan; ClipFadeIn.Value = clip.FadeInSeconds; ClipFadeOut.Value = clip.FadeOutSeconds;
        ClipLoop.IsChecked = clip.Loop; ClipMuted.IsChecked = clip.Muted;
        ClipAnchorKind.SelectedItem = ClipAnchorKind.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == clip.Anchor.Kind.ToString()) ?? ClipAnchorKind.Items[0];
        ClipAnchorChapter.ItemsSource = App.Services.Session.Project?.Chapters;
        ClipAnchorChapter.SelectedItem = App.Services.Session.Project?.Chapters.FirstOrDefault(chapter => chapter.ChapterId == clip.Anchor.ChapterId);
        ClipAnchorOffset.Value = clip.Anchor.OffsetSeconds;
        ClipTrack.SelectedItem = (ClipTrack.ItemsSource as IEnumerable<AudioTrackDocument>)?.FirstOrDefault(track => track.TrackId == clip.TrackId);
        EnvelopeText.Text = string.Join(", ", clip.Envelope.Select(point => $"{point.OffsetSeconds.ToString(CultureInfo.InvariantCulture)}:{point.GainDb.ToString(CultureInfo.InvariantCulture)}"));
    }

    private async void ApplyInspector_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        AudioClipDocument clip = Workspace.Timeline?.Clips.FirstOrDefault(item => Workspace.SelectedClipIds.Contains(item.ClipId)) ?? throw new InvalidOperationException("Select a clip first.");
        List<VolumeEnvelopePoint> envelope = [];
        foreach (string pair in EnvelopeText.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = pair.Split(':');
            if (values.Length != 2 || !double.TryParse(values[0], CultureInfo.InvariantCulture, out double time) || !double.TryParse(values[1], CultureInfo.InvariantCulture, out double gain))
                throw new InvalidOperationException("Use envelope points as seconds:dB, separated by commas.");
            envelope.Add(new(time,gain));
        }
        AudioClipDocument update = clip with { Name = ClipName.Text, TrackId = (ClipTrack.SelectedItem as AudioTrackDocument)?.TrackId ?? clip.TrackId,
            StartSeconds = ClipStart.Value, SourceStartSeconds = ClipSourceStart.Value, DurationSeconds = ClipDuration.Value, GainDb = ClipGain.Value,
            Pan = ClipPan.Value, FadeInSeconds = ClipFadeIn.Value, FadeOutSeconds = ClipFadeOut.Value, Loop = ClipLoop.IsChecked == true, Muted = ClipMuted.IsChecked == true, Envelope = envelope,
            Anchor = new() { Kind = Enum.Parse<TimelineAnchorKind>((string)((ComboBoxItem)ClipAnchorKind.SelectedItem).Tag), ChapterId = (ClipAnchorChapter.SelectedItem as ChapterDocument)?.ChapterId, OffsetSeconds = ClipAnchorOffset.Value } };
        await Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.UpdateClip, ClipId = clip.ClipId, Clip = update });
    });

    private async void AddTrack_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        AudioTrackKind kind = Enum.Parse<AudioTrackKind>((string)((MenuFlyoutItem)sender).Tag);
        AudioTrackDocument track = new() { Name = kind.ToString(), Kind = kind, Order = Workspace.Timeline?.Tracks.Count ?? 0 };
        Workspace.SelectedTrackId = track.TrackId;
        return Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.AddTrack, Track = track });
    });

    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (string extension in new[] { ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;
        ComboBox reference = new() { Header = "Optional existing reference transcript", ItemsSource = App.Services.Session.Project?.Segments,
            DisplayMemberPath = "Title", PlaceholderText = "Music / effect · no transcript", SelectedIndex = -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        StackPanel importFields = new() { Spacing = 12 };
        importFields.Children.Add(new TextBlock { Text = $"{file.Name}\nVoiceBench copies the original into this project. Linking a segment registers a recording take; approval confirms transcript accuracy. No audio is uploaded during import.", TextWrapping = TextWrapping.Wrap });
        importFields.Children.Add(reference);
        ContentDialog importDialog = new() { XamlRoot = XamlRoot, Title = "Import audio", Content = importFields, PrimaryButtonText = "Import original", CloseButtonText = "Cancel" };
        if (await importDialog.ShowAsync() != ContentDialogResult.Primary) return;
        await App.Services.RunTrackedJobAsync<AudioAssetRecord>("assets.import", new { sourcePath = file.Path, segmentId = (reference.SelectedItem as SegmentDocument)?.SegmentId });
        await App.Services.Session.RefreshAsync();
        Workspace.SetNotice("Imported original copied into this project. Select the asset and choose Place.");
    });

    private async void PlaceAsset_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        AudioAssetRecord asset = AssetsList.SelectedItem as AudioAssetRecord ?? throw new InvalidOperationException("Select an audio asset from the library.");
        Guid trackId = Workspace.SelectedTrackId ?? throw new InvalidOperationException("Add or select a track first.");
        AudioClipDocument clip = new() { TrackId = trackId, AssetId = asset.AssetId, AssetSha256 = asset.Sha256, Name = asset.Name,
            StartSeconds = App.Services.AudioPlayback?.Position.TotalSeconds ?? Workspace.ViewStart, DurationSeconds = asset.DurationSeconds,
            RenderId = asset.RenderId, SegmentId = asset.SegmentId, SourceSha256 = asset.SourceSha256, CleanSourceRelativePath = asset.CleanSourceRelativePath };
        return Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.AddClip, Clip = clip });
    });

    private double Playhead => App.Services.AudioPlayback?.Position.TotalSeconds ?? Ruler.Value;
    private async void Split_Click(object sender, RoutedEventArgs e) => await SplitAsync();
    private async Task SplitAsync() => await RunAsync(() => Workspace.EditAsync(Workspace.SelectedClipIds.Select(id => new TimelineEdit { Operation = TimelineEditOperation.SplitClip, ClipId = id, SplitSeconds = Playhead }).ToArray()));
    private async void Duplicate_Click(object sender, RoutedEventArgs e) => await DuplicateAsync();
    private async Task DuplicateAsync() => await RunAsync(() => Workspace.EditAsync(Workspace.SelectedClipIds.Select(id => new TimelineEdit { Operation = TimelineEditOperation.DuplicateClip, ClipId = id }).ToArray()));
    private async void Lift_Click(object sender, RoutedEventArgs e) => await LiftAsync();
    private async Task LiftAsync() => await RunAsync(() => Workspace.EditAsync(Workspace.SelectedClipIds.Select(id => new TimelineEdit { Operation = TimelineEditOperation.DeleteClip, ClipId = id }).ToArray()));
    private async void CloseGap_Click(object sender, RoutedEventArgs e) => await RunAsync(() => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.CloseGap, ClipId = Workspace.SelectedClipIds.FirstOrDefault() }));
    private async void Marker_Click(object sender, RoutedEventArgs e) => await RunAsync(() => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.SetMarker, Marker = new() { Name = "Marker", PositionSeconds = Playhead } }));
    private async void EndMarker_Click(object sender, RoutedEventArgs e) => await RunAsync(() => Workspace.EditAsync(new TimelineEdit { Operation = TimelineEditOperation.SetEndMarker, StartSeconds = Playhead }));
    private void SelectAll_Click(object sender, RoutedEventArgs e) { if (Workspace.Timeline is not { } timeline) return; Workspace.SelectedClipIds.UnionWith(timeline.Clips.Select(clip => clip.ClipId)); Workspace.Notify(); }
    private async void Undo_Click(object sender, RoutedEventArgs e) => await HistoryAsync("mix.history.undo");
    private async void Redo_Click(object sender, RoutedEventArgs e) => await HistoryAsync("mix.history.redo");
    private async Task HistoryAsync(string command) => await RunAsync(async () => { await App.Services.RunTimelineCommandAsync<object>(command,new { }); await App.Services.Session.RefreshAsync(); await Workspace.LoadAsync(); });
    public Task UndoTimelineAsync(bool undo) => HistoryAsync(undo ? "mix.history.undo" : "mix.history.redo");
    public bool OwnsKeyboardFocus()
    {
        DependencyObject? element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element is not null) { if (ReferenceEquals(element,this)) return true; element = VisualTreeHelper.GetParent(element); }
        return false;
    }

    private void Snap_Click(object sender, RoutedEventArgs e) { Workspace.SnapEnabled = SnapToggle.IsChecked == true; Workspace.Notify(); }
    private void Viewport_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_refreshing) return; Workspace.ViewStart = e.NewValue; Workspace.Notify(); }
    private void Zoom_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_refreshing || TimelineNotice is null) return; Workspace.VisibleSeconds = e.NewValue; Workspace.Notify(); }
    private async void Ruler_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_refreshing && App.Services.AudioPlayback is { } playback) await RunAsync(() => playback.SeekAsync(TimeSpan.FromSeconds(e.NewValue)));
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (App.Services.AudioPlayback?.IsPlaying == true) { App.Services.AudioPlayback.Pause(); return; }
        if (PlaybackTargetPicker.SelectedIndex == 1) { App.Services.SelectedTakePlayback?.Invoke(); return; }
        if (App.Services.PlaybackTarget == "mix" && App.Services.AudioPlayback is { } loaded) loaded.Play();
        else await App.Services.PlayMixAsync();
    });
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { if (App.Services.AudioPlayback is { } playback) { playback.Stop(); await playback.SeekAsync(TimeSpan.Zero); } });
    private void Loop_Click(object sender, RoutedEventArgs e) { if (App.Services.AudioPlayback is { } playback) playback.Loop = LoopToggle.IsChecked == true; }

    private void UpdateTransport()
    {
        if (App.Services.AudioPlayback is not { } playback) return;
        _refreshing = true;
        MixPlayButton.Content = playback.IsPlaying ? "Ⅱ Pause" : "▶ Play";
        PositionText.Text = TimelinePresentation.Time(playback.Position.TotalSeconds);
        Ruler.Value = Math.Clamp(playback.Position.TotalSeconds,0,Ruler.Maximum);
        LoopToggle.IsChecked = playback.Loop;
        LeftMeter.Value = Math.Clamp(playback.Meter.PeakLeft,0,1);
        RightMeter.Value = Math.Clamp(playback.Meter.PeakRight,0,1);
        InvalidateWaves();
        _refreshing = false;
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        TextBox name = new() { Header = "Versioned export name", Text = "Mix" };
        CheckBox mp3 = new() { Content = "Also export MP3" };
        CheckBox stems = new() { Content = "Also export aligned pre-master stems" };
        StackPanel panel = new() { Spacing = 10 }; panel.Children.Add(name); panel.Children.Add(mp3); panel.Children.Add(stems);
        ContentDialog dialog = new() { XamlRoot = XamlRoot, Title = "Export mix · stereo 48 kHz / 24-bit / −16 LUFS / −1 dBTP", Content = panel, PrimaryButtonText = "Export", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await App.Services.RunTrackedJobAsync<object>("mix.export", new { scope = Workspace.Scope, chapterId = Workspace.ChapterId, name = name.Text, mp3 = mp3.IsChecked == true, stems = stems.IsChecked == true });
        Workspace.SetNotice("Mix exported. Previous masters remain available.");
    });

    private async void Timeline_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or NumberBox) return;
        if (e.Key == VirtualKey.Escape) { _dragClip = null; _dragDelta = 0; InvalidateWaves(); Workspace.SetNotice("Gesture cancelled"); e.Handled = true; return; }
        if (e.Key == VirtualKey.Delete) { await LiftAsync(); e.Handled = true; }
        else if (e.Key == VirtualKey.S && !ControlDown()) { await SplitAsync(); e.Handled = true; }
        else if (e.Key == VirtualKey.D && ControlDown()) { await DuplicateAsync(); e.Handled = true; }
        else if (e.Key == VirtualKey.Z && ControlDown()) { await HistoryAsync("mix.history.undo"); e.Handled = true; }
        else if (e.Key == VirtualKey.Y && ControlDown()) { await HistoryAsync("mix.history.redo"); e.Handled = true; }
        else if (e.Key is VirtualKey.Left or VirtualKey.Right && Workspace.Timeline is { } timeline)
        {
            double amount = (ControlDown() ? 1 : .1) * (e.Key == VirtualKey.Left ? -1 : 1);
            await RunAsync(() => Workspace.EditAsync(TimelinePresentation.MoveSelection(timeline,Workspace.SelectedClipIds,amount).ToArray())); e.Handled = true;
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Workspace.SetNotice(error.Message); }
    }
}
