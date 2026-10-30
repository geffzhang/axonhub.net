using System.Globalization;
using System.Text;
using AxonHub.Abstractions;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Template data for channel override rendering (the reference's
/// orchestrator.RenderContext, internal/server/orchestrator/override.go:23-40).
/// </summary>
/// <remarks>
/// Field names mirror the exported Go struct fields because templates address
/// them by name; <see cref="Metadata"/> can be a nil Go map when the inbound
/// request carried no metadata.
/// </remarks>
internal sealed class OverrideRenderContext
{
    /// <summary>The model used in the original client request.</summary>
    public string RequestModel { get; init; } = string.Empty;

    /// <summary>The model sent to the LLM service.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The metadata of the current request; a nil map when absent.</summary>
    public TemplateMap Metadata { get; init; } = TemplateMap.Nil;

    /// <summary>Filtered request headers keyed by canonical and lowercase names.</summary>
    public TemplateMap RequestHeader { get; init; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The prompt cache key of the original request.</summary>
    public string PromptCacheKey { get; init; } = string.Empty;

    /// <summary>The reasoning effort of the current request.</summary>
    public string ReasoningEffort { get; init; } = string.Empty;
}

/// <summary>
/// A Go <c>map[string]string</c> as template data. A <see langword="null"/>
/// <see cref="Values"/> represents a nil map: it formats as <c>map[]</c>,
/// marshals as <c>null</c> and indexes to the string zero value.
/// </summary>
internal sealed class TemplateMap(Dictionary<string, string>? values)
{
    public static TemplateMap Nil { get; } = new(null);

    public Dictionary<string, string>? Values { get; } = values;

    public bool IsNil => Values is null;

    public int Count => Values?.Count ?? 0;

    public bool TryGetValue(string key, out string value)
    {
        if (Values is not null && Values.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Go map iteration/formatting order: sorted by key.</summary>
    public IEnumerable<KeyValuePair<string, string>> SortedMembers()
        => Values is null
            ? Enumerable.Empty<KeyValuePair<string, string>>()
            : Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal);
}

/// <summary>
/// A behaviour-compatible subset of Go's <c>text/template</c> for channel
/// override values (orchestrator.renderTemplate / renderOverrideValue).
/// </summary>
/// <remarks>
/// The reference renders every override value (and condition) with
/// <c>template.New("override").Funcs(overrideTemplateFuncs).Parse(value)</c> and
/// returns the original string when parsing or execution fails, so any
/// construct this engine cannot evaluate also falls back to the original text.
/// <para>
/// Supported: text/actions, <c>{{- -}}</c> trim markers, comments, pipelines,
/// <c>if/else if/else</c>, <c>range</c>, <c>with</c>, variables and declarations,
/// field/map chains, and the functions the reference exposes plus the commonly
/// used text/template builtins. Unsupported Go template features (define,
/// block, template, and obscure builtins such as <c>slice</c>/<c>call</c>)
/// degrade to the unrendered value exactly like a parse error in the reference
/// (difference C040).
/// </para>
/// </remarks>
internal static class GoTextTemplate
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if",
        "else",
        "end",
        "range",
        "with",
        "define",
        "block",
        "template",
        "break",
        "continue",
    };

    private static readonly HashSet<string> FunctionNames = new(StringComparer.Ordinal)
    {
        "toJSON",
        "eq",
        "ne",
        "lt",
        "le",
        "gt",
        "ge",
        "and",
        "or",
        "not",
        "len",
        "index",
        "print",
        "println",
        "printf",
    };

    /// <summary>
    /// Renders <paramref name="value"/> against <paramref name="context"/>; on
    /// any parse or execution error returns the original string (the reference
    /// logs the error and falls back).
    /// </summary>
    public static string Render(string value, OverrideRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(context);

        if (!value.Contains("{{", StringComparison.Ordinal) || !value.Contains("}}", StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var elements = Scan(value);
            var parser = new TemplateParser(elements);
            var nodes = parser.ParseRoot();
            var state = new TemplateState(context);
            var output = new StringBuilder(value.Length);
            ExecuteNodes(nodes, state, context, output);
            return output.ToString();
        }
        catch (GoTemplateException)
        {
            return value;
        }
    }

    // -----------------------------------------------------------------------
    // Scanning: text, actions, trim markers and comments
    // -----------------------------------------------------------------------

    private abstract class TemplateElement;

    private sealed class TemplateTextElement(string text) : TemplateElement
    {
        public string Text { get; } = text;
    }

    private sealed class TemplateActionElement(string content) : TemplateElement
    {
        public string Content { get; } = content;
    }

    private static List<TemplateElement> Scan(string template)
    {
        var elements = new List<TemplateElement>();
        var position = 0;
        var trimLeading = false;

        while (position < template.Length)
        {
            var start = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0)
            {
                AddText(elements, template[position..], trimLeading);
                break;
            }

            var afterDelimiter = start + 2;
            var trimTrailing = afterDelimiter + 1 < template.Length
                && template[afterDelimiter] == '-'
                && IsSpace(template[afterDelimiter + 1]);
            var contentStart = trimTrailing ? afterDelimiter + 1 : afterDelimiter;

            var text = template[position..start];
            if (trimTrailing)
            {
                text = text.TrimEnd(' ', '\t', '\r', '\n');
            }

            AddText(elements, text, trimLeading);
            trimLeading = false;

            var close = FindActionEnd(template, contentStart);
            var content = template[contentStart..close];

            var trimNext = false;
            if (content.Length >= 2 && content[^1] == '-' && IsSpace(content[^2]))
            {
                content = content[..^1];
                trimNext = true;
            }

            var trimmed = content.TrimStart(' ', '\t', '\r', '\n');
            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var commentEnd = trimmed.IndexOf("*/", 2, StringComparison.Ordinal);
                if (commentEnd < 0 || trimmed[(commentEnd + 2)..].Trim(' ', '\t', '\r', '\n').Length != 0)
                {
                    throw new GoTemplateException("comment ends before closing delimiter");
                }
            }
            else
            {
                elements.Add(new TemplateActionElement(content));
            }

            position = close + 2;
            trimLeading = trimNext;
        }

        return elements;
    }

    private static void AddText(List<TemplateElement> elements, string text, bool trimLeading)
    {
        if (trimLeading)
        {
            text = text.TrimStart(' ', '\t', '\r', '\n');
        }

        if (text.Length > 0)
        {
            elements.Add(new TemplateTextElement(text));
        }
    }

    private static int FindActionEnd(string template, int start)
    {
        // A comment's closing "*/" is found before the action delimiter: the
        // lexer then requires the delimiter to follow the comment, so a "}}"
        // inside the comment text must not end the action.
        var look = start;
        while (look < template.Length && IsSpace(template[look]))
        {
            look++;
        }

        var scan = start;
        if (look + 1 < template.Length && template[look] == '/' && template[look + 1] == '*')
        {
            var commentEnd = template.IndexOf("*/", look + 2, StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                throw new GoTemplateException("unclosed comment");
            }

            scan = commentEnd + 2;
        }

        for (var index = scan; index < template.Length; index++)
        {
            switch (template[index])
            {
                case '"':
                    index = SkipQuoted(template, index, '"');
                    break;
                case '\'':
                    index = SkipQuoted(template, index, '\'');
                    break;
                case '`':
                    var rawEnd = template.IndexOf('`', index + 1);
                    if (rawEnd < 0)
                    {
                        throw new GoTemplateException("unterminated raw string");
                    }

                    index = rawEnd;
                    break;
                case '}' when index + 1 < template.Length && template[index + 1] == '}':
                    return index;
            }
        }

        throw new GoTemplateException("unclosed action");
    }

    private static int SkipQuoted(string template, int start, char quote)
    {
        for (var index = start + 1; index < template.Length; index++)
        {
            if (template[index] == '\\')
            {
                index++;
                continue;
            }

            if (template[index] == quote)
            {
                return index;
            }
        }

        throw new GoTemplateException("unterminated quoted string");
    }

    // -----------------------------------------------------------------------
    // Expression tokens
    // -----------------------------------------------------------------------

    private enum TemplateTokenKind
    {
        Identifier,
        Field,
        Variable,
        String,
        Number,
        Character,
        LeftParen,
        RightParen,
        Pipe,
        Comma,
        Declare,
        Assign,
        Dot,
    }

    private sealed class TemplateToken(
        TemplateTokenKind kind,
        string text,
        int start,
        int end,
        object? value = null)
    {
        public TemplateTokenKind Kind { get; } = kind;

        public string Text { get; } = text;

        public int Start { get; } = start;

        public int End { get; } = end;

        public object? Value { get; } = value;
    }

    private static bool IsSpace(char value) => value is ' ' or '\t' or '\r' or '\n';

    private static bool IsAlphaNumeric(char value) => value == '_' || char.IsLetter(value) || char.IsDigit(value);

    private static List<TemplateToken> LexAction(string content)
    {
        var tokens = new List<TemplateToken>();
        var index = 0;

        while (index < content.Length)
        {
            var current = content[index];
            if (IsSpace(current))
            {
                index++;
                continue;
            }

            var start = index;
            switch (current)
            {
                case '|':
                    index++;
                    tokens.Add(new TemplateToken(TemplateTokenKind.Pipe, "|", start, index));
                    break;

                case '(':
                    index++;
                    tokens.Add(new TemplateToken(TemplateTokenKind.LeftParen, "(", start, index));
                    break;

                case ')':
                    index++;
                    tokens.Add(new TemplateToken(TemplateTokenKind.RightParen, ")", start, index));
                    break;

                case ',':
                    index++;
                    tokens.Add(new TemplateToken(TemplateTokenKind.Comma, ",", start, index));
                    break;

                case ':':
                    if (index + 1 >= content.Length || content[index + 1] != '=')
                    {
                        throw new GoTemplateException("unexpected ':'");
                    }

                    index += 2;
                    tokens.Add(new TemplateToken(TemplateTokenKind.Declare, ":=", start, index));
                    break;

                case '=':
                    index++;
                    tokens.Add(new TemplateToken(TemplateTokenKind.Assign, "=", start, index));
                    break;

                case '"':
                {
                    var (value, end) = ReadQuotedString(content, index);
                    tokens.Add(new TemplateToken(TemplateTokenKind.String, content[start..end], start, end, value));
                    index = end;
                    break;
                }

                case '`':
                {
                    var end = content.IndexOf('`', index + 1);
                    if (end < 0)
                    {
                        throw new GoTemplateException("unterminated raw string");
                    }

                    var value = content[(index + 1)..end].Replace("\r", string.Empty, StringComparison.Ordinal);
                    tokens.Add(new TemplateToken(TemplateTokenKind.String, content[start..(end + 1)], start, end + 1, value));
                    index = end + 1;
                    break;
                }

                case '\'':
                {
                    var (value, end) = ReadCharacter(content, index);
                    tokens.Add(new TemplateToken(TemplateTokenKind.Character, content[start..end], start, end, value));
                    index = end;
                    break;
                }

                case '.':
                    if (index + 1 < content.Length && IsAlphaNumeric(content[index + 1]))
                    {
                        var nameEnd = ReadNameEnd(content, index + 1);
                        tokens.Add(new TemplateToken(
                            TemplateTokenKind.Field,
                            content[(index + 1)..nameEnd],
                            start,
                            nameEnd));
                        index = nameEnd;
                    }
                    else
                    {
                        index++;
                        tokens.Add(new TemplateToken(TemplateTokenKind.Dot, ".", start, index));
                    }

                    break;

                case '$':
                {
                    var nameEnd = ReadNameEnd(content, index + 1);
                    tokens.Add(new TemplateToken(
                        TemplateTokenKind.Variable,
                        content[(index + 1)..nameEnd],
                        start,
                        nameEnd));
                    index = nameEnd;
                    break;
                }

                default:
                    if (char.IsDigit(current)
                        || (current is '+' or '-' && index + 1 < content.Length && char.IsDigit(content[index + 1])))
                    {
                        var (value, end) = ReadNumber(content, index);
                        tokens.Add(new TemplateToken(TemplateTokenKind.Number, content[start..end], start, end, value));
                        index = end;
                        break;
                    }

                    if (IsAlphaNumeric(current))
                    {
                        var end = ReadNameEnd(content, index);
                        tokens.Add(new TemplateToken(
                            TemplateTokenKind.Identifier,
                            content[start..end],
                            start,
                            end));
                        index = end;
                        break;
                    }

                    throw new GoTemplateException($"unexpected character '{current}' in action");
            }
        }

        return tokens;
    }

    private static int ReadNameEnd(string content, int start)
    {
        var end = start;
        while (end < content.Length && IsAlphaNumeric(content[end]))
        {
            end++;
        }

        return end;
    }

    private static (string Value, int End) ReadQuotedString(string content, int start)
    {
        var builder = new StringBuilder();
        var index = start + 1;
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '"')
            {
                return (builder.ToString(), index + 1);
            }

            if (current == '\n')
            {
                throw new GoTemplateException("unterminated quoted string");
            }

            if (current == '\\')
            {
                var (escaped, end) = ReadEscape(content, index, '"');
                builder.Append(escaped);
                index = end;
                continue;
            }

            builder.Append(current);
            index++;
        }

        throw new GoTemplateException("unterminated quoted string");
    }

    private static (long Value, int End) ReadCharacter(string content, int start)
    {
        var index = start + 1;
        if (index >= content.Length)
        {
            throw new GoTemplateException("unterminated character constant");
        }

        string text;
        if (content[index] == '\\')
        {
            var (escaped, end) = ReadEscape(content, index, '\'');
            text = escaped;
            index = end;
        }
        else if (char.IsHighSurrogate(content[index])
            && index + 1 < content.Length
            && char.IsLowSurrogate(content[index + 1]))
        {
            text = content.Substring(index, 2);
            index += 2;
        }
        else
        {
            text = content[index].ToString();
            index++;
        }

        if (index >= content.Length || content[index] != '\'')
        {
            throw new GoTemplateException("invalid character constant");
        }

        if ((text.Length == 1 && char.IsSurrogate(text[0]))
            || (text.Length == 2 && !char.IsSurrogatePair(text[0], text[1])))
        {
            throw new GoTemplateException("invalid character constant");
        }

        var rune = char.ConvertToUtf32(text, 0);
        return (rune, index + 1);
    }

    private static (string Value, int End) ReadEscape(string content, int start, char quote)
    {
        if (start + 1 >= content.Length)
        {
            throw new GoTemplateException("unterminated escape");
        }

        var escape = content[start + 1];
        switch (escape)
        {
            case 'a':
                return ("\a", start + 2);
            case 'b':
                return ("\b", start + 2);
            case 'f':
                return ("\f", start + 2);
            case 'n':
                return ("\n", start + 2);
            case 'r':
                return ("\r", start + 2);
            case 't':
                return ("\t", start + 2);
            case 'v':
                return ("\v", start + 2);
            case '\\':
                return ("\\", start + 2);
            case '\'':
                return ("'", start + 2);
            case '"':
                return ("\"", start + 2);
            case 'x':
                return (ReadHexEscape(content, start + 2, 2), start + 4);
            case 'u':
                return (ReadHexEscape(content, start + 2, 4), start + 6);
            case 'U':
            {
                var scalar = Convert.ToInt32(ReadHexEscape(content, start + 2, 8), 16);
                if (scalar is < 0 or > 0x10FFFF || (scalar >= 0xD800 && scalar <= 0xDFFF))
                {
                    throw new GoTemplateException("invalid Unicode code point");
                }

                return (char.ConvertFromUtf32(scalar), start + 10);
            }

            default:
                if (escape is >= '0' and <= '7')
                {
                    var digits = content.Substring(start + 1, Math.Min(3, content.Length - start - 1));
                    if (digits.Length != 3 || digits.Any(static digit => digit is < '0' or > '7'))
                    {
                        throw new GoTemplateException("invalid octal escape");
                    }

                    return (((char)Convert.ToInt32(digits, 8)).ToString(), start + 4);
                }

                throw new GoTemplateException($"unknown escape '\\{escape}' in {quote} string");
        }
    }

    private static string ReadHexEscape(string content, int start, int length)
    {
        if (start + length > content.Length)
        {
            throw new GoTemplateException("invalid hexadecimal escape");
        }

        var text = content.Substring(start, length);
        if (text.Any(static digit => !char.IsAsciiHexDigit(digit)))
        {
            throw new GoTemplateException("invalid hexadecimal escape");
        }

        var scalar = Convert.ToInt32(text, 16);
        if (scalar > 0x10FFFF || (scalar >= 0xD800 && scalar <= 0xDFFF))
        {
            throw new GoTemplateException("invalid Unicode code point");
        }

        return char.ConvertFromUtf32(scalar);
    }

    private static (object Value, int End) ReadNumber(string content, int start)
    {
        try
        {
            var index = start;
            var negative = false;
            if (content[index] is '+' or '-')
            {
                negative = content[index] == '-';
                index++;
            }

            if (content[index] == '0'
                && index + 1 < content.Length
                && content[index + 1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
            {
                var prefix = char.ToLowerInvariant(content[index + 1]);
                index += 2;
                var digitStart = index;
                while (index < content.Length && (char.IsAsciiHexDigit(content[index]) || content[index] == '_'))
                {
                    index++;
                }

                var digits = content[digitStart..index].Replace("_", string.Empty, StringComparison.Ordinal);
                var numberBase = prefix switch
                {
                    'x' => 16,
                    'o' => 8,
                    _ => 2,
                };
                var parsed = Convert.ToInt64(digits, numberBase);
                return (negative ? -parsed : parsed, index);
            }

            var numberStart = start;
            while (index < content.Length && (char.IsDigit(content[index]) || content[index] == '_'))
            {
                index++;
            }

            var isFloat = false;
            if (index < content.Length && content[index] == '.')
            {
                isFloat = true;
                index++;
                while (index < content.Length && (char.IsDigit(content[index]) || content[index] == '_'))
                {
                    index++;
                }
            }

            if (index < content.Length && content[index] is 'e' or 'E')
            {
                isFloat = true;
                index++;
                if (index < content.Length && content[index] is '+' or '-')
                {
                    index++;
                }

                var exponentStart = index;
                while (index < content.Length && (char.IsDigit(content[index]) || content[index] == '_'))
                {
                    index++;
                }

                if (index == exponentStart)
                {
                    throw new GoTemplateException("invalid number");
                }
            }

            var text = content[numberStart..index].Replace("_", string.Empty, StringComparison.Ordinal);
            if (isFloat)
            {
                return (double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), index);
            }

            return (long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), index);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw new GoTemplateException("invalid number");
        }
    }

    // -----------------------------------------------------------------------
    // Expression nodes
    // -----------------------------------------------------------------------

    private abstract class TemplateExpression
    {
        public abstract object? Evaluate(TemplateState state, object? dot);
    }

    private sealed class TemplateLiteralExpression(object? value) : TemplateExpression
    {
        public override object? Evaluate(TemplateState state, object? dot)
        {
            _ = state;
            _ = dot;
            return value;
        }
    }

    private sealed class TemplateDotExpression : TemplateExpression
    {
        public override object? Evaluate(TemplateState state, object? dot)
        {
            _ = state;
            return dot;
        }
    }

    private sealed class TemplateFieldExpression(List<string> segments) : TemplateExpression
    {
        public List<string> Segments { get; } = segments;

        public override object? Evaluate(TemplateState state, object? dot)
        {
            _ = state;
            var value = dot;
            foreach (var segment in Segments)
            {
                value = FieldAccess(value, segment);
            }

            return value;
        }
    }

    private sealed class TemplateVariableExpression(string name, List<string> segments) : TemplateExpression
    {
        public string Name { get; } = name;

        public List<string> Segments { get; } = segments;

        public override object? Evaluate(TemplateState state, object? dot)
        {
            _ = dot;
            if (!state.TryLookup(Name, out var value))
            {
                throw new GoTemplateException($"undefined variable ${Name}");
            }

            foreach (var segment in Segments)
            {
                value = FieldAccess(value, segment);
            }

            return value;
        }
    }

    private sealed class TemplateParenExpression(TemplatePipeline pipeline) : TemplateExpression
    {
        public override object? Evaluate(TemplateState state, object? dot)
            => pipeline.Evaluate(state, dot);
    }

    private sealed class TemplateChainExpression(TemplateExpression target, string segment) : TemplateExpression
    {
        public override object? Evaluate(TemplateState state, object? dot)
            => FieldAccess(target.Evaluate(state, dot), segment);
    }

    private sealed class TemplateFunctionExpression(string name) : TemplateExpression
    {
        public string Name { get; } = name;

        public override object? Evaluate(TemplateState state, object? dot)
        {
            _ = state;
            _ = dot;
            throw new GoTemplateException($"function {Name} not defined");
        }
    }

    private sealed class TemplateNilExpression : TemplateExpression
    {
        public override object? Evaluate(TemplateState state, object? dot)
        {
            _ = state;
            _ = dot;
            return null;
        }
    }

    private sealed class TemplateCommand(TemplateExpression first, List<TemplateExpression> arguments)
    {
        public TemplateExpression First { get; } = first;

        public List<TemplateExpression> Arguments { get; } = arguments;

        public object? Evaluate(TemplateState state, object? dot, object? final, bool hasFinal)
        {
            if (First is TemplateFunctionExpression function)
            {
                if (function.Name is "and" or "or")
                {
                    return EvaluateAndOr(function.Name, state, dot, final, hasFinal);
                }

                var arguments = new List<object?>(Arguments.Count + 1);
                foreach (var argument in Arguments)
                {
                    arguments.Add(argument.Evaluate(state, dot));
                }

                if (hasFinal)
                {
                    arguments.Add(final);
                }

                CheckArity(function.Name, arguments.Count);
                return Invoke(function.Name, arguments);
            }

            if (First is TemplateNilExpression && Arguments.Count == 0 && !hasFinal)
            {
                throw new GoTemplateException("nil is not a command");
            }

            if (Arguments.Count > 0 || hasFinal)
            {
                throw new GoTemplateException("can't give argument to non-function");
            }

            return First.Evaluate(state, dot);
        }

        private object? EvaluateAndOr(
            string name,
            TemplateState state,
            object? dot,
            object? final,
            bool hasFinal)
        {
            if (Arguments.Count == 0 && !hasFinal)
            {
                throw new GoTemplateException($"wrong number of args for {name}");
            }

            object? last = null;
            foreach (var argument in Arguments)
            {
                last = argument.Evaluate(state, dot);
                if (Truth(last) == (name == "or"))
                {
                    return last;
                }
            }

            return hasFinal ? final : last;
        }
    }

    private sealed class TemplatePipeline(
        List<TemplateCommand> commands,
        List<string> declaredVariables,
        bool isAssignment)
    {
        public List<TemplateCommand> Commands { get; } = commands;

        public List<string> DeclaredVariables { get; } = declaredVariables;

        public bool IsAssignment { get; } = isAssignment;

        public object? Evaluate(TemplateState state, object? dot)
        {
            object? value = null;
            var hasValue = false;
            foreach (var command in Commands)
            {
                value = command.Evaluate(state, dot, value, hasValue);
                hasValue = true;
            }

            return value;
        }
    }

    private static object? FieldAccess(object? target, string field)
    {
        switch (target)
        {
            case null:
                // An invalid (missing) receiver stays invalid; Go's evalField
                // returns the zero Value without an error.
                return null;

            case OverrideRenderContext context:
                return field switch
                {
                    "RequestModel" => context.RequestModel,
                    "Model" => context.Model,
                    "Metadata" => context.Metadata,
                    "RequestHeader" => context.RequestHeader,
                    "PromptCacheKey" => context.PromptCacheKey,
                    "ReasoningEffort" => context.ReasoningEffort,
                    _ => throw new GoTemplateException($"can't evaluate field {field} in render context"),
                };

            case TemplateMap map:
                return map.TryGetValue(field, out var value) ? value : null;

            default:
                throw new GoTemplateException($"can't evaluate field {field}");
        }
    }

    // -----------------------------------------------------------------------
    // Statement nodes
    // -----------------------------------------------------------------------

    private abstract class TemplateNode
    {
        public abstract void Execute(TemplateState state, object? dot, StringBuilder output);
    }

    private sealed class TemplateTextNode(string text) : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            _ = state;
            _ = dot;
            output.Append(text);
        }
    }

    private sealed class TemplateActionNode(TemplatePipeline pipeline) : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            var value = pipeline.Evaluate(state, dot);
            if (pipeline.DeclaredVariables.Count == 0)
            {
                output.Append(FormatValue(value));
                return;
            }

            BindDeclarations(state, pipeline, value);
        }
    }

    private sealed class TemplateIfNode(
        TemplatePipeline pipeline,
        List<TemplateNode> body,
        List<TemplateNode>? elseBody) : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            state.PushScope();
            try
            {
                var value = pipeline.Evaluate(state, dot);
                BindDeclarations(state, pipeline, value);
                if (Truth(value))
                {
                    ExecuteNodes(body, state, dot, output);
                }
                else if (elseBody is not null)
                {
                    ExecuteNodes(elseBody, state, dot, output);
                }
            }
            finally
            {
                state.PopScope();
            }
        }
    }

    private sealed class TemplateWithNode(
        TemplatePipeline pipeline,
        List<TemplateNode> body,
        List<TemplateNode>? elseBody) : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            state.PushScope();
            try
            {
                var value = pipeline.Evaluate(state, dot);
                BindDeclarations(state, pipeline, value);
                if (Truth(value))
                {
                    ExecuteNodes(body, state, value, output);
                }
                else if (elseBody is not null)
                {
                    ExecuteNodes(elseBody, state, dot, output);
                }
            }
            finally
            {
                state.PopScope();
            }
        }
    }

    private sealed class TemplateRangeNode(
        TemplatePipeline pipeline,
        List<TemplateNode> body,
        List<TemplateNode>? elseBody) : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            state.PushScope();
            try
            {
                var value = pipeline.Evaluate(state, dot);
                var items = EnumerateRange(value);
                if (items.Count == 0)
                {
                    if (elseBody is not null)
                    {
                        ExecuteNodes(elseBody, state, dot, output);
                    }

                    return;
                }

                foreach (var (key, element) in items)
                {
                    BindRangeVariables(state, pipeline, key, element);
                    try
                    {
                        ExecuteNodes(body, state, element, output);
                    }
                    catch (TemplateContinueSignal)
                    {
                        continue;
                    }
                    catch (TemplateBreakSignal)
                    {
                        break;
                    }
                }
            }
            finally
            {
                state.PopScope();
            }
        }

        private static void BindRangeVariables(
            TemplateState state,
            TemplatePipeline pipeline,
            object? key,
            object? element)
        {
            var names = pipeline.DeclaredVariables;
            switch (names.Count)
            {
                case 0:
                    return;
                case 1:
                    Bind(state, pipeline.IsAssignment, names[0], element);
                    break;
                default:
                    Bind(state, pipeline.IsAssignment, names[0], key);
                    Bind(state, pipeline.IsAssignment, names[1], element);
                    break;
            }
        }
    }

    private sealed class TemplateBreakNode : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            _ = state;
            _ = dot;
            _ = output;
            throw new TemplateBreakSignal();
        }
    }

    private sealed class TemplateContinueNode : TemplateNode
    {
        public override void Execute(TemplateState state, object? dot, StringBuilder output)
        {
            _ = state;
            _ = dot;
            _ = output;
            throw new TemplateContinueSignal();
        }
    }

    private static void ExecuteNodes(
        List<TemplateNode> nodes,
        TemplateState state,
        object? dot,
        StringBuilder output)
    {
        foreach (var node in nodes)
        {
            node.Execute(state, dot, output);
        }
    }

    private static void BindDeclarations(TemplateState state, TemplatePipeline pipeline, object? value)
    {
        foreach (var name in pipeline.DeclaredVariables)
        {
            Bind(state, pipeline.IsAssignment, name, value);
        }
    }

    private static void Bind(TemplateState state, bool isAssignment, string name, object? value)
    {
        if (isAssignment)
        {
            state.Assign(name, value);
        }
        else
        {
            state.Declare(name, value);
        }
    }

    private static List<KeyValuePair<object?, object?>> EnumerateRange(object? value)
    {
        var items = new List<KeyValuePair<object?, object?>>();
        switch (value)
        {
            case TemplateMap map:
                foreach (var pair in map.SortedMembers())
                {
                    items.Add(new KeyValuePair<object?, object?>(pair.Key, pair.Value));
                }

                break;

            case List<object?> list:
                for (var index = 0; index < list.Count; index++)
                {
                    items.Add(new KeyValuePair<object?, object?>(index, list[index]));
                }

                break;

            case long count:
                if (count > int.MaxValue)
                {
                    throw new GoTemplateException("range over too large integer");
                }

                for (var index = 0L; index < count; index++)
                {
                    items.Add(new KeyValuePair<object?, object?>(index, index));
                }

                break;

            default:
                throw new GoTemplateException("range can't iterate over value");
        }

        return items;
    }

    // -----------------------------------------------------------------------
    // Parser
    // -----------------------------------------------------------------------

    private sealed class TemplateParser(List<TemplateElement> elements)
    {
        // Bounds recursion on pathological settings values: the reference
        // parser recurses on native stacks, this port must not overflow.
        private const int MaxControlDepth = 1000;

        private int _index;
        private int _rangeDepth;
        private int _controlDepth;

        public List<TemplateNode> ParseRoot()
        {
            // The root list stops at end of input; "else"/"end" are only here so
            // a stray closing keyword is reported instead of parsed as a command.
            var (nodes, stop, _) = ParseList(requireStop: false, "else", "end");
            if (stop is not null)
            {
                throw new GoTemplateException($"unexpected {stop}");
            }

            return nodes;
        }

        private (
            List<TemplateNode> Nodes,
            string? StopKeyword,
            List<TemplateToken>? StopTokens) ParseList(bool requireStop, params string[] stops)
        {
            var nodes = new List<TemplateNode>();
            while (_index < elements.Count)
            {
                switch (elements[_index])
                {
                    case TemplateTextElement text:
                        _index++;
                        nodes.Add(new TemplateTextNode(text.Text));
                        continue;

                    case TemplateActionElement action:
                    {
                        var tokens = LexAction(action.Content);
                        if (tokens.Count == 0)
                        {
                            throw new GoTemplateException("empty action");
                        }

                        var keyword = KeywordOf(tokens);
                        if (keyword is "else" or "end")
                        {
                            if (Array.IndexOf(stops, keyword) < 0)
                            {
                                throw new GoTemplateException($"unexpected {keyword}");
                            }

                            return (nodes, keyword, tokens);
                        }

                        _index++;
                        nodes.Add(ParseStatement(keyword, tokens));
                        continue;
                    }
                }
            }

            if (requireStop)
            {
                throw new GoTemplateException("unexpected end of template");
            }

            return (nodes, null, null);
        }

        private TemplateNode ParseStatement(string? keyword, List<TemplateToken> tokens)
        {
            switch (keyword)
            {
                case "if":
                    return ParseIf(tokens, 1);

                case "with":
                    return ParseWith(tokens, 1);

                case "range":
                    return ParseRange(tokens, 1);

                case "break":
                    EnsureRange();
                    EnsureKeywordOnly(tokens);
                    return new TemplateBreakNode();

                case "continue":
                    EnsureRange();
                    EnsureKeywordOnly(tokens);
                    return new TemplateContinueNode();

                case "define":
                case "block":
                case "template":
                    throw new GoTemplateException($"unsupported action {keyword}");

                default:
                {
                    var cursor = new TemplateTokenCursor(tokens, 0);
                    var pipeline = ParsePipeline(cursor, allowDeclarations: true, allowTwoDeclarations: false);
                    cursor.ExpectEnd();
                    return new TemplateActionNode(pipeline);
                }
            }
        }

        private void EnsureRange()
        {
            if (_rangeDepth == 0)
            {
                throw new GoTemplateException("break/continue outside range");
            }
        }

        private static void EnsureKeywordOnly(List<TemplateToken> tokens)
        {
            if (tokens.Count != 1)
            {
                throw new GoTemplateException("unexpected arguments to break/continue");
            }
        }

        private void EnterControl()
        {
            _controlDepth++;
            if (_controlDepth > MaxControlDepth)
            {
                throw new GoTemplateException("template nesting too deep");
            }
        }

        private void ExitControl() => _controlDepth--;

        private TemplateIfNode ParseIf(List<TemplateToken> tokens, int start)
        {
            EnterControl();
            try
            {
                var pipeline = ParseControlPipeline(tokens, start, allowTwoDeclarations: false);
                var (body, stop, stopTokens) = ParseList(requireStop: true, "else", "end");
                if (stop is null || stopTokens is null)
                {
                    throw new GoTemplateException("unexpected end of template");
                }

                if (stop == "end")
                {
                    _index++;
                    return new TemplateIfNode(pipeline, body, null);
                }

                _index++;
                return new TemplateIfNode(pipeline, body, ParseElseContinuation(stopTokens));
            }
            finally
            {
                ExitControl();
            }
        }

        private TemplateWithNode ParseWith(List<TemplateToken> tokens, int start)
        {
            EnterControl();
            try
            {
                var pipeline = ParseControlPipeline(tokens, start, allowTwoDeclarations: false);
                var (body, stop, stopTokens) = ParseList(requireStop: true, "else", "end");
                if (stop is null || stopTokens is null)
                {
                    throw new GoTemplateException("unexpected end of template");
                }

                if (stop == "end")
                {
                    _index++;
                    return new TemplateWithNode(pipeline, body, null);
                }

                _index++;
                return new TemplateWithNode(pipeline, body, ParseElseContinuation(stopTokens));
            }
            finally
            {
                ExitControl();
            }
        }

        private TemplateRangeNode ParseRange(List<TemplateToken> tokens, int start)
        {
            EnterControl();
            var pipeline = ParseControlPipeline(tokens, start, allowTwoDeclarations: true);
            _rangeDepth++;
            try
            {
                var (body, stop, stopTokens) = ParseList(requireStop: true, "else", "end");
                if (stop is null)
                {
                    throw new GoTemplateException("unexpected end of template");
                }

                if (stop == "end")
                {
                    _index++;
                    return new TemplateRangeNode(pipeline, body, null);
                }

                if (stopTokens is null || stopTokens.Count != 1)
                {
                    throw new GoTemplateException("unexpected else");
                }

                _index++;
                var (elseBody, stop2, _) = ParseList(requireStop: true, "end");
                if (stop2 != "end")
                {
                    throw new GoTemplateException("unexpected else");
                }

                _index++;
                return new TemplateRangeNode(pipeline, body, elseBody);
            }
            finally
            {
                _rangeDepth--;
                ExitControl();
            }
        }

        private List<TemplateNode> ParseElseContinuation(List<TemplateToken> tokens)
        {
            if (tokens.Count == 1)
            {
                var (body, stop, _) = ParseList(requireStop: true, "end");
                if (stop != "end")
                {
                    throw new GoTemplateException("unexpected else");
                }

                _index++;
                return body;
            }

            if (tokens[1] is { Kind: TemplateTokenKind.Identifier, Text: "if" })
            {
                return [ParseIf(tokens, 2)];
            }

            if (tokens[1] is { Kind: TemplateTokenKind.Identifier, Text: "with" })
            {
                return [ParseWith(tokens, 2)];
            }

            throw new GoTemplateException("unexpected else");
        }

        private static TemplatePipeline ParseControlPipeline(
            List<TemplateToken> tokens,
            int start,
            bool allowTwoDeclarations)
        {
            var cursor = new TemplateTokenCursor(tokens, start);
            var pipeline = ParsePipeline(cursor, allowDeclarations: true, allowTwoDeclarations);
            cursor.ExpectEnd();
            return pipeline;
        }

        private static TemplatePipeline ParsePipeline(
            TemplateTokenCursor cursor,
            bool allowDeclarations,
            bool allowTwoDeclarations)
        {
            var declared = new List<string>();
            var isAssignment = false;

            if (allowDeclarations && cursor.Peek() is { Kind: TemplateTokenKind.Variable } first)
            {
                if (cursor.PeekAt(1) is { Kind: TemplateTokenKind.Declare or TemplateTokenKind.Assign } declaration)
                {
                    declared.Add(first.Text);
                    isAssignment = declaration.Kind == TemplateTokenKind.Assign;
                    cursor.Advance(2);
                }
                else if (allowTwoDeclarations
                    && cursor.PeekAt(1) is { Kind: TemplateTokenKind.Comma }
                    && cursor.PeekAt(2) is { Kind: TemplateTokenKind.Variable } second
                    && cursor.PeekAt(3) is { Kind: TemplateTokenKind.Declare or TemplateTokenKind.Assign } pairDeclaration)
                {
                    declared.Add(first.Text);
                    declared.Add(second.Text);
                    isAssignment = pairDeclaration.Kind == TemplateTokenKind.Assign;
                    cursor.Advance(4);
                }
            }

            var commands = new List<TemplateCommand> { ParseCommand(cursor) };
            while (cursor.Peek() is { Kind: TemplateTokenKind.Pipe })
            {
                cursor.Advance(1);
                commands.Add(ParseCommand(cursor));
            }

            return new TemplatePipeline(commands, declared, isAssignment);
        }

        private static TemplateCommand ParseCommand(TemplateTokenCursor cursor)
        {
            var first = ParseOperand(cursor);
            var arguments = new List<TemplateExpression>();
            while (cursor.Peek() is { } token
                && token.Kind is not (TemplateTokenKind.Pipe or TemplateTokenKind.RightParen))
            {
                arguments.Add(ParseOperand(cursor));
            }

            return new TemplateCommand(first, arguments);
        }

        private static TemplateExpression ParseOperand(TemplateTokenCursor cursor)
        {
            var term = ParseTerm(cursor);
            while (cursor.Peek() is { Kind: TemplateTokenKind.Field } field
                && field.Start == cursor.LastConsumedEnd)
            {
                cursor.Advance(1);
                term = AppendChain(term, field.Text);
            }

            return term;
        }

        private static TemplateExpression AppendChain(TemplateExpression term, string segment)
            => term switch
            {
                TemplateFieldExpression field => new TemplateFieldExpression(AddSegment(field, segment)),
                TemplateVariableExpression variable => new TemplateVariableExpression(
                    variable.Name,
                    AddSegment(variable, segment)),
                TemplateParenExpression => new TemplateChainExpression(term, segment),
                _ => throw new GoTemplateException("unexpected . after term"),
            };

        private static List<string> AddSegment(TemplateFieldExpression field, string segment)
        {
            var segments = new List<string>(field.Segments) { segment };
            return segments;
        }

        private static List<string> AddSegment(TemplateVariableExpression variable, string segment)
        {
            var segments = new List<string>(variable.Segments) { segment };
            return segments;
        }

        private static TemplateExpression ParseTerm(TemplateTokenCursor cursor)
        {
            var token = cursor.Peek() ?? throw new GoTemplateException("unexpected end of action");
            switch (token.Kind)
            {
                case TemplateTokenKind.LeftParen:
                {
                    cursor.Advance(1);
                    var pipeline = ParsePipeline(cursor, allowDeclarations: false, allowTwoDeclarations: false);
                    if (cursor.Peek() is not { Kind: TemplateTokenKind.RightParen })
                    {
                        throw new GoTemplateException("unclosed parenthesis");
                    }

                    cursor.Advance(1);
                    return new TemplateParenExpression(pipeline);
                }

                case TemplateTokenKind.Dot:
                    cursor.Advance(1);
                    return new TemplateDotExpression();

                case TemplateTokenKind.Field:
                    cursor.Advance(1);
                    return new TemplateFieldExpression([token.Text]);

                case TemplateTokenKind.Variable:
                    cursor.Advance(1);
                    return new TemplateVariableExpression(token.Text, []);

                case TemplateTokenKind.String:
                case TemplateTokenKind.Number:
                case TemplateTokenKind.Character:
                    cursor.Advance(1);
                    return new TemplateLiteralExpression(token.Value);

                case TemplateTokenKind.Identifier:
                    cursor.Advance(1);
                    return token.Text switch
                    {
                        "true" => new TemplateLiteralExpression(true),
                        "false" => new TemplateLiteralExpression(false),
                        "nil" => new TemplateNilExpression(),
                        _ when FunctionNames.Contains(token.Text) => new TemplateFunctionExpression(token.Text),
                        _ => throw new GoTemplateException($"function {token.Text} not defined"),
                    };

                default:
                    throw new GoTemplateException("unexpected token in action");
            }
        }

        private static string? KeywordOf(List<TemplateToken> tokens)
            => tokens[0] is { Kind: TemplateTokenKind.Identifier } identifier
                && Keywords.Contains(identifier.Text)
                    ? identifier.Text
                    : null;
    }

    private sealed class TemplateTokenCursor(List<TemplateToken> tokens, int index)
    {
        private int _index = index;

        public int LastConsumedEnd { get; private set; } = index > 0 ? tokens[index - 1].End : 0;

        public TemplateToken? Peek() => _index < tokens.Count ? tokens[_index] : null;

        public TemplateToken? PeekAt(int offset)
            => _index + offset < tokens.Count ? tokens[_index + offset] : null;

        public void Advance(int count)
        {
            for (var step = 0; step < count; step++)
            {
                if (_index >= tokens.Count)
                {
                    throw new GoTemplateException("unexpected end of action");
                }

                LastConsumedEnd = tokens[_index].End;
                _index++;
            }
        }

        public void ExpectEnd()
        {
            if (_index < tokens.Count)
            {
                throw new GoTemplateException("unexpected token in action");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Evaluation state
    // -----------------------------------------------------------------------

    private sealed class TemplateState(object? root)
    {
        private readonly List<Dictionary<string, object?>> _scopes =
        [
            new Dictionary<string, object?>(StringComparer.Ordinal) { [string.Empty] = root },
        ];

        public void PushScope() => _scopes.Add(new Dictionary<string, object?>(StringComparer.Ordinal));

        public void PopScope()
        {
            if (_scopes.Count <= 1)
            {
                throw new GoTemplateException("pop root scope");
            }

            _scopes.RemoveAt(_scopes.Count - 1);
        }

        public void Declare(string name, object? value) => _scopes[^1][name] = value;

        public void Assign(string name, object? value)
        {
            for (var index = _scopes.Count - 1; index >= 0; index--)
            {
                if (_scopes[index].ContainsKey(name))
                {
                    _scopes[index][name] = value;
                    return;
                }
            }

            throw new GoTemplateException($"undefined variable ${name}");
        }

        public bool TryLookup(string name, out object? value)
        {
            for (var index = _scopes.Count - 1; index >= 0; index--)
            {
                if (_scopes[index].TryGetValue(name, out value))
                {
                    return true;
                }
            }

            value = null;
            return false;
        }
    }

    private sealed class TemplateBreakSignal : Exception;

    private sealed class TemplateContinueSignal : Exception;

    private sealed class GoTemplateException(string message) : Exception(message);

    // -----------------------------------------------------------------------
    // Functions and truthiness
    // -----------------------------------------------------------------------

    private enum TemplateValueKind
    {
        Invalid,
        Bool,
        Int,
        Float,
        String,
        Slice,
        Map,
        Struct,
    }

    private enum TemplateRelationalOperator
    {
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    private static TemplateValueKind KindOf(object? value) => value switch
    {
        null => TemplateValueKind.Invalid,
        bool => TemplateValueKind.Bool,
        long => TemplateValueKind.Int,
        double => TemplateValueKind.Float,
        string => TemplateValueKind.String,
        List<object?> => TemplateValueKind.Slice,
        TemplateMap => TemplateValueKind.Map,
        _ => TemplateValueKind.Struct,
    };

    private static bool Truth(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        long integer => integer != 0,
        double number => number != 0,
        List<object?> list => list.Count > 0,
        TemplateMap map => map.Count > 0,

        // Struct values are always true (Go's isTrue).
        _ => true,
    };

    private static void CheckArity(string name, int count)
    {
        var (minimum, maximum) = name switch
        {
            "toJSON" => (1, 1),
            "eq" => (1, int.MaxValue),
            "ne" or "lt" or "le" or "gt" or "ge" => (2, 2),
            "not" or "len" => (1, 1),
            "index" => (1, int.MaxValue),
            "print" or "println" => (0, int.MaxValue),
            "printf" => (1, int.MaxValue),
            _ => throw new GoTemplateException($"function {name} not defined"),
        };

        if (count < minimum || count > maximum)
        {
            throw new GoTemplateException($"wrong number of args for {name}");
        }
    }

    private static object? Invoke(string name, IReadOnlyList<object?> arguments) => name switch
    {
        "toJSON" => ToJson(arguments[0]),
        "eq" => Equal(arguments),
        "ne" => !Equal(arguments),
        "lt" => Relational(arguments, TemplateRelationalOperator.Less),
        "le" => Relational(arguments, TemplateRelationalOperator.LessOrEqual),
        "gt" => Relational(arguments, TemplateRelationalOperator.Greater),
        "ge" => Relational(arguments, TemplateRelationalOperator.GreaterOrEqual),
        "not" => !Truth(arguments[0]),
        "len" => Length(arguments[0]),
        "index" => Index(arguments),
        "print" => Sprint(arguments),
        "println" => Sprintln(arguments),
        "printf" => SprintfArgument(arguments),
        _ => throw new GoTemplateException($"function {name} not defined"),
    };

    private static bool Equal(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 1)
        {
            throw new GoTemplateException("missing argument for comparison");
        }

        var first = arguments[0];
        var firstKind = KindOf(first);
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var argumentKind = KindOf(argument);
            bool truth;
            if (firstKind != argumentKind)
            {
                if (firstKind != TemplateValueKind.Invalid && argumentKind != TemplateValueKind.Invalid)
                {
                    throw new GoTemplateException("incompatible types for comparison");
                }

                truth = false;
            }
            else
            {
                truth = firstKind switch
                {
                    TemplateValueKind.Invalid => true,
                    TemplateValueKind.Bool => (bool)first! == (bool)argument!,
                    TemplateValueKind.Int => (long)first! == (long)argument!,
                    TemplateValueKind.Float => (double)first! == (double)argument!,
                    TemplateValueKind.String => string.Equals((string)first!, (string)argument!, StringComparison.Ordinal),
                    TemplateValueKind.Map => CompareMaps((TemplateMap)first!, (TemplateMap)argument!),
                    _ => throw new GoTemplateException("non-comparable types"),
                };
            }

            if (truth)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CompareMaps(TemplateMap left, TemplateMap right)
    {
        if (!left.IsNil && !right.IsNil)
        {
            throw new GoTemplateException("non-comparable type");
        }

        return left.IsNil == right.IsNil;
    }

    private static bool Relational(
        IReadOnlyList<object?> arguments,
        TemplateRelationalOperator comparisonOperator)
    {
        var left = arguments[0];
        var right = arguments[1];
        var leftKind = KindOf(left);
        if (leftKind != KindOf(right))
        {
            throw new GoTemplateException(
                leftKind == TemplateValueKind.Invalid || KindOf(right) == TemplateValueKind.Invalid
                    ? "invalid type for comparison"
                    : "incompatible types for comparison");
        }

        return leftKind switch
        {
            TemplateValueKind.Int => CompareLongs((long)left!, (long)right!, comparisonOperator),
            TemplateValueKind.Float => CompareDoubles((double)left!, (double)right!, comparisonOperator),
            TemplateValueKind.String => CompareStrings((string)left!, (string)right!, comparisonOperator),
            _ => throw new GoTemplateException("invalid type for comparison"),
        };
    }

    private static bool CompareLongs(long left, long right, TemplateRelationalOperator comparisonOperator)
        => comparisonOperator switch
        {
            TemplateRelationalOperator.Less => left < right,
            TemplateRelationalOperator.LessOrEqual => left <= right,
            TemplateRelationalOperator.Greater => left > right,
            _ => left >= right,
        };

    private static bool CompareDoubles(double left, double right, TemplateRelationalOperator comparisonOperator)
        => comparisonOperator switch
        {
            TemplateRelationalOperator.Less => left < right,
            TemplateRelationalOperator.LessOrEqual => left <= right,
            TemplateRelationalOperator.Greater => left > right,
            _ => left >= right,
        };

    private static bool CompareStrings(string left, string right, TemplateRelationalOperator comparisonOperator)
    {
        var comparison = string.CompareOrdinal(left, right);
        return comparisonOperator switch
        {
            TemplateRelationalOperator.Less => comparison < 0,
            TemplateRelationalOperator.LessOrEqual => comparison <= 0,
            TemplateRelationalOperator.Greater => comparison > 0,
            _ => comparison >= 0,
        };
    }

    private static long Length(object? value) => value switch
    {
        string text => Encoding.UTF8.GetByteCount(text),
        List<object?> list => list.Count,
        TemplateMap map => map.Count,
        null => throw new GoTemplateException("len of nil pointer"),
        _ => throw new GoTemplateException("len of type"),
    };

    private static object? Index(IReadOnlyList<object?> arguments)
    {
        object? item = arguments[0];
        for (var index = 1; index < arguments.Count; index++)
        {
            switch (item)
            {
                case null:
                    throw new GoTemplateException("index of untyped nil");

                case string text:
                {
                    var position = RequireInteger(arguments[index]);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    if (position < 0 || position >= bytes.Length)
                    {
                        throw new GoTemplateException("index out of range");
                    }

                    item = (long)bytes[(int)position];
                    break;
                }

                case List<object?> list:
                {
                    var position = RequireInteger(arguments[index]);
                    if (position < 0 || position >= list.Count)
                    {
                        throw new GoTemplateException("index out of range");
                    }

                    item = list[(int)position];
                    break;
                }

                case TemplateMap map:
                {
                    if (arguments[index] is not string key)
                    {
                        throw new GoTemplateException("map index must be a string");
                    }

                    item = map.TryGetValue(key, out var found) ? found : string.Empty;
                    break;
                }

                default:
                    throw new GoTemplateException("can't index item");
            }
        }

        return item;
    }

    private static long RequireInteger(object? value)
        => value is long integer
            ? integer
            : throw new GoTemplateException("index must be an integer");

    // -----------------------------------------------------------------------
    // Value formatting (Go fmt)
    // -----------------------------------------------------------------------

    private static string FormatValue(object? value) => value is null ? "<no value>" : FormatV(value);

    private static string FormatV(object? value) => value switch
    {
        null => "<nil>",
        string text => text,
        bool boolean => boolean ? "true" : "false",
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double number => GoNumbers.FormatG(number),
        List<object?> list => "[" + string.Join(" ", list.Select(FormatV)) + "]",
        TemplateMap map => FormatTemplateMap(map),
        OverrideRenderContext context => "{" + string.Join(
            " ",
            context.RequestModel,
            context.Model,
            FormatV(context.Metadata),
            FormatV(context.RequestHeader),
            context.PromptCacheKey,
            context.ReasoningEffort) + "}",
        _ => throw new GoTemplateException("cannot format value"),
    };

    private static string FormatTemplateMap(TemplateMap map)
    {
        if (map.IsNil)
        {
            return "map[]";
        }

        return "map[" + string.Join(
            " ",
            map.SortedMembers().Select(static pair => pair.Key + ":" + FormatV(pair.Value))) + "]";
    }

    private static string FormatTypeName(object? value) => value switch
    {
        null => "<nil>",
        bool => "bool",
        long => "int",
        double => "float64",
        string => "string",
        List<object?> => "[]interface {}",
        TemplateMap => "map[string]string",
        _ => "struct",
    };

    // -----------------------------------------------------------------------
    // json.Marshal (toJSON)
    // -----------------------------------------------------------------------

    private static string ToJson(object? value)
    {
        var builder = new StringBuilder();
        WriteJson(value, builder);
        return builder.ToString();
    }

    private static void WriteJson(object? value, StringBuilder builder)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;

            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                break;

            case long integer:
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;

            case double number:
                builder.Append(GoNumbers.FormatJson(number));
                break;

            case string text:
                GoJson.WriteQuoted(builder, text);
                break;

            case List<object?> list:
                builder.Append('[');
                for (var index = 0; index < list.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    WriteJson(list[index], builder);
                }

                builder.Append(']');
                break;

            case TemplateMap map:
                if (map.IsNil)
                {
                    builder.Append("null");
                    break;
                }

                builder.Append('{');
                var first = true;
                foreach (var pair in map.SortedMembers())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    GoJson.WriteQuoted(builder, pair.Key);
                    builder.Append(':');
                    GoJson.WriteQuoted(builder, pair.Value);
                }

                builder.Append('}');
                break;

            case OverrideRenderContext context:
                builder.Append("{\"request_model\":");
                GoJson.WriteQuoted(builder, context.RequestModel);
                builder.Append(",\"model\":");
                GoJson.WriteQuoted(builder, context.Model);
                builder.Append(",\"metadata\":");
                WriteJson(context.Metadata, builder);
                builder.Append(",\"request_header\":");
                WriteJson(context.RequestHeader, builder);
                builder.Append(",\"prompt_cache_key\":");
                GoJson.WriteQuoted(builder, context.PromptCacheKey);
                builder.Append(",\"reasoning_effort\":");
                GoJson.WriteQuoted(builder, context.ReasoningEffort);
                builder.Append('}');
                break;

            default:
                throw new GoTemplateException("cannot marshal value");
        }
    }

    // -----------------------------------------------------------------------
    // print / println / printf
    // -----------------------------------------------------------------------

    private static string Sprint(IReadOnlyList<object?> arguments)
    {
        var builder = new StringBuilder();
        object? previous = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var current = arguments[index];
            if (index > 0 && current is not string && previous is not string)
            {
                builder.Append(' ');
            }

            builder.Append(FormatV(current));
            previous = current;
        }

        return builder.ToString();
    }

    private static string Sprintln(IReadOnlyList<object?> arguments)
        => string.Join(" ", arguments.Select(FormatV)) + "\n";

    private static string SprintfArgument(IReadOnlyList<object?> arguments)
    {
        if (arguments[0] is not string format)
        {
            throw new GoTemplateException("printf format must be a string");
        }

        return Sprintf(format, arguments);
    }

    private static string Sprintf(string format, IReadOnlyList<object?> arguments)
    {
        var builder = new StringBuilder();
        var argumentIndex = 1;
        var index = 0;

        while (index < format.Length)
        {
            var current = format[index];
            if (current != '%')
            {
                builder.Append(current);
                index++;
                continue;
            }

            index++;
            if (index >= format.Length)
            {
                builder.Append("%!(NOVERB)");
                break;
            }

            if (format[index] == '%')
            {
                builder.Append('%');
                index++;
                continue;
            }

            var flagStart = index;
            while (index < format.Length && IsFormatFlag(format[index]))
            {
                index++;
            }

            var flags = format[flagStart..index];

            var width = -1;
            if (index < format.Length && format[index] == '*')
            {
                if (argumentIndex >= arguments.Count)
                {
                    builder.Append("%!(BADWIDTH)");
                    index++;
                    continue;
                }

                width = (int)RequireInteger(arguments[argumentIndex]);
                argumentIndex++;
                index++;
            }
            else
            {
                var widthStart = index;
                while (index < format.Length && char.IsAsciiDigit(format[index]))
                {
                    index++;
                }

                if (index > widthStart)
                {
                    width = int.Parse(format[widthStart..index], NumberStyles.None, CultureInfo.InvariantCulture);
                }
            }

            var precision = -1;
            if (index < format.Length && format[index] == '.')
            {
                index++;
                if (index < format.Length && format[index] == '*')
                {
                    if (argumentIndex < arguments.Count)
                    {
                        precision = (int)RequireInteger(arguments[argumentIndex]);
                        argumentIndex++;
                    }

                    index++;
                }
                else
                {
                    var precisionStart = index;
                    while (index < format.Length && char.IsAsciiDigit(format[index]))
                    {
                        index++;
                    }

                    precision = index == precisionStart
                        ? 0
                        : int.Parse(format[precisionStart..index], NumberStyles.None, CultureInfo.InvariantCulture);
                }
            }

            if (index >= format.Length)
            {
                builder.Append("%!(NOVERB)");
                break;
            }

            var verb = format[index];
            index++;

            if (argumentIndex >= arguments.Count)
            {
                builder.Append("%!").Append(verb).Append("(MISSING)");
                continue;
            }

            var argument = arguments[argumentIndex];
            argumentIndex++;
            FormatVerb(verb, argument, flags, width, precision, builder);
        }

        if (argumentIndex < arguments.Count)
        {
            builder.Append("%!(EXTRA ");
            for (var extra = argumentIndex; extra < arguments.Count; extra++)
            {
                if (extra > argumentIndex)
                {
                    builder.Append(", ");
                }

                builder.Append(FormatTypeName(arguments[extra]))
                    .Append('=')
                    .Append(FormatV(arguments[extra]));
            }

            builder.Append(')');
        }

        return builder.ToString();
    }

    private static bool IsFormatFlag(char value) => value is '-' or '+' or '#' or ' ' or '0';

    private static void FormatVerb(
        char verb,
        object? argument,
        string flags,
        int width,
        int precision,
        StringBuilder builder)
    {
        switch (verb)
        {
            case 'v':
            {
                var text = FormatV(argument);
                if (precision >= 0 && argument is string)
                {
                    text = Truncate(text, precision);
                }

                builder.Append(Pad(text, flags, width));
                return;
            }

            case 's':
                if (argument is string value)
                {
                    if (precision >= 0)
                    {
                        value = Truncate(value, precision);
                    }

                    builder.Append(Pad(value, flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 'q':
                if (argument is string quoted)
                {
                    builder.Append(Pad(GoQuote(quoted), flags, width));
                    return;
                }

                if (argument is long scalar)
                {
                    builder.Append(Pad(GoQuoteRune(scalar), flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 'd':
                if (argument is long decimalValue)
                {
                    AppendSignedNumber(
                        decimalValue.ToString(CultureInfo.InvariantCulture),
                        flags,
                        width,
                        precision,
                        builder);
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 't':
                if (argument is bool boolean)
                {
                    builder.Append(Pad(boolean ? "true" : "false", flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 'f':
            case 'e':
            case 'g':
                if (argument is double number)
                {
                    var formatted = verb switch
                    {
                        'f' => number.ToString(
                            "F" + (precision >= 0 ? precision : 6).ToString(CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture),
                        'e' => FormatExponent(number, precision >= 0 ? precision : 6, upper: false),
                        _ => FormatGeneral(number, precision),
                    };
                    if (flags.Contains('#'))
                    {
                        // %#g keeps trailing zeros; not emulated (difference C040).
                    }

                    builder.Append(ApplyNumericPadding(formatted, flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 'x':
            case 'X':
            {
                var upper = verb == 'X';
                if (argument is long integer)
                {
                    var digits = Convert.ToString(integer, 16);
                    digits = upper ? digits.ToUpperInvariant() : digits;
                    if (flags.Contains('#'))
                    {
                        digits = (upper ? "0X" : "0x") + digits;
                    }

                    builder.Append(ApplyNumericPadding(digits, flags, width));
                    return;
                }

                if (argument is string text)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var digits = Convert.ToHexString(bytes);
                    if (!upper)
                    {
                        digits = digits.ToLowerInvariant();
                    }

                    builder.Append(Pad(digits, flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;
            }

            case 'o':
                if (argument is long octal)
                {
                    var digits = Convert.ToString(octal, 8);
                    if (flags.Contains('#'))
                    {
                        digits = "0" + digits;
                    }

                    builder.Append(ApplyNumericPadding(digits, flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 'b':
                if (argument is long binary)
                {
                    var digits = Convert.ToString(binary, 2);
                    if (flags.Contains('#'))
                    {
                        digits = "0b" + digits;
                    }

                    builder.Append(ApplyNumericPadding(digits, flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            case 'c':
                if (argument is long character && character is >= 0 and <= 0x10FFFF)
                {
                    builder.Append(Pad(char.ConvertFromUtf32((int)character), flags, width));
                    return;
                }

                builder.Append(BadVerb(verb, argument));
                return;

            default:
                builder.Append(BadVerb(verb, argument));
                return;
        }
    }

    private static string BadVerb(char verb, object? argument)
        => argument is null
            ? $"%!{verb}(<nil>)"
            : $"%!{verb}({FormatTypeName(argument)}={FormatV(argument)})";

    private static string Pad(string text, string flags, int width)
    {
        if (width <= 0 || text.Length >= width)
        {
            return text;
        }

        var padding = width - text.Length;
        if (flags.Contains('-'))
        {
            return text + new string(' ', padding);
        }

        var padCharacter = flags.Contains('0') ? '0' : ' ';
        return new string(padCharacter, padding) + text;
    }

    private static string ApplyNumericPadding(string text, string flags, int width)
    {
        if (width <= 0 || text.Length >= width)
        {
            return text;
        }

        var padding = width - text.Length;
        if (flags.Contains('-'))
        {
            return text + new string(' ', padding);
        }

        if (flags.Contains('0'))
        {
            var sign = text.Length > 0 && (text[0] == '-' || text[0] == '+') ? text[..1] : string.Empty;
            var body = sign.Length > 0 ? text[1..] : text;
            return sign + new string('0', padding) + body;
        }

        return new string(' ', padding) + text;
    }

    private static void AppendSignedNumber(
        string text,
        string flags,
        int width,
        int precision,
        StringBuilder builder)
    {
        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        if (precision > body.Length)
        {
            body = new string('0', precision - body.Length) + body;
        }

        var sign = negative
            ? "-"
            : flags.Contains('+')
                ? "+"
                : flags.Contains(' ')
                    ? " "
                    : string.Empty;
        var combined = sign + body;
        if (width > combined.Length)
        {
            combined = flags.Contains('-')
                ? combined + new string(' ', width - combined.Length)
                : flags.Contains('0')
                    ? sign + new string('0', width - combined.Length) + body
                    : new string(' ', width - combined.Length) + combined;
        }

        builder.Append(combined);
    }

    private static string Truncate(string text, int precision)
    {
        if (precision < 0)
        {
            return text;
        }

        var runes = text.EnumerateRunes().ToArray();
        return runes.Length <= precision ? text : string.Concat(runes.Take(precision).Select(static rune => rune.ToString()));
    }

    private static string FormatExponent(double value, int precision, bool upper)
    {
        var text = value.ToString("E" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var exponentIndex = text.IndexOf('E', StringComparison.Ordinal);
        if (exponentIndex < 0)
        {
            return text;
        }

        var mantissa = text[..exponentIndex];
        var exponent = int.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var sign = exponent < 0 ? "-" : "+";
        var digits = Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        var marker = upper ? 'E' : 'e';
        return mantissa + marker + sign + digits;
    }

    private static string FormatGeneral(double value, int precision)
        => precision < 0
            ? GoNumbers.FormatG(value)
            : value.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Port of <c>strconv.Quote</c> (Go's <c>%q</c> verb).</summary>
    internal static string GoQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                default:
                    AppendQuotedRune(builder, rune);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendQuotedRune(StringBuilder builder, Rune rune)
    {
        if (rune.Value < 0x20 || rune.Value == 0x7F)
        {
            builder.Append("\\x").Append(rune.Value.ToString("x2", CultureInfo.InvariantCulture));
            return;
        }

        if (!Rune.IsControl(rune) && !Rune.IsSeparator(rune) && rune.Value != 0xFFFD)
        {
            builder.Append(rune.ToString());
            return;
        }

        builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
    }

    private static string GoQuoteRune(long value)
    {
        if (value is < 0 or > 0x10FFFF)
        {
            return "�";
        }

        var rune = new Rune((int)value);
        var builder = new StringBuilder();
        builder.Append('\'');
        switch (rune.Value)
        {
            case '\'':
                builder.Append("\\'");
                break;
            case '\\':
                builder.Append("\\\\");
                break;
            case '\n':
                builder.Append("\\n");
                break;
            case '\r':
                builder.Append("\\r");
                break;
            case '\t':
                builder.Append("\\t");
                break;
            default:
                AppendQuotedRune(builder, rune);
                break;
        }

        builder.Append('\'');
        return builder.ToString();
    }
}
