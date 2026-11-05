using System.Text.Encodings.Web;

namespace TermRest.Core.Json;

/// <summary>
/// Escapes only what JSON requires (quotes, backslashes and control characters), like JavaScript's
/// <c>JSON.stringify</c>, so files exported by Postman round-trip byte-for-byte.
/// </summary>
internal sealed class MinimalJsonEncoder : JavaScriptEncoder
{
    public static readonly MinimalJsonEncoder Instance = new();

    private MinimalJsonEncoder()
    {
    }

    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) =>
        unicodeScalar < 0x20 || unicodeScalar == '"' || unicodeScalar == '\\' || unicodeScalar is >= 0xD800 and <= 0xDFFF;

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            var c = text[i];
            if (c < 0x20 || c == '"' || c == '\\')
            {
                return i;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < textLength && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return i;
            }

            if (char.IsLowSurrogate(c))
            {
                return i;
            }
        }

        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        Span<char> scratch = stackalloc char[6];
        scoped ReadOnlySpan<char> escaped;
        switch (unicodeScalar)
        {
            case '"': escaped = "\\\""; break;
            case '\\': escaped = "\\\\"; break;
            case '\b': escaped = "\\b"; break;
            case '\f': escaped = "\\f"; break;
            case '\n': escaped = "\\n"; break;
            case '\r': escaped = "\\r"; break;
            case '\t': escaped = "\\t"; break;
            default:
                if (unicodeScalar > 0xFFFF)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }

                scratch[0] = '\\';
                scratch[1] = 'u';
                ((ushort)unicodeScalar).TryFormat(scratch[2..], out _, "x4", System.Globalization.CultureInfo.InvariantCulture);
                escaped = scratch;
                break;
        }

        if (escaped.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        escaped.CopyTo(new Span<char>(buffer, bufferLength));
        numberOfCharactersWritten = escaped.Length;
        return true;
    }
}
