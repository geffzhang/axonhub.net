using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Settings;
using AxonHub.Llm.Json;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Codex;

/// <summary>
/// Port of <c>CodexCompatibilityHandlers</c>
/// (internal/server/api/codex_compatibility.go) — the runtime half of the
/// <c>/codex</c> surface (C244): whoami, accounts/check and the filtered model
/// catalog. The images and responses routes reuse the OpenAI handlers.
/// </summary>
public static class CodexEndpoint
{
    /// <summary>Port of <c>CodexCompatibilityHandlers.Whoami</c>.</summary>
    public static async Task WhoamiAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var key = RequestIdentity.TryFromHttpContext(context, out var identity)
            ? identity.ApiKey
            : null;
        if (key is null)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid API key",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        var id = CodexKeyId(key.Id);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["email"] = key.Name,
            ["chatgpt_user_id"] = id,
            ["chatgpt_account_id"] = id,
            ["chatgpt_plan_type"] = "promax",
            ["chatgpt_account_is_fedramp"] = false,
        };

        context.Response.Headers.CacheControl = "no-store";
        await WriteJsonAsync(context, body).ConfigureAwait(false);
    }

    /// <summary>Port of <c>CodexCompatibilityHandlers.AccountsCheck</c>.</summary>
    public static async Task AccountsCheckAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var key = RequestIdentity.TryFromHttpContext(context, out var identity)
            ? identity.ApiKey
            : null;
        if (key is null)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid API key",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        var id = CodexKeyId(key.Id);

        // codexAccountCheckResponse is a struct: gin marshals it (and the
        // entry) in declaration order, unlike a map.
        var body = new CodexAccountCheckResponse
        {
            Accounts =
            [
                new CodexAccountCheckEntry
                {
                    Id = id,
                    PlanType = "promax",
                    WorkspaceBackendOrigin = "NO_CONSTRAINT",
                    AccountRoutingOverride = "NO_CONSTRAINT",
                    Name = key.Name,
                    Structure = "personal",
                },
            ],
            AccountOrdering = [id],
            DefaultAccountId = id,
        };

        context.Response.Headers.CacheControl = "no-store";
        await WriteJsonAsync(context, body).ConfigureAwait(false);
    }

    /// <summary>Port of <c>CodexCompatibilityHandlers.ListModels</c>.</summary>
    public static async Task ListModelsAsync(
        HttpContext context,
        ModelService models,
        ICodexCompatibilitySettingsService settings,
        ICodexCatalogService catalogs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalogs);

        var cancellationToken = context.RequestAborted;
        context.Response.Headers.CacheControl = "no-store";

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // errors.Is(err, privacy.Deny) → 403, everything else → 500; both
            // carry the fixed message.
            await AbortResponse.WriteErrorAsync(
                    context,
                    ex is PrivacyDenyException
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status500InternalServerError,
                    "failed to list visible models",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CodexCatalog catalog;
        try
        {
            // authz.RunWithSystemBypass("codex-catalog-source").
            var current = await settings.GetBypassAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current.ChannelID is not { } channelId)
            {
                throw new CodexCatalogException(
                    503,
                    "Codex catalog source is unavailable");
            }

            catalog = await catalogs
                .FetchAsync(
                    channelId,
                    context.Request.Query["client_version"].FirstOrDefault() ?? string.Empty,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CodexCatalogException ex)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    ex.Status,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception)
        {
            // errors.As fails: the callback's own errors (settings read) are
            // the fixed "failed to read Codex catalog settings" 500.
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "failed to read Codex catalog settings",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var ids = new string[visibleModels.Count];
        for (var i = 0; i < visibleModels.Count; i++)
        {
            ids[i] = visibleModels[i].Id;
        }

        var body = catalog.Intersect(ids);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>fmt.Sprintf("axonhub-key-%d", key.ID).</summary>
    private static string CodexKeyId(long id)
        => "axonhub-key-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task WriteJsonAsync<T>(HttpContext context, T body)
    {
        var payload = GoJson.Marshal(body);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(payload, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Port of <c>codexAccountCheckResponse</c> (field order matters).</summary>
    private sealed class CodexAccountCheckResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("accounts")]
        public List<CodexAccountCheckEntry> Accounts { get; init; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("account_ordering")]
        public List<string> AccountOrdering { get; init; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("default_account_id")]
        public string DefaultAccountId { get; init; } = string.Empty;
    }

    /// <summary>Port of <c>codexAccountCheckEntry</c> (field order matters).</summary>
    private sealed class CodexAccountCheckEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("plan_type")]
        public string PlanType { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("workspace_backend_origin")]
        public string WorkspaceBackendOrigin { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("account_routing_override")]
        public string AccountRoutingOverride { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("structure")]
        public string Structure { get; init; } = string.Empty;
    }
}
