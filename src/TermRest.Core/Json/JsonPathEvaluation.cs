using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TermRest.Core.Json;

/// <summary>A query: from the root ($) or from the current value of a filter (@), then its segments.</summary>
internal sealed class PathQuery(bool absolute, Segment[] segments)
{
    /// <summary>Whether it selects at most one value: one name or index per segment, and no descendants.</summary>
    public bool IsSingular { get; } = Array.TrueForAll(segments, s => s is ChildSegment { Selectors: [NameSelector or IndexSelector] });

    public List<JsonElement> Select(JsonElement current, PathEvaluation evaluation)
    {
        if (absolute && evaluation.Cached(this) is { } cached)
        {
            return cached; // the same for every value a filter tests
        }

        List<JsonElement> nodes = [absolute ? evaluation.Root : current];
        foreach (var segment in segments)
        {
            var next = new List<JsonElement>();
            foreach (var node in nodes)
            {
                segment.Apply(node, next, evaluation);
            }

            nodes = next;
            if (nodes.Count == 0)
            {
                break;
            }
        }

        if (absolute)
        {
            evaluation.Cache(this, nodes);
        }

        return nodes;
    }

    /// <summary>The value a singular query selects, if any.</summary>
    public bool TrySelectOne(JsonElement current, PathEvaluation evaluation, out JsonElement value)
    {
        if (absolute)
        {
            // Worked out once per filter, not once per value tested: $[-1] reads the whole array before its last element.
            var nodes = Select(current, evaluation);
            value = nodes.Count > 0 ? nodes[0] : default;
            return nodes.Count > 0;
        }

        value = current;
        foreach (var segment in segments)
        {
            if (!segment.Selectors[0].TrySelectOne(value, out value))
            {
                return false;
            }
        }

        return true;
    }

    public bool SelectsAny(JsonElement current, PathEvaluation evaluation) =>
        IsSingular ? TrySelectOne(current, evaluation, out _) : Select(current, evaluation).Count > 0;
}

internal abstract class Segment(Selector[] selectors)
{
    public Selector[] Selectors { get; } = selectors;

    public abstract void Apply(JsonElement node, List<JsonElement> output, PathEvaluation evaluation);
}

/// <summary>[selectors], .name or .*: what the selectors select among the children of a value.</summary>
internal sealed class ChildSegment(Selector[] selectors) : Segment(selectors)
{
    public override void Apply(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        foreach (var selector in Selectors)
        {
            selector.Select(node, output, evaluation);
        }
    }
}

/// <summary>..[selectors], ..name or ..*: the same for a value and each of its descendants, each before its children.</summary>
internal sealed class DescendantSegment(Selector[] selectors) : Segment(selectors)
{
    public override void Apply(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var selector in Selectors)
        {
            selector.Select(node, output, evaluation);
        }

        evaluation.Step();
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                Apply(item, output, evaluation);
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                Apply(property.Value, output, evaluation);
            }
        }
    }
}

internal abstract class Selector
{
    public abstract void Select(JsonElement node, List<JsonElement> output, PathEvaluation evaluation);

    /// <summary>For the selectors a singular query is made of (a name, an index): the value selected, if any.</summary>
    public virtual bool TrySelectOne(JsonElement node, out JsonElement value) => throw new NotSupportedException();
}

internal sealed class NameSelector(string name) : Selector
{
    public override void Select(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        if (TrySelectOne(node, out var value))
        {
            evaluation.Add(output, value);
        }
    }

    public override bool TrySelectOne(JsonElement node, out JsonElement value)
    {
        value = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out value);
    }
}

internal sealed class WildcardSelector : Selector
{
    public static readonly WildcardSelector Instance = new();

    public override void Select(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                evaluation.Add(output, item);
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                evaluation.Add(output, property.Value);
            }
        }
    }
}

internal sealed class IndexSelector(long index) : Selector
{
    public override void Select(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        if (TrySelectOne(node, out var value))
        {
            evaluation.Add(output, value);
        }
    }

    public override bool TrySelectOne(JsonElement node, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            var length = node.GetArrayLength();
            var i = index >= 0 ? index : length + index;
            if (i >= 0 && i < length)
            {
                value = node[(int)i];
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>[start:end:step], with the bounds of RFC 9535: negative ones count from the end, and a negative step goes backwards.</summary>
internal sealed class SliceSelector(long? start, long? end, long step) : Selector
{
    public override void Select(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        if (node.ValueKind != JsonValueKind.Array || step == 0)
        {
            return;
        }

        // The elements are enumerated, not indexed: finding an element of an array of objects by index reads all before it.
        long length = node.GetArrayLength();
        if (step > 0)
        {
            var lower = Math.Clamp(Normalize(start ?? 0, length), 0, length);
            var upper = Math.Clamp(Normalize(end ?? length, length), 0, length);
            long i = 0;
            var next = lower;
            foreach (var item in node.EnumerateArray())
            {
                if (i >= upper)
                {
                    break;
                }

                if (i == next)
                {
                    evaluation.Add(output, item);
                    next += step;
                }

                i++;
            }
        }
        else
        {
            var upper = Math.Clamp(Normalize(start ?? length - 1, length), -1, length - 1);
            var lower = Math.Clamp(Normalize(end ?? -length - 1, length), -1, length - 1);
            if (upper <= lower)
            {
                return;
            }

            var items = new JsonElement[upper + 1];
            var count = 0;
            foreach (var item in node.EnumerateArray())
            {
                if (count > upper)
                {
                    break;
                }

                items[count++] = item;
            }

            for (var i = upper; i > lower; i += step)
            {
                evaluation.Add(output, items[i]);
            }
        }
    }

    private static long Normalize(long index, long length) => index >= 0 ? index : length + index;
}

/// <summary>[?test]: the children of a value for which the test is true.</summary>
internal sealed class FilterSelector(LogicalExpression condition) : Selector
{
    public override void Select(JsonElement node, List<JsonElement> output, PathEvaluation evaluation)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                evaluation.Step();
                if (condition.Test(item, evaluation))
                {
                    evaluation.Add(output, item);
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                evaluation.Step();
                if (condition.Test(property.Value, evaluation))
                {
                    evaluation.Add(output, property.Value);
                }
            }
        }
    }
}

internal abstract class LogicalExpression
{
    public abstract bool Test(JsonElement current, PathEvaluation evaluation);
}

internal sealed class OrExpression(LogicalExpression left, LogicalExpression right) : LogicalExpression
{
    public override bool Test(JsonElement current, PathEvaluation evaluation) => left.Test(current, evaluation) || right.Test(current, evaluation);
}

internal sealed class AndExpression(LogicalExpression left, LogicalExpression right) : LogicalExpression
{
    public override bool Test(JsonElement current, PathEvaluation evaluation) => left.Test(current, evaluation) && right.Test(current, evaluation);
}

internal sealed class NotExpression(LogicalExpression inner) : LogicalExpression
{
    public override bool Test(JsonElement current, PathEvaluation evaluation) => !inner.Test(current, evaluation);
}

/// <summary>A query as a test: whether it selects anything (a value that is false or null too).</summary>
internal sealed class ExistsExpression(PathQuery query) : LogicalExpression
{
    public override bool Test(JsonElement current, PathEvaluation evaluation) => query.SelectsAny(current, evaluation);
}

internal sealed class FunctionTestExpression(FunctionCall call) : LogicalExpression
{
    public override bool Test(JsonElement current, PathEvaluation evaluation) => call.Test(current, evaluation);
}

/// <summary>The comparison operators, in the order the parser tries them.</summary>
internal enum ComparisonOperator
{
    Equal,
    NotEqual,
    LessOrEqual,
    GreaterOrEqual,
    Less,
    Greater,
}

internal sealed class ComparisonExpression(ValueExpression left, ComparisonOperator op, ValueExpression right) : LogicalExpression
{
    public override bool Test(JsonElement current, PathEvaluation evaluation)
    {
        var a = left.Evaluate(current, evaluation);
        var b = right.Evaluate(current, evaluation);
        return op switch
        {
            ComparisonOperator.Equal => PathValue.AreEqual(a, b),
            ComparisonOperator.NotEqual => !PathValue.AreEqual(a, b),
            ComparisonOperator.Less => PathValue.IsLess(a, b),
            ComparisonOperator.LessOrEqual => PathValue.IsLess(a, b) || PathValue.AreEqual(a, b),
            ComparisonOperator.Greater => PathValue.IsLess(b, a),
            _ => PathValue.IsLess(b, a) || PathValue.AreEqual(a, b),
        };
    }
}

internal abstract class ValueExpression
{
    public abstract PathValue Evaluate(JsonElement current, PathEvaluation evaluation);
}

internal sealed class LiteralExpression(PathValue value) : ValueExpression
{
    public PathValue Value { get; } = value;

    public override PathValue Evaluate(JsonElement current, PathEvaluation evaluation) => Value;
}

/// <summary>A singular query as a value: the value it selects, or nothing.</summary>
internal sealed class QueryValueExpression(PathQuery query) : ValueExpression
{
    public override PathValue Evaluate(JsonElement current, PathEvaluation evaluation) =>
        query.TrySelectOne(current, evaluation, out var value) ? PathValue.Of(value) : default;
}

internal sealed class FunctionValueExpression(FunctionCall call) : ValueExpression
{
    public override PathValue Evaluate(JsonElement current, PathEvaluation evaluation) => call.Evaluate(current, evaluation);
}

/// <summary>The types of RFC 9535 function parameters and results.</summary>
internal enum PathType
{
    Value,
    Logical,
    Nodes,
}

internal enum PathFunction
{
    Length,
    Count,
    Match,
    Search,
    Value,
}

internal sealed class FunctionCall
{
    private readonly PathFunction _function;
    private readonly object[] _arguments;
    private readonly bool _literalPattern;
    private readonly Regex? _regex;

    public FunctionCall(string name, PathFunction function, PathType result, object[] arguments)
    {
        Name = name;
        Result = result;
        _function = function;
        _arguments = arguments;
        if (function is PathFunction.Match or PathFunction.Search && arguments[1] is LiteralExpression literal)
        {
            // Written in the query: translated once, not for each value tested.
            _literalPattern = true;
            _regex = literal.Value.Kind == JsonValueKind.String ? IRegexp.Create(literal.Value.GetString(), whole: function == PathFunction.Match) : null;
        }
    }

    public string Name { get; }

    public PathType Result { get; }

    public static (PathFunction Function, PathType[] Parameters, PathType Result)? Find(string name) => name switch
    {
        "length" => (PathFunction.Length, [PathType.Value], PathType.Value),
        "count" => (PathFunction.Count, [PathType.Nodes], PathType.Value),
        "match" => (PathFunction.Match, [PathType.Value, PathType.Value], PathType.Logical),
        "search" => (PathFunction.Search, [PathType.Value, PathType.Value], PathType.Logical),
        "value" => (PathFunction.Value, [PathType.Nodes], PathType.Value),
        _ => null,
    };

    /// <summary>length, count or value.</summary>
    public PathValue Evaluate(JsonElement current, PathEvaluation evaluation)
    {
        switch (_function)
        {
            case PathFunction.Length:
                var value = ((ValueExpression)_arguments[0]).Evaluate(current, evaluation);
                return value.Kind switch
                {
                    JsonValueKind.String => PathValue.Of(CodePoints(value.GetString())),
                    JsonValueKind.Array => PathValue.Of(value.Element.GetArrayLength()),
                    JsonValueKind.Object => PathValue.Of(value.Element.GetPropertyCount()),
                    _ => default,
                };
            case PathFunction.Count:
                return PathValue.Of(((PathQuery)_arguments[0]).Select(current, evaluation).Count);
            default:
                var nodes = ((PathQuery)_arguments[0]).Select(current, evaluation);
                return nodes.Count == 1 ? PathValue.Of(nodes[0]) : default;
        }
    }

    /// <summary>match (the whole string) or search (a part of it).</summary>
    public bool Test(JsonElement current, PathEvaluation evaluation)
    {
        var subject = ((ValueExpression)_arguments[0]).Evaluate(current, evaluation);
        if (subject.Kind != JsonValueKind.String)
        {
            return false;
        }

        Regex? regex;
        if (_literalPattern)
        {
            regex = _regex;
        }
        else
        {
            var pattern = ((ValueExpression)_arguments[1]).Evaluate(current, evaluation);
            if (pattern.Kind != JsonValueKind.String)
            {
                return false;
            }

            regex = evaluation.Regex(pattern.GetString(), whole: _function == PathFunction.Match);
        }

        return regex is not null && IRegexp.IsMatch(regex, subject.GetString());
    }

    /// <summary>The length of a string in characters (code points): an emoji is one.</summary>
    private static int CodePoints(string text)
    {
        var count = text.Length;
        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
            {
                count--;
            }
        }

        return count;
    }
}

/// <summary>One application of a query: the document, what is worth keeping while it runs, and its cancellation.</summary>
internal sealed class PathEvaluation(JsonElement root, CancellationToken cancellationToken)
{
    private const int MaxCachedRegexes = 100;

    private int _steps;
    private Dictionary<PathQuery, List<JsonElement>>? _absolute;
    private Dictionary<(string Pattern, bool Whole), Regex?>? _regexes;

    public JsonElement Root { get; } = root;

    public void Step()
    {
        if ((++_steps & 1023) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public void Add(List<JsonElement> nodes, JsonElement node)
    {
        if (nodes.Count >= JsonPath.MaxNodes)
        {
            throw new JsonPathException($"The query selects more than {JsonPath.MaxNodes.ToString("N0", CultureInfo.InvariantCulture)} values in one step.");
        }

        nodes.Add(node);
        Step();
    }

    public List<JsonElement>? Cached(PathQuery query) => _absolute?.GetValueOrDefault(query);

    public void Cache(PathQuery query, List<JsonElement> nodes) => (_absolute ??= [])[query] = nodes;

    /// <summary>A pattern taken from the document: translated once per distinct pattern.</summary>
    public Regex? Regex(string pattern, bool whole)
    {
        _regexes ??= [];
        if (!_regexes.TryGetValue((pattern, whole), out var regex))
        {
            regex = IRegexp.Create(pattern, whole);
            if (_regexes.Count < MaxCachedRegexes)
            {
                _regexes[(pattern, whole)] = regex;
            }
        }

        return regex;
    }
}

/// <summary>A value in a filter: a value of the document, a literal, a number a function computed, or nothing.</summary>
internal readonly struct PathValue
{
    private readonly JsonElement _element;
    private readonly string? _text;
    private readonly long _integer;
    private readonly Source _source;

    private PathValue(Source source, JsonElement element, string? text, long integer)
    {
        _source = source;
        _element = element;
        _text = text;
        _integer = integer;
    }

    private enum Source : byte
    {
        Nothing,
        Element,
        Integer,
        Text,
    }

    /// <summary>The kind of value; <see cref="JsonValueKind.Undefined"/> for nothing (a query that selects no value).</summary>
    public JsonValueKind Kind => _source switch
    {
        Source.Element => _element.ValueKind,
        Source.Integer => JsonValueKind.Number,
        Source.Text => JsonValueKind.String,
        _ => JsonValueKind.Undefined,
    };

    public JsonElement Element => _element;

    public static PathValue Of(JsonElement element) => new(Source.Element, element, null, 0);

    public static PathValue Of(long integer) => new(Source.Integer, default, null, integer);

    public static PathValue Of(string text) => new(Source.Text, default, text, 0);

    public string GetString() => _text ?? _element.GetString()!;

    /// <summary>==: nothing equals only nothing; numbers by value (1 == 1.0); arrays and objects by their contents.</summary>
    public static bool AreEqual(in PathValue a, in PathValue b)
    {
        var kind = a.Kind;
        if (kind != b.Kind)
        {
            return false;
        }

        return kind switch
        {
            JsonValueKind.Number => CompareNumbers(a, b) == 0,
            JsonValueKind.String => a._text is not null ? (b._text is not null ? a._text == b._text : b._element.ValueEquals(a._text))
                : b._text is not null ? a._element.ValueEquals(b._text) : StringsEqual(a._element, b._element),
            JsonValueKind.Array or JsonValueKind.Object => DeepEquals(a._element, b._element),
            _ => true, // true, false, null; and nothing
        };
    }

    /// <summary>&lt;: numbers by value, strings by code point; false for anything else.</summary>
    public static bool IsLess(in PathValue a, in PathValue b)
    {
        var kind = a.Kind;
        if (kind != b.Kind)
        {
            return false;
        }

        return kind switch
        {
            JsonValueKind.Number => CompareNumbers(a, b) < 0,
            JsonValueKind.String => CompareCodePoints(a.GetString(), b.GetString()) < 0,
            _ => false,
        };
    }

    /// <summary>
    /// Compares strings by code point, as RFC 9535 requires: UTF-16 order puts U+E000 to U+FFFF after the surrogate pairs
    /// that write U+10000 and above.
    /// </summary>
    internal static int CompareCodePoints(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return Order(a[i]) - Order(b[i]);
            }
        }

        return a.Length - b.Length;

        static int Order(char c) => c < 0xD800 ? c : c >= 0xE000 ? c - 0x800 : c + 0x2000;
    }

    private static int CompareNumbers(in PathValue a, in PathValue b)
    {
        if (a._source == Source.Integer && b._source == Source.Integer)
        {
            return a._integer.CompareTo(b._integer);
        }

        Span<byte> x = stackalloc byte[20];
        Span<byte> y = stackalloc byte[20];
        return JsonNumbers.Compare(a.RawNumber(x), b.RawNumber(y));
    }

    private ReadOnlySpan<byte> RawNumber(Span<byte> buffer)
    {
        if (_source != Source.Integer)
        {
            return JsonMarshal.GetRawUtf8Value(_element);
        }

        _integer.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
        return buffer[..written];
    }

    private static bool StringsEqual(JsonElement a, JsonElement b)
    {
        // Equal strings written without escapes are the same bytes.
        var x = JsonMarshal.GetRawUtf8Value(a);
        var y = JsonMarshal.GetRawUtf8Value(b);
        return x.Contains((byte)'\\') || y.Contains((byte)'\\') ? a.GetString() == b.GetString() : x.SequenceEqual(y);
    }

    private static bool DeepEquals(JsonElement a, JsonElement b)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                return JsonNumbers.Compare(JsonMarshal.GetRawUtf8Value(a), JsonMarshal.GetRawUtf8Value(b)) == 0;
            case JsonValueKind.String:
                return StringsEqual(a, b);
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                {
                    return false;
                }

                var left = a.EnumerateArray();
                var right = b.EnumerateArray();
                while (left.MoveNext() && right.MoveNext())
                {
                    if (!DeepEquals(left.Current, right.Current))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                if (a.GetPropertyCount() != b.GetPropertyCount())
                {
                    return false;
                }

                foreach (var property in a.EnumerateObject())
                {
                    if (!b.TryGetProperty(property.Name, out var other) || !DeepEquals(property.Value, other))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }
}

/// <summary>Exact comparison of JSON numbers as written: 1, 1.0 and 1e0 are equal; 9007199254740993 is not 9007199254740992.</summary>
internal static class JsonNumbers
{
    /// <summary>Exponents beyond this (in absolute value) are taken as this: no JSON number is that far from 1 in practice.</summary>
    private const long MaxExponent = 1_000_000_000_000_000;

    public static int Compare(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var a = new Parts(x);
        var b = new Parts(y);
        if (a.Sign != b.Sign)
        {
            return a.Sign.CompareTo(b.Sign);
        }

        if (a.Sign == 0)
        {
            return 0;
        }

        var magnitude = a.Exponent != b.Exponent ? a.Exponent.CompareTo(b.Exponent) : CompareDigits(a, b);
        return a.Sign > 0 ? magnitude : -magnitude;
    }

    private static int CompareDigits(in Parts a, in Parts b)
    {
        var count = Math.Min(a.Count, b.Count);
        for (var i = 0; i < count; i++)
        {
            var difference = a.Digit(i) - b.Digit(i);
            if (difference != 0)
            {
                return difference;
            }
        }

        return a.Count.CompareTo(b.Count);
    }

    /// <summary>A number as 0.d1d2…dn × 10^Exponent, its digits without leading or trailing zeros.</summary>
    private readonly ref struct Parts
    {
        private readonly ReadOnlySpan<byte> _integer;
        private readonly ReadOnlySpan<byte> _fraction;
        private readonly int _first;

        public Parts(ReadOnlySpan<byte> text)
        {
            var negative = text.Length > 0 && text[0] == '-';
            if (negative)
            {
                text = text[1..];
            }

            var e = text.IndexOfAny((byte)'e', (byte)'E');
            var mantissa = e < 0 ? text : text[..e];
            var dot = mantissa.IndexOf((byte)'.');
            _integer = dot < 0 ? mantissa : mantissa[..dot];
            _fraction = dot < 0 ? default : mantissa[(dot + 1)..];

            var total = _integer.Length + _fraction.Length;
            var first = 0;
            while (first < total && At(_integer, _fraction, first) == '0')
            {
                first++;
            }

            _first = first;
            if (first == total)
            {
                return; // zero, either sign
            }

            var last = total - 1;
            while (At(_integer, _fraction, last) == '0')
            {
                last--;
            }

            Count = last - first + 1;
            Sign = negative ? -1 : 1;
            Exponent = _integer.Length - first + (e < 0 ? 0 : ParseExponent(text[(e + 1)..]));
        }

        public int Sign { get; }

        public int Count { get; }

        public long Exponent { get; }

        public int Digit(int i) => At(_integer, _fraction, _first + i);

        private static int At(ReadOnlySpan<byte> integer, ReadOnlySpan<byte> fraction, int i) => i < integer.Length ? integer[i] : fraction[i - integer.Length];

        private static long ParseExponent(ReadOnlySpan<byte> text)
        {
            var negative = text[0] == '-';
            if (text[0] is (byte)'-' or (byte)'+')
            {
                text = text[1..];
            }

            long value = 0;
            foreach (var digit in text)
            {
                value = Math.Min(value * 10 + (digit - '0'), MaxExponent);
            }

            return negative ? -value : value;
        }
    }
}
