using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class WritingContextLoaderTests
{
    [Fact]
    public async Task LoadAsync_IncludesOnlyExplicitlySelectedReferences()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Writing");
        string selectedPath = "01-source/references/selected.md";
        string excludedPath = "01-source/references/excluded.md";
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, selectedPath), "Selected context");
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, excludedPath), "Private context");
        project = project with
        {
            SourceReferences =
            [
                new SourceReferenceDocument { ReferenceId = "selected", Name = "selected.md", RelativePath = selectedPath, Sha256 = SourceTextHasher.Compute("Selected context") },
                new SourceReferenceDocument { ReferenceId = "excluded", Name = "excluded.md", RelativePath = excludedPath, Sha256 = SourceTextHasher.Compute("Private context") },
            ],
        };
        await repository.SaveAsync(directory.Root, project);
        WritingContextLoader loader = new(repository);

        WritingContext context = await loader.LoadAsync(directory.Root, "Brief", ["selected"]);

        WritingReference reference = Assert.Single(context.References);
        Assert.Equal("selected.md", reference.Name);
        Assert.Equal("Selected context", reference.Text);
    }

    [Fact]
    public async Task LoadAsync_BlocksExternallyChangedReference()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Writing");
        string path = "01-source/references/reference.md";
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, path), "Changed");
        project = project with
        {
            SourceReferences =
            [new SourceReferenceDocument { ReferenceId = "reference", Name = "reference.md", RelativePath = path, Sha256 = SourceTextHasher.Compute("Original") }],
        };
        await repository.SaveAsync(directory.Root, project);
        WritingContextLoader loader = new(repository);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(directory.Root, "", ["reference"]));

        Assert.Contains("Re-import", exception.Message, StringComparison.Ordinal);
    }
}
