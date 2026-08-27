using IssueLab.VoiceBench.App.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
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

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void Credential_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string providerId }) return;
        string providerName = providerId == "openai" ? "OpenAI" : "Google Gemini";
        PasswordBox keyBox = new()
        {
            Header = $"{providerName} API key",
            PlaceholderText = "Stored only in Windows Credential Manager",
        };
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

        try
        {
            await App.Services.SaveCredentialAsync(providerId, keyBox.Password.Trim());
            keyBox.Password = string.Empty;
            await RefreshStatusAsync();
            ShowStatus(InfoBarSeverity.Success, $"{providerName} key saved", "Windows Credential Manager now holds this key for VoiceBench.");
        }
        catch (Exception exception)
        {
            keyBox.Password = string.Empty;
            ShowStatus(InfoBarSeverity.Error, "Credential could not be saved", exception.Message);
        }
    }

    private async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowStatus(InfoBarSeverity.Informational, "Downloading pinned FFmpeg", "VoiceBench will use the tool only after its published SHA-256 matches.");
            var toolchain = await App.Services.EnsureFfmpegAsync();
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
