using Dapper;
using Microsoft.EntityFrameworkCore;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>
/// One planned price mutation from biz.calculatePriceChanges
/// (channel_price.go:315-380). The service computes the ordered plan before
/// opening the store transaction, exactly like the reference.
/// </summary>
public enum ChannelModelPriceChangeType
{
    Create,
    Update,
    Delete,
    Skip,
}

/// <summary>
/// One entry of the ordered price-change plan. <see cref="Existing"/> is only
/// used by <see cref="ChannelModelPriceChangeType.Skip"/>: the reference
/// appends the already-loaded row to the result without writing.
/// </summary>
public sealed record ChannelModelPriceChange(
    ChannelModelPriceChangeType Type,
    string ModelId,
    long? ExistingId,
    string? PriceJson,
    string? ReferenceId,
    ChannelModelPriceRecord? Existing);

/// <summary>
/// Maps a raw <c>models.model_card</c> JSON document to the objects.ModelPrice
/// JSON to store, or null when the card yields no priced items
/// (modelCardToChannelModelPrice, channel_price.go:58-88). The Go wire shape
/// stays in the API layer's billing code.
/// </summary>
public delegate string? ModelCardPriceJsonFactory(string modelCardJson);

/// <summary>Port of generateReferenceID (channel_price.go:534-544).</summary>
public delegate string ChannelPriceReferenceIdFactory();

/// <summary>One resolved auto-price candidate (channelModelPriceTemplate).</summary>
public sealed record ChannelModelPriceTemplate(string ModelId, string PriceJson);

/// <summary>
/// Port of the scalar and edge predicates of ent.ChannelModelPriceWhereInput
/// (ent.graphql). The filter itself never restricts <c>deleted_at</c>, matching
/// the generated sqlgraph predicates used inside a nested edge filter.
/// </summary>
public sealed record ChannelModelPriceListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public long? IdGT { get; init; }

    public long? IdGTE { get; init; }

    public long? IdLT { get; init; }

    public long? IdLTE { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? CreatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; init; }

    public DateTime? CreatedAtGT { get; init; }

    public DateTime? CreatedAtGTE { get; init; }

    public DateTime? CreatedAtLT { get; init; }

    public DateTime? CreatedAtLTE { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? UpdatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; init; }

    public DateTime? UpdatedAtGT { get; init; }

    public DateTime? UpdatedAtGTE { get; init; }

    public DateTime? UpdatedAtLT { get; init; }

    public DateTime? UpdatedAtLTE { get; init; }

    public long? ChannelId { get; init; }

    public long? ChannelIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelIdIn { get; init; }

    public IReadOnlyList<long>? ChannelIdNotIn { get; init; }

    public string? ModelId { get; init; }

    public string? ModelIdNEQ { get; init; }

    public IReadOnlyList<string>? ModelIdIn { get; init; }

    public IReadOnlyList<string>? ModelIdNotIn { get; init; }

    public string? ModelIdGT { get; init; }

    public string? ModelIdGTE { get; init; }

    public string? ModelIdLT { get; init; }

    public string? ModelIdLTE { get; init; }

    public string? ModelIdContains { get; init; }

    public string? ModelIdHasPrefix { get; init; }

    public string? ModelIdHasSuffix { get; init; }

    public string? ModelIdEqualFold { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public string? ReferenceId { get; init; }

    public string? ReferenceIdNEQ { get; init; }

    public IReadOnlyList<string>? ReferenceIdIn { get; init; }

    public IReadOnlyList<string>? ReferenceIdNotIn { get; init; }

    public string? ReferenceIdGT { get; init; }

    public string? ReferenceIdGTE { get; init; }

    public string? ReferenceIdLT { get; init; }

    public string? ReferenceIdLTE { get; init; }

    public string? ReferenceIdContains { get; init; }

    public string? ReferenceIdHasPrefix { get; init; }

    public string? ReferenceIdHasSuffix { get; init; }

    public string? ReferenceIdEqualFold { get; init; }

    public string? ReferenceIdContainsFold { get; init; }

    /// <summary>
    /// Ent's <c>hasChannel</c>: true → an EXISTS over <c>channels</c>, false →
    /// NOT EXISTS. Generated sqlgraph does not re-apply the Channel soft-delete
    /// predicate.
    /// </summary>
    public bool? HasChannel { get; init; }

    public IReadOnlyList<ChannelListFilter>? HasChannelWith { get; init; }

    /// <summary>
    /// Ent's <c>hasVersions</c>: true → the price has a version row, false →
    /// NOT EXISTS.
    /// </summary>
    public bool? HasVersions { get; init; }

    public IReadOnlyList<ChannelModelPriceVersionListFilter>? HasVersionsWith { get; init; }

    public ChannelModelPriceListFilter? Not { get; init; }

    public IReadOnlyList<ChannelModelPriceListFilter>? And { get; init; }

    public IReadOnlyList<ChannelModelPriceListFilter>? Or { get; init; }
}

/// <summary>
/// Port of the scalar and edge predicates of
/// ent.ChannelModelPriceVersionWhereInput (ent.graphql).
/// </summary>
public sealed record ChannelModelPriceVersionListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public long? IdGT { get; init; }

    public long? IdGTE { get; init; }

    public long? IdLT { get; init; }

    public long? IdLTE { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? CreatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; init; }

    public DateTime? CreatedAtGT { get; init; }

    public DateTime? CreatedAtGTE { get; init; }

    public DateTime? CreatedAtLT { get; init; }

    public DateTime? CreatedAtLTE { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? UpdatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; init; }

    public DateTime? UpdatedAtGT { get; init; }

    public DateTime? UpdatedAtGTE { get; init; }

    public DateTime? UpdatedAtLT { get; init; }

    public DateTime? UpdatedAtLTE { get; init; }

    public long? ChannelId { get; init; }

    public long? ChannelIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelIdIn { get; init; }

    public IReadOnlyList<long>? ChannelIdNotIn { get; init; }

    public long? ChannelIdGT { get; init; }

    public long? ChannelIdGTE { get; init; }

    public long? ChannelIdLT { get; init; }

    public long? ChannelIdLTE { get; init; }

    public string? ModelId { get; init; }

    public string? ModelIdNEQ { get; init; }

    public IReadOnlyList<string>? ModelIdIn { get; init; }

    public IReadOnlyList<string>? ModelIdNotIn { get; init; }

    public string? ModelIdGT { get; init; }

    public string? ModelIdGTE { get; init; }

    public string? ModelIdLT { get; init; }

    public string? ModelIdLTE { get; init; }

    public string? ModelIdContains { get; init; }

    public string? ModelIdHasPrefix { get; init; }

    public string? ModelIdHasSuffix { get; init; }

    public string? ModelIdEqualFold { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public long? ChannelModelPriceId { get; init; }

    public long? ChannelModelPriceIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelModelPriceIdIn { get; init; }

    public IReadOnlyList<long>? ChannelModelPriceIdNotIn { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public DateTime? EffectiveStartAt { get; init; }

    public DateTime? EffectiveStartAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? EffectiveStartAtIn { get; init; }

    public IReadOnlyList<DateTime>? EffectiveStartAtNotIn { get; init; }

    public DateTime? EffectiveStartAtGT { get; init; }

    public DateTime? EffectiveStartAtGTE { get; init; }

    public DateTime? EffectiveStartAtLT { get; init; }

    public DateTime? EffectiveStartAtLTE { get; init; }

    public DateTime? EffectiveEndAt { get; init; }

    public DateTime? EffectiveEndAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? EffectiveEndAtIn { get; init; }

    public IReadOnlyList<DateTime>? EffectiveEndAtNotIn { get; init; }

    public DateTime? EffectiveEndAtGT { get; init; }

    public DateTime? EffectiveEndAtGTE { get; init; }

    public DateTime? EffectiveEndAtLT { get; init; }

    public DateTime? EffectiveEndAtLTE { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool EffectiveEndAtIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool EffectiveEndAtNotNil { get; init; }

    public string? ReferenceId { get; init; }

    public string? ReferenceIdNEQ { get; init; }

    public IReadOnlyList<string>? ReferenceIdIn { get; init; }

    public IReadOnlyList<string>? ReferenceIdNotIn { get; init; }

    public string? ReferenceIdGT { get; init; }

    public string? ReferenceIdGTE { get; init; }

    public string? ReferenceIdLT { get; init; }

    public string? ReferenceIdLTE { get; init; }

    public string? ReferenceIdContains { get; init; }

    public string? ReferenceIdHasPrefix { get; init; }

    public string? ReferenceIdHasSuffix { get; init; }

    public string? ReferenceIdEqualFold { get; init; }

    public string? ReferenceIdContainsFold { get; init; }

    /// <summary>
    /// Ent's <c>hasChannelModelPrice</c>: true → the version's parent price
    /// row exists, false → NOT EXISTS.
    /// </summary>
    public bool? HasChannelModelPrice { get; init; }

    public IReadOnlyList<ChannelModelPriceListFilter>? HasChannelModelPriceWith { get; init; }

    public ChannelModelPriceVersionListFilter? Not { get; init; }

    public IReadOnlyList<ChannelModelPriceVersionListFilter>? And { get; init; }

    public IReadOnlyList<ChannelModelPriceVersionListFilter>? Or { get; init; }
}

/// <summary>SQL fragments for ent.ChannelModelPriceWhereInput.</summary>
internal static class ChannelModelPriceFilterSql
{
    internal static string? Build(
        ChannelModelPriceListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        if (filter.IdIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        AppendComparison(predicates, alias, "created_at", filter.CreatedAt, "=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtIn, args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "channel_id", filter.ChannelId, "=", args);
        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdNEQ, "<>", args);
        if (filter.ChannelIdIn is not null)
        {
            AppendIdIn(predicates, alias, "channel_id", filter.ChannelIdIn, args);
        }

        if (filter.ChannelIdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "channel_id", filter.ChannelIdNotIn, args, negate: true);
        }

        AppendStringPredicates(
            predicates,
            alias,
            "model_id",
            filter.ModelId,
            filter.ModelIdNEQ,
            filter.ModelIdIn,
            filter.ModelIdNotIn,
            filter.ModelIdGT,
            filter.ModelIdGTE,
            filter.ModelIdLT,
            filter.ModelIdLTE,
            filter.ModelIdContains,
            filter.ModelIdHasPrefix,
            filter.ModelIdHasSuffix,
            filter.ModelIdEqualFold,
            filter.ModelIdContainsFold,
            args);

        AppendStringPredicates(
            predicates,
            alias,
            "reference_id",
            filter.ReferenceId,
            filter.ReferenceIdNEQ,
            filter.ReferenceIdIn,
            filter.ReferenceIdNotIn,
            filter.ReferenceIdGT,
            filter.ReferenceIdGTE,
            filter.ReferenceIdLT,
            filter.ReferenceIdLTE,
            filter.ReferenceIdContains,
            filter.ReferenceIdHasPrefix,
            filter.ReferenceIdHasSuffix,
            filter.ReferenceIdEqualFold,
            filter.ReferenceIdContainsFold,
            args);

        if (filter.HasChannel is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channels\" mpch WHERE mpch.\"id\" = "
                + alias + ".\"channel_id\")";
            predicates.Add(filter.HasChannel.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelWith is { Count: > 0 } channelFilters)
        {
            var channelPredicates = channelFilters
                .Select(channel => ChannelAdminStore.BuildFilter(channel, "mpchf", args))
                .Where(static channel => channel is not null)
                .ToArray();
            var channelCondition = Combine(channelPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channels\" mpchf WHERE mpchf.\"id\" = "
                + alias + ".\"channel_id\""
                + (channelCondition is null ? string.Empty : " AND " + channelCondition)
                + ")");
        }

        if (filter.HasVersions is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channel_model_price_versions\" mpv"
                + " WHERE mpv.\"channel_model_price_id\" = " + alias + ".\"id\")";
            predicates.Add(filter.HasVersions.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasVersionsWith is { Count: > 0 } versionFilters)
        {
            var versionPredicates = versionFilters
                .Select(version => ChannelModelPriceVersionFilterSql.Build(version, "mpv", args))
                .Where(static version => version is not null)
                .ToArray();
            var versionCondition = Combine(versionPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channel_model_price_versions\" mpv"
                + " WHERE mpv.\"channel_model_price_id\" = " + alias + ".\"id\""
                + (versionCondition is null ? string.Empty : " AND " + versionCondition)
                + ")");
        }

        if (filter.Not is not null)
        {
            var not = Build(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return Combine(predicates);
    }
}

/// <summary>SQL fragments for ent.ChannelModelPriceVersionWhereInput.</summary>
internal static class ChannelModelPriceVersionFilterSql
{
    internal static string? Build(
        ChannelModelPriceVersionListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        if (filter.IdIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        AppendComparison(predicates, alias, "created_at", filter.CreatedAt, "=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtIn, args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "channel_id", filter.ChannelId, "=", args);
        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdNEQ, "<>", args);
        if (filter.ChannelIdIn is not null)
        {
            AppendIdIn(predicates, alias, "channel_id", filter.ChannelIdIn, args);
        }

        if (filter.ChannelIdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "channel_id", filter.ChannelIdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdGT, ">", args);
        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdGTE, ">=", args);
        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdLT, "<", args);
        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdLTE, "<=", args);

        AppendStringPredicates(
            predicates,
            alias,
            "model_id",
            filter.ModelId,
            filter.ModelIdNEQ,
            filter.ModelIdIn,
            filter.ModelIdNotIn,
            filter.ModelIdGT,
            filter.ModelIdGTE,
            filter.ModelIdLT,
            filter.ModelIdLTE,
            filter.ModelIdContains,
            filter.ModelIdHasPrefix,
            filter.ModelIdHasSuffix,
            filter.ModelIdEqualFold,
            filter.ModelIdContainsFold,
            args);

        AppendComparison(
            predicates,
            alias,
            "channel_model_price_id",
            filter.ChannelModelPriceId,
            "=",
            args);
        AppendComparison(
            predicates,
            alias,
            "channel_model_price_id",
            filter.ChannelModelPriceIdNEQ,
            "<>",
            args);
        if (filter.ChannelModelPriceIdIn is not null)
        {
            AppendIdIn(
                predicates,
                alias,
                "channel_model_price_id",
                filter.ChannelModelPriceIdIn,
                args);
        }

        if (filter.ChannelModelPriceIdNotIn is not null)
        {
            AppendIdIn(
                predicates,
                alias,
                "channel_model_price_id",
                filter.ChannelModelPriceIdNotIn,
                args,
                negate: true);
        }

        AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        AppendComparison(predicates, alias, "status", filter.StatusNEQ, "<>", args);
        if (filter.StatusIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.StatusNotIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "effective_start_at", filter.EffectiveStartAt, "=", args);
        AppendComparison(predicates, alias, "effective_start_at", filter.EffectiveStartAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "effective_start_at", filter.EffectiveStartAtIn, args);
        AppendTimeIn(predicates, alias, "effective_start_at", filter.EffectiveStartAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "effective_start_at", filter.EffectiveStartAtGT, ">", args);
        AppendComparison(predicates, alias, "effective_start_at", filter.EffectiveStartAtGTE, ">=", args);
        AppendComparison(predicates, alias, "effective_start_at", filter.EffectiveStartAtLT, "<", args);
        AppendComparison(predicates, alias, "effective_start_at", filter.EffectiveStartAtLTE, "<=", args);

        AppendComparison(predicates, alias, "effective_end_at", filter.EffectiveEndAt, "=", args);
        AppendComparison(predicates, alias, "effective_end_at", filter.EffectiveEndAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "effective_end_at", filter.EffectiveEndAtIn, args);
        AppendTimeIn(predicates, alias, "effective_end_at", filter.EffectiveEndAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "effective_end_at", filter.EffectiveEndAtGT, ">", args);
        AppendComparison(predicates, alias, "effective_end_at", filter.EffectiveEndAtGTE, ">=", args);
        AppendComparison(predicates, alias, "effective_end_at", filter.EffectiveEndAtLT, "<", args);
        AppendComparison(predicates, alias, "effective_end_at", filter.EffectiveEndAtLTE, "<=", args);
        if (filter.EffectiveEndAtIsNil)
        {
            predicates.Add(alias + ".\"effective_end_at\" IS NULL");
        }

        if (filter.EffectiveEndAtNotNil)
        {
            predicates.Add(alias + ".\"effective_end_at\" IS NOT NULL");
        }

        AppendStringPredicates(
            predicates,
            alias,
            "reference_id",
            filter.ReferenceId,
            filter.ReferenceIdNEQ,
            filter.ReferenceIdIn,
            filter.ReferenceIdNotIn,
            filter.ReferenceIdGT,
            filter.ReferenceIdGTE,
            filter.ReferenceIdLT,
            filter.ReferenceIdLTE,
            filter.ReferenceIdContains,
            filter.ReferenceIdHasPrefix,
            filter.ReferenceIdHasSuffix,
            filter.ReferenceIdEqualFold,
            filter.ReferenceIdContainsFold,
            args);

        if (filter.HasChannelModelPrice is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channel_model_prices\" vmp"
                + " WHERE vmp.\"id\" = " + alias + ".\"channel_model_price_id\")";
            predicates.Add(filter.HasChannelModelPrice.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelModelPriceWith is { Count: > 0 } priceFilters)
        {
            var pricePredicates = priceFilters
                .Select(price => ChannelModelPriceFilterSql.Build(price, "vmp", args))
                .Where(static price => price is not null)
                .ToArray();
            var priceCondition = Combine(pricePredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channel_model_prices\" vmp"
                + " WHERE vmp.\"id\" = " + alias + ".\"channel_model_price_id\""
                + (priceCondition is null ? string.Empty : " AND " + priceCondition)
                + ")");
        }

        if (filter.Not is not null)
        {
            var not = Build(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return Combine(predicates);
    }
}

/// <summary>Persistence seam for the reference <c>channel_model_prices</c> table.</summary>
public interface IChannelModelPriceStore
{
    Task<long> CreateAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live (not soft-deleted) price row by id, or null when absent. This is
    /// the ChannelModelPrice.channelModelPrice back-edge lookup.
    /// </summary>
    Task<ChannelModelPriceRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>Live (not soft-deleted) prices of one channel, ordered by id.</summary>
    Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
        long channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live prices of a channel that is itself live and enabled. This is the
    /// query preloadModelPrices makes against the enabled-channel snapshot.
    /// </summary>
    Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
        long channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.SaveChannelModelPrices' transaction
    /// (channel_price.go:381-520). Delete/update actions archive the active
    /// version, create/update write the current row plus a new active version,
    /// deletes are soft-deletes, and the channel's updated_at is stamped so the
    /// reference's reload hook fires. Returns one result per action (null for
    /// deletes) in plan order.
    /// </summary>
    Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceChange> changes,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ensureChannelModelPrices (channel_price.go:262-315): fills prices
    /// for the given model IDs that have no live price yet, reading the costs
    /// from the model library. When a transaction is already open the writes
    /// join it (the reference's nested RunInTransaction); a standalone call
    /// opens and commits its own, like the reference. Returns whether any price
    /// row was created by this call.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The message already carries the reference prefix ("failed to query
    /// existing channel model prices", "failed to query model prices from model
    /// library", or "failed to auto-configure channel model price: model_id=...").
    /// </exception>
    Task<bool> EnsureAutoPricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of resolveChannelModelPriceTemplates (channel_price.go:142-191):
    /// de-duplicates the non-empty model IDs preserving their first occurrence,
    /// reads the enabled/disabled model cards in batches of 500, and converts
    /// each priced card into a template in input order. Callers that need to
    /// apply the same plan to many channels resolve it once, exactly like
    /// BulkCreateChannels (channel_bulk.go:100-104).
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The library query or card decoding failed; the message already carries
    /// "failed to query model prices from model library".
    /// </exception>
    Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveAutoPriceTemplatesAsync(
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of applyChannelModelPriceTemplates (channel_price.go:195-212): one
    /// <c>now</c> for the whole plan, then one create-if-missing per template.
    /// The reference runs it inside the caller's logical transaction; this
    /// method never opens one, so the ambient transaction (if any) is reused.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// A create failed; the message already carries
    /// "failed to auto-configure channel model price: model_id=...".
    /// </exception>
    Task<bool> ApplyAutoPriceTemplatesAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceTemplate> templates,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of DuplicateChannel's price copy (channel_duplicate.go:50-64):
    /// reads every live price of the source channel, then creates the current
    /// price and its initial active version for the target channel. One
    /// <c>now</c> is shared by the whole copy and every row gets a fresh
    /// reference ID. The caller must run it inside the duplicate transaction so
    /// the channel and its prices commit together.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The source query failed with "failed to query source channel model
    /// prices", or a row failed with "failed to copy channel model price".
    /// </exception>
    Task CopyChannelPricesAsync(
        long sourceChannelId,
        long targetChannelId,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default);

    /// <summary>Version rows of one price, ordered by id (test/read seam).</summary>
    Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
        long channelModelPriceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>channel_model_prices</c> table. Writes go through
/// EF Core; reads use Dapper and honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ChannelModelPriceStore(AxonHubDbContext db) : IChannelModelPriceStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "model_id", "price",
        "reference_id", "channel_id"
        """;

    private const string VersionColumns = """
        "id", "created_at", "updated_at", "channel_id", "model_id",
        "channel_model_price_id", "price", "status", "effective_start_at",
        "effective_end_at", "reference_id"
        """;

    public async Task<long> CreateAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(price);

        db.ChannelModelPrices.Add(price);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return price.Id;
    }

    public async Task<ChannelModelPriceRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"channel_model_prices\" WHERE \"id\" = @Id"
            + " AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ChannelModelPriceRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"channel_model_prices\" WHERE \"channel_id\" = @ChannelId"
            + " AND \"deleted_at\" = 0 ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelModelPriceRecord>(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        // GetEnabledChannel only holds channels with status = enabled and a live
        // soft-delete marker; an empty result therefore also covers a disabled
        // channel, matching the reference's lookup miss.
        var sql = """
            SELECT p."id", p."created_at", p."updated_at", p."deleted_at",
                   p."model_id", p."price", p."reference_id", p."channel_id"
            FROM "channel_model_prices" p
            INNER JOIN "channels" c ON c."id" = p."channel_id"
            WHERE p."channel_id" = @ChannelId
              AND p."deleted_at" = 0
              AND c."deleted_at" = 0
              AND c."status" = 'enabled'
            ORDER BY p."id";
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelModelPriceRecord>(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceChange> changes,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var results = new ChannelModelPriceRecord?[changes.Count];

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            switch (change.Type)
            {
                case ChannelModelPriceChangeType.Skip:
                    results[index] = change.Existing;
                    break;

                case ChannelModelPriceChangeType.Delete:
                    await ArchiveActiveVersionsAsync(
                            change.ExistingId!.Value,
                            now,
                            "failed to archive channel model price versions for delete",
                            cancellationToken)
                        .ConfigureAwait(false);

                    // The SoftDeleteMixin turns DeleteOne into an UPDATE:
                    // deleted_at becomes Unix seconds and updated_at refreshes.
                    int deleted;
                    try
                    {
                        deleted = await db.ChannelModelPrices
                            .Where(row => row.Id == change.ExistingId.Value && row.DeletedAt == 0)
                            .ExecuteUpdateAsync(
                                setters => setters
                                    .SetProperty(
                                        row => row.DeletedAt,
                                        DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                                    .SetProperty(row => row.UpdatedAt, DateTime.UtcNow),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new StoreFailureException(
                            "failed to delete channel model price: " + ex.Message,
                            ex);
                    }

                    if (deleted == 0)
                    {
                        throw new StoreFailureException(
                            "failed to delete channel model price: ent: channel model price not found",
                            new InvalidOperationException("ent: channel model price not found"));
                    }

                    results[index] = null;
                    break;

                case ChannelModelPriceChangeType.Create:
                    results[index] = await CreateCurrentAsync(channelId, change, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ChannelModelPriceChangeType.Update:
                    results[index] = await UpdateCurrentAsync(change, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException(
                        "unsupported channel model price change: " + change.Type);
            }
        }

        // Force update channel updated_at to trigger reload cache
        // (channel_price.go:503-505). The reference does not wrap this error;
        // UpdateOneID reports a missing (or soft-deleted) channel as not found.
        int touched;
        try
        {
            touched = await db.Channels
                .Where(row => row.Id == channelId && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }

        if (touched == 0)
        {
            throw new StoreFailureException(
                "ent: channel not found",
                new InvalidOperationException("ent: channel not found"));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
        long channelModelPriceId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + VersionColumns
            + " FROM \"channel_model_price_versions\""
            + " WHERE \"channel_model_price_id\" = @ChannelModelPriceId ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelModelPriceVersionRecord>(
            new CommandDefinition(
                sql,
                new { ChannelModelPriceId = channelModelPriceId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>channelModelPriceQueryBatchSize (channel_price.go:45).</summary>
    private const int QueryBatchSize = 500;

    public async Task<bool> EnsureAutoPricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        ArgumentNullException.ThrowIfNull(modelCardToPriceJson);
        ArgumentNullException.ThrowIfNull(referenceIdFactory);

        // uniqueNonEmptyModelIDs keeps the first occurrence order.
        var uniqueModelIds = new List<string>(modelIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelId in modelIds)
        {
            if (modelId.Length > 0 && seen.Add(modelId))
            {
                uniqueModelIds.Add(modelId);
            }
        }

        if (uniqueModelIds.Count == 0)
        {
            return false;
        }

        // ent's RunInTransaction reuses the enclosing logical transaction and
        // only opens one for a standalone call (channel_price.go:272).
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var changed = await EnsureAutoPricesCoreAsync(
                channelId,
                uniqueModelIds,
                modelCardToPriceJson,
                referenceIdFactory,
                cancellationToken)
            .ConfigureAwait(false);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> EnsureAutoPricesCoreAsync(
        long channelId,
        List<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken)
    {
        var existingModelIds = await ListExistingModelIdsAsync(channelId, modelIds, cancellationToken)
            .ConfigureAwait(false);
        var missingModelIds = new List<string>(modelIds.Count);
        foreach (var modelId in modelIds)
        {
            if (!existingModelIds.Contains(modelId))
            {
                missingModelIds.Add(modelId);
            }
        }

        if (missingModelIds.Count == 0)
        {
            return false;
        }

        var templates = await ResolveAutoPriceTemplatesAsync(
                missingModelIds,
                modelCardToPriceJson,
                cancellationToken)
            .ConfigureAwait(false);

        return await ApplyAutoPriceTemplatesAsync(
                channelId,
                templates,
                referenceIdFactory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveAutoPriceTemplatesAsync(
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        ArgumentNullException.ThrowIfNull(modelCardToPriceJson);

        // uniqueNonEmptyModelIDs keeps the first occurrence order.
        var uniqueModelIds = new List<string>(modelIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelId in modelIds)
        {
            if (modelId.Length > 0 && seen.Add(modelId))
            {
                uniqueModelIds.Add(modelId);
            }
        }

        if (uniqueModelIds.Count == 0)
        {
            return [];
        }

        return await ResolveTemplatesAsync(
                uniqueModelIds,
                modelCardToPriceJson,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ApplyAutoPriceTemplatesAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceTemplate> templates,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(referenceIdFactory);

        // applyChannelModelPriceTemplates stamps one now for the whole plan.
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var template in templates)
        {
            try
            {
                var created = await CreatePriceIfMissingAsync(
                        channelId,
                        template,
                        referenceIdFactory(),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                changed = changed || created;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to auto-configure channel model price: "
                        + "model_id=" + template.ModelId + ": " + ex.Message,
                    ex);
            }
        }

        return changed;
    }

    public async Task CopyChannelPricesAsync(
        long sourceChannelId,
        long targetChannelId,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referenceIdFactory);

        IReadOnlyList<ChannelModelPriceRecord> sourcePrices;
        try
        {
            // db.ChannelModelPrice.Query().Where(ChannelID(source)): the
            // SoftDeleteMixin predicate keeps only live rows.
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = await connection.QueryAsync<ChannelModelPriceRecord>(
                new CommandDefinition(
                    "SELECT " + Columns
                        + " FROM \"channel_model_prices\""
                        + " WHERE \"channel_id\" = @ChannelId AND \"deleted_at\" = 0"
                        + " ORDER BY \"id\";",
                    new { ChannelId = sourceChannelId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            sourcePrices = rows.AsList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to query source channel model prices: " + ex.Message,
                ex);
        }

        // time.Now() is stamped once for every copied row
        // (channel_duplicate.go:59).
        var now = DateTime.UtcNow;
        foreach (var source in sourcePrices)
        {
            try
            {
                await CreateCurrentPriceAsync(
                        targetChannelId,
                        source.ModelId,
                        source.Price,
                        now,
                        referenceIdFactory(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to copy channel model price: " + ex.Message,
                    ex);
            }
        }
    }

    /// <summary>
    /// Port of createChannelModelPrice (channel_price.go:238-260): a plain
    /// save (no conflict handling) plus the initial active version. The caller
    /// must already be inside the mutation transaction.
    /// </summary>
    private async Task CreateCurrentPriceAsync(
        long channelId,
        string modelId,
        string priceJson,
        DateTime now,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var entity = new ChannelModelPriceRecord
        {
            ChannelId = channelId,
            ModelId = modelId,
            Price = priceJson,
            ReferenceId = referenceId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            db.ChannelModelPrices.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ent's Save returns the driver error verbatim; EF wraps it in a
            // DbUpdateException, so the reference-visible text is the base one.
            throw new StoreFailureException(
                "failed to create channel model price: " + ex.GetBaseException().Message,
                ex);
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ListExistingModelIdsAsync(
        long channelId,
        List<string> modelIds,
        CancellationToken cancellationToken)
    {
        var existingModelIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            for (var start = 0; start < modelIds.Count; start += QueryBatchSize)
            {
                var batch = modelIds.Skip(start).Take(QueryBatchSize).ToArray();
                var rows = await connection.QueryAsync<string>(new CommandDefinition(
                    """
                    SELECT "model_id" FROM "channel_model_prices"
                    WHERE "channel_id" = @ChannelId
                      AND "model_id" = ANY(@ModelIds)
                      AND "deleted_at" = 0;
                    """,
                    new { ChannelId = channelId, ModelIds = batch },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                foreach (var modelId in rows)
                {
                    existingModelIds.Add(modelId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to query existing channel model prices: " + ex.Message,
                ex);
        }

        return existingModelIds;
    }

    private async Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveTemplatesAsync(
        List<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        CancellationToken cancellationToken)
    {
        // resolveChannelModelPriceTemplates keeps one card per model id; the
        // live-row unique index makes duplicates impossible.
        var modelCards = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            for (var start = 0; start < modelIds.Count; start += QueryBatchSize)
            {
                var batch = modelIds.Skip(start).Take(QueryBatchSize).ToArray();
                var rows = await connection.QueryAsync<ModelCardRow>(new CommandDefinition(
                    """
                    SELECT "model_id", "model_card" FROM "models"
                    WHERE "model_id" = ANY(@ModelIds)
                      AND "status" IN ('enabled', 'disabled')
                      AND "deleted_at" = 0;
                    """,
                    new { ModelIds = batch },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    modelCards[row.ModelId] = row.ModelCard;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to query model prices from model library: " + ex.Message,
                ex);
        }

        var templates = new List<ChannelModelPriceTemplate>(modelCards.Count);
        foreach (var modelId in modelIds)
        {
            if (!modelCards.TryGetValue(modelId, out var modelCard))
            {
                continue;
            }

            string? priceJson;
            try
            {
                priceJson = modelCardToPriceJson(modelCard);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ent decodes model_card while scanning the query rows, so a
                // malformed card fails under the same query prefix.
                throw new StoreFailureException(
                    "failed to query model prices from model library: " + ex.Message,
                    ex);
            }

            if (priceJson is null)
            {
                continue;
            }

            templates.Add(new ChannelModelPriceTemplate(modelId, priceJson));
        }

        return templates;
    }

    private async Task<bool> CreatePriceIfMissingAsync(
        long channelId,
        ChannelModelPriceTemplate template,
        string candidateReferenceId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO "channel_model_prices"
                    ("created_at", "updated_at", "deleted_at", "model_id", "price",
                     "reference_id", "channel_id")
                VALUES (@Now, @Now, 0, @ModelId, CAST(@Price AS jsonb), @ReferenceId, @ChannelId)
                ON CONFLICT ("channel_id", "model_id", "deleted_at") DO NOTHING;
                """,
                new
                {
                    Now = now,
                    ModelId = template.ModelId,
                    Price = template.PriceJson,
                    ReferenceId = candidateReferenceId,
                    ChannelId = channelId,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to create channel model price: " + ex.Message,
                ex);
        }

        ChannelModelPriceRecord? entity;
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            entity = await connection.QuerySingleOrDefaultAsync<ChannelModelPriceRecord>(
                new CommandDefinition(
                    "SELECT " + Columns
                        + " FROM \"channel_model_prices\""
                        + " WHERE \"channel_id\" = @ChannelId AND \"model_id\" = @ModelId"
                        + " AND \"deleted_at\" = 0;",
                    new { ChannelId = channelId, ModelId = template.ModelId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to load channel model price after upsert: " + ex.Message,
                ex);
        }

        if (entity is null)
        {
            throw new StoreFailureException(
                "failed to load channel model price after upsert: ent: channel model price not found",
                new InvalidOperationException("ent: channel model price not found"));
        }

        // The candidate reference id identifies whether this transaction won
        // the upsert race; only the winner creates the initial version
        // (channel_price.go:131-139).
        if (entity.ReferenceId != candidateReferenceId)
        {
            return false;
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed class ModelCardRow
    {
        public string ModelId { get; set; } = string.Empty;

        public string ModelCard { get; set; } = "{}";
    }

    private async Task<ChannelModelPriceRecord> CreateCurrentAsync(
        long channelId,
        ChannelModelPriceChange change,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var entity = new ChannelModelPriceRecord
        {
            ChannelId = channelId,
            ModelId = change.ModelId,
            Price = change.PriceJson ?? "null",
            ReferenceId = change.ReferenceId ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            db.ChannelModelPrices.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to create channel model price: " + ex.Message,
                ex);
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    private async Task<ChannelModelPriceRecord> UpdateCurrentAsync(
        ChannelModelPriceChange change,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await ArchiveActiveVersionsAsync(
                change.ExistingId!.Value,
                now,
                "failed to archive old channel model price versions",
                cancellationToken)
            .ConfigureAwait(false);

        ChannelModelPriceRecord? entity;
        try
        {
            entity = await db.ChannelModelPrices
                .SingleOrDefaultAsync(
                    row => row.Id == change.ExistingId.Value && row.DeletedAt == 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to update channel model price: " + ex.Message,
                ex);
        }

        if (entity is null)
        {
            throw new StoreFailureException(
                "failed to update channel model price: ent: channel model price not found",
                new InvalidOperationException("ent: channel model price not found"));
        }

        entity.Price = change.PriceJson ?? "null";
        entity.ReferenceId = change.ReferenceId ?? string.Empty;
        entity.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to update channel model price: " + ex.Message,
                ex);
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    private async Task ArchiveActiveVersionsAsync(
        long channelModelPriceId,
        DateTime now,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.ChannelModelPriceVersions
                .Where(row => row.ChannelModelPriceId == channelModelPriceId
                    && row.Status == "active")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, "archived")
                        .SetProperty(row => row.EffectiveEndAt, now)
                        .SetProperty(row => row.UpdatedAt, DateTime.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(errorPrefix + ": " + ex.Message, ex);
        }
    }

    private async Task InsertActiveVersionAsync(
        ChannelModelPriceRecord entity,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var version = new ChannelModelPriceVersionRecord
        {
            ChannelId = entity.ChannelId,
            ModelId = entity.ModelId,
            ChannelModelPriceId = entity.Id,
            Price = entity.Price,
            Status = "active",
            EffectiveStartAt = now,
            EffectiveEndAt = null,
            ReferenceId = entity.ReferenceId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        try
        {
            db.ChannelModelPriceVersions.Add(version);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to create channel model price version: "
                    + ex.GetBaseException().Message,
                ex);
        }
    }
}
