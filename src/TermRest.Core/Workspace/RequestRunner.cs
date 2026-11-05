using TermRest.Core.Certificates;
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

    public PostmanEnvironment? Environment => EnvironmentOverride ?? Workspace.ActiveEnvironment;

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
        return preparer.Prepare(spec, auth, collection.BaseDirectory);
    }

    public Task<HttpExchange> SendAsync(PreparedRequest prepared, CancellationToken cancellationToken = default) =>
        Executor.SendAsync(prepared, cancellationToken);

    public Task<HttpExchange> SendAsync(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec, CancellationToken cancellationToken = default) =>
        SendAsync(Prepare(collection, node, spec), cancellationToken);

    /// <summary>Call after editing the certificate registry so changed entries are reloaded.</summary>
    public void ReloadCertificates()
    {
        Certificates.Dispose();
        Certificates = new ClientCertificateProvider(Workspace.Certificates);
    }

    public void Dispose() => Certificates.Dispose();
}
