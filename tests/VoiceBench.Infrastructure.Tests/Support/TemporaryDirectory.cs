namespace IssueLab.VoiceBench.Infrastructure.Tests.Support;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "IssueLab.VoiceBench.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
