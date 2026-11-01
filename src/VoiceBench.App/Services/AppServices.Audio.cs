using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;

namespace IssueLab.VoiceBench.App.Services;

public sealed partial class AppServices
{
    private AudioAssetService? _audioAssets;
    private AudioPlaybackEngine? _audioPlayback;
    private Task<AudioPlaybackEngine>? _audioPlaybackInitialization;
    private double _mixResumeSeconds;
    public AudioTimelineService AudioTimeline { get; private set; } = null!;
    public AudioTimelineWorkspace AudioWorkspace { get; private set; } = null!;
    public AudioPlaybackEngine? AudioPlayback => _audioPlayback;
    public string PlaybackTarget { get; internal set; } = "none";
    public string? PlaybackSourcePath { get; internal set; }
    public event EventHandler? AudioTargetChanged;
    public Action? SelectedTakePlayback { get; set; }
    public MixRenderPlan? ActiveMixPlan { get; private set; }

    private void InitializeAudioWorkspace()
    {
        AudioTimeline = new AudioTimelineService(Projects, AuthoringTransactions);
        AudioWorkspace = new AudioTimelineWorkspace(this);
        Session.ProjectChanged += async (_, _) =>
        {
            try { await AudioWorkspace.LoadAsync(); }
            catch (Exception error) { AudioWorkspace.SetNotice(error.Message); }
        };
    }

    public async Task<AudioAssetService> GetAudioAssetsAsync(CancellationToken cancellationToken = default) =>
        _audioAssets ??= new AudioAssetService(Projects, _processRunner, await EnsureFfmpegAsync(cancellationToken));

    public Task<AudioPlaybackEngine> GetAudioPlaybackAsync(CancellationToken cancellationToken = default) =>
        _audioPlaybackInitialization ??= InitializePlaybackAsync(cancellationToken);

    private async Task<AudioPlaybackEngine> InitializePlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            _audioPlayback = new AudioPlaybackEngine(await GetAudioAssetsAsync(cancellationToken));
            if (Windows.Storage.ApplicationData.Current.LocalSettings.Values["Audio.MonitorVolume"] is double monitorVolume) _audioPlayback.Volume = (float)monitorVolume;
            if (Windows.Storage.ApplicationData.Current.LocalSettings.Values["Audio.OutputDevice"] is string deviceId && !string.IsNullOrWhiteSpace(deviceId))
                await _audioPlayback.SetOutputDeviceAsync(deviceId,cancellationToken);
            _audioPlayback.PlaybackFailed += (_, error) => AudioWorkspace.SetNotice($"Audio device stopped: {error.Message}. Select an output device or play again.");
            return _audioPlayback;
        }
        catch { _audioPlaybackInitialization = null; throw; }
    }

    public async Task LoadPlaybackFileAsync(string path, string target, CancellationToken cancellationToken = default)
    {
        AudioPlaybackEngine playback = await GetAudioPlaybackAsync(cancellationToken);
        await Task.Run(() => playback.LoadFileAsync(path, cancellationToken),cancellationToken);
        PlaybackTarget = target;
        PlaybackSourcePath = path;
        AudioTargetChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PlayMixAsync(CancellationToken cancellationToken = default)
    {
        string mixRoot = Session.RequireProjectRoot();
        MixRenderPlan plan = await Task.Run(() => AudioTimeline.BuildPreviewPlanAsync(mixRoot, AudioWorkspace.Scope, AudioWorkspace.ChapterId, cancellationToken),cancellationToken);
        AudioPlaybackEngine playback = await GetAudioPlaybackAsync(cancellationToken);
        await Task.Run(() => playback.LoadMixAsync(mixRoot, plan, cancellationToken),cancellationToken);
        if (AudioWorkspace.MonitorSoloTrackIds.Count > 0) await playback.SetSoloTracksAsync(AudioWorkspace.MonitorSoloTrackIds,cancellationToken);
        if (_mixResumeSeconds > 0) await playback.SeekAsync(TimeSpan.FromSeconds(Math.Min(_mixResumeSeconds,plan.DurationSeconds)),cancellationToken);
        _mixResumeSeconds = 0;
        PlaybackTarget = "mix";
        ActiveMixPlan = plan;
        PlaybackSourcePath = null;
        AudioTargetChanged?.Invoke(this, EventArgs.Empty);
        playback.Play();
    }

    public void InvalidateMixPlayback()
    {
        if (PlaybackTarget != "mix") return;
        _mixResumeSeconds = AudioPlayback?.Position.TotalSeconds ?? 0;
        AudioPlayback?.Pause(); PlaybackTarget = "none"; ActiveMixPlan = null;
        AudioTargetChanged?.Invoke(this,EventArgs.Empty);
    }

    public async Task<T> RunTimelineCommandAsync<T>(string command, object arguments, CancellationToken cancellationToken = default)
    {
        AutomationDispatcher automation = Automation ?? throw new InvalidOperationException("Local control is still starting. Try again shortly.");
        AutomationResult result = await automation.ExecuteAsync(new(command, AutomationJson.Element(arguments), "desktop-" + Guid.NewGuid().ToString("N")), cancellationToken);
        if (result.IsError) throw new AutomationException(result.Code!, result.Message!);
        return result.Data.Deserialize<T>(AutomationJson.Options) ?? throw new InvalidDataException("The timeline operation returned no result.");
    }

    public async Task<object?> ExecuteAudioAutomationAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
    {
        string? Text(string key) => arguments.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string key) => arguments.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.True;
        AudioTimelineScope scope = arguments.TryGetProperty("scope",out var scopeValue)
            ? scopeValue.Deserialize<AudioTimelineScope>(AutomationJson.Options) : AudioTimelineScope.Compilation;
        string? chapterId = scope == AudioTimelineScope.Chapter ? Text("chapterId") : null;
        string root = Session.RequireProjectRoot();
        switch (command)
        {
            case "assets.import.execute":
                var importService = await GetAudioAssetsAsync(cancellationToken);
                string importPath = Text("sourcePath") ?? throw new AutomationException("invalid_arguments","Supply sourcePath.");
                string? importSegment = Text("segmentId");
                var imported = await Task.Run(() => importService.ImportAsync(root,importPath,importSegment,cancellationToken),cancellationToken);
                await Session.RefreshAsync(cancellationToken);
                return imported;
            case "assets.prepare.execute":
                Guid assetId = arguments.GetProperty("assetId").GetGuid();
                var asset = (await Projects.LoadAsync(root,cancellationToken)).AudioAssets.Single(item => item.AssetId == assetId);
                return await (await GetAudioAssetsAsync(cancellationToken)).PrepareAsync(root,asset,cancellationToken);
            case "assets.peaks":
            case "assets.waveform":
                Guid peaksId = arguments.GetProperty("assetId").GetGuid();
                var peaksAsset = (await Projects.LoadAsync(root,cancellationToken)).AudioAssets.Single(item => item.AssetId == peaksId);
                return await (await GetAudioAssetsAsync(cancellationToken)).ReadPeaksAsync(root,peaksAsset,
                    arguments.TryGetProperty("offset",out var offset) ? offset.GetInt64() : arguments.TryGetProperty("startBucket",out var start) ? start.GetInt64() : 0,
                    arguments.TryGetProperty("count",out var count) ? Math.Clamp(count.GetInt32(),0,4096) : 1024,cancellationToken);
            case "assets.relink.execute":
                Guid relinkId = arguments.GetProperty("assetId").GetGuid();
                var relinkAsset = (await Projects.LoadAsync(root,cancellationToken)).AudioAssets.Single(item => item.AssetId == relinkId);
                await (await GetAudioAssetsAsync(cancellationToken)).RelinkAsync(root,relinkAsset,Text("sourcePath")!,cancellationToken);
                return new { relinked = true, assetId = relinkId };
            case "mix.export.execute":
                var plan = await Task.Run(() => AudioTimeline.BuildPlanAsync(root,scope,chapterId,cancellationToken),cancellationToken);
                var exporter = new MixExportService(await GetAudioAssetsAsync(cancellationToken),_processRunner,await EnsureFfmpegAsync(cancellationToken));
                string exportName = Text("name") ?? "Mix";
                bool exportMp3 = Flag("mp3"), exportStems = Flag("stems");
                return await Task.Run(() => exporter.ExportAsync(root,plan,exportName,exportMp3,exportStems,cancellationToken),cancellationToken);
            case "mix.playback.get": return ReadMixPlaybackStatus();
            case "mix.playback.play":
                if (PlaybackTarget != "mix" || ActiveMixPlan?.Scope != scope || ActiveMixPlan?.ChapterId != chapterId)
                {
                    await AudioWorkspace.LoadAsync(scope,chapterId);
                    await PlayMixAsync(cancellationToken);
                }
                else (await GetAudioPlaybackAsync(cancellationToken)).Play();
                return ReadMixPlaybackStatus();
            case "mix.playback.pause": AudioPlayback?.Pause(); return ReadMixPlaybackStatus();
            case "mix.playback.stop":
                if (AudioPlayback is { } stopped) { stopped.Stop(); await stopped.SeekAsync(TimeSpan.Zero,cancellationToken); }
                return ReadMixPlaybackStatus();
            case "mix.playback.seek":
                await (await GetAudioPlaybackAsync(cancellationToken)).SeekAsync(TimeSpan.FromSeconds(arguments.TryGetProperty("seconds",out var seekSeconds) ? seekSeconds.GetDouble() : arguments.GetProperty("positionSeconds").GetDouble()),cancellationToken);
                return ReadMixPlaybackStatus();
            case "mix.playback.loop":
                var looping = await GetAudioPlaybackAsync(cancellationToken);
                if ((arguments.TryGetProperty("seconds",out var loopStart) || arguments.TryGetProperty("startSeconds",out loopStart)) && arguments.TryGetProperty("endSeconds",out var loopEnd))
                    await looping.SetLoopRangeAsync(TimeSpan.FromSeconds(loopStart.GetDouble()),TimeSpan.FromSeconds(loopEnd.GetDouble()),cancellationToken);
                looping.Loop = Flag("enabled"); return ReadMixPlaybackStatus();
            case "mix.playback.monitor":
                if (!arguments.TryGetProperty("volume",out _) && !arguments.TryGetProperty("soloTrackIds",out _))
                    return new { meter = AudioPlayback?.Meter, underruns = AudioPlayback?.UnderrunCount ?? 0, volume = AudioPlayback?.Volume ?? 1 };
                var monitoring = await GetAudioPlaybackAsync(cancellationToken);
                if (arguments.TryGetProperty("volume",out var volume)) monitoring.Volume = volume.GetSingle();
                if (arguments.TryGetProperty("soloTrackIds",out var solo))
                {
                    var soloIds = solo.Deserialize<Guid[]>(AutomationJson.Options) ?? [];
                    await monitoring.SetSoloTracksAsync(soloIds,cancellationToken);
                    AudioWorkspace.MonitorSoloTrackIds.Clear(); AudioWorkspace.MonitorSoloTrackIds.UnionWith(soloIds); AudioWorkspace.Notify();
                }
                return new { meter = monitoring.Meter, underruns = monitoring.UnderrunCount, volume = monitoring.Volume };
            case "mix.playback.devices": return new { devices = GetAudioOutputDevices() };
            case "mix.playback.device":
                var devicePlayback = await GetAudioPlaybackAsync(cancellationToken);
                if (arguments.TryGetProperty("deviceId",out _)) await devicePlayback.SetOutputDeviceAsync(Text("deviceId"),cancellationToken);
                return new { devices = devicePlayback.GetOutputDevices() };
            default: throw new AutomationException("unknown_command",$"Unknown audio command '{command}'.");
        }
    }

    private object ReadMixPlaybackStatus() => new { target = PlaybackTarget, isPlaying = AudioPlayback?.IsPlaying ?? false,
        positionSeconds = AudioPlayback?.Position.TotalSeconds ?? 0, durationSeconds = AudioPlayback?.Duration.TotalSeconds ?? 0,
        loop = AudioPlayback?.Loop ?? false, meter = AudioPlayback?.Meter, underruns = AudioPlayback?.UnderrunCount ?? 0 };

    public static IReadOnlyList<AudioOutputDevice> GetAudioOutputDevices()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        List<AudioOutputDevice> devices = [];
        foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render,NAudio.CoreAudioApi.DeviceState.Active))
        { using (device) devices.Add(new(device.ID,device.FriendlyName)); }
        return devices;
    }
}
