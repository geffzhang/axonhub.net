using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Llm.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of api.InvitationHandlers
/// (internal/server/api/invitation.go:16-138): the three invitation routes of
/// routes.go:87-88 (public lookup and registration) and :112 (JWT-gated
/// creation).
/// </summary>
/// <remarks>
/// gin's ShouldBindJSON decodes one JSON value with case-insensitive property
/// names and <em>without</em> rejecting trailing content; System.Text.Json
/// rejects trailing content and, under JsonSerializerDefaults.Web, would also
/// accept numbers as strings. The reader below keeps the reference's strict
/// number handling and uses case-insensitive names (C201).
/// </remarks>
public static class InvitationHandlers
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of InvitationHandlers.Create (invitation.go:69-90).</summary>
    public static async Task CreateAsync(
        HttpContext context,
        InvitationService invitations,
        IManagementIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(identity);

        var cancellationToken = context.RequestAborted;

        // contexts.GetProjectID: WithProjectID only sets it when the header
        // carries a valid project gid.
        var projectId = identity.GetCurrentProjectId();
        if (projectId is null)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "X-Project-ID header is required",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CreateInvitationRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CreateInvitationRequest>(
                    context.Request.Body,
                    RequestJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        // `roleID binding:"required"` on an int rejects the zero value; a JSON
        // null leaves the Go zero value, so an explicit null is also a bind
        // failure.
        if (request is null || request.RoleID is not { } roleId || roleId == 0)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid invitation request",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CreatedInvitation created;
        try
        {
            created = await invitations
                .CreateInvitationAsync(
                    projectId.Value,
                    roleId,
                    request.ExpiresInHours,
                    request.MaxUses ?? 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteInvitationAsync(context, created.Token, created.Info, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of InvitationHandlers.Get (invitation.go:92-101).</summary>
    public static async Task GetAsync(
        HttpContext context,
        string token,
        InvitationService invitations)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invitations);

        var cancellationToken = context.RequestAborted;

        InvitationInfo info;
        try
        {
            info = await invitations.GetInvitationAsync(token, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteInvitationAsync(context, token: null, info, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of InvitationHandlers.Register (invitation.go:103-127).</summary>
    public static async Task RegisterAsync(
        HttpContext context,
        string token,
        InvitationService invitations,
        ManagementAuthService auth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(auth);

        var cancellationToken = context.RequestAborted;

        RegisterInvitationRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RegisterInvitationRequest>(
                    context.Request.Body,
                    RequestJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        // `binding:"required,email"` and `binding:"required,min=7"`: the
        // validator counts password runes, not bytes.
        if (request is null
            || string.IsNullOrEmpty(request.Email)
            || !ManagementAuthHandlers.EmailRegex().IsMatch(request.Email)
            || string.IsNullOrEmpty(request.Password)
            || RuneCount(request.Password) < 7)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid registration request",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        RegisteredInvitation registered;
        try
        {
            registered = await invitations
                .RegisterInvitationAsync(
                    token,
                    request.Email,
                    request.Password,
                    request.FirstName ?? string.Empty,
                    request.LastName ?? string.Empty,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string sessionToken;
        try
        {
            sessionToken = await auth.GenerateJwtTokenAsync(registered.User, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "failed to create session",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(
                new
                {
                    user = ManagementAuthHandlers.UserRecordDto.From(registered.Profile),
                    token = sessionToken,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of api.CreateInvitationRequest (invitation.go:39-43).</summary>
    public sealed record CreateInvitationRequest(
        int? ExpiresInHours,
        int? MaxUses,
        int? RoleID);

    /// <summary>Port of api.RegisterInvitationRequest (invitation.go:56-61).</summary>
    public sealed record RegisterInvitationRequest(
        string? Email,
        string? Password,
        string? FirstName,
        string? LastName);

    /// <summary>The three invitation routes (routes.go:87,88,112).</summary>
    public static IEndpointRouteBuilder MapInvitationRoutes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/admin/invitations", CreateAsync);
        endpoints.MapGet("/auth/invitations/{token}", GetAsync);
        endpoints.MapPost("/auth/invitations/{token}/register", RegisterAsync);
        return endpoints;
    }

    /// <summary>
    /// Writes api.InvitationResponse (invitation.go:46-53): an empty token is
    /// omitted (`token,omitempty`), and expiresAt is the Go time rendering or
    /// null.
    /// </summary>
    private static async Task WriteInvitationAsync(
        HttpContext context,
        string? token,
        InvitationInfo info,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(token))
            {
                writer.WriteString("token", token);
            }

            writer.WriteString("projectName", info.ProjectName);
            if (info.ExpiresAt is { } expiresAt)
            {
                writer.WriteString("expiresAt", GoTime.Rfc3339Nano(expiresAt));
            }
            else
            {
                writer.WriteNull("expiresAt");
            }

            writer.WriteNumber("maxUses", info.MaxUses);
            writer.WriteNumber("usedCount", info.UsedCount);
            writer.WriteNumber("remainingUses", info.RemainingUses);
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static int RuneCount(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}
