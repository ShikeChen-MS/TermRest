using TermRest.Core.Certificates;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Variables;

namespace TermRest.Core.Workspace;

/// <summary>
/// Ties a workspace to the HTTP engine: builds variable scopes (local overrides &gt; environment &gt; collection &gt; globals,
/// each with the values scripts set in this session on top), resolves inherited auth, selects client certificates, runs
/// scripts and keeps a cookie jar for the session. Used by both the TUI and CLI.
/// </summary>
public sealed partial class RequestRunner : IDisposable
{
    private readonly List<ClientCertificateProvider> _retiredProviders = [];

    public RequestRunner(ApiWorkspace workspace)
    {
        Workspace = workspace;
        Certificates = new ClientCertificateProvider(workspace.Certificates);
    }

    public ApiWorkspace Workspace { get; }

    public ClientCertificateProvider Certificates { get; private set; }

    public HttpExecutor Executor { get; } = new();

    /// <summary>Highest-precedence variables (CLI <c>--var</c>, values captured during a run).</summary>
    public Dictionary<string, string> LocalVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>Overrides the workspace's active environment (e.g. CLI <c>--env file.json</c>).</summary>
    public PostmanEnvironment? EnvironmentOverride { get; set; }

    /// <summary>Overrides SSL verification for every request (CLI <c>--insecure</c>).</summary>
    public bool? StrictSslOverride { get; set; }

    public int? TimeoutOverrideMs { get; set; }

    /// <summary>Where sent requests are recorded; null turns recording off (see <see cref="ConfigureHistory"/>).</summary>
    public HistoryStore? History { get; set; }

    /// <summary>While set, every sent request is also recorded into a HAR file (see <see cref="RequestRecorder"/>).</summary>
    public RequestRecorder? Recorder { get; set; }

    /// <summary>Largest response body kept with a history entry.</summary>
    public int HistoryMaxBodyBytes { get; set; } = 256 * 1024;

    public PostmanEnvironment? Environment => EnvironmentOverride ?? Workspace.ActiveEnvironment;

    /// <summary>Turns history recording on or off according to the workspace settings.</summary>
    /// <param name="stateDirectory">Overrides the per-user state folder (tests, portable setups).</param>
    public void ConfigureHistory(string? stateDirectory = null)
    {
        var settings = Workspace.Settings;
        History = settings.HistoryEnabled ? HistoryStore.ForWorkspace(Workspace.RootPath, settings.HistoryLimit, stateDirectory) : null;
        HistoryMaxBodyBytes = Math.Max(0, settings.HistoryResponseKb) * 1024;
    }

    /// <param name="requestVariables">Variables scripts set for this request only (<c>tr.variables</c>); null unsets.</param>
    public VariableResolver CreateResolver(PostmanCollection? collection, IReadOnlyDictionary<string, string?>? requestVariables = null)
    {
        var scopes = new List<IVariableScope>();
        if (LocalVariables.Count > 0 || requestVariables is { Count: > 0 })
        {
            scopes.Add(new OverlayScope(new DictionaryScope("local", LocalVariables), requestVariables));
        }

        if (Environment is { } environment)
        {
            scopes.Add(new OverlayScope(new EntryListScope($"environment '{environment.Name}'", environment.Values), Session.Find(environment)));
        }

        if (collection is not null)
        {
            scopes.Add(new OverlayScope(new EntryListScope("collection", collection.GetVariables()), Session.Find(collection)));
        }

        if (Workspace.Globals is not null || Session.Globals.Count > 0)
        {
            scopes.Add(new OverlayScope(new EntryListScope("globals", Workspace.Globals?.Values ?? []), Session.Globals));
        }

        return new VariableResolver(scopes);
    }

    public ExecutionDefaults CreateDefaults()
    {
        var settings = Workspace.Settings;
        var timeoutMs = TimeoutOverrideMs ?? settings.TimeoutMs;
        return new ExecutionDefaults
        {
            StrictSsl = StrictSslOverride ?? settings.StrictSsl,
            CaBundlePath = string.IsNullOrWhiteSpace(settings.CaBundle) ? null : Workspace.ResolvePath(settings.CaBundle),
            Timeout = timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : null,
            FollowRedirects = settings.FollowRedirects,
            MaxRedirects = settings.MaxRedirects,
            Proxy = settings.Proxy,
            MaxResponseBytes = Math.Max(1, settings.MaxResponseSizeMb) * 1024L * 1024L,
            HttpVersion = settings.HttpVersion,
        };
    }

    public PreparedRequest Prepare(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec) => Prepare(collection, node, spec, spec, null);

    /// <param name="spec">The request to send (as changed by pre-request scripts).</param>
    /// <param name="source">The request as written: kept in the history, so re-sending it runs its scripts again.</param>
    /// <param name="requestVariables">Variables set by scripts for this request only.</param>
    private PreparedRequest Prepare(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec, HttpRequestSpec source,
        IReadOnlyDictionary<string, string?>? requestVariables)
    {
        var preparer = new RequestPreparer(CreateResolver(collection, requestVariables), Certificates, CreateDefaults());
        var auth = node is not null ? collection.GetEffectiveAuth(node, spec) : spec.Auth ?? collection.RootNode.Auth;
        var prepared = preparer.Prepare(spec, auth, collection.BaseDirectory);
        var saved = node is not null && node.Collection == collection && collection.FilePath is not null && node.Parent is not null;

        // History keeps a self-contained copy: inherited auth is stored with the request so it can be re-sent on its own.
        var snapshot = source.Clone();
        snapshot.Auth ??= auth?.Clone();
        prepared.Source = new RequestSource(
            snapshot,
            collection.FilePath is null ? null : collection.Name,
            saved ? node!.PathInCollection : null,
            saved ? node!.Name : null);
        prepared.SecretValues.UnionWith(SecretVariableValues(collection));
        prepared.SensitiveValues.UnionWith(VariablesNamedLikeSecrets(collection, preparer.Variables, requestVariables));
        return prepared;
    }

    /// <summary>Sends a prepared request and records it in <see cref="History"/> and <see cref="Recorder"/> (when on).</summary>
    public async Task<HttpExchange> SendAsync(PreparedRequest prepared, CancellationToken cancellationToken = default)
    {
        // No ConfigureAwait(false): UI callers resume on their own thread, which also records the entry.
        var exchange = await Executor.SendAsync(prepared, cancellationToken).ConfigureAwait(true);
        Record(exchange);
        Recorder?.Record(exchange, Environment?.Name);
        return exchange;
    }

    public Task<HttpExchange> SendAsync(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec, CancellationToken cancellationToken = default) =>
        SendAsync(collection, node, spec, null, cancellationToken);

    private void Record(HttpExchange exchange)
    {
        var request = exchange.Request;
        if (History is not { } history || request.Source is not { } source || exchange.Cancelled
            || (exchange.Response is null && (request.Errors.Count > 0 || request.Uri is null)))
        {
            return; // nothing was sent
        }

        try
        {
            var entry = HistoryEntry.Create(exchange, source, Environment?.Name, HistoryMaxBodyBytes, out var body);
            history.Add(entry, body);
            exchange.HistoryEntry = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            request.Warnings.Add($"The request was not added to the history: {ex.Message}");
        }
    }

    /// <summary>Values of variables typed "secret" in the environment, collection and globals, including values scripts set.</summary>
    private List<string> SecretVariableValues(PostmanCollection collection)
    {
        var secrets = new List<string>();
        Add(Environment?.Values, Environment is { } environment ? Session.Find(environment) : null);
        Add(collection.GetVariables(), Session.Find(collection));
        Add(Workspace.Globals?.Values, Session.Globals);
        return secrets;

        void Add(IEnumerable<KeyValueEntry>? entries, IReadOnlyDictionary<string, string?>? setByScripts)
        {
            foreach (var entry in entries ?? [])
            {
                if (entry.Enabled && entry.IsSecret)
                {
                    if (!string.IsNullOrEmpty(entry.Value))
                    {
                        secrets.Add(entry.Value);
                    }

                    if (setByScripts?.GetValueOrDefault(entry.Key) is { Length: > 0 } current)
                    {
                        secrets.Add(current);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolved values of variables named like credentials (password, token, apiKey … and the workspace's extra
    /// <see cref="WorkspaceSettings.RecordingRedact"/> names) that are not typed "secret": recordings redact them too.
    /// Values set by scripts count as well.
    /// </summary>
    private IEnumerable<string> VariablesNamedLikeSecrets(PostmanCollection collection, VariableResolver resolver,
        IReadOnlyDictionary<string, string?>? requestVariables)
    {
        var extraNames = SecretNames.NormalizeAll(Workspace.Settings.RecordingRedact);
        IEnumerable<KeyValuePair<string, string?>> variables = (Environment?.Values ?? [])
            .Concat(collection.GetVariables())
            .Concat(Workspace.Globals?.Values ?? [])
            .Where(e => e.Enabled)
            .Select(e => new KeyValuePair<string, string?>(e.Key, e.Value));
        variables = variables.Concat(LocalVariables.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));
        IEnumerable<IReadOnlyDictionary<string, string?>?> setByScripts =
            [Environment is { } environment ? Session.Find(environment) : null, Session.Find(collection), Session.Globals, requestVariables];
        variables = variables.Concat(setByScripts.SelectMany(values => values ?? new Dictionary<string, string?>()));
        return variables
            .Where(v => !string.IsNullOrEmpty(v.Value) && SecretNames.IsSecret(v.Key, NameKind.Field, extraNames))
            .Select(v => resolver.Resolve(v.Value))
            .Where(v => v.Length > 0);
    }

    /// <summary>Call after editing the certificate registry so changed entries are reloaded.</summary>
    public void ReloadCertificates()
    {
        // A request still in flight may be using the old certificates, so they are released with the runner.
        _retiredProviders.Add(Certificates);
        Certificates = new ClientCertificateProvider(Workspace.Certificates);
    }

    public void Dispose()
    {
        StopScripts();
        Certificates.Dispose();
        foreach (var provider in _retiredProviders)
        {
            provider.Dispose();
        }

        _retiredProviders.Clear();
    }
}
