using System.Text.Json;

namespace TermRest.Core.Json;

/// <summary>
/// A JSONPath query (RFC 9535), such as <c>$.store.book[?@.price &lt; 10].title</c>: selects values of a JSON document.
/// Its functions are the RFC's: length, count, value, and match and search, which take an I-Regexp (RFC 9485).
/// </summary>
public sealed class JsonPath
{
    /// <summary>The most values one step of a query may select: <c>$..*..*</c> on a large document fails rather than fill the memory.</summary>
    internal const int MaxNodes = 1 << 22;

    private readonly PathQuery _query;

    private JsonPath(string text, PathQuery query)
    {
        Text = text;
        _query = query;
    }

    /// <summary>The query as written.</summary>
    public string Text { get; }

    /// <summary>Parses a query; a <see cref="JsonPathException"/> says where it is not valid JSONPath.</summary>
    public static JsonPath Parse(string text) => new(text, JsonPathParser.Parse(text));

    /// <summary>
    /// The values the query selects in <paramref name="root"/>, in document order. A <see cref="JsonPathException"/> when a
    /// step selects too many values, or a regular expression takes too long.
    /// </summary>
    public IReadOnlyList<JsonElement> Select(JsonElement root, CancellationToken cancellationToken = default) =>
        _query.Select(root, new PathEvaluation(root, cancellationToken));

    public override string ToString() => Text;
}

/// <summary>A query that is not valid JSONPath (<see cref="Position"/> says where), or that could not be applied.</summary>
public sealed class JsonPathException(string message, int position = -1) : FormatException(message)
{
    /// <summary>Where in the query the problem is (from 0), or -1.</summary>
    public int Position { get; } = position;
}
