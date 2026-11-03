using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Packaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace IssueLab.VoiceBench.App.Views;

/// <summary>
/// Package approved takes into a folder (synced on later runs) and clean up the other takes of
/// approved segments. Both dialogs are shared by the Workbench and Assembly.
/// </summary>
public sealed partial class WorkbenchView
{
    private const string PackageFolderSettingPrefix = "Packaging.Folder.";
    private bool _takeCleanupRunning;

    private async void PackageApprovedTakes_Click(object sender, RoutedEventArgs e)
    {
        // The chapter's own menu packages that chapter (plus whatever the package already holds).
        string? chapterId = sender is FrameworkElement { Tag: "chapter" } ? GetSelectedRailItem()?.ChapterId : null;
        await PackageApprovedTakesAsync(chapterId);
    }

    private async void CleanUpTakes_Click(object sender, RoutedEventArgs e) => await CleanUpTakesAsync();

    // ---- Package ------------------------------------------------------------------------------

    internal async Task PackageApprovedTakesAsync(string? chapterId = null)
    {
        if (!App.Services.Session.HasProject)
        {
            await ShowMessageAsync("Open a project first", "Packaging copies the approved takes of the open project.");
            return;
        }

        string projectRoot = App.Services.Session.RequireProjectRoot();
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(project);
        if (!candidates.Any(candidate => candidate.CanPackage))
        {
            await ShowMessageAsync("No approved takes yet", "Approve a take for at least one segment, then package again.");
            return;
        }

        TakeStorageReport storage = await App.Services.TakeCleanup.MeasureAsync(projectRoot);
        string? packageRoot = ApplicationData.Current.LocalSettings.Values[PackageFolderSettingPrefix + project.ProjectId.ToString("N")] as string;
        PackageFolderState folder = PackageFolderState.Empty;
        string? folderProblem = null;

        // ---- Dialog content ----
        TextBlock destinationText = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        TextBlock folderNote = new() { TextWrapping = TextWrapping.Wrap, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"] };
        HyperlinkButton chooseFolder = new() { Content = "Choose folder…", Padding = new Thickness(0) };
        AutomationProperties.SetName(chooseFolder, "Choose package folder");
        TextBlock planText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        TextBlock storageText = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Takes on disk {TakeCleanupRules.FormatBytes(storage.TotalBytes)} · approved {TakeCleanupRules.FormatBytes(storage.ApprovedBytes)}"
                + (storage.RemovableBytes > 0 ? $" · clean-up can free {TakeCleanupRules.FormatBytes(storage.RemovableBytes)}" : string.Empty),
        };

        HashSet<string> previouslyPackaged = new(StringComparer.Ordinal);
        List<(CheckBox Box, PackageCandidate Candidate)> boxes = [];
        List<(CheckBox Box, string ChapterId)> chapterBoxes = [];
        StackPanel list = new() { Spacing = 2 };
        bool syncing = false;
        Dictionary<string, ChapterTakeStorage> storageByChapter = storage.Chapters.ToDictionary(item => item.ChapterId, StringComparer.Ordinal);
        long largestChapter = storage.Chapters.Select(item => item.TotalBytes).DefaultIfEmpty(0).Max();
        foreach (IGrouping<string, PackageCandidate> chapter in candidates.GroupBy(candidate => candidate.ChapterId))
        {
            PackageCandidate first = chapter.First();
            int approved = chapter.Count(candidate => candidate.CanPackage);
            storageByChapter.TryGetValue(chapter.Key, out ChapterTakeStorage? chapterStorage);
            string detail = $"{approved} of {chapter.Count()} approved"
                + (chapterStorage is null ? string.Empty : $" · takes on disk {TakeCleanupRules.FormatBytes(chapterStorage.TotalBytes)}")
                + (chapterStorage?.RemovableBytes > 0 ? $" · clean-up frees {TakeCleanupRules.FormatBytes(chapterStorage.RemovableBytes)}" : string.Empty)
                + (first.ChapterIncludedInCompilation ? string.Empty : " · excluded from compilation");
            CheckBox chapterBox = new()
            {
                IsEnabled = approved > 0,
                Margin = new Thickness(0, 8, 0, 0),
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = $"{first.ChapterNumber:00}  {first.ChapterTitle}", FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = detail, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"], TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            AutomationProperties.SetName(chapterBox, $"Package chapter {first.ChapterTitle}");
            chapterBoxes.Add((chapterBox, chapter.Key));
            list.Children.Add(chapterBox);
            if (chapterStorage is not null)
                list.Children.Add(StorageBar(chapterStorage.TotalBytes, chapterStorage.ApprovedBytes, chapterStorage.RemovableBytes,
                    largestChapter, new Thickness(28, 2, 0, 4)));
            foreach (PackageCandidate candidate in chapter)
            {
                string line = candidate.CanPackage
                    ? $"Take {candidate.ApprovedTake!.TakeNumber:00} · {TakeListPresentation.FormatDuration(candidate.ApprovedTake.Duration)}"
                    : candidate.Problem ?? "Not approved yet";
                CheckBox box = new()
                {
                    IsEnabled = candidate.CanPackage,
                    Margin = new Thickness(28, 0, 0, 0),
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = $"{candidate.ChapterNumber:00}-{candidate.SegmentNumber:000}  {candidate.SegmentTitle}", TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = line, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"] },
                        },
                    },
                };
                AutomationProperties.SetName(box, $"{candidate.SegmentTitle}, {line}");
                boxes.Add((box, candidate));
                list.Children.Add(box);
            }
        }

        StackPanel content = new() { Spacing = 10, MinWidth = 600 };
        content.Children.Add(new TextBlock
        {
            Text = "Copies each approved take, unchanged, into a folder per chapter, named in chapter order (01-003 Observe.wav). Packaging into the same folder again updates it: new approvals are added, changed takes replaced, moved or renamed segments renamed, and files for segments you untick removed. Files you add or edit there are never touched.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "PACKAGE FOLDER", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"] },
                destinationText,
                folderNote,
                chooseFolder,
            },
        });
        content.Children.Add(storageText);
        content.Children.Add(StorageBar(storage.TotalBytes, storage.ApprovedBytes, storage.RemovableBytes, storage.TotalBytes, new Thickness(0)));
        content.Children.Add(StorageLegend());
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 340, Padding = new Thickness(0, 0, 14, 0) });
        content.Children.Add(planText);
        ProgressBar progress = new() { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
        content.Children.Add(progress);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Resources = { ["ContentDialogMaxWidth"] = 760.0 },
            Title = "Package approved takes",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Package",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        IEnumerable<PackageCandidate> Selected() => boxes.Where(item => item.Box.IsChecked == true).Select(item => item.Candidate);
        ApprovedTakePackagePlan CurrentPlan() => ApprovedTakePackageRules.Plan(Selected(), folder);

        void Refresh()
        {
            destinationText.Text = packageRoot ?? "No folder chosen yet";
            folderNote.Text = folderProblem
                ?? (packageRoot is null ? "Choose where the package goes. VoiceBench creates a “<project> - approved takes” folder there and remembers it."
                    : folder.Manifest is null ? "A new package will be created here." : $"Updates the package made {folder.Manifest.PackagedAt.ToLocalTime():MMM d · HH:mm}.");
            ApprovedTakePackagePlan plan = CurrentPlan();
            int count = Selected().Count();
            planText.Text = packageRoot is null || folderProblem is not null ? string.Empty : plan.Summary;
            dialog.PrimaryButtonText = count == 0 ? "Package" : $"Package {count} take{(count == 1 ? "" : "s")}";
            dialog.IsPrimaryButtonEnabled = count > 0 && packageRoot is not null && folderProblem is null;
            syncing = true;
            foreach ((CheckBox chapterBox, string chapterId) in chapterBoxes)
            {
                List<CheckBox> children = boxes.Where(item => item.Candidate.ChapterId == chapterId && item.Box.IsEnabled).Select(item => item.Box).ToList();
                int ticked = children.Count(box => box.IsChecked == true);
                chapterBox.IsThreeState = ticked > 0 && ticked < children.Count;
                chapterBox.IsChecked = ticked == 0 ? false : ticked == children.Count ? true : null;
            }

            syncing = false;
        }

        async Task LoadFolderAsync()
        {
            folderProblem = null;
            folder = PackageFolderState.Empty;
            if (packageRoot is null) return;
            try
            {
                folder = await App.Services.TakePackages.ReadFolderAsync(projectRoot, packageRoot, project, candidates);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or ArgumentException)
            {
                folderProblem = exception.Message;
            }
        }

        void ApplyDefaultTicks()
        {
            previouslyPackaged.Clear();
            foreach (PackagedTakeFile file in folder.Manifest?.Files ?? []) previouslyPackaged.Add(file.SegmentId);
            foreach ((CheckBox box, PackageCandidate candidate) in boxes)
            {
                bool wanted = chapterId is not null
                    ? candidate.ChapterId == chapterId || previouslyPackaged.Contains(candidate.SegmentId)
                    : previouslyPackaged.Count > 0 ? previouslyPackaged.Contains(candidate.SegmentId) || candidate.ChapterIncludedInCompilation
                    : candidate.ChapterIncludedInCompilation;
                box.IsChecked = candidate.CanPackage && wanted;
            }
        }

        foreach ((CheckBox box, _) in boxes)
        {
            box.Checked += (_, _) => { if (!syncing) Refresh(); };
            box.Unchecked += (_, _) => { if (!syncing) Refresh(); };
        }

        foreach ((CheckBox chapterBox, string id) in chapterBoxes)
        {
            void SetChildren(bool value)
            {
                if (syncing) return;
                syncing = true;
                foreach ((CheckBox box, PackageCandidate candidate) in boxes)
                    if (candidate.ChapterId == id && box.IsEnabled) box.IsChecked = value;
                syncing = false;
                Refresh();
            }

            chapterBox.Checked += (_, _) => SetChildren(true);
            chapterBox.Unchecked += (_, _) => SetChildren(false);
            // Clicking a partly ticked chapter ticks all of it rather than cycling through "partly".
            chapterBox.Indeterminate += (_, _) => SetChildren(true);
        }

        bool pickingFolder = false;
        chooseFolder.Click += (_, _) =>
        {
            pickingFolder = true;
            dialog.Hide();
        };

        ApprovedTakePackageResult? result = null;
        Exception? failure = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try
            {
                dialog.IsPrimaryButtonEnabled = false;
                chooseFolder.IsEnabled = false;
                progress.Visibility = Visibility.Visible;
                planText.Text = "Packaging…";
                ApprovedTakePackagePlan plan = CurrentPlan();
                Progress<PackageProgress> reporter = new(update =>
                {
                    progress.Value = update.Total == 0 ? 1 : (double)update.Done / update.Total;
                    planText.Text = $"Packaging {update.Done} of {update.Total} · {update.CurrentFile}";
                });
                string root = packageRoot!;
                result = await Task.Run(() => App.Services.TakePackages.ApplyAsync(projectRoot, root, project, plan, folder, reporter));
                ApplicationData.Current.LocalSettings.Values[PackageFolderSettingPrefix + project.ProjectId.ToString("N")] = result.PackageRoot;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await LoadFolderAsync();
        ApplyDefaultTicks();
        Refresh();
        while (true)
        {
            pickingFolder = false;
            ContentDialogResult shown = await dialog.ShowAsync();
            if (!pickingFolder)
            {
                if (shown != ContentDialogResult.Primary && result is null && failure is null) return;
                break;
            }

            // The folder picker can't open over a dialog, so the dialog steps aside and comes back
            // with the operator's ticks; a folder that already holds a package restores its selection.
            string? chosen = await PickFolderAsync();
            if (chosen is null) continue;
            packageRoot = ApprovedTakePackageService.ResolvePackageRoot(chosen, project.Name);
            await LoadFolderAsync();
            if (folder.Manifest is not null) ApplyDefaultTicks();
            Refresh();
        }
        if (failure is not null)
        {
            await ShowMessageAsync("The takes could not be packaged", Actionable(failure));
            return;
        }

        if (result is not null) await ShowPackageResultAsync(result);
    }

    private async Task ShowPackageResultAsync(ApprovedTakePackageResult result)
    {
        TakeStorageReport storage = await App.Services.TakeCleanup.MeasureAsync(App.Services.Session.RequireProjectRoot());
        List<string> parts = [];
        if (result.Copied > 0) parts.Add($"{result.Copied} copied");
        if (result.Moved > 0) parts.Add($"{result.Moved} renamed");
        if (result.Removed > 0) parts.Add($"{result.Removed} removed");
        if (result.Unchanged > 0) parts.Add($"{result.Unchanged} unchanged");
        StackPanel content = new() { Spacing = 8, MaxWidth = 560 };
        content.Children.Add(new TextBlock { Text = result.PackageRoot, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock
        {
            Text = $"{string.Join(" · ", parts)} · {TakeCleanupRules.FormatBytes(result.PackagedBytes)} in the package. A voicebench-package.json file records what VoiceBench wrote, so packaging here again updates it.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (result.LeftUntouched.Count > 0)
            content.Children.Add(new TextBlock
            {
                Text = "Left untouched:\n" + string.Join("\n", result.LeftUntouched.Take(6)) + (result.LeftUntouched.Count > 6 ? "\n…" : string.Empty),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["PickupBrush"],
            });
        if (storage.RemovableTakes > 0)
            content.Children.Add(new TextBlock
            {
                Text = $"Clean-up can free {TakeCleanupRules.FormatBytes(storage.RemovableBytes)}: the {storage.RemovableTakes} other take{(storage.RemovableTakes == 1 ? "" : "s")} of approved segments.",
                TextWrapping = TextWrapping.Wrap,
            });

        ContentDialog done = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Packaged {result.PackagedTakes} approved take{(result.PackagedTakes == 1 ? "" : "s")}",
            Content = content,
            PrimaryButtonText = storage.RemovableTakes > 0 ? "Clean up takes…" : string.Empty,
            SecondaryButtonText = "Open folder",
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult choice = await done.ShowAsync();
        if (choice == ContentDialogResult.Secondary) await ShellLauncher.OpenFolderAsync(result.PackageRoot);
        else if (choice == ContentDialogResult.Primary) await CleanUpTakesAsync();
    }

    // ---- Clean up -----------------------------------------------------------------------------

    internal async Task CleanUpTakesAsync()
    {
        if (!App.Services.Session.HasProject)
        {
            await ShowMessageAsync("Open a project first", "Clean-up works on the takes of the open project.");
            return;
        }

        if (StudioBusy)
        {
            await ShowMessageAsync("Wait for the current render", "Clean-up changes the project, so it runs when nothing is rendering or being approved.");
            return;
        }

        string projectRoot = App.Services.Session.RequireProjectRoot();
        VoiceBenchProjectDocument project = App.Services.Session.RequireProject();
        TakeStorageReport storage = await App.Services.TakeCleanup.MeasureAsync(projectRoot);
        List<Guid> removable = TakeCleanupService.Plan(projectRoot, project).SelectMany(segment => segment.Removable).Select(take => take.RenderId).ToList();

        StackPanel content = new() { Spacing = 10, MinWidth = 600 };
        content.Children.Add(new TextBlock
        {
            Text = "Moves the audio of the other takes of approved segments to the Recycle Bin. Approved takes are kept, segments still awaiting review keep all their takes, and takes used in a mix are kept. Cleaned-up takes stay in each segment's history marked “Audio cleaned up”. Their files stay in the Recycle Bin until you empty it; restoring one puts the file back on disk, but the take stays cleaned up in VoiceBench. A file that can't go to the Recycle Bin (for example on a network drive) is never deleted; its take is kept.",
            TextWrapping = TextWrapping.Wrap,
        });
        StackPanel rows = new() { Spacing = 6 };
        long largest = storage.Chapters.Select(item => item.TotalBytes).DefaultIfEmpty(0).Max();
        foreach (ChapterTakeStorage chapter in storage.Chapters)
        {
            string line = chapter.RemovableTakes == 0
                ? "nothing to clean up"
                : $"{chapter.RemovableTakes} take{(chapter.RemovableTakes == 1 ? "" : "s")} · frees {TakeCleanupRules.FormatBytes(chapter.RemovableBytes)}";
            if (chapter.KeptForMixTakes > 0) line += $" · {chapter.KeptForMixTakes} kept for a mix";
            rows.Children.Add(new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = $"{chapter.ChapterNumber:00}  {chapter.ChapterTitle}", FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = $"Takes on disk {TakeCleanupRules.FormatBytes(chapter.TotalBytes)} · approved {TakeCleanupRules.FormatBytes(chapter.ApprovedBytes)} · {line}",
                        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                    StorageBar(chapter.TotalBytes, chapter.ApprovedBytes, chapter.RemovableBytes, largest, new Thickness(0, 3, 0, 0)),
                },
            });
        }

        content.Children.Add(StorageLegend());
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, Padding = new Thickness(0, 0, 14, 0) });
        content.Children.Add(new TextBlock
        {
            Text = removable.Count == 0
                ? "Nothing to clean up: every approved segment has only its approved take, or its other takes are used in a mix."
                : $"Frees {TakeCleanupRules.FormatBytes(storage.RemovableBytes)} once the Recycle Bin is emptied.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        });

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Resources = { ["ContentDialogMaxWidth"] = 760.0 },
            Title = "Clean up takes",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = removable.Count == 0 ? string.Empty : $"Move {removable.Count} take{(removable.Count == 1 ? "" : "s")} to the Recycle Bin",
            CloseButtonText = removable.Count == 0 ? "Close" : "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || removable.Count == 0) return;
        if (StudioBusy)
        {
            await ShowMessageAsync("Wait for the current render", "A render or approval started while the dialog was open. Clean up once it finishes.");
            return;
        }

        _takeCleanupRunning = true;
        SetRenderQueueBusy(true);
        TakeCleanupResult result;
        try
        {
            // The take bar must not hold a file that is about to move.
            if (_transportRenderId is Guid loaded && removable.Contains(loaded))
            {
                StopChapterPlayback(announce: false);
                TakeTransport.ClearAudio("TAKE AUDIO CLEANED UP");
            }

            result = await Task.Run(() => App.Services.TakeCleanup.CleanUpAsync(projectRoot, removable));
            await App.Services.Session.RefreshAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Takes could not be cleaned up", Actionable(exception));
            return;
        }
        finally
        {
            _takeCleanupRunning = false;
            SetRenderQueueBusy(StudioBusy);
        }

        string summary = $"Moved {result.TakesRemoved} take{(result.TakesRemoved == 1 ? "" : "s")} ({TakeCleanupRules.FormatBytes(result.BytesRecycled)}) to the Recycle Bin.";
        if (result.Failures.Count == 0)
        {
            // A dialog, not a toast: clean-up also runs from Assembly, where Workbench toasts aren't visible.
            await ShowMessageAsync("Takes cleaned up", summary + " Empty the Recycle Bin to free the space.");
        }
        else
        {
            await ShowMessageAsync(
                result.TakesRemoved == 0 ? "No takes were cleaned up" : "Some takes were not cleaned up",
                $"{summary} These were kept because their files couldn't be moved (close any app that has them open, then try again):\n"
                + string.Join("\n", result.Failures.Take(6)) + (result.Failures.Count > 6 ? "\n…" : string.Empty));
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// A size bar: green for approved audio, amber for what clean-up frees, grey for takes that stay
    /// (segments awaiting review, takes a mix uses). Its length is relative to <paramref name="scale"/>.
    /// </summary>
    private static FrameworkElement StorageBar(long total, long approved, long removable, long scale, Thickness margin)
    {
        long kept = Math.Max(0, total - approved - removable);
        Grid track = new() { Height = 8, Margin = margin, HorizontalAlignment = HorizontalAlignment.Stretch };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(total, 0), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(scale - total, 0), GridUnitType.Star) });
        if (total <= 0) return track;
        Grid bar = new() { CornerRadius = new CornerRadius(4), ColumnSpacing = 1 };
        (long Bytes, string Brush)[] parts = [(approved, "ApprovalBrush"), (removable, "PickupBrush"), (kept, "MutedTextBrush")];
        foreach ((long bytes, string brush) in parts)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            if (bytes <= 0) continue;
            Border piece = new() { Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[brush] };
            Grid.SetColumn(piece, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(piece);
        }

        string description = $"{TakeCleanupRules.FormatBytes(total)} of takes: {TakeCleanupRules.FormatBytes(approved)} approved, "
            + $"{TakeCleanupRules.FormatBytes(removable)} clean-up can free, {TakeCleanupRules.FormatBytes(kept)} kept";
        ToolTipService.SetToolTip(bar, description);
        AutomationProperties.SetName(bar, description);
        track.Children.Add(bar);
        return track;
    }

    private static FrameworkElement StorageLegend()
    {
        StackPanel legend = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach ((string brush, string label) in new[]
                 {
                     ("ApprovalBrush", "Approved"),
                     ("PickupBrush", "Clean-up can free"),
                     ("MutedTextBrush", "Kept: awaiting review or used in a mix"),
                 })
        {
            legend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Border
                    {
                        Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                        Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[brush],
                    },
                    new TextBlock { Text = label, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["UtilityLabelTextStyle"] },
                },
            });
        }

        return legend;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private static async Task<string?> PickFolderAsync()
    {
        FolderPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
