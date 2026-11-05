using TermRest.Core.Certificates;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Variables;

namespace TermRest.Core.Workspace;

/// <summary>
/// Ties a workspace to the HTTP engine: builds variable scopes (local overrides &gt; environment &gt; collection &gt; globals),
/// resolves inherited auth, selects client certificates and keeps a cookie jar for the session. Used by both the TUI and CLI.
/// </summary>
public sealed class RequestRunner : IDisposable
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

    public VariableResolver CreateResolver(PostmanCollection? collection)
    {
        var scopes = new List<IVariableScope>();
        if (LocalVariables.Count > 0)
        {
            scopes.Add(new DictionaryScope("local", LocalVariables));
        }

        if (Environment is { } environment)
        {
            scopes.Add(new EntryListScope($"environment '{environment.Name}'", environment.Values));
        }

        if (collection is not null)
        {
            scopes.Add(new EntryListScope("collection", collection.GetVariables()));
        }

        if (Workspace.Globals is { } globals)
        {
            scopes.Add(new EntryListScope("globals", globals.Values));
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

    public PreparedRequest Prepare(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec)
    {
        var preparer = new RequestPreparer(CreateResolver(collection), Certificates, CreateDefaults());
        var auth = node is not null ? collection.GetEffectiveAuth(node, spec) : spec.Auth ?? collection.RootNode.Auth;
        var prepared = preparer.Prepare(spec, auth, collection.BaseDirectory);
        var saved = node is not null && node.Collection == collection && collection.FilePath is not null && node.Parent is not null;

        // History keeps a self-contained copy: inherited auth is stored with the request so it can be re-sent on its own.
        var snapshot = spec.Clone();
        snapshot.Auth ??= auth?.Clone();
        prepared.Source = new RequestSource(
            snapshot,
            collection.FilePath is null ? null : collection.Name,
            saved ? node!.PathInCollection : null,
            saved ? node!.Name : null);
        prepared.SecretValues.UnionWith(SecretVariableValues(collection));
        return prepared;
    }

    /// <summary>Sends a prepared request and records it in <see cref="History"/> (when on).</summary>
    public async Task<HttpExchange> SendAsync(PreparedRequest prepared, CancellationToken cancellationToken = default)
    {
        // No ConfigureAwait(false): UI callers resume on their own thread, which also records the entry.
        var exchange = await Executor.SendAsync(prepared, cancellationToken).ConfigureAwait(true);
        Record(exchange);
        return exchange;
    }

    public Task<HttpExchange> SendAsync(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec, CancellationToken cancellationToken = default) =>
        SendAsync(Prepare(collection, node, spec), cancellationToken);

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

    /// <summary>Values of variables typed "secret" in the environment, collection and globals.</summary>
    private IEnumerable<string> SecretVariableValues(PostmanCollection collection)
    {
        IEnumerable<KeyValueEntry> entries = Environment?.Values ?? [];
        entries = entries.Concat(collection.GetVariables());
        if (Workspace.Globals is { } globals)
        {
            entries = entries.Concat(globals.Values);
        }

        return entries.Where(e => e.Enabled && e.IsSecret && !string.IsNullOrEmpty(e.Value)).Select(e => e.Value!);
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
        Certificates.Dispose();
        foreach (var provider in _retiredProviders)
        {
            provider.Dispose();
        }

        _retiredProviders.Clear();
    }
}
