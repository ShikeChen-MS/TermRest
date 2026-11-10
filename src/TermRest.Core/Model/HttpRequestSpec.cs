namespace TermRest.Core.Model;

/// <summary>
/// Format-neutral, editable view of one HTTP request. Mapped to and from Postman JSON by
/// <see cref="Postman.PostmanRequestMapper"/>, which only rewrites the sections that actually changed.
/// </summary>
public sealed class HttpRequestSpec
{
    public static readonly string[] StandardMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public string Method { get; set; } = "GET";

    /// <summary>The URL as typed (Postman <c>url.raw</c>), including the query string.</summary>
    public string Url { get; set; } = "";

    /// <summary>All query parameters, including disabled ones that do not appear in <see cref="Url"/>.</summary>
    public List<KeyValueEntry> QueryParams { get; set; } = [];

    /// <summary>Values for <c>:name</c> path segments.</summary>
    public List<KeyValueEntry> PathVariables { get; set; } = [];

    public List<KeyValueEntry> Headers { get; set; } = [];

    public RequestBody Body { get; set; } = new();

    /// <summary>Null means "inherit auth from parent".</summary>
    public AuthSpec? Auth { get; set; }

    public CertificateSpec? Certificate { get; set; }

    public RequestSettings Settings { get; set; } = new();

    public string? Description { get; set; }

    /// <summary>The request's scripts, in each language (see <see cref="Scripting.ScriptSet"/>).</summary>
    public Scripting.ScriptSet Scripts { get; set; } = Scripting.ScriptSet.Empty;

    /// <summary>How the request runs its scripts: their language, and whether pre-request and post-response scripts run.</summary>
    public Scripting.ScriptSettings ScriptSettings { get; set; } = Scripting.ScriptSettings.Auto;

    /// <summary>True when the source used the short form <c>"url": "..."</c>.</summary>
    internal bool UrlWasString { get; set; }

    public HttpRequestSpec Clone() => new()
    {
        Method = Method,
        Url = Url,
        QueryParams = KeyValueEntry.CloneList(QueryParams),
        PathVariables = KeyValueEntry.CloneList(PathVariables),
        Headers = KeyValueEntry.CloneList(Headers),
        Body = Body.Clone(),
        Auth = Auth?.Clone(),
        Certificate = Certificate?.Clone(),
        Settings = Settings.Clone(),
        Description = Description,
        Scripts = Scripts,
        ScriptSettings = ScriptSettings,
        UrlWasString = UrlWasString,
    };

    public bool ContentEquals(HttpRequestSpec other) =>
        Method == other.Method && UrlEquals(other) && KeyValueEntry.ListEquals(Headers, other.Headers)
        && Body.ContentEquals(other.Body) && AuthSpec.AreEqual(Auth, other.Auth)
        && CertificateSpec.AreEqual(Certificate, other.Certificate) && Settings.ContentEquals(other.Settings)
        && Description == other.Description && Scripts.Equals(other.Scripts) && ScriptSettings == other.ScriptSettings;

    internal bool UrlEquals(HttpRequestSpec other) =>
        Url == other.Url && KeyValueEntry.ListEquals(QueryParams, other.QueryParams)
        && KeyValueEntry.ListEquals(PathVariables, other.PathVariables);
}
