using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of objects.ProjectProfiles (internal/objects/project.go:3-6).</summary>
public sealed record ProjectProfilesModel
{
    public string ActiveProfile { get; init; } = string.Empty;

    /// <summary>Nil (JSON null) and empty (JSON []) are distinct on the wire.</summary>
    public IReadOnlyList<ProjectProfileModel>? Profiles { get; init; }
}

/// <summary>Port of objects.ProjectProfile (internal/objects/project.go:8-13).</summary>
public sealed record ProjectProfileModel
{
    public string Name { get; init; } = string.Empty;

    /// <summary>JSON omitempty: nil or empty both disappear from the payload.</summary>
    public IReadOnlyList<int>? ChannelIDs { get; init; }

    /// <summary>JSON omitempty: nil or empty both disappear from the payload.</summary>
    public IReadOnlyList<string>? ChannelTags { get; init; }

    /// <summary>objects.ChannelTagsMatchMode: "" / any / all / none.</summary>
    public string ChannelTagsMatchMode { get; init; } = string.Empty;
}

/// <summary>
/// Go-compatible codec for the <c>projects.profiles</c> jsonb column. Go
/// marshals objects.ProjectProfiles with encoding/json: fixed field order,
/// omitempty on the per-profile optional fields, HTML escaping and lowercase
/// control escapes.
/// </summary>
public static class ProjectProfilesCodec
{
    /// <summary>Map of the reference's ent create default (Project.Profiles).</summary>
    public const string DefaultJson = "{\"activeProfile\":\"\",\"profiles\":null}";

    public static string Serialize(ProjectProfilesModel profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("activeProfile", profiles.ActiveProfile);

            writer.WritePropertyName("profiles");
            if (profiles.Profiles is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var profile in profiles.Profiles)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", profile.Name);

                    if (profile.ChannelIDs is { Count: > 0 })
                    {
                        writer.WritePropertyName("channelIDs");
                        writer.WriteStartArray();
                        foreach (var id in profile.ChannelIDs)
                        {
                            writer.WriteNumberValue(id);
                        }

                        writer.WriteEndArray();
                    }

                    if (profile.ChannelTags is { Count: > 0 })
                    {
                        writer.WritePropertyName("channelTags");
                        writer.WriteStartArray();
                        foreach (var tag in profile.ChannelTags)
                        {
                            writer.WriteStringValue(tag);
                        }

                        writer.WriteEndArray();
                    }

                    if (!string.IsNullOrEmpty(profile.ChannelTagsMatchMode))
                    {
                        writer.WriteString("channelTagsMatchMode", profile.ChannelTagsMatchMode);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    public static ProjectProfilesModel Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new ProjectProfilesModel();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var activeProfile = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("activeProfile", out var activeElement)
            && activeElement.ValueKind == JsonValueKind.String
                ? activeElement.GetString() ?? string.Empty
                : string.Empty;

        IReadOnlyList<ProjectProfileModel>? profiles = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("profiles", out var profilesElement)
            && profilesElement.ValueKind == JsonValueKind.Array)
        {
            profiles = [.. profilesElement.EnumerateArray().Select(ReadProfile)];
        }

        return new ProjectProfilesModel
        {
            ActiveProfile = activeProfile,
            Profiles = profiles,
        };
    }

    /// <summary>Port of ValidateProjectProfiles (biz/project.go:259-298).</summary>
    public static void Validate(ProjectProfilesModel profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = profiles.Profiles ?? [];

        foreach (var profile in list)
        {
            var nameLower = profile.Name.Trim().ToLowerInvariant();
            if (nameLower.Length == 0)
            {
                throw new InvalidOperationException("profile name cannot be empty");
            }

            if (!seen.Add(nameLower))
            {
                throw new InvalidOperationException("duplicate profile name: " + profile.Name);
            }

            if (!IsValidMatchMode(profile.ChannelTagsMatchMode))
            {
                throw new InvalidOperationException(
                    "profile '" + profile.Name + "' channelTagsMatchMode is invalid");
            }
        }

        if (profiles.ActiveProfile.Length > 0
            && !list.Any(profile => profile.Name == profiles.ActiveProfile))
        {
            throw new InvalidOperationException(
                "active profile '" + profiles.ActiveProfile
                + "' does not exist in the profiles list");
        }
    }

    private static bool IsValidMatchMode(string mode)
        => mode is "" or "any" or "all" or "none";

    private static ProjectProfileModel ReadProfile(JsonElement element)
    {
        var name = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

        IReadOnlyList<int>? channelIds = null;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("channelIDs", out var idsElement)
            && idsElement.ValueKind == JsonValueKind.Array)
        {
            channelIds = [.. idsElement.EnumerateArray().Select(static id => id.GetInt32())];
        }

        IReadOnlyList<string>? channelTags = null;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("channelTags", out var tagsElement)
            && tagsElement.ValueKind == JsonValueKind.Array)
        {
            channelTags = [.. tagsElement.EnumerateArray().Select(static tag => tag.GetString()!)];
        }

        var matchMode = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("channelTagsMatchMode", out var modeElement)
            && modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString() ?? string.Empty
                : string.Empty;

        return new ProjectProfileModel
        {
            Name = name,
            ChannelIDs = channelIds,
            ChannelTags = channelTags,
            ChannelTagsMatchMode = matchMode,
        };
    }
}

/// <summary>Port of the fields ProjectService.CreateProject reads from ent.CreateProjectInput.</summary>
public sealed record CreateProjectCommand
{
    public required string Name { get; init; }

    public string? Description { get; init; }
}

/// <summary>Port of the fields ProjectService.UpdateProject applies from ent.UpdateProjectInput.</summary>
public sealed record UpdateProjectCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public bool ClearUsers { get; init; }

    public IReadOnlyList<long>? AddUserIds { get; init; }

    public IReadOnlyList<long>? RemoveUserIds { get; init; }
}

/// <summary>Port of objects.ProjectProfiles passed to UpdateProjectProfiles.</summary>
public sealed record UpdateProjectProfilesCommand
{
    public required long Id { get; init; }

    public required ProjectProfilesModel Profiles { get; init; }
}

/// <summary>
/// Port of the management methods of biz.ProjectService
/// (internal/server/biz/project.go:51-385) over <see cref="IProjectStore"/>.
/// CreateProject intentionally runs without a transaction: the reference
/// persists each step independently, so a later failure leaves earlier rows
/// behind. The project cache is not ported (C174).
/// </summary>
public sealed class ProjectManagementService(
    IProjectStore projects,
    IRoleStore roles,
    IManagementIdentity identity,
    PermissionValidator validator,
    ProjectPrivacy privacy,
    RolePrivacy rolePrivacy,
    UserProjectPrivacy userProjectPrivacy)
{
    public const string ProjectNotFound = "ent: project not found";

    public const string DefaultProjectName = "default";

    public const string AdminRoleName = "Admin";

    public const string DeveloperRoleName = "Developer";

    public const string ViewerRoleName = "Viewer";

    private static readonly string[] AdminScopes =
    [
        "read_users",
        "write_users",
        "read_roles",
        "write_roles",
        "read_api_keys",
        "write_api_keys",
        "read_requests",
        "write_requests",
        "read_prompts",
        "write_prompts",
    ];

    private static readonly string[] DeveloperScopes =
    [
        "read_api_keys",
        "write_api_keys",
        "read_requests",
        "write_requests",
    ];

    private static readonly string[] ViewerScopes =
    [
        "read_prompts",
        "read_requests",
    ];

    /// <summary>Port of ProjectService.CreateProject (project.go:51-157).</summary>
    public async Task<ProjectRecord> CreateAsync(
        CreateProjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var currentUser = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentUser is null)
        {
            throw new InvalidOperationException(PermissionValidator.UserNotFoundInContext);
        }

        return await CreateForUserAsync(command, currentUser.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of ProjectService.CreateProject with the creator supplied directly.
    /// The reference reads it from <c>contexts.GetUserFromContext</c>; the
    /// initialize transaction has no request user and installs the owner it
    /// just created (biz/system.go:780-789).
    /// </summary>
    /// <param name="bypassPrivacy">
    /// The initialize path runs under <c>authz.WithSystemBypass</c>
    /// (biz/system.go:723), so every policy below is skipped. The request path
    /// leaves this false and evaluates them exactly like ent.
    /// </param>
    public async Task<ProjectRecord> CreateForUserAsync(
        CreateProjectCommand command,
        long currentUserId,
        CancellationToken cancellationToken = default,
        bool bypassPrivacy = false)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool exists;
        try
        {
            // client.Project.Query().Where(NameEQ).Exist runs the Project query
            // policy (project.go:62-67); WhereID(ctx) narrows the check to the
            // request's project.
            long? nameScope = null;
            if (!bypassPrivacy)
            {
                nameScope = await privacy.EvaluateQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            exists = await projects
                .NameExistsAsync(command.Name, cancellationToken, idScope: nameScope)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to check project name uniqueness: " + ex.Message,
                ex);
        }

        if (exists)
        {
            throw DuplicateName(command.Name);
        }

        var now = DateTime.UtcNow;
        var project = new ProjectRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Name = command.Name,
            // Ent create defaults: description "", status "active", profiles
            // objects.ProjectProfiles{}.
            Description = command.Description ?? string.Empty,
            Status = "active",
            Profiles = ProjectProfilesCodec.DefaultJson,
        };

        long id;
        try
        {
            // client.Project.Create().Save runs the Project mutation policy
            // (project.go:81-84).
            if (!bypassPrivacy)
            {
                await privacy
                    .EvaluateMutationAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            id = await projects.CreateAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to create project: " + ex.Message, ex);
        }

        project.Id = id;

        // Project-level role presets, created in order with no transaction.
        await CreateRoleAsync(
                project.Id,
                AdminRoleName,
                AdminScopes,
                "failed to create admin role: ",
                cancellationToken,
                bypassPrivacy)
            .ConfigureAwait(false);
        await CreateRoleAsync(
                project.Id,
                DeveloperRoleName,
                DeveloperScopes,
                "failed to create developer role: ",
                cancellationToken,
                bypassPrivacy)
            .ConfigureAwait(false);
        await CreateRoleAsync(
                project.Id,
                ViewerRoleName,
                ViewerScopes,
                "failed to create viewer role: ",
                cancellationToken,
                bypassPrivacy)
            .ConfigureAwait(false);

        try
        {
            // client.UserProject.Create().Save runs the UserProject mutation
            // policy (project.go:146-154).
            if (!bypassPrivacy)
            {
                await userProjectPrivacy
                    .EvaluateMutationAsync(
                        UserProjectMutationOp.Create,
                        project.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await projects.AssignOwnerAsync(currentUserId, project.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's wording.
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to assign user to project: " + ex.Message,
                ex);
        }

        return project;
    }

    /// <summary>Port of ProjectService.UpdateProject (project.go:159-188).</summary>
    public async Task<ProjectRecord> UpdateAsync(
        UpdateProjectCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ProjectRecord? updated;
        try
        {
            // client.Project.UpdateOneID(id).Save runs the Project mutation
            // policy (project.go:163-182).
            await privacy
                .EvaluateMutationAsync(cancellationToken)
                .ConfigureAwait(false);
            updated = await projects.UpdateAsync(
                    new ProjectUpdate
                    {
                        Id = command.Id,
                        Name = command.Name,
                        Description = command.Description,
                        ClearUsers = command.ClearUsers,
                        AddedUserIds = command.AddUserIds,
                        RemovedUserIds = command.RemoveUserIds,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fmt.Errorf("failed to update project: %w", err)
            throw new InvalidOperationException("failed to update project: " + ex.Message, ex);
        }

        return updated
            ?? throw new InvalidOperationException("failed to update project: " + ProjectNotFound);
    }

    /// <summary>Port of ProjectService.UpdateProjectStatus (project.go:300-315).</summary>
    public async Task<ProjectRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        ProjectRecord? updated;
        try
        {
            // client.Project.UpdateOneID(id).Save runs the Project mutation
            // policy (project.go:304-309).
            await privacy
                .EvaluateMutationAsync(cancellationToken)
                .ConfigureAwait(false);
            updated = await projects.UpdateStatusAsync(id, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update project status: " + ex.Message,
                ex);
        }

        return updated
            ?? throw new InvalidOperationException(
                "failed to update project status: " + ProjectNotFound);
    }

    /// <summary>Port of ProjectService.UpdateProjectProfiles (project.go:236-256).</summary>
    public async Task<ProjectRecord> UpdateProfilesAsync(
        UpdateProjectProfilesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validation errors are returned unwrapped (project.go:239-241).
        ProjectProfilesCodec.Validate(command.Profiles);

        var json = ProjectProfilesCodec.Serialize(command.Profiles);

        ProjectRecord? updated;
        try
        {
            // client.Project.UpdateOneID(id).SetProfiles().Save runs the
            // Project mutation policy (project.go:245-250).
            await privacy
                .EvaluateMutationAsync(cancellationToken)
                .ConfigureAwait(false);
            updated = await projects.UpdateProfilesAsync(command.Id, json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update project profiles: " + ex.Message,
                ex);
        }

        return updated
            ?? throw new InvalidOperationException(
                "failed to update project profiles: " + ProjectNotFound);
    }

    /// <summary>Port of ProjectService.DeleteProject (project.go:335-385).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            await validator.CanDeleteProjectAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("permission denied: " + ex.Message, ex);
        }

        // The validator admits system owners only; every policy below is
        // therefore allowed by OwnerRule. client.Project.Get still runs the
        // Project query policy (project.go:345), whose WhereID(ctx) filters a
        // header-scoped owner to that one project.
        long? queryScope;
        try
        {
            queryScope = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get project: " + ex.Message, ex);
        }

        bool deleted;
        try
        {
            deleted = await projects
                .DeleteAsync(id, cancellationToken, idScope: queryScope)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's per-step wording.
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (!deleted)
        {
            throw new InvalidOperationException("failed to get project: " + ProjectNotFound);
        }
    }

    public async Task<ListPage<ProjectRecord>> ListAsync(
        ProjectListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The Project query policy installs WhereID(ctx) before the page runs.
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (projectScope is not null)
        {
            var scope = new ProjectListFilter { Id = projectScope.Value };
            query = query with
            {
                Where = query.Where is null
                    ? scope
                    : new ProjectListFilter { And = [query.Where, scope] },
            };
        }

        return await projects.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateRoleAsync(
        long projectId,
        string name,
        IReadOnlyList<string> scopes,
        string failurePrefix,
        CancellationToken cancellationToken,
        bool bypassPrivacy)
    {
        var now = DateTime.UtcNow;
        var role = new RoleRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Name = name,
            Level = RoleManagementService.LevelProject,
            Scopes = JsonSerializer.Serialize(scopes),
            ProjectId = projectId,
        };

        try
        {
            // client.Role.Create().Save runs the Role mutation policy
            // (project.go:101-106).
            if (!bypassPrivacy)
            {
                await rolePrivacy
                    .EvaluateMutationAsync(RoleMutationOp.Create, projectId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await roles.CreateAsync(role, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(failurePrefix + ex.Message, ex);
        }
    }

    /// <summary>Port of xerrors.DuplicateNameError("project", name).</summary>
    private static CodedException DuplicateName(string name) => new(
        CodedException.DuplicateName,
        $"project name '{name}' already exists",
        new Dictionary<string, object?>
        {
            ["resource"] = "project",
            ["field"] = "name",
            ["value"] = name,
        });
}
