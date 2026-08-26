namespace IssueLab.VoiceBench.App.Tests;

internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string App(string relativePath) =>
        Path.Combine(Root, "src", "VoiceBench.App", relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        foreach (var startingPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(startingPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "VoiceBench.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate VoiceBench.slnx from the test output directory.");
    }
}
