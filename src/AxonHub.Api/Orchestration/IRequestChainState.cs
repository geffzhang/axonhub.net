namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the id accessors of orchestrator.PersistenceState
/// (internal/server/orchestrator/state.go): the request row and the current
/// execution row the persistence middleware pair has already written. The live
/// preview middleware reads them after persistRequestExecution created the
/// execution for this attempt, so both ids are populated when it registers the
/// buffers.
/// </summary>
public interface IRequestChainState
{
    /// <summary>The request row id; 0 before the inbound hook created it.</summary>
    long CurrentRequestId { get; }

    /// <summary>The current attempt's execution row id; 0 before it was created.</summary>
    long CurrentExecutionId { get; }
}
