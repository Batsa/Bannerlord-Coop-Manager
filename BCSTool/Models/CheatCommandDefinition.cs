namespace BCSTool.Models;

public enum CheatPlayerTarget
{
    None,
    ControllerId,
    HeroId,
    HeroName,
    PartyId,
    ClanId
}

/// <summary>
/// One verified command exposed by the installed Bannerlord Coop release.
/// </summary>
public sealed class CheatCommandDefinition
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Description { get; init; }
    public CheatPlayerTarget PlayerTarget { get; init; }
    public string AdditionalArgumentsHint { get; init; } = "No additional arguments";
    public bool RequiresAdditionalArguments { get; init; }

    public string DisplayName => Name;
}
