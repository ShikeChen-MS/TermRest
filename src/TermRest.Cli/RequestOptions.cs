using System.CommandLine;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary>Options shared by <c>send</c> and <c>run</c>.</summary>
internal sealed class RequestOptions
{
    public Option<string?> Environment { get; } = new("--env", "-e") { Description = "Environment name in the workspace, or a *.postman_environment.json file" };

    public Option<string[]> Variables { get; } = new("--var") { Description = "Set a variable (highest precedence): --var key=value (repeatable)" };

    public Option<int?> Timeout { get; } = new("--timeout") { Description = "Request timeout in milliseconds" };

    public void AddTo(Command command)
    {
        command.Options.Add(Environment);
        command.Options.Add(Variables);
        command.Options.Add(Timeout);
    }

    /// <summary>Opens the workspace for a collection file or folder and configures a runner. Returns null on error (already reported).</summary>
    public (ApiWorkspace Workspace, PostmanCollection? Collection, RequestRunner Runner)? Open(ParseResult parseResult, string path, Option<bool> insecure)
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

        foreach (var error in workspace.LoadErrors)
        {
            Output.Warning(error);
        }

        PostmanCollection? collection = null;
        if (File.Exists(path))
        {
            var full = Path.GetFullPath(path);
            collection = workspace.Collections.FirstOrDefault(c => string.Equals(c.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (collection is null)
            {
                Output.Error($"'{path}' is not a Postman collection.");
                return null;
            }
        }

        var runner = new RequestRunner(workspace)
        {
            StrictSslOverride = parseResult.GetValue(insecure) ? false : null,
            TimeoutOverrideMs = parseResult.GetValue(Timeout),
        };

        if (parseResult.GetValue(Environment) is { } environment)
        {
            if (File.Exists(environment))
            {
                runner.EnvironmentOverride = PostmanEnvironment.Load(environment);
            }
            else if (workspace.Environments.FirstOrDefault(e => string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                runner.EnvironmentOverride = named;
            }
            else
            {
                Output.Error($"Environment '{environment}' was not found (available: {string.Join(", ", workspace.Environments.Select(e => e.Name).DefaultIfEmpty("none"))}).");
                runner.Dispose();
                return null;
            }
        }

        foreach (var assignment in parseResult.GetValue(Variables) ?? [])
        {
            var equals = assignment.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                Output.Error($"--var expects key=value, got '{assignment}'.");
                runner.Dispose();
                return null;
            }

            runner.LocalVariables[assignment[..equals]] = assignment[(equals + 1)..];
        }

        return (workspace, collection, runner);
    }
}
