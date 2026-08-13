namespace BCSTool.Models;

public enum CoopPreparationChangeKind
{
    ReplaceFile,
    CreateFile
}

public sealed record CoopPreparationChange(
    CoopPreparationChangeKind Kind,
    string TargetPath,
    string Description,
    string OriginalSha256,
    string ProposedSha256);

/// <summary>
/// Immutable preview of a compatibility transformation. Proposed file bytes
/// remain private to the patch service; this public surface is safe to display.
/// </summary>
public sealed class CoopPreparationPlan
{
    public required string PlanId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string ServerRoot { get; init; }
    public required string RuleId { get; init; }
    public required IReadOnlyList<string> ModuleIds { get; init; }
    public required IReadOnlyList<string> Blockers { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<CoopPreparationChange> Changes { get; init; }

    public bool CanApply => Blockers.Count == 0 && Changes.Count > 0;

    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"Rule: {RuleId}",
                $"Modules: {string.Join(", ", ModuleIds)}",
                $"Planned file changes: {Changes.Count}"
            };

            if (Blockers.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("BLOCKED:");
                lines.AddRange(Blockers.Select(blocker => "- " + blocker));
            }

            if (Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Warnings:");
                lines.AddRange(Warnings.Select(warning => "- " + warning));
            }

            if (Changes.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Changes:");
                lines.AddRange(Changes.Select(change =>
                    $"- {change.Description}: {change.TargetPath}"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed record CoopPreparationResult(
    string PlanId,
    string BackupDirectory,
    string ManifestPath,
    IReadOnlyList<string> ModuleIds);
