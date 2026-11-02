using HotChocolate;
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
}
