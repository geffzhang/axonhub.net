using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.OAuth;

/// <summary>
/// Port of llm/oauth/credentials.go and the parse half of
/// llm/oauth/exchange_strategy.go: credentials round-trip, token-response
/// decoding and the OAuth error envelope.
/// </summary>
public static class OAuthCredentialsJson
{
    /// <summary>
    /// Port of <c>time.Time.UnmarshalJSON</c>/<c>MarshalJSON</c> for the
    /// <c>expires_at</c> field: RFC3339 and RFC3339Nano, with Go's zero time
    /// (<c>0001-01-01T00:00:00Z</c>) as the absent-expiry value.
    /// </summary>
    private sealed class GoTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            // Go's Time.UnmarshalJSON treats a JSON null as a no-op (zero time).
            if (reader.TokenType == JsonTokenType.Null)
            {
                return DateTimeOffset.MinValue;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Time.UnmarshalJSON: input is not a JSON string");
            }

            var text = reader.GetString() ?? string.Empty;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                throw new JsonException(
                    "parsing time \"" + text + "\": cannot parse as RFC3339");
            }

            return parsed;
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
            => writer.WriteStringValue(GoTime.Rfc3339Nano(value.UtcDateTime));
    }

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(LlmJson.Options);
        options.Converters.Add(new GoTimeOffsetConverter());
        return options;
    }

    /// <summary>Port of oauth.ParseCredentialsJSON.</summary>
    /// <exception cref="InvalidOperationException">
    /// Empty input or a missing <c>access_token</c>. A malformed document
    /// surfaces as the decoder's <see cref="JsonException"/> (difference C093:
    /// the decoder's message text differs from Go's).
    /// </exception>
    public static OAuthCredentials ParseCredentialsJson(string raw)
    {
        var trimmed = GoStrings.TrimSpace(raw);
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("empty credentials");
        }

        var creds = JsonSerializer.Deserialize<OAuthCredentials>(trimmed, Options)
            ?? throw new JsonException("credentials: unexpected JSON null");

        creds.ClientId = NonNull(creds.ClientId);
        creds.AccessToken = NonNull(creds.AccessToken);
        creds.RefreshToken = NonNull(creds.RefreshToken);
        creds.IdToken = NonNull(creds.IdToken);
        creds.TokenType = NonNull(creds.TokenType);

        if (creds.AccessToken.Length == 0)
        {
            throw new InvalidOperationException("access_token is empty");
        }

        // If refresh_token exists but expires_at is missing, assume 1 hour.
        if (creds.RefreshToken.Length > 0 && creds.ExpiresAt == DateTimeOffset.MinValue)
        {
            creds.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        }

        return creds;
    }

    /// <summary>
    /// Decodes the <c>oauth</c> object of a channel credentials document the way
    /// Go's <c>json.Unmarshal</c> decodes it into oauth.OAuthCredentials: absent
    /// fields keep their zero value and no validation happens here. The caller
    /// re-marshals the result and then runs it through
    /// <see cref="ParseCredentialsJson"/>, exactly like
    /// <c>buildCodexOutbound</c>.
    /// </summary>
    /// <exception cref="JsonException">The document is not a decodable object.</exception>
    public static OAuthCredentials ParseOAuthObject(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var creds = JsonSerializer.Deserialize<OAuthCredentials>(raw, Options)
            ?? throw new JsonException("oauth credentials: unexpected JSON null");

        creds.ClientId = NonNull(creds.ClientId);
        creds.AccessToken = NonNull(creds.AccessToken);
        creds.RefreshToken = NonNull(creds.RefreshToken);
        creds.IdToken = NonNull(creds.IdToken);
        creds.TokenType = NonNull(creds.TokenType);

        return creds;
    }

    /// <summary>
    /// Port of <c>(*objects.ChannelCredentials).ResolveOAuthCredentials</c>
    /// (internal/objects/channel.go:631-639): a typed <c>oauth</c> object with a
    /// non-blank access token wins; otherwise the legacy <c>apiKey</c> string is
    /// parsed as the credentials document (which rejects empty input and an
    /// empty <c>access_token</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Empty input or a missing access token.</exception>
    /// <exception cref="JsonException">The document is not a decodable object.</exception>
    public static OAuthCredentials ResolveChannelCredentials(string? credentialsJson)
    {
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(credentialsJson);
        if (oauthJson is not null)
        {
            var oauth = ParseOAuthObject(oauthJson);
            if (GoStrings.TrimSpace(oauth.AccessToken).Length > 0)
            {
                return oauth;
            }
        }

        return ParseCredentialsJson(ChannelCredentials.GetLegacyApiKey(credentialsJson));
    }

    /// <summary>Port of oauth.ParseTokenResponse (llm/oauth/exchange_strategy.go:162-195).</summary>
    public static OAuthCredentials ParseTokenResponse(byte[] responseBody, string clientId)
    {
        TokenResponse tokenResponse;
        try
        {
            tokenResponse = JsonSerializer.Deserialize<TokenResponse>(responseBody, Options)
                ?? throw new JsonException("token response: unexpected JSON null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("decode response: " + ex.Message, ex);
        }

        tokenResponse.AccessToken = NonNull(tokenResponse.AccessToken);

        if (tokenResponse.AccessToken.Length == 0)
        {
            TokenError? tokenError = null;
            try
            {
                tokenError = JsonSerializer.Deserialize<TokenError>(responseBody, Options);
            }
            catch (JsonException)
            {
                // The reference ignores the error envelope's decode failure and
                // reports the missing access_token instead.
            }

            if (!string.IsNullOrEmpty(tokenError?.Error))
            {
                throw new InvalidOperationException(
                    "token request failed: " + tokenError.Error + " - " + NonNull(tokenError.ErrorDescription));
            }

            throw new InvalidOperationException("token response missing access_token");
        }

        var creds = new OAuthCredentials
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = NonNull(tokenResponse.RefreshToken),
            IdToken = NonNull(tokenResponse.IdToken),
            ClientId = clientId,
            TokenType = NonNull(tokenResponse.TokenType),
        };

        var scope = NonNull(tokenResponse.Scope);
        if (scope.Length > 0)
        {
            creds.Scopes = SplitFields(scope);
        }

        if (tokenResponse.ExpiresIn > 0)
        {
            creds.ExpiresAt = tokenResponse.ExpiresAt();
        }

        return creds;
    }

    /// <summary>Port of <c>strings.Fields</c>: split around Unicode white space.</summary>
    internal static List<string> SplitFields(string value)
        => [.. value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    private static string NonNull(string? value) => value ?? string.Empty;
}

/// <summary>Port of <c>(*OAuthCredentials).ToJSON</c> (llm/oauth/credentials.go:79-86).</summary>
public sealed partial class OAuthCredentials
{
    /// <summary>
    /// Go <c>json.Marshal</c> of the credentials struct: declaration-order
    /// fields, empty strings and empty scope lists omitted (<c>omitempty</c>),
    /// <c>expires_at</c> always present as RFC3339Nano.
    /// </summary>
    /// <remarks>
    /// The encoder leaves <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> unescaped
    /// where Go escapes them (difference C002).
    /// </remarks>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();

            if (ClientId.Length > 0)
            {
                writer.WriteString("client_id", ClientId);
            }

            writer.WriteString("access_token", AccessToken);
            writer.WriteString("refresh_token", RefreshToken);

            if (IdToken.Length > 0)
            {
                writer.WriteString("id_token", IdToken);
            }

            writer.WriteString("expires_at", GoTime.Rfc3339Nano(ExpiresAt.UtcDateTime));

            if (TokenType.Length > 0)
            {
                writer.WriteString("token_type", TokenType);
            }

            if (Scopes is { Count: > 0 } scopes)
            {
                writer.WriteStartArray("scopes");
                foreach (var scope in scopes)
                {
                    writer.WriteStringValue(scope);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
