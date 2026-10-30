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

    public DbSet<RequestRecord> Requests => Set<RequestRecord>();

    public DbSet<RequestExecutionRecord> RequestExecutions => Set<RequestExecutionRecord>();

    public DbSet<UsageLogRecord> UsageLogs => Set<UsageLogRecord>();

    public DbSet<UserRecord> Users => Set<UserRecord>();

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();

    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    public DbSet<SystemRecord> Systems => Set<SystemRecord>();

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
        ConfigureRequest(modelBuilder.Entity<RequestRecord>());
        ConfigureRequestExecution(modelBuilder.Entity<RequestExecutionRecord>());
        ConfigureUsageLog(modelBuilder.Entity<UsageLogRecord>());
        ConfigureUser(modelBuilder.Entity<UserRecord>());
        ConfigureProject(modelBuilder.Entity<ProjectRecord>());
        ConfigureApiKey(modelBuilder.Entity<ApiKeyRecord>());
        ConfigureSystem(modelBuilder.Entity<SystemRecord>());
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
