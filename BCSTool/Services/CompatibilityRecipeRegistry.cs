using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Central registry for compatibility targets that are supported by the
/// user-facing prepare/install lifecycle. Static analysis and experimental
/// planners are deliberately not promoted to supported recipes here.
/// </summary>
internal static class CompatibilityRecipeRegistry
{
    private static readonly CompatibilityRecipeDefinition[] Recipes =
    [
        new(
            Id: "europe-1700",
            DisplayName: "Empires of Europe 1700",
            RootModuleId: "Europe1700",
            PreparedModuleIds: ["Europe1700"],
            CurrentRuleId: "europe-1700-1.4.7.1-server-v58",
            RecognizedRuleIds:
            [
                "europe-1700-1.4.7.1-server-v55",
                "europe-1700-1.4.7.1-server-v57",
                "europe-1700-1.4.7.1-server-v58"
            ],
            BuildPlan: static context => CoopCompatibilityPatcher.BuildEurope1700Plan(
                context.Selected,
                context.InstalledById,
                context.ModulesRoot,
                context.Blockers,
                context.Warnings,
                context.Proposed,
                context.SelectedIds,
                context.SelectedDllNames,
                context.DisabledDllNames),
            CreateBridgeOptions: static (module, selectedDllNames, disabledDllNames) =>
                new CompatibilityRecipeBridgeOptions(
                CoopCompatibilityPatcher.Europe1700AuthorityRules
                    .Where(rule => selectedDllNames.Contains(
                        rule.DllName,
                        StringComparer.OrdinalIgnoreCase))
                    .ToArray(),
                CoopCompatibilityPatcher.Europe1700ContentExclusions
                    .Where(exclusion =>
                        selectedDllNames.Contains(
                            "ClansResourceAdder.dll",
                            StringComparer.OrdinalIgnoreCase) ||
                        !exclusion.RelativePath.Equals(
                            "bin/Win64_Shipping_Server/conf_clans_resource_adder.xml",
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                CoopCompatibilityPatcher.Europe1700ServerFileRedirects,
                CoopCompatibilityPatcher.CreateEurope1700SchemaOverlays(module),
                selectedDllNames.Contains(
                    "BattleArtilleryReworked.dll",
                    StringComparer.OrdinalIgnoreCase)
                    ? CoopCompatibilityPatcher.CreateEurope1700ClientAssemblyResolves()
                    : Array.Empty<BridgeClientAssemblyResolve>(),
                CoopCompatibilityPatcher.Europe1700ServerMapTerrainSizes,
                disabledDllNames
                    .Select(dllName => new BridgeDisabledSubModule(module.Id, dllName))
                    .ToArray(),
                selectedDllNames
                    .Where(dllName => CoopCompatibilityPatcher.Europe1700ClientOnlyDlls.Contains(
                        dllName,
                        StringComparer.OrdinalIgnoreCase))
                    .Select(dllName => new BridgeClientOnlySubModule(module.Id, dllName))
                    .ToArray(),
                BattleSceneCatalogContractRegistry.CreateEurope1700()),
            RuntimeFeatures: BridgeRuntimeFeatureSets.Europe1700,
            CampaignSaveDescription: "EOE",
            SupportsPopulationGuide: true)
    ];

    internal static IReadOnlyList<CompatibilityRecipeDefinition> All => Recipes;

    internal static CompatibilityRecipeDefinition? FindByRootModule(string moduleId) =>
        Recipes.SingleOrDefault(recipe =>
            recipe.RootModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase));

    internal static CompatibilityRecipeDefinition? FindByRuleId(string ruleId) =>
        Recipes.SingleOrDefault(recipe =>
            recipe.RecognizedRuleIds.Contains(ruleId, StringComparer.Ordinal));
}

internal sealed record CompatibilityRecipeDefinition(
    string Id,
    string DisplayName,
    string RootModuleId,
    IReadOnlyList<string> PreparedModuleIds,
    string CurrentRuleId,
    IReadOnlyList<string> RecognizedRuleIds,
    Action<CompatibilityRecipePlanContext> BuildPlan,
    Func<
        BannerlordModule,
        IReadOnlyCollection<string>,
        IReadOnlyCollection<string>,
        CompatibilityRecipeBridgeOptions> CreateBridgeOptions,
    IReadOnlyList<BridgeRuntimeFeature> RuntimeFeatures,
    string CampaignSaveDescription,
    bool SupportsPopulationGuide);

internal sealed record CompatibilityRecipePlanContext(
    BannerlordModule Selected,
    IReadOnlyDictionary<string, BannerlordModule> InstalledById,
    string ModulesRoot,
    ICollection<string> Blockers,
    ICollection<string> Warnings,
    ICollection<CoopCompatibilityPatcher.PendingChange> Proposed,
    ICollection<string> SelectedIds,
    IReadOnlyCollection<string> SelectedDllNames,
    IReadOnlyCollection<string> DisabledDllNames);

internal sealed record CompatibilityRecipeBridgeOptions(
    IReadOnlyList<BridgeAuthorityRule> AuthorityRules,
    IReadOnlyList<BridgeContentExclusion> ContentExclusions,
    IReadOnlyList<BridgeServerFileRedirect> ServerFileRedirects,
    IReadOnlyList<BridgeServerXmlOverlay> ServerXmlOverlays,
    IReadOnlyList<BridgeClientAssemblyResolve> ClientAssemblyResolves,
    IReadOnlyList<BridgeServerMapTerrainSize> ServerMapTerrainSizes,
    IReadOnlyList<BridgeDisabledSubModule> DisabledSubModules,
    IReadOnlyList<BridgeClientOnlySubModule> ClientOnlySubModules,
    BridgeBattleSceneCatalogContract? BattleSceneCatalogContract);

internal static class BridgeRuntimeFeatureSets
{
    internal static readonly BridgeRuntimeFeature[] Europe1700 =
    [
        BridgeRuntimeFeature.ClientMapEventCompatibility,
        BridgeRuntimeFeature.ClientRegistryLifecycleCompatibility,
        BridgeRuntimeFeature.ClientMapEventPositionAuthority,
        BridgeRuntimeFeature.ClientTroopUpgradeLoadRepair,
        BridgeRuntimeFeature.ClientSetDisorganizedDiagnostic,
        BridgeRuntimeFeature.ClientTroopRosterSequenceDiagnostic,
        BridgeRuntimeFeature.ClientCharacterCreationLifecycleCompatibility,
        BridgeRuntimeFeature.ServerRegistryLifecycleCompatibility,
        BridgeRuntimeFeature.ServerPopulationControl,
        BridgeRuntimeFeature.ServerFailedIdCompatibility,
        BridgeRuntimeFeature.ClientDeterministicBattleSceneProjection,
        BridgeRuntimeFeature.ServerEurope1700ShieldProductionSuppression
    ];
}
