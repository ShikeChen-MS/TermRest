using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Postman.V3;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>
/// Opt-in check against real Postman-written v3 collections (set TERMREST_V3_CORPUS to a folder containing them, e.g. a
/// checkout of repositories with postman/collections). Every collection is loaded and saved twice:
/// untouched, and after every section of every request was edited and edited back. Both saves must write nothing.
/// </summary>
public class PostmanV3CorpusTests
{
    [Fact]
    public void Real_world_collections_round_trip_without_changes()
    {
        var corpus = Environment.GetEnvironmentVariable("TERMREST_V3_CORPUS");
        if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus))
        {
            Assert.Skip("Set TERMREST_V3_CORPUS to a folder with Postman v3 collections to run.");
            return;
        }

        var work = Infrastructure.TestFiles.NewTempDirectory();
        CopyDirectory(corpus, work);

        // Every top-most folder that holds requests (partial checkouts work too).
        var candidates = Directory.EnumerateDirectories(work, "*", SearchOption.AllDirectories)
            .Where(d => !d.Contains(Path.DirectorySeparatorChar + V3Names.ResourcesDirectory, StringComparison.Ordinal) && V3Names.IsCollectionDirectory(d))
            .OrderBy(d => d.Length)
            .ToList();
        var collections = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!collections.Any(c => candidate.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                collections.Add(candidate);
            }
        }

        Assert.NotEmpty(collections);

        var problems = new List<string>();
        foreach (var directory in collections)
        {
            var before = Snapshot(directory);
            PostmanCollection collection;
            try
            {
                collection = PostmanCollection.Load(directory);
            }
            catch (Exception ex)
            {
                problems.Add($"{directory}: load failed: {ex.Message}");
                continue;
            }

            collection.MarkDirty();
            collection.Save();
            Report(problems, directory, "untouched save", before, Snapshot(directory));

            foreach (var node in collection.AllRequests().ToList())
            {
                var spec = node.ReadSpec();
                var touched = spec.Clone();
                touched.Url += "x";
                touched.QueryParams = PostmanUrl.SyncQueryFromRaw(touched.Url, touched.QueryParams);
                touched.Headers.Add(new KeyValueEntry { Key = "X-Touch", Value = "1" });
                touched.Body = new RequestBody { Mode = BodyMode.Raw, Raw = "touch" };
                touched.Auth = new AuthSpec { Type = "noauth" };
                touched.Settings = new RequestSettings { StrictSsl = !(spec.Settings.StrictSsl ?? true) };
                touched.PreRequestScript += "//";
                touched.TestScript += "//";
                touched.Description = (spec.Description ?? "") + "x";
                collection.UpdateRequest(node, touched);
                collection.UpdateRequest(node, spec);
            }

            collection.Save();
            Report(problems, directory, "edited and reverted", before, Snapshot(directory));
        }

        Assert.True(problems.Count == 0, $"{collections.Count} collections, {problems.Count} problem(s):\n" + string.Join("\n", problems));
        TestContext.Current.SendDiagnosticMessage($"{collections.Count} collections, {collections.Sum(c => Directory.EnumerateFiles(c, "*.request.yaml", SearchOption.AllDirectories).Count())} request files round-tripped.");
    }

    private static void Report(List<string> problems, string directory, string step, Dictionary<string, string> before, Dictionary<string, string> after)
    {
        foreach (var (file, text) in after)
        {
            if (!before.TryGetValue(file, out var original))
            {
                problems.Add($"{step}: new file {file}");
            }
            else if (original != text)
            {
                problems.Add($"{step}: {file}\n    {string.Join("\n    ", Infrastructure.TestFiles.ChangedLines(original, text))}");
            }
        }

        problems.AddRange(before.Keys.Where(k => !after.ContainsKey(k)).Select(k => $"{step}: deleted {k}"));
        _ = directory;
    }

    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
