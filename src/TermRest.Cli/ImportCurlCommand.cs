using System.CommandLine;
using TermRest.Core.Import;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary><c>termrest import-curl</c>: add a request to a collection from a curl command (argument or stdin).</summary>
internal static class ImportCurlCommand
{
    public static Command Create()
    {
        var command = new Command("import-curl",
            "Add a request to a collection from a curl command (bash, cmd or PowerShell syntax, e.g. from a browser's 'Copy as cURL').");
        var collection = new Argument<string>("collection") { Description = "Collection file or v3 folder, or a workspace with one collection" };
        var curl = new Argument<string?>("command") { Description = "The curl command (quoted); read from stdin when omitted or '-'", Arity = ArgumentArity.ZeroOrOne };
        var folder = new Option<string?>("--folder") { Description = "Folder path inside the collection (created when missing)" };
        var name = new Option<string?>("--name") { Description = "Request name (default: derived from the URL path)" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be imported without saving" };
        command.Arguments.Add(collection);
        command.Arguments.Add(curl);
        command.Options.Add(folder);
        command.Options.Add(name);
        command.Options.Add(dryRun);
        command.SetAction(parseResult =>
        {
            var text = parseResult.GetValue(curl) is { } given && given != "-" ? given : Console.In.ReadToEnd();
            CurlImportResult imported;
            try
            {
                imported = CurlImporter.Import(text);
            }
            catch (FormatException ex)
            {
                Output.Error(ex.Message);
                return 2;
            }

            foreach (var warning in imported.Warnings)
            {
                Output.Warning(warning);
            }

            var spec = imported.Spec;
            Output.Info($"{spec.Method} {spec.Url}");
            Output.Info($"  {spec.Headers.Count} header(s) · body {spec.Body.Mode}{(spec.Auth is { } auth ? $" · auth {auth.Type}" : "")}" +
                        $"{(spec.Certificate is not null ? " · client certificate" : "")}{(spec.Settings.StrictSsl == false ? " · SSL verification off" : "")}");
            if (parseResult.GetValue(dryRun))
            {
                return 0;
            }

            var target = Find(parseResult.GetValue(collection)!);
            if (target is null)
            {
                return 2;
            }

            var parent = (FolderNode)target.RootNode;
            if (parseResult.GetValue(folder) is { Length: > 0 } folderPath)
            {
                foreach (var part in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    parent = parent.Children.OfType<FolderNode>().FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase))
                             ?? target.AddFolder(parent, part);
                }
            }

            var node = target.AddRequest(parent, parseResult.GetValue(name) ?? imported.SuggestedName, spec);
            target.Save();
            Output.Status($"✓ Added '{node.PathInCollection}' to {target.Name} ({target.FormatName})", true);
            return 0;
        });
        return command;
    }

    private static PostmanCollection? Find(string path)
    {
        ApiWorkspace workspace;
        try
        {
            workspace = ApiWorkspace.Open(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Output.Error(ex.Message);
            return null;
        }

        if (workspace.FindCollection(path) is { } exact)
        {
            return exact;
        }

        if (workspace.Collections.Count == 1)
        {
            return workspace.Collections[0];
        }

        Output.Error(workspace.Collections.Count == 0
            ? $"No collection found in '{path}'."
            : $"'{path}' has {workspace.Collections.Count} collections; pass one of them: {string.Join(", ", workspace.Collections.Select(c => c.FilePath))}");
        return null;
    }
}
