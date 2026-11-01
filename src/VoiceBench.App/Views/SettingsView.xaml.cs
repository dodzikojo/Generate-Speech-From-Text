using IssueLab.VoiceBench.App.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class SettingsView : UserControl
{
    private readonly DispatcherTimer _localControlTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshingLocalControl;
    private bool _localControlRefreshActive;
    private bool _credentialDialogOpen;
    private const string CodexConfig = "[mcp_servers.voicebench]\ncommand = \"voicebench-mcp.exe\"";
    public SettingsView()
    {
        InitializeComponent();
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
            FfmpegStatusText.Text = tools is null ? "!  MISSING" : "✓  VERIFIED";
            ToolTipService.SetToolTip(FfmpegStatusText, tools?.FfmpegPath);
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
            FfmpegStatusText.Text = "✓  VERIFIED";
            ShowStatus(InfoBarSeverity.Success, "FFmpeg ready", toolchain.FfmpegPath);
        }
        catch (Exception exception)
        {
            FfmpegStatusText.Text = "!  MISSING";
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
            FfmpegStatusText.Text = "✓  SELECTED";
            ShowStatus(InfoBarSeverity.Success, "Existing FFmpeg selected", $"Using {toolchain.FfmpegPath} with the adjacent ffprobe.exe.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, "Executable could not be used", exception.Message);
        }
    }

    private async Task RefreshStatusAsync()
    {
        OpenAiCredentialStatusText.Text = string.IsNullOrWhiteSpace(await App.Services.GetCredentialAsync("openai"))
            ? "○  NOT CONFIGURED" : "✓  CONFIGURED";
        GeminiCredentialStatusText.Text = string.IsNullOrWhiteSpace(await App.Services.GetCredentialAsync("gemini"))
            ? "○  NOT CONFIGURED" : "✓  CONFIGURED";
        var ffmpeg = App.Services.FindInstalledFfmpeg();
        FfmpegStatusText.Text = ffmpeg is null ? "!  MISSING" : "✓  VERIFIED";
        ToolTipService.SetToolTip(FfmpegStatusText, ffmpeg?.FfmpegPath);
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}
