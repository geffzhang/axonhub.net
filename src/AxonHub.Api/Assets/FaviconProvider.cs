namespace AxonHub.Api.Assets;

/// <summary>
/// Port of <c>assets.Favicon.ReadFile("favicon.ico")</c>
/// (internal/server/assets/favicon.go, //go:embed favicon.ico). A missing
/// resource is the reference's read error: null here, 500 at the route.
/// </summary>
public interface IFaviconProvider
{
    byte[]? ReadDefaultFavicon();
}

/// <summary>
/// Reads the favicon embedded in this assembly. The file is byte-identical to
/// the reference's <c>internal/server/assets/favicon.ico</c>.
/// </summary>
public sealed class EmbeddedFaviconProvider : IFaviconProvider
{
    public const string ResourceName = "AxonHub.Api.Assets.favicon.ico";

    public byte[]? ReadDefaultFavicon()
    {
        using var stream = typeof(EmbeddedFaviconProvider).Assembly
            .GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
