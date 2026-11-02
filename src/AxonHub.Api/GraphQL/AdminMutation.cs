using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the system-settings mutations of mutationResolver
/// (internal/server/gql/system.resolvers.go:60-67, 80-100, 212-236).
/// </summary>
public sealed class AdminMutation
{
    /// <summary>Port of mutationResolver.UpdateRetryPolicy.</summary>
    [GraphQLName("updateRetryPolicy")]
    public async Task<bool> UpdateRetryPolicyAsync(
        UpdateRetryPolicyInput input,
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await settings.SetRetryPolicyAsync(
                    SystemSettingsMapping.ToDomain(input),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // fmt.Errorf("failed to update retry policy: %w", err)
            throw new InvalidOperationException(
                "failed to update retry policy: " + ex.Message,
                ex);
        }

        return true;
    }

    /// <summary>Port of mutationResolver.UpdateSystemModelSettings.</summary>
    [GraphQLName("updateSystemModelSettings")]
    public async Task<bool> UpdateSystemModelSettingsAsync(
        UpdateSystemModelSettingsInput input,
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        var modelSettings = SystemSettingsMapping.ToDomain(input);

        // Older clients may update the toggles without sending developer
        // rules; preserve the stored list unless the caller sends one.
        if (input.DeveloperSettings is null)
        {
            ModelSettings current;
            try
            {
                current = await settings.GetModelSettingsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get current system model settings: " + ex.Message,
                    ex);
            }

            modelSettings = modelSettings with { DeveloperSettings = current.DeveloperSettings };
        }

        try
        {
            await settings.SetModelSettingsAsync(modelSettings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to update system model settings: " + ex.Message,
                ex);
        }

        return true;
    }

    /// <summary>Port of mutationResolver.UpdateSecuritySettings.</summary>
    [GraphQLName("updateSecuritySettings")]
    public async Task<bool> UpdateSecuritySettingsAsync(
        UpdateSecuritySettingsInput input,
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        SecuritySettings current;
        try
        {
            current = await settings.GetSecuritySettingsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to read current security settings: " + ex.Message,
                ex);
        }

        var updated = current with
        {
            BlockedIps = input.BlockedIps?.ToList() ?? current.BlockedIps,
            ShowRequestLogIpBanIcon =
                input.ShowRequestLogIpBanIcon ?? current.ShowRequestLogIpBanIcon,
        };

        try
        {
            await settings.SetSecuritySettingsAsync(updated, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to update security settings: " + ex.Message,
                ex);
        }

        return true;
    }

    /// <summary>Port of mutationResolver.CreateUser (axonhub.resolvers.go:526-528).</summary>
    [GraphQLName("createUser")]
    public async Task<UserType> CreateUserAsync(
        CreateUserInput input,
        [Service] UserManagementService users,
        CancellationToken cancellationToken)
    {
        var user = await users.CreateAsync(
                new CreateUserCommand
                {
                    Email = input.Email,
                    Password = input.Password,
                    FirstName = input.FirstName,
                    LastName = input.LastName,
                    Scopes = input.Scopes?.ToList(),
                    RoleIds = ManagementMapping.ParseGidList(input.RoleIDs),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.ToGraphQL(user);
    }

    /// <summary>Port of mutationResolver.UpdateUser (axonhub.resolvers.go:531-533).</summary>
    [GraphQLName("updateUser")]
    public async Task<UserType> UpdateUserAsync(
        [GraphQLType(typeof(IdType))] string id,
        UpdateUserInput input,
        [Service] UserManagementService users,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);

        var user = await users.UpdateAsync(
                new UpdateUserCommand
                {
                    Id = gid.Id,
                    Email = input.Email,
                    FirstName = input.FirstName,
                    LastName = input.LastName,
                    PreferLanguage = input.PreferLanguage,
                    Avatar = input.Avatar,
                    ClearAvatar = input.ClearAvatar ?? false,
                    IsOwner = input.IsOwner,
                    Password = input.Password,
                    Scopes = input.Scopes?.ToList(),
                    AppendScopes = input.AppendScopes?.ToList(),
                    ClearScopes = input.ClearScopes ?? false,
                    AddRoleIds = ManagementMapping.ParseGidList(input.AddRoleIDs),
                    RemoveRoleIds = ManagementMapping.ParseGidList(input.RemoveRoleIDs),
                    ClearRoles = input.ClearRoles ?? false,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.ToGraphQL(user);
    }

    /// <summary>
    /// Port of mutationResolver.UpdateUserStatus (axonhub.resolvers.go:536-538).
    /// </summary>
    [GraphQLName("updateUserStatus")]
    public async Task<UserType> UpdateUserStatusAsync(
        [GraphQLType(typeof(IdType))] string id,
        UserStatusValue status,
        [Service] UserManagementService users,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);

        var user = await users.UpdateStatusAsync(
                gid.Id,
                status == UserStatusValue.Activated ? "activated" : "deactivated",
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.ToGraphQL(user);
    }

    /// <summary>Port of mutationResolver.DeleteUser (axonhub.resolvers.go:541-547).</summary>
    [GraphQLName("deleteUser")]
    public async Task<bool> DeleteUserAsync(
        [GraphQLType(typeof(IdType))] string id,
        [Service] UserManagementService users,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);
        await users.DeleteAsync(gid.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.CreateRole (axonhub.resolvers.go:550-552).</summary>
    [GraphQLName("createRole")]
    public async Task<RoleType> CreateRoleAsync(
        CreateRoleInput input,
        [Service] RoleManagementService roles,
        CancellationToken cancellationToken)
    {
        var role = await roles.CreateAsync(
                new CreateRoleCommand
                {
                    Name = input.Name,
                    Level = input.Level is null
                        ? null
                        : input.Level == RoleLevelValue.System
                            ? RoleManagementService.LevelSystem
                            : RoleManagementService.LevelProject,
                    Scopes = input.Scopes?.ToList(),
                    ProjectId = input.ProjectID is null
                        ? null
                        : GidValue.Parse(input.ProjectID).Id,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.ToGraphQL(role);
    }

    /// <summary>Port of mutationResolver.UpdateRole (axonhub.resolvers.go:555-557).</summary>
    [GraphQLName("updateRole")]
    public async Task<RoleType> UpdateRoleAsync(
        [GraphQLType(typeof(IdType))] string id,
        UpdateRoleInput input,
        [Service] RoleManagementService roles,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);

        var role = await roles.UpdateAsync(
                new UpdateRoleCommand
                {
                    Id = gid.Id,
                    Name = input.Name,
                    Scopes = input.Scopes?.ToList(),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.ToGraphQL(role);
    }

    /// <summary>Port of mutationResolver.DeleteRole (axonhub.resolvers.go:560-567).</summary>
    [GraphQLName("deleteRole")]
    public async Task<bool> DeleteRoleAsync(
        [GraphQLType(typeof(IdType))] string id,
        [Service] RoleManagementService roles,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);
        await roles.DeleteAsync(gid.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Port of mutationResolver.BulkDeleteRoles (axonhub.resolvers.go:570-579).
    /// </summary>
    [GraphQLName("bulkDeleteRoles")]
    public async Task<bool> BulkDeleteRolesAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [Service] RoleManagementService roles,
        CancellationToken cancellationToken)
    {
        var roleIds = ManagementMapping.ParseGidList(ids)
            ?? throw new InvalidOperationException("ids is required");

        await roles.BulkDeleteAsync(roleIds, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Port of mutationResolver.UpdateChannelStatus
    /// (axonhub.resolvers.go:203-206; biz channel.go:1205-1220).
    /// </summary>
    [GraphQLName("updateChannelStatus")]
    public async Task<ChannelType> UpdateChannelStatusAsync(
        [GraphQLType(typeof(IdType))] string id,
        ChannelStatusValue status,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);

        var channel = await channels.UpdateStatusAsync(
                gid.Id,
                ChannelMapping.ToWire(status),
                cancellationToken)
            .ConfigureAwait(false);

        return ChannelMapping.ToGraphQL(channel);
    }

    /// <summary>
    /// Port of mutationResolver.DeleteChannel
    /// (axonhub.resolvers.go:208-215; biz channel.go:1280-1290).
    /// </summary>
    [GraphQLName("deleteChannel")]
    public async Task<bool> DeleteChannelAsync(
        [GraphQLType(typeof(IdType))] string id,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);
        await channels.DeleteAsync(gid.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkArchiveChannels (axonhub.resolvers.go:217-226).</summary>
    [GraphQLName("bulkArchiveChannels")]
    public async Task<bool> BulkArchiveChannelsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkArchiveAsync(ParseGids(ids), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkDisableChannels (axonhub.resolvers.go:228-237).</summary>
    [GraphQLName("bulkDisableChannels")]
    public async Task<bool> BulkDisableChannelsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkDisableAsync(ParseGids(ids), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkEnableChannels (axonhub.resolvers.go:239-248).</summary>
    [GraphQLName("bulkEnableChannels")]
    public async Task<bool> BulkEnableChannelsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkEnableAsync(ParseGids(ids), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkRecoverChannels (axonhub.resolvers.go:250-259).</summary>
    [GraphQLName("bulkRecoverChannels")]
    public async Task<bool> BulkRecoverChannelsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkRecoverAsync(ParseGids(ids), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkDeleteChannels (axonhub.resolvers.go:261-270).</summary>
    [GraphQLName("bulkDeleteChannels")]
    public async Task<bool> BulkDeleteChannelsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkDeleteAsync(ParseGids(ids), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkAddChannelTags (axonhub.resolvers.go:272-281).</summary>
    [GraphQLName("bulkAddChannelTags")]
    public async Task<bool> BulkAddChannelTagsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [GraphQLType(typeof(ListType<NonNullType<StringType>>))] IReadOnlyList<string> tags,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkAddTagsAsync(ParseGids(ids), tags, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkRemoveChannelTags (axonhub.resolvers.go:283-292).</summary>
    [GraphQLName("bulkRemoveChannelTags")]
    public async Task<bool> BulkRemoveChannelTagsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [GraphQLType(typeof(ListType<NonNullType<StringType>>))] IReadOnlyList<string> tags,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkRemoveTagsAsync(ParseGids(ids), tags, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of mutationResolver.BulkManageChannelTags (axonhub.resolvers.go:294-303).</summary>
    [GraphQLName("bulkManageChannelTags")]
    public async Task<bool> BulkManageChannelTagsAsync(
        [GraphQLType(typeof(ListType<NonNullType<IdType>>))] IReadOnlyList<string> ids,
        [GraphQLType(typeof(ListType<NonNullType<StringType>>))] IReadOnlyList<string> addTags,
        [GraphQLType(typeof(ListType<NonNullType<StringType>>))] IReadOnlyList<string> removeTags,
        [Service] ChannelLifecycleService channels,
        CancellationToken cancellationToken)
    {
        await channels.BulkManageTagsAsync(
                ParseGids(ids),
                addTags,
                removeTags,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static List<long> ParseGids(IReadOnlyList<string> ids)
    {
        var result = new List<long>(ids.Count);
        foreach (var id in ids)
        {
            result.Add(GidValue.Parse(id).Id);
        }

        return result;
    }
}
