using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the ported schema to the reference's generated Ent migration
/// (internal/ent/migrate/schema.go at b8bb342) and Ent's PostgreSQL type mapping
/// (entgo.io/ent/dialect/sql/schema/postgres.go at v0.14.6). The expectations below
/// are copied from those sources by hand on purpose: they must not be derived from
/// <see cref="DatabaseSchema"/>, otherwise the test could never catch a drift in it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture fixture)
{
    private static readonly string[] ExpectedTables =
    [
        "api_keys", "channel_model_prices", "channels", "projects",
        "request_executions", "requests", "systems", "usage_logs", "users",
    ];

    private static readonly Dictionary<string, string[]> ExpectedColumns = new(StringComparer.Ordinal)
    {
        ["api_keys"] =
        [
            "id", "created_at", "updated_at", "deleted_at", "key", "name", "type",
            "status", "scopes", "profiles", "allowed_ips", "project_id", "user_id",
        ],
        ["projects"] =
        [
            "id", "created_at", "updated_at", "deleted_at", "name", "description",
            "status", "profiles",
        ],
        ["users"] =
        [
            "id", "created_at", "updated_at", "deleted_at", "email", "status",
            "prefer_language", "password", "first_name", "last_name", "avatar",
            "is_owner", "scopes",
        ],
        ["systems"] =
        [
            "id", "created_at", "updated_at", "deleted_at", "key", "value",
        ],
        ["channel_model_prices"] =
        [
            "id", "created_at", "updated_at", "deleted_at", "model_id", "price",
            "reference_id", "channel_id",
        ],
        ["channels"] =
        [
            "id", "created_at", "updated_at", "deleted_at", "type", "base_url", "name",
            "status", "credentials", "disabled_api_keys", "supported_models",
            "manual_models", "auto_sync_supported_models", "auto_sync_model_pattern",
            "tags", "default_test_model", "policies", "settings", "ordering_weight",
            "error_message", "auto_disabled_at", "auto_disable_expires_at", "remark",
            "endpoints",
        ],
        ["requests"] =
        [
            "id", "created_at", "updated_at", "user_id", "source", "model_id",
            "reasoning_effort", "format", "request_headers", "request_body",
            "response_headers", "response_body", "response_chunks", "external_id",
            "status", "stream", "client_ip", "user_agent", "metrics_latency_ms",
            "metrics_first_token_latency_ms", "metrics_reasoning_duration_ms",
            "content_saved", "content_storage_id", "content_storage_key",
            "content_saved_at", "api_key_id", "channel_id", "data_storage_id",
            "project_id", "trace_id",
        ],
        ["request_executions"] =
        [
            "id", "created_at", "updated_at", "project_id", "channel_api_key_index",
            "external_id", "model_id", "upstream_model_id", "format",
            "reasoning_effort", "channel_api_key_suffix", "request_body",
            "response_headers", "response_body", "response_chunks", "error_message",
            "response_status_code", "status", "stream", "metrics_latency_ms",
            "metrics_first_token_latency_ms", "metrics_reasoning_duration_ms",
            "request_headers", "request_url", "pass_through_applied", "channel_id",
            "data_storage_id", "request_id",
        ],
        ["usage_logs"] =
        [
            "id", "created_at", "updated_at", "api_key_id", "model_id",
            "prompt_tokens", "completion_tokens", "total_tokens",
            "prompt_audio_tokens", "prompt_cached_tokens",
            "prompt_write_cached_tokens", "prompt_write_cached_tokens_5m",
            "prompt_write_cached_tokens_1h", "completion_audio_tokens",
            "completion_reasoning_tokens", "completion_accepted_prediction_tokens",
            "completion_rejected_prediction_tokens", "source", "format", "total_cost",
            "cost_items", "cost_price_reference_id", "channel_id", "project_id",
            "request_id",
        ],
    };

    /// <summary>Ent field type -> PostgreSQL data_type, per table.</summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> ExpectedTypes =
        new(StringComparer.Ordinal)
        {
            ["api_keys"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] = ["id", "deleted_at", "project_id", "user_id"],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["jsonb"] = ["scopes", "profiles", "allowed_ips"],
                ["character varying"] = ["key", "name", "type", "status"],
            },
            ["projects"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] = ["id", "deleted_at"],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["jsonb"] = ["profiles"],
                ["character varying"] = ["name", "description", "status"],
            },
            ["users"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] = ["id", "deleted_at"],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["boolean"] = ["is_owner"],
                ["jsonb"] = ["scopes"],
                ["character varying"] =
                [
                    "email", "status", "prefer_language", "password", "first_name",
                    "last_name", "avatar",
                ],
            },
            ["systems"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] = ["id", "deleted_at"],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["character varying"] = ["key", "value"],
            },
            ["channel_model_prices"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] = ["id", "deleted_at", "channel_id"],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["jsonb"] = ["price"],
                ["character varying"] = ["model_id", "reference_id"],
            },
            ["channels"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] = ["id", "deleted_at", "ordering_weight"],
                ["timestamp with time zone"] =
                    ["created_at", "updated_at", "auto_disabled_at", "auto_disable_expires_at"],
                ["boolean"] = ["auto_sync_supported_models"],
                ["jsonb"] =
                [
                    "credentials", "disabled_api_keys", "supported_models", "manual_models",
                    "tags", "policies", "settings", "endpoints",
                ],
                ["character varying"] =
                [
                    "type", "base_url", "name", "status", "auto_sync_model_pattern",
                    "default_test_model", "error_message", "remark",
                ],
            },
            ["requests"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] =
                [
                    "id", "user_id", "metrics_latency_ms", "metrics_first_token_latency_ms",
                    "metrics_reasoning_duration_ms", "content_storage_id", "api_key_id",
                    "channel_id", "data_storage_id", "project_id", "trace_id",
                ],
                ["timestamp with time zone"] = ["created_at", "updated_at", "content_saved_at"],
                ["boolean"] = ["stream", "content_saved"],
                ["jsonb"] =
                [
                    "request_headers", "request_body", "response_headers", "response_body",
                    "response_chunks",
                ],
                ["character varying"] =
                [
                    "source", "model_id", "reasoning_effort", "format", "external_id",
                    "status", "client_ip", "user_agent", "content_storage_key",
                ],
            },
            ["request_executions"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] =
                [
                    "id", "project_id", "channel_api_key_index", "response_status_code",
                    "metrics_latency_ms", "metrics_first_token_latency_ms",
                    "metrics_reasoning_duration_ms", "channel_id", "data_storage_id",
                    "request_id",
                ],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["boolean"] = ["stream", "pass_through_applied"],
                ["jsonb"] =
                [
                    "request_body", "response_headers", "response_body", "response_chunks",
                    "request_headers",
                ],
                ["character varying"] =
                [
                    "external_id", "model_id", "upstream_model_id", "format",
                    "reasoning_effort", "channel_api_key_suffix", "error_message", "status",
                    "request_url",
                ],
            },
            ["usage_logs"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["bigint"] =
                [
                    "id", "api_key_id", "prompt_tokens", "completion_tokens", "total_tokens",
                    "prompt_audio_tokens", "prompt_cached_tokens",
                    "prompt_write_cached_tokens", "prompt_write_cached_tokens_5m",
                    "prompt_write_cached_tokens_1h", "completion_audio_tokens",
                    "completion_reasoning_tokens", "completion_accepted_prediction_tokens",
                    "completion_rejected_prediction_tokens", "channel_id", "project_id",
                    "request_id",
                ],
                ["timestamp with time zone"] = ["created_at", "updated_at"],
                ["double precision"] = ["total_cost"],
                ["jsonb"] = ["cost_items"],
                ["character varying"] =
                    ["model_id", "source", "format", "cost_price_reference_id"],
            },
        };

    private static readonly Dictionary<string, (string Table, bool Unique, string[] Columns)> ExpectedIndexes =
        new(StringComparer.Ordinal)
        {
            ["channel_model_prices_by_channel_id_model_id"] =
                ("channel_model_prices", true, ["channel_id", "model_id", "deleted_at"]),
            ["channel_model_prices_reference_id_key"] =
                ("channel_model_prices", true, ["reference_id"]),
            ["channels_by_name"] = ("channels", true, ["name", "deleted_at"]),
            ["projects_by_name"] = ("projects", true, ["name", "deleted_at"]),
            ["user_email_deleted_at"] = ("users", true, ["email", "deleted_at"]),
            ["api_keys_by_user_id"] = ("api_keys", false, ["user_id"]),
            ["api_keys_by_project_id"] = ("api_keys", false, ["project_id"]),
            ["api_keys_by_key"] = ("api_keys", true, ["key"]),
            ["systems_key_key"] = ("systems", true, ["key"]),
            ["requests_by_api_key_id_created_at"] = ("requests", false, ["api_key_id", "created_at"]),
            ["requests_by_project_id_created_at"] = ("requests", false, ["project_id", "created_at"]),
            ["requests_by_project_id_user_id_created_at"] =
                ("requests", false, ["project_id", "user_id", "created_at"]),
            ["requests_by_channel_id_created_at"] = ("requests", false, ["channel_id", "created_at"]),
            ["requests_by_trace_id_created_at"] = ("requests", false, ["trace_id", "created_at"]),
            ["requests_by_external_id_api_key_id_status_created_at"] =
                ("requests", false, ["external_id", "api_key_id", "status", "created_at"]),
            ["requests_by_created_at"] = ("requests", false, ["created_at"]),
            ["request_executions_by_request_id_status_created_at"] =
                ("request_executions", false, ["request_id", "status", "created_at"]),
            ["request_executions_by_request_id_created_at"] =
                ("request_executions", false, ["request_id", "created_at"]),
            ["request_executions_by_channel_id_created_at"] =
                ("request_executions", false, ["channel_id", "created_at"]),
            ["usage_logs_by_request_id"] = ("usage_logs", false, ["request_id"]),
            ["usage_logs_by_created_at"] = ("usage_logs", false, ["created_at"]),
            ["usage_logs_by_model_id_created_at"] = ("usage_logs", false, ["model_id", "created_at"]),
            ["usage_logs_by_project_id_created_at"] = ("usage_logs", false, ["project_id", "created_at"]),
            ["usage_logs_by_channel_id_created_at"] = ("usage_logs", false, ["channel_id", "created_at"]),
            ["usage_logs_by_api_key_id_created_at"] = ("usage_logs", false, ["api_key_id", "created_at"]),
        };

    [Fact]
    public async Task TablesAndColumnsMatchReferenceEntDefinitions()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        var tables = await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' " +
            "ORDER BY table_name;");

        Assert.Equal(ExpectedTables, tables.ToArray());

        foreach (var (table, expectedColumns) in ExpectedColumns)
        {
            var columns = await connection.QueryAsync<string>(
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND table_name = @Table " +
                "ORDER BY ordinal_position;",
                new { Table = table });

            Assert.Equal(expectedColumns, columns.ToArray());
        }
    }

    [Fact]
    public async Task ColumnTypesMatchEntPostgresMapping()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        foreach (var (table, expectedTypes) in ExpectedTypes)
        {
            var rows = (await connection.QueryAsync<ColumnInfo>(
                "SELECT column_name, data_type FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND table_name = @Table;",
                new { Table = table })).ToDictionary(row => row.ColumnName, row => row.DataType,
                StringComparer.Ordinal);

            // The type buckets must partition the table exactly, so a column added
            // to one list but not the other fails loudly.
            var typedColumns = expectedTypes.Values
                .SelectMany(columns => columns)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expectedColumns = ExpectedColumns[table].Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedColumns, typedColumns);

            foreach (var (dataType, columns) in expectedTypes)
            {
                foreach (var column in columns)
                {
                    Assert.True(
                        rows.TryGetValue(column, out var actual),
                        $"{table}.{column} is missing from information_schema.columns");
                    Assert.Equal(dataType, actual);
                }
            }

            var isIdentity = await connection.ExecuteScalarAsync<string>(
                "SELECT is_identity FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND table_name = @Table " +
                "AND column_name = 'id';",
                new { Table = table });
            Assert.Equal("YES", isIdentity);
        }
    }

    [Fact]
    public async Task IndexesMatchReferenceEntDefinitions()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        foreach (var (table, _) in ExpectedColumns)
        {
            // PostgreSQL also names the primary-key index "<table>_pkey".
            var expectedNames = ExpectedIndexes
                .Where(index => index.Value.Table == table)
                .Select(index => index.Key)
                .Append(table + "_pkey")
                .Order(StringComparer.Ordinal)
                .ToArray();

            var actualNames = (await connection.QueryAsync<string>(
                "SELECT indexname FROM pg_indexes " +
                "WHERE schemaname = current_schema() AND tablename = @Table ORDER BY indexname;",
                new { Table = table })).ToArray();

            Assert.Equal(expectedNames, actualNames);

            foreach (var name in expectedNames)
            {
                var unique = await connection.ExecuteScalarAsync<bool>(
                    "SELECT i.indisunique FROM pg_index i " +
                    "WHERE i.indexrelid = to_regclass(format('%I.%I', current_schema(), @Index));",
                    new { Index = name });

                var expectedUnique = ExpectedIndexes.TryGetValue(name, out var expected)
                    ? expected.Unique
                    : true; // primary key
                Assert.Equal(expectedUnique, unique);

                if (!ExpectedIndexes.TryGetValue(name, out expected))
                {
                    continue;
                }

                var columns = (await connection.QueryAsync<string>(
                    """
                    SELECT a.attname
                    FROM pg_index i
                    JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS keys(attnum, ord) ON TRUE
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = keys.attnum
                    WHERE i.indexrelid = to_regclass(format('%I.%I', current_schema(), @Index))
                    ORDER BY keys.ord;
                    """,
                    new { Index = name })).ToArray();
                Assert.Equal(expected.Columns, columns);
            }
        }
    }

    [Fact]
    public async Task SchemaHasNoForeignKeyConstraints()
    {
        // The reference opens Ent with migrate.WithForeignKeys(false), so the
        // generated foreign keys never reach PostgreSQL. The port must stay free of
        // constraints too, or existing databases would behave differently.
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        foreach (var table in ExpectedColumns.Keys)
        {
            var foreignKeys = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.table_constraints " +
                "WHERE table_schema = current_schema() AND table_name = @Table " +
                "AND constraint_type = 'FOREIGN KEY';",
                new { Table = table });
            Assert.Equal(0, foreignKeys);
        }
    }

    [Fact]
    public async Task MigrationIsIdempotent()
    {
        await using var db = new TestDatabase(fixture);

        await using (var first = await db.OpenMigratedAsync())
        {
            var count = await first.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE';");
            Assert.Equal(ExpectedTables.Length, count);
        }

        // The reference re-runs Ent auto-migration on every startup; re-applying
        // the ported DDL must succeed without changing the schema.
        await using var second = await db.OpenMigratedAsync();
        var secondCount = await second.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE';");
        Assert.Equal(ExpectedTables.Length, secondCount);
    }

    private sealed class ColumnInfo
    {
        public string ColumnName { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;
    }
}
