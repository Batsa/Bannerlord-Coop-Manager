namespace BCSTool.Services;

/// <summary>
/// Fully resolved process launch information. Environment overrides apply only
/// to the child process and never mutate BCS Tool's own environment.
/// </summary>
public sealed record ServerLaunchPlan(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<string> ActiveModuleIds,
    bool UsesManagedModuleProfile);
