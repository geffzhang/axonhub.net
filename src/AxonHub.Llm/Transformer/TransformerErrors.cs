namespace AxonHub.Llm.Transformer;

/// <summary>Port of the sentinel errors in llm/transformer/errors.go.</summary>
public enum TransformerErrorKind
{
    None,
    InvalidRequest,
    InvalidModel,
    InvalidResponse,

    /// <summary>
    /// Port of <c>transformer.ErrUnsupportedConversion</c>: the requested
    /// protocol/transport pair cannot be produced. Retrying cannot repair it,
    /// so the pipeline stops before the retry policy runs
    /// (llm/pipeline/pipeline.go:326-328).
    /// </summary>
    UnsupportedConversion,
}

/// <summary>
/// Carrier for the reference's wrapped sentinel errors: the message matches
/// Go's <c>fmt.Errorf("%w: %s", sentinel, detail)</c> text so error surfaces
/// stay byte-identical.
/// </summary>
public sealed class TransformerException : Exception
{
    public TransformerException(TransformerErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public TransformerException(TransformerErrorKind kind, string message, Exception inner)
        : base(message, inner)
    {
        Kind = kind;
    }

    public TransformerErrorKind Kind { get; }
}

public static class TransformerErrors
{
    public static TransformerException InvalidRequest(string detail)
        => new(TransformerErrorKind.InvalidRequest, "invalid request: " + detail);

    public static TransformerException InvalidRequest(string detail, Exception inner)
        => new(TransformerErrorKind.InvalidRequest, "invalid request: " + detail, inner);

    public static TransformerException InvalidModel(string detail)
        => new(TransformerErrorKind.InvalidModel, "model not found: " + detail);

    /// <summary>Port of <c>fmt.Errorf("%w: %s", ErrUnsupportedConversion, detail)</c>.</summary>
    public static TransformerException UnsupportedConversion(string detail)
        => new(TransformerErrorKind.UnsupportedConversion, "unsupported conversion: " + detail);

    public static TransformerException InvalidResponse(string detail)
        => new(TransformerErrorKind.InvalidResponse, "invalid response: " + detail);

    public static TransformerException InvalidResponse(string detail, Exception inner)
        => new(TransformerErrorKind.InvalidResponse, "invalid response: " + detail, inner);

    /// <summary>
    /// Port of <c>errors.Is(err, transformer.ErrUnsupportedConversion)</c>: the
    /// sentinel may sit anywhere in the wrap chain, not only at its head.
    /// </summary>
    public static bool IsUnsupportedConversion(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is TransformerException { Kind: TransformerErrorKind.UnsupportedConversion })
            {
                return true;
            }
        }

        return false;
    }
}
