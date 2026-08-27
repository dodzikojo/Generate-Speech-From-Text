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
    public void Main_window_exposes_all_four_studio_sections()
    {
        var document = XDocument.Load(RepositoryPaths.App("MainWindow.xaml"));
        var navigationItems = document.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .ToArray();

        var labels = navigationItems
            .Select(element => (string?)element.Attribute("Content"))
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("Projects", labels);
        Assert.Contains("Workbench", labels);
        Assert.Contains("Voice Lab", labels);
        Assert.Contains("Settings", labels);
        Assert.All(navigationItems, item => Assert.NotNull(item.Attribute("AutomationProperties.Name")));
        Assert.Equal(4, document.Descendants().Count(element => element.Name.LocalName == "KeyboardAccelerator"));
    }

    [Fact]
    public void Workbench_keeps_the_take_rail_and_status_vocabulary_visible()
    {
        var path = RepositoryPaths.App("Views/WorkbenchView.xaml");
        var source = File.ReadAllText(path);
        var codeBehind = File.ReadAllText(RepositoryPaths.App("Views/WorkbenchView.xaml.cs"));
        var document = XDocument.Load(path);

        Assert.Contains("TAKE RAIL", source, StringComparison.Ordinal);
        Assert.Contains("VoiceDirector", source, StringComparison.Ordinal);
        Assert.Contains("ScriptEditor", source, StringComparison.Ordinal);
        Assert.Contains("Build master", source, StringComparison.Ordinal);
        Assert.Contains("Reload", source, StringComparison.Ordinal);
        Assert.Contains("Keep current", source, StringComparison.Ordinal);
        Assert.Contains("Compare", source, StringComparison.Ordinal);
        Assert.Contains("PopulateTakeRail", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateTakeRailItemContent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("statusDot", codeBehind, StringComparison.Ordinal);
        Assert.Contains("takeText", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetSegmentStatusBrush", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FormatStatus", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SegmentStatus.PickupRequired", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("1 ACTIVE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedIndex=\"3\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"29,0,0,0\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Text = $\"{status}  ·  {takes}", codeBehind, StringComparison.Ordinal);

        var triggerWidths = document.Descendants()
            .Where(element => element.Name.LocalName == "AdaptiveTrigger")
            .Select(element => (string?)element.Attribute("MinWindowWidth"))
            .ToArray();

        Assert.Contains("860", triggerWidths);
        Assert.Contains("1180", triggerWidths);
        Assert.True(source.Split("AutomationProperties.Name=", StringSplitOptions.None).Length > 20,
            "Workbench controls need explicit accessible names, not color-only state.");
        Assert.True(document.Descendants().Count(element => element.Name.LocalName == "KeyboardAccelerator") >= 4);
    }

    [Fact]
    public void Voice_lab_requires_an_explicit_ab_choice()
    {
        var path = RepositoryPaths.App("Views/VoiceLabView.xaml");
        var source = File.ReadAllText(path);
        var document = XDocument.Load(path);

        Assert.Contains("AUDITION A", source, StringComparison.Ordinal);
        Assert.Contains("AUDITION B", source, StringComparison.Ordinal);
        Assert.Contains("No audition has been completed", source, StringComparison.Ordinal);
        Assert.Contains("never substitutes another voice silently", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use A as project default", source, StringComparison.Ordinal);
        Assert.Contains("Use B as project default", source, StringComparison.Ordinal);

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
        Assert.Contains("MediaPlayer", transportCode, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", transportCode, StringComparison.Ordinal);
        Assert.Contains("LoadAudio", transportCode, StringComparison.Ordinal);
        Assert.Contains("DisposePlayer", transportCode, StringComparison.Ordinal);
        Assert.Contains("AuditionATransport", voiceLab, StringComparison.Ordinal);
        Assert.Contains("AuditionBTransport", voiceLab, StringComparison.Ordinal);
        Assert.Contains("TakeTransport", workbench, StringComparison.Ordinal);
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
}
