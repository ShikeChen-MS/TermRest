using System.CommandLine;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary><c>termrest convert</c>: between Postman collection format v2.1 (JSON file) and v3 (YAML folder).</summary>
internal static class ConvertCommand
{
    public static Command Create()
    {
        var command = new Command("convert",
            "Convert a Postman collection or environment between v2.1 JSON and v3 YAML (the Postman v12 'Native Git' layout).");
        var input = new Argument<string>("input") { Description = "A *.postman_collection.json file, a v3 collection folder, or an environment file" };
        var to = new Option<string?>("--to") { Description = "Target format: v3 or v2.1 (default: the other one)" };
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Target folder (v3) or file (v2.1). Default: postman/collections/<name> next to a v2.1 file; <name>.postman_collection.json in a v3 collection's workspace",
        };
        to.AcceptOnlyFromAmong("v3", "v2.1");
        command.Arguments.Add(input);
        command.Options.Add(to);
        command.Options.Add(output);
        command.SetAction(parseResult =>
        {
            var path = Path.GetFullPath(parseResult.GetValue(input)!);
            try
            {
                return IsEnvironment(path)
                    ? ConvertEnvironment(path, parseResult.GetValue(to), parseResult.GetValue(output))
                    : ConvertCollection(path, parseResult.GetValue(to), parseResult.GetValue(output));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                Output.Error(ex.Message);
                return 1;
            }
        });
        return command;
    }

    private static bool IsEnvironment(string path) =>
        File.Exists(path) && (path.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
                              || path.EndsWith(PostmanEnvironment.GlobalsSuffix, StringComparison.OrdinalIgnoreCase)
                              || PostmanEnvironment.IsYamlPath(path));

    private static int ConvertCollection(string path, string? to, string? output)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Output.Error($"'{path}' does not exist.");
            return 2;
        }

        var source = PostmanCollection.Load(path);
        foreach (var warning in source.LoadWarnings)
        {
            Output.Warning(warning);
        }

        var toV3 = (to ?? (source.IsV3 ? "v2.1" : "v3")) == "v3";
        if (toV3 == source.IsV3)
        {
            Output.Error($"'{source.Name}' already is a {source.FormatName} collection.");
            return 2;
        }

        if (toV3)
        {
            var target = output ?? Path.Combine(Path.GetDirectoryName(path)!, ApiWorkspace.PostmanFolder, "collections", SafeName(source.Name));
            var converted = source.ConvertToV3(target);
            var examples = Directory.EnumerateFiles(target, "*.example.yaml", SearchOption.AllDirectories).Count();
            Output.Status($"✓ {converted.AllRequests().Count()} request(s){(examples > 0 ? $" and {examples} example(s)" : "")} written to {target}", true);
            return 0;
        }

        var file = output ?? Path.Combine(source.BaseDirectory, SafeName(source.Name) + PostmanCollection.FileSuffix);
        if (File.Exists(file))
        {
            Output.Error($"'{file}' already exists; choose another --output.");
            return 2;
        }

        File.WriteAllText(file, source.ExportV21Json());
        Output.Status($"✓ {source.AllRequests().Count()} request(s) written to {file}", true);
        if (Directory.EnumerateFiles(path, "*.example.yaml", SearchOption.AllDirectories).Any())
        {
            Output.Info("  Saved examples (*.example.yaml) are not included in the v2.1 file.");
        }

        return 0;
    }

    private static int ConvertEnvironment(string path, string? to, string? output)
    {
        var environment = PostmanEnvironment.Load(path);
        var toV3 = (to ?? (environment.IsYaml ? "v2.1" : "v3")) == "v3";
        if (toV3 == environment.IsYaml)
        {
            Output.Error($"'{environment.Name}' already is in that format.");
            return 2;
        }

        var directory = Path.GetDirectoryName(path)!;
        var target = output ?? (toV3
            ? Path.Combine(directory, ApiWorkspace.PostmanFolder, environment.IsGlobals ? "globals" : "environments",
                environment.IsGlobals ? "workspace.globals.yaml" : SafeName(environment.Name) + ".environment.yaml")
            : Path.Combine(directory, SafeName(environment.Name) + (environment.IsGlobals ? PostmanEnvironment.GlobalsSuffix : PostmanEnvironment.EnvironmentSuffix)));
        if (File.Exists(target))
        {
            Output.Error($"'{target}' already exists; choose another --output.");
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        environment.Save(target);
        Output.Status($"✓ {environment.Values.Count} value(s) written to {target}", true);
        return 0;
    }

    private static string SafeName(string name)
    {
        var safe = new string(name.Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "collection" : safe;
    }
}
