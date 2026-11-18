namespace TermRest.Core.Model;

/// <summary>
/// Format-neutral, editable view of a request's saved example (Postman's saved response): the request it was saved with,
/// and the response's status, headers and body. Mapped to and from Postman JSON by <see cref="Postman.ExampleMapper"/>,
/// which only rewrites what changed.
/// </summary>
public sealed class ExampleSpec
{
    /// <summary>The languages an example's body is shown in (Postman's <c>_postman_previewlanguage</c>).</summary>
    public static readonly string[] Languages = ["json", "xml", "html", "javascript", "text"];

    public string Name { get; set; } = "";

    /// <summary>The request the example was saved with (Postman's <c>originalRequest</c>).</summary>
    public HttpRequestSpec Request { get; set; } = new();

    /// <summary>The status code; null when the example has none.</summary>
    public int? Code { get; set; } = 200;

    /// <summary>The reason phrase (Postman's <c>status</c>), such as OK.</summary>
    public string Status { get; set; } = "OK";

    public List<KeyValueEntry> Headers { get; set; } = [];

    public string Body { get; set; } = "";

    /// <summary>What the body is written in: one of <see cref="Languages"/>, or what the example says; null when it says nothing.</summary>
    public string? Language { get; set; }

    public ExampleSpec Clone() => new()
    {
        Name = Name,
        Request = Request.Clone(),
        Code = Code,
        Status = Status,
        Headers = KeyValueEntry.CloneList(Headers),
        Body = Body,
        Language = Language,
    };

    public bool ContentEquals(ExampleSpec other) =>
        Name == other.Name && Request.ContentEquals(other.Request) && Code == other.Code && Status == other.Status
        && KeyValueEntry.ListEquals(Headers, other.Headers) && Body == other.Body && Language == other.Language;

    /// <summary>The response part (status, headers, body and its language) equals <paramref name="other"/>'s.</summary>
    public bool ResponseEquals(ExampleSpec other) =>
        Code == other.Code && Status == other.Status && KeyValueEntry.ListEquals(Headers, other.Headers) && Body == other.Body && Language == other.Language;
}
