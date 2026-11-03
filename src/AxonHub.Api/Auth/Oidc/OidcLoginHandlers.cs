using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of the login-flow handlers of api.OIDCHandlers
/// (internal/server/api/oidc.go:73-277): authorize, link authorize, callback
/// (with and without the provider segment) and exchange. The routes are
/// mounted in Program.cs from <see cref="OidcLoginRoutes"/>.
/// </summary>
public static class OidcLoginHandlers
{
    private const string IdpCallbackPath = "/oauth/oidc/idp-callback";

    private const string ExchangeBindingRequired =
        "Key: 'Code' Error:Field validation for 'Code' failed on the 'required' tag";

    /// <summary>
    /// Anonymous struct the reference binds on POST /oauth/oidc/exchange; its
    /// Go type string appears verbatim in root-type decode errors.
    /// </summary>
    private const string ExchangeRequestGoType =
        "struct { Code string \"json:\\\"code\\\" binding:\\\"required\\\"\" }";

    /// <summary>Port of <c>OIDCHandlers.GetAuthorizeURL</c> (oidc.go:73-101).</summary>
    public static async Task GetAuthorizeUrlAsync(
        HttpContext context,
        string provider,
        OidcLoginService login,
        OidcLoginOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(provider))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse("Provider is required"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!options.TryGetBaseUrl(out var baseUrl, out var baseError))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse(baseError),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        OidcAuthorizeUrl result;
        try
        {
            result = await login.GetAuthorizeUrlAsync(provider, baseUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse(ex.Message),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new AuthorizeResponse(new AuthorizeData(result.State, result.Url)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of <c>OIDCHandlers.GetLinkAuthorizeURL</c> (oidc.go:103-139).</summary>
    public static async Task GetLinkAuthorizeUrlAsync(
        HttpContext context,
        string provider,
        OidcLoginService login,
        OidcLoginOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(provider))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse("Provider is required"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (context.Items[OidcLinkAuth.ContextKey] is not UserRecord user)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    new ErrorResponse("Unauthorized"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!options.TryGetBaseUrl(out var baseUrl, out var baseError))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse(baseError),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        OidcAuthorizeUrl result;
        try
        {
            result = await login.GetLinkAuthorizeUrlAsync(
                    provider,
                    baseUrl,
                    user.Id,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse(ex.Message),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new AuthorizeResponse(new AuthorizeData(result.State, result.Url)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of <c>OIDCHandlers.Callback</c> without the provider segment.</summary>
    public static Task CallbackDefaultAsync(
        HttpContext context,
        OidcService oidc,
        OidcLoginService login,
        OidcLoginOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(oidc);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(options);

        return CallbackCoreAsync(context, null, oidc, login, options, cancellationToken);
    }

    /// <summary>Port of <c>OIDCHandlers.Callback</c> with the provider segment.</summary>
    public static Task CallbackAsync(
        HttpContext context,
        string provider,
        OidcService oidc,
        OidcLoginService login,
        OidcLoginOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(oidc);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(options);

        return CallbackCoreAsync(context, provider, oidc, login, options, cancellationToken);
    }

    private static async Task CallbackCoreAsync(
        HttpContext context,
        string? provider,
        OidcService oidc,
        OidcLoginService login,
        OidcLoginOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(provider))
        {
            // If only one provider, the parameter is optional.
            if (oidc.CountProviders() == 1)
            {
                var providers = await oidc.GetProvidersAsync(null, cancellationToken)
                    .ConfigureAwait(false);
                if (providers.Count > 0)
                {
                    provider = providers[0].Id;
                }
            }

            if (string.IsNullOrEmpty(provider))
            {
                await WriteJsonAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        new ErrorResponse("Provider is required"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        var code = FirstQueryValue(context, "code");
        var state = FirstQueryValue(context, "state");
        var error = FirstQueryValue(context, "error");

        if (error.Length > 0)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse(FirstQueryValue(context, "error_description")),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (code.Length == 0 || state.Length == 0)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse("Code and state are required"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!options.TryGetBaseUrl(out var baseUrl, out var baseError))
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse(baseError),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        OidcCallbackResult result;
        try
        {
            result = await login.CallbackAsync(provider, code, state, baseUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Response.Redirect(
                baseUrl + IdpCallbackPath + "?error=auth_failed&error_description="
                + HttpClientUtils.QueryEscape(ex.Message));
            return;
        }

        if (string.Equals(result.Intent, "link", StringComparison.Ordinal))
        {
            context.Response.Redirect(baseUrl + "/settings/profile?oidc_link=success");
            return;
        }

        context.Response.Redirect(baseUrl + IdpCallbackPath + "?code=" + result.ExchangeCode);
    }

    /// <summary>Port of <c>OIDCHandlers.Exchange</c> (oidc.go:243-277).</summary>
    public static async Task ExchangeAsync(
        HttpContext context,
        OidcLoginService login,
        ManagementAuthService auth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(auth);

        var binding = await BindExchangeCodeAsync(context, cancellationToken)
            .ConfigureAwait(false);
        if (binding.Error is not null)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    new ErrorResponse(binding.Error),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        UserRecord user;
        try
        {
            user = await login.ExchangeCodeAsync(binding.Code!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var status = ex.Message.Contains("invalid or expired", StringComparison.Ordinal)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status500InternalServerError;
            await WriteJsonAsync(context, status, new ErrorResponse(ex.Message), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string token;
        try
        {
            token = await auth.GenerateJwtTokenAsync(user, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    new ErrorResponse("Failed to generate token: " + ex.Message),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new ExchangeResponse(new ExchangeData(token, EntUserJson.From(user))),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// gin's <c>ShouldBindJSON</c> over the reference's anonymous request
    /// struct: a streaming decode (one top-level value, trailing bytes
    /// ignored), Go's key matching (case-insensitive, last match wins) and the
    /// <c>required</c> validation error.
    /// </summary>
    private static async Task<(string? Code, string? Error)> BindExchangeCodeAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var body = buffer.ToArray();

        var scanError = GoJsonSyntax.ScanDecoderValue(body, 0, out var end);
        if (scanError is not null)
        {
            return (null, scanError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            return rootKind == "null"
                ? (null, ExchangeBindingRequired)
                : (null, GoStructJson.TypeMismatch(rootKind, ExchangeRequestGoType));
        }

        var slice = body.AsSpan(0, end).ToArray();
        var sanitized = GoJsonUtf8.SanitizeStringLiterals(slice);
        if (sanitized is not null)
        {
            slice = sanitized;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(slice);
        }
        catch (JsonException)
        {
            // GoJsonSyntax accepted the document, so a decoder failure can only
            // come from a shape this port does not model (C003).
            return (null, "unexpected end of JSON input");
        }

        using (document)
        {
            var code = string.Empty;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("code", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        code = property.Value.GetString() ?? string.Empty;
                        break;
                    case JsonValueKind.Null:
                        code = string.Empty;
                        break;
                    case JsonValueKind.Number:
                        return (null, FieldTypeError("number"));
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return (null, FieldTypeError("bool"));
                    case JsonValueKind.Object:
                        return (null, FieldTypeError("object"));
                    case JsonValueKind.Array:
                        return (null, FieldTypeError("array"));
                }
            }

            return code.Length == 0 ? (null, ExchangeBindingRequired) : (code, null);
        }
    }

    private static string FieldTypeError(string kind)
        => "json: cannot unmarshal " + kind + " into Go struct field .code of type string";

    /// <summary>gin's <c>c.Query</c>: url.Values.Get, the first value.</summary>
    private static string FirstQueryValue(HttpContext context, string name)
    {
        var values = context.Request.Query[name];
        return values.Count == 0 ? string.Empty : values[0] ?? string.Empty;
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int statusCode,
        T value,
        CancellationToken cancellationToken)
    {
        var body = GoJson.Marshal(value);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ErrorResponse(
        [property: JsonPropertyName("error")] string Error);

    private sealed record AuthorizeData(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("url")] string Url);

    private sealed record AuthorizeResponse(
        [property: JsonPropertyName("data")] AuthorizeData Data);

    private sealed record ExchangeData(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("user")] EntUserJson User);

    private sealed record ExchangeResponse(
        [property: JsonPropertyName("data")] ExchangeData Data);

    /// <summary>
    /// ent.User's generated JSON: every field carries <c>omitempty</c> except
    /// <c>edges</c>, <c>password</c> is excluded, and times use
    /// <c>time.Time.MarshalJSON</c> (RFC3339Nano). <c>time.Time</c> is a
    /// struct, so its zero value is never omitted.
    /// </summary>
    private sealed record EntUserJson
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long Id { get; init; }

        [JsonPropertyName("created_at")]
        public required string CreatedAt { get; init; }

        [JsonPropertyName("updated_at")]
        public required string UpdatedAt { get; init; }

        [JsonPropertyName("deleted_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long DeletedAt { get; init; }

        [JsonPropertyName("email")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Email { get; init; }

        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Status { get; init; }

        [JsonPropertyName("prefer_language")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreferLanguage { get; init; }

        [JsonPropertyName("first_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FirstName { get; init; }

        [JsonPropertyName("last_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastName { get; init; }

        [JsonPropertyName("avatar")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Avatar { get; init; }

        [JsonPropertyName("is_owner")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsOwner { get; init; }

        [JsonPropertyName("scopes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Scopes { get; init; }

        [JsonPropertyName("edges")]
        public IReadOnlyDictionary<string, object> Edges { get; init; } =
            new Dictionary<string, object>(StringComparer.Ordinal);

        public static EntUserJson From(UserRecord user)
        {
            ArgumentNullException.ThrowIfNull(user);

            return new EntUserJson
            {
                Id = user.Id,
                CreatedAt = GoTime.Rfc3339Nano(user.CreatedAt),
                UpdatedAt = GoTime.Rfc3339Nano(user.UpdatedAt),
                DeletedAt = user.DeletedAt,
                Email = NullIfEmpty(user.Email),
                Status = NullIfEmpty(user.Status),
                PreferLanguage = NullIfEmpty(user.PreferLanguage),
                FirstName = NullIfEmpty(user.FirstName),
                LastName = NullIfEmpty(user.LastName),
                Avatar = NullIfEmpty(user.Avatar),
                IsOwner = user.IsOwner,
                Scopes = ParseScopes(user.Scopes),
            };
        }

        private static string? NullIfEmpty(string? value)
            => string.IsNullOrEmpty(value) ? null : value;

        private static List<string>? ParseScopes(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                var scopes = JsonSerializer.Deserialize<List<string>>(json, LlmJson.Options);
                return scopes is { Count: > 0 } ? scopes : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// The routes of the OIDC login flow (routes.go:101-104 and :131). Kept next to
/// the handlers so Program.cs only wires the public surface.
/// </summary>
public static class OidcLoginRoutes
{
    /// <summary>Registers the handlers on the application's endpoint builder.</summary>
    public static void MapOidcLoginRoutes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/oauth/oidc/authorize/{provider}",
            OidcLoginHandlers.GetAuthorizeUrlAsync);
        endpoints.MapGet("/oauth/oidc/callback", OidcLoginHandlers.CallbackDefaultAsync);
        endpoints.MapGet(
            "/oauth/oidc/callback/{provider}",
            OidcLoginHandlers.CallbackAsync);
        endpoints.MapPost("/oauth/oidc/exchange", OidcLoginHandlers.ExchangeAsync);

        // routes.go:131: the link flow lives under the admin JWT gate wired in
        // Program.cs, not on the public oauth group.
        endpoints.MapGet(
            "/admin/oidc/link/{provider}",
            OidcLoginHandlers.GetLinkAuthorizeUrlAsync);
    }
}
