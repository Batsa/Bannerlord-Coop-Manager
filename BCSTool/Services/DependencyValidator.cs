using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Validates enabled modules against installed manifests and current order.
/// It reports problems but never changes operator selections.
/// </summary>
public sealed class DependencyValidator
{
    private static readonly HashSet<string> NonServerOfficialModules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CustomBattle",
            "StoryMode",
            "BirthAndDeath"
        };

    public IReadOnlyList<string> Validate(IReadOnlyList<BannerlordModule> modules)
    {
        var globalMessages = new List<string>();
        var byId = modules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var indexes = modules
            .Select((module, index) => (module.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            var messages = new List<string>();

            if (module.IsRequired && !module.IsInstalled)
                messages.Add("Required server module is missing.");

            if (module.Enabled && !module.IsInstalled && !module.IsRequired)
                messages.Add("Selected module is missing from engine\\Modules.");

            if (!module.Enabled)
            {
                module.SetValidationMessages(messages);
                continue;
            }

            foreach (var dependencyId in module.Dependencies)
            {
                if (NonServerOfficialModules.Contains(dependencyId))
                    continue;

                if (!byId.TryGetValue(dependencyId, out var dependency) || !dependency.IsInstalled)
                {
                    messages.Add($"Missing dependency: {dependencyId}.");
                    continue;
                }

                if (!dependency.Enabled)
                {
                    messages.Add($"Dependency is disabled: {dependencyId}.");
                    continue;
                }

            }

            foreach (var earlierId in module.MustLoadAfter)
            {
                if (byId.TryGetValue(earlierId, out var earlier) &&
                    earlier.Enabled &&
                    indexes[earlierId] > indexes[module.Id])
                {
                    messages.Add($"Dependency must load first: {earlierId}.");
                }
            }

            foreach (var laterId in module.MustLoadBefore)
            {
                if (byId.TryGetValue(laterId, out var later) &&
                    later.Enabled &&
                    indexes[module.Id] > indexes[laterId])
                {
                    messages.Add($"Must load before: {laterId}.");
                }
            }

            foreach (var incompatibleId in module.IncompatibleModules)
            {
                if (byId.TryGetValue(incompatibleId, out var incompatible) && incompatible.Enabled)
                    messages.Add($"Incompatible active module: {incompatibleId}.");
            }

            if (module.Id.Equals("DedicatedServer.Windows", StringComparison.OrdinalIgnoreCase) &&
                indexes.TryGetValue("Native", out var nativeIndex) &&
                indexes[module.Id] != nativeIndex + 1)
            {
                messages.Add("DedicatedServer.Windows must be immediately after Native.");
            }

            if (module.Id.StartsWith(
                    CoopBridgePackageBuilder.BridgeIdPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                modules.Skip(indexes[module.Id] + 1).Any(candidate =>
                    candidate.Enabled &&
                    !candidate.Id.StartsWith(
                        CoopBridgePackageBuilder.BridgeIdPrefix,
                        StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add("Generated BCS bridges must load after all active gameplay modules.");
            }

            if (module.Id.Equals("HexAntiCheat", StringComparison.OrdinalIgnoreCase))
                messages.Add("HexAntiCheat is obsolete and must remain disabled.");

            if (module.Id.Equals("RepeatableLordQuests", StringComparison.OrdinalIgnoreCase) &&
                byId.TryGetValue("HexServerPack", out var serverPack) &&
                serverPack.Enabled)
            {
                messages.Add("HexServerPack already includes RepeatableLordQuests; disable the standalone module.");
            }

            module.SetValidationMessages(messages);
        }

        foreach (var module in modules.Where(module => module.HasWarnings))
            globalMessages.Add($"{module.Id}: {string.Join(" ", module.ValidationMessages)}");

        return globalMessages;
    }
}
