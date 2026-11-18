using System.CommandLine;
using TermRest.Core.Http;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Scripting;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary>Options shared by <c>send</c> and <c>run</c>.</summary>
internal sealed class RequestOptions
{
    public Option<string?> Environment { get; } = new("--env", "-e") { Description = "Environment name in the workspace, or an environment file (*.postman_environment.json, or a v3 *.environment.yaml)" };

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

    public Option<string?> PowerShell { get; } = new("--powershell")
    {
        Description = "The pwsh (PowerShell 7 or later) that runs the collection's PowerShell scripts, e.g. pwsh (default: the one chosen for this workspace in the full-screen UI)",
    };

    public Option<string?> ScriptOrder { get; } = new("--script-order")
    {
        Description = "The order in which script languages are tried for requests that choose none (the first with all of a request's scripts runs them), " +
                      "e.g. powershell,javascript; the others follow (default: your order in the full-screen UI, Tools › Scripts…)",
    };

    public Option<bool> NoScripts { get; } = new("--no-scripts") { Description = "Do not run pre-request and test scripts" };

    public Option<bool> AllowOutsideFiles { get; } = new("--allow-outside-files")
    {
        Description = "Also send body and form-data files from outside the workspace folder (default: only where allowed for the workspace in the full-screen UI, Tools › Settings…)",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Environment);
        command.Options.Add(Variables);
        command.Options.Add(Timeout);
        command.Options.Add(Record);
        command.Options.Add(Python);
        command.Options.Add(JavaScript);
        command.Options.Add(CSharp);
        command.Options.Add(PowerShell);
        command.Options.Add(ScriptOrder);
        command.Options.Add(NoScripts);
        command.Options.Add(AllowOutsideFiles);
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
    /// <param name="stopping">Whether the command was cancelled (Ctrl+C): saving a large recording takes a while, so it says what a second Ctrl+C does.</param>
    public bool SaveRecording(ParseResult parseResult, RequestRunner runner, RequestRecorder? recorder, bool stopping)
    {
        if (recorder is null)
        {
            return true;
        }

        runner.Recorder = null;
        var path = parseResult.GetValue(Record)!;
        if (stopping)
        {
            Output.Info($"Saving the recording to {path}…");
            Output.Info($"  Ctrl+C again ends TermRest at once; open TermRest then and use Tools > Recover unsaved recordings ({recorder.TempDirectory}).");
        }

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
    /// <remarks>Of the workspace's collections, only the one given is loaded (all of them for a workspace folder).</remarks>
    public (ApiWorkspace Workspace, PostmanCollection? Collection, RequestRunner Runner)? Open(ParseResult parseResult, string path, Option<bool> insecure)
    {
        ApiWorkspace workspace;
        try
        {
            workspace = ApiWorkspace.Open(path, CollectionLoading.Requested);
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

        var scriptOrder = new List<string>();
        foreach (var name in (parseResult.GetValue(ScriptOrder) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ScriptLanguage.Parse(name) is not { } language)
            {
                Output.Error($"--script-order: '{name}' is not a script language (python, javascript, csharp or powershell).");
                return null;
            }

            scriptOrder.Add(language);
        }

        var runner = new RequestRunner(workspace)
        {
            StrictSslOverride = parseResult.GetValue(insecure) ? false : null,
            TimeoutOverrideMs = parseResult.GetValue(Timeout),
            ScriptsEnabled = !parseResult.GetValue(NoScripts),
            OAuth2Interaction = new ConsoleSignIn(),
        };

        if (runner.ScriptsEnabled)
        {
            runner.ConfigureScripts(Runtime(Python), Runtime(JavaScript), Runtime(CSharp), Runtime(PowerShell), scriptOrder: scriptOrder);
            if (runner.UserSettingsError is { } problem)
            {
                Output.Warning(problem);
                Output.Info("  The script runtimes chosen for this workspace and your script language order are not used until the file is fixed.");
            }
        }

        runner.ConfigureProxy();
        if (runner.BlockedProxy is { } proxy)
        {
            Output.Warning($"The proxy in {workspace.SettingsPath} ({PostmanUrl.MaskCredentials(proxy.Trim())}) is not used, as a cloned repository could send " +
                           "your requests through a proxy of its choosing; requests use the system's proxy settings. Allow it in the full-screen UI " +
                           "(Tools > Settings > Save), or set HTTPS_PROXY.");
        }

        runner.ConfigureFiles();
        runner.OutsideFilesAllowed |= parseResult.GetValue(AllowOutsideFiles);

        if (parseResult.GetValue(Environment) is { } environment)
        {
            if (File.Exists(environment))
            {
                try
                {
                    runner.EnvironmentOverride = PostmanEnvironment.Load(environment);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Output.Error($"Environment file '{environment}' could not be read: {ex.Message}");
                    runner.Dispose();
                    return null;
                }
            }
            else if (workspace.Environments.FindAll(e => string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase)) is { Count: > 0 } named)
            {
                if (named.Count > 1)
                {
                    Output.Error($"Environment '{environment}' is ambiguous: environments in these files have that name\n  " +
                                 string.Join("\n  ", named.Select(e => e.FilePath)) + "\nPass the file of the one to use with -e instead.");
                    runner.Dispose();
                    return null;
                }

                runner.EnvironmentOverride = named[0];
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

        // A runtime's path is relative to the current folder, as the other paths of the command line are (the runner takes it
        // as relative to the workspace, as it does the UI's choices); a command (node) stays a name, looked up on PATH.
        string? Runtime(Option<string?> option)
        {
            var runtime = parseResult.GetValue(option);
            var path = runtime?.Trim().Trim('"');
            return path is not null && path.IndexOfAny(['/', '\\']) >= 0 && !Path.IsPathRooted(path) ? Path.GetFullPath(path) : runtime;
        }
    }

    /// <summary>
    /// Reports workspace settings that could not be read (their warning is printed when the workspace is opened): no
    /// request is sent then, as they may set the proxy and TLS verification, turn the history off and name what
    /// recordings and reports redact.
    /// </summary>
    public static bool ReportUnreadableSettings(ApiWorkspace workspace)
    {
        if (workspace.SettingsError is null)
        {
            return false;
        }

        Output.Error($"The workspace settings could not be read (see the warning above), so no request is sent: they may set the proxy, " +
                     $"TLS verification, the history and what recordings redact. Fix or delete {workspace.SettingsPath}.");
        return true;
    }

    /// <summary>
    /// Reports when files of <paramref name="collection"/> could not be read (their warnings are printed when the
    /// workspace is opened): a v3 request file that was left out, or a definition whose auth and scripts are missing.
    /// The send or run fails then; the note about hidden non-HTTP requests is not such a problem.
    /// </summary>
    public static bool ReportUnreadableFiles(PostmanCollection collection)
    {
        if (!collection.HasUnreadableFiles)
        {
            return false;
        }

        Output.Error($"Files of '{collection.Name}' could not be read (see the warnings above): its requests may lack their auth, scripts and tests.");
        return true;
    }
}
