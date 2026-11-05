using System.Text.Json.Nodes;

namespace TermRest.Core.Model;

public enum BodyMode
{
    None,
    Raw,
    UrlEncoded,
    FormData,
    File,
    GraphQL,
}

public sealed class RequestBody
{
    public BodyMode Mode { get; set; }

    public string Raw { get; set; } = "";

    /// <summary>Postman <c>options.raw.language</c>: json, xml, html, javascript or text.</summary>
    public string RawLanguage { get; set; } = "text";

    public List<KeyValueEntry> UrlEncoded { get; set; } = [];

    public List<KeyValueEntry> FormData { get; set; } = [];

    public string? FileSrc { get; set; }

    public string GraphQLQuery { get; set; } = "";

    public string GraphQLVariables { get; set; } = "";

    /// <summary>Postman's "disabled" flag on the body object.</summary>
    public bool Disabled { get; set; }

    public JsonObject? Source { get; set; }

    public RequestBody Clone() => new()
    {
        Mode = Mode,
        Raw = Raw,
        RawLanguage = RawLanguage,
        UrlEncoded = KeyValueEntry.CloneList(UrlEncoded),
        FormData = KeyValueEntry.CloneList(FormData),
        FileSrc = FileSrc,
        GraphQLQuery = GraphQLQuery,
        GraphQLVariables = GraphQLVariables,
        Disabled = Disabled,
        Source = Source,
    };

    public bool ContentEquals(RequestBody other) =>
        Mode == other.Mode && Raw == other.Raw && RawLanguage == other.RawLanguage
        && KeyValueEntry.ListEquals(UrlEncoded, other.UrlEncoded) && KeyValueEntry.ListEquals(FormData, other.FormData)
        && FileSrc == other.FileSrc && GraphQLQuery == other.GraphQLQuery && GraphQLVariables == other.GraphQLVariables
        && Disabled == other.Disabled;

    public static string ContentTypeForLanguage(string? language) => language?.ToLowerInvariant() switch
    {
        "json" => "application/json",
        "xml" => "application/xml",
        "html" => "text/html",
        "javascript" => "application/javascript",
        _ => "text/plain",
    };
}
