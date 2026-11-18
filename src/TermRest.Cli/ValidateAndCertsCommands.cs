using System.CommandLine;
using System.Globalization;
using TermRest.Core.Certificates;
using TermRest.Core.IO;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary><c>termrest validate</c>: proves that TermRest would save a collection without changing it.</summary>
internal static class ValidateCommand
{
    public static Command Create()
    {
        var command = new Command("validate",
            "Check that Postman files load and that TermRest saves them unchanged: JSON collections/environments round-trip " +
            "byte-for-byte; v3 collection folders and YAML environments convert losslessly.");
        var files = new Argument<string[]>("files") { Description = "Collection or environment files, or v3 collection folders", Arity = ArgumentArity.OneOrMore };
        command.Arguments.Add(files);
        command.SetAction(parseResult =>
        {
            var failures = 0;
            foreach (var file in parseResult.GetValue(files)!)
            {
                if (Directory.Exists(file) || PostmanEnvironment.IsYamlPath(file))
                {
                    failures += ValidateV3(file) ? 0 : 1;
                    continue;
                }

                try
                {
                    var (original, info) = TextFile.Read(file);
                    var isEnvironment = file.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
                                        || file.EndsWith(PostmanEnvironment.GlobalsSuffix, StringComparison.OrdinalIgnoreCase);
                    string serialized;
                    string summary;
                    if (isEnvironment)
                    {
                        var environment = PostmanEnvironment.Load(file);
                        serialized = TermRest.Core.Json.PostmanJson.Serialize(environment.Root);
                        summary = $"environment '{environment.Name}', {environment.Values.Count} value(s)";
                    }
                    else
                    {
                        var collection = PostmanCollection.Load(file);
                        serialized = collection.Serialize();
                        summary = $"collection '{collection.Name}' (schema {collection.SchemaVersion}), {collection.AllRequests().Count()} request(s)";
                    }

                    var written = TextFile.AsWritten(serialized, info);
                    if (written == original)
                    {
                        Output.Status($"✓ {file}: {summary}; round-trip identical", true);
                    }
                    else
                    {
                        failures++;
                        var (line, before, after) = FirstDifference(original, written);
                        Output.Status($"✗ {file}: {summary}; differs at line {line}", false);
                        Output.Info($"    original: {before}");
                        Output.Info($"    termrest: {after}");
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    failures++;
                    Output.Status($"✗ {file}: {ex.Message}", false);
                }
            }

            return failures == 0 ? 0 : 1;
        });
        return command;
    }

    /// <summary>v3 collection folder or YAML environment: loads, and checks that editing would not change other details.</summary>
    private static bool ValidateV3(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var collection = PostmanCollection.Load(path);
                var problems = collection.Verify();
                var wrapping = collection.EffectiveYamlLineWidth is > 0 and var width ? $", long values wrapped at {width} columns" : "";
                var summary = $"v3 collection '{collection.Name}', {collection.AllRequests().Count()} request(s){wrapping}";
                foreach (var warning in collection.LoadWarnings)
                {
                    Output.Warning($"  {warning}");
                }

                if (problems.Count == 0)
                {
                    Output.Status($"✓ {path}: {summary}; every file converts losslessly", true);

                    // Unreadable files are reported as load warnings; hidden non-HTTP requests are fine.
                    return !collection.HasUnreadableFiles;
                }

                Output.Status($"✗ {path}: {summary}; {problems.Count} file(s) would change when edited", false);
                foreach (var problem in problems)
                {
                    Output.Info($"    {problem}");
                }

                return false;
            }

            var environment = PostmanEnvironment.Load(path);
            var environmentWrapping = environment.EffectiveYamlLineWidth is > 0 and var columns ? $", long values wrapped at {columns} columns" : "";
            Output.Status($"✓ {path}: v3 environment '{environment.Name}', {environment.Values.Count} value(s){environmentWrapping}", true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            Output.Status($"✗ {path}: {ex.Message}", false);
            return false;
        }
    }

    private static (int Line, string Before, string After) FirstDifference(string a, string b)
    {
        var left = a.Split('\n');
        var right = b.Split('\n');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length ? left[i] : "<end of file>";
            var r = i < right.Length ? right[i] : "<end of file>";
            if (l != r)
            {
                if (l.EndsWith('\r') && r.EndsWith('\r'))
                {
                    (l, r) = (l[..^1], r[..^1]); // CRLF in both: no part of the difference
                }

                // Lines that differ in white space only (Postman indents with tabs) show it: \t, \r, and · for a space.
                return l.Trim() == r.Trim() ? (i + 1, ShowWhiteSpace(l), ShowWhiteSpace(r)) : (i + 1, l.Trim(), r.Trim());
            }
        }

        return (0, "", "");
    }

    private static string ShowWhiteSpace(string line) =>
        line.Replace("\t", "\\t", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace(' ', '·');
}

/// <summary><c>termrest certs …</c>: inspect the certificate registry and OS stores.</summary>
internal static class CertsCommand
{
    public static Command Create()
    {
        var command = new Command("certs", "Client certificate (mTLS) helpers.");

        var list = new Command("list", "List the workspace certificate registry and test that each certificate loads.");
        var workspaceArg = new Argument<string?>("workspace") { Description = "Workspace folder (default: current directory)", Arity = ArgumentArity.ZeroOrOne };
        var allowOutsideFiles = new Option<bool>("--allow-outside-files")
        {
            Description = "Also open certificate files on network shares outside the workspace folder (default: only where allowed for the workspace in the full-screen UI, Tools › Settings…)",
        };
        list.Arguments.Add(workspaceArg);
        list.Options.Add(allowOutsideFiles);
        list.SetAction(parseResult =>
        {
            if (OpenWorkspace(parseResult.GetValue(workspaceArg)) is not { } workspace)
            {
                return 2;
            }

            if (workspace.CertificatesError is not null)
            {
                return 1;
            }

            if (workspace.Certificates.Entries.Count == 0)
            {
                Output.Info($"No certificates registered in {workspace.CertificatesPath}.");
                return 0;
            }

            using var provider = new ClientCertificateProvider(workspace.Certificates);
            using var runner = new RequestRunner(workspace);
            runner.ConfigureFiles();
            runner.OutsideFilesAllowed |= parseResult.GetValue(allowOutsideFiles);
            var resolver = runner.CreateResolver(null);
            var failures = 0;
            foreach (var entry in workspace.Certificates.Entries)
            {
                Console.Out.WriteLine($"{entry.Name}{(entry.Disabled ? " (disabled)" : "")}");
                Console.Out.WriteLine($"  matches: {(entry.Matches.Count == 0 ? "none (sent to no server; it can sign OAuth 2.0 client assertions)" : string.Join(", ", entry.Matches))}");
                Console.Out.WriteLine($"  source:  {entry.DescribeSource()}");
                try
                {
                    var loaded = provider.Load(entry, workspace.RootPath, value =>
                        value is not null && value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? Environment.GetEnvironmentVariable(value[4..]) : resolver.Resolve(value),
                        runner.FilesFolder);
                    var s = loaded.Summary;
                    Output.Status($"  ✓ {s.Subject} · issuer {s.IssuerCommonName} · expires {s.NotAfter:yyyy-MM-dd}{(s.IsExpired ? " (EXPIRED)" : "")} · key {(s.HasPrivateKey ? "ok" : "MISSING")}", !s.IsExpired && s.HasPrivateKey);
                }
                catch (CertificateLoadException ex)
                {
                    failures++;
                    Output.Status($"  ✗ {ex.Message}", false);
                }
            }

            return failures == 0 ? 0 : 1;
        });

        var store = new Command("store", "List OS-store certificates with a private key (Windows store, macOS keychain, .NET store on Linux).");
        var location = new Option<string>("--location") { Description = "CurrentUser or LocalMachine", DefaultValueFactory = _ => "CurrentUser" };
        store.Options.Add(location);
        store.SetAction(parseResult =>
        {
            var certificates = CertificateLoader.ListStore(parseResult.GetValue(location)!);
            if (certificates.Count == 0)
            {
                Output.Info("No certificates with private keys found.");
            }

            foreach (var c in certificates)
            {
                Console.Out.WriteLine($"{c.Thumbprint}  {c.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{(c.IsExpired ? "*" : " ")}  {c.CommonName}  (issuer: {c.IssuerCommonName})");
            }

            return 0;
        });

        var match = new Command("match", "Show which registered certificate would be used for a URL.");
        var url = new Argument<string>("url") { Description = "An https:// URL" };
        var matchWorkspace = new Argument<string?>("workspace") { Description = "Workspace folder (default: current directory)", Arity = ArgumentArity.ZeroOrOne };
        match.Arguments.Add(url);
        match.Arguments.Add(matchWorkspace);
        match.SetAction(parseResult =>
        {
            if (OpenWorkspace(parseResult.GetValue(matchWorkspace)) is not { } workspace)
            {
                return 2;
            }

            if (workspace.CertificatesError is not null)
            {
                return 1;
            }

            if (!Uri.TryCreate(parseResult.GetValue(url), UriKind.Absolute, out var uri))
            {
                Output.Error("Enter an absolute URL.");
                return 2;
            }

            if (workspace.Certificates.FindBestMatch(uri) is { } best)
            {
                Console.Out.WriteLine($"{best.Entry.Name}  (pattern {best.Pattern}, {best.Entry.DescribeSource()})");
                return 0;
            }

            Output.Info($"No registered certificate matches {uri}.");
            return 1;
        });

        command.Subcommands.Add(list);
        command.Subcommands.Add(store);
        command.Subcommands.Add(match);
        return command;
    }

    /// <summary>
    /// Opens a workspace for the certificate commands (its collections are not needed) and reports what could not be
    /// read; a registry that could not be read is an error (see <see cref="ApiWorkspace.CertificatesError"/>). Null when
    /// the workspace does not exist.
    /// </summary>
    private static ApiWorkspace? OpenWorkspace(string? path)
    {
        ApiWorkspace workspace;
        try
        {
            workspace = ApiWorkspace.Open(path ?? Environment.CurrentDirectory, CollectionLoading.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Error(ex.Message);
            return null;
        }

        foreach (var error in workspace.LoadErrors)
        {
            if (error == workspace.CertificatesError)
            {
                Output.Error(error);
            }
            else
            {
                Output.Warning(error);
            }
        }

        return workspace;
    }
}
