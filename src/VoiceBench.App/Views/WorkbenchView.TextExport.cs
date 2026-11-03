using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

/// <summary>
/// Copies or saves a segment's text: the full script with production cues, or only the narration.
/// The open segment exports what the editor shows (including unsaved edits); other segments export
/// their saved script file.
/// </summary>
public sealed partial class WorkbenchView
{
    private async void CopySegmentScript_Click(object sender, RoutedEventArgs e) =>
        await ExportSegmentTextAsync(sender, SegmentTextKind.Script, toFile: false);

    private async void CopySegmentNarration_Click(object sender, RoutedEventArgs e) =>
        await ExportSegmentTextAsync(sender, SegmentTextKind.Narration, toFile: false);

    private async void SaveSegmentScript_Click(object sender, RoutedEventArgs e) =>
        await ExportSegmentTextAsync(sender, SegmentTextKind.Script, toFile: true);

    private async void SaveSegmentNarration_Click(object sender, RoutedEventArgs e) =>
        await ExportSegmentTextAsync(sender, SegmentTextKind.Narration, toFile: true);

    private async Task ExportSegmentTextAsync(object sender, SegmentTextKind kind, bool toFile)
    {
        if (_project is null || !App.Services.Session.HasProject)
        {
            ShowStatus(InfoBarSeverity.Warning, "Open a project first", "Select a segment to copy or save its text.");
            return;
        }

        string? segmentId = sender is FrameworkElement { Tag: "segment" } ? GetSelectedRailItem()?.SegmentId : _segment?.SegmentId;
        SegmentDocument? segment = segmentId is null ? null : _project.Segments.FirstOrDefault(item => item.SegmentId == segmentId);
        if (segment is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Select a segment first", "Choose a segment in the chapter rail or the segment picker.");
            return;
        }

        string kindName = kind == SegmentTextKind.Script ? "Script" : "Narration";
        try
        {
            string script = segment.SegmentId == _segment?.SegmentId
                ? GetEditorText()
                : await File.ReadAllTextAsync(App.Services.Session.ResolveProjectPath(segment.ScriptRelativePath));
            string text = SegmentTextExport.Text(script, kind);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus(InfoBarSeverity.Warning, "Nothing to export", kind == SegmentTextKind.Narration
                    ? $"“{segment.Title}” has no narration yet; production cues are not spoken."
                    : $"“{segment.Title}” is empty.");
                return;
            }

            string windowsText = SegmentTextExport.WithWindowsLineEndings(text);
            int words = SegmentTextExport.CountWords(text);
            if (!toFile)
            {
                ShellLauncher.CopyText(windowsText);
                ShowStatus(InfoBarSeverity.Success, $"{kindName} copied",
                    $"{words:N0} {(words == 1 ? "word" : "words")} from “{segment.Title}” {(kind == SegmentTextKind.Script ? "with production cues " : string.Empty)}are on the clipboard.");
                return;
            }

            string scriptExtension = Path.GetExtension(segment.ScriptRelativePath);
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(SegmentTextExport.SuggestedFileName(segment.Title, kind, scriptExtension)),
            };
            if (kind == SegmentTextKind.Script && scriptExtension.Equals(".md", StringComparison.OrdinalIgnoreCase))
                picker.FileTypeChoices.Add("Markdown", [".md"]);
            picker.FileTypeChoices.Add("Plain text", [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteTextAsync(file, windowsText);
            ShowStatus(InfoBarSeverity.Success, $"{kindName} saved", $"{words:N0} {(words == 1 ? "word" : "words")} saved to {file.Path}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowStatus(InfoBarSeverity.Error, $"{kindName} could not be {(toFile ? "saved" : "copied")}", exception.Message);
        }
    }
}
