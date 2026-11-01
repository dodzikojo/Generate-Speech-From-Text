using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace IssueLab.VoiceBench.App.Controls;

public enum AudioTransportState
{
    Ready,
    Playing,
    Paused,
    Ended,
    Failed,
    Cleared,
}

public sealed record AudioPositionChangedEventArgs(TimeSpan Position, bool IsPlaying);

public sealed record AudioTransportStateChangedEventArgs(AudioTransportState State);

public sealed partial class AudioTransport : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private MediaPlayer? _player;
    private MediaSource? _mediaSource;
    private string? _sourcePath;
    private string _audioLabel = "Audio";
    private TimeSpan _knownDuration;
    private bool _updatingPosition;

    public AudioTransport()
    {
        InitializeComponent();
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _positionTimer.Tick += PositionTimer_Tick;
        Loaded += AudioTransport_Loaded;
        Unloaded += AudioTransport_Unloaded;
    }

    private void ApplyResponsiveState(double width) => VisualStateManager.GoToState(
        this,
        width >= 620 ? "WideTransport" : "NarrowTransport",
        false);

    public event EventHandler? PlaybackStarted;

    public event EventHandler<AudioPositionChangedEventArgs>? PlaybackPositionChanged;

    public event EventHandler<AudioTransportStateChangedEventArgs>? PlaybackStateChanged;

    public bool HasAudio => _sourcePath is not null;

    public void LoadAudio(string path, string label, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            ClearAudio("AUDIO FILE MISSING");
            return;
        }

        _sourcePath = Path.GetFullPath(path);
        _audioLabel = string.IsNullOrWhiteSpace(label) ? "Audio" : label.Trim();
        _knownDuration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        EnsurePlayer();
        SetMediaSource();
        SetReadyUi();
    }

    public void ClearAudio(string label = "NO AUDIO LOADED")
    {
        _positionTimer.Stop();
        if (_player is not null)
        {
            _player.Pause();
            _player.Source = null;
        }

        _mediaSource?.Dispose();
        _mediaSource = null;
        _sourcePath = null;
        _knownDuration = TimeSpan.Zero;
        _audioLabel = label;
        _updatingPosition = true;
        PositionSlider.Value = 0;
        PositionSlider.Maximum = 1;
        _updatingPosition = false;
        PositionSlider.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        RestartButton.IsEnabled = false;
        PlayPauseButton.Content = "▶  Play";
        AutomationProperties.SetName(PlayPauseButton, "Play audio");
        AudioLabelText.Text = label;
        PlaybackStateText.Text = "Render or select a take to listen";
        CurrentTimeText.Text = "00:00";
        DurationText.Text = "00:00";
        RaiseState(AudioTransportState.Cleared);
    }

    public void Pause()
    {
        if (_player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
    }

    public void Stop()
    {
        if (_player is null) return;
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
        UpdatePositionUi(TimeSpan.Zero);
    }

    private void AudioTransport_Loaded(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null) return;
        EnsurePlayer();
        if (_player!.Source is null)
        {
            SetMediaSource();
        }
    }

    private void AudioTransport_Unloaded(object sender, RoutedEventArgs e) => DisposePlayer();

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        _player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Media };
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;
        _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
    }

    private void SetMediaSource()
    {
        if (_player is null || _sourcePath is null) return;
        _player.Pause();
        _player.Source = null;
        _mediaSource?.Dispose();
        _mediaSource = MediaSource.CreateFromUri(new Uri(_sourcePath));
        _player.Source = _mediaSource;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || _sourcePath is null) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            return;
        }

        if (_knownDuration > TimeSpan.Zero
            && _player.PlaybackSession.Position >= _knownDuration - TimeSpan.FromMilliseconds(250))
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
        }

        PlaybackStarted?.Invoke(this, EventArgs.Empty);
        _player.Play();
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || _sourcePath is null) return;
        _player.PlaybackSession.Position = TimeSpan.Zero;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
        _player.Play();
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPosition || _player is null || !PositionSlider.IsEnabled) return;
        TimeSpan requested = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, PositionSlider.Maximum));
        if (Math.Abs((_player.PlaybackSession.Position - requested).TotalMilliseconds) >= 500)
        {
            _player.PlaybackSession.Position = requested;
            UpdatePositionUi(requested);
        }
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (_player is null) return;
        UpdatePositionUi(_player.PlaybackSession.Position);
    }

    private void Player_MediaOpened(MediaPlayer sender, object args) =>
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            TimeSpan naturalDuration = sender.PlaybackSession.NaturalDuration;
            if (naturalDuration > TimeSpan.Zero)
            {
                _knownDuration = naturalDuration;
            }

            SetReadyUi();
        });

    private void Player_MediaEnded(MediaPlayer sender, object args) =>
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer.Stop();
            sender.PlaybackSession.Position = TimeSpan.Zero;
            RaiseState(AudioTransportState.Ended);
            UpdatePositionUi(TimeSpan.Zero);
            SetPlaybackState("READY", isPlaying: false);
        });

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer.Stop();
            SetPlaybackState("PLAYBACK FAILED", isPlaying: false);
            PlaybackStateText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["FailureBrush"];
            RaiseState(AudioTransportState.Failed);
        });

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args) =>
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            switch (sender.PlaybackState)
            {
                case MediaPlaybackState.Playing:
                    _positionTimer.Start();
                    SetPlaybackState("PLAYING", isPlaying: true);
                    RaiseState(AudioTransportState.Playing);
                    break;
                case MediaPlaybackState.Buffering:
                case MediaPlaybackState.Opening:
                    _positionTimer.Stop();
                    SetPlaybackState("PREPARING AUDIO", isPlaying: false);
                    break;
                default:
                    _positionTimer.Stop();
                    SetPlaybackState(_sourcePath is null ? "NO AUDIO" : "READY", isPlaying: false);
                    RaiseState(_sourcePath is null ? AudioTransportState.Cleared : AudioTransportState.Paused);
                    break;
            }
        });

    private void SetReadyUi()
    {
        bool hasAudio = _sourcePath is not null;
        PlayPauseButton.IsEnabled = hasAudio;
        RestartButton.IsEnabled = hasAudio;
        PositionSlider.IsEnabled = hasAudio && _knownDuration > TimeSpan.Zero;
        _updatingPosition = true;
        PositionSlider.Maximum = Math.Max(1, _knownDuration.TotalSeconds);
        PositionSlider.Value = 0;
        _updatingPosition = false;
        AudioLabelText.Text = _audioLabel.ToUpperInvariant();
        PlaybackStateText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"];
        DurationText.Text = FormatTime(_knownDuration);
        CurrentTimeText.Text = "00:00";
        SetPlaybackState(hasAudio ? "READY" : "NO AUDIO", isPlaying: false);
        RaiseState(hasAudio ? AudioTransportState.Ready : AudioTransportState.Cleared);
    }

    private void SetPlaybackState(string state, bool isPlaying)
    {
        PlaybackStateText.Text = state;
        PlayPauseButton.Content = isPlaying ? "Ⅱ  Pause" : "▶  Play";
        AutomationProperties.SetName(PlayPauseButton, isPlaying ? "Pause audio" : "Play audio");
    }

    private void UpdatePositionUi(TimeSpan position)
    {
        TimeSpan bounded = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (_knownDuration > TimeSpan.Zero && bounded > _knownDuration)
        {
            bounded = _knownDuration;
        }

        _updatingPosition = true;
        PositionSlider.Value = Math.Clamp(bounded.TotalSeconds, 0, PositionSlider.Maximum);
        _updatingPosition = false;
        CurrentTimeText.Text = FormatTime(bounded);
        PlaybackPositionChanged?.Invoke(
            this,
            new AudioPositionChangedEventArgs(
                bounded,
                _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing));
    }

    private void RaiseState(AudioTransportState state) =>
        PlaybackStateChanged?.Invoke(this, new AudioTransportStateChangedEventArgs(state));

    private void DisposePlayer()
    {
        _positionTimer.Stop();
        if (_player is null) return;
        _player.MediaOpened -= Player_MediaOpened;
        _player.MediaEnded -= Player_MediaEnded;
        _player.MediaFailed -= Player_MediaFailed;
        _player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
        _player.Pause();
        _player.Source = null;
        _mediaSource?.Dispose();
        _mediaSource = null;
        _player.Dispose();
        _player = null;
    }

    private static string FormatTime(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
}
