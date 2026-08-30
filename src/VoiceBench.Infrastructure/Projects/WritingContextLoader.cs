using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class WritingContextLoader(IProjectRepository repository)
{
    public async Task<WritingContext> LoadAsync(
        string projectRoot,
        string chapterBrief,
        IReadOnlyCollection<string> selectedReferenceIds,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        HashSet<string> selected = selectedReferenceIds.ToHashSet(StringComparer.Ordinal);
        List<WritingReference> references = [];
        foreach (SourceReferenceDocument reference in project.SourceReferences.Where(item => selected.Contains(item.ReferenceId)))
        {
            string text = await File.ReadAllTextAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, reference.RelativePath),
                cancellationToken).ConfigureAwait(false);
            string hash = SourceTextHasher.Compute(text);
            if (!string.Equals(hash, reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Reference '{reference.Name}' changed outside VoiceBench. Re-import it before sending it to a writing provider.");
            }

            references.Add(new WritingReference(reference.Name, text, hash));
        }

        return new WritingContext(project.WritingProfile, chapterBrief, references);
    }
}
