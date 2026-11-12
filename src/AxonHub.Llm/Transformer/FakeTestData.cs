namespace AxonHub.Llm.Transformer;

/// <summary>
/// Port of the <c>//go:embed testdata/*</c> fixtures used by the fake channel
/// transformers (anthropic/fake.go, openai/fake.go). The files are embedded
/// with explicit logical names in AxonHub.Llm.csproj.
/// </summary>
internal static class FakeTestData
{
    /// <summary>Reads one embedded fixture; the resource name must be a LogicalName.</summary>
    public static byte[] Read(string resourceName)
    {
        using var stream = typeof(FakeTestData).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("missing embedded testdata resource: " + resourceName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
