using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>Opens project files and folders in Windows Explorer or their default app.</summary>
internal static class ShellLauncher
{
    /// <summary>Opens the folder that contains <paramref name="filePath"/> with the file selected.</summary>
    public static async Task RevealFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            string? folder = Path.GetDirectoryName(filePath);
            if (folder is not null && Directory.Exists(folder))
            {
                await OpenFolderAsync(folder);
                return;
            }

            throw new FileNotFoundException("The file no longer exists. It may have been moved or deleted.", filePath);
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
        StorageFolder parent = await file.GetParentAsync()
            ?? await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(filePath)!);
        FolderLauncherOptions options = new();
        options.ItemsToSelect.Add(file);
        if (!await Launcher.LaunchFolderAsync(parent, options))
            throw new InvalidOperationException("Windows could not open the containing folder.");
    }

    /// <summary>Opens a folder in Explorer, creating it first when it is a known project folder that is still empty.</summary>
    public static async Task OpenFolderAsync(string folderPath, bool create = false)
    {
        if (create) Directory.CreateDirectory(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"The folder '{folderPath}' does not exist.");
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
        if (!await Launcher.LaunchFolderAsync(folder))
            throw new InvalidOperationException("Windows could not open the folder.");
    }

    /// <summary>Opens a file with its default Windows app (for example, the default audio player).</summary>
    public static async Task OpenFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The file no longer exists. It may have been moved or deleted.", filePath);
        StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
        if (!await Launcher.LaunchFileAsync(file))
            throw new InvalidOperationException("Windows could not find an app associated with this file.");
    }

    /// <summary>Puts a path on the clipboard; throws a readable error when another app holds the clipboard.</summary>
    public static void CopyPath(string path) => CopyText(path);

    /// <summary>Puts text on the clipboard; throws a readable error when another app holds the clipboard.</summary>
    public static void CopyText(string text)
    {
        try
        {
            DataPackage package = new() { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The clipboard is in use by another app. Try again in a moment.", exception);
        }
    }

    public static Task OpenProjectFolderAsync(ProjectFolder folder)
    {
        string root = App.Services.Session.RequireProjectRoot();
        return OpenFolderAsync(ProjectLocations.Resolve(root, folder), create: folder != ProjectFolder.Root);
    }
}
