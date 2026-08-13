namespace BCSTool.Models;

/// <summary>
/// IDs reported by Coop's released coop.debug.players.list command.
/// Different debug commands require different object IDs, so these values
/// must not be treated as interchangeable.
/// </summary>
public sealed class CoopPlayerIdentity
{
    public string ControllerId { get; init; } = "";
    public string HeroId { get; init; } = "";
    public string PartyId { get; init; } = "";
    public string ClanId { get; init; } = "";
    public bool IsLocalPlayer { get; init; }

    /// <summary>
    /// Coop controller IDs commonly use &lt;prefix&gt;_&lt;player name&gt;.
    /// Commands accepting a hero name use the portion after the first
    /// underscore, matching Coop's own documented convention.
    /// </summary>
    public string PlayerName
    {
        get
        {
            var separator = ControllerId.IndexOf('_');

            var name =
                separator >= 0 && separator + 1 < ControllerId.Length
                    ? ControllerId[(separator + 1)..]
                    : ControllerId;

            return name.Replace(' ', '_');
        }
    }

    public string DisplayName =>
        IsLocalPlayer
            ? $"{PlayerName} (you)"
            : PlayerName;
}
