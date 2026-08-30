using IssueLab.VoiceBench.Cli;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RunAsync_UsesStableUsageExitCode()
    {
        CliApplication app = new(new FileProjectRepository(), new StringWriter(), new StringWriter());

        int exitCode = await app.RunAsync([]);

        Assert.Equal((int)CliExitCode.Usage, exitCode);
    }

    [Fact]
    public async Task ProjectCreateAndValidate_ReturnSuccess()
    {
        using TemporaryDirectory directory = new();
        StringWriter output = new();
        CliApplication app = new(new FileProjectRepository(), output, new StringWriter());

        int createCode = await app.RunAsync(["project", "create", directory.Root, "--name", "CLI fixture"]);
        int validateCode = await app.RunAsync(["project", "validate", directory.Root]);

        Assert.Equal((int)CliExitCode.Success, createCode);
        Assert.Equal((int)CliExitCode.Success, validateCode);
        Assert.Contains("Project is valid", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectValidate_ReturnsStableValidationExitCode()
    {
        using TemporaryDirectory directory = new();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName),
            "{ not json }");
        StringWriter error = new();
        CliApplication app = new(new FileProjectRepository(), new StringWriter(), error);

        int exitCode = await app.RunAsync(["project", "validate", directory.Root]);

        Assert.Equal((int)CliExitCode.ValidationFailure, exitCode);
        Assert.Contains("project.manifest-invalid", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Master_RejectsConflictingChapterAndCompilationScopes()
    {
        StringWriter error = new();
        CliApplication app = new(new FileProjectRepository(), new StringWriter(), error);

        int exitCode = await app.RunAsync(
            ["master", "project", "--name", "Final", "--chapter", "chapter-a", "--compilation"]);

        Assert.Equal((int)CliExitCode.Usage, exitCode);
        Assert.Contains("either --chapter", error.ToString(), StringComparison.Ordinal);
    }
}
