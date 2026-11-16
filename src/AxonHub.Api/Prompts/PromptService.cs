using AxonHub.Persistence;

namespace AxonHub.Api.Prompts;

/// <summary>
/// Port of orchestrator.PromptProvider (internal/server/orchestrator/prompt.go):
/// the enabled prompts of one project.
/// </summary>
public interface IPromptProvider
{
    Task<IReadOnlyList<Prompt>> GetEnabledPromptsAsync(
        long projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read side of the reference's enabled prompt cache
/// (biz.PromptService.GetEnabledPrompts). The reference serves the list from a
/// per-project cache refreshed every minute and after in-process CRUD; the
/// port reads the database once per request (difference C187). A malformed
/// settings column is an error: the reference's Ent scan fails the whole
/// query and the orchestrator then fails open.
/// </summary>
public sealed class PromptService(IPromptStore store) : IPromptProvider
{
    public async Task<IReadOnlyList<Prompt>> GetEnabledPromptsAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var records = await store
            .ListEnabledByProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        var prompts = new List<Prompt>(records.Count);
        foreach (var record in records)
        {
            prompts.Add(new Prompt
            {
                Id = record.Id,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
                Name = record.Name,
                Description = record.Description,
                Role = record.Role,
                Content = record.Content,
                Status = record.Status,
                Order = record.Order,
                ProjectId = record.ProjectId,
                Settings = PromptSettings.FromJson(record.Settings),
            });
        }

        return prompts;
    }
}
