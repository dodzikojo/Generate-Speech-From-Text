using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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

    public event EventHandler? PlaybackStarted;
    public event EventHandler<AudioPositionChangedEventArgs>? PlaybackPositionChanged;
    public event EventHandler<AudioTransportStateChangedEventArgs>? PlaybackStateChanged;
    public bool HasAudio => _sourcePath is not null;

    public object GetPlaybackStatus() => new
    {
        hasAudio = HasAudio, label = _audioLabel, state = _lastState.ToString(),
        positionSeconds = IsActive ? App.Services.AudioPlayback?.Position.TotalSeconds ?? 0 : 0,
        durationSeconds = IsActive ? App.Services.AudioPlayback?.Duration.TotalSeconds ?? _knownDuration.TotalSeconds : _knownDuration.TotalSeconds,
        target = App.Services.PlaybackTarget,
    };

    public void Play() => _ = PlayCoreAsync();

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
    {
        if (_sourcePath is null) throw new InvalidOperationException("Select an audio take before seeking.");
        if (position < TimeSpan.Zero || (_knownDuration > TimeSpan.Zero && position > _knownDuration)) throw new ArgumentOutOfRangeException(nameof(position));
        _ = SeekCoreAsync(position);
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
        SetPlaybackState(playing ? "PLAYING" : "READY",playing);
        RaiseState(playing ? AudioTransportState.Playing : AudioTransportState.Paused);
        UpdatePositionUi(playback.Position);
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
