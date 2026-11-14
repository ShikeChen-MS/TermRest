using System.CommandLine;
using TermRest.Core.IO;
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
            Description = "Target folder (v3) or file (v2.1). Default: postman/collections/<name> in a v2.1 file's workspace; <name>.postman_collection.json in a v3 collection's workspace",
        };
        var lineWidth = new Option<int?>("--line-width")
        {
            Description = "YAML output: wrap long values at this column, like Postman's YAML library (0: never). " +
                          "Default: the workspace's yamlLineWidth setting, else no wrapping (like current Postman versions)",
        };
        to.AcceptOnlyFromAmong("v3", "v2.1");
        lineWidth.Validators.Add(result =>
        {
            if (result.GetValue(lineWidth) is int width && width != 0 && width is < WorkspaceSettings.MinYamlLineWidth or > WorkspaceSettings.MaxYamlLineWidth)
            {
                result.AddError($"--line-width must be 0 (no wrapping) or {WorkspaceSettings.MinYamlLineWidth} to {WorkspaceSettings.MaxYamlLineWidth}.");
            }
        });
        command.Arguments.Add(input);
        command.Options.Add(to);
        command.Options.Add(output);
        command.Options.Add(lineWidth);
        command.SetAction(parseResult =>
        {
            var path = Path.GetFullPath(parseResult.GetValue(input)!);
            var width = parseResult.GetValue(lineWidth) ?? WorkspaceLineWidth(path);
            try
            {
                return IsEnvironment(path)
                    ? ConvertEnvironment(path, parseResult.GetValue(to), parseResult.GetValue(output), width)
                    : ConvertCollection(path, parseResult.GetValue(to), parseResult.GetValue(output), width);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                Output.Error(ex.Message);
                return 1;
            }
        });
        return command;
    }

    /// <summary>The yamlLineWidth setting of the workspace that holds <paramref name="path"/> (its folder or a parent with a .termrest folder).</summary>
    private static int? WorkspaceLineWidth(string path)
    {
        var directory = Path.GetDirectoryName(path);
        for (var level = 0; directory is not null && level < 4; level++, directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ApiWorkspace.ConfigDirectoryName)))
            {
                return ApiWorkspace.ReadSettings(directory).YamlLineWidth;
            }
        }

        return null;
    }

    private static string Wrapping(int? width) => width is > 0 ? $" (long values wrapped at {width} columns)" : "";

    private static bool IsEnvironment(string path) =>
        File.Exists(path) && (path.EndsWith(PostmanEnvironment.EnvironmentSuffix, StringComparison.OrdinalIgnoreCase)
                              || path.EndsWith(PostmanEnvironment.GlobalsSuffix, StringComparison.OrdinalIgnoreCase)
                              || PostmanEnvironment.IsYamlPath(path));

    private static int ConvertCollection(string path, string? to, string? output, int? lineWidth)
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

        // Files that could not be read (a definition with the collection's auth, variables and scripts, say) would be
        // missing from the converted collection; the note about hidden non-HTTP requests is no such problem.
        if (source.HasUnreadableFiles)
        {
            Output.Error($"Files of '{source.Name}' could not be read (see the warnings above), so nothing was written: the converted collection would lack what they hold.");
            return 1;
        }

        if (toV3)
        {
            var target = output ?? Path.Combine(ApiWorkspace.RootForFile(path), ApiWorkspace.PostmanFolder, "collections", ApiWorkspace.SafeFileName(source.Name));
            var converted = source.ConvertToV3(target, lineWidth);
            var examples = Directory.EnumerateFiles(target, "*.example.yaml", SearchOption.AllDirectories).Count();
            Output.Status($"✓ {converted.AllRequests().Count()} request(s){(examples > 0 ? $" and {examples} example(s)" : "")} written to {target}{Wrapping(lineWidth)}", true);
            if (source.SecretValuesLeftOutOfV3() is { Count: > 0 } secrets)
            {
                Output.Info($"  The values of secret variables ({string.Join(", ", secrets)}) are not written: Postman's v3 collections keep them out of their files. Set them in an environment.");
            }

            return 0;
        }

        var file = output ?? Path.Combine(source.BaseDirectory, ApiWorkspace.SafeFileName(source.Name) + PostmanCollection.FileSuffix);
        if (File.Exists(file))
        {
            Output.Error($"'{file}' already exists; choose another --output.");
            return 2;
        }

        TextFile.WriteAtomic(file, source.ExportV21Json(), newFileMode: TextFile.CopyMode(path)); // as private as its source
        Output.Status($"✓ {source.AllRequests().Count()} request(s) written to {file}", true);
        if (Directory.EnumerateFiles(path, "*.example.yaml", SearchOption.AllDirectories).Any())
        {
            Output.Info("  Saved examples (*.example.yaml) are not included in the v2.1 file.");
        }

        if (source.CredentialSetsLeftOutOfV21() is { Count: > 0 } owners)
        {
            Output.Warning($"The auth of {string.Join(", ", owners)} has credential sets chosen by URL rules, which v2.1 can't hold: the v2.1 file has the first set only, and Postman sends it to every URL.");
        }

        return 0;
    }

    private static int ConvertEnvironment(string path, string? to, string? output, int? lineWidth)
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
            ? Path.Combine(ApiWorkspace.RootForFile(path), ApiWorkspace.PostmanFolder, environment.IsGlobals ? "globals" : "environments",
                environment.IsGlobals ? "workspace.globals.yaml" : ApiWorkspace.SafeFileName(environment.Name) + ".environment.yaml")
            : Path.Combine(directory, ApiWorkspace.SafeFileName(environment.Name) + (environment.IsGlobals ? PostmanEnvironment.GlobalsSuffix : PostmanEnvironment.EnvironmentSuffix)));
        if (File.Exists(target))
        {
            Output.Error($"'{target}' already exists; choose another --output.");
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        environment.YamlLineWidth = lineWidth;
        environment.Save(target); // as private as its source
        Output.Status($"✓ {environment.Values.Count} value(s) written to {target}{(toV3 ? Wrapping(lineWidth) : "")}", true);
        return 0;
    }
}
