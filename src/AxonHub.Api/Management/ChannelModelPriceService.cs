using System.Text;
using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of biz.SaveChannelModelPriceInput (channel_price.go:24-27).</summary>
public sealed record SaveChannelModelPriceInput(string ModelId, ModelPrice Price);

/// <summary>Arguments of ChannelService.SaveChannelModelPrices.</summary>
public sealed record SaveChannelModelPricesCommand(
    long ChannelId,
    IReadOnlyList<SaveChannelModelPriceInput> Inputs);

/// <summary>
/// Port of ChannelService.SaveChannelModelPrices (channel_price.go:374-508): the
/// GraphQL resolver's write half of the channel pricing admin surface. The
/// reference refreshes its in-memory price cache after commit; the port reads
/// prices live per request (C029-style), so only the persisted effects remain.
/// </summary>
public sealed class ChannelModelPriceService(IChannelModelPriceStore prices)
{
    /// <summary>
    /// Mirrors the reference's ordered action plan: inputs first (create for
    /// absent models, update when the decoded price differs, skip when equal),
    /// then one delete per live row whose model is absent from the inputs.
    /// Deleted rows are not part of the returned list.
    /// </summary>
    public async Task<IReadOnlyList<ChannelModelPriceRecord>> SaveAsync(
        SaveChannelModelPricesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var seenModelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in command.Inputs)
        {
            if (!seenModelIds.Add(input.ModelId))
            {
                throw new InvalidOperationException(
                    "duplicate model price input: model_id=" + input.ModelId);
            }

            try
            {
                ModelPriceValidation.ValidateModelPrice(input.Price);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"invalid model price: model_id={input.ModelId}: {ex.Message}",
                    ex);
            }
        }

        IReadOnlyList<ChannelModelPriceRecord> existing;
        try
        {
            existing = await prices.ListByChannelAsync(command.ChannelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(
                "failed to query existing channel model prices: " + ex.Message,
                ex);
        }

        var existingByModelId = new Dictionary<string, ChannelModelPriceRecord>(StringComparer.Ordinal);
        foreach (var row in existing)
        {
            existingByModelId[row.ModelId] = row;
        }

        var now = DateTime.UtcNow;
        var changes = new List<ChannelModelPriceChange>(command.Inputs.Count + existing.Count);
        var inputModelIds = new HashSet<string>(
            command.Inputs.Select(static input => input.ModelId),
            StringComparer.Ordinal);

        foreach (var input in command.Inputs)
        {
            if (!existingByModelId.TryGetValue(input.ModelId, out var row))
            {
                changes.Add(new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    input.ModelId,
                    null,
                    SerializePrice(input.Price),
                    GenerateReferenceId(),
                    null));
                continue;
            }

            ModelPrice existingPrice;
            try
            {
                existingPrice = DeserializePrice(row.Price);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to query existing channel model prices: " + ex.Message,
                    ex);
            }

            if (ModelPriceValidation.ModelPriceEquals(existingPrice, input.Price))
            {
                changes.Add(new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Skip,
                    input.ModelId,
                    row.Id,
                    null,
                    null,
                    row));
                continue;
            }

            changes.Add(new ChannelModelPriceChange(
                ChannelModelPriceChangeType.Update,
                input.ModelId,
                row.Id,
                SerializePrice(input.Price),
                GenerateReferenceId(),
                row));
        }

        foreach (var row in existing)
        {
            if (!inputModelIds.Contains(row.ModelId))
            {
                changes.Add(new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Delete,
                    row.ModelId,
                    row.Id,
                    null,
                    null,
                    row));
            }
        }

        IReadOnlyList<ChannelModelPriceRecord?> applied;
        try
        {
            applied = await prices.ApplyAsync(command.ChannelId, changes, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var results = new List<ChannelModelPriceRecord>(applied.Count);
        foreach (var row in applied)
        {
            if (row is not null)
            {
                results.Add(row);
            }
        }

        return results;
    }

    /// <summary>objects.ModelPrice JSON as ent stores it (jsonb).</summary>
    public static string SerializePrice(ModelPrice price)
        => JsonSerializer.Serialize(price, BillingJson.Options);

    /// <summary>
    /// Decodes a stored price. JSON <c>null</c> unmarshals into the Go zero
    /// value, i.e. an empty ModelPrice, exactly like encoding/json.
    /// </summary>
    public static ModelPrice DeserializePrice(string? json)
    {
        if (json is null)
        {
            return new ModelPrice();
        }

        return JsonSerializer.Deserialize<ModelPrice>(json, BillingJson.Options)
            ?? new ModelPrice();
    }

    /// <summary>Port of generateReferenceID (channel_price.go:534-544).</summary>
    public static string GenerateReferenceId()
    {
        const string letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        var builder = new StringBuilder(8);
        for (var index = 0; index < 8; index++)
        {
            builder.Append(letters[Random.Shared.Next(letters.Length)]);
        }

        return builder.ToString();
    }
}
