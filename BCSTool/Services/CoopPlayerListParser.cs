using System.Text.RegularExpressions;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Parses the released Coop output from coop.debug.players.list.
/// </summary>
public sealed class CoopPlayerListParser
{
    private static readonly Regex ControllerRegex = new(
        @"-\s*ControllerId:\s*(?<id>\S+)(?<local>\s+\(you\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ObjectRegex = new(
        @"\b(?<kind>Hero|Party|Clan):\s*(?<id>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool TryParse(
        string output,
        out IReadOnlyList<CoopPlayerIdentity> players)
    {
        players = Array.Empty<CoopPlayerIdentity>();

        if (
            string.IsNullOrWhiteSpace(output) ||
            !output.Contains(
                "PlayerObjects entries",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parsed = new List<CoopPlayerIdentity>();
        PlayerBuilder? current = null;

        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var controllerMatch = ControllerRegex.Match(line);

            if (controllerMatch.Success)
            {
                AddCurrent(parsed, current);

                current = new PlayerBuilder
                {
                    ControllerId = controllerMatch.Groups["id"].Value,
                    IsLocalPlayer = controllerMatch.Groups["local"].Success
                };

                continue;
            }

            if (current is null)
                continue;

            var objectMatch = ObjectRegex.Match(line);

            if (!objectMatch.Success)
                continue;

            var id = objectMatch.Groups["id"].Value;

            // Ignore Coop's explicit missing/unresolved marker. Commands
            // cannot safely target it.
            if (id.StartsWith("<", StringComparison.Ordinal))
                id = "";

            switch (objectMatch.Groups["kind"].Value.ToUpperInvariant())
            {
                case "HERO":
                    current.HeroId = id;
                    break;

                case "PARTY":
                    current.PartyId = id;
                    break;

                case "CLAN":
                    current.ClanId = id;
                    break;
            }
        }

        AddCurrent(parsed, current);

        players = parsed
            .OrderByDescending(player => player.IsLocalPlayer)
            .ToArray();

        return true;
    }

    private static void AddCurrent(
        ICollection<CoopPlayerIdentity> players,
        PlayerBuilder? current)
    {
        if (current is null || string.IsNullOrWhiteSpace(current.ControllerId))
            return;

        players.Add(
            new CoopPlayerIdentity
            {
                ControllerId = current.ControllerId,
                HeroId = current.HeroId,
                PartyId = current.PartyId,
                ClanId = current.ClanId,
                IsLocalPlayer = current.IsLocalPlayer
            });
    }

    private sealed class PlayerBuilder
    {
        public string ControllerId { get; init; } = "";
        public string HeroId { get; set; } = "";
        public string PartyId { get; set; } = "";
        public string ClanId { get; set; } = "";
        public bool IsLocalPlayer { get; init; }
    }
}
