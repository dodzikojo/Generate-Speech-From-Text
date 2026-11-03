using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using IssueLab.VoiceBench.App.Services;

namespace IssueLab.VoiceBench.App.Controls;

public enum AudioTransportState { Ready, Playing, Paused, Ended, Failed, Cleared }
public sealed record AudioPositionChangedEventArgs(TimeSpan Position, bool IsPlaying);
public sealed record AudioTransportStateChangedEventArgs(AudioTransportState State);

/// <summary>A native view of the application's single WASAPI transport.</summary>
public sealed partial class AudioTransport : UserControl
{
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string? _sourcePath;
    private string _audioLabel = "Audio";
    private TimeSpan _knownDuration;
    private bool _updatingPosition;
    private AudioTransportState _lastState = AudioTransportState.Cleared;
    private bool IsActive => _sourcePath is not null && string.Equals(App.Services.PlaybackSourcePath, _sourcePath, StringComparison.OrdinalIgnoreCase);

    public AudioTransport()
    {
        InitializeComponent();
        SizeChanged += (_, args) => VisualStateManager.GoToState(this,args.NewSize.Width >= 620 ? "WideTransport" : "NarrowTransport",false);
        _positionTimer.Tick += (_, _) => RefreshPlayback();
        Loaded += (_, _) => _positionTimer.Start();
        Unloaded += (_, _) => _positionTimer.Stop();
        App.Services.AudioTargetChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsActive) RaiseState(AudioTransportState.Paused);
        });
    }

    /// <summary>
    /// Single-row layout for docked bars: restart, seek and time on one line, without the raised
    /// surface, label/state text or play button (the host supplies its own play control).
    /// </summary>
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            _isCompact = value;
            TransportSurface.Background = value ? null : (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["PanelRaisedBrush"];
            TransportSurface.BorderThickness = value ? new Thickness(0) : new Thickness(1);
            TransportSurface.Padding = value ? new Thickness(0) : new Thickness(10);
            TransportMetadata.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetRow(PositionSlider, value ? 0 : 2);
            Grid.SetColumn(PositionSlider, value ? 2 : 0);
            Grid.SetColumnSpan(PositionSlider, value ? 1 : 4);
            if (value)
            {
                Grid.SetColumn(TransportTime, 3);
                TransportTimeColumn.Width = GridLength.Auto;
            }
            // Docked hosts provide their own primary play control next to the take selector.
            PlayPauseButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private bool _isCompact;

    public event EventHandler? PlaybackStarted;
    public event EventHandler<AudioPositionChangedEventArgs>? PlaybackPositionChanged;
    public event EventHandler<AudioTransportStateChangedEventArgs>? PlaybackStateChanged;
    public bool HasAudio => _sourcePath is not null;

    /// <summary>Full path of the loaded audio file, for "show in folder" actions.</summary>
    public string? SourcePath => _sourcePath;
    public bool IsPlaying => IsActive && App.Services.AudioPlayback?.IsPlaying == true;
    public SelectedTakePlaybackState ReadSelectedTakeState() => new(HasAudio,IsActive,IsPlaying,_audioLabel,
        IsActive ? App.Services.AudioPlayback?.Position ?? TimeSpan.Zero : TimeSpan.Zero,
        IsActive ? App.Services.AudioPlayback?.Duration ?? _knownDuration : _knownDuration);

    public object GetPlaybackStatus() => new
    {
        hasAudio = HasAudio, label = _audioLabel, state = _lastState.ToString(),
        positionSeconds = IsActive ? App.Services.AudioPlayback?.Position.TotalSeconds ?? 0 : 0,
        durationSeconds = IsActive ? App.Services.AudioPlayback?.Duration.TotalSeconds ?? _knownDuration.TotalSeconds : _knownDuration.TotalSeconds,
        target = App.Services.PlaybackTarget,
    };

    public void Play() => _ = PlayCoreAsync();
    public Task PlayAsync() => PlayCoreAsync();
    public async Task TogglePlaybackAsync() { if (IsPlaying) Pause(); else await PlayCoreAsync(); }

    private async Task PlayCoreAsync()
    {
        try
        {
            if (_sourcePath is null) throw new InvalidOperationException("Select an audio take before playing it.");
            if (!IsActive) await App.Services.LoadPlaybackFileAsync(_sourcePath,"take");
            var playback = App.Services.AudioPlayback!;
            if (playback.Duration > TimeSpan.Zero && playback.Position >= playback.Duration - TimeSpan.FromMilliseconds(50))
                await playback.SeekAsync(TimeSpan.Zero);
            PlaybackStarted?.Invoke(this,EventArgs.Empty);
            playback.Play();
            _positionTimer.Start();
            RefreshPlayback();
        }
        catch (Exception error)
        {
            PlaybackStateText.Text = "PLAYBACK FAILED";
            ToolTipService.SetToolTip(PlaybackStateText,error.Message);
            RaiseState(AudioTransportState.Failed);
            App.Services.AudioWorkspace.SetNotice(error.Message);
        }
    }

    public void Seek(TimeSpan position)
        => _ = SeekToAsync(position);

    public Task SeekToAsync(TimeSpan position)
    {
        if (_sourcePath is null) throw new InvalidOperationException("Select an audio take before seeking.");
        if (position < TimeSpan.Zero || (_knownDuration > TimeSpan.Zero && position > _knownDuration)) throw new ArgumentOutOfRangeException(nameof(position));
        return SeekCoreAsync(position);
    }

    private async Task SeekCoreAsync(TimeSpan position)
    {
        try
        {
            if (!IsActive) await App.Services.LoadPlaybackFileAsync(_sourcePath!,"take");
            await App.Services.AudioPlayback!.SeekAsync(position);
            UpdatePositionUi(position);
        }
        catch (Exception error) { App.Services.AudioWorkspace.SetNotice(error.Message); RaiseState(AudioTransportState.Failed); }
    }

    public void LoadAudio(string path,string label,TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) { ClearAudio("AUDIO FILE MISSING"); return; }
        string fullPath = Path.GetFullPath(path);
        bool changed = !string.Equals(fullPath,_sourcePath,StringComparison.OrdinalIgnoreCase);
        if (changed && IsActive) App.Services.AudioPlayback?.Pause();
        _sourcePath = fullPath;
        _audioLabel = string.IsNullOrWhiteSpace(label) ? "Audio" : label.Trim();
        _knownDuration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        PlayPauseButton.IsEnabled = true; RestartButton.IsEnabled = true;
        PositionSlider.IsEnabled = _knownDuration > TimeSpan.Zero;
        _updatingPosition = true; PositionSlider.Maximum = Math.Max(1,_knownDuration.TotalSeconds); _updatingPosition = false;
        AudioLabelText.Text = _audioLabel.ToUpperInvariant(); DurationText.Text = FormatTime(_knownDuration);
        if (changed) { UpdatePositionUi(TimeSpan.Zero); SetPlaybackState("READY",false); RaiseState(AudioTransportState.Ready); }
    }

    public void ClearAudio(string label = "NO AUDIO LOADED")
    {
        if (IsActive) App.Services.AudioPlayback?.Stop();
        _sourcePath = null; _knownDuration = TimeSpan.Zero; _audioLabel = label;
        _updatingPosition = true; PositionSlider.Value = 0; PositionSlider.Maximum = 1; _updatingPosition = false;
        PositionSlider.IsEnabled = false; PlayPauseButton.IsEnabled = false; RestartButton.IsEnabled = false;
        AudioLabelText.Text = label; CurrentTimeText.Text = "00:00"; DurationText.Text = "00:00";
        SetPlaybackState("Select a take to listen",false); RaiseState(AudioTransportState.Cleared);
    }

    public void Pause() { if (IsActive) { App.Services.AudioPlayback?.Pause(); RefreshPlayback(); } }
    public void Stop() { if (IsActive) { App.Services.AudioPlayback?.Stop(); RefreshPlayback(); } }
    private void PlayPause_Click(object sender,RoutedEventArgs e) { if (IsActive && App.Services.AudioPlayback?.IsPlaying == true) Pause(); else Play(); }
    private async void Restart_Click(object sender,RoutedEventArgs e) { await SeekCoreAsync(TimeSpan.Zero); Play(); }
    private void PositionSlider_ValueChanged(object sender,RangeBaseValueChangedEventArgs e) { if (!_updatingPosition && PositionSlider.IsEnabled) Seek(TimeSpan.FromSeconds(e.NewValue)); }

    private void RefreshPlayback()
    {
        if (!IsActive || App.Services.AudioPlayback is not { } playback) return;
        _knownDuration = playback.Duration;
        bool playing = playback.IsPlaying;
        TimeSpan position = playback.Position;
        TransportPhase phase = TransportPhaseRules.Next(playing,
            _lastState is AudioTransportState.Playing or AudioTransportState.Ended, position, _knownDuration);
        SetPlaybackState(phase switch { TransportPhase.Playing => "PLAYING", TransportPhase.Ended => "ENDED", _ => "READY" },playing);
        RaiseState(phase switch
        {
            TransportPhase.Playing => AudioTransportState.Playing,
            TransportPhase.Ended => AudioTransportState.Ended,
            _ => AudioTransportState.Paused,
        });
        UpdatePositionUi(position);
    }

    private void SetPlaybackState(string state,bool playing)
    {
        PlaybackStateText.Text = state; PlayPauseButton.Content = playing ? "Ⅱ Pause" : "▶ Play";
        AutomationProperties.SetName(PlayPauseButton,playing ? "Pause audio" : "Play audio");
    }

    private void UpdatePositionUi(TimeSpan position)
    {
        _updatingPosition = true;
        PositionSlider.Value = Math.Clamp(position.TotalSeconds,0,PositionSlider.Maximum);
        _updatingPosition = false;
        CurrentTimeText.Text = FormatTime(position); DurationText.Text = FormatTime(_knownDuration);
        PlaybackPositionChanged?.Invoke(this,new(position,IsActive && App.Services.AudioPlayback?.IsPlaying == true));
    }

    private void RaiseState(AudioTransportState state)
    {
        if (_lastState == state) return;
        _lastState = state;
        PlaybackStateChanged?.Invoke(this,new(state));
    }

    private static string FormatTime(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
}
