using System.CommandLine;
using TermRest.Core.Http;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary>Options shared by <c>send</c> and <c>run</c>.</summary>
internal sealed class RequestOptions
{
    public Option<string?> Environment { get; } = new("--env", "-e") { Description = "Environment name in the workspace, or a *.postman_environment.json file" };

    public Option<string[]> Variables { get; } = new("--var") { Description = "Set a variable (highest precedence): --var key=value (repeatable)" };

    public Option<int?> Timeout { get; } = new("--timeout") { Description = "Request timeout in milliseconds" };

    public Option<string?> Record { get; } = new("--record")
    {
        Description = "Also record the requests and responses to this HAR file (JSON, as browser dev tools save them); secrets are redacted",
    };

    public Option<string?> Python { get; } = new("--python")
    {
        Description = "Python that runs the collection's Python scripts, e.g. .venv/bin/python (default: the one chosen for this workspace in the full-screen UI)",
    };

    public Option<string?> JavaScript { get; } = new("--javascript")
    {
        Description = "Node.js or Bun that runs the collection's JavaScript (Postman pm.*) scripts, e.g. node (default: the one chosen for this workspace in the full-screen UI)",
    };

    public Option<string?> CSharp { get; } = new("--csharp")
    {
        Description = "The dotnet (.NET SDK 10 or later) that builds and runs the collection's C# scripts, e.g. dotnet (default: the one chosen for this workspace in the full-screen UI)",
    };

    public Option<bool> NoScripts { get; } = new("--no-scripts") { Description = "Do not run pre-request and test scripts" };

    public void AddTo(Command command)
    {
        command.Options.Add(Environment);
        command.Options.Add(Variables);
        command.Options.Add(Timeout);
        command.Options.Add(Record);
        command.Options.Add(Python);
        command.Options.Add(JavaScript);
        command.Options.Add(CSharp);
        command.Options.Add(NoScripts);
    }

    /// <summary>
    /// Starts recording for <c>--record</c> (sent requests go to a private temporary file until <see cref="SaveRecording"/>).
    /// Returns false when the recording was asked for but cannot start (already reported).
    /// </summary>
    public bool StartRecording(ParseResult parseResult, ApiWorkspace workspace, RequestRunner runner, out RequestRecorder? recorder)
    {
        recorder = null;
        if (parseResult.GetValue(Record) is null)
        {
            return true;
        }

        try
        {
            recorder = RequestRecorder.Start(new RecordingOptions
            {
                WorkspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace.RootPath)),
                WorkspaceRoot = workspace.RootPath,
                RedactNames = workspace.Settings.RecordingRedact ?? [],
            });
            runner.Recorder = recorder;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Error($"The recording could not start: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the <c>--record</c> file. Returns false when it could not be written (reported; the recording can still be recovered).</summary>
    public bool SaveRecording(ParseResult parseResult, RequestRunner runner, RequestRecorder? recorder)
    {
        if (recorder is null)
        {
            return true;
        }

        runner.Recorder = null;
        var path = parseResult.GetValue(Record)!;
        try
        {
            var result = recorder.Save(path);
            Output.Info($"Recorded {(result.Requests == 1 ? "1 request" : $"{result.Requests} requests")} to {result.Path} " +
                        $"({HttpExchange.FormatSize(result.Bytes)}, {result.Redactions} secret value(s) redacted)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Output.Error($"The recording could not be saved to {path}: {ex.Message}");
            Output.Info($"  Nothing is lost: open TermRest and use Tools > Recover unsaved recordings ({recorder.TempDirectory}).");
            recorder.Dispose();
            return false;
        }
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
        if (File.Exists(path) || (Directory.Exists(path) && workspace.FindCollection(path) is not null))
        {
            collection = workspace.FindCollection(path);
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
            ScriptsEnabled = !parseResult.GetValue(NoScripts),
        };

        if (runner.ScriptsEnabled)
        {
            runner.ConfigureScripts(parseResult.GetValue(Python), parseResult.GetValue(JavaScript), parseResult.GetValue(CSharp));
        }

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
