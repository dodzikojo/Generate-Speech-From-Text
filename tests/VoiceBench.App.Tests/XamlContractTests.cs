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
        Assert.Contains("FormatStatus", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SegmentStatus.PickupRequired", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("1 ACTIVE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedIndex=\"3\"", source, StringComparison.Ordinal);

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
        var source = File.ReadAllText(RepositoryPaths.App("Views/VoiceLabView.xaml"));

        Assert.Contains("AUDITION A", source, StringComparison.Ordinal);
        Assert.Contains("AUDITION B", source, StringComparison.Ordinal);
        Assert.Contains("No audition has been completed", source, StringComparison.Ordinal);
        Assert.Contains("never substitutes another voice silently", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use A as project default", source, StringComparison.Ordinal);
        Assert.Contains("Use B as project default", source, StringComparison.Ordinal);
    }
}
