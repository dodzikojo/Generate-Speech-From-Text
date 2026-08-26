using System.Xml.Linq;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class PackagingContractTests
{
    [Fact]
    public void Package_uses_the_issuelab_voicebench_identity_and_desktop_target()
    {
        var document = XDocument.Load(RepositoryPaths.App("Package.appxmanifest"));
        var identity = document.Descendants().Single(element => element.Name.LocalName == "Identity");
        var target = document.Descendants().Single(element => element.Name.LocalName == "TargetDeviceFamily");
        var capability = document.Descendants().Single(element => element.Name.LocalName == "Capability");

        Assert.Equal("IssueLab.VoiceBench", (string?)identity.Attribute("Name"));
        Assert.Equal("CN=IssueLab", (string?)identity.Attribute("Publisher"));
        Assert.Equal("Windows.Desktop", (string?)target.Attribute("Name"));
        Assert.Equal("10.0.17763.0", (string?)target.Attribute("MinVersion"));
        Assert.Equal("runFullTrust", (string?)capability.Attribute("Name"));
    }

    [Fact]
    public void All_manifest_branding_paths_are_generated_from_source()
    {
        var manifestSource = File.ReadAllText(RepositoryPaths.App("Package.appxmanifest"));
        var projectSource = File.ReadAllText(RepositoryPaths.App("VoiceBench.App.csproj"));
        var generatorSource = File.ReadAllText(RepositoryPaths.App("Build/GeneratePlaceholderAssets.ps1"));
        var sourceAssetDirectory = RepositoryPaths.App("Assets");

        foreach (var assetName in new[]
                 {
                     "Square44x44Logo.png", "Square150x150Logo.png", "Wide310x150Logo.png", "SplashScreen.png"
                 })
        {
            Assert.Contains(assetName, manifestSource, StringComparison.Ordinal);
            Assert.Contains(assetName, projectSource, StringComparison.Ordinal);
            Assert.Contains(assetName, generatorSource, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(sourceAssetDirectory, "*.png", SearchOption.AllDirectories));
        Assert.Contains("BeforeTargets=\"PrepareForBuild\"", projectSource, StringComparison.Ordinal);
    }

    [Fact]
    public void App_manifest_declares_per_monitor_dpi_and_long_path_awareness()
    {
        var source = File.ReadAllText(RepositoryPaths.App("app.manifest"));

        Assert.Contains("PerMonitorV2", source, StringComparison.Ordinal);
        Assert.Contains("longPathAware", source, StringComparison.Ordinal);
        Assert.Contains("UTF-8", source, StringComparison.Ordinal);
    }
}
