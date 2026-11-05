namespace TermRest.Core.Tests.Infrastructure;

internal static class TestFiles
{
    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    public static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    public static string DemoCollection => Sample("TermRest Demo.postman_collection.json");

    public static string DemoEnvironment => Sample("Postman Echo.postman_environment.json");

    /// <summary>Copies a file into a fresh temp directory so tests can modify it.</summary>
    public static string CopyToTemp(string path, string? directory = null)
    {
        directory ??= NewTempDirectory();
        var target = Path.Combine(directory, Path.GetFileName(path));
        File.Copy(path, target, overwrite: true);
        return target;
    }

    public static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "termrest-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Lines that differ between two texts, for asserting that an edit touched only what it should.</summary>
    public static List<string> ChangedLines(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var changes = new List<string>();
        changes.AddRange(a.Except(b).Select(l => "- " + l.Trim()));
        changes.AddRange(b.Except(a).Select(l => "+ " + l.Trim()));
        return changes;
    }
}
