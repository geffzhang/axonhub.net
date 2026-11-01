using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Persistence seam for the reference <c>prompt_protection_rules</c> table
/// (internal/ent/schema/prompt_protection_rule.go).
/// </summary>
public interface IPromptProtectionRuleStore
{
    Task<long> CreateAsync(
        PromptProtectionRuleRecord rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live rules with status <c>enabled</c>, ordered by id. This is the query
    /// the reference rule service runs to fill its live rule cache
    /// (internal/server/biz/prompt_protection_rule.go, ListEnabled).
    /// </summary>
    Task<IReadOnlyList<PromptProtectionRuleRecord>> ListEnabledAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>prompt_protection_rules</c> table. Writes go
/// through EF Core; reads use Dapper and honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class PromptProtectionRuleStore(AxonHubDbContext db) : IPromptProtectionRuleStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "pattern", "status", "settings"
        """;

    public async Task<long> CreateAsync(
        PromptProtectionRuleRecord rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        db.PromptProtectionRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rule.Id;
    }

    public async Task<IReadOnlyList<PromptProtectionRuleRecord>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        // The reference query is
        //   client.PromptProtectionRule.Query().
        //     Where(promptprotectionrule.StatusEQ(objects.PromptProtectionRuleStatusEnabled)).
        //     Order(ent.Asc(promptprotectionrule.FieldID)).
        //     All(ctx)
        // and Ent's SoftDeleteMixin adds the deleted_at = 0 predicate.
        var sql = "SELECT " + Columns
            + " FROM \"prompt_protection_rules\""
            + " WHERE \"status\" = 'enabled' AND \"deleted_at\" = 0 ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PromptProtectionRuleRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
