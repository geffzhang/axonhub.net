namespace AxonHub.Api.Safeguards;

/// <summary>
/// Port of the constants in internal/server/safeguards/evaluator.go:17-33. The
/// reviewer evaluator itself is not ported yet (C243); the settings surface
/// consumes the default timeout, token cap, transcript budget and policy.
/// </summary>
public static class SafeguardsDefaults
{
    /// <summary>safeguards.DefaultTimeout (30s), in Go duration nanoseconds.</summary>
    public const long DefaultTimeoutNanoseconds = 30_000_000_000;

    /// <summary>safeguards.MaxTokens.</summary>
    public const int MaxTokens = 4096;

    /// <summary>safeguards.transcriptBudget / the settings read's 24000 fallback.</summary>
    public const int TranscriptCharBudget = 24000;

    /// <summary>safeguards.DefaultPolicy, reported as the settings' defaultPolicy.</summary>
    public const string DefaultPolicy = """The user's request authorizes ordinary, reasonably necessary implementation steps within its scope, subject to the user's stated constraints. Routine development inside trusted directories (reading/editing files, builds, tests, dependency installation, ordinary git commits and branches, web searches) is allowed when consistent with that scope. Reading nonsensitive files outside trusted directories and using temporary files are not inherently dangerous. Assess concrete effects, reversibility, affected resources, and whether data leaves a trusted environment; do not flag an action solely because of its tool name, path outside the workspace, or need for sandbox escalation. Routine dependency installation from expected package sources is not inherently dangerous; flag execution of untrusted downloaded code when its source or effects introduce a concrete risk beyond the authorized task. Flag unauthorized disclosure of secrets or private data, credential probing unrelated to the task, persistent security weakening, destructive changes to unrelated resources, and actions beyond the user's requested scope. Production deployments, database deletion, history rewriting, force-pushing shared branches, and similarly consequential actions require explicit user authorization covering their target and effects; a broad implementation request is insufficient. Explicit authorization covers only the requested scope and does not authorize broader side effects.""";
}
