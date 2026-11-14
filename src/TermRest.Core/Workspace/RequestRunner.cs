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

    /// <summary>
    /// The proxy URL of the workspace's settings that requests may use: the one the user allowed for this workspace folder
    /// (read by <see cref="ConfigureProxy"/>; saved in Tools › Settings of the full-screen UI). The settings come with the
    /// repository a workspace is cloned from, and a proxy sees every request: one not allowed is not used (see <see cref="BlockedProxy"/>).
    /// </summary>
    public string? AllowedProxy { get; set; }

    /// <summary>
    /// The proxy URL of the workspace's settings while it is not the <see cref="AllowedProxy"/>: requests use the system's
    /// proxy settings then (the OS's, <c>HTTPS_PROXY</c>), as without a proxy setting. Null when the settings name no proxy
    /// URL ("system", "none") or the one allowed.
    /// </summary>
    public string? BlockedProxy =>
        Workspace.Settings.Proxy is var proxy && WorkspaceSettings.IsProxyUrl(proxy) && !string.Equals(proxy.Trim(), AllowedProxy?.Trim(), StringComparison.Ordinal)
            ? proxy
            : null;

    /// <summary>
    /// Whether requests may send body and form-data files from outside the workspace folder, and open client certificates and
    /// a CA bundle on network shares outside it: allowed for this workspace folder in the user's settings (read by
    /// <see cref="ConfigureFiles"/>; saved in Tools › Settings of the full-screen UI), or by <c>--allow-outside-files</c>.
    /// Otherwise see <see cref="FilesFolder"/>.
    /// </summary>
    public bool OutsideFilesAllowed { get; set; }

    /// <summary>
    /// The workspace folder while <see cref="OutsideFilesAllowed"/> is not set: requests send only its files, and open no
    /// file on a network share outside it (<see cref="ExecutionDefaults.FilesFolder"/>); null when they may.
    /// </summary>
    public string? FilesFolder => OutsideFilesAllowed ? null : Workspace.RootPath;

    /// <summary>Where sent requests are recorded; null turns recording off (see <see cref="ConfigureHistory"/>).</summary>
    public HistoryStore? History { get; set; }

    /// <summary>While set, every sent request is also recorded into a HAR file (see <see cref="RequestRecorder"/>).</summary>
    public RequestRecorder? Recorder { get; set; }

    /// <summary>Largest response body kept with a history entry.</summary>
    public int HistoryMaxBodyBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// The environment requests use: <see cref="EnvironmentOverride"/>, else the workspace's active one. A request reads it
    /// once, when it starts, and keeps it until it is recorded, so switching while it is on its way changes later requests only.
    /// </summary>
    public PostmanEnvironment? Environment => EnvironmentOverride ?? Workspace.ActiveEnvironment;

    /// <summary>
    /// Turns history recording on or off according to the workspace settings; off while they cannot be read
    /// (<see cref="ApiWorkspace.SettingsError"/>), since they may turn it off.
    /// </summary>
    /// <param name="stateDirectory">Overrides the per-user state folder (tests, portable setups).</param>
    public void ConfigureHistory(string? stateDirectory = null)
    {
        var settings = Workspace.Settings;
        History = settings.HistoryEnabled && Workspace.SettingsError is null ? HistoryStore.ForWorkspace(Workspace.RootPath, settings.HistoryLimit, stateDirectory) : null;
        HistoryMaxBodyBytes = (int)Math.Min(int.MaxValue, Math.Max(0L, settings.HistoryResponseKb) * 1024);
    }

    /// <summary>
    /// Sets <see cref="AllowedProxy"/> to the proxy URL of the workspace's settings when the user's settings allow it for
    /// this workspace folder (<see cref="UserSettings.IsProxyAllowed"/>), else to none.
    /// </summary>
    /// <param name="userSettingsDirectory">Overrides the user's configuration folder (tests, portable setups).</param>
    public void ConfigureProxy(string? userSettingsDirectory = null)
    {
        var proxy = Workspace.Settings.Proxy;
        AllowedProxy = WorkspaceSettings.IsProxyUrl(proxy) && UserSettings.Load(userSettingsDirectory).IsProxyAllowed(Workspace.RootPath, proxy) ? proxy : null;
    }

    /// <summary>Sets <see cref="OutsideFilesAllowed"/> as the user's settings say for this workspace folder.</summary>
    /// <param name="userSettingsDirectory">Overrides the user's configuration folder (tests, portable setups).</param>
    public void ConfigureFiles(string? userSettingsDirectory = null) =>
        OutsideFilesAllowed = UserSettings.Load(userSettingsDirectory).AreOutsideFilesAllowed(Workspace.RootPath);

    /// <summary>The variables of <paramref name="collection"/> as a request sent now sees them (with the current <see cref="Environment"/>).</summary>
    /// <param name="requestVariables">Variables scripts set for this request only (<c>tr.variables</c>); null unsets.</param>
    public VariableResolver CreateResolver(PostmanCollection? collection, IReadOnlyDictionary<string, string?>? requestVariables = null) =>
        CreateResolver(Environment, collection, requestVariables);

    /// <param name="environment">The request's environment (see <see cref="Environment"/>).</param>
    private VariableResolver CreateResolver(PostmanEnvironment? environment, PostmanCollection? collection,
        IReadOnlyDictionary<string, string?>? requestVariables)
    {
        var scopes = new List<IVariableScope>();
        if (LocalVariables.Count > 0 || requestVariables is { Count: > 0 })
        {
            scopes.Add(new OverlayScope(new DictionaryScope("local", LocalVariables), requestVariables));
        }

        if (environment is not null)
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
            // One not allowed: the system's proxy settings, as without one. Trimmed, as requests use it: recordings then find
            // the password of a URL written with a space before it.
            Proxy = BlockedProxy is null ? settings.Proxy?.Trim() : null,
            MaxResponseBytes = Math.Max(1, settings.MaxResponseSizeMb) * 1024L * 1024L,
            HttpVersion = settings.HttpVersion,
            FilesFolder = FilesFolder,
        };
    }

    public PreparedRequest Prepare(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec) =>
        Prepare(Environment, collection, node, spec, spec, null);

    /// <param name="environment">The request's environment (see <see cref="Environment"/>).</param>
    /// <param name="spec">The request to send (as changed by pre-request scripts).</param>
    /// <param name="source">The request as written: kept in the history, so re-sending it runs its scripts again.</param>
    /// <param name="requestVariables">Variables set by scripts for this request only.</param>
    private PreparedRequest Prepare(PostmanEnvironment? environment, PostmanCollection collection, RequestNode? node, HttpRequestSpec spec,
        HttpRequestSpec source, IReadOnlyDictionary<string, string?>? requestVariables)
    {
        var preparer = new RequestPreparer(CreateResolver(environment, collection, requestVariables), Certificates, CreateDefaults());
        var auth = node is not null ? collection.GetEffectiveAuth(node, spec) : spec.Auth ?? collection.RootNode.Auth;
        var prepared = preparer.Prepare(spec, auth, collection.BaseDirectory);
        var saved = node is not null && node.Collection == collection && collection.FilePath is not null && node.Parent is not null;

        // History keeps a self-contained copy: inherited auth is stored with the request so it can be re-sent on its own (of
        // credential sets, the one its URL selected).
        var snapshot = source.Clone();
        var used = prepared.AuthUsed;
        var authInherited = snapshot.Auth is null && used is not null;
        snapshot.Auth ??= used?.Clone();
        prepared.Source = new RequestSource(
            snapshot,
            collection.FilePath is null ? null : collection.Name,
            saved ? node!.PathInCollection : null,
            saved ? node!.Name : null,
            authInherited,
            Workspace.FileOf(collection));
        prepared.AddSecretValues(SecretVariableValues(environment, collection, preparer.Variables));
        prepared.AddSensitiveValues(VariablesNamedLikeSecrets(environment, collection, preparer.Variables, requestVariables));
        return prepared;
    }

    /// <summary>Sends a prepared request and records it in <see cref="History"/> and <see cref="Recorder"/> (when on).</summary>
    public Task<HttpExchange> SendAsync(PreparedRequest prepared, CancellationToken cancellationToken = default) =>
        SendAsync(Environment, prepared, cancellationToken);

    /// <param name="environment">The request's environment (see <see cref="Environment"/>): the history and recordings name it.</param>
    private async Task<HttpExchange> SendAsync(PostmanEnvironment? environment, PreparedRequest prepared, CancellationToken cancellationToken)
    {
        // No ConfigureAwait(false): UI callers resume on their own thread, which also records the entry.
        var exchange = await Executor.SendAsync(prepared, cancellationToken).ConfigureAwait(true);
        Record(exchange, environment?.Name);
        Recorder?.Record(exchange, environment?.Name);
        return exchange;
    }

    public Task<HttpExchange> SendAsync(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec, CancellationToken cancellationToken = default) =>
        SendAsync(collection, node, spec, null, cancellationToken);

    private void Record(HttpExchange exchange, string? environment)
    {
        var request = exchange.Request;
        if (History is not { } history || request.Source is not { } source || exchange.Cancelled
            || (exchange.Response is null && (request.Errors.Count > 0 || request.Uri is null)))
        {
            return; // nothing was sent
        }

        try
        {
            var entry = HistoryEntry.Create(exchange, source, environment, HistoryMaxBodyBytes, out var body);
            history.Add(entry, body);
            exchange.HistoryEntry = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            request.Warnings.Add($"The request was not added to the history: {ex.Message}");
        }
    }

    /// <summary>
    /// Values of variables typed "secret" in the environment, collection and globals: as written, as scripts set them, and
    /// as <paramref name="resolver"/> resolves them, which is the value sent (a CLI <c>--var</c> or a value a script set
    /// for the request may take precedence, and the value may reference other variables).
    /// </summary>
    private List<string> SecretVariableValues(PostmanEnvironment? environment, PostmanCollection collection, VariableResolver resolver)
    {
        var secrets = new List<string>();
        Add(environment?.Values, environment is not null ? Session.Find(environment) : null);
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

                    var reference = "{{" + entry.Key + "}}";
                    var used = resolver.Resolve(reference);
                    if (used != reference)
                    {
                        secrets.Add(used);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolved values of variables named like credentials (password, token, apiKey … and the workspace's extra
    /// <see cref="WorkspaceSettings.RecordingRedact"/> names, with those of the <see cref="Recorder"/>, which keeps the
    /// names removed from the settings since it started) that are not typed "secret": recordings redact them too.
    /// Values set by scripts count as well.
    /// </summary>
    private IEnumerable<string> VariablesNamedLikeSecrets(PostmanEnvironment? environment, PostmanCollection collection, VariableResolver resolver,
        IReadOnlyDictionary<string, string?>? requestVariables)
    {
        var extraNames = SecretNames.NormalizeAll(Workspace.Settings.RecordingRedact);
        if (Recorder is { } recorder)
        {
            extraNames.UnionWith(recorder.ExtraNames);
        }

        IEnumerable<KeyValuePair<string, string?>> variables = (environment?.Values ?? [])
            .Concat(collection.GetVariables())
            .Concat(Workspace.Globals?.Values ?? [])
            .Where(e => e.Enabled)
            .Select(e => new KeyValuePair<string, string?>(e.Key, e.Value));
        variables = variables.Concat(LocalVariables.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));
        IEnumerable<IReadOnlyDictionary<string, string?>?> setByScripts =
            [environment is not null ? Session.Find(environment) : null, Session.Find(collection), Session.Globals, requestVariables];
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
