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
            CurrentRuleId: "europe-1700-1.4.7.1-server-v57",
            RecognizedRuleIds:
            [
                "europe-1700-1.4.7.1-server-v55",
                "europe-1700-1.4.7.1-server-v57"
            ],
            BuildPlan: static context => CoopCompatibilityPatcher.BuildEurope1700Plan(
                context.Selected,
                context.InstalledById,
                context.ModulesRoot,
                context.Blockers,
                context.Warnings,
                context.Proposed,
                context.SelectedIds),
            CreateBridgeOptions: static module => new CompatibilityRecipeBridgeOptions(
                CoopCompatibilityPatcher.Europe1700AuthorityRules,
                CoopCompatibilityPatcher.Europe1700ContentExclusions,
                CoopCompatibilityPatcher.Europe1700ServerFileRedirects,
                CoopCompatibilityPatcher.CreateEurope1700SchemaOverlays(module),
                CoopCompatibilityPatcher.CreateEurope1700ClientAssemblyResolves(),
                CoopCompatibilityPatcher.Europe1700ServerMapTerrainSizes),
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
    Func<BannerlordModule, CompatibilityRecipeBridgeOptions> CreateBridgeOptions,
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
    ICollection<string> SelectedIds);

internal sealed record CompatibilityRecipeBridgeOptions(
    IReadOnlyList<BridgeAuthorityRule> AuthorityRules,
    IReadOnlyList<BridgeContentExclusion> ContentExclusions,
    IReadOnlyList<BridgeServerFileRedirect> ServerFileRedirects,
    IReadOnlyList<BridgeServerXmlOverlay> ServerXmlOverlays,
    IReadOnlyList<BridgeClientAssemblyResolve> ClientAssemblyResolves,
    IReadOnlyList<BridgeServerMapTerrainSize> ServerMapTerrainSizes);

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
        BridgeRuntimeFeature.ServerFailedIdCompatibility
    ];
}
