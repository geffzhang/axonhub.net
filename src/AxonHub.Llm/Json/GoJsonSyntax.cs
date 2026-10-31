using System.Text;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of Go's <c>encoding/json</c> syntax scanner, used to reproduce the exact
/// <c>SyntaxError</c> text <c>json.Unmarshal</c> reports before it decodes.
/// </summary>
/// <remarks>
/// <para>
/// Go validates the whole document first (<c>checkValid</c>) and only then
/// decodes, so a malformed body always yields one of the messages produced by
/// this state machine — never a decoder message. System.Text.Json's own syntax
/// errors are worded completely differently, so the port runs this pass first
/// and surfaces its message.
/// </para>
/// <para>
/// Type errors (valid JSON, wrong shape) still come from the decoder and are
/// tracked as difference C003.
/// </para>
/// </remarks>
public sealed class GoJsonSyntax
{
    private const int MaxNestingDepth = 10000;

    private const int ParseObjectKey = 0;
    private const int ParseObjectValue = 1;
    private const int ParseArrayValue = 2;

    private const int OpContinue = 0;
    private const int OpBeginLiteral = 1;
    private const int OpBeginObject = 2;
    private const int OpObjectKey = 3;
    private const int OpObjectValue = 4;
    private const int OpEndObject = 5;
    private const int OpBeginArray = 6;
    private const int OpArrayValue = 7;
    private const int OpEndArray = 8;
    private const int OpSkipSpace = 9;
    private const int OpEnd = 10;
    private const int OpError = 11;

    private const int SBeginValue = 0;
    private const int SBeginValueOrEmpty = 1;
    private const int SBeginStringOrEmpty = 2;
    private const int SBeginString = 3;
    private const int SEndValue = 4;
    private const int SEndTop = 5;
    private const int SInString = 6;
    private const int SInStringEsc = 7;
    private const int SInStringEscU = 8;
    private const int SInStringEscU1 = 9;
    private const int SInStringEscU12 = 10;
    private const int SInStringEscU123 = 11;
    private const int SNeg = 12;
    private const int SOne = 13;
    private const int SZero = 14;
    private const int SDot = 15;
    private const int SDotZero = 16;
    private const int SE = 17;
    private const int SESign = 18;
    private const int SEZero = 19;
    private const int ST = 20;
    private const int STr = 21;
    private const int STru = 22;
    private const int SF = 23;
    private const int SFa = 24;
    private const int SFal = 25;
    private const int SFals = 26;
    private const int SN = 27;
    private const int SNu = 28;
    private const int SNul = 29;
    private const int SError = 30;

    private readonly List<int> _parseState = [];
    private int _state;
    private bool _endTop;
    private string? _error;

    // Streaming mode (json.Decoder.readValue): the scanner stops as soon as one
    // top-level value completes, leaving the byte that revealed the end
    // unconsumed ("scanEnd is delayed one byte").
    private bool _streamMode;
    private bool _valueComplete;
    private int _valueEnd;
    private int _byteIndex;

    /// <summary>
    /// Returns Go's <c>SyntaxError</c> text for <paramref name="data"/>, or
    /// <see langword="null"/> when the document is valid JSON.
    /// </summary>
    public static string? Validate(ReadOnlySpan<byte> data)
    {
        var scanner = new GoJsonSyntax();
        return scanner.Run(data);
    }

    /// <summary>
    /// Port of one <c>json.Decoder.Decode</c> scan step
    /// (<c>Decoder.readValue</c>): scans exactly one top-level value starting at
    /// <paramref name="start"/> and leaves every later byte untouched.
    /// </summary>
    /// <param name="data">The whole body.</param>
    /// <param name="start">Offset of the next unread byte.</param>
    /// <param name="end">Offset just past the decoded value on success.</param>
    /// <returns>
    /// <see langword="null"/> when a value was decoded; <c>"EOF"</c> when only
    /// whitespace remains (Go's <c>io.EOF</c>); <c>"unexpected EOF"</c> when the
    /// input ends inside a value (Go's <c>io.ErrUnexpectedEOF</c>); otherwise the
    /// scanner's own <c>SyntaxError</c> text.
    /// </returns>
    public static string? ScanDecoderValue(ReadOnlySpan<byte> data, int start, out int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, data.Length);

        var scanner = new GoJsonSyntax { _streamMode = true };
        return scanner.ScanStream(data, start, out end);
    }

    private string? Run(ReadOnlySpan<byte> data)
    {
        _state = SBeginValue;
        _parseState.Clear();
        _error = null;
        _endTop = false;

        foreach (var b in data)
        {
            if (Step(b) == OpError)
            {
                return _error;
            }
        }

        return EndOfInput() ? null : _error;
    }

    /// <summary>Port of <c>Decoder.readValue</c>'s scan loop.</summary>
    private string? ScanStream(ReadOnlySpan<byte> data, int start, out int end)
    {
        _state = SBeginValue;
        _parseState.Clear();
        _error = null;
        _endTop = false;
        _valueComplete = false;

        for (var i = start; i < data.Length; i++)
        {
            _byteIndex = i;
            if (Step(data[i]) == OpError)
            {
                end = data.Length;
                return _error;
            }

            if (_valueComplete)
            {
                end = _valueEnd;
                return null;
            }
        }

        // readValue invents one space byte at EOF. A value that ends there is
        // complete; otherwise readValue discards any scanner error from that
        // synthetic step and reports io.EOF / io.ErrUnexpectedEOF instead.
        _byteIndex = data.Length;
        _ = Step((byte)' ');
        if (_valueComplete)
        {
            end = data.Length;
            return null;
        }

        end = data.Length;
        return HasNonSpace(data, start) ? "unexpected EOF" : "EOF";
    }

    private static bool HasNonSpace(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start; i < data.Length; i++)
        {
            if (!IsSpace(data[i]))
            {
                return true;
            }
        }

        return false;
    }

    private bool EndOfInput()
    {
        if (_error is not null)
        {
            return false;
        }

        if (_endTop)
        {
            return true;
        }

        _ = Step((byte)' ');

        if (_endTop)
        {
            return true;
        }

        _error ??= "unexpected end of JSON input";
        return false;
    }

    private int Step(byte c)
    {
        return _state switch
        {
            SBeginValue => StateBeginValue(c),
            SBeginValueOrEmpty => StateBeginValueOrEmpty(c),
            SBeginStringOrEmpty => StateBeginStringOrEmpty(c),
            SBeginString => StateBeginString(c),
            SEndValue => StateEndValue(c),
            SEndTop => StateEndTop(c),
            SInString => StateInString(c),
            SInStringEsc => StateInStringEsc(c),
            SInStringEscU => StateInStringEscU(c),
            SInStringEscU1 => StateInStringEscU1(c),
            SInStringEscU12 => StateInStringEscU12(c),
            SInStringEscU123 => StateInStringEscU123(c),
            SNeg => StateNeg(c),
            SOne => StateOne(c),
            SZero => StateZero(c),
            SDot => StateDot(c),
            SDotZero => StateDotZero(c),
            SE => StateE(c),
            SESign => StateESign(c),
            SEZero => StateEZero(c),
            ST => StateT(c),
            STr => StateTr(c),
            STru => StateTru(c),
            SF => StateF(c),
            SFa => StateFa(c),
            SFal => StateFal(c),
            SFals => StateFals(c),
            SN => StateN(c),
            SNu => StateNu(c),
            SNul => StateNul(c),
            _ => OpError,
        };
    }

    private static bool IsSpace(byte c) => c <= ' ' && (c == ' ' || c == '\t' || c == '\r' || c == '\n');

    private static bool IsDigit(byte c) => c >= '0' && c <= '9';

    private static bool IsHexDigit(byte c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private int PushParseState(byte c, int newParseState, int successOp)
    {
        _parseState.Add(newParseState);
        if (_parseState.Count <= MaxNestingDepth)
        {
            return successOp;
        }

        return Fail(c, "exceeded max depth");
    }

    private void PopParseState()
    {
        var n = _parseState.Count - 1;
        _parseState.RemoveAt(n);

        if (n == 0)
        {
            _state = SEndTop;
            _endTop = true;
            if (_streamMode)
            {
                // The closing bracket that completes the top-level container is
                // part of the value.
                _valueComplete = true;
                _valueEnd = _byteIndex + 1;
            }
        }
        else
        {
            _state = SEndValue;
        }
    }

    private int Fail(byte c, string context)
    {
        _state = SError;
        _error = "invalid character " + QuoteChar(c) + " " + context;
        return OpError;
    }

    private int StateBeginValueOrEmpty(byte c)
    {
        if (IsSpace(c))
        {
            return OpSkipSpace;
        }

        if (c == ']')
        {
            return StateEndValue(c);
        }

        return StateBeginValue(c);
    }

    private int StateBeginValue(byte c)
    {
        if (IsSpace(c))
        {
            return OpSkipSpace;
        }

        switch (c)
        {
            case (byte)'{':
                _state = SBeginStringOrEmpty;
                return PushParseState(c, ParseObjectKey, OpBeginObject);
            case (byte)'[':
                _state = SBeginValueOrEmpty;
                return PushParseState(c, ParseArrayValue, OpBeginArray);
            case (byte)'"':
                _state = SInString;
                return OpBeginLiteral;
            case (byte)'-':
                _state = SNeg;
                return OpBeginLiteral;
            case (byte)'0':
                _state = SZero;
                return OpBeginLiteral;
            case (byte)'t':
                _state = ST;
                return OpBeginLiteral;
            case (byte)'f':
                _state = SF;
                return OpBeginLiteral;
            case (byte)'n':
                _state = SN;
                return OpBeginLiteral;
        }

        if (c >= '1' && c <= '9')
        {
            _state = SOne;
            return OpBeginLiteral;
        }

        return Fail(c, "looking for beginning of value");
    }

    private int StateBeginStringOrEmpty(byte c)
    {
        if (IsSpace(c))
        {
            return OpSkipSpace;
        }

        if (c == '}')
        {
            _parseState[^1] = ParseObjectValue;
            return StateEndValue(c);
        }

        return StateBeginString(c);
    }

    private int StateBeginString(byte c)
    {
        if (IsSpace(c))
        {
            return OpSkipSpace;
        }

        if (c == '"')
        {
            _state = SInString;
            return OpBeginLiteral;
        }

        return Fail(c, "looking for beginning of object key string");
    }

    private int StateEndValue(byte c)
    {
        var n = _parseState.Count;
        if (n == 0)
        {
            _state = SEndTop;
            _endTop = true;
            if (_streamMode)
            {
                // The current byte revealed the end of a top-level scalar but is
                // not part of it; the next Decode starts there.
                _valueComplete = true;
                _valueEnd = _byteIndex;
                return OpEnd;
            }

            return StateEndTop(c);
        }

        if (IsSpace(c))
        {
            _state = SEndValue;
            return OpSkipSpace;
        }

        switch (_parseState[n - 1])
        {
            case ParseObjectKey:
                if (c == ':')
                {
                    _parseState[n - 1] = ParseObjectValue;
                    _state = SBeginValue;
                    return OpObjectKey;
                }

                return Fail(c, "after object key");
            case ParseObjectValue:
                if (c == ',')
                {
                    _parseState[n - 1] = ParseObjectKey;
                    _state = SBeginString;
                    return OpObjectValue;
                }

                if (c == '}')
                {
                    PopParseState();
                    return OpEndObject;
                }

                return Fail(c, "after object key:value pair");
            default:
                if (c == ',')
                {
                    _state = SBeginValue;
                    return OpArrayValue;
                }

                if (c == ']')
                {
                    PopParseState();
                    return OpEndArray;
                }

                return Fail(c, "after array element");
        }
    }

    private int StateEndTop(byte c)
    {
        if (!IsSpace(c))
        {
            _ = Fail(c, "after top-level value");
        }

        return OpEnd;
    }

    private int StateInString(byte c)
    {
        if (c == '"')
        {
            _state = SEndValue;
            return OpContinue;
        }

        if (c == '\\')
        {
            _state = SInStringEsc;
            return OpContinue;
        }

        if (c < 0x20)
        {
            return Fail(c, "in string literal");
        }

        return OpContinue;
    }

    private int StateInStringEsc(byte c)
    {
        switch (c)
        {
            case (byte)'b':
            case (byte)'f':
            case (byte)'n':
            case (byte)'r':
            case (byte)'t':
            case (byte)'\\':
            case (byte)'/':
            case (byte)'"':
                _state = SInString;
                return OpContinue;
            case (byte)'u':
                _state = SInStringEscU;
                return OpContinue;
        }

        return Fail(c, "in string escape code");
    }

    private int StateInStringEscU(byte c)
    {
        if (IsHexDigit(c))
        {
            _state = SInStringEscU1;
            return OpContinue;
        }

        return Fail(c, "in \\u hexadecimal character escape");
    }

    private int StateInStringEscU1(byte c)
    {
        if (IsHexDigit(c))
        {
            _state = SInStringEscU12;
            return OpContinue;
        }

        return Fail(c, "in \\u hexadecimal character escape");
    }

    private int StateInStringEscU12(byte c)
    {
        if (IsHexDigit(c))
        {
            _state = SInStringEscU123;
            return OpContinue;
        }

        return Fail(c, "in \\u hexadecimal character escape");
    }

    private int StateInStringEscU123(byte c)
    {
        if (IsHexDigit(c))
        {
            _state = SInString;
            return OpContinue;
        }

        return Fail(c, "in \\u hexadecimal character escape");
    }

    private int StateNeg(byte c)
    {
        if (c == '0')
        {
            _state = SZero;
            return OpContinue;
        }

        if (c >= '1' && c <= '9')
        {
            _state = SOne;
            return OpContinue;
        }

        return Fail(c, "in numeric literal");
    }

    private int StateOne(byte c)
    {
        if (IsDigit(c))
        {
            _state = SOne;
            return OpContinue;
        }

        return StateZero(c);
    }

    private int StateZero(byte c)
    {
        if (c == '.')
        {
            _state = SDot;
            return OpContinue;
        }

        if (c is (byte)'e' or (byte)'E')
        {
            _state = SE;
            return OpContinue;
        }

        return StateEndValue(c);
    }

    private int StateDot(byte c)
    {
        if (IsDigit(c))
        {
            _state = SDotZero;
            return OpContinue;
        }

        return Fail(c, "after decimal point in numeric literal");
    }

    private int StateDotZero(byte c)
    {
        if (IsDigit(c))
        {
            return OpContinue;
        }

        if (c is (byte)'e' or (byte)'E')
        {
            _state = SE;
            return OpContinue;
        }

        return StateEndValue(c);
    }

    private int StateE(byte c)
    {
        if (c is (byte)'+' or (byte)'-')
        {
            _state = SESign;
            return OpContinue;
        }

        return StateESign(c);
    }

    private int StateESign(byte c)
    {
        if (IsDigit(c))
        {
            _state = SEZero;
            return OpContinue;
        }

        return Fail(c, "in exponent of numeric literal");
    }

    private int StateEZero(byte c)
    {
        if (IsDigit(c))
        {
            return OpContinue;
        }

        return StateEndValue(c);
    }

    private int StateT(byte c)
    {
        if (c == 'r')
        {
            _state = STr;
            return OpContinue;
        }

        return Fail(c, "in literal true (expecting 'r')");
    }

    private int StateTr(byte c)
    {
        if (c == 'u')
        {
            _state = STru;
            return OpContinue;
        }

        return Fail(c, "in literal true (expecting 'u')");
    }

    private int StateTru(byte c)
    {
        if (c == 'e')
        {
            _state = SEndValue;
            return OpContinue;
        }

        return Fail(c, "in literal true (expecting 'e')");
    }

    private int StateF(byte c)
    {
        if (c == 'a')
        {
            _state = SFa;
            return OpContinue;
        }

        return Fail(c, "in literal false (expecting 'a')");
    }

    private int StateFa(byte c)
    {
        if (c == 'l')
        {
            _state = SFal;
            return OpContinue;
        }

        return Fail(c, "in literal false (expecting 'l')");
    }

    private int StateFal(byte c)
    {
        if (c == 's')
        {
            _state = SFals;
            return OpContinue;
        }

        return Fail(c, "in literal false (expecting 's')");
    }

    private int StateFals(byte c)
    {
        if (c == 'e')
        {
            _state = SEndValue;
            return OpContinue;
        }

        return Fail(c, "in literal false (expecting 'e')");
    }

    private int StateN(byte c)
    {
        if (c == 'u')
        {
            _state = SNu;
            return OpContinue;
        }

        return Fail(c, "in literal null (expecting 'u')");
    }

    private int StateNu(byte c)
    {
        if (c == 'l')
        {
            _state = SNul;
            return OpContinue;
        }

        return Fail(c, "in literal null (expecting 'l')");
    }

    private int StateNul(byte c)
    {
        if (c == 'l')
        {
            _state = SEndValue;
            return OpContinue;
        }

        return Fail(c, "in literal null (expecting 'l')");
    }

    /// <summary>Port of Go's <c>quoteChar</c>: the byte as a Go character literal.</summary>
    private static string QuoteChar(byte c)
    {
        if (c == '\'')
        {
            return "'\\''";
        }

        if (c == '"')
        {
            return "'\"'";
        }

        return "'" + QuoteRuneBody(c) + "'";
    }

    /// <summary>Body of <c>strconv.Quote(string(rune(c)))</c> with the quotes stripped.</summary>
    private static string QuoteRuneBody(byte c)
    {
        switch (c)
        {
            case (byte)'\\':
                return "\\\\";
            case 0x07:
                return "\\a";
            case 0x08:
                return "\\b";
            case 0x0C:
                return "\\f";
            case (byte)'\n':
                return "\\n";
            case (byte)'\r':
                return "\\r";
            case (byte)'\t':
                return "\\t";
            case 0x0B:
                return "\\v";
        }

        if (c >= 0x20 && c <= 0x7E)
        {
            return ((char)c).ToString();
        }

        // strconv.IsPrint accepts Latin-1 letters, punctuation and symbols, so
        // only the C1 controls, U+00A0 and the soft hyphen U+00AD are escaped.
        if (c >= 0xA1 && c != 0xAD)
        {
            return ((char)c).ToString();
        }

        var sb = new StringBuilder(6);
        if (c < 0x80)
        {
            sb.Append("\\x");
            sb.Append(c.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("\\u");
            sb.Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
