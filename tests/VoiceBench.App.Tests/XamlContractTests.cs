using System.Xml.Linq;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class XamlContractTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Every_xaml_document_is_well_formed()
    {
        var xamlFiles = Directory.GetFiles(
            RepositoryPaths.App(string.Empty),
            "*.xaml",
            SearchOption.AllDirectories);

        Assert.NotEmpty(xamlFiles);
        foreach (var xamlFile in xamlFiles)
        {
            var exception = Record.Exception(() => XDocument.Load(xamlFile, LoadOptions.SetLineInfo));
            Assert.True(exception is null, $"{xamlFile} is not well-formed XML: {exception}");
        }
    }

    [Fact]
    public void Theme_encodes_the_ratified_clashwise_studio_tokens()
    {
        var themePath = RepositoryPaths.App("Styles/VoiceBenchTheme.xaml");
        var source = File.ReadAllText(themePath);
        var document = XDocument.Load(themePath);

        foreach (var color in new[]
                 {
                     "#0E1014", "#16181D", "#23262E", "#EDEFF3",
                     "#35C7E8", "#F49B52", "#58C58B", "#EF6A6A"
                 })
        {
            Assert.Contains(color, source, StringComparison.OrdinalIgnoreCase);
        }

        var fontFamilies = document.Descendants()
            .Where(element => element.Name.LocalName == "FontFamily")
            .Select(element => element.Value.Trim())
            .ToArray();

        Assert.Contains("Bahnschrift", fontFamilies);
        Assert.Contains("Segoe UI Variable Text", fontFamilies);
        Assert.Contains("Cascadia Mono", fontFamilies);
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "ResourceDictionary" &&
            string.Equals((string?)element.Attribute(XamlNamespace + "Key"), "HighContrast", StringComparison.Ordinal));
        Assert.Contains("FocusVisualPrimaryBrush", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Studio_density_is_compact_and_cue_colors_survive_editor_visual_states()
    {
        string themePath = RepositoryPaths.App("Styles/VoiceBenchTheme.xaml");
        XDocument theme = XDocument.Load(themePath);
        string shell = File.ReadAllText(RepositoryPaths.App("MainWindow.xaml"));
        string workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        string FontSizeFor(string styleKey)
        {
            XElement style = theme.Descendants().Single(element =>
                element.Name.LocalName == "Style" &&
                string.Equals((string?)element.Attribute(XamlNamespace + "Key"), styleKey, StringComparison.Ordinal));
            return (string?)style.Elements().Single(element =>
                element.Name.LocalName == "Setter" &&
                string.Equals((string?)element.Attribute("Property"), "FontSize", StringComparison.Ordinal)).Attribute("Value")
                ?? throw new InvalidOperationException($"{styleKey} has no FontSize setter.");
        }

        Assert.Equal("24", FontSizeFor("PageTitleTextStyle"));
        Assert.Equal("14", FontSizeFor("StudioHeadingTextStyle"));
        Assert.Equal("14", FontSizeFor("ReadingTextStyle"));
        Assert.Equal("10", FontSizeFor("UtilityLabelTextStyle"));
        Assert.Equal("13", FontSizeFor("SignalButtonStyle"));
        Assert.Equal("13", FontSizeFor("QuietButtonStyle"));
        Assert.Contains("FontSize=\"15\"", workbench, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=\"18\"", workbench, StringComparison.Ordinal);

        Assert.Contains("PointerEntered=\"ScriptTextBox_PointerStateChanged\"", workbench, StringComparison.Ordinal);
        Assert.Contains("PointerExited=\"ScriptTextBox_PointerStateChanged\"", workbench, StringComparison.Ordinal);
        Assert.Contains("GotFocus=\"ScriptTextBox_FocusStateChanged\"", workbench, StringComparison.Ordinal);
        Assert.Contains("LostFocus=\"ScriptTextBox_FocusStateChanged\"", workbench, StringComparison.Ordinal);
        Assert.Contains("private void ScriptTextBox_PointerStateChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private void ScriptTextBox_FocusStateChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsPaneOpen=\"False\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_exposes_all_six_studio_sections_with_shortcuts()
    {
        var document = XDocument.Load(RepositoryPaths.App("MainWindow.xaml"));
        var navigationItems = document.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .ToArray();

        var labels = navigationItems
            .Select(element => (string?)element.Attribute("Content") ?? string.Empty)
            .ToArray();

        Assert.Equal(["Projects", "Workbench", "Voice Lab", "Audio", "Assembly", "Settings"], labels);
        Assert.All(navigationItems, item => Assert.NotNull(item.Attribute("AutomationProperties.Name")));
        Assert.All(navigationItems, item => Assert.Single(item.Descendants(), element => element.Name.LocalName == "KeyboardAccelerator"));
        Assert.Equal(
            ["Number1", "Number2", "Number3", "Number4", "Number5", "Number6"],
            navigationItems.SelectMany(item => item.Descendants())
                .Where(element => element.Name.LocalName == "KeyboardAccelerator")
                .Select(element => (string?)element.Attribute("Key") ?? string.Empty)
                .ToArray());
    }

    [Fact]
    public void Title_bar_project_menu_opens_every_project_output_folder()
    {
        string shell = File.ReadAllText(RepositoryPaths.App("MainWindow.xaml"));
        string shellCode = File.ReadAllText(RepositoryPaths.App("MainWindow.xaml.cs"));

        Assert.Contains("<TitleBar x:Name=\"AppTitleBar\"", shell, StringComparison.Ordinal);
        Assert.Contains("IsPaneToggleButtonVisible=\"True\"", shell, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProjectMenuButton\"", shell, StringComparison.Ordinal);
        foreach (string folder in new[] { "Root", "GeneratedAudio", "Masters", "Scripts", "Exports", "Reports" })
            Assert.Contains($"Tag=\"{folder}\" Click=\"OpenProjectFolder_Click\"", shell, StringComparison.Ordinal);
        Assert.Contains("ShellLauncher.OpenProjectFolderAsync(folder)", shellCode, StringComparison.Ordinal);
        Assert.Contains("SetTitleBar(AppTitleBar)", shellCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_exposes_chapter_authoring_writing_and_responsive_direction()
    {
        var path = RepositoryPaths.App("Views/WorkbenchView.xaml");
        var source = File.ReadAllText(path);
        var codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        var document = XDocument.Load(path);

        Assert.Contains("CHAPTER RAIL", source, StringComparison.Ordinal);
        Assert.Contains("ChapterTree", source, StringComparison.Ordinal);
        Assert.Contains("TreeView", source, StringComparison.Ordinal);
        Assert.Contains("Writing assistant", source, StringComparison.Ordinal);
        Assert.Contains("DirectorPanel", source, StringComparison.Ordinal);
        Assert.Contains("ScriptEditor", source, StringComparison.Ordinal);
        Assert.Contains("Open Assembly", source, StringComparison.Ordinal);
        Assert.Contains("New chapter", source, StringComparison.Ordinal);
        Assert.Contains("Draft with AI", source, StringComparison.Ordinal);
        Assert.Contains("Import scripts", source, StringComparison.Ordinal);
        Assert.Contains("Reload", source, StringComparison.Ordinal);
        Assert.Contains("Keep current", source, StringComparison.Ordinal);
        Assert.Contains("Compare", source, StringComparison.Ordinal);
        Assert.Contains("PopulateChapterTree", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateChapterFromDraftAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowWritingDiffAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WritingAction_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetSegmentStatusBrush", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FormatStatus", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SegmentStatus.PickupRequired", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CompactSegmentPicker", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WorkbenchHeaderActions\"", source, StringComparison.Ordinal);
        Assert.Contains("IsDynamicOverflowEnabled=\"True\"", source, StringComparison.Ordinal);
        // Layout geometry is computed from the view width in one place, not split between VSM and code.
        Assert.DoesNotContain("Target=\"DirectorRow.Height\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Target=\"TakeRailColumn.Width\"", source, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkspacePanelLayout(width, height)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("bool stackInspector = width < 700", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GridUnitType.Star", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TextChanged=\"DirectionTextBox_TextChanged\"", source, StringComparison.Ordinal);
        Assert.Contains("_directionsDirty", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Directions = normalizedDirections", codeBehind, StringComparison.Ordinal);

        var triggerWidths = document.Descendants()
            .Where(element => element.Name.LocalName == "AdaptiveTrigger")
            .Select(element => (string?)element.Attribute("MinWindowWidth"))
            .ToArray();

        Assert.Contains("860", triggerWidths);
        Assert.Contains("1180", triggerWidths);
        Assert.True(source.Split("AutomationProperties.Name=", StringSplitOptions.None).Length > 8,
            "Semantic Workbench regions and icon-only controls need explicit accessible names.");
        Assert.True(document.Descendants().Count(element => element.Name.LocalName == "KeyboardAccelerator") >= 4);
    }

    [Fact]
    public void Workbench_authoring_editor_supports_plain_text_production_cues()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("<RichEditBox", xaml, StringComparison.Ordinal);
        Assert.Contains("ClipboardCopyFormat=\"PlainText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource BaseBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource ReadingTextBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("InsertCue_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("InsertCustomCue_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ChangeCueType_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ChangeCustomCueType_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ConvertLineToCue_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ConvertLineToCustomCue_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("RemoveCueMarker_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("Generate production cues", xaml, StringComparison.Ordinal);
        Assert.Contains("Refine cues", xaml, StringComparison.Ordinal);
        Assert.Contains("ProductionScriptParser", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplyCueFormatting", codeBehind, StringComparison.Ordinal);
        Assert.Contains("text.Length == 0", codeBehind, StringComparison.Ordinal);
        Assert.Contains("exception is UnauthorizedAccessException or COMException", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_cueFormattingRetryCount", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TextGetOptions.None", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TextSetOptions.None", codeBehind, StringComparison.Ordinal);
        Assert.Contains("bool wasEnabled = ScriptTextBox.IsEnabled", codeBehind, StringComparison.Ordinal);
        Assert.Contains("bool wasReadOnly = ScriptTextBox.IsReadOnly", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.IsEnabled = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.IsReadOnly = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.IsReadOnly = wasReadOnly", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.IsEnabled = wasEnabled", codeBehind, StringComparison.Ordinal);
        Assert.Contains("string.Equals(currentText, _lastEditorText", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.CreateTimer()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(150)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_cueFormatTimer.IsRepeating = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_cueFormatTimer.Stop()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_cueFormatTimer.Start()", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundColor =", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RunTrackedJobAsync<ProductionExportResult>", codeBehind, StringComparison.Ordinal);
        Assert.Contains("\"exports.create\"", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_status_overlay_and_editor_text_size_do_not_disrupt_the_workspace()
    {
        string path = RepositoryPaths.App("Views/WorkbenchView.xaml");
        string xaml = File.ReadAllText(path);
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        XDocument document = XDocument.Load(path);

        XElement status = document.Descendants()
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "WorkbenchInfoBar");
        Assert.Equal("WorkbenchRoot", (string?)status.Parent?.Attribute(XamlNamespace + "Name"));
        Assert.Equal("Top", (string?)status.Attribute("VerticalAlignment"));
        Assert.Equal("20", (string?)status.Attribute("Canvas.ZIndex"));
        Assert.Contains("WorkbenchInfoBar.Width = Math.Max", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Target=\"WorkbenchHeaderActions.(Grid.Row)\" Value=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"WorkbenchHeaderActions.(Grid.Column)\" Value=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"PanelVisibilityControls.(Grid.Column)\" Value=\"2\"", xaml, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"EditorFontSizeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorFontSizeSlider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"12\" Maximum=\"24\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Workbench.EditorFontSize", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplicationData.Current.LocalSettings.Values[EditorFontSizeSetting]", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.FontSize = fontSize", codeBehind, StringComparison.Ordinal);
        Assert.Contains("QueueCueFormatting()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_editor_console_keeps_tools_compact_and_split_transaction_current()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("IsDynamicOverflowEnabled=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ClosedDisplayMode=\"Compact\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Label=\"Pronunciation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Label=\"Split\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ScriptEditorActions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<RichEditBox x:Name=\"ScriptTextBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PlaybackScriptSurface\"", xaml, StringComparison.Ordinal);

        Assert.Contains("_suppressProjectChangedReload", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _projectChangeVersion)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("version != _projectChangeVersion", codeBehind, StringComparison.Ordinal);
        Assert.Contains("string originalSegmentId = _segment.SegmentId", codeBehind, StringComparison.Ordinal);
        Assert.Contains("chapter.SegmentOrder.IndexOf(originalSegmentId)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_inspector_tabs_and_script_footer_keep_their_labels_readable()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string takes = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.Takes.cs"));

        Assert.Contains("<SelectorBar x:Name=\"InspectorTabs\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Voice\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Takes\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Write\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Voice director\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Writing assistant\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Workbench.InspectorTab", takes, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource ReadingTextBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ScriptPathText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxLines=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding Text, ElementName=ScriptPathText}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_proposal_preview_uses_selectable_wrapped_text_panes()
    {
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("CreateWritingReviewPane(\"Original\", original)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Text = ToMultilineDisplayText(text)", codeBehind, StringComparison.Ordinal);
        Assert.Contains(".Replace(\"\\n\", Environment.NewLine, StringComparison.Ordinal)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsTextSelectionEnabled = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWritingDiffTextBox", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_critique_is_actionable_and_long_reviews_are_responsive()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("x:Name=\"WritingActivityPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WritingActivityRing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WritingActivityText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WritingActionButtons\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Revise from critique", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WritingAction.ApplyCritique", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateWritingCritiqueContent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateWritingComparison", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new TabView", codeBehind, StringComparison.Ordinal);
        Assert.Contains("comparisonHost.SizeChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplyWritingComparisonLayout", codeBehind, StringComparison.Ordinal);
        Assert.Contains("EstimateWritingComparisonHeight", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateWritingReviewTab(\"Proposal\", proposed, reviewHeight)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualHeight - 360", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MaxWidth = 1120", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Width = WritingReviewWidth()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetWritingActivity(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowWritingConfirmation(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Proposal rejected", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FontSize = 14", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Nothing has changed yet", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Assembly_exposes_persisted_order_inclusion_blockers_and_scoped_builds()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml.cs"));

        Assert.Contains("AssemblyChaptersList", xaml, StringComparison.Ordinal);
        Assert.Contains("Build compilation", xaml, StringComparison.Ordinal);
        Assert.Contains("Compilation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MasterScope.Chapter", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IncludedInCompilation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MasterEligibilityEvaluator", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ChapterAuthoring.MoveChapterAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_lab_keeps_audition_optional_and_allows_direct_voice_selection()
    {
        var path = RepositoryPaths.App("Views/VoiceLabView.xaml");
        var source = File.ReadAllText(path);
        var document = XDocument.Load(path);

        Assert.Contains("AUDITION A", source, StringComparison.Ordinal);
        Assert.Contains("AUDITION B", source, StringComparison.Ordinal);
        Assert.Contains("Auditioning is optional", source, StringComparison.Ordinal);
        Assert.Contains("never substitutes another voice silently", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use A settings as project voice", source, StringComparison.Ordinal);
        Assert.Contains("Use B settings as project voice", source, StringComparison.Ordinal);

        string workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string workbenchCode = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        Assert.Contains("Use as project voice", workbench, StringComparison.Ordinal);
        Assert.Contains("GetOrCreateSelectedVoicePreset", workbenchCode, StringComparison.Ordinal);
        Assert.Contains("ProjectVoiceSelectionRules.SetProjectVoice", workbenchCode, StringComparison.Ordinal);
        Assert.Contains("ProjectVoiceSelectionRules.SetProjectVoice", File.ReadAllText(RepositoryPaths.App("Views/VoiceLabView.xaml.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("A/B audition required", workbench, StringComparison.Ordinal);
        Assert.DoesNotContain("has not completed the project A/B audition", workbenchCode, StringComparison.Ordinal);

        XElement auditionBColumn = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "AuditionBColumn", StringComparison.Ordinal));
        XElement auditionBCard = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "AuditionBCard", StringComparison.Ordinal));
        XElement narrowState = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "VisualState" &&
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "NarrowVoiceLab", StringComparison.Ordinal));

        Assert.Equal("*", (string?)auditionBColumn.Attribute("Width"));
        Assert.Equal("0", (string?)auditionBCard.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)auditionBCard.Attribute("Grid.Column"));
        Assert.Contains(narrowState.Descendants(), element =>
            string.Equals((string?)element.Attribute("Target"), "AuditionBCard.(Grid.Row)", StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Value"), "1", StringComparison.Ordinal));
    }

    [Fact]
    public void Voice_selectors_are_populated_from_provider_descriptors()
    {
        var voiceLab = File.ReadAllText(RepositoryPaths.App("Views/VoiceLabView.xaml.cs"));
        var workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("App.Services.SpeechProviders", voiceLab, StringComparison.Ordinal);
        Assert.Contains("PopulateProviderControls", voiceLab, StringComparison.Ordinal);
        Assert.Contains("App.Services.SpeechProviders", workbench, StringComparison.Ordinal);
        Assert.Contains("PopulateProviderControls", workbench, StringComparison.Ordinal);
        Assert.Contains("ProviderComboBox_SelectionChanged", workbench, StringComparison.Ordinal);
        Assert.Contains("ModelComboBox_SelectionChanged", workbench, StringComparison.Ordinal);
        Assert.Contains("VoiceComboBoxItemFactory.Create", voiceLab, StringComparison.Ordinal);
        Assert.Contains("VoiceComboBoxItemFactory.Create", workbench, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_transport_is_reusable_accessible_and_present_at_audio_decisions()
    {
        var transportPath = RepositoryPaths.App("Controls/AudioTransport.xaml");
        var transportSource = File.ReadAllText(transportPath);
        var transportCode = File.ReadAllText(RepositoryPaths.App("Controls/AudioTransport.xaml.cs"));
        var voiceLab = File.ReadAllText(RepositoryPaths.App("Views/VoiceLabView.xaml"));
        var workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));

        Assert.Contains("PlayPauseButton", transportSource, StringComparison.Ordinal);
        Assert.Contains("RestartButton", transportSource, StringComparison.Ordinal);
        Assert.Contains("PositionSlider", transportSource, StringComparison.Ordinal);
        Assert.Contains("CurrentTimeText", transportSource, StringComparison.Ordinal);
        Assert.Contains("DurationText", transportSource, StringComparison.Ordinal);
        Assert.True(transportSource.Split("AutomationProperties.Name=", StringSplitOptions.None).Length >= 4,
            "Playback controls need explicit accessible names.");
        Assert.DoesNotContain("MediaPlayer", transportCode, StringComparison.Ordinal);
        Assert.Contains("App.Services.AudioPlayback", transportCode, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", transportCode, StringComparison.Ordinal);
        Assert.Contains("LoadAudio", transportCode, StringComparison.Ordinal);
        Assert.Contains("LoadPlaybackFileAsync", transportCode, StringComparison.Ordinal);
        Assert.Contains("AuditionATransport", voiceLab, StringComparison.Ordinal);
        Assert.Contains("AuditionBTransport", voiceLab, StringComparison.Ordinal);
        Assert.Contains("TakeTransport", workbench, StringComparison.Ordinal);
        Assert.Contains("TakeReviewContext", workbench, StringComparison.Ordinal);
        Assert.Contains("TakeDecisionGroup", workbench, StringComparison.Ordinal);
        Assert.Contains("AssemblyHandoff", workbench, StringComparison.Ordinal);
        Assert.Contains("Request pickup", workbench, StringComparison.Ordinal);
        Assert.Contains("Approve take", workbench, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_review_exposes_persisted_non_destructive_word_follow_controls()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        string transport = File.ReadAllText(RepositoryPaths.App("Controls/AudioTransport.xaml.cs"));

        Assert.Contains("x:Name=\"AlignmentStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AlignmentProgressRing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AlignWordsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FollowScriptToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PanelToggleButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("FollowScriptSetting", workbench, StringComparison.Ordinal);
        Assert.Contains("ApplicationData.Current.LocalSettings.Values[FollowScriptSetting]", workbench, StringComparison.Ordinal);
        Assert.Contains("WordFollowPlaybackRules.FindActiveWord", workbench, StringComparison.Ordinal);
        Assert.Contains("word.AuthoringStart", workbench, StringComparison.Ordinal);
        Assert.Contains("PlaybackScriptText.TextHighlighters", workbench, StringComparison.Ordinal);
        Assert.Contains("EndFollowSession", workbench, StringComparison.Ordinal);
        Assert.Contains("PlaybackScriptSurface.Visibility = Visibility.Visible", workbench, StringComparison.Ordinal);
        int positionHandlerStart = workbench.IndexOf("private void TakeTransport_PlaybackPositionChanged", StringComparison.Ordinal);
        int stateHandlerStart = workbench.IndexOf("private void TakeTransport_PlaybackStateChanged", StringComparison.Ordinal);
        Assert.True(positionHandlerStart >= 0 && stateHandlerStart > positionHandlerStart);
        Assert.DoesNotContain("QueueCueFormatting", workbench[positionHandlerStart..stateHandlerStart], StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptTextBox", workbench[positionHandlerStart..stateHandlerStart], StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(50)", transport, StringComparison.Ordinal);
        Assert.Contains("PlaybackPositionChanged", transport, StringComparison.Ordinal);
        Assert.Contains("PlaybackStateChanged", transport, StringComparison.Ordinal);
        Assert.Contains("UpdatePositionUi(position)", transport, StringComparison.Ordinal);
    }

    [Fact]
    public void Chapter_rail_templates_bind_through_tree_view_node_content()
    {
        var workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));

        Assert.Contains("{Binding Content.Title}", workbench, StringComparison.Ordinal);
        Assert.Contains("{Binding Content.Metadata}", workbench, StringComparison.Ordinal);
        Assert.Contains("{Binding Content.StatusBrush}", workbench, StringComparison.Ordinal);
    }

    [Fact]
    public void Duration_is_estimated_before_rendering_and_measured_after_a_current_take()
    {
        string workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        string assembly = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml.cs"));
        string assemblyXaml = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml"));
        string projects = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml.cs"));

        Assert.Contains("LoadEstimatesAsync", workbench, StringComparison.Ordinal);
        Assert.Contains("NarrationDurationProjection.Resolve", workbench, StringComparison.Ordinal);
        Assert.Contains("LATEST TAKE", workbench, StringComparison.Ordinal);
        Assert.Contains("EST.", workbench, StringComparison.Ordinal);
        Assert.Contains("NarrationDurationProjection.Summarize", assembly, StringComparison.Ordinal);
        Assert.Contains("EXPECTED DURATION", assemblyXaml, StringComparison.Ordinal);
        Assert.Contains("DurationBasisText", assemblyXaml, StringComparison.Ordinal);
        Assert.Contains("expected", projects, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renderedDuration", projects, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_marshals_session_refreshes_to_the_ui_dispatcher()
    {
        string projects = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml.cs"));

        Assert.Contains("private void Session_ProjectChanged", projects, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(async () => await UpdateRecentProjectsAsync())", projects, StringComparison.Ordinal);
        Assert.DoesNotContain("private async void Session_ProjectChanged", projects, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_separates_plain_text_undo_from_cue_formatting_and_durable_history()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        string appServices = File.ReadAllText(RepositoryPaths.App("Services/AppServices.cs"));
        string workspaceSession = File.ReadAllText(RepositoryPaths.App("Services/WorkspaceSession.cs"));

        Assert.Contains("x:Name=\"UndoAuthoringButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RedoAuthoringButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AuthoringHistoryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LabelPosition=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Z\" Modifiers=\"Control\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Y\" Modifiers=\"Control\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.Document.UndoLimit = 0", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_editorUndoHistory", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_editorRedoHistory", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_pendingEditorUndo", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UndoAccelerator_Invoked", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RedoAccelerator_Invoked", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplyEditorSnapshot", codeBehind, StringComparison.Ordinal);
        Assert.Contains("string.Equals(currentText, _lastEditorText", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptTextBox.Document.CanUndo()", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptTextBox.Document.CanRedo()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.Document.ClearUndoRedoHistory()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.Document.SetDefaultCharacterFormat(defaultFormat)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplyDurableHistoryAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowAuthoringHistory_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ExecuteAuthoringTransactionAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AuthoringTransactions = new FileAuthoringTransactionManager()", appServices, StringComparison.Ordinal);
        Assert.Contains("RecoverInterruptedAsync", workspaceSession, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_authoring_state_blocks_the_editor_and_receives_focus()
    {
        string workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("x:Name=\"EmptyNewChapterButton\"", workbench, StringComparison.Ordinal);
        Assert.Contains("Canvas.ZIndex=\"10\"", workbench, StringComparison.Ordinal);
        Assert.Contains("SetEditorAvailability", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptEditor.IsHitTestVisible = hasSegment", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.IsEnabled = hasSegment", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptTextBox.IsReadOnly = !hasSegment", codeBehind, StringComparison.Ordinal);
        Assert.Contains("EmptyNewChapterButton.Focus(FocusState.Programmatic)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_panels_are_collapsible_and_rail_actions_are_contextual()
    {
        string workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("ChapterRailToggle", workbench, StringComparison.Ordinal);
        Assert.Contains("DirectorToggle", workbench, StringComparison.Ordinal);
        Assert.Contains("TakeReviewToggle", workbench, StringComparison.Ordinal);
        Assert.Contains("PanelToggleButtonStyle", workbench, StringComparison.Ordinal);
        Assert.Contains("Symbol=\"Bullets\"", workbench, StringComparison.Ordinal);
        Assert.Contains("Symbol=\"Edit\"", workbench, StringComparison.Ordinal);
        Assert.Contains("Symbol=\"Audio\"", workbench, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"Chapter rail\"", workbench, StringComparison.Ordinal);
        Assert.Contains("RailItem_RightTapped", workbench, StringComparison.Ordinal);
        Assert.Contains("Delete selected…", workbench, StringComparison.Ordinal);
        Assert.Contains("Open selected", workbench, StringComparison.Ordinal);
        Assert.Contains("Show in folder", workbench, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"WrapWholeWords\"", workbench, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkspacePanelLayout", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LoadWorkspacePanelPreferences", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SaveWorkspacePanelPreferences", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ApplicationData.Current.LocalSettings.Values", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DeleteSelected_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScriptFileMonitor_ScriptChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HandleExternalScriptChangeAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await App.Services.Session.SaveAsync(project)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Launcher.LaunchFileAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("02-scripts/.trash", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Studio_surfaces_reflow_for_narrow_mobile_sized_windows()
    {
        var projects = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml"));
        var voiceLab = File.ReadAllText(RepositoryPaths.App("Views/VoiceLabView.xaml"));
        var workbench = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        var settings = File.ReadAllText(RepositoryPaths.App("Views/SettingsView.xaml"));
        var transport = File.ReadAllText(RepositoryPaths.App("Controls/AudioTransport.xaml"));
        var shell = File.ReadAllText(RepositoryPaths.App("MainWindow.xaml"));
        var settingsCode = File.ReadAllText(RepositoryPaths.App("Views/SettingsView.xaml.cs"));
        var responsiveCodeBehind = new[]
        {
            RepositoryPaths.App("Views/ProjectsView.xaml.cs"),
            RepositoryPaths.App("Views/VoiceLabView.xaml.cs"),
            RepositoryPaths.App("Views/WorkbenchView.xaml.cs"),
            RepositoryPaths.App("Views/SettingsView.xaml.cs"),
            RepositoryPaths.App("Controls/AudioTransport.xaml.cs"),
        }.Select(File.ReadAllText).ToArray();

        Assert.Contains("ProjectsWidthStates", projects, StringComparison.Ordinal);
        Assert.Contains("ProjectCards", projects, StringComparison.Ordinal);
        Assert.Contains("AuditionBCard", voiceLab, StringComparison.Ordinal);
        Assert.Contains("RenderBothButton", voiceLab, StringComparison.Ordinal);
        Assert.Contains("WorkbenchFooter", workbench, StringComparison.Ordinal);
        Assert.Contains("SettingsWidthStates", settings, StringComparison.Ordinal);
        Assert.Contains("TransportWidthStates", transport, StringComparison.Ordinal);
        Assert.Contains("PaneDisplayMode=\"Auto\"", shell, StringComparison.Ordinal);

        Assert.DoesNotContain("MinWidth=\"460\"", projects, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth = 460", settingsCode, StringComparison.Ordinal);

        Assert.All(responsiveCodeBehind, source =>
            Assert.Contains("VisualStateManager.GoToState", source, StringComparison.Ordinal));

        foreach (string source in new[] { projects, voiceLab, workbench, settings, transport })
        {
            Assert.Contains("AdaptiveTrigger", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Standard_pages_use_the_shared_centered_frame_layout()
    {
        var pagePaths = new[]
        {
            "Views/ProjectsView",
            "Views/VoiceLabView",
            "Views/SettingsView",
        };

        foreach (string pagePath in pagePaths)
        {
            string xaml = File.ReadAllText(RepositoryPaths.App($"{pagePath}.xaml"));
            string codeBehind = File.ReadAllText(RepositoryPaths.App($"{pagePath}.xaml.cs"));

            Assert.Contains("HorizontalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
            Assert.Contains("StudioPageFrame.Resize", codeBehind, StringComparison.Ordinal);
        }

        string layoutHelper = File.ReadAllText(RepositoryPaths.App("Layout/StudioPageFrame.cs"));
        Assert.Contains("availableWidth - (horizontalGutter * 2)", layoutHelper, StringComparison.Ordinal);
        Assert.Contains("Math.Min(maxContentWidth", layoutHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_empty_state_centers_its_primary_action()
    {
        var document = XDocument.Load(RepositoryPaths.App("Views/ProjectsView.xaml"));
        XElement createButton = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            string.Equals((string?)element.Attribute("Content"), "Create the first project", StringComparison.Ordinal));

        Assert.Equal("Center", (string?)createButton.Attribute("HorizontalAlignment"));
    }

    [Fact]
    public void Projects_page_renders_the_persisted_catalog_without_legacy_placeholder_content()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml.cs"));
        string windowCodeBehind = File.ReadAllText(RepositoryPaths.App("MainWindow.xaml.cs"));

        Assert.Contains("RecentProjectsList", xaml, StringComparison.Ordinal);
        Assert.Contains("RecentProjectsEmptyText", xaml, StringComparison.Ordinal);
        Assert.Contains("RecentProject_Click", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy ClashWise narration", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FindSampleProject", codeBehind, StringComparison.Ordinal);
        Assert.Contains("App.Services.RecentProjects.GetAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RestoreMostRecentAsync", windowCodeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void New_project_dialog_can_browse_for_its_parent_folder()
    {
        var document = XDocument.Load(RepositoryPaths.App("Views/ProjectsView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml.cs"));
        XElement dialog = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "ContentDialog" &&
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "NewProjectDialog", StringComparison.Ordinal));
        XElement folderBox = Assert.Single(dialog.Descendants(), element =>
            element.Name.LocalName == "TextBox" &&
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "ProjectFolderBox", StringComparison.Ordinal));
        XElement browseButton = Assert.Single(dialog.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            string.Equals((string?)element.Attribute("Click"), "BrowseProjectFolder_Click", StringComparison.Ordinal));

        Assert.Equal("Parent folder", (string?)folderBox.Attribute("Header"));
        Assert.Equal("Browse…", (string?)browseButton.Attribute("Content"));
        Assert.NotNull(browseButton.Attribute("AutomationProperties.Name"));
        Assert.Contains("ProjectFolderBox.Text = folder", codeBehind, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(ProjectNameBox.Text)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new DirectoryInfo(folder).Name", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_app_has_a_visual_studio_msix_launch_profile()
    {
        string path = RepositoryPaths.App("Properties/launchSettings.json");
        Assert.True(File.Exists(path), "The packaged WinUI project requires Properties/launchSettings.json for Visual Studio F5 deployment.");

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        System.Text.Json.JsonElement profiles = document.RootElement.GetProperty("profiles");
        System.Text.Json.JsonProperty packageProfile = profiles.EnumerateObject().First();
        Assert.Equal("VoiceBench.App (Package)", packageProfile.Name);
        Assert.Equal("MsixPackage", packageProfile.Value.GetProperty("commandName").GetString());
        Assert.Equal("Project", profiles.GetProperty("VoiceBench.App (Unpackaged)").GetProperty("commandName").GetString());
    }

    [Fact]
    public void Packaged_app_is_enabled_for_visual_studio_deployment()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryPaths.Root, "VoiceBench.slnx"));
        var appProjectDocument = XDocument.Load(RepositoryPaths.App("VoiceBench.App.csproj"));
        XElement appProject = Assert.Single(solution.Descendants(), element =>
            element.Name.LocalName == "Project" &&
            string.Equals(
                ((string?)element.Attribute("Path"))?.Replace('\\', '/'),
                "src/VoiceBench.App/VoiceBench.App.csproj",
                StringComparison.OrdinalIgnoreCase));

        Assert.Contains(appProject.Elements(), element => element.Name.LocalName == "Deploy");
        XElement appPlatform = Assert.Single(
            appProject.Elements(), element => element.Name.LocalName == "Platform");
        Assert.Equal("x64", (string?)appPlatform.Attribute("Project"));

        string[] solutionPlatforms = solution.Descendants()
            .Where(element => element.Name.LocalName == "Platform")
            .Select(element => (string?)element.Attribute("Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        Assert.Equal(["x64"], solutionPlatforms);
        Assert.Contains(appProjectDocument.Descendants(), element =>
            element.Name.LocalName == "ProjectCapability" &&
            string.Equals((string?)element.Attribute("Include"), "Msix", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_profile_does_not_replace_the_debug_layout_with_an_msix_archive()
    {
        var profile = XDocument.Load(RepositoryPaths.App("Properties/PublishProfiles/win-x64.pubxml"));
        string[] propertyNames = profile.Descendants()
            .Select(element => element.Name.LocalName)
            .ToArray();

        Assert.DoesNotContain("GenerateAppxPackageOnBuild", propertyNames);
        Assert.DoesNotContain("Configuration", propertyNames);
        Assert.Contains("PublishDir", propertyNames);
    }

    [Fact]
    public void Workbench_voice_selection_never_uses_a_control_type_name_as_a_voice()
    {
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.DoesNotContain("VoiceComboBox.SelectedItem?.ToString()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetSelectedContentOrNull(VoiceComboBox)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new ComboBoxItem { Content = content, Tag = content }", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Chapter_rail_stretches_across_the_editor_and_director_rows()
    {
        var document = XDocument.Load(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        XElement rail = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Border" &&
            string.Equals((string?)element.Attribute(XamlNamespace + "Name"), "TakeRailPanel", StringComparison.Ordinal));

        Assert.Equal("2", (string?)rail.Attribute("Grid.RowSpan"));
        Assert.Equal("Stretch", (string?)rail.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void Cue_formatting_retries_after_editor_load_and_status_messages_wrap()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));

        Assert.Contains("Loaded=\"ScriptTextBox_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WorkbenchInfoBarMessage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxCueFormattingRetries", codeBehind, StringComparison.Ordinal);
        Assert.Contains("QueueCueFormatting(resetFailures: false)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_cueFormattingUnavailable = true", codeBehind, StringComparison.Ordinal);

        int followStart = codeBehind.IndexOf("private void BeginFollowSession", StringComparison.Ordinal);
        int positionStart = codeBehind.IndexOf("private void TakeTransport_PlaybackPositionChanged", StringComparison.Ordinal);
        Assert.True(followStart >= 0 && positionStart > followStart);
        Assert.Contains("PlaybackScriptSurface.Visibility = Visibility.Visible", codeBehind[followStart..positionStart], StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptTextBox.Document.Selection.SetRange", codeBehind[followStart..positionStart], StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_gives_the_editor_the_height_and_keeps_the_mix_timeline_opt_in()
    {
        var document = XDocument.Load(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        XElement layout = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(XamlNamespace + "Name") == "WorkbenchLayout");
        string[] rows = layout.Elements().Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements().Select(row => (string?)row.Attribute("Height") ?? "*").ToArray();
        XElement columns = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(XamlNamespace + "Name") == "WorkbenchColumns");
        XElement dockToggle = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(XamlNamespace + "Name") == "TimelineDockToggle");
        XElement dockHost = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(XamlNamespace + "Name") == "TimelineDockHost");

        Assert.Equal("*", rows[(int?)columns.Attribute("Grid.Row") ?? 0]);
        Assert.Equal(1, rows.Count(height => height == "*"));
        Assert.Equal("False", (string?)dockToggle.Attribute("IsChecked"));
        Assert.Equal("Collapsed", (string?)dockHost.Attribute("Visibility"));
        Assert.Contains("TakeTransport.IsCompact = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("values[TimelineDockVisibilitySetting] is true", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_audio_is_one_click_from_the_take_bar_takes_tab_and_rail()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string takes = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.Takes.cs"));

        Assert.Contains("x:Name=\"RevealTakeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TakeList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"local:TakeListItem\"", xaml, StringComparison.Ordinal);
        foreach (string handler in new[] { "TakeRowReveal_Click", "TakeRowOpen_Click", "TakeRowCopyPath_Click", "TakeRowApprove_Click",
                     "TakeRowPlay_Click", "OpenTakesFolder_Click", "OpenGeneratedAudioFolder_Click", "ShowSelectedTakesFolder_Click", "RevealScript_Click" })
        {
            Assert.Contains($"Click=\"{handler}\"", xaml, StringComparison.Ordinal);
            Assert.Contains($"void {handler}(", takes, StringComparison.Ordinal);
        }

        Assert.Contains("ShellLauncher.RevealFileAsync(path)", takes, StringComparison.Ordinal);
        Assert.Contains("TakeListPresentation.Build(segment)", takes, StringComparison.Ordinal);
        Assert.Contains("ProjectLocations.ResolveInsideProject", takes, StringComparison.Ordinal);
    }

    [Fact]
    public void Chapter_playback_plays_approved_takes_in_order_and_ends_on_manual_changes()
    {
        string xaml = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml"));
        string playback = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.ChapterPlayback.cs"));
        string codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        string transport = File.ReadAllText(RepositoryPaths.App("Controls/AudioTransport.xaml.cs"));
        string assembly = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml.cs"));

        Assert.Contains("x:Name=\"PlayChapterButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ChapterPlaybackPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PlayChapterFromHere_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Space\" Modifiers=\"Control,Shift\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ChapterPlaylistBuilder.Build", playback, StringComparison.Ordinal);
        Assert.Contains("AdvanceChapterPlaybackIfEnded(e.State)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("EndChapterPlaybackForManualChange(GetSelectedRender()?.RenderId)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TransportPhaseRules.Next", transport, StringComparison.Ordinal);
        Assert.Contains("window.PlayChapterAsync(chapterId)", assembly, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_assembly_audio_and_voice_lab_reveal_their_files()
    {
        string projects = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml"));
        string projectsCode = File.ReadAllText(RepositoryPaths.App("Views/ProjectsView.xaml.cs"));
        string assembly = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml"));
        string assemblyCode = File.ReadAllText(RepositoryPaths.App("Views/AssemblyView.xaml.cs"));
        string audio = File.ReadAllText(RepositoryPaths.App("Views/AudioView.xaml"));
        string timeline = File.ReadAllText(RepositoryPaths.App("Controls/AudioTimelineControl.xaml.cs"));
        string voiceLab = File.ReadAllText(RepositoryPaths.App("Views/VoiceLabView.xaml"));

        Assert.Contains("Click=\"ShowProjectInFolder_Click\"", projects, StringComparison.Ordinal);
        Assert.Contains("Click=\"RemoveRecentProject_Click\"", projects, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProjectFilterBox\"", projects, StringComparison.Ordinal);
        Assert.Contains("App.Services.RecentProjects.RemoveAsync(projectRoot)", projectsCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MastersList\"", assembly, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenMastersFolder_Click\"", assembly, StringComparison.Ordinal);
        Assert.Contains("ProjectLocations.ListMasters(root)", assemblyCode, StringComparison.Ordinal);
        Assert.Contains("loadVersion != _loadVersion", assemblyCode, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Masters\"", audio, StringComparison.Ordinal);
        Assert.Contains("RunTrackedJobAsync<IssueLab.VoiceBench.Infrastructure.Audio.MixExportResult>(\"mix.export\"", timeline, StringComparison.Ordinal);
        Assert.Contains("Click=\"RevealAudition_Click\"", voiceLab, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_visual_states_live_on_the_root_so_wide_windows_use_the_wide_layout()
    {
        var document = XDocument.Load(RepositoryPaths.App("Views/SettingsView.xaml"));
        XElement groups = Assert.Single(document.Descendants(), element => element.Name.LocalName == "VisualStateManager.VisualStateGroups");
        string code = File.ReadAllText(RepositoryPaths.App("Views/SettingsView.xaml.cs"));

        Assert.Equal("SettingsShell", (string?)groups.Parent?.Attribute(XamlNamespace + "Name"));
        Assert.Same(document.Root!.Elements().Single(), groups.Parent);
        Assert.Contains("$\"Replace {providerName} key\"", code, StringComparison.Ordinal);
    }
}
