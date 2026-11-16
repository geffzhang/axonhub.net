using System.Globalization;
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
/// port covers service accounts, gcloud user credentials, workload identity
/// federation (file / URL / AWS / executable subject tokens), external-account
/// authorized users and impersonated service accounts; interactive web/installed
/// client configurations are rejected at construction (difference C128).
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
            "external_account" => CreateExternalAccountTokenSource(file, options),
            "" => throw new InvalidOperationException("missing 'type' field in credentials"),
            _ => throw new InvalidOperationException(
                "unknown credential type: " + GoCast.GoQuote(file.Type)),
        };

    /// <summary>
    /// Port of the external_account branch (google.go:200-223 and
    /// externalaccount.NewTokenSource): validate audience / subject token type /
    /// source, build the file- or URL-sourced STS exchange, and optionally wrap
    /// it in service-account impersonation.
    /// </summary>
    private static IGcpTokenSource CreateExternalAccountTokenSource(
        GcpCredentialFile file,
        GcpTokenOptions options)
    {
        if (file.Audience.Length == 0)
        {
            throw new InvalidOperationException(
                "oauth2/google/externalaccount: Audience must be set");
        }

        if (file.SubjectTokenType.Length == 0)
        {
            throw new InvalidOperationException(
                "oauth2/google/externalaccount: Subject token type must be set");
        }

        if (file.WorkforcePoolUserProject.Length > 0
            && !WorkforceAudiencePattern.IsMatch(file.Audience))
        {
            throw new InvalidOperationException(
                "oauth2/google/externalaccount: Workforce pool user project "
                + "should not be set for non-workforce pool credentials");
        }

        var source = file.CredentialSource
            ?? throw new InvalidOperationException(
                "oauth2/google/externalaccount: One of CredentialSource, "
                + "SubjectTokenSupplier, or AwsSecurityCredentialsSupplier must be set");

        IExternalSubjectTokenSource sourceTokenSource;
        if (source.EnvironmentId.Length > 3
            && source.EnvironmentId.StartsWith("aws", StringComparison.Ordinal))
        {
            if (!int.TryParse(
                    source.EnvironmentId[3..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var awsVersion))
            {
                // Go's Atoi failure falls through to the generic source error.
                throw new InvalidOperationException(
                    "oauth2/google/externalaccount: unable to parse credential source");
            }

            if (awsVersion != 1)
            {
                throw new InvalidOperationException(
                    "oauth2/google/externalaccount: aws version '" + awsVersion
                    + "' is not supported in the current build");
            }

            sourceTokenSource = new ExternalAccountAwsGcpTokenSource(
                source,
                file.Audience,
                options);
        }
        else if (source.File.Length > 0)
        {
            sourceTokenSource = new ExternalAccountFileGcpTokenSource(
                source.File,
                source.Format);
        }
        else if (source.Url.Length > 0)
        {
            sourceTokenSource = new ExternalAccountUrlGcpTokenSource(
                source.Url,
                source.Headers,
                source.Format,
                options);
        }
        else if (source.Executable is not null)
        {
            sourceTokenSource = ExternalAccountExecutableGcpTokenSource.Create(
                source.Executable,
                file.Audience,
                file.SubjectTokenType,
                file.ServiceAccountImpersonationUrl,
                options);
        }
        else
        {
            throw new InvalidOperationException(
                "oauth2/google/externalaccount: unable to parse credential source");
        }

        var tokenUrl = file.TokenUrlExternal.Length > 0
            ? file.TokenUrlExternal
            : "https://sts." + (file.UniverseDomain.Length > 0
                ? file.UniverseDomain
                : "googleapis.com") + "/v1/token";

        var stsSource = new ExternalAccountGcpTokenSource(
            sourceTokenSource,
            file.Audience,
            file.SubjectTokenType,
            tokenUrl,
            file.ServiceAccountImpersonationUrl.Length > 0
                ? ["https://www.googleapis.com/auth/cloud-platform"]
                : [],
            file.ClientId,
            file.ClientSecret,
            file.WorkforcePoolUserProject,
            options);

        if (file.ServiceAccountImpersonationUrl.Length == 0)
        {
            return new ReuseGcpTokenSource(stsSource, options);
        }

        return new ReuseGcpTokenSource(
            new ImpersonatedGcpTokenSource(
                new ReuseGcpTokenSource(stsSource, options),
                file.ServiceAccountImpersonationUrl,
                null,
                file.TokenLifetimeSeconds,
                options),
            options);
    }

    private static readonly System.Text.RegularExpressions.Regex WorkforceAudiencePattern =
        new(
            "//iam\\.googleapis\\.com/locations/[^/]+/workforcePools/",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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

    /// <summary>Port of externalaccount.CredentialSource / Format (basecredentials.go).</summary>
    private static GcpCredentialSource? DecodeCredentialSource(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field credentialsFile.credential_source "
                + "of type externalaccount.CredentialSource");
        }

        var source = new GcpCredentialSource();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "file":
                    source.File = ReadStringValue(property.Value, "credential_source.file");
                    break;
                case "url":
                    source.Url = ReadStringValue(property.Value, "credential_source.url");
                    break;
                case "environment_id":
                    source.EnvironmentId = ReadStringValue(
                        property.Value,
                        "credential_source.environment_id");
                    break;
                case "region_url":
                    source.RegionUrl = ReadStringValue(
                        property.Value,
                        "credential_source.region_url");
                    break;
                case "regional_cred_verification_url":
                    source.RegionalCredVerificationUrl = ReadStringValue(
                        property.Value,
                        "credential_source.regional_cred_verification_url");
                    break;
                case "imdsv2_session_token_url":
                    source.Imdsv2SessionTokenUrl = ReadStringValue(
                        property.Value,
                        "credential_source.imdsv2_session_token_url");
                    break;
                case "headers":
                    source.Headers = AssignStringMap(
                        property.Value,
                        "credential_source.headers");
                    break;
                case "format":
                    source.Format = DecodeCredentialFormat(property.Value);
                    break;
                case "executable":
                    source.Executable = DecodeExecutable(property.Value);
                    break;
            }
        }

        return source;
    }

    private static Dictionary<string, string>? AssignStringMap(
        JsonElement value,
        string jsonName)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field " + jsonName + " of type map[string]string");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                result[property.Name] = string.Empty;
            }
            else if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            else
            {
                throw new InvalidOperationException(
                    "json: cannot unmarshal " + KindName(property.Value)
                    + " into Go struct field " + jsonName + " of type string");
            }
        }

        return result;
    }

    private static GcpCredentialFormat DecodeCredentialFormat(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new GcpCredentialFormat();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field credential_source.format "
                + "of type externalaccount.Format");
        }

        var format = new GcpCredentialFormat();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "type":
                    format.Type = ReadStringValue(property.Value, "credential_source.format.type");
                    break;
                case "subject_token_field_name":
                    format.SubjectTokenFieldName = ReadStringValue(
                        property.Value,
                        "credential_source.format.subject_token_field_name");
                    break;
            }
        }

        return format;
    }

    private static GcpExecutableConfig? DecodeExecutable(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field credential_source.executable "
                + "of type externalaccount.ExecutableConfig");
        }

        var executable = new GcpExecutableConfig();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "command":
                    executable.Command = ReadStringValue(
                        property.Value,
                        "credential_source.executable.command");
                    break;
                case "timeout_millis":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        executable.TimeoutMillis = null;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number
                             && property.Value.TryGetInt32(out var timeout))
                    {
                        executable.TimeoutMillis = timeout;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "json: cannot unmarshal " + KindName(property.Value)
                            + " into Go struct field credential_source.executable.timeout_millis "
                            + "of type int");
                    }

                    break;
                case "output_file":
                    executable.OutputFile = ReadStringValue(
                        property.Value,
                        "credential_source.executable.output_file");
                    break;
            }
        }

        return executable;
    }

    private static string ReadStringValue(JsonElement value, string jsonName)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "json: cannot unmarshal " + KindName(value)
                + " into Go struct field " + jsonName + " of type string");
        }

        return value.GetString() ?? string.Empty;
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
                case "subject_token_type":
                    AssignString(
                        ref file.SubjectTokenType,
                        property.Value,
                        "subject_token_type");
                    break;
                case "workforce_pool_user_project":
                    AssignString(
                        ref file.WorkforcePoolUserProject,
                        property.Value,
                        "workforce_pool_user_project");
                    break;
                case "credential_source":
                    file.CredentialSource = DecodeCredentialSource(property.Value);
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

    public string SubjectTokenType = string.Empty;

    public string WorkforcePoolUserProject = string.Empty;

    public GcpCredentialSource? CredentialSource;

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

/// <summary>Port of externalaccount.CredentialSource (basecredentials.go).</summary>
public sealed class GcpCredentialSource
{
    public string File { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string>? Headers { get; set; }

    public string EnvironmentId { get; set; } = string.Empty;

    public string RegionUrl { get; set; } = string.Empty;

    public string RegionalCredVerificationUrl { get; set; } = string.Empty;

    public string Imdsv2SessionTokenUrl { get; set; } = string.Empty;

    public GcpCredentialFormat Format { get; set; } = new();

    public GcpExecutableConfig? Executable { get; set; }
}

/// <summary>Port of externalaccount.Format (basecredentials.go).</summary>
public sealed class GcpCredentialFormat
{
    public string Type { get; set; } = string.Empty;

    public string SubjectTokenFieldName { get; set; } = string.Empty;
}

/// <summary>Port of externalaccount.ExecutableConfig.</summary>
public sealed class GcpExecutableConfig
{
    public string Command { get; set; } = string.Empty;

    public int? TimeoutMillis { get; set; }

    public string OutputFile { get; set; } = string.Empty;
}
