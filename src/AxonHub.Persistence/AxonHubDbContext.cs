using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;

namespace AxonHub.Persistence;

/// <summary>
/// EF Core model for the ported tables. The write side of the persistence layer
/// goes through this context so <c>SaveChanges</c> owns aggregate consistency.
/// The schema itself is still created by <see cref="DatabaseMigrator"/>'s
/// idempotent DDL (the reference applies Ent's auto-migration on every startup
/// and keeps no version table); this model is kept structurally identical and is
/// verified against the migrated database by <c>EfCoreModelTests</c>.
/// </summary>
/// <remarks>
/// Read-side queries stay on Dapper. Both sides can share one connection and
/// transaction: <c>Database.GetDbConnection()</c> and
/// <c>Database.CurrentTransaction.GetDbTransaction()</c> are the same objects
/// Dapper needs.
/// </remarks>
public sealed class AxonHubDbContext(DbContextOptions<AxonHubDbContext> options) : DbContext(options)
{
    public DbSet<ChannelRecord> Channels => Set<ChannelRecord>();

    public DbSet<ChannelModelPriceRecord> ChannelModelPrices => Set<ChannelModelPriceRecord>();

    public DbSet<ChannelModelPriceVersionRecord> ChannelModelPriceVersions => Set<ChannelModelPriceVersionRecord>();

    public DbSet<ChannelOverrideTemplateRecord> ChannelOverrideTemplates => Set<ChannelOverrideTemplateRecord>();

    public DbSet<ChannelProbeRecord> ChannelProbes => Set<ChannelProbeRecord>();

    public DbSet<ModelRecord> Models => Set<ModelRecord>();

    public DbSet<RequestRecord> Requests => Set<RequestRecord>();

    public DbSet<RequestExecutionRecord> RequestExecutions => Set<RequestExecutionRecord>();

    public DbSet<UsageLogRecord> UsageLogs => Set<UsageLogRecord>();

    public DbSet<UserRecord> Users => Set<UserRecord>();

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();

    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    public DbSet<ApiKeyProfileTemplateRecord> ApiKeyProfileTemplates => Set<ApiKeyProfileTemplateRecord>();

    public DbSet<SystemRecord> Systems => Set<SystemRecord>();

    public DbSet<PromptRecord> Prompts => Set<PromptRecord>();

    public DbSet<DataStorageRecord> DataStorages => Set<DataStorageRecord>();

    public DbSet<PromptProtectionRuleRecord> PromptProtectionRules => Set<PromptProtectionRuleRecord>();

    public DbSet<TraceRecord> Traces => Set<TraceRecord>();

    public DbSet<ThreadRecord> Threads => Set<ThreadRecord>();

    public DbSet<RoleRecord> Roles => Set<RoleRecord>();

    public DbSet<UserRoleRecord> UserRoles => Set<UserRoleRecord>();

    public DbSet<UserProjectRecord> UserProjects => Set<UserProjectRecord>();

    public DbSet<OidcIdentityRecord> OidcIdentities => Set<OidcIdentityRecord>();

    public DbSet<InvitationRecord> Invitations => Set<InvitationRecord>();

    public DbSet<ProviderQuotaStatusRecord> ProviderQuotaStatuses => Set<ProviderQuotaStatusRecord>();

    /// <summary>The transaction Dapper must enlist in, or null when none is open.</summary>
    public DbTransaction? CurrentDbTransaction => Database.CurrentTransaction?.GetDbTransaction();

    /// <summary>
    /// Opens the context connection if needed and returns it for Dapper. Inside a
    /// transaction this is the same physical connection, so reads see the
    /// uncommitted writes of the enclosing unit of work.
    /// </summary>
    public async Task<DbConnection> OpenDapperConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureChannel(modelBuilder.Entity<ChannelRecord>());
        ConfigureChannelModelPrice(modelBuilder.Entity<ChannelModelPriceRecord>());
        ConfigureChannelModelPriceVersion(modelBuilder.Entity<ChannelModelPriceVersionRecord>());
        ConfigureChannelOverrideTemplate(modelBuilder.Entity<ChannelOverrideTemplateRecord>());
        ConfigureChannelProbe(modelBuilder.Entity<ChannelProbeRecord>());
        ConfigureModel(modelBuilder.Entity<ModelRecord>());
        ConfigureRequest(modelBuilder.Entity<RequestRecord>());
        ConfigureRequestExecution(modelBuilder.Entity<RequestExecutionRecord>());
        ConfigureUsageLog(modelBuilder.Entity<UsageLogRecord>());
        ConfigureUser(modelBuilder.Entity<UserRecord>());
        ConfigureProject(modelBuilder.Entity<ProjectRecord>());
        ConfigureApiKey(modelBuilder.Entity<ApiKeyRecord>());
        ConfigureApiKeyProfileTemplate(modelBuilder.Entity<ApiKeyProfileTemplateRecord>());
        ConfigureSystem(modelBuilder.Entity<SystemRecord>());
        ConfigurePrompt(modelBuilder.Entity<PromptRecord>());
        ConfigureDataStorage(modelBuilder.Entity<DataStorageRecord>());
        ConfigurePromptProtectionRule(modelBuilder.Entity<PromptProtectionRuleRecord>());
        ConfigureTrace(modelBuilder.Entity<TraceRecord>());
        ConfigureThread(modelBuilder.Entity<ThreadRecord>());
        ConfigureRole(modelBuilder.Entity<RoleRecord>());
        ConfigureUserRole(modelBuilder.Entity<UserRoleRecord>());
        ConfigureUserProject(modelBuilder.Entity<UserProjectRecord>());
        ConfigureOidcIdentity(modelBuilder.Entity<OidcIdentityRecord>());
        ConfigureInvitation(modelBuilder.Entity<InvitationRecord>());
        ConfigureProviderQuotaStatus(modelBuilder.Entity<ProviderQuotaStatusRecord>());
    }

    private static void ConfigureProviderQuotaStatus(
        EntityTypeBuilder<ProviderQuotaStatusRecord> builder)
    {
        builder.ToTable("provider_quota_status");
        builder.HasKey(status => status.Id);
        builder.Property(status => status.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(status => status.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(status => status.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(status => status.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired();
        builder.Property(status => status.ProviderType)
            .HasColumnName("provider_type")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(status => status.Status)
            .HasColumnName("status")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(status => status.QuotaData)
            .HasColumnName("quota_data")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(status => status.NextResetAt)
            .HasColumnName("next_reset_at")
            .HasColumnType("timestamptz");
        builder.Property(status => status.Ready)
            .HasColumnName("ready")
            .HasDefaultValue(true)
            .IsRequired();
        builder.Property(status => status.NextCheckAt)
            .HasColumnName("next_check_at")
            .HasColumnType("timestamptz")
            .IsRequired();
        builder.Property(status => status.AccountKey)
            .HasColumnName("account_key")
            .HasColumnType("varchar")
            .HasDefaultValue(string.Empty);
        builder.Property(status => status.ChannelId)
            .HasColumnName("channel_id")
            .IsRequired();

        builder.HasIndex(status => status.ChannelId)
            .HasDatabaseName("providerquotastatus_channel_id")
            .IsUnique();
        builder.HasIndex(status => status.NextCheckAt)
            .HasDatabaseName("providerquotastatus_next_check_at");
        builder.HasIndex(status => status.AccountKey)
            .HasDatabaseName("providerquotastatus_account_key");
    }

    private static void ConfigureInvitation(EntityTypeBuilder<InvitationRecord> builder)
    {
        builder.ToTable("invitations");
        builder.HasKey(invitation => invitation.Id);
        builder.Property(invitation => invitation.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(invitation => invitation.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(invitation => invitation.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(invitation => invitation.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired();
        builder.Property(invitation => invitation.TokenHash)
            .HasColumnName("token_hash")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(invitation => invitation.RoleId).HasColumnName("role_id");
        builder.Property(invitation => invitation.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");
        builder.Property(invitation => invitation.MaxUses)
            .HasColumnName("max_uses")
            .IsRequired();
        builder.Property(invitation => invitation.UsedCount)
            .HasColumnName("used_count")
            .IsRequired();
        builder.Property(invitation => invitation.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.HasIndex(invitation => invitation.TokenHash)
            .HasDatabaseName("invitation_token_hash")
            .IsUnique();
        builder.HasIndex(invitation => invitation.ProjectId)
            .HasDatabaseName("invitation_project_id");
    }

    private static void ConfigureOidcIdentity(EntityTypeBuilder<OidcIdentityRecord> builder)
    {
        builder.ToTable("oidc_identities");
        builder.HasKey(identity => identity.Id);
        builder.Property(identity => identity.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(identity => identity.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(identity => identity.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(identity => identity.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(identity => identity.Issuer)
            .HasColumnName("issuer")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(identity => identity.Subject)
            .HasColumnName("subject")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(identity => identity.Email).HasColumnName("email").HasColumnType("varchar");
        builder.Property(identity => identity.IdpName).HasColumnName("idp_name").HasColumnType("varchar");
        builder.Property(identity => identity.LastLoginAt)
            .HasColumnName("last_login_at")
            .HasColumnType("timestamptz");
        builder.Property(identity => identity.UserId).HasColumnName("user_id").IsRequired();

        builder.HasIndex(identity => new
        {
            identity.Issuer,
            identity.Subject,
            identity.DeletedAt,
        })
            .HasDatabaseName("oidc_identities_by_issuer_subject_deleted_at")
            .IsUnique();
        builder.HasIndex(identity => identity.UserId)
            .HasDatabaseName("oidc_identities_by_user_id");
    }

    private static void ConfigureRole(EntityTypeBuilder<RoleRecord> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(role => role.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(role => role.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(role => role.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(role => role.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(role => role.Level).HasColumnName("level").HasColumnType("varchar").IsRequired();
        builder.Property(role => role.Scopes).HasColumnName("scopes").HasColumnType("jsonb");
        builder.Property(role => role.ProjectId).HasColumnName("project_id");

        builder.HasIndex(role => new { role.ProjectId, role.Name })
            .HasDatabaseName("roles_by_project_id_name")
            .IsUnique();
        builder.HasIndex(role => role.Level).HasDatabaseName("roles_by_level");
    }

    private static void ConfigureUserRole(EntityTypeBuilder<UserRoleRecord> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(userRole => userRole.Id);
        builder.Property(userRole => userRole.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(userRole => userRole.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(userRole => userRole.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(userRole => userRole.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(userRole => userRole.RoleId).HasColumnName("role_id").IsRequired();

        builder.HasIndex(userRole => new { userRole.UserId, userRole.RoleId })
            .HasDatabaseName("user_roles_by_user_id_role_id")
            .IsUnique();
        builder.HasIndex(userRole => userRole.RoleId).HasDatabaseName("user_roles_by_role_id");
    }

    private static void ConfigureUserProject(EntityTypeBuilder<UserProjectRecord> builder)
    {
        builder.ToTable("user_projects");
        builder.HasKey(userProject => userProject.Id);
        builder.Property(userProject => userProject.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(userProject => userProject.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(userProject => userProject.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(userProject => userProject.IsOwner)
            .HasColumnName("is_owner")
            .IsRequired();
        builder.Property(userProject => userProject.Scopes).HasColumnName("scopes").HasColumnType("jsonb");
        builder.Property(userProject => userProject.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(userProject => userProject.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.HasIndex(userProject => new { userProject.UserId, userProject.ProjectId })
            .HasDatabaseName("user_projects_by_user_id_project_id")
            .IsUnique();
        builder.HasIndex(userProject => userProject.ProjectId)
            .HasDatabaseName("user_projects_by_project_id");
    }

    private static void ConfigureTrace(EntityTypeBuilder<TraceRecord> builder)
    {
        builder.ToTable("traces");
        builder.HasKey(trace => trace.Id);
        builder.Property(trace => trace.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(trace => trace.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(trace => trace.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(trace => trace.TraceId)
            .HasColumnName("trace_id")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(trace => trace.Status)
            .HasColumnName("status")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(trace => trace.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();
        builder.Property(trace => trace.ThreadId).HasColumnName("thread_id");

        builder.HasIndex(trace => trace.ProjectId).HasDatabaseName("traces_by_project_id");
        builder.HasIndex(trace => trace.TraceId)
            .HasDatabaseName("traces_by_trace_id")
            .IsUnique();
        builder.HasIndex(trace => trace.ThreadId).HasDatabaseName("traces_by_thread_id");
        builder.HasIndex(trace => new { trace.ProjectId, trace.Status })
            .HasDatabaseName("traces_by_project_id_status");
    }

    private static void ConfigureThread(EntityTypeBuilder<ThreadRecord> builder)
    {
        builder.ToTable("threads");
        builder.HasKey(thread => thread.Id);
        builder.Property(thread => thread.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(thread => thread.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(thread => thread.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(thread => thread.ThreadId)
            .HasColumnName("thread_id")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(thread => thread.Status)
            .HasColumnName("status")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(thread => thread.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.HasIndex(thread => thread.ProjectId).HasDatabaseName("threads_by_project_id");
        builder.HasIndex(thread => thread.ThreadId)
            .HasDatabaseName("threads_by_thread_id")
            .IsUnique();
        builder.HasIndex(thread => new { thread.ProjectId, thread.Status })
            .HasDatabaseName("threads_by_project_id_status");
    }

    private static void ConfigurePrompt(EntityTypeBuilder<PromptRecord> builder)
    {
        builder.ToTable("prompts");
        builder.HasKey(prompt => prompt.Id);
        builder.Property(prompt => prompt.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(prompt => prompt.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(prompt => prompt.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(prompt => prompt.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(prompt => prompt.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(prompt => prompt.Description)
            .HasColumnName("description")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(prompt => prompt.Role).HasColumnName("role").HasColumnType("varchar").IsRequired();
        builder.Property(prompt => prompt.Content).HasColumnName("content").HasColumnType("varchar").IsRequired();
        builder.Property(prompt => prompt.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(prompt => prompt.Order).HasColumnName("order").IsRequired();
        builder.Property(prompt => prompt.Settings).HasColumnName("settings").HasColumnType("jsonb").IsRequired();
        builder.Property(prompt => prompt.ProjectId).HasColumnName("project_id").IsRequired();

        builder.HasIndex(prompt => prompt.ProjectId).HasDatabaseName("prompts_by_project_id");
        builder.HasIndex(prompt => new { prompt.ProjectId, prompt.Name, prompt.DeletedAt })
            .HasDatabaseName("prompts_by_project_id_name")
            .IsUnique();
    }

    private static void ConfigureDataStorage(EntityTypeBuilder<DataStorageRecord> builder)
    {
        builder.ToTable("data_storages");
        builder.HasKey(storage => storage.Id);
        builder.Property(storage => storage.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(storage => storage.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(storage => storage.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(storage => storage.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(storage => storage.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(storage => storage.Description)
            .HasColumnName("description")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(storage => storage.Primary).HasColumnName("primary").IsRequired();
        builder.Property(storage => storage.Type).HasColumnName("type").HasColumnType("varchar").IsRequired();
        builder.Property(storage => storage.Settings)
            .HasColumnName("settings")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(storage => storage.Status)
            .HasColumnName("status")
            .HasColumnType("varchar")
            .IsRequired();

        // Ent's index.Fields("name") has no deleted_at column, so a soft-deleted
        // name stays reserved at the database level.
        builder.HasIndex(storage => storage.Name)
            .HasDatabaseName("data_sources_by_name")
            .IsUnique();
    }

    private static void ConfigurePromptProtectionRule(
        EntityTypeBuilder<PromptProtectionRuleRecord> builder)
    {
        builder.ToTable("prompt_protection_rules");
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(rule => rule.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(rule => rule.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(rule => rule.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(rule => rule.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(rule => rule.Description)
            .HasColumnName("description")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(rule => rule.Pattern).HasColumnName("pattern").HasColumnType("varchar").IsRequired();
        builder.Property(rule => rule.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(rule => rule.Settings).HasColumnName("settings").HasColumnType("jsonb").IsRequired();

        builder.HasIndex(rule => new { rule.Name, rule.DeletedAt })
            .HasDatabaseName("prompt_protection_rules_by_name")
            .IsUnique();
    }

    private static void ConfigureSystem(EntityTypeBuilder<SystemRecord> builder)
    {
        builder.ToTable("systems");
        builder.HasKey(system => system.Id);
        builder.Property(system => system.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(system => system.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(system => system.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(system => system.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(system => system.Key).HasColumnName("key").HasColumnType("varchar").IsRequired();
        builder.Property(system => system.Value).HasColumnName("value").HasColumnType("varchar").IsRequired();

        // Ent's field.String("key").Unique() becomes the PostgreSQL constraint
        // name "<table>_<column>_key".
        builder.HasIndex(system => system.Key).HasDatabaseName("systems_key_key").IsUnique();
    }

    private static void ConfigureUser(EntityTypeBuilder<UserRecord> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(user => user.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(user => user.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(user => user.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(user => user.Email).HasColumnName("email").HasColumnType("varchar").IsRequired();
        builder.Property(user => user.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(user => user.PreferLanguage)
            .HasColumnName("prefer_language")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(user => user.Password).HasColumnName("password").HasColumnType("varchar").IsRequired();
        builder.Property(user => user.FirstName)
            .HasColumnName("first_name")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(user => user.LastName).HasColumnName("last_name").HasColumnType("varchar").IsRequired();
        builder.Property(user => user.Avatar).HasColumnName("avatar").HasColumnType("varchar");
        builder.Property(user => user.IsOwner).HasColumnName("is_owner").IsRequired();
        builder.Property(user => user.Scopes).HasColumnName("scopes").HasColumnType("jsonb");

        builder.HasIndex(user => new { user.Email, user.DeletedAt })
            .HasDatabaseName("user_email_deleted_at")
            .IsUnique();
    }

    private static void ConfigureProject(EntityTypeBuilder<ProjectRecord> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(project => project.Id);
        builder.Property(project => project.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(project => project.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(project => project.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(project => project.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(project => project.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(project => project.Description)
            .HasColumnName("description")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(project => project.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(project => project.Profiles).HasColumnName("profiles").HasColumnType("jsonb");

        builder.HasIndex(project => new { project.Name, project.DeletedAt })
            .HasDatabaseName("projects_by_name")
            .IsUnique();
    }

    private static void ConfigureApiKey(EntityTypeBuilder<ApiKeyRecord> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(apiKey => apiKey.Id);
        builder.Property(apiKey => apiKey.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(apiKey => apiKey.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(apiKey => apiKey.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(apiKey => apiKey.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(apiKey => apiKey.Key).HasColumnName("key").HasColumnType("varchar").IsRequired();
        builder.Property(apiKey => apiKey.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(apiKey => apiKey.Type).HasColumnName("type").HasColumnType("varchar").IsRequired();
        builder.Property(apiKey => apiKey.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(apiKey => apiKey.Scopes).HasColumnName("scopes").HasColumnType("jsonb");
        builder.Property(apiKey => apiKey.Profiles).HasColumnName("profiles").HasColumnType("jsonb");
        builder.Property(apiKey => apiKey.AllowedIps)
            .HasColumnName("allowed_ips")
            .HasColumnType("jsonb");
        builder.Property(apiKey => apiKey.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(apiKey => apiKey.UserId).HasColumnName("user_id");

        builder.HasIndex(apiKey => apiKey.UserId).HasDatabaseName("api_keys_by_user_id");
        builder.HasIndex(apiKey => apiKey.ProjectId).HasDatabaseName("api_keys_by_project_id");
        builder.HasIndex(apiKey => apiKey.Key).HasDatabaseName("api_keys_by_key").IsUnique();
    }

    private static void ConfigureApiKeyProfileTemplate(
        EntityTypeBuilder<ApiKeyProfileTemplateRecord> builder)
    {
        builder.ToTable("api_key_profile_templates");
        builder.HasKey(template => template.Id);
        builder.Property(template => template.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(template => template.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(template => template.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(template => template.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(template => template.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(template => template.Description)
            .HasColumnName("description")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(template => template.Profile).HasColumnName("profile").HasColumnType("jsonb");
        builder.Property(template => template.ProjectId).HasColumnName("project_id").IsRequired();

        builder.HasIndex(template => new { template.ProjectId, template.Name, template.DeletedAt })
            .HasDatabaseName("api_key_profile_templates_by_project_name")
            .IsUnique();
    }

    private static void ConfigureChannel(EntityTypeBuilder<ChannelRecord> builder)
    {
        builder.ToTable("channels");
        builder.HasKey(channel => channel.Id);
        builder.Property(channel => channel.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(channel => channel.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(channel => channel.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(channel => channel.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(channel => channel.Type).HasColumnName("type").HasColumnType("varchar").IsRequired();
        builder.Property(channel => channel.BaseUrl).HasColumnName("base_url").HasColumnType("varchar");
        builder.Property(channel => channel.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(channel => channel.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(channel => channel.Credentials).HasColumnName("credentials").HasColumnType("jsonb").IsRequired();
        builder.Property(channel => channel.DisabledApiKeys)
            .HasColumnName("disabled_api_keys")
            .HasColumnType("jsonb");
        builder.Property(channel => channel.SupportedModels)
            .HasColumnName("supported_models")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(channel => channel.ManualModels).HasColumnName("manual_models").HasColumnType("jsonb");
        builder.Property(channel => channel.AutoSyncSupportedModels)
            .HasColumnName("auto_sync_supported_models")
            .IsRequired();
        builder.Property(channel => channel.AutoSyncModelPattern)
            .HasColumnName("auto_sync_model_pattern")
            .HasColumnType("varchar");
        builder.Property(channel => channel.Tags).HasColumnName("tags").HasColumnType("jsonb");
        builder.Property(channel => channel.DefaultTestModel)
            .HasColumnName("default_test_model")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(channel => channel.Policies).HasColumnName("policies").HasColumnType("jsonb");
        builder.Property(channel => channel.Settings).HasColumnName("settings").HasColumnType("jsonb");
        builder.Property(channel => channel.OrderingWeight).HasColumnName("ordering_weight").IsRequired();
        builder.Property(channel => channel.ErrorMessage).HasColumnName("error_message").HasColumnType("varchar");
        builder.Property(channel => channel.AutoDisabledAt)
            .HasColumnName("auto_disabled_at")
            .HasColumnType("timestamptz");
        builder.Property(channel => channel.AutoDisableExpiresAt)
            .HasColumnName("auto_disable_expires_at")
            .HasColumnType("timestamptz");
        builder.Property(channel => channel.Remark).HasColumnName("remark").HasColumnType("varchar");
        builder.Property(channel => channel.Endpoints).HasColumnName("endpoints").HasColumnType("jsonb");

        builder.HasIndex(channel => new { channel.Name, channel.DeletedAt })
            .HasDatabaseName("channels_by_name")
            .IsUnique();
    }

    private static void ConfigureChannelModelPrice(EntityTypeBuilder<ChannelModelPriceRecord> builder)
    {
        builder.ToTable("channel_model_prices");
        builder.HasKey(price => price.Id);
        builder.Property(price => price.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(price => price.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(price => price.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(price => price.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(price => price.ModelId).HasColumnName("model_id").HasColumnType("varchar").IsRequired();
        builder.Property(price => price.Price).HasColumnName("price").HasColumnType("jsonb").IsRequired();
        builder.Property(price => price.ReferenceId)
            .HasColumnName("reference_id")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(price => price.ChannelId).HasColumnName("channel_id").IsRequired();

        builder.HasIndex(price => new { price.ChannelId, price.ModelId, price.DeletedAt })
            .HasDatabaseName("channel_model_prices_by_channel_id_model_id")
            .IsUnique();
        builder.HasIndex(price => price.ReferenceId)
            .HasDatabaseName("channel_model_prices_reference_id_key")
            .IsUnique();
    }

    private static void ConfigureChannelOverrideTemplate(
        EntityTypeBuilder<ChannelOverrideTemplateRecord> builder)
    {
        builder.ToTable("channel_override_templates");
        builder.HasKey(template => template.Id);
        builder.Property(template => template.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(template => template.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(template => template.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(template => template.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(template => template.Name)
            .HasColumnName("name")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(template => template.Description)
            .HasColumnName("description")
            .HasColumnType("varchar");
        builder.Property(template => template.OverrideParameters)
            .HasColumnName("override_parameters")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(template => template.OverrideHeaders)
            .HasColumnName("override_headers")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(template => template.HeaderOverrideOperations)
            .HasColumnName("header_override_operations")
            .HasColumnType("jsonb");
        builder.Property(template => template.BodyOverrideOperations)
            .HasColumnName("body_override_operations")
            .HasColumnType("jsonb");
        builder.Property(template => template.UserId).HasColumnName("user_id");

        builder.HasIndex(template => new { template.UserId, template.Name, template.DeletedAt })
            .HasDatabaseName("channel_override_templates_by_user_name")
            .IsUnique();
    }

    private static void ConfigureChannelProbe(EntityTypeBuilder<ChannelProbeRecord> builder)
    {
        // internal/ent/schema/channel_probe.go has no mixin: no created_at,
        // updated_at or deleted_at, and no foreign-key clause is emitted
        // (migrate.WithForeignKeys(false)).
        builder.ToTable("channel_probes");
        builder.HasKey(probe => probe.Id);
        builder.Property(probe => probe.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(probe => probe.TotalRequestCount)
            .HasColumnName("total_request_count")
            .IsRequired();
        builder.Property(probe => probe.SuccessRequestCount)
            .HasColumnName("success_request_count")
            .IsRequired();
        builder.Property(probe => probe.AvgTokensPerSecond)
            .HasColumnName("avg_tokens_per_second");
        builder.Property(probe => probe.AvgTimeToFirstTokenMs)
            .HasColumnName("avg_time_to_first_token_ms");
        builder.Property(probe => probe.Timestamp).HasColumnName("timestamp").IsRequired();
        builder.Property(probe => probe.ChannelId).HasColumnName("channel_id").IsRequired();

        builder.HasIndex(probe => new { probe.ChannelId, probe.Timestamp })
            .HasDatabaseName("channel_probes_by_channel_id_timestamp");
    }

    private static void ConfigureChannelModelPriceVersion(
        EntityTypeBuilder<ChannelModelPriceVersionRecord> builder)
    {
        builder.ToTable("channel_model_price_versions");
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(version => version.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(version => version.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(version => version.ChannelId).HasColumnName("channel_id").IsRequired();
        builder.Property(version => version.ModelId).HasColumnName("model_id").HasColumnType("varchar").IsRequired();
        builder.Property(version => version.ChannelModelPriceId)
            .HasColumnName("channel_model_price_id")
            .IsRequired();
        builder.Property(version => version.Price).HasColumnName("price").HasColumnType("jsonb").IsRequired();
        builder.Property(version => version.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(version => version.EffectiveStartAt)
            .HasColumnName("effective_start_at")
            .HasColumnType("timestamptz")
            .IsRequired();
        builder.Property(version => version.EffectiveEndAt)
            .HasColumnName("effective_end_at")
            .HasColumnType("timestamptz");
        builder.Property(version => version.ReferenceId)
            .HasColumnName("reference_id")
            .HasColumnType("varchar")
            .IsRequired();

        builder.HasIndex(version => version.ReferenceId)
            .HasDatabaseName("channel_model_price_versions_reference_id_key")
            .IsUnique();
    }

    private static void ConfigureModel(EntityTypeBuilder<ModelRecord> builder)
    {
        builder.ToTable("models");
        builder.HasKey(model => model.Id);
        builder.Property(model => model.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(model => model.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(model => model.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(model => model.DeletedAt).HasColumnName("deleted_at").IsRequired();
        builder.Property(model => model.Developer)
            .HasColumnName("developer")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(model => model.ModelId)
            .HasColumnName("model_id")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(model => model.Type).HasColumnName("type").HasColumnType("varchar").IsRequired();
        builder.Property(model => model.Name).HasColumnName("name").HasColumnType("varchar").IsRequired();
        builder.Property(model => model.Icon).HasColumnName("icon").HasColumnType("varchar").IsRequired();
        builder.Property(model => model.Group).HasColumnName("group").HasColumnType("varchar").IsRequired();
        builder.Property(model => model.ModelCard)
            .HasColumnName("model_card")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(model => model.Settings)
            .HasColumnName("settings")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(model => model.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(model => model.Remark).HasColumnName("remark").HasColumnType("varchar");

        builder.HasIndex(model => new { model.Name, model.DeletedAt })
            .HasDatabaseName("models_by_name")
            .IsUnique();
        builder.HasIndex(model => new { model.ModelId, model.DeletedAt })
            .HasDatabaseName("models_by_model_id")
            .IsUnique();
    }

    private static void ConfigureRequest(EntityTypeBuilder<RequestRecord> builder)
    {
        builder.ToTable("requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(request => request.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(request => request.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        builder.Property(request => request.UserId).HasColumnName("user_id");
        builder.Property(request => request.Source).HasColumnName("source").HasColumnType("varchar").IsRequired();
        builder.Property(request => request.ModelId).HasColumnName("model_id").HasColumnType("varchar").IsRequired();
        builder.Property(request => request.ReasoningEffort)
            .HasColumnName("reasoning_effort")
            .HasColumnType("varchar");
        builder.Property(request => request.Format).HasColumnName("format").HasColumnType("varchar").IsRequired();
        builder.Property(request => request.RequestHeaders)
            .HasColumnName("request_headers")
            .HasColumnType("jsonb");
        builder.Property(request => request.RequestBody)
            .HasColumnName("request_body")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(request => request.ResponseHeaders)
            .HasColumnName("response_headers")
            .HasColumnType("jsonb");
        builder.Property(request => request.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        builder.Property(request => request.ResponseChunks)
            .HasColumnName("response_chunks")
            .HasColumnType("jsonb");
        builder.Property(request => request.ExternalId).HasColumnName("external_id").HasColumnType("varchar");
        builder.Property(request => request.Status).HasColumnName("status").HasColumnType("varchar").IsRequired();
        builder.Property(request => request.Stream).HasColumnName("stream").IsRequired();
        builder.Property(request => request.ClientIp).HasColumnName("client_ip").HasColumnType("varchar").IsRequired();
        builder.Property(request => request.UserAgent)
            .HasColumnName("user_agent")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(request => request.MetricsLatencyMs).HasColumnName("metrics_latency_ms");
        builder.Property(request => request.MetricsFirstTokenLatencyMs)
            .HasColumnName("metrics_first_token_latency_ms");
        builder.Property(request => request.MetricsReasoningDurationMs)
            .HasColumnName("metrics_reasoning_duration_ms");
        builder.Property(request => request.ContentSaved).HasColumnName("content_saved").IsRequired();
        builder.Property(request => request.ContentStorageId).HasColumnName("content_storage_id");
        builder.Property(request => request.ContentStorageKey)
            .HasColumnName("content_storage_key")
            .HasColumnType("varchar");
        builder.Property(request => request.ContentSavedAt)
            .HasColumnName("content_saved_at")
            .HasColumnType("timestamptz");
        builder.Property(request => request.ApiKeyId).HasColumnName("api_key_id");
        builder.Property(request => request.ChannelId).HasColumnName("channel_id");
        builder.Property(request => request.DataStorageId).HasColumnName("data_storage_id");
        builder.Property(request => request.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(request => request.TraceId).HasColumnName("trace_id");

        builder.HasIndex(request => new { request.ApiKeyId, request.CreatedAt })
            .HasDatabaseName("requests_by_api_key_id_created_at");
        builder.HasIndex(request => new { request.ProjectId, request.CreatedAt })
            .HasDatabaseName("requests_by_project_id_created_at");
        builder.HasIndex(request => new { request.ProjectId, request.UserId, request.CreatedAt })
            .HasDatabaseName("requests_by_project_id_user_id_created_at");
        builder.HasIndex(request => new { request.ChannelId, request.CreatedAt })
            .HasDatabaseName("requests_by_channel_id_created_at");
        builder.HasIndex(request => new { request.TraceId, request.CreatedAt })
            .HasDatabaseName("requests_by_trace_id_created_at");
        builder.HasIndex(request => new
        {
            request.ExternalId,
            request.ApiKeyId,
            request.Status,
            request.CreatedAt,
        })
            .HasDatabaseName("requests_by_external_id_api_key_id_status_created_at");
        builder.HasIndex(request => request.CreatedAt)
            .HasDatabaseName("requests_by_created_at");
    }

    private static void ConfigureRequestExecution(EntityTypeBuilder<RequestExecutionRecord> builder)
    {
        builder.ToTable("request_executions");
        builder.HasKey(execution => execution.Id);
        builder.Property(execution => execution.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(execution => execution.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(execution => execution.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(execution => execution.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(execution => execution.ChannelApiKeyIndex).HasColumnName("channel_api_key_index");
        builder.Property(execution => execution.ExternalId).HasColumnName("external_id").HasColumnType("varchar");
        builder.Property(execution => execution.ModelId)
            .HasColumnName("model_id")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(execution => execution.UpstreamModelId)
            .HasColumnName("upstream_model_id")
            .HasColumnType("varchar");
        builder.Property(execution => execution.Format)
            .HasColumnName("format")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(execution => execution.ReasoningEffort)
            .HasColumnName("reasoning_effort")
            .HasColumnType("varchar");
        builder.Property(execution => execution.ChannelApiKeySuffix)
            .HasColumnName("channel_api_key_suffix")
            .HasColumnType("varchar");
        builder.Property(execution => execution.RequestBody)
            .HasColumnName("request_body")
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(execution => execution.ResponseHeaders)
            .HasColumnName("response_headers")
            .HasColumnType("jsonb");
        builder.Property(execution => execution.ResponseBody)
            .HasColumnName("response_body")
            .HasColumnType("jsonb");
        builder.Property(execution => execution.ResponseChunks)
            .HasColumnName("response_chunks")
            .HasColumnType("jsonb");
        builder.Property(execution => execution.ErrorMessage)
            .HasColumnName("error_message")
            .HasColumnType("varchar");
        builder.Property(execution => execution.ResponseStatusCode)
            .HasColumnName("response_status_code");
        builder.Property(execution => execution.Status)
            .HasColumnName("status")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(execution => execution.Stream).HasColumnName("stream").IsRequired();
        builder.Property(execution => execution.MetricsLatencyMs).HasColumnName("metrics_latency_ms");
        builder.Property(execution => execution.MetricsFirstTokenLatencyMs)
            .HasColumnName("metrics_first_token_latency_ms");
        builder.Property(execution => execution.MetricsReasoningDurationMs)
            .HasColumnName("metrics_reasoning_duration_ms");
        builder.Property(execution => execution.RequestHeaders)
            .HasColumnName("request_headers")
            .HasColumnType("jsonb");
        builder.Property(execution => execution.RequestUrl)
            .HasColumnName("request_url")
            .HasColumnType("varchar");
        builder.Property(execution => execution.PassThroughApplied)
            .HasColumnName("pass_through_applied")
            .IsRequired();
        builder.Property(execution => execution.ChannelId).HasColumnName("channel_id");
        builder.Property(execution => execution.DataStorageId).HasColumnName("data_storage_id");
        builder.Property(execution => execution.RequestId).HasColumnName("request_id").IsRequired();

        builder.HasIndex(execution => new { execution.RequestId, execution.Status, execution.CreatedAt })
            .HasDatabaseName("request_executions_by_request_id_status_created_at");
        builder.HasIndex(execution => new { execution.RequestId, execution.CreatedAt })
            .HasDatabaseName("request_executions_by_request_id_created_at");
        builder.HasIndex(execution => new { execution.ChannelId, execution.CreatedAt })
            .HasDatabaseName("request_executions_by_channel_id_created_at");
    }

    private static void ConfigureUsageLog(EntityTypeBuilder<UsageLogRecord> builder)
    {
        builder.ToTable("usage_logs");
        builder.HasKey(usageLog => usageLog.Id);
        builder.Property(usageLog => usageLog.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(usageLog => usageLog.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");
        builder.Property(usageLog => usageLog.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");
        builder.Property(usageLog => usageLog.ApiKeyId).HasColumnName("api_key_id");
        builder.Property(usageLog => usageLog.ModelId)
            .HasColumnName("model_id")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(usageLog => usageLog.PromptTokens).HasColumnName("prompt_tokens").IsRequired();
        builder.Property(usageLog => usageLog.CompletionTokens)
            .HasColumnName("completion_tokens")
            .IsRequired();
        builder.Property(usageLog => usageLog.TotalTokens).HasColumnName("total_tokens").IsRequired();

        // Ent omits nil optional details, so the schema default 0 is stored.
        // Nullable properties whose CLR default is null are likewise omitted by
        // EF when a store default is configured.
        builder.Property(usageLog => usageLog.PromptAudioTokens)
            .HasColumnName("prompt_audio_tokens")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.PromptCachedTokens)
            .HasColumnName("prompt_cached_tokens")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.PromptWriteCachedTokens)
            .HasColumnName("prompt_write_cached_tokens")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.PromptWriteCachedTokens5m)
            .HasColumnName("prompt_write_cached_tokens_5m")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.PromptWriteCachedTokens1h)
            .HasColumnName("prompt_write_cached_tokens_1h")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.CompletionAudioTokens)
            .HasColumnName("completion_audio_tokens")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.CompletionReasoningTokens)
            .HasColumnName("completion_reasoning_tokens")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.CompletionAcceptedPredictionTokens)
            .HasColumnName("completion_accepted_prediction_tokens")
            .HasDefaultValue(0L);
        builder.Property(usageLog => usageLog.CompletionRejectedPredictionTokens)
            .HasColumnName("completion_rejected_prediction_tokens")
            .HasDefaultValue(0L);

        builder.Property(usageLog => usageLog.Source)
            .HasColumnName("source")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(usageLog => usageLog.Format)
            .HasColumnName("format")
            .HasColumnType("varchar")
            .IsRequired();
        builder.Property(usageLog => usageLog.TotalCost).HasColumnName("total_cost");
        builder.Property(usageLog => usageLog.CostItems).HasColumnName("cost_items").HasColumnType("jsonb");
        builder.Property(usageLog => usageLog.CostPriceReferenceId)
            .HasColumnName("cost_price_reference_id")
            .HasColumnType("varchar");
        builder.Property(usageLog => usageLog.ChannelId).HasColumnName("channel_id");
        builder.Property(usageLog => usageLog.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(usageLog => usageLog.RequestId).HasColumnName("request_id").IsRequired();

        builder.HasIndex(usageLog => usageLog.RequestId)
            .HasDatabaseName("usage_logs_by_request_id");
        builder.HasIndex(usageLog => usageLog.CreatedAt)
            .HasDatabaseName("usage_logs_by_created_at");
        builder.HasIndex(usageLog => new { usageLog.ModelId, usageLog.CreatedAt })
            .HasDatabaseName("usage_logs_by_model_id_created_at");
        builder.HasIndex(usageLog => new { usageLog.ProjectId, usageLog.CreatedAt })
            .HasDatabaseName("usage_logs_by_project_id_created_at");
        builder.HasIndex(usageLog => new { usageLog.ChannelId, usageLog.CreatedAt })
            .HasDatabaseName("usage_logs_by_channel_id_created_at");
        builder.HasIndex(usageLog => new { usageLog.ApiKeyId, usageLog.CreatedAt })
            .HasDatabaseName("usage_logs_by_api_key_id_created_at");
    }
}
