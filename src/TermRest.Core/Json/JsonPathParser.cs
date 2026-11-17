using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TermRest.Core.Json;

/// <summary>Parses JSONPath (RFC 9535), checking that function calls are well-typed as the RFC requires.</summary>
internal sealed class JsonPathParser
{
    /// <summary>How deep filters, parentheses and function calls may nest: deeper ones would only run out of stack.</summary>
    private const int MaxNesting = 64;

    /// <summary>The largest index, 2^53 - 1: I-JSON numbers are exact up to it.</summary>
    private const long MaxIndex = 9_007_199_254_740_991;

    private static readonly string[] ComparisonOperators = ["==", "!=", "<=", ">=", "<", ">"];

    private readonly string _text;
    private int _pos;
    private int _nesting;

    private JsonPathParser(string text) => _text = text;

    private enum OperandKind
    {
        Literal,
        Query,
        Function,
        Logical,
    }

    public static PathQuery Parse(string text)
    {
        var parser = new JsonPathParser(text);
        if (!parser.Peek('$'))
        {
            throw parser.Error(text.Length == 0 ? "The query is empty: a query starts with $" : "A query starts with $ (the whole document)");
        }

        parser._pos++;
        var query = parser.Segments(absolute: true);
        if (parser._pos < text.Length)
        {
            throw parser.Unexpected("a segment such as .name, [0] or ..name");
        }

        return query;
    }

    // ----- segments and selectors ---------------------------------------------------------

    private PathQuery Segments(bool absolute)
    {
        var segments = new List<Segment>();
        while (true)
        {
            var start = _pos;
            SkipBlanks();
            if (Peek('.') && Peek('.', 1))
            {
                _pos += 2;
                segments.Add(new DescendantSegment(Peek('[') ? Bracketed() : [DotSelector(descendant: true)]));
            }
            else if (Take('.'))
            {
                segments.Add(new ChildSegment([DotSelector(descendant: false)]));
            }
            else if (Peek('['))
            {
                segments.Add(new ChildSegment(Bracketed()));
            }
            else
            {
                _pos = start; // blanks after a query belong to what follows it
                return new PathQuery(absolute, [.. segments]);
            }
        }
    }

    private Selector DotSelector(bool descendant)
    {
        if (Take('*'))
        {
            return WildcardSelector.Instance;
        }

        var start = _pos;
        if (!TakeNameChar(first: true))
        {
            throw Unexpected(descendant ? "a name, * or [ after .." : "a name or * after .");
        }

        while (TakeNameChar(first: false))
        {
        }

        return new NameSelector(_text[start.._pos]);
    }

    /// <summary>A character of a name after a dot: a letter, _, a digit (not first) or any non-ASCII character.</summary>
    private bool TakeNameChar(bool first)
    {
        if (_pos >= _text.Length)
        {
            return false;
        }

        var c = _text[_pos];
        if (char.IsAsciiLetter(c) || c == '_' || (!first && char.IsAsciiDigit(c)) || (c >= 0x80 && !char.IsSurrogate(c)))
        {
            _pos++;
            return true;
        }

        if (char.IsHighSurrogate(c) && _pos + 1 < _text.Length && char.IsLowSurrogate(_text[_pos + 1]))
        {
            _pos += 2;
            return true;
        }

        return false;
    }

    private Selector[] Bracketed()
    {
        _pos++;
        var selectors = new List<Selector>();
        while (true)
        {
            SkipBlanks();
            selectors.Add(Selector());
            SkipBlanks();
            if (Take(']'))
            {
                return [.. selectors];
            }

            if (!Take(','))
            {
                throw Unexpected("',' or ']'");
            }
        }
    }

    private Selector Selector()
    {
        if (Take('*'))
        {
            return WildcardSelector.Instance;
        }

        if (Peek('\'') || Peek('"'))
        {
            return new NameSelector(StringLiteral());
        }

        if (Take('?'))
        {
            Enter();
            SkipBlanks();
            var condition = Test(Or());
            Leave();
            return new FilterSelector(condition);
        }

        var start = Integer();
        if (start is not null)
        {
            var end = _pos;
            SkipBlanks();
            if (!Peek(':'))
            {
                _pos = end;
                return new IndexSelector(start.Value);
            }
        }
        else if (!Peek(':'))
        {
            throw Unexpected("a selector: 'name', *, an index, a slice (start:end:step) or a filter (?…)");
        }

        _pos++;
        SkipBlanks();
        var stop = Integer();
        SkipBlanks();
        long step = 1;
        if (Take(':'))
        {
            var beforeStep = _pos;
            SkipBlanks();
            if (Integer() is { } value)
            {
                step = value;
            }
            else
            {
                _pos = beforeStep;
            }
        }

        return new SliceSelector(start, stop, step);
    }

    /// <summary>An index or a slice bound: 0, or digits that do not start with 0, perhaps after a -. Null when none starts here.</summary>
    private long? Integer()
    {
        var start = _pos;
        var negative = Take('-');
        var digits = _pos;
        while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
        {
            _pos++;
        }

        if (_pos == digits)
        {
            if (negative)
            {
                throw Error("Expected a digit after -", digits);
            }

            return null;
        }

        var span = _text.AsSpan(digits, _pos - digits);
        if (span[0] == '0' && (span.Length > 1 || negative))
        {
            throw Error("An index does not start with 0, and is not -0", start);
        }

        var value = span.Length > 16 ? long.MaxValue : long.Parse(span, NumberStyles.None, CultureInfo.InvariantCulture);
        if (value > MaxIndex)
        {
            throw Error("The index is out of range: at most 9007199254740991, either way", start);
        }

        return negative ? -value : value;
    }

    private string StringLiteral()
    {
        var quote = _text[_pos];
        var start = _pos++;
        var builder = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length)
            {
                throw Error("The string has no closing quote", start);
            }

            var c = _text[_pos];
            if (c == quote)
            {
                _pos++;
                return builder.ToString();
            }

            if (c == '\\')
            {
                Escape(builder, quote, start);
            }
            else if (c < 0x20)
            {
                throw Error($"A string cannot hold {Describe(_pos)}: write it as an escape (\\n, \\t, \\u0001 …)");
            }
            else if (char.IsHighSurrogate(c) && _pos + 1 < _text.Length && char.IsLowSurrogate(_text[_pos + 1]))
            {
                builder.Append(c).Append(_text[_pos + 1]);
                _pos += 2;
            }
            else if (char.IsSurrogate(c))
            {
                throw Error("A string cannot hold half of a character (a lone surrogate)");
            }
            else
            {
                builder.Append(c);
                _pos++;
            }
        }
    }

    private void Escape(StringBuilder builder, char quote, int stringStart)
    {
        var at = _pos++;
        if (_pos >= _text.Length)
        {
            throw Error("The string has no closing quote", stringStart);
        }

        var c = _text[_pos++];
        switch (c)
        {
            case 'b': builder.Append('\b'); break;
            case 'f': builder.Append('\f'); break;
            case 'n': builder.Append('\n'); break;
            case 'r': builder.Append('\r'); break;
            case 't': builder.Append('\t'); break;
            case '/': builder.Append('/'); break;
            case '\\': builder.Append('\\'); break;
            case 'u':
                var unit = Hex4(at);
                if (char.IsHighSurrogate(unit))
                {
                    if (!(Peek('\\') && Peek('u', 1)))
                    {
                        throw Error("\\u" + ((int)unit).ToString("X4", CultureInfo.InvariantCulture) + " is half of a character: the other half (\\uDC00 to \\uDFFF) must follow it", at);
                    }

                    var lowAt = _pos;
                    _pos += 2;
                    var low = Hex4(lowAt);
                    if (!char.IsLowSurrogate(low))
                    {
                        throw Error("\\u" + ((int)unit).ToString("X4", CultureInfo.InvariantCulture) + " is half of a character: the other half (\\uDC00 to \\uDFFF) must follow it", at);
                    }

                    builder.Append(unit).Append(low);
                }
                else if (char.IsLowSurrogate(unit))
                {
                    throw Error("\\u" + ((int)unit).ToString("X4", CultureInfo.InvariantCulture) + " is the second half of a character, without the first", at);
                }
                else
                {
                    builder.Append(unit);
                }

                break;
            default:
                if (c != quote)
                {
                    throw Error($"\\{c} is not an escape: write \\{quote}, \\\\, \\/, \\b, \\f, \\n, \\r, \\t or \\uXXXX", at);
                }

                builder.Append(c);
                break;
        }
    }

    /// <summary>The four hexadecimal digits of a \u escape (the position is after the u).</summary>
    private char Hex4(int escapeStart)
    {
        if (_pos + 4 > _text.Length || !int.TryParse(_text.AsSpan(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            throw Error("\\u takes four hexadecimal digits", escapeStart);
        }

        _pos += 4;
        return (char)value;
    }

    // ----- filter expressions -------------------------------------------------------------

    private Operand Or()
    {
        var left = And();
        while (TakeOperator("||"))
        {
            var right = And();
            left = new Operand(OperandKind.Logical, new OrExpression(Test(left), Test(right)), left.Position);
        }

        return left;
    }

    private Operand And()
    {
        var left = Basic();
        while (TakeOperator("&&"))
        {
            var right = Basic();
            left = new Operand(OperandKind.Logical, new AndExpression(Test(left), Test(right)), left.Position);
        }

        return left;
    }

    private Operand Basic()
    {
        var start = _pos;
        if (Take('!'))
        {
            SkipBlanks();
            var negated = Peek('(') ? Parenthesized() : Comparable();
            return new Operand(OperandKind.Logical, new NotExpression(Test(negated)), start);
        }

        if (Peek('('))
        {
            return Parenthesized();
        }

        var left = Comparable();
        var end = _pos;
        SkipBlanks();
        if (ComparisonOperator() is { } op)
        {
            SkipBlanks();
            var right = Comparable();
            return new Operand(OperandKind.Logical, new ComparisonExpression(Value(left), op, Value(right)), start);
        }

        if (Peek('=') && !Peek('=', 1))
        {
            throw Error("Compare with == (a single = is not an operator)");
        }

        _pos = end;
        return left;
    }

    private Operand Parenthesized()
    {
        var start = _pos++;
        Enter();
        SkipBlanks();
        var inner = Or();
        SkipBlanks();
        if (!Take(')'))
        {
            throw Unexpected("')'");
        }

        Leave();
        return new Operand(OperandKind.Logical, Test(inner), start);
    }

    private ComparisonOperator? ComparisonOperator()
    {
        for (var i = 0; i < ComparisonOperators.Length; i++)
        {
            if (_text.AsSpan(_pos).StartsWith(ComparisonOperators[i], StringComparison.Ordinal))
            {
                _pos += ComparisonOperators[i].Length;
                return (ComparisonOperator)i;
            }
        }

        return null;
    }

    /// <summary>A literal, a query or a function call.</summary>
    private Operand Comparable()
    {
        var start = _pos;
        var c = _pos < _text.Length ? _text[_pos] : '\0';
        switch (c)
        {
            case '@' or '$':
                _pos++;
                return new Operand(OperandKind.Query, Segments(absolute: c == '$'), start);
            case '\'' or '"':
                return new Operand(OperandKind.Literal, new LiteralExpression(PathValue.Of(StringLiteral())), start);
            case '-' or (>= '0' and <= '9'):
                return new Operand(OperandKind.Literal, NumberLiteral(), start);
            case >= 'a' and <= 'z':
                while (_pos < _text.Length && (char.IsAsciiLetterLower(_text[_pos]) || char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '_'))
                {
                    _pos++;
                }

                var name = _text[start.._pos];
                if (Peek('('))
                {
                    return new Operand(OperandKind.Function, Function(name, start), start);
                }

                return name is "true" or "false" or "null"
                    ? new Operand(OperandKind.Literal, new LiteralExpression(PathValue.Of(JsonElement.Parse(name))), start)
                    : throw Error($"Unknown name {name}: call a function as {name}(…), and quote a string", start);
            default:
                throw Unexpected("a value, a query (@… or $…) or a function");
        }
    }

    /// <summary>A number as JSON writes it: -0, 1, 1.5, 1e-3; not 01, 1. or .5.</summary>
    private LiteralExpression NumberLiteral()
    {
        var start = _pos;
        Take('-');
        if (!Take('0'))
        {
            if (!(_pos < _text.Length && _text[_pos] is >= '1' and <= '9'))
            {
                throw Error("Expected a digit");
            }

            SkipDigits();
        }

        if (Take('.'))
        {
            RequireDigits("Expected a digit after the decimal point");
        }

        if (Take('e') || Take('E'))
        {
            _ = Take('+') || Take('-');
            RequireDigits("Expected a digit in the exponent");
        }

        return new LiteralExpression(PathValue.Of(JsonElement.Parse(_text[start.._pos])));
    }

    private FunctionCall Function(string name, int start)
    {
        var signature = FunctionCall.Find(name)
            ?? throw Error($"Unknown function {name}(): the functions are length, count, match, search and value", start);
        _pos++;
        Enter();
        SkipBlanks();
        var operands = new List<Operand>();
        if (!Peek(')'))
        {
            while (true)
            {
                operands.Add(Or());
                SkipBlanks();
                if (!Take(','))
                {
                    break;
                }

                SkipBlanks();
            }
        }

        if (!Take(')'))
        {
            throw Unexpected("',' or ')'");
        }

        Leave();
        var parameters = signature.Parameters;
        if (operands.Count != parameters.Length)
        {
            throw Error($"{name}() takes {parameters.Length} argument{(parameters.Length == 1 ? "" : "s")}, not {operands.Count}", start);
        }

        var arguments = new object[operands.Count];
        for (var i = 0; i < operands.Count; i++)
        {
            arguments[i] = parameters[i] switch
            {
                PathType.Value => Value(operands[i]),
                PathType.Nodes => operands[i].Kind == OperandKind.Query
                    ? operands[i].Node
                    : throw Error($"{name}() takes a query, such as @.items or $..id", operands[i].Position),
                _ => Test(operands[i]),
            };
        }

        return new FunctionCall(name, signature.Function, signature.Result, arguments);
    }

    /// <summary>An operand used as a value: a literal, a query that selects at most one value, or length, count or value.</summary>
    private ValueExpression Value(Operand operand) => operand.Kind switch
    {
        OperandKind.Literal => (LiteralExpression)operand.Node,
        OperandKind.Query when ((PathQuery)operand.Node).IsSingular => new QueryValueExpression((PathQuery)operand.Node),
        OperandKind.Query => throw Error(
            "This query can select more than one value, where one is expected: use names and indexes only (@.a[0]), or count() or value()",
            operand.Position),
        OperandKind.Function when ((FunctionCall)operand.Node).Result == PathType.Value => new FunctionValueExpression((FunctionCall)operand.Node),
        OperandKind.Function => throw Error($"{((FunctionCall)operand.Node).Name}() is true or false, not a value: use it as a test", operand.Position),
        _ => throw Error("A test (==, &&, ! …) is not a value: it cannot be compared", operand.Position),
    };

    /// <summary>An operand used as a test: a query (whether it selects anything), a comparison, or match or search.</summary>
    private LogicalExpression Test(Operand operand) => operand.Kind switch
    {
        OperandKind.Logical => (LogicalExpression)operand.Node,
        OperandKind.Query => new ExistsExpression((PathQuery)operand.Node),
        OperandKind.Function when ((FunctionCall)operand.Node).Result != PathType.Value => new FunctionTestExpression((FunctionCall)operand.Node),
        OperandKind.Function => throw Error($"{((FunctionCall)operand.Node).Name}() is a value, not a test: compare it, e.g. {((FunctionCall)operand.Node).Name}(…) > 0", operand.Position),
        _ => throw Error("A value is not a test: compare it, e.g. @.name == 'x'", operand.Position),
    };

    // ----- characters ---------------------------------------------------------------------

    private bool Peek(char c, int offset = 0) => _pos + offset < _text.Length && _text[_pos + offset] == c;

    private bool Take(char c)
    {
        if (!Peek(c))
        {
            return false;
        }

        _pos++;
        return true;
    }

    private bool TakeOperator(string op)
    {
        var start = _pos;
        SkipBlanks();
        if (_text.AsSpan(_pos).StartsWith(op, StringComparison.Ordinal))
        {
            _pos += op.Length;
            SkipBlanks();
            return true;
        }

        _pos = start;
        return false;
    }

    private void SkipBlanks()
    {
        while (_pos < _text.Length && _text[_pos] is ' ' or '\t' or '\n' or '\r')
        {
            _pos++;
        }
    }

    private void SkipDigits()
    {
        while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
        {
            _pos++;
        }
    }

    private void RequireDigits(string message)
    {
        if (!(_pos < _text.Length && char.IsAsciiDigit(_text[_pos])))
        {
            throw Error(message);
        }

        SkipDigits();
    }

    private void Enter()
    {
        if (++_nesting > MaxNesting)
        {
            throw Error($"The query nests filters, parentheses and functions more than {MaxNesting} deep");
        }
    }

    private void Leave() => _nesting--;

    private JsonPathException Unexpected(string expected) =>
        _pos >= _text.Length ? Error($"The query ends where {expected} is expected") : Error($"Unexpected {Describe(_pos)}: expected {expected}");

    private JsonPathException Error(string message, int? at = null)
    {
        var position = at ?? _pos;
        return new JsonPathException($"{message} (character {position + 1}).", position);
    }

    private string Describe(int at) => _text[at] switch
    {
        ' ' => "space",
        '\t' => "tab",
        '\n' or '\r' => "line break",
        var c when char.IsControl(c) => $"control character U+{(int)c:X4}",
        var c when char.IsHighSurrogate(c) && at + 1 < _text.Length && char.IsLowSurrogate(_text[at + 1]) => $"'{_text.Substring(at, 2)}'",
        var c when char.IsSurrogate(c) => $"half of a character (U+{(int)c:X4})",
        var c => $"'{c}'",
    };

    /// <summary>A part of a filter expression, whose use (a value, a query or a test) is checked where it is used.</summary>
    private readonly record struct Operand(OperandKind Kind, object Node, int Position);
}
