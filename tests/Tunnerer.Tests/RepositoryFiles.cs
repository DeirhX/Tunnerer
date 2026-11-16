using System.Runtime.CompilerServices;

namespace Tunnerer.Tests;

/// <summary>Finds the repository and its source files for the tests that read source code.</summary>
internal static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    /// <summary>Every .cs file under <paramref name="relativeDir"/>, skipping build output.</summary>
    public static IEnumerable<string> CSharpFiles(string relativeDir) =>
        Directory.EnumerateFiles(Path.Combine(Root, relativeDir), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal);

    /// <summary>Path relative to the repository root, with '/' separators on every OS.</summary>
    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    public static bool IsBuildOutput(string path) =>
        Relative(path).Split('/').Any(part => part is "obj" or "bin" or "out");

    // The compiler records this file's path, so the search does not depend on where the tests run from.
    private static string FindRoot([CallerFilePath] string sourcePath = "")
    {
        foreach (string? start in new[] { Path.GetDirectoryName(sourcePath), AppContext.BaseDirectory })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Tunnerer.slnx")))
                    return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not find Tunnerer.slnx above the test source or output directory.");
    }
}
