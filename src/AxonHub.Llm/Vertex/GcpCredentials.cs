using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Vertex;

/// <summary>
/// Port of the credential half of <c>google.Credentials</c>
/// (golang.org/x/oauth2/google): decode a service-account, gcloud user or
/// client-configuration JSON document into a token source.
/// </summary>
/// <remarks>
/// <para>
/// The reference delegates to the full google.golang.org/api auth stack. The
/// port covers the credential types the AxonHub UI stores (service account and
/// <c>gcloud auth application-default login</c> user credentials); workload
/// identity federation, impersonated service accounts and interactive client
/// configurations are rejected at construction (difference C128).
/// </para>
/// <para>
/// Decoding follows Go's <c>encoding/json</c>: case-insensitive field names,
/// last duplicate wins, JSON null is a no-op, and a wrong field type fails with
/// Go's <c>UnmarshalTypeError</c> text.
/// </para>
/// </remarks>
public sealed class GcpCredentials
{
    private GcpCredentials(string projectId, IGcpTokenSource tokenSource)
    {
        ProjectId = projectId;
        TokenSource = tokenSource;
    }

    /// <summary>The <c>project_id</c> field (empty for client configurations).</summary>
    public string ProjectId { get; }

    /// <summary>The token source the Vertex executor attaches to every request.</summary>
    public IGcpTokenSource TokenSource { get; }

    /// <summary>Port of <c>google.CredentialsFromJSON</c>.</summary>
    public static GcpCredentials FromJson(string jsonData, GcpTokenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(jsonData);
        return FromJson(Encoding.UTF8.GetBytes(jsonData), options);
    }

    /// <summary>Port of <c>google.CredentialsFromJSON</c> over raw bytes.</summary>
    public static GcpCredentials FromJson(ReadOnlySpan<byte> jsonData, GcpTokenOptions? options = null)
    {
        options ??= new GcpTokenOptions();

        // CredentialsFromJSONWithParams tries ConfigFromJSON first and ignores
        // its error; a client_credentials.json document (web/installed with at
        // least one redirect URI) wins over every other interpretation.
        if (TryParseClientConfig(jsonData, out _))
        {
            return new GcpCredentials(string.Empty, new ClientConfigGcpTokenSource());
        }

        var file = DecodeCredentialsFile(jsonData);
        return new GcpCredentials(file.ProjectId, CreateTokenSource(file, options));
    }

    /// <summary>
    /// Port of <c>credentialsFile.tokenSource</c> (google.go:154-240): dispatch
    /// one credential document to its token source. The type is also used
    /// recursively for an impersonated service account's
    /// <c>source_credentials</c>.
    /// </summary>
    private static IGcpTokenSource CreateTokenSource(
        GcpCredentialFile file,
        GcpTokenOptions options)
        => file.Type switch
        {
            // The reference's jwt.Config.TokenSource and oauth2.Config.TokenSource
            // both wrap the refresher in a reuseTokenSource, so the access token
            // is fetched once and served until 10s before its expiry.
            "service_account" or "gdch_service_account"
                => new ReuseGcpTokenSource(new ServiceAccountGcpTokenSource(file, options), options),
            "authorized_user"
                => new ReuseGcpTokenSource(new AuthorizedUserGcpTokenSource(file, options), options),
            "external_account_authorized_user"
                => new ReuseGcpTokenSource(
                    ExternalAccountAuthorizedUserGcpTokenSource.Create(file, options),
                    options),
            "impersonated_service_account" => CreateImpersonatedTokenSource(file, options),
            "external_account" => throw new NotSupportedException(
                "GCP credential type " + GoCast.GoQuote(file.Type)
                + " is not supported by the .NET port (difference C128)"),
            "" => throw new InvalidOperationException("missing 'type' field in credentials"),
            _ => throw new InvalidOperationException(
                "unknown credential type: " + GoCast.GoQuote(file.Type)),
        };

    /// <summary>
    /// Port of the impersonated_service_account branch (google.go:224-240):
    /// require the impersonation URL and source credentials, recursively build
    /// the source token source, then exchange it at the IAM credentials URL.
    /// </summary>
    private static IGcpTokenSource CreateImpersonatedTokenSource(
        GcpCredentialFile file,
        GcpTokenOptions options)
    {
        if (file.ServiceAccountImpersonationUrl.Length == 0
            || file.SourceCredentials is null)
        {
            throw new InvalidOperationException(
                "missing 'source_credentials' field or "
                + "'service_account_impersonation_url' in credentials");
        }

        return new ReuseGcpTokenSource(
            new ImpersonatedGcpTokenSource(
                CreateTokenSource(file.SourceCredentials, options),
                file.ServiceAccountImpersonationUrl,
                file.Delegates,
                file.TokenLifetimeSeconds,
                options),
            options);
    }

    /// <summary>Port of the <c>credentialsFile</c> struct decode (google.go:118-151).</summary>
    private static GcpCredentialFile DecodeCredentialsFile(ReadOnlySpan<byte> data)
    {
        var syntax = GoJsonSyntax.Validate(data);
        if (syntax is not null)
        {
            throw new InvalidOperationException(syntax);
        }

        var kind = GoStructJson.RootKind(data);
        if (kind is null)
        {
            using var document = JsonDocument.Parse(data.ToArray());
            return DecodeCredentialsFileObject(document.RootElement);
        }

        if (kind != "null")
        {
            // Go: json: cannot unmarshal <kind> into Go value of type google.credentialsFile.
            throw new InvalidOperationException(
                GoStructJson.TypeMismatch(kind, "google.credentialsFile"));
        }

        var file = new GcpCredentialFile();

        return file;
    }

    private static List<string>? AssignStringList(JsonElement value, string jsonName)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field credentialsFile." + jsonName
                + " of type []string");
        }

        var result = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "json: cannot unmarshal " + KindName(element)
                    + " into Go struct field credentialsFile." + jsonName
                    + " of type string");
            }

            result.Add(element.GetString() ?? string.Empty);
        }

        return result;
    }

    private static GcpCredentialFile? DecodeSourceCredentials(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field credentialsFile.source_credentials "
                + "of type google.credentialsFile");
        }

        return DecodeCredentialsFileObject(value);
    }

    private static int DecodeImpersonationLifetime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field credentialsFile.service_account_impersonation "
                + "of type google.serviceAccountImpersonationInfo");
        }

        var lifetime = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!string.Equals(
                    FoldAscii(property.Name),
                    "token_lifetime_seconds",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out lifetime))
            {
                throw new InvalidOperationException(
                    "json: cannot unmarshal " + KindName(property.Value)
                    + " into Go struct field credentialsFile.service_account_impersonation."
                    + "token_lifetime_seconds of type int");
            }
        }

        return lifetime;
    }

    /// <summary>
    /// Shared object decoder for the top-level document and the nested
    /// <c>source_credentials</c> object.
    /// </summary>
    private static GcpCredentialFile DecodeCredentialsFileObject(JsonElement root)
    {
        var file = new GcpCredentialFile();
        foreach (var property in root.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "type":
                    AssignString(ref file.Type, property.Value, "type");
                    break;
                case "client_email":
                    AssignString(ref file.ClientEmail, property.Value, "client_email");
                    break;
                case "private_key_id":
                    AssignString(ref file.PrivateKeyId, property.Value, "private_key_id");
                    break;
                case "private_key":
                    AssignString(ref file.PrivateKey, property.Value, "private_key");
                    break;
                case "auth_uri":
                    AssignString(ref file.AuthUri, property.Value, "auth_uri");
                    break;
                case "token_uri":
                    AssignString(ref file.TokenUri, property.Value, "token_uri");
                    break;
                case "token_url":
                    AssignString(ref file.TokenUrlExternal, property.Value, "token_url");
                    break;
                case "revoke_url":
                    AssignString(ref file.RevokeUrl, property.Value, "revoke_url");
                    break;
                case "service_account_impersonation_url":
                    AssignString(
                        ref file.ServiceAccountImpersonationUrl,
                        property.Value,
                        "service_account_impersonation_url");
                    break;
                case "delegates":
                    file.Delegates = AssignStringList(property.Value, "delegates");
                    break;
                case "source_credentials":
                    file.SourceCredentials = DecodeSourceCredentials(property.Value);
                    break;
                case "service_account_impersonation":
                    file.TokenLifetimeSeconds = DecodeImpersonationLifetime(property.Value);
                    break;
                case "project_id":
                    AssignString(ref file.ProjectId, property.Value, "project_id");
                    break;
                case "universe_domain":
                    AssignString(ref file.UniverseDomain, property.Value, "universe_domain");
                    break;
                case "client_secret":
                    AssignString(ref file.ClientSecret, property.Value, "client_secret");
                    break;
                case "client_id":
                    AssignString(ref file.ClientId, property.Value, "client_id");
                    break;
                case "refresh_token":
                    AssignString(ref file.RefreshToken, property.Value, "refresh_token");
                    break;
                case "audience":
                    AssignString(ref file.Audience, property.Value, "audience");
                    break;
                case "quota_project_id":
                    AssignString(ref file.QuotaProjectId, property.Value, "quota_project_id");
                    break;
            }
        }

        return file;
    }

    private static void AssignString(ref string target, JsonElement value, string jsonName)
    {
        // Unmarshaling null into a string field is a no-op.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value) + " into Go struct field credentialsFile."
                + jsonName + " of type string");
        }

        target = value.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Port of <c>google.ConfigFromJSON</c>'s detection half: an object with a
    /// valid <c>web</c> or <c>installed</c> block carrying at least one redirect
    /// URI. Any type error makes the reference's decode fail (and its error is
    /// ignored), so this returns false.
    /// </summary>
    private static bool TryParseClientConfig(ReadOnlySpan<byte> data, out ClientConfig config)
    {
        config = null!;

        if (GoJsonSyntax.Validate(data) is not null || GoStructJson.RootKind(data) is not null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(data.ToArray());
        ClientConfig? web = null;
        ClientConfig? installed = null;
        try
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (FoldAscii(property.Name))
                {
                    case "web":
                        web = DecodeClientConfig(property.Value);
                        break;
                    case "installed":
                        installed = DecodeClientConfig(property.Value);
                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // ConfigFromJSON's error is discarded by CredentialsFromJSON.
            return false;
        }

        var chosen = web ?? installed;
        if (chosen is null || chosen.RedirectUris.Count == 0)
        {
            return false;
        }

        config = chosen;
        return true;
    }

    private static ClientConfig? DecodeClientConfig(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value) + " into Go struct field cred of type struct");
        }

        var config = new ClientConfig();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "client_id":
                    config.ClientId = ClientConfigString(property.Value, "cred.client_id");
                    break;
                case "client_secret":
                    config.ClientSecret = ClientConfigString(property.Value, "cred.client_secret");
                    break;
                case "auth_uri":
                    config.AuthUri = ClientConfigString(property.Value, "cred.auth_uri");
                    break;
                case "token_uri":
                    config.TokenUri = ClientConfigString(property.Value, "cred.token_uri");
                    break;
                case "redirect_uris":
                    config.RedirectUris = ClientConfigStrings(property.Value);
                    break;
            }
        }

        return config;
    }

    private static string ClientConfigString(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value) + " into Go struct field "
                + field + " of type string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static List<string> ClientConfigStrings(JsonElement value)
    {
        var result = new List<string>();
        if (value.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field cred.redirect_uris of type []string");
        }

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "json: cannot unmarshal " + KindName(element)
                    + " into Go struct field cred.redirect_uris of type string");
            }

            result.Add(element.GetString() ?? string.Empty);
        }

        return result;
    }

    private static string KindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "null",
    };

    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    private sealed class ClientConfig
    {
        public string ClientId { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;

        public string AuthUri { get; set; } = string.Empty;

        public string TokenUri { get; set; } = string.Empty;

        public List<string> RedirectUris { get; set; } = [];
    }
}

/// <summary>Port of the <c>credentialsFile</c> struct (google.go:118-151).</summary>
internal sealed class GcpCredentialFile
{
    // Fields (not properties) so DecodeCredentialsFile can pass them by ref to
    // the no-op-on-null string assignment.
#pragma warning disable CA1051 // internal type; not part of the public surface
    public string Type = string.Empty;

    public string ClientEmail = string.Empty;

    public string PrivateKeyId = string.Empty;

    public string PrivateKey = string.Empty;

    public string AuthUri = string.Empty;

    public string TokenUri = string.Empty;

    public string TokenUrlExternal = string.Empty;

    public string RevokeUrl = string.Empty;

    public string ServiceAccountImpersonationUrl = string.Empty;

    public List<string>? Delegates;

    public GcpCredentialFile? SourceCredentials;

    public int TokenLifetimeSeconds;

    public string ProjectId = string.Empty;

    public string UniverseDomain = string.Empty;

    public string ClientSecret = string.Empty;

    public string ClientId = string.Empty;

    public string RefreshToken = string.Empty;

    public string Audience = string.Empty;

    public string QuotaProjectId = string.Empty;
#pragma warning restore CA1051
}
