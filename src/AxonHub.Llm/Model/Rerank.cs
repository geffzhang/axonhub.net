namespace AxonHub.Llm;

/// <summary>
/// Port of llm.RerankRequest (llm/rerank.go): the unified rerank sub-request.
/// Common fields like Model live on the parent <see cref="LlmRequest"/>.
/// </summary>
public sealed class RerankRequest
{
    /// <summary>The search query to compare documents against.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>The list of documents to rerank.</summary>
    public List<string> Documents { get; set; } = [];

    /// <summary>Number of most relevant documents to return. Optional.</summary>
    public int? TopN { get; set; }

    /// <summary>Whether to return the original documents in the response. Optional.</summary>
    public bool? ReturnDocuments { get; set; }
}

/// <summary>
/// Port of llm.RerankResponse (llm/rerank.go): the unified rerank sub-response.
/// Common fields like Usage live on the parent <see cref="LlmResponse"/>.
/// </summary>
public sealed class RerankResponse
{
    /// <summary>The object type, typically "list".</summary>
    public string Object { get; set; } = string.Empty;

    /// <summary>The reranked documents with relevance scores.</summary>
    public List<RerankResult> Results { get; set; } = [];
}

/// <summary>Port of llm.RerankResult: a single reranked document result.</summary>
public sealed class RerankResult
{
    /// <summary>The index of the document in the original list.</summary>
    public int Index { get; set; }

    /// <summary>The relevance score of the document to the query.</summary>
    public double RelevanceScore { get; set; }

    /// <summary>The original document text (optional, omitted to save bandwidth).</summary>
    public RerankDocument? Document { get; set; }
}

/// <summary>Port of llm.RerankDocument.</summary>
public sealed class RerankDocument
{
    public string Text { get; set; } = string.Empty;
}
