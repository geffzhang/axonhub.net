using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>UserInfo</c> type (me.graphql:1-16).</summary>
/// <remarks>
/// The reference returns <c>[String!]!</c> for scopes, roles, projects and
/// oidcIdentities; the collections are always materialized, so an empty list is
/// sent as <c>[]</c>.
/// </remarks>
[GraphQLName("UserInfo")]
public sealed class UserInfoType
{
    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;

    public bool IsOwner { get; init; }

    public string PreferLanguage { get; init; } = string.Empty;

    /// <summary>
    /// ConvertUserToUserInfo stores the address of the user's avatar string, so
    /// the value is never null even though the schema type is nullable.
    /// </summary>
    public string? Avatar { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public IReadOnlyList<RoleInfoType> Roles { get; init; } = [];

    public IReadOnlyList<UserProjectInfoType> Projects { get; init; } = [];

    public IReadOnlyList<OidcIdentityInfoType> OidcIdentities { get; init; } = [];

    public bool HasPassword { get; init; }
}

/// <summary>Port of the gqlgen <c>RoleInfo</c> type (me.graphql:24-26).</summary>
[GraphQLName("RoleInfo")]
public sealed class RoleInfoType
{
    public string Name { get; init; } = string.Empty;
}

/// <summary>Port of the gqlgen <c>UserProjectInfo</c> type (me.graphql:28-34).</summary>
[GraphQLName("UserProjectInfo")]
public sealed class UserProjectInfoType
{
    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectId { get; init; } = string.Empty;

    public bool IsOwner { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public IReadOnlyList<string> EffectiveScopes { get; init; } = [];

    public IReadOnlyList<RoleInfoType> Roles { get; init; } = [];
}

/// <summary>Port of the gqlgen <c>OIDCIdentityInfo</c> type (me.graphql:18-24).</summary>
[GraphQLName("OIDCIdentityInfo")]
public sealed class OidcIdentityInfoType
{
    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    public string IdpName { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;
}

/// <summary>Port of the gqlgen <c>ProjectStatus</c> enum (ent.graphql:3187-3190).</summary>
[GraphQLName("ProjectStatus")]
public enum ProjectStatusValue
{
    [GraphQLName("active")]
    Active,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>
/// Port of the scalar surface of the entgql <c>Project</c> type
/// (ent.graphql:2836-2852). The edge fields are split between
/// <see cref="ProjectTypeExtensions"/> (the nine connections and the
/// projectUsers list) and <c>GetProfiles</c>/profiles wiring (§86).
/// </summary>
[GraphQLName("Project")]
public sealed class ProjectType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public ProjectStatusValue Status { get; init; }

    /// <summary>
    /// Port of the entgql <c>Project.profiles</c> field (ent.graphql:2852). The
    /// column is nullable jsonb, so a NULL column is serialized as null.
    /// </summary>
    [GraphQLType(typeof(ProjectProfilesType))]
    public ProjectProfilesType? Profiles { get; init; }
}

/// <summary>Port of gql's <c>UpdateMeInput</c> (me.graphql:31-36).</summary>
public sealed class UpdateMeInput
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? PreferLanguage { get; set; }

    public string? Avatar { get; set; }
}

/// <summary>Port of gql's <c>UpdateMyPasswordInput</c> (me.graphql:47-50).</summary>
public sealed class UpdateMyPasswordInput
{
    public string? OldPassword { get; set; }

    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>Domain ↔ GraphQL mapping for the self-service slice.</summary>
public static class UserProfileMapping
{
    public static UserInfoType ToGraphQL(UserProfileInfo info) => new()
    {
        Id = new GidValue("User", info.Id).ToString(),
        Email = info.Email,
        FirstName = info.FirstName,
        LastName = info.LastName,
        IsOwner = info.IsOwner,
        PreferLanguage = info.PreferLanguage,
        Avatar = info.Avatar,
        Scopes = info.Scopes,
        Roles = [.. info.Roles.Select(static role => new RoleInfoType { Name = role.Name })],
        Projects =
        [
            .. info.Projects.Select(static project => new UserProjectInfoType
            {
                ProjectId = new GidValue("Project", project.ProjectId).ToString(),
                IsOwner = project.IsOwner,
                Scopes = project.Scopes,
                EffectiveScopes = project.EffectiveScopes,
                Roles =
                [
                    .. project.Roles.Select(static role => new RoleInfoType
                    {
                        Name = role.Name,
                    }),
                ],
            }),
        ],
        OidcIdentities =
        [
            .. info.OidcIdentities.Select(static identity => new OidcIdentityInfoType
            {
                Id = new GidValue("OIDCIdentity", identity.Id).ToString(),
                IdpName = identity.IdpName,
                Issuer = identity.Issuer,
                Subject = identity.Subject,
                Email = identity.Email,
            }),
        ],
        HasPassword = info.HasPassword,
    };

    public static ProjectType ToGraphQL(ProjectRecord record) => new()
    {
        DbId = record.Id,
        Id = new GidValue("Project", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        Name = record.Name,
        Description = record.Description,
        Status = ToGraphQLStatus(record.Status),
        Profiles = ProjectMapping.ToGraphQL(record.Profiles),
    };

    public static ProjectStatusValue ToGraphQLStatus(string status) => status switch
    {
        "active" => ProjectStatusValue.Active,
        "archived" => ProjectStatusValue.Archived,
        _ => throw new GraphQLException(
            "Enum \"ProjectStatus\" cannot represent value: \"" + status + "\""),
    };
}
