using IssueLab.VoiceBench.App.Layout;
using IssueLab.VoiceBench.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Infrastructure.Toolchain;
using IssueLab.VoiceBench.Infrastructure.Audio;
using Windows.Storage;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class SettingsView : UserControl
{
    private readonly DispatcherTimer _localControlTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshingLocalControl;
    private bool _localControlRefreshActive;
    private bool _credentialDialogOpen;
    private bool _audioSettingsLoaded;
    private const string CodexConfig = "[mcp_servers.voicebench]\ncommand = \"voicebench-mcp.exe\"";
    public SettingsView()
    {
        InitializeComponent();
        MonitorVolume.Value = ApplicationData.Current.LocalSettings.Values["Audio.MonitorVolume"] is double savedVolume ? savedVolume : 1;
        _audioSettingsLoaded = true;
        Loaded += SettingsView_Loaded;
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
        LocalMcpCodexConfig.Text = CodexConfig;
        _localControlTimer.Tick += async (_, _) => await RefreshLocalControlAsync();
        Unloaded += (_, _) => _localControlTimer.Stop();
    }

    private void ApplyResponsiveState(double width)
    {
        bool isWide = width >= 700;
        VisualStateManager.GoToState(
            this,
            isWide ? "WideSettings" : "NarrowSettings",
            false);
        StudioPageFrame.Resize(SettingsRoot, width, 980, isWide ? 32 : 16);
    }

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        await RefreshLocalControlAsync();
        _localControlTimer.Start();
    }

    private async void AudioDevices_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            AudioOutputPicker.ItemsSource = new[] { new AudioOutputDevice(string.Empty,"System default · follows Windows output") }.Concat(await Task.Run(IssueLab.VoiceBench.App.Services.AppServices.GetAudioOutputDevices)).ToArray();
            string selected = ApplicationData.Current.LocalSettings.Values["Audio.OutputDevice"] as string ?? string.Empty;
            AudioOutputPicker.SelectedItem = ((IEnumerable<AudioOutputDevice>)AudioOutputPicker.ItemsSource).FirstOrDefault(device => device.DeviceId == selected);
        }
        catch (Exception error) { ShowStatus(InfoBarSeverity.Error,"Audio devices unavailable",error.Message); }
    }

    private async void AudioDeviceApply_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if (AudioOutputPicker.SelectedItem is not AudioOutputDevice device) throw new InvalidOperationException("Refresh devices and choose an output first.");
            if (App.Services.AudioPlayback is { } playback) await playback.SetOutputDeviceAsync(string.IsNullOrEmpty(device.DeviceId) ? null : device.DeviceId);
            ApplicationData.Current.LocalSettings.Values["Audio.OutputDevice"] = device.DeviceId;
            ShowStatus(InfoBarSeverity.Success,"Audio output selected",device.Name);
        }
        catch (Exception error) { ShowStatus(InfoBarSeverity.Error,"Audio output could not change",error.Message); }
    }

    private void MonitorVolume_Changed(object sender,Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_audioSettingsLoaded) return;
        if (App.Services.AudioPlayback is { } playback) playback.Volume = (float)e.NewValue;
        ApplicationData.Current.LocalSettings.Values["Audio.MonitorVolume"] = e.NewValue;
    }

    private async void Credential_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string providerId }) return;
        await ShowCredentialEntryAsync(providerId);
    }

    internal async void OpenCredentialEntry(string providerId)
    {
        try { await ShowCredentialEntryAsync(providerId); }
        catch { ShowStatus(InfoBarSeverity.Error, "Credential dialog unavailable", "Close the current dialog and try again in Settings."); }
    }

    private async Task ShowCredentialEntryAsync(string providerId)
    {
        if (_credentialDialogOpen) return;
        _credentialDialogOpen = true;
        string providerName = providerId == "openai" ? "OpenAI" : "Google Gemini";
        PasswordBox keyBox = new()
        {
            Header = $"{providerName} API key",
            PlaceholderText = "Stored only in Windows Credential Manager",
        };
        try
        {
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = $"Save {providerName} key",
                PrimaryButtonText = "Save key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = keyBox,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            if (string.IsNullOrWhiteSpace(keyBox.Password))
            {
                ShowStatus(InfoBarSeverity.Warning, "Key not saved", "Paste a non-empty API key and try again.");
                return;
            }
            await App.Services.SaveCredentialAsync(providerId, keyBox.Password.Trim());
            keyBox.Password = string.Empty;
            await RefreshStatusAsync();
            ShowStatus(InfoBarSeverity.Success, $"{providerName} key saved", "Windows Credential Manager now holds this key for VoiceBench.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Credential could not be saved", exception.Message);
        }
        finally
        {
            keyBox.Password = string.Empty;
            _credentialDialogOpen = false;
        }
    }

    private async void LocalMcpToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshingLocalControl || App.MainWindow?.LocalControl is not { } control) return;
        try
        {
            await control.SetEnabledAsync(LocalMcpToggle.IsOn);
            await RefreshLocalControlAsync();
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Local control could not change", exception.Message);
            await RefreshLocalControlAsync();
        }
    }

    private async Task RefreshLocalControlAsync()
    {
        if (_localControlRefreshActive || LocalMcpStatusText is null) return;
        _localControlRefreshActive = true;
        try
        {
            if (App.MainWindow?.LocalControl is not { } control) return;
            _refreshingLocalControl = true;
            LocalMcpToggle.IsOn = control.Enabled;
            JsonElement status = AutomationJson.Element(await control.GetStatusAsync());
            int clientCount = status.GetProperty("connectedClients").GetInt32();
            LocalMcpStatusText.Text = control.StartupError ?? $"{(control.Enabled ? "ENABLED" : "DISABLED")}  ·  {clientCount} CONNECTED CLIENT{(clientCount == 1 ? "" : "S")}  ·  LOCAL STDIO / NAMED PIPE";
            JsonElement jobs = status.GetProperty("jobs");
            JsonElement[] items = jobs.ValueKind == JsonValueKind.Array ? jobs.EnumerateArray().ToArray() : [];
            string[] active = items.Where(item => item.TryGetProperty("status", out JsonElement state) && state.GetString() is "Queued" or "InProgress")
                .Select(item => item.TryGetProperty("command", out JsonElement command) ? command.GetString() ?? "Job" : "Job").ToArray();
            LocalMcpJobsText.Text = active.Length == 0 ? "No active jobs" : $"{active.Length} active job(s): {string.Join(", ", active)}";
            var tools = App.Services.FindInstalledFfmpeg();
            SetFfmpegStatus(tools is null ? "!  MISSING" : "✓  VERIFIED", tools?.FfmpegPath);
        }
        catch { LocalMcpStatusText.Text = "Local control status is temporarily unavailable."; }
        finally { _refreshingLocalControl = false; _localControlRefreshActive = false; }
    }

    private void CopyCodexConfig_Click(object sender, RoutedEventArgs e) => CopyConfiguration(CodexConfig);

    private void CopyGenericConfig_Click(object sender, RoutedEventArgs e) =>
        CopyConfiguration("{\n  \"mcpServers\": {\n    \"voicebench\": { \"command\": \"voicebench-mcp.exe\", \"args\": [] }\n  }\n}");

    private void CopyConfiguration(string text)
    {
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowStatus(InfoBarSeverity.Success, "Configuration copied", "The installed voicebench-mcp.exe execution alias starts VoiceBench on the first operational request.");
    }

    private async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowStatus(InfoBarSeverity.Informational, "Downloading pinned FFmpeg", "VoiceBench will use the tool only after its published SHA-256 matches.");
            ToolchainPaths toolchain = App.Services.Session.HasProject
                ? await App.Services.RunTrackedJobAsync<ToolchainPaths>("toolchain.ensure", new { })
                : await App.Services.EnsureFfmpegAsync();
            SetFfmpegStatus("✓  VERIFIED", toolchain.FfmpegPath);
            ShowStatus(InfoBarSeverity.Success, "FFmpeg ready", toolchain.FfmpegPath);
        }
        catch (Exception exception)
        {
            SetFfmpegStatus("!  MISSING", App.Services.FindInstalledFfmpeg()?.FfmpegPath);
            ShowStatus(InfoBarSeverity.Error, "FFmpeg setup failed", $"Nothing unverified was installed. {exception.Message}");
        }
    }

    private async void SelectFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add(".exe");
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var toolchain = App.Services.SelectExistingFfmpeg(file.Path);
            SetFfmpegStatus("✓  SELECTED", toolchain.FfmpegPath);
            ShowStatus(InfoBarSeverity.Success, "Existing FFmpeg selected", $"Using {toolchain.FfmpegPath} with the adjacent ffprobe.exe.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Executable could not be used", exception.Message);
        }
    }

    private async Task RefreshStatusAsync()
    {
        SetCredentialStatus(OpenAiCredentialStatusText, OpenAiCredentialButton, "OpenAI",
            !string.IsNullOrWhiteSpace(await App.Services.GetCredentialAsync("openai")));
        SetCredentialStatus(GeminiCredentialStatusText, GeminiCredentialButton, "Gemini",
            !string.IsNullOrWhiteSpace(await App.Services.GetCredentialAsync("gemini")));
        var ffmpeg = App.Services.FindInstalledFfmpeg();
        SetFfmpegStatus(ffmpeg is null ? "!  MISSING" : "✓  VERIFIED", ffmpeg?.FfmpegPath);
    }

    private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private static void SetCredentialStatus(TextBlock status, Button button, string providerName, bool configured)
    {
        status.Text = configured ? "✓  CONFIGURED" : "○  NOT CONFIGURED";
        status.Foreground = ThemeBrush(configured ? "ApprovalBrush" : "PickupBrush");
        button.Content = configured ? $"Replace {providerName} key" : $"Add {providerName} key";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            button,
            $"{(configured ? "Replace" : "Add")} {providerName} API key in Windows Credential Manager");
    }

    private void SetFfmpegStatus(string text, string? ffmpegPath)
    {
        bool ok = text.StartsWith('✓');
        FfmpegStatusText.Text = text;
        FfmpegStatusText.Foreground = ThemeBrush(ok ? "ApprovalBrush" : "PickupBrush");
        FfmpegStatusBadge.BorderBrush = ThemeBrush(ok ? "ApprovalBrush" : "PickupBrush");
        ToolTipService.SetToolTip(FfmpegStatusText, ffmpegPath);
        FfmpegPathText.Text = ffmpegPath ?? "%LOCALAPPDATA%\\IssueLab\\VoiceBench\\Tools";
        RevealFfmpegButton.IsEnabled = ffmpegPath is not null;
        RevealFfmpegButton.Tag = ffmpegPath;
    }

    private async void RevealFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (RevealFfmpegButton.Tag is not string path) return;
        try { await ShellLauncher.RevealFileAsync(path); }
        catch (Exception exception) { ShowStatus(InfoBarSeverity.Error, "FFmpeg location could not be shown", exception.Message); }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}
