using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Creates and applies conservative, version-scoped dedicated-server
/// transformations. It never modifies client modules or protected Coop files.
/// </summary>
public sealed class CoopCompatibilityPatcher
{
    private const string RealmOfThronesRule = "realm-of-thrones-8.1.7-server-v2";
    private const string ContentOnlyRule = "content-only-server-v1";
    private const string GenericExecutableRule = "generic-executable-bridge-v1";
    private const long MaximumManifestCharacters = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    private static readonly HashSet<string> SupportedReleasedCoopVersions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "v0.1.1",
            "v0.1.2"
        };

    private static readonly HashSet<string> ProtectedModuleIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Coop",
            "Native",
            "SandBoxCore",
            "Sandbox",
            "DedicatedServer.Windows"
        };

    private static readonly HashSet<string> ClientOnlyOfficialDependencies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "StoryMode",
            "CustomBattle",
            "BirthAndDeath"
        };

    private static readonly string[] RealmOfThronesModuleIds =
        ["ROT-Core", "ROT-Content", "ROT-Dragon", "ROT_Map"];

    private static readonly string[] RequiredGameRuntimeFiles =
    [
        "TaleWorlds.CampaignSystem.dll",
        "TaleWorlds.Core.dll",
        "TaleWorlds.Library.dll",
        "TaleWorlds.Localization.dll",
        "TaleWorlds.ModuleManager.dll",
        "TaleWorlds.MountAndBlade.dll",
        "TaleWorlds.ObjectSystem.dll"
    ];

    private static readonly IReadOnlyDictionary<ushort, OpCode> IlOpCodes =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private static readonly HashSet<string> SupportedGameVersionCompatibilityPairs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "v1.4.7|v1.4.8"
        };

    private static readonly HashSet<string> FrameworkModuleIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Bannerlord.Harmony",
            "Bannerlord.ButterLib",
            "Bannerlord.UIExtenderEx",
            "Bannerlord.MBOptionScreen"
        };

    private static readonly HashSet<string> CoreRuntimeModuleIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Native",
            "SandBoxCore",
            "Sandbox"
        };

    internal static readonly string[] Europe1700ClientOnlyDlls =
    [
        "BannerColorPersistence.dll"
    ];

    internal static readonly BridgeAuthorityRule[] Europe1700AuthorityRules =
    [
        new(
            "Europe1700",
            "ClansResourceAdder.dll",
            "ClansResourceAdder.ResourcesAdderEvents",
            "AddResources",
            0,
            BridgeInvocationScope.ServerOnly),
        new(
            "Europe1700",
            "Bannerlord.EOEPatches.dll",
            "Bannerlord.EOEPatches.SubModule",
            "OnBeforeInitialModuleScreenSetAsRoot",
            0,
            BridgeInvocationScope.ClientOnly)
    ];

    internal static readonly BridgeContentExclusion[] Europe1700ContentExclusions =
    [
        new("Europe1700", "ModuleData/collision_infos.xml"),
        new("Europe1700", "ModuleData/action_sets.xml"),
        new("Europe1700", "ModuleData/action_types.xml"),
        new("Europe1700", "Prefabs/props_siege_trebuchets.xml"),
        new(
            "Europe1700",
            "bin/Win64_Shipping_Server/conf_clans_resource_adder.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_western_npcs.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_muslim_npcs.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_turkic_npcs.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_northern_npcs.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_eastern_npcs.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_aserai.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_italian.xml"),
        new("Europe1700", "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_khuzait.xml"),
        new("Europe1700", "ModuleData/lords_main/lords_ottoman_extra.xml"),
        new("Europe1700", "ModuleData/npccharacters/spnpccharacters_scottish.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets.xslt"),
        new("Europe1700", "ModuleData/trooptrees/spnpccharacters.xslt"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_baltic.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_battania.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_cossack.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_finnic.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_rus.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_scottish.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_sturgia.xml"),
        new("Europe1700", "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_welsh.xml"),
        new("Europe1700", "ModuleData/spworkshops.xml")
    ];

    private static readonly Europe1700SchemaRepair[] Europe1700SchemaRepairs =
    [
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_western_npcs.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            461),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_muslim_npcs.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            412),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_turkic_npcs.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            444),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_northern_npcs.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            455),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_eastern_npcs.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            383),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_aserai.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            69),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_italian.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            11),
        new(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_khuzait.xml",
            Europe1700SchemaRepairKind.EquipmentElementCase,
            63),
        new(
            "ModuleData/lords_main/lords_ottoman_extra.xml",
            Europe1700SchemaRepairKind.TraitsElementCase,
            24),
        new(
            "ModuleData/npccharacters/spnpccharacters_scottish.xml",
            Europe1700SchemaRepairKind.StrayElementTerminator,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets.xslt",
            Europe1700SchemaRepairKind.MergedBearskinEquipmentXslt,
            1),
        new(
            "ModuleData/trooptrees/spnpccharacters.xslt",
            Europe1700SchemaRepairKind.MergedNpcCompatibilityXslt,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_baltic.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_battania.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            9),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_cossack.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_finnic.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_rus.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_scottish.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            9),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_sturgia.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            1),
        new(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_welsh.xml",
            Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment,
            9),
        new(
            "ModuleData/spworkshops.xml",
            Europe1700SchemaRepairKind.WorkshopOutputCategories,
            5)
    ];

    internal static readonly BridgeServerFileRedirect[] Europe1700ServerFileRedirects =
    [
        new(
            "Europe1700",
            "ModuleData/DistanceCaches/settlements_distance_cache_Default.bin",
            string.Empty)
    ];

    internal static readonly BridgeServerMapTerrainSize[] Europe1700ServerMapTerrainSizes =
    [
        new(
            "Europe1700",
            "SceneObj/Main_map/scene.xscene",
            string.Empty,
            1696f,
            1696f,
            "DedicatedServer.Core",
            string.Empty,
            "SandBox",
            string.Empty)
    ];

    private static readonly IReadOnlyDictionary<string, Europe1700AnimationProjection>
        Europe1700AnimationProjections =
            new Dictionary<string, Europe1700AnimationProjection>(StringComparer.Ordinal)
            {
                ["act_ready_musket_cla"] = new("1_cla_ready_musket", "ready_crossbow"),
                ["act_release_musket_cla"] = new("1_cla_release_musket", "release_crossbow"),
                ["act_ready_continue_musket_cla"] = new("1_cla_ready_continue_musket", "ready_continue_crossbow"),
                ["act_reload_musket_cla"] = new("reznov_anim_reload_musket", "reload_crossbow"),
                ["act_reload_musket_continue_cla"] = new("reznov_anim_reload_musket_continue", "reload_crossbow_continue"),
                ["act_ready_cannon_cla"] = new("2_cla_ready_cannon", "ready_crossbow"),
                ["act_release_cannon_cla"] = new("2_cla_release_cannon", "release_crossbow"),
                ["act_ready_continue_cannon_cla"] = new("2_cla_ready_continue_cannon", "ready_continue_crossbow"),
                ["act_reload_cannon_cla"] = new("2_cla_reload_cannon", "reload_crossbow"),
                ["act_reload_cannon_continue_cla"] = new("2_cla_reload_cannon_continue", "reload_crossbow_continue"),
                ["act_reload_cannon_horseback_cla"] = new("2_cla_reload_cannon_horseback", "reload_crossbow_horseback"),
                ["act_reload_cannon_continue_horseback_cla"] = new("2_cla_reload_cannon_continue_horseback", "reload_crossbow_continue_horseback"),
                ["act_ready_pistol_cla"] = new("3_cla_ready_pistol", "ready_crossbow"),
                ["act_release_pistol_cla"] = new("3_cla_release_pistol", "release_crossbow"),
                ["act_ready_continue_pistol_cla"] = new("3_cla_ready_continue_pistol", "ready_continue_crossbow"),
                ["act_reload_musket_fast_cla"] = new("1b_cla_reload_musket_fast", "reload_crossbow_fast"),
                ["act_reload_musket_continue_fast_cla"] = new("1b_cla_reload_musket_continue_fast", "reload_crossbow_fast_continue"),
                ["act_release_revolver_cla"] = new("7_cla_release_revolver", "release_crossbow"),
                ["act_release_rifle_cla"] = new("5_cla_release_rifle", "release_crossbow"),
                ["act_release_bolt_rifle_cla"] = new("6_cla_release_bolt_rifle", "release_crossbow"),
                ["act_reload_rifle_cla"] = new("5_cla_reload_rifle", "reload_crossbow"),
                ["act_reload_bolt_rifle_continue_cla"] = new("6_cla_reload_bolt_rifle_continue", "reload_crossbow_continue"),
                ["cla_act_reload_bomb"] = new("cla_reload_bomb", "reload_crossbow_continue"),
                ["cla_act_cla_spear_idle_1"] = new("cla_spear_idle_1", "troop_stand_spear_1")
            };

    private readonly Dictionary<string, PendingPlan> _pendingPlans =
        new(StringComparer.Ordinal);
    private readonly CoopBridgePackageBuilder _bridgePackageBuilder = new();

    internal int PendingPlanCount => _pendingPlans.Count;

    public CoopPreparationPlan CreatePlan(
        BannerlordModule selected,
        IReadOnlyList<BannerlordModule> installedModules,
        string serverRoot,
        BridgeDllSelection? bridgeDllSelection = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(installedModules);

        var canonicalServerRoot = Path.GetFullPath(serverRoot);
        var modulesRoot = Path.Combine(canonicalServerRoot, "engine", "Modules");
        EnsureDirectChild(selected.Path, modulesRoot, "Selected module");

        var byId = installedModules
            .Where(module => module.IsInstalled)
            .ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var proposed = new List<PendingChange>();
        var selectedIds = new List<string>();
        string ruleId;

        if (ProtectedModuleIds.Contains(selected.Id))
        {
            blockers.Add($"Protected module '{selected.Id}' cannot be transformed.");
            ruleId = "none";
            selectedIds.Add(selected.Id);
        }
        else if (IsRealmOfThronesModule(selected.Id))
        {
            ruleId = RealmOfThronesRule;
            BuildRealmOfThronesPlan(
                byId,
                modulesRoot,
                blockers,
                warnings,
                proposed,
                selectedIds);
            if (blockers.Count == 0)
            {
                AddBridgePackage(
                    installedModules,
                    selectedIds,
                    canonicalServerRoot,
                    proposed,
                    selectedIds,
                    warnings);
            }
        }
        else if (CompatibilityRecipeRegistry.FindByRootModule(selected.Id) is { } recipe)
        {
            ruleId = recipe.CurrentRuleId;
            var dllSelection = ResolveBridgeDllSelection(selected, bridgeDllSelection);
            recipe.BuildPlan(new CompatibilityRecipePlanContext(
                selected,
                byId,
                modulesRoot,
                blockers,
                warnings,
                proposed,
                selectedIds,
                dllSelection.Selected,
                dllSelection.Disabled));
            if (blockers.Count == 0)
            {
                var options = recipe.CreateBridgeOptions(
                    selected,
                    dllSelection.Selected,
                    dllSelection.Disabled);
                AddBridgePackage(
                    installedModules,
                    selectedIds,
                    canonicalServerRoot,
                    proposed,
                    selectedIds,
                    warnings,
                    options.AuthorityRules,
                    options.ContentExclusions,
                    options.ServerFileRedirects,
                    options.ServerXmlOverlays,
                    options.ClientAssemblyResolves,
                    options.ServerMapTerrainSizes,
                    recipe.RuntimeFeatures,
                    recipe.CampaignSaveDescription,
                    options.DisabledSubModules,
                    options.ClientOnlySubModules);
            }
        }
        else
        {
            var family = BuildGenericDependencyFamily(
                selected,
                byId,
                modulesRoot,
                blockers,
                selectedIds);
            var hasExecutableCode = false;
            foreach (var module in family)
            {
                var manifest = LoadManifest(Path.Combine(module.Path, "SubModule.xml"));
                var declaredDlls = DeclaredDllNames(manifest).ToArray();
                hasExecutableCode |= declaredDlls.Length > 0;
                AddManifestTransformation(
                    module,
                    addCoopOrdering: !IsEarlyFramework(module),
                    enableDllNames: declaredDlls,
                    proposed,
                    suppressSubModules: IsEarlyFramework(module));
                if (declaredDlls.Length > 0)
                    AddClientDllProjection(module, proposed);
            }

            ruleId = hasExecutableCode ? GenericExecutableRule : ContentOnlyRule;
            var authorityRules = DiscoverMcmSettingsFallbackRules(family);
            if (blockers.Count == 0)
            {
                AddBridgePackage(
                    installedModules,
                    selectedIds,
                    canonicalServerRoot,
                    proposed,
                    selectedIds,
                    warnings,
                    authorityRules);
            }

            if (authorityRules.Count > 0)
            {
                warnings.Add(
                    $"BCS generated {authorityRules.Count} module-bound server settings fallback rule(s) " +
                    "for MCM-backed module lifecycle methods.");
            }

            warnings.Add(hasExecutableCode
                ? "BCS generated a generic, version-scoped Coop bridge and server projection. " +
                  "It does not invent synchronization for private mod state; authority adapters are still " +
                  "required when runtime tests expose custom state divergence."
                : "Content XML and a version-matched bridge package will be loaded by the server, but a real " +
                  "client join and campaign round-trip are still required before calling the module compatible.");
        }

        var planId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" +
                     Guid.NewGuid().ToString("N")[..8];
        var publicPlan = new CoopPreparationPlan
        {
            PlanId = planId,
            CreatedUtc = DateTimeOffset.UtcNow,
            ServerRoot = canonicalServerRoot,
            RuleId = ruleId,
            ModuleIds = selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Blockers = blockers.Distinct(StringComparer.Ordinal).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            Changes = proposed.Select(change => change.PublicChange).ToArray()
        };

        if (publicPlan.CanApply)
            _pendingPlans[planId] = new PendingPlan(publicPlan, proposed);
        return publicPlan;
    }

    /// <summary>
    /// Removes Windows internet-zone metadata from managed assemblies in the
    /// enabled bridge-managed module set. Unblocking an alternate data stream must
    /// never alter assembly bytes, so every file is hashed before and after.
    /// </summary>
    public int UnblockPreparedModuleAssemblies(
        string serverRoot,
        IReadOnlyCollection<string> enabledModuleIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        ArgumentNullException.ThrowIfNull(enabledModuleIds);

        var canonicalServerRoot = Path.GetFullPath(serverRoot);
        var modulesRoot = Path.Combine(canonicalServerRoot, "engine", "Modules");
        if (!Directory.Exists(modulesRoot))
            throw new DirectoryNotFoundException($"Dedicated-server Modules directory was not found: {modulesRoot}");

        var installedModules = new ModuleScanner()
            .Scan(modulesRoot)
            .ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);

        var blockedAssemblies = new List<BlockedAssemblyMarker>();
        foreach (var moduleId in enabledModuleIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                throw new InvalidDataException("Enabled module ID cannot be empty while unblocking assemblies.");

            if (!installedModules.TryGetValue(moduleId, out var installedModule))
                throw new DirectoryNotFoundException($"Enabled module was not found: {moduleId}");

            // Workshop imports retain their numeric folder name. Resolve the
            // canonical path from SubModule.xml instead of assuming folder == ID.
            var moduleRoot = Path.GetFullPath(installedModule.Path);
            EnsureDirectChild(moduleRoot, modulesRoot, $"Enabled module {moduleId}");
            if (!Directory.Exists(moduleRoot))
                throw new DirectoryNotFoundException($"Enabled module was not found: {moduleRoot}");
            if ((File.GetAttributes(moduleRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked enabled module is not safe to unblock: {moduleRoot}");
            foreach (var assemblyPath in Directory.EnumerateFiles(
                         moduleRoot,
                         "*.dll",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsRegularUnlinkedFileWithin(assemblyPath, moduleRoot))
                    throw new InvalidDataException($"Linked assembly is not safe to unblock: {assemblyPath}");

                var zoneIdentifier = assemblyPath + ":Zone.Identifier";
                if (!File.Exists(zoneIdentifier))
                    continue;

                blockedAssemblies.Add(new BlockedAssemblyMarker(
                    assemblyPath,
                    HashFile(assemblyPath),
                    zoneIdentifier,
                    File.ReadAllBytes(zoneIdentifier)));
            }
        }

        var removedMarkers = new List<BlockedAssemblyMarker>();
        try
        {
            foreach (var blockedAssembly in blockedAssemblies)
            {
                if (!File.Exists(blockedAssembly.ZoneIdentifierPath))
                {
                    throw new IOException(
                        $"Blocked-file marker changed during validation: {blockedAssembly.AssemblyPath}");
                }

                File.Delete(blockedAssembly.ZoneIdentifierPath);
                if (File.Exists(blockedAssembly.ZoneIdentifierPath))
                {
                    throw new IOException(
                        $"Windows did not remove the blocked-file marker: {blockedAssembly.AssemblyPath}");
                }

                removedMarkers.Add(blockedAssembly);
                var after = HashFile(blockedAssembly.AssemblyPath);
                if (!after.Equals(blockedAssembly.AssemblySha256, StringComparison.Ordinal))
                {
                    throw new IOException(
                        $"Assembly bytes changed while unblocking: {blockedAssembly.AssemblyPath}");
                }
            }

            return removedMarkers.Count;
        }
        catch (Exception unblockException)
        {
            var restoreErrors = new List<Exception>();
            foreach (var removedMarker in removedMarkers.AsEnumerable().Reverse())
            {
                try
                {
                    File.WriteAllBytes(
                        removedMarker.ZoneIdentifierPath,
                        removedMarker.ZoneIdentifierBytes);
                    if (!File.Exists(removedMarker.ZoneIdentifierPath) ||
                        !File.ReadAllBytes(removedMarker.ZoneIdentifierPath)
                            .SequenceEqual(removedMarker.ZoneIdentifierBytes))
                    {
                        throw new IOException(
                            $"Blocked-file marker bytes were not restored: {removedMarker.AssemblyPath}");
                    }
                }
                catch (Exception restoreException)
                {
                    restoreErrors.Add(restoreException);
                }
            }

            if (restoreErrors.Count > 0)
            {
                throw new AggregateException(
                    "Assembly unblocking failed and one or more blocked-file markers could not be restored.",
                    new[] { unblockException }.Concat(restoreErrors));
            }

            throw;
        }
    }

    public CoopPreparationResult Apply(CoopPreparationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_pendingPlans.TryGetValue(plan.PlanId, out var pending) ||
            !ReferenceEquals(plan, pending.PublicPlan))
        {
            throw new InvalidOperationException(
                "This compatibility plan is stale or was created by another Bannerlord Coop Manager session. Analyze again.");
        }

        if (!plan.CanApply)
            throw new InvalidOperationException("The compatibility plan has blockers or no file changes.");

        var backupDirectory = Path.Combine(
            plan.ServerRoot,
            "bcs-compatibility-backups",
            plan.PlanId);
        if (Directory.Exists(backupDirectory))
            throw new IOException($"Compatibility backup already exists: {backupDirectory}");

        foreach (var change in pending.Changes)
            VerifyUnchanged(change);

        Directory.CreateDirectory(backupDirectory);
        var applied = new List<PendingChange>();
        var manifestPath = Path.Combine(backupDirectory, "bcs-compatibility-backup.json");
        try
        {
            foreach (var change in pending.Changes)
            {
                var relative = Path.GetRelativePath(plan.ServerRoot, change.TargetPath);
                EnsureSafeRelativePath(relative);
                if (change.OriginalExists)
                {
                    var backupPath = Path.Combine(backupDirectory, "files", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(change.TargetPath, backupPath, overwrite: false);
                }

                ReplaceFileSafely(change.TargetPath, change.ProposedBytes);
                applied.Add(change);
            }

            var manifest = new BackupManifest
            {
                SchemaVersion = 1,
                PlanId = plan.PlanId,
                CreatedUtc = plan.CreatedUtc,
                ServerRoot = plan.ServerRoot,
                RuleId = plan.RuleId,
                ModuleIds = plan.ModuleIds.ToArray(),
                Files = pending.Changes.Select(change => new BackupFileEntry
                {
                    RelativePath = Path.GetRelativePath(plan.ServerRoot, change.TargetPath),
                    OriginalExisted = change.OriginalExists,
                    OriginalSha256 = change.PublicChange.OriginalSha256,
                    AppliedSha256 = change.PublicChange.ProposedSha256
                }).ToList()
            };
            var manifestBytes = Utf8NoBom.GetBytes(
                JsonSerializer.Serialize(manifest, JsonOptions()) + Environment.NewLine);

            UnblockPreparedModuleAssemblies(plan.ServerRoot, ReadEnabledModuleIds(plan.ServerRoot));
            ReplaceFileSafely(manifestPath, manifestBytes);

            _pendingPlans.Remove(plan.PlanId);
            return new CoopPreparationResult(
                plan.PlanId,
                backupDirectory,
                manifestPath,
                plan.ModuleIds);
        }
        catch (Exception applyException)
        {
            var rollbackErrors = RollBackApplied(plan.ServerRoot, backupDirectory, applied).ToList();
            try
            {
                File.Delete(manifestPath);
            }
            catch (Exception cleanupException)
            {
                rollbackErrors.Add(cleanupException);
            }
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Compatibility apply failed and one or more files could not be rolled back. " +
                    $"Recovery material remains at: {backupDirectory}",
                    new[] { applyException }.Concat(rollbackErrors));
            }

            throw;
        }
    }

    public CoopPreparationResult Revert(string manifestPath)
    {
        var canonicalManifest = Path.GetFullPath(manifestPath);
        if (!File.Exists(canonicalManifest))
            throw new FileNotFoundException("Compatibility backup manifest was not found.", canonicalManifest);

        var backupDirectory = Path.GetDirectoryName(canonicalManifest)!;
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
                           File.ReadAllText(canonicalManifest, Utf8NoBom),
                           JsonOptions())
                       ?? throw new InvalidDataException("Compatibility backup manifest is empty.");
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.PlanId))
            throw new InvalidDataException("Unsupported compatibility backup manifest.");

        var serverRoot = Path.GetFullPath(manifest.ServerRoot);
        foreach (var entry in manifest.Files)
        {
            EnsureSafeRelativePath(entry.RelativePath);
            var target = Path.GetFullPath(Path.Combine(serverRoot, entry.RelativePath));
            EnsureWithin(target, serverRoot, "Backup target");
            if (!File.Exists(target) || !HashFile(target).Equals(entry.AppliedSha256, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Cannot revert because the prepared file changed after apply: {target}");
            }
        }

        var appliedBytes = manifest.Files.ToDictionary(
            entry => entry.RelativePath,
            entry => File.ReadAllBytes(
                Path.GetFullPath(Path.Combine(serverRoot, entry.RelativePath))),
            StringComparer.OrdinalIgnoreCase);
        var reverted = new List<BackupFileEntry>();
        try
        {
            foreach (var entry in manifest.Files.AsEnumerable().Reverse())
            {
                var target = Path.GetFullPath(Path.Combine(serverRoot, entry.RelativePath));
                if (entry.OriginalExisted)
                {
                    var backup = Path.Combine(backupDirectory, "files", entry.RelativePath);
                    if (!File.Exists(backup) ||
                        !HashFile(backup).Equals(entry.OriginalSha256, StringComparison.Ordinal))
                    {
                        throw new IOException($"Original backup is missing or damaged: {backup}");
                    }

                    ReplaceFileSafely(target, File.ReadAllBytes(backup));
                }
                else
                {
                    File.Delete(target);
                }

                reverted.Add(entry);
            }
        }
        catch (Exception revertException)
        {
            var recoveryErrors = new List<Exception>();
            foreach (var entry in reverted.AsEnumerable().Reverse())
            {
                try
                {
                    var target = Path.GetFullPath(Path.Combine(serverRoot, entry.RelativePath));
                    ReplaceFileSafely(target, appliedBytes[entry.RelativePath]);
                }
                catch (Exception recoveryException)
                {
                    recoveryErrors.Add(recoveryException);
                }
            }

            if (recoveryErrors.Count > 0)
            {
                throw new AggregateException(
                    "Compatibility revert failed and the prepared state could not be fully restored.",
                    new[] { revertException }.Concat(recoveryErrors));
            }

            throw;
        }

        File.WriteAllText(
            Path.Combine(backupDirectory, "REVERTED.txt"),
            $"Reverted by Bannerlord Coop Manager at {DateTimeOffset.UtcNow:O}{Environment.NewLine}",
            Utf8NoBom);

        return new CoopPreparationResult(
            manifest.PlanId,
            backupDirectory,
            canonicalManifest,
            manifest.ModuleIds);
    }

    public string? FindLatestBackupManifest(string serverRoot)
    {
        var root = Path.Combine(Path.GetFullPath(serverRoot), "bcs-compatibility-backups");
        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateFiles(
                root,
                "bcs-compatibility-backup.json",
                SearchOption.AllDirectories)
            .Where(path => !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "REVERTED.txt")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void BuildRealmOfThronesPlan(
        IReadOnlyDictionary<string, BannerlordModule> byId,
        string modulesRoot,
        ICollection<string> blockers,
        ICollection<string> warnings,
        ICollection<PendingChange> proposed,
        ICollection<string> selectedIds)
    {
        var family = new List<BannerlordModule>();
        foreach (var id in RealmOfThronesModuleIds)
        {
            if (!byId.TryGetValue(id, out var module))
            {
                blockers.Add($"Realm of Thrones package is incomplete; missing server module '{id}'.");
                continue;
            }

            EnsureDirectChild(module.Path, modulesRoot, $"Module '{id}'");
            family.Add(module);
            selectedIds.Add(id);
            if (!module.Version.Equals("v8.1.7", StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(
                    $"Realm of Thrones rule supports v8.1.7, but '{id}' is {module.Version}.");
            }
        }

        if (!byId.TryGetValue("Coop", out var coop) ||
            !IsSupportedReleasedCoopVersion(coop.Version))
        {
            blockers.Add(
                "Realm of Thrones rule requires a verified released Coop build " +
                "(v0.1.1 or v0.1.2).");
        }

        foreach (var framework in new[]
                 {
                     "Bannerlord.Harmony",
                     "Bannerlord.ButterLib",
                     "Bannerlord.UIExtenderEx",
                     "Bannerlord.MBOptionScreen"
                 })
        {
            if (!byId.ContainsKey(framework))
                blockers.Add($"Required server framework module is missing: {framework}.");
            else
                selectedIds.Add(framework);
        }

        var core = family.FirstOrDefault(module =>
            module.Id.Equals("ROT-Core", StringComparison.OrdinalIgnoreCase));
        if (core is null)
            return;

        var coreDll = FindDeclaredDll(core.Path, "ROT.dll");
        if (coreDll is null)
        {
            blockers.Add("ROT-Core does not contain its declared ROT.dll.");
            return;
        }

        var navalDlc = FindAssembly(byId.Values, modulesRoot, "NavalDLC.dll");
        if (navalDlc is null)
        {
            blockers.Add(
                "ROT.dll directly references NavalDLC.dll from the commercial War Sails expansion, " +
                "but that licensed runtime is not installed in the client or dedicated-server module set. " +
                "Bannerlord Coop Manager will not synthesize or bypass paid DLC code.");
        }

        if (blockers.Count > 0)
            return;

        foreach (var module in family)
        {
            AddManifestTransformation(
                module,
                addCoopOrdering: true,
                enableDllNames: module.Id.Equals("ROT-Core", StringComparison.OrdinalIgnoreCase)
                    ? ["ROT.dll"]
                    : [],
                proposed);
        }

        AddClientDllProjection(core, proposed);
        warnings.Add(
            "RoT naval/War Sails behaviors are not proven compatible with the current Coop protocol. " +
            "Do not use an existing campaign until isolated join, save, reconnect, and battle tests pass.");
        warnings.Add(
            "RoT adds custom campaign behaviors and Harmony patches. Preparing files only reaches the " +
            "server-load milestone; it is not proof that every campaign mutation is synchronized.");
    }

    internal static void BuildEurope1700Plan(
        BannerlordModule module,
        IReadOnlyDictionary<string, BannerlordModule> byId,
        string modulesRoot,
        ICollection<string> blockers,
        ICollection<string> warnings,
        ICollection<PendingChange> proposed,
        ICollection<string> selectedIds,
        IReadOnlyCollection<string>? selectedDllNames = null,
        IReadOnlyCollection<string>? disabledDllNames = null)
    {
        EnsureDirectChild(module.Path, modulesRoot, "Empires of Europe 1700 module");
        selectedIds.Add(module.Id);
        var manifest = LoadManifest(Path.Combine(module.Path, "SubModule.xml"));
        var dllSelection = ValidateEurope1700DllSelection(
            manifest,
            selectedDllNames,
            disabledDllNames);
        var selectedDlls = dllSelection.Enabled;
        var disabledDlls = dllSelection.Disabled;
        var serverDlls = SelectEurope1700ServerDlls(manifest)
            .Where(dllName => selectedDlls.Contains(
                dllName,
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var serverDisabledDlls = disabledDlls
            .Concat(selectedDlls.Where(dllName => Europe1700ClientOnlyDlls.Contains(
                dllName,
                StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!module.Version.Equals("v1.4.7.1", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(
                $"Empires of Europe 1700 rule supports v1.4.7.1, but the installed module is {module.Version}.");
        }
        if (!byId.TryGetValue("Coop", out var coop) ||
            !IsSupportedReleasedCoopVersion(coop.Version))
        {
            blockers.Add(
                "Empires of Europe 1700 rule requires a verified released Coop build " +
                "(v0.1.1 or v0.1.2).");
        }

        var clientBin = Path.Combine(module.Path, "bin", "Win64_Shipping_Client");
        foreach (var expectedAssembly in selectedDlls)
        {
            var path = Path.Combine(clientBin, expectedAssembly);
            if (!File.Exists(path))
            {
                blockers.Add($"Empires of Europe 1700 assembly is missing: {expectedAssembly}.");
                continue;
            }
        }

        var clansEnabled = serverDlls.Contains(
            "ClansResourceAdder.dll",
            StringComparer.OrdinalIgnoreCase);
        var clansResourceConfig = Path.Combine(clientBin, "conf_clans_resource_adder.xml");
        byte[]? clansResourceConfigBytes = null;
        if (clansEnabled && !File.Exists(clansResourceConfig))
        {
            blockers.Add(
                "Empires of Europe 1700 ClansResourceAdder configuration is missing: " +
                clansResourceConfig);
        }

        else if (clansEnabled)
            clansResourceConfigBytes = File.ReadAllBytes(clansResourceConfig);

        var mainMapScene = Path.Combine(
            module.Path,
            "SceneObj",
            "Main_map",
            "scene.xscene");
        if (!IsRegularUnlinkedFileWithin(mainMapScene, module.Path))
        {
            blockers.Add(
                "Empires of Europe 1700 Main_map scene is missing, linked, or outside its module: " +
                mainMapScene);
        }

        var engineRoot = Directory.GetParent(modulesRoot)?.FullName;
        if (engineRoot is null)
        {
            blockers.Add("Dedicated-server engine root could not be resolved for EOE terrain compatibility.");
        }
        else
        {
            ValidateRuntimeAssembly(
                Path.Combine(
                    engineRoot,
                    "bin",
                    "Win64_Shipping_Server",
                    "DedicatedServer.Core.dll"),
                engineRoot,
                "DedicatedServer.Core",
                "EOE dedicated-server map loader",
                blockers);
        }

        if (!byId.TryGetValue("Sandbox", out var sandboxModule))
        {
            blockers.Add("Sandbox module is missing for EOE terrain compatibility.");
        }
        else
        {
            ValidateRuntimeAssembly(
                Path.Combine(
                    sandboxModule.Path,
                    "bin",
                    "Win64_Shipping_Server",
                    "SandBox.dll"),
                sandboxModule.Path,
                "SandBox",
                "EOE server terrain patch target",
                blockers);
        }

        string? storyModeBin = null;
        string? storyModeHash = null;
        var artilleryEnabled = serverDlls.Contains(
            "BattleArtilleryReworked.dll",
            StringComparer.OrdinalIgnoreCase);
        var bannerlordRoot = artilleryEnabled
            ? ServerExecutableLocator.FindBannerlordInstallRoot()
            : null;
        if (artilleryEnabled && bannerlordRoot is null)
        {
            blockers.Add(
                "Empires of Europe 1700 artillery requires the installed Bannerlord StoryMode runtime, " +
                "but the Bannerlord client installation could not be located.");
        }
        else if (artilleryEnabled)
        {
            storyModeBin = Path.Combine(
                bannerlordRoot!,
                "Modules",
                "StoryMode",
                "bin",
                "Win64_Shipping_Client");
            var storyModeDll = Path.Combine(storyModeBin, "StoryMode.dll");
            if (!File.Exists(storyModeDll))
            {
                blockers.Add(
                    $"Empires of Europe 1700 artillery requires StoryMode.dll, which is missing: {storyModeDll}");
            }
            else
            {
                try
                {
                    var identity = AssemblyName.GetAssemblyName(storyModeDll).Name;
                    if (!string.Equals(identity, "StoryMode", StringComparison.Ordinal))
                        blockers.Add($"EOE StoryMode dependency has unexpected assembly identity: {identity}.");
                    storyModeHash = string.Empty;
                }
                catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
                {
                    blockers.Add($"EOE StoryMode dependency is not a readable managed assembly: {storyModeDll}");
                }
            }
        }

        if (blockers.Count > 0)
            return;

        AddEurope1700ItemsSchemaTransformation(module, engineRoot!, proposed);
        AddManifestTransformation(
            module,
            addCoopOrdering: false,
            enableDllNames: serverDlls,
            proposed,
            disableDllNames: serverDisabledDlls,
            sourceManifest: manifest);
        AddClientDllProjection(module, proposed, serverDlls);
        AddEurope1700HeadlessCollisionTransformation(module, proposed);
        AddEurope1700HeadlessActionSetTransformation(module, proposed);
        AddEurope1700HeadlessActionTypesTransformation(module, proposed);
        AddEurope1700TrebuchetPrefabTransformation(module, proposed);
        AddEurope1700ManagedDependencyProfile(
            modulesRoot,
            storyModeBin,
            storyModeHash,
            proposed);
        if (clansEnabled)
        {
            AddPendingChange(
                proposed,
                Path.Combine(
                    module.Path,
                    "bin",
                    "Win64_Shipping_Server",
                    "conf_clans_resource_adder.xml"),
                clansResourceConfigBytes!,
                "Project ClansResourceAdder configuration into the server bin");
        }
        warnings.Add(
            "EOE bridge DLL selection: " +
            (selectedDlls.Count == 0 ? "none included" : "included " + string.Join(", ", selectedDlls)) + ". " +
            (disabledDlls.Count == 0 ? "No declared DLLs excluded." :
                "Excluded " + string.Join(", ", disabledDlls) + ". ") +
            "Dedicated-server active DLLs: " +
            (serverDlls.Length == 0 ? "none enabled" : "enabled " + string.Join(", ", serverDlls)) + ". " +
            "Selected client-only DLLs remain server-suppressed: " +
            (selectedDlls.Where(dllName => Europe1700ClientOnlyDlls.Contains(
                    dllName,
                    StringComparer.OrdinalIgnoreCase)).Any()
                ? string.Join(", ", selectedDlls.Where(dllName => Europe1700ClientOnlyDlls.Contains(
                    dllName,
                    StringComparer.OrdinalIgnoreCase)))
                : "none") + ".");
        if (clansEnabled || serverDlls.Contains(
                "Bannerlord.EOEPatches.dll",
                StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add(
                "Selected EOE authority adapters remain role-scoped: ClansResourceAdder mutations are server-only " +
                "and EOEPatches' music/UI startup hook is client-only. Real join, save, reconnect, campaign tick, " +
                "and field/siege battle tests remain required.");
        }
        warnings.Add(
            "EOE client animation TPACs crash Bannerlord's no-render asset loader. The server projection keeps " +
            "all 24 EOE action IDs, maps their visual animations to Native headless equivalents, and " +
            "repairs the package's mismatched bomb-reload action ID and four malformed trebuchet XML tags.");
        warnings.Add(
            "The generated bridge redirects EOE XML reads to bridge-owned casing, terminator, workshop-output, " +
            "legacy NPC equipment-type, and invalid equipment-slot overlays. EOE files remain unchanged. " +
            "Firearm alternate melee modes are retained through a reversible server Items.xsd correction, " +
            "which must validate the complete EOE items_guns.xml before installation.");
        if (artilleryEnabled)
        {
            warnings.Add(
                "BattleArtilleryReworked references StoryMode.CampaignStoryMode while EOE declares no StoryMode " +
                "dependency and the dedicated-server package omits StoryMode.dll. BCS locates the installed official " +
                "StoryMode binary for both role-specific resolvers; no official game files are copied or redistributed.");
        }
        if (clansEnabled)
        {
            warnings.Add(
                "ClansResourceAdder resolves conf_clans_resource_adder.xml beside its loaded assembly. BCS validates and " +
                "projects the EOE-supplied configuration into the server bin so campaign initialization does not fail.");
        }
        warnings.Add(
            "The released dedicated server selects Sandbox's settlement distance cache even while EOE's Main_map " +
            "is active. The generated bridge redirects only that server cache read to EOE's cache; " +
            "official Coop, Sandbox, and client files remain unchanged.");
        warnings.Add(
            "The released dedicated-server map loader reports a fixed 848x848 terrain while EOE's Main_map " +
            "is 1696x1696. The generated bridge corrects SandBox.MapScene.GetTerrainSize only on the server, " +
            "preventing valid EOE positions from indexing outside Bannerlord's weather grid.");
    }

    internal static IReadOnlyList<BridgeClientAssemblyResolve>
        CreateEurope1700ClientAssemblyResolves()
    {
        var bannerlordRoot = ServerExecutableLocator.FindBannerlordInstallRoot()
                             ?? throw new InvalidDataException(
                                 "Bannerlord client installation could not be located for the EOE StoryMode resolver.");
        const string relativePath = "bin/Win64_Shipping_Client/StoryMode.dll";
        var sourcePath = Path.Combine(
            bannerlordRoot,
            "Modules",
            "StoryMode",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("EOE StoryMode client dependency is missing.", sourcePath);
        var assemblyName = AssemblyName.GetAssemblyName(sourcePath).Name;
        if (!string.Equals(assemblyName, "StoryMode", StringComparison.Ordinal))
            throw new InvalidDataException(
                "EOE StoryMode client dependency has an unexpected assembly identity: " + assemblyName + ".");
        return
        [
            new BridgeClientAssemblyResolve(
                "StoryMode",
                relativePath,
                string.Empty,
                sourcePath)
        ];
    }

    internal static IReadOnlyList<BridgeServerXmlOverlay> CreateEurope1700SchemaOverlays(
        BannerlordModule module)
    {
        var overlays = new List<BridgeServerXmlOverlay>();
        foreach (var repair in Europe1700SchemaRepairs)
        {
            var path = Path.Combine(
                module.Path,
                repair.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException("Required EOE schema-repair file is missing.", path);

            var transformed = TransformEurope1700SchemaRepairForHeadless(
                repair.RelativePath,
                File.ReadAllBytes(path));
            overlays.Add(new BridgeServerXmlOverlay(
                module.Id,
                repair.RelativePath,
                string.Empty,
                "bcs-server-overlays/" + module.Id + "/" + repair.RelativePath,
                string.Empty,
                transformed));
        }
        return overlays;
    }

    private static void AddEurope1700ItemsSchemaTransformation(
        BannerlordModule module,
        string engineRoot,
        ICollection<PendingChange> proposed)
    {
        var schemaPath = Path.Combine(engineRoot, "XmlSchemas", "Items.xsd");
        if (!IsRegularUnlinkedFileWithin(schemaPath, engineRoot))
        {
            throw new InvalidDataException(
                "Dedicated-server Items.xsd is missing, linked, or outside the engine root: " + schemaPath);
        }

        var itemsPath = Path.Combine(module.Path, "ModuleData", "items", "items_guns.xml");
        if (!IsRegularUnlinkedFileWithin(itemsPath, module.Path))
        {
            throw new InvalidDataException(
                "EOE items_guns.xml is missing, linked, or outside the module root: " + itemsPath);
        }

        AddPendingChange(
            proposed,
            schemaPath,
            TransformEurope1700ItemsSchema(
                File.ReadAllBytes(schemaPath),
                File.ReadAllBytes(itemsPath)),
            "Allow runtime-supported repeated EOE Weapon modes in the server Items.xsd");
    }

    internal static byte[] TransformEurope1700ItemsSchema(
        byte[] schemaBytes,
        byte[] itemsBytes)
    {
        ArgumentNullException.ThrowIfNull(schemaBytes);
        ArgumentNullException.ThrowIfNull(itemsBytes);

        var schemaDocument = LoadSecureXmlDocument(schemaBytes, "Dedicated-server Items.xsd");
        var schemaNamespaces = new XmlNamespaceManager(schemaDocument.NameTable);
        schemaNamespaces.AddNamespace("xs", XmlSchema.Namespace);
        const string weaponDeclarationPath =
            "/xs:schema/xs:element[@name='Items']/xs:complexType/xs:choice/" +
            "xs:element[@name='Item']/xs:complexType/xs:sequence/" +
            "xs:element[@name='ItemComponent']/xs:complexType/xs:choice/" +
            "xs:element[@name='Weapon']";
        var weaponDeclarations = schemaDocument.SelectNodes(
            weaponDeclarationPath,
            schemaNamespaces);
        if (weaponDeclarations?.Count != 1 || weaponDeclarations[0] is not XmlElement weaponDeclaration)
        {
            throw new InvalidDataException(
                "Dedicated-server Items.xsd must contain exactly one Weapon declaration under ItemComponent.");
        }

        var itemsDocument = LoadSecureXmlDocument(itemsBytes, "EOE items_guns.xml");
        var repeatedWeaponItems = itemsDocument.SelectNodes(
            "/Items/Item/ItemComponent[count(Weapon) > 1]");
        if (repeatedWeaponItems is null || repeatedWeaponItems.Count == 0)
        {
            throw new InvalidDataException(
                "EOE items_guns.xml does not contain an item with repeated Weapon modes.");
        }

        var maximum = weaponDeclaration.GetAttribute("maxOccurs");
        if (maximum.Equals("unbounded", StringComparison.Ordinal))
        {
            ValidateItemsAgainstSchema(schemaBytes, itemsBytes);
            return schemaBytes.ToArray();
        }
        if (!maximum.Equals("1", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The EOE ItemComponent Weapon declaration must have maxOccurs=\"1\" or \"unbounded\", " +
                $"but found \"{maximum}\".");
        }

        var schemaText = Utf8NoBom.GetString(schemaBytes);
        const string weaponOpeningPattern =
            @"<(?:(?:[A-Za-z_][\w.-]*):)?element\b" +
            @"(?=[^>]*\bname\s*=\s*(?<nameQuote>[""'])Weapon\k<nameQuote>)[^>]*>";
        var weaponOpenings = Regex.Matches(
            schemaText,
            weaponOpeningPattern,
            RegexOptions.CultureInvariant);
        if (weaponOpenings.Count != 1)
        {
            throw new InvalidDataException(
                "Dedicated-server Items.xsd must contain exactly one textual Weapon declaration.");
        }

        var opening = weaponOpenings[0];
        var maximumAttributes = Regex.Matches(
            opening.Value,
            @"\bmaxOccurs\s*=\s*(?<quote>[""'])(?<value>[^""']*)\k<quote>",
            RegexOptions.CultureInvariant);
        if (maximumAttributes.Count != 1 ||
            !maximumAttributes[0].Groups["value"].Value.Equals("1", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The EOE ItemComponent Weapon declaration must contain exactly one maxOccurs=\"1\" attribute.");
        }

        var value = maximumAttributes[0].Groups["value"];
        var valueOffset = opening.Index + value.Index;
        var transformedText = schemaText.Remove(valueOffset, value.Length)
            .Insert(valueOffset, "unbounded");
        var transformedBytes = Utf8NoBom.GetBytes(transformedText);

        var transformedDocument = LoadSecureXmlDocument(
            transformedBytes,
            "Transformed dedicated-server Items.xsd");
        var transformedNamespaces = new XmlNamespaceManager(transformedDocument.NameTable);
        transformedNamespaces.AddNamespace("xs", XmlSchema.Namespace);
        var transformedDeclarations = transformedDocument.SelectNodes(
            weaponDeclarationPath,
            transformedNamespaces);
        if (transformedDeclarations?.Count != 1 ||
            transformedDeclarations[0] is not XmlElement transformedDeclaration ||
            !transformedDeclaration.GetAttribute("maxOccurs").Equals(
                "unbounded",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The transformed Items.xsd did not retain one unbounded ItemComponent Weapon declaration.");
        }

        ValidateItemsAgainstSchema(transformedBytes, itemsBytes);
        return transformedBytes;
    }

    private static XmlDocument LoadSecureXmlDocument(byte[] bytes, string description)
    {
        if (bytes.Length == 0)
            throw new InvalidDataException(description + " is empty.");

        try
        {
            var document = new XmlDocument { XmlResolver = null };
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, SecureXmlReaderSettings());
            document.Load(reader);
            return document;
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(description + " is not secure, well-formed XML.", exception);
        }
    }

    private static void ValidateItemsAgainstSchema(byte[] schemaBytes, byte[] itemsBytes)
    {
        var validationMessages = new List<string>();
        void RecordValidation(object? _, ValidationEventArgs eventArgs) =>
            validationMessages.Add(eventArgs.Message);

        try
        {
            var schemas = new XmlSchemaSet { XmlResolver = null };
            schemas.ValidationEventHandler += RecordValidation;
            using (var schemaStream = new MemoryStream(schemaBytes, writable: false))
            using (var schemaReader = XmlReader.Create(schemaStream, SecureXmlReaderSettings()))
            {
                schemas.Add(null, schemaReader);
            }
            schemas.Compile();
            if (validationMessages.Count > 0)
            {
                throw new InvalidDataException(
                    "Transformed dedicated-server Items.xsd did not compile cleanly: " +
                    validationMessages[0]);
            }

            var settings = SecureXmlReaderSettings();
            settings.ValidationType = ValidationType.Schema;
            settings.Schemas = schemas;
            settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings;
            settings.ValidationEventHandler += RecordValidation;
            using var itemsStream = new MemoryStream(itemsBytes, writable: false);
            using var itemsReader = XmlReader.Create(itemsStream, settings);
            while (itemsReader.Read())
            {
            }
        }
        catch (Exception exception) when (exception is XmlException or XmlSchemaException)
        {
            throw new InvalidDataException(
                "The transformed Items.xsd or EOE items_guns.xml could not be validated.",
                exception);
        }

        if (validationMessages.Count > 0)
        {
            throw new InvalidDataException(
                "EOE items_guns.xml did not validate cleanly against the transformed Items.xsd: " +
                validationMessages[0]);
        }
    }

    private static XmlReaderSettings SecureXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumManifestCharacters
    };

    internal static byte[] TransformEurope1700SchemaRepairForHeadless(
        string relativePath,
        byte[] sourceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(sourceBytes);
        var repair = Europe1700SchemaRepairs.SingleOrDefault(candidate =>
            candidate.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        if (repair is null)
            throw new InvalidDataException($"Unknown EOE schema repair: {relativePath}.");

        var source = Utf8NoBom.GetString(sourceBytes);
        string transformed;
        switch (repair.Kind)
        {
            case Europe1700SchemaRepairKind.EquipmentElementCase:
            {
                var count = CountElementOpenings(source, "equipment");
                if (count != repair.ExpectedOccurrences)
                {
                    throw new InvalidDataException(
                        $"EOE file {relativePath} contains {count} lowercase equipment elements; " +
                        $"expected {repair.ExpectedOccurrences}.");
                }
                transformed = source.Replace("<equipment", "<Equipment", StringComparison.Ordinal);
                break;
            }
            case Europe1700SchemaRepairKind.TraitsElementCase:
            {
                var openings = CountElementOpenings(source, "traits");
                var closings = CountOccurrences(source, "</traits>");
                if (openings != repair.ExpectedOccurrences || closings != repair.ExpectedOccurrences)
                {
                    throw new InvalidDataException(
                        $"EOE file {relativePath} contains {openings} lowercase trait openings and " +
                        $"{closings} closings; expected {repair.ExpectedOccurrences} of each.");
                }
                transformed = source
                    .Replace("<traits", "<Traits", StringComparison.Ordinal)
                    .Replace("</traits>", "</Traits>", StringComparison.Ordinal);
                break;
            }
            case Europe1700SchemaRepairKind.StrayElementTerminator:
            {
                const string malformed = "equipmentType=\"Civilian\" />/>";
                var count = CountOccurrences(source, malformed);
                if (count != repair.ExpectedOccurrences)
                {
                    throw new InvalidDataException(
                        $"EOE file {relativePath} contains {count} stray element terminators; " +
                        $"expected {repair.ExpectedOccurrences}.");
                }
                transformed = source.Replace(
                    malformed,
                    "equipmentType=\"Civilian\" />",
                    StringComparison.Ordinal);
                break;
            }
            case Europe1700SchemaRepairKind.MergedBearskinEquipmentXslt:
            {
                transformed = AppendEurope1700CompatibilityXslt(
                    source,
                    includeCivilianEquipmentTypeRepair: false,
                    repair.ExpectedOccurrences,
                    relativePath);
                break;
            }
            case Europe1700SchemaRepairKind.MergedNpcCompatibilityXslt:
            {
                transformed = AppendEurope1700CompatibilityXslt(
                    source,
                    includeCivilianEquipmentTypeRepair: true,
                    repair.ExpectedOccurrences,
                    relativePath);
                break;
            }
            case Europe1700SchemaRepairKind.InvalidBearskinCapeEquipment:
            {
                const string invalidBearskinCape =
                    "<Equipment\\s+slot=\"Cape\"\\s+id=\"Item\\.bearskin\"\\s*/>";
                var count = Regex.Matches(
                    source,
                    invalidBearskinCape,
                    RegexOptions.CultureInvariant).Count;
                if (count != repair.ExpectedOccurrences)
                {
                    throw new InvalidDataException(
                        $"EOE file {relativePath} contains {count} invalid Bearskin Cape equipment entries; " +
                        $"expected {repair.ExpectedOccurrences}.");
                }
                transformed = Regex.Replace(
                    source,
                    invalidBearskinCape,
                    string.Empty,
                    RegexOptions.CultureInvariant);
                break;
            }
            case Europe1700SchemaRepairKind.WorkshopOutputCategories:
            {
                const string rangedTierOne = "output=\"ItemCategory.ranged_weapons\"";
                const string rangedTierTwo = "output=\"ItemCategory.ranged_weapons_2\"";
                const string rangedTierThree = "output=\"ItemCategory.ranged_weapons_3\"";
                const string rangedTierFour = "output=\"ItemCategory.ranged_weapons_4\"";
                const string rangedTierFive = "output=\"ItemCategory.ranged_weapons_5\"";
                var tierOneCount = CountOccurrences(source, rangedTierOne);
                var tierTwoCount = CountOccurrences(source, rangedTierTwo);
                var tierThreeCount = CountOccurrences(source, rangedTierThree);
                var tierFourCount = CountOccurrences(source, rangedTierFour);
                var totalCount = tierOneCount + tierTwoCount + tierThreeCount + tierFourCount;
                if (tierOneCount != 1 ||
                    tierTwoCount != 1 ||
                    tierThreeCount != 2 ||
                    tierFourCount != 1 ||
                    totalCount != repair.ExpectedOccurrences)
                {
                    throw new InvalidDataException(
                        $"EOE file {relativePath} contains ranged workshop outputs " +
                        $"{tierOneCount}/{tierTwoCount}/{tierThreeCount}/{tierFourCount}; " +
                        "expected 1/1/2/1.");
                }

                transformed = source
                    .Replace(rangedTierOne, rangedTierFive, StringComparison.Ordinal)
                    .Replace(rangedTierTwo, rangedTierFive, StringComparison.Ordinal)
                    .Replace(rangedTierThree, rangedTierFive, StringComparison.Ordinal)
                    .Replace(rangedTierFour, rangedTierFive, StringComparison.Ordinal);
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported EOE schema repair: {repair.Kind}.");
        }

        var transformedBytes = Utf8NoBom.GetBytes(transformed);
        var document = new XmlDocument { XmlResolver = null };
        using var stream = new MemoryStream(transformedBytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestCharacters
        });
        document.Load(reader);
        return transformedBytes;
    }

    private static string AppendEurope1700CompatibilityXslt(
        string source,
        bool includeCivilianEquipmentTypeRepair,
        int expectedOccurrences,
        string relativePath)
    {
        const string closing = "</xsl:stylesheet>";
        const string bearskinTemplate =
            "<xsl:template match=\"equipment[@slot='Cape' and @id='Item.bearskin'] | " +
            "Equipment[@slot='Cape' and @id='Item.bearskin']\"/>";
        const string civilianTemplate =
            "<xsl:template match=\"EquipmentSet[not(@equipmentType)]/@civilian[.='true']\">";
        var closingCount = CountOccurrences(source, closing);
        if (closingCount != expectedOccurrences ||
            source.Contains(bearskinTemplate, StringComparison.Ordinal) ||
            source.Contains(civilianTemplate, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"EOE XSLT {relativePath} has {closingCount} stylesheet terminators or already contains " +
                $"a BCS compatibility template; expected {expectedOccurrences} unmodified terminator.");
        }

        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var addition = "\t" + bearskinTemplate + newline;
        if (includeCivilianEquipmentTypeRepair)
        {
            addition +=
                "\t" + civilianTemplate + newline +
                "\t\t<xsl:attribute name=\"equipmentType\">Civilian</xsl:attribute>" + newline +
                "\t</xsl:template>" + newline;
        }
        addition += newline + closing;
        return source.Replace(closing, addition, StringComparison.Ordinal);
    }

    private static int CountElementOpenings(string text, string elementName)
    {
        var prefix = "<" + elementName;
        var count = 0;
        for (var index = 0;;)
        {
            index = text.IndexOf(prefix, index, StringComparison.Ordinal);
            if (index < 0)
                return count;
            var followingIndex = index + prefix.Length;
            if (followingIndex < text.Length &&
                (char.IsWhiteSpace(text[followingIndex]) ||
                 text[followingIndex] == '/' ||
                 text[followingIndex] == '>'))
            {
                count++;
            }
            index = followingIndex;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0;;)
        {
            index = text.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            index += value.Length;
        }
    }

    private static void AddEurope1700ManagedDependencyProfile(
        string modulesRoot,
        string? storyModeBin,
        string? storyModeHash,
        ICollection<PendingChange> proposed)
    {
        if ((storyModeBin is null) != (storyModeHash is null))
            throw new InvalidDataException("EOE StoryMode dependency profile input is incomplete.");

        var serverRoot = Directory.GetParent(Directory.GetParent(modulesRoot)!.FullName)!.FullName;
        var profilePath = Path.Combine(
            serverRoot,
            DedicatedServerLaunchBuilder.ManagedDependencyProfileFileName);
        var profileExists = File.Exists(profilePath);
        if (!profileExists && storyModeBin is null)
            return;

        var profile = profileExists
            ? JsonSerializer.Deserialize<GeneratedManagedDependencyProfile>(
                  File.ReadAllText(profilePath, Utf8NoBom),
                  JsonOptions())
              ?? throw new InvalidDataException(
                  $"Managed dependency profile is empty: {profilePath}")
            : new GeneratedManagedDependencyProfile();
        if (profile.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Managed dependency profile has an unsupported schema: {profilePath}");
        }

        profile.Directories.RemoveAll(entry => entry.RequiredFiles.Any(file =>
            file.Name.Equals("StoryMode.dll", StringComparison.OrdinalIgnoreCase)));
        if (storyModeBin is not null)
        {
            profile.Directories.Add(new GeneratedManagedDependencyDirectory
            {
                Path = Path.GetFullPath(storyModeBin),
                RequiredFiles =
                [
                    new GeneratedManagedDependencyFile
                    {
                        Name = "StoryMode.dll",
                        Sha256 = storyModeHash!
                    }
                ]
            });
        }

        var bytes = Utf8NoBom.GetBytes(
            JsonSerializer.Serialize(profile, JsonOptions()) + Environment.NewLine);
        AddPendingChange(
            proposed,
            profilePath,
            bytes,
            storyModeBin is null
                ? "Remove the EOE artillery StoryMode runtime registration"
                : "Register Bannerlord StoryMode runtime for EOE artillery on the dedicated server");
    }

    private static void AddEurope1700TrebuchetPrefabTransformation(
        BannerlordModule module,
        ICollection<PendingChange> proposed)
    {
        var path = Path.Combine(module.Path, "Prefabs", "props_siege_trebuchets.xml");
        if (!File.Exists(path))
            throw new FileNotFoundException("Empires of Europe 1700 trebuchet prefab is missing.", path);

        byte[] transformed;
        try
        {
            transformed = TransformEurope1700TrebuchetPrefabForHeadless(File.ReadAllBytes(path));
        }
        catch (InvalidDataException) when (IsValidEurope1700TrebuchetPrefab(path))
        {
            return;
        }
        AddPendingChange(
            proposed,
            path,
            transformed,
            "Close four malformed EOE trebuchet ProjectileSpeed XML elements");
    }

    internal static byte[] TransformEurope1700TrebuchetPrefabForHeadless(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        const string malformed = "<variable name=\"ProjectileSpeed\" value=\"53.500\"";
        const string repaired = "<variable name=\"ProjectileSpeed\" value=\"53.500\"/>";
        var source = Utf8NoBom.GetString(sourceBytes);
        var repairedOccurrences = CountOccurrences(source, repaired);
        var malformedOccurrences = CountOccurrences(source, malformed) - repairedOccurrences;
        if (malformedOccurrences == 0 && repairedOccurrences == 4)
        {
            ValidateEurope1700TrebuchetPrefab(sourceBytes);
            return sourceBytes;
        }
        if (malformedOccurrences != 4 || repairedOccurrences != 0)
        {
            throw new InvalidDataException(
                "EOE trebuchet prefab must contain either four malformed or four repaired " +
                $"ProjectileSpeed elements; found {malformedOccurrences} malformed and " +
                $"{repairedOccurrences} repaired.");
        }

        var transformed = Utf8NoBom.GetBytes(source.Replace(
            malformed,
            repaired,
            StringComparison.Ordinal));
        ValidateEurope1700TrebuchetPrefab(transformed);
        return transformed;
    }

    private static void ValidateEurope1700TrebuchetPrefab(byte[] bytes)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestCharacters
        });
        document.Load(reader);
        if (document.DocumentElement?.LocalName != "prefabs")
            throw new InvalidDataException("Unexpected EOE trebuchet-prefab root.");
    }

    private static bool IsValidEurope1700TrebuchetPrefab(string path)
    {
        try
        {
            var document = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestCharacters
            });
            document.Load(reader);
            return document.DocumentElement?.LocalName == "prefabs";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static void AddEurope1700HeadlessActionTypesTransformation(
        BannerlordModule module,
        ICollection<PendingChange> proposed)
    {
        var path = Path.Combine(module.Path, "ModuleData", "action_types.xml");
        if (!File.Exists(path))
            throw new FileNotFoundException("Empires of Europe 1700 action types are missing.", path);

        var transformed = TransformEurope1700ActionTypesForHeadless(File.ReadAllBytes(path));
        AddPendingChange(
            proposed,
            path,
            transformed,
            "Repair EOE bomb-reload action ID for dedicated-server action registration");
    }

    internal static byte[] TransformEurope1700ActionTypesForHeadless(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        var document = new XmlDocument { XmlResolver = null };
        using (var stream = new MemoryStream(sourceBytes, writable: false))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Prohibit,
                   XmlResolver = null,
                   MaxCharactersInDocument = MaximumManifestCharacters
               }))
        {
            document.Load(reader);
        }
        if (document.DocumentElement?.LocalName != "action_types")
            throw new InvalidDataException("Unexpected EOE action-types root.");

        var mismatched = document.SelectNodes(
                "/action_types/action[@name='cla_reload_bomb']")!
            .OfType<XmlElement>()
            .ToArray();
        var repaired = document.SelectNodes(
                "/action_types/action[@name='cla_act_reload_bomb']")!
            .OfType<XmlElement>()
            .ToArray();
        if (mismatched.Length == 0 && repaired.Length == 1)
            return sourceBytes;
        if (mismatched.Length != 1 || repaired.Length != 0)
        {
            throw new InvalidDataException(
                "EOE bomb-reload action types do not match the required repair pattern.");
        }
        mismatched[0].SetAttribute("name", "cla_act_reload_bomb");

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = document.FirstChild is not XmlDeclaration
        };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, settings))
            document.Save(writer);
        return output.ToArray();
    }

    internal static bool IsSupportedReleasedCoopVersion(string? version) =>
        version is not null && SupportedReleasedCoopVersions.Contains(version);

    private static void AddEurope1700HeadlessActionSetTransformation(
        BannerlordModule module,
        ICollection<PendingChange> proposed)
    {
        var path = Path.Combine(module.Path, "ModuleData", "action_sets.xml");
        if (!File.Exists(path))
            throw new FileNotFoundException("Empires of Europe 1700 action set is missing.", path);

        var transformed = TransformEurope1700ActionSetForHeadless(File.ReadAllBytes(path));
        AddPendingChange(
            proposed,
            path,
            transformed,
            "Map EOE visual actions to Native animations available in headless asset packages");
    }

    internal static byte[] TransformEurope1700ActionSetForHeadless(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        var document = new XmlDocument { XmlResolver = null };
        using (var stream = new MemoryStream(sourceBytes, writable: false))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Prohibit,
                   XmlResolver = null,
                   MaxCharactersInDocument = MaximumManifestCharacters
               }))
        {
            document.Load(reader);
        }
        if (document.DocumentElement?.LocalName != "action_sets")
            throw new InvalidDataException("Unexpected EOE action-set root.");

        var actions = document.SelectNodes("/action_sets/action_set/action")!
            .OfType<XmlElement>()
            .ToArray();
        if (actions.Length != Europe1700AnimationProjections.Count)
        {
            throw new InvalidDataException(
                $"EOE action set contains {actions.Length} actions; expected {Europe1700AnimationProjections.Count}.");
        }

        var actionsByType = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var type = action.GetAttribute("type");
            if (!Europe1700AnimationProjections.ContainsKey(type) ||
                !actionsByType.TryAdd(type, action))
            {
                throw new InvalidDataException($"Unexpected or duplicate EOE action type: {type}");
            }
        }

        foreach (var projection in Europe1700AnimationProjections)
        {
            var action = actionsByType[projection.Key];
            var currentAnimation = action.GetAttribute("animation");
            if (currentAnimation.Equals(projection.Value.HeadlessAnimation, StringComparison.Ordinal))
                continue;
            if (!currentAnimation.Equals(projection.Value.ClientAnimation, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"EOE action '{projection.Key}' expected animation " +
                    $"'{projection.Value.ClientAnimation}', found '{currentAnimation}'.");
            }
            action.SetAttribute("animation", projection.Value.HeadlessAnimation);
        }

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = document.FirstChild is not XmlDeclaration
        };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, settings))
            document.Save(writer);
        return output.ToArray();
    }

    private static void AddEurope1700HeadlessCollisionTransformation(
        BannerlordModule module,
        ICollection<PendingChange> proposed)
    {
        var path = Path.Combine(module.Path, "ModuleData", "collision_infos.xml");
        if (!File.Exists(path))
            throw new FileNotFoundException("Empires of Europe 1700 collision info is missing.", path);

        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Prohibit,
                   XmlResolver = null,
                   MaxCharactersInDocument = MaximumManifestCharacters
               }))
        {
            document.Load(reader);
        }
        if (document.DocumentElement?.LocalName != "base")
            throw new InvalidDataException($"Unexpected EOE collision-info root: {path}");

        var particleAttributes = document
            .SelectNodes("/base/collision_infos/material[@id='cla_explosion' or @id='cla_explosion_small']/collision_info/collision_effect/@particle")!
            .OfType<XmlAttribute>()
            .ToArray();
        if (particleAttributes.Length == 0)
            return;
        if (particleAttributes.Length != 76 ||
            particleAttributes.Any(attribute =>
                !attribute.Value.Equals("cla_explosion", StringComparison.Ordinal) &&
                !attribute.Value.Equals("cla_explosion_small", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "EOE collision particles do not match the expected headless transformation.");
        }

        foreach (var attribute in particleAttributes)
            attribute.OwnerElement!.RemoveAttributeNode(attribute);
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = document.FirstChild is not XmlDeclaration
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        AddPendingChange(
            proposed,
            path,
            stream.ToArray(),
            "Remove EOE collision particles unavailable in headless dedicated-server asset packages");
    }

    private sealed record Europe1700AnimationProjection(
        string ClientAnimation,
        string HeadlessAnimation);

    private sealed record Europe1700SchemaRepair(
        string RelativePath,
        Europe1700SchemaRepairKind Kind,
        int ExpectedOccurrences);

    private enum Europe1700SchemaRepairKind
    {
        EquipmentElementCase,
        TraitsElementCase,
        StrayElementTerminator,
        MergedBearskinEquipmentXslt,
        MergedNpcCompatibilityXslt,
        InvalidBearskinCapeEquipment,
        WorkshopOutputCategories
    }

    private static void AddManifestTransformation(
        BannerlordModule module,
        bool addCoopOrdering,
        IReadOnlyCollection<string> enableDllNames,
        ICollection<PendingChange> proposed,
        IReadOnlyCollection<string>? disableDllNames = null,
        bool suppressSubModules = false,
        XmlDocument? sourceManifest = null)
    {
        var manifestPath = Path.Combine(module.Path, "SubModule.xml");
        var document = sourceManifest ?? LoadManifest(manifestPath);
        var root = document.DocumentElement!;

        foreach (XmlElement dependency in root.SelectNodes("./DependedModules/DependedModule")!)
        {
            var id = dependency.GetAttribute("Id");
            if (ClientOnlyOfficialDependencies.Contains(id))
                dependency.ParentNode!.RemoveChild(dependency);
        }

        if (addCoopOrdering &&
            !module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase))
        {
            var container = root.ChildNodes.OfType<XmlElement>().FirstOrDefault(element =>
                element.LocalName.Equals("DependedModuleMetadatas", StringComparison.OrdinalIgnoreCase));
            if (container is null)
            {
                container = document.CreateElement("DependedModuleMetadatas");
                root.AppendChild(container);
            }

            var existing = container.ChildNodes.OfType<XmlElement>().FirstOrDefault(element =>
                element.LocalName.Equals("DependedModuleMetadata", StringComparison.OrdinalIgnoreCase) &&
                element.GetAttribute("Id").Equals("Coop", StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = document.CreateElement("DependedModuleMetadata");
                existing.SetAttribute("Id", "Coop");
                container.AppendChild(existing);
            }

            existing.SetAttribute("Order", "LoadBeforeThis");
            existing.SetAttribute("Optional", "true");
        }

        disableDllNames ??= Array.Empty<string>();
        var disabledSet = disableDllNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enableDllNames.Any(disabledSet.Contains))
            throw new InvalidDataException("A server submodule DLL cannot be both enabled and disabled.");

        foreach (var dllName in enableDllNames
                     .Concat(disableDllNames)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var submodules = root.SelectNodes("./SubModules/SubModule")!
                .OfType<XmlElement>()
                .Where(element =>
                    Value(element, "DLLName").Equals(dllName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (submodules.Length == 0)
                throw new InvalidDataException(
                    $"Manifest does not declare expected server submodule '{dllName}': {manifestPath}");

            foreach (var submodule in submodules)
            {
                var tags = submodule.ChildNodes.OfType<XmlElement>().FirstOrDefault(element =>
                    element.LocalName.Equals("Tags", StringComparison.OrdinalIgnoreCase));
                if (suppressSubModules || disabledSet.Contains(dllName))
                {
                    if (tags is null)
                    {
                        tags = document.CreateElement("Tags");
                        submodule.AppendChild(tags);
                    }
                    foreach (var setting in new[]
                             {
                                 (Key: "DedicatedServerType", Value: "none"),
                                 (Key: "IsNoRenderModeElement", Value: "false")
                             })
                    {
                        var tag = tags.ChildNodes.OfType<XmlElement>().FirstOrDefault(element =>
                            element.LocalName.Equals("Tag", StringComparison.OrdinalIgnoreCase) &&
                            element.GetAttribute("key").Equals(setting.Key, StringComparison.OrdinalIgnoreCase));
                        if (tag is null)
                        {
                            tag = document.CreateElement("Tag");
                            tag.SetAttribute("key", setting.Key);
                            tags.AppendChild(tag);
                        }
                        tag.SetAttribute("value", setting.Value);
                    }
                }
                else if (tags is not null)
                {
                    foreach (var tag in tags.ChildNodes.OfType<XmlElement>().Where(element =>
                                 element.LocalName.Equals("Tag", StringComparison.OrdinalIgnoreCase) &&
                                 (element.GetAttribute("key").Equals(
                                      "DedicatedServerType",
                                      StringComparison.OrdinalIgnoreCase) ||
                                  element.GetAttribute("key").Equals(
                                      "IsNoRenderModeElement",
                                      StringComparison.OrdinalIgnoreCase))).ToArray())
                    {
                        tags.RemoveChild(tag);
                    }
                }
            }
        }

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            NewLineChars = Environment.NewLine,
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = document.FirstChild is not XmlDeclaration
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        AddPendingChange(
            proposed,
            manifestPath,
            stream.ToArray(),
            $"Transform {module.Id} manifest for dedicated-server loading");
    }

    private static IReadOnlyList<BridgeAuthorityRule> DiscoverMcmSettingsFallbackRules(
        IReadOnlyList<BannerlordModule> modules)
    {
        var rules = new List<BridgeAuthorityRule>();
        foreach (var module in modules.Where(candidate => !IsEarlyFramework(candidate)))
        {
            var manifest = LoadManifest(Path.Combine(module.Path, "SubModule.xml"));
            foreach (var submodule in manifest.SelectNodes("/Module/SubModules/SubModule")!
                         .OfType<XmlElement>())
            {
                var dllName = Value(submodule, "DLLName");
                var typeName = Value(submodule, "SubModuleClassType");
                if (string.IsNullOrWhiteSpace(dllName) || string.IsNullOrWhiteSpace(typeName))
                    continue;

                var assemblyPath = FindDeclaredAssembly(module.Path, dllName);
                if (assemblyPath is null)
                    continue;
                var rule = TryCreateMcmSettingsFallbackRule(
                    module.Id,
                    dllName,
                    typeName,
                    assemblyPath);
                if (rule is not null)
                    rules.Add(rule);
            }
        }
        return rules;
    }

    private static BridgeAuthorityRule? TryCreateMcmSettingsFallbackRule(
        string moduleId,
        string dllName,
        string typeName,
        string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
                return null;
            var reader = peReader.GetMetadataReader();
            if (!reader.AssemblyReferences.Any(handle =>
                    reader.GetString(reader.GetAssemblyReference(handle).Name)
                        .StartsWith("MCM", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var typeHandle = reader.TypeDefinitions.FirstOrDefault(handle =>
            {
                var type = reader.GetTypeDefinition(handle);
                var name = reader.GetString(type.Name);
                var @namespace = reader.GetString(type.Namespace);
                var fullName = string.IsNullOrWhiteSpace(@namespace) ? name : $"{@namespace}.{name}";
                return fullName.Equals(typeName, StringComparison.Ordinal);
            });
            if (typeHandle.IsNil)
                return null;
            var typeDefinition = reader.GetTypeDefinition(typeHandle);
            var onGameStartCount = typeDefinition.GetMethods().Count(handle =>
            {
                var method = reader.GetMethodDefinition(handle);
                return reader.GetString(method.Name).Equals("OnGameStart", StringComparison.Ordinal) &&
                       method.GetParameters().Count(parameterHandle =>
                           reader.GetParameter(parameterHandle).SequenceNumber > 0) == 2;
            });
            return onGameStartCount == 1
                ? new BridgeAuthorityRule(
                    moduleId,
                    dllName,
                    typeName,
                    "OnGameStart",
                    2,
                    BridgeInvocationScope.ServerSettingsFallback)
                : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static string? FindDeclaredAssembly(string modulePath, string dllName)
    {
        foreach (var relativeBin in new[]
                 {
                     Path.Combine("bin", "Win64_Shipping_Server"),
                     Path.Combine("bin", "Win64_Shipping_Client"),
                     Path.Combine("bin", "Gaming.Desktop.x64_Shipping_Client")
                 })
        {
            var candidate = Path.Combine(modulePath, relativeBin, dllName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IReadOnlyList<BannerlordModule> BuildGenericDependencyFamily(
        BannerlordModule selected,
        IReadOnlyDictionary<string, BannerlordModule> byId,
        string modulesRoot,
        ICollection<string> blockers,
        ICollection<string> selectedIds)
    {
        var family = new List<BannerlordModule>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<BannerlordModule>();
        pending.Push(selected);

        while (pending.Count > 0)
        {
            var module = pending.Pop();
            if (!visited.Add(module.Id))
                continue;

            if (ProtectedModuleIds.Contains(module.Id) ||
                ClientOnlyOfficialDependencies.Contains(module.Id))
                continue;
            if (!module.IsInstalled)
            {
                blockers.Add($"Required module is not installed: {module.Id}.");
                continue;
            }

            EnsureDirectChild(module.Path, modulesRoot, $"Module '{module.Id}'");
            family.Add(module);
            selectedIds.Add(module.Id);

            foreach (var dependencyId in module.Dependencies)
            {
                if (ProtectedModuleIds.Contains(dependencyId) ||
                    ClientOnlyOfficialDependencies.Contains(dependencyId))
                    continue;
                if (!byId.TryGetValue(dependencyId, out var dependency) || !dependency.IsInstalled)
                {
                    blockers.Add($"Required dependency is missing: {module.Id} -> {dependencyId}.");
                    continue;
                }
                pending.Push(dependency);
            }
        }

        return family;
    }

    private void AddBridgePackage(
        IReadOnlyList<BannerlordModule> installedModules,
        IReadOnlyCollection<string> preparedIds,
        string serverRoot,
        ICollection<PendingChange> proposed,
        ICollection<string> selectedIds,
        ICollection<string> warnings,
        IReadOnlyList<BridgeAuthorityRule>? authorityRules = null,
        IReadOnlyList<BridgeContentExclusion>? contentExclusions = null,
        IReadOnlyList<BridgeServerFileRedirect>? serverFileRedirects = null,
        IReadOnlyList<BridgeServerXmlOverlay>? serverXmlOverlays = null,
        IReadOnlyList<BridgeClientAssemblyResolve>? clientAssemblyResolves = null,
        IReadOnlyList<BridgeServerMapTerrainSize>? serverMapTerrainSizes = null,
        IReadOnlyList<BridgeRuntimeFeature>? runtimeFeatures = null,
        string campaignSaveDescription = "campaign",
        IReadOnlyList<BridgeDisabledSubModule>? disabledSubModules = null,
        IReadOnlyList<BridgeClientOnlySubModule>? clientOnlySubModules = null)
    {
        var compatibleModules = installedModules
            .Where(module => module.IsInstalled &&
                             (preparedIds.Contains(module.Id, StringComparer.OrdinalIgnoreCase) ||
                              module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (!compatibleModules.Any(module =>
                module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Released Coop must be installed before a bridge can be generated.");
        }

        var package = _bridgePackageBuilder.Build(
            compatibleModules,
            projectedModuleIds: preparedIds,
            authorityRules: authorityRules,
            contentExclusions: contentExclusions,
            plannedContentPaths: proposed.Select(change => change.TargetPath).ToArray(),
            serverFileRedirects: serverFileRedirects,
            serverXmlOverlays: serverXmlOverlays,
            gameVersionCompatibility: CreateGameVersionCompatibility(serverRoot),
            clientAssemblyResolves: clientAssemblyResolves,
            serverMapTerrainSizes: serverMapTerrainSizes,
            runtimeFeatures: runtimeFeatures,
            disabledSubModules: disabledSubModules,
            clientOnlySubModules: clientOnlySubModules);
        var bridgeRoot = Path.Combine(
            serverRoot,
            "engine",
            "Modules",
            package.ModuleId);
        AddPendingChange(
            proposed,
            Path.Combine(bridgeRoot, "SubModule.xml"),
            package.Manifest,
            $"Create generated bridge manifest {package.ModuleId}");
        AddPendingChange(
            proposed,
            Path.Combine(bridgeRoot, "bcs-coop-bridge.config"),
            package.Configuration,
            "Create module/version bridge configuration");
        AddPendingChange(
            proposed,
            Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Server", "BCS.CoopBridge.dll"),
            package.Assembly,
            "Install generic Coop bridge runtime on the dedicated server");
        AddPendingChange(
            proposed,
            Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Client", "BCS.CoopBridge.dll"),
            package.ClientAssembly,
            "Install matching client bridge runtime for full package identity validation");
        foreach (var overlay in package.ServerXmlOverlays)
        {
            AddPendingChange(
                proposed,
                Path.Combine(
                    bridgeRoot,
                    overlay.OverlayRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                overlay.Content,
                "Install bridge-owned server XML overlay for " +
                overlay.ModuleId + "/" + overlay.RelativePath);
        }
        AddPendingChange(
            proposed,
            Path.Combine(serverRoot, "bcs-client-packages", package.ModuleId + ".zip"),
            package.ClientPackageZip,
            "Create matching client bridge package");

        selectedIds.Add(package.ModuleId);
        AddProfileTransformation(installedModules, preparedIds, package.ModuleId, serverRoot, proposed);
        warnings.Add(
            $"Every client must install the generated package bcs-client-packages\\{package.ModuleId}.zip. " +
            "Coop will reject a different bridge module ID/version, and the bridge requires the declared module paths " +
            "and managed assembly identities at startup.");
        if (package.DisabledSubModules.Count > 0)
        {
            warnings.Add(
                "Before launching each client, remove or comment these DLL declarations from the matching client " +
                "mod SubModule.xml: " +
                string.Join(", ", package.DisabledSubModules.Select(value =>
                    value.ModuleId + "/" + value.DllName)) +
                ". The generated package does not overwrite client Workshop manifests and fails closed if one remains active.");
        }
        warnings.Add(
            $"The bridge never creates or repairs campaign state. Select an existing {campaignSaveDescription} save before starting " +
            "the server; missing-save handling remains owned by Bannerlord Coop.");
        if (package.GameVersionCompatibility is not null)
            warnings.Add(CreateGameVersionCompatibilityWarning(package.GameVersionCompatibility));
    }

    internal static string CreateGameVersionCompatibilityWarning(
        BridgeGameVersionCompatibility compatibility)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        if (compatibility.ServerVersion.Equals(
                compatibility.ClientVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            return
                "The dedicated server and Bannerlord client use the same base game version. " +
                "The bridge observed and records the exact semantic runtime pair " +
                $"{compatibility.ServerRuntimeVersion} -> {compatibility.ClientRuntimeVersion}. " +
                "No Coop base-version bypass is installed; required runtime names and method signatures still fail closed.";
        }

        return
            "The released Coop server and installed Bannerlord client use different supported base game versions. " +
            "The bridge records the exact semantic runtime pair and bypasses only Coop's game-version gate for " +
            $"{compatibility.ServerRuntimeVersion} -> {compatibility.ClientRuntimeVersion}; " +
            "required runtime names and method signatures still fail closed.";
    }

    internal static BridgeGameVersionCompatibility? CreateGameVersionCompatibility(
        string serverRoot)
    {
        var bannerlordRoot = ServerExecutableLocator.FindBannerlordInstallRoot();
        if (bannerlordRoot is null)
            return null;

        var serverManifestPath = Path.Combine(
            serverRoot,
            "engine",
            "Modules",
            "Native",
            "SubModule.xml");
        var clientManifestPath = Path.Combine(
            bannerlordRoot,
            "Modules",
            "Native",
            "SubModule.xml");
        var serverBin = Path.Combine(
            serverRoot,
            "engine",
            "bin",
            "Win64_Shipping_Server");
        var clientBin = Path.Combine(
            bannerlordRoot,
            "bin",
            "Win64_Shipping_Client");
        var missingRuntimeFiles = RequiredGameRuntimeFiles
            .SelectMany(fileName => new[]
            {
                Path.Combine(serverBin, fileName),
                Path.Combine(clientBin, fileName)
            })
            .Where(path => !File.Exists(path))
            .ToArray();
        if (!File.Exists(serverManifestPath) || !File.Exists(clientManifestPath) ||
            missingRuntimeFiles.Length != 0)
        {
            return null;
        }

        var serverVersion = ValidateManifestGameVersion(
            Value(
            LoadManifest(serverManifestPath).DocumentElement!,
            "Version"),
            "dedicated server");
        var clientVersion = ValidateManifestGameVersion(
            Value(
            LoadManifest(clientManifestPath).DocumentElement!,
            "Version"),
            "Bannerlord client");
        if (!serverVersion.Equals(clientVersion, StringComparison.OrdinalIgnoreCase) &&
            !SupportedGameVersionCompatibilityPairs.Contains(
                serverVersion + "|" + clientVersion))
        {
            throw new InvalidDataException(
                "Unsupported Bannerlord server/client base-version pair: " +
                serverVersion + " -> " + clientVersion + ".");
        }

        var serverRuntimeVersion = serverVersion + "." +
            ReadModuleManagerChangeSet(
                Path.Combine(serverBin, "TaleWorlds.ModuleManager.dll"),
                "dedicated server");
        var clientRuntimeVersion = clientVersion + "." +
            ReadModuleManagerChangeSet(
                Path.Combine(clientBin, "TaleWorlds.ModuleManager.dll"),
                "Bannerlord client");

        return new BridgeGameVersionCompatibility(
            serverVersion,
            clientVersion,
            serverRuntimeVersion,
            clientRuntimeVersion);
    }

    private static string ValidateManifestGameVersion(string version, string role)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            !Regex.IsMatch(
                version,
                @"^[abevd](?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException(
                "The " + role + " Native manifest has an invalid game version: " +
                (version ?? "<null>") + ".");
        }
        return version;
    }

    internal static int ReadModuleManagerChangeSet(string assemblyPath, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        try
        {
            using var stream = new FileStream(
                Path.GetFullPath(assemblyPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
                throw new InvalidDataException("The assembly has no managed metadata.");

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly ||
                !metadata.GetString(metadata.GetAssemblyDefinition().Name).Equals(
                    "TaleWorlds.ModuleManager",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The assembly identity is not TaleWorlds.ModuleManager.");
            }

            var constructorTokens = metadata.MemberReferences
                .Where(handle => IsApplicationVersionConstructor(metadata, handle))
                .Select(handle => MetadataTokens.GetToken(handle))
                .ToArray();
            if (constructorTokens.Length != 1)
            {
                throw new InvalidDataException(
                    "Expected exactly one instance void TaleWorlds.Library.ApplicationVersion" +
                    " constructor reference with parameters " +
                    "(ApplicationVersionType, int, int, int, int).");
            }

            var values = new[] { "ModuleInfo", "DependedModule" }
                .Select(typeName => ReadUpdateVersionChangeSet(
                    peReader,
                    metadata,
                    typeName,
                    constructorTokens[0]))
                .ToArray();
            if (values[0] <= 0 || values[0] != values[1])
            {
                throw new InvalidDataException(
                    "ModuleInfo and DependedModule do not declare one matching positive change set.");
            }
            return values[0];
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                "Could not read the " + role +
                " TaleWorlds.ModuleManager semantic game revision from " + assemblyPath + ": " +
                exception.Message,
                exception);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException)
        {
            throw new InvalidDataException(
                "Could not read the " + role +
                " TaleWorlds.ModuleManager semantic game revision from " + assemblyPath + ".",
                exception);
        }
    }

    private static bool IsApplicationVersionConstructor(
        MetadataReader metadata,
        MemberReferenceHandle handle)
    {
        var reference = metadata.GetMemberReference(handle);
        if (!metadata.GetString(reference.Name).Equals(".ctor", StringComparison.Ordinal) ||
            reference.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var parent = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
        if (!metadata.GetString(parent.Namespace).Equals(
                "TaleWorlds.Library",
                StringComparison.Ordinal) ||
            !metadata.GetString(parent.Name).Equals(
                "ApplicationVersion",
                StringComparison.Ordinal))
        {
            return false;
        }

        var signature = metadata.GetBlobReader(reference.Signature);
        var header = signature.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method ||
            header.CallingConvention != SignatureCallingConvention.Default ||
            !header.IsInstance ||
            header.HasExplicitThis ||
            header.IsGeneric ||
            signature.ReadCompressedInteger() != 5 ||
            signature.ReadSignatureTypeCode() != SignatureTypeCode.Void ||
            signature.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
        {
            return false;
        }

        var applicationVersionType = signature.ReadTypeHandle();
        if (!IsNamedType(
                metadata,
                applicationVersionType,
                "TaleWorlds.Library",
                "ApplicationVersionType"))
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if (signature.ReadSignatureTypeCode() != SignatureTypeCode.Int32)
                return false;
        }
        return signature.RemainingBytes == 0;
    }

    private static bool IsNamedType(
        MetadataReader metadata,
        EntityHandle handle,
        string expectedNamespace,
        string expectedName)
    {
        StringHandle namespaceHandle;
        StringHandle nameHandle;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                namespaceHandle = definition.Namespace;
                nameHandle = definition.Name;
                break;
            }
            case HandleKind.TypeReference:
            {
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                namespaceHandle = reference.Namespace;
                nameHandle = reference.Name;
                break;
            }
            default:
                return false;
        }

        return metadata.GetString(namespaceHandle).Equals(
                   expectedNamespace,
                   StringComparison.Ordinal) &&
               metadata.GetString(nameHandle).Equals(
                   expectedName,
                   StringComparison.Ordinal);
    }

    private static int ReadUpdateVersionChangeSet(
        PEReader peReader,
        MetadataReader metadata,
        string typeName,
        int applicationVersionConstructorToken)
    {
        var types = metadata.TypeDefinitions
            .Where(handle =>
            {
                var definition = metadata.GetTypeDefinition(handle);
                return metadata.GetString(definition.Namespace).Equals(
                           "TaleWorlds.ModuleManager",
                           StringComparison.Ordinal) &&
                       metadata.GetString(definition.Name).Equals(
                           typeName,
                           StringComparison.Ordinal);
            })
            .ToArray();
        if (types.Length != 1)
        {
            throw new InvalidDataException(
                "Expected exactly one TaleWorlds.ModuleManager." + typeName + " type.");
        }

        var methods = metadata.GetTypeDefinition(types[0]).GetMethods()
            .Where(handle =>
            {
                var definition = metadata.GetMethodDefinition(handle);
                return metadata.GetString(definition.Name).Equals(
                           "UpdateVersionChangeSet",
                           StringComparison.Ordinal) &&
                       !definition.Attributes.HasFlag(MethodAttributes.Static) &&
                       IsInstanceVoidParameterlessMethod(metadata, definition.Signature);
            })
            .ToArray();
        if (methods.Length != 1)
        {
            throw new InvalidDataException(
                typeName + " must contain exactly one instance void zero-parameter " +
                "UpdateVersionChangeSet method.");
        }

        var method = metadata.GetMethodDefinition(methods[0]);
        if (method.RelativeVirtualAddress == 0)
            throw new InvalidDataException(typeName + ".UpdateVersionChangeSet has no IL body.");
        var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il is null || il.Length == 0)
            throw new InvalidDataException(typeName + ".UpdateVersionChangeSet has empty IL.");

        var candidates = new List<int>();
        int? previousInteger = null;
        var offset = 0;
        while (offset < il.Length)
        {
            var opCode = ReadIlOpCode(il, ref offset);
            var operandOffset = offset;
            var operandSize = GetIlOperandSize(opCode, il, operandOffset);
            EnsureIlRange(il, operandOffset, operandSize);
            var integer = ReadLdcI4(opCode, il, operandOffset);
            if (opCode == OpCodes.Newobj && operandSize == sizeof(int) &&
                BitConverter.ToInt32(il, operandOffset) == applicationVersionConstructorToken &&
                previousInteger.HasValue)
            {
                candidates.Add(previousInteger.Value);
            }
            offset += operandSize;
            previousInteger = integer;
        }

        if (candidates.Count != 1 || candidates[0] <= 0)
        {
            throw new InvalidDataException(
                typeName + ".UpdateVersionChangeSet must load one positive change set immediately " +
                "before constructing ApplicationVersion.");
        }
        return candidates[0];
    }

    private static bool IsInstanceVoidParameterlessMethod(
        MetadataReader metadata,
        BlobHandle signatureHandle)
    {
        var signature = metadata.GetBlobReader(signatureHandle);
        var header = signature.ReadSignatureHeader();
        return header.Kind == SignatureKind.Method &&
               header.CallingConvention == SignatureCallingConvention.Default &&
               header.IsInstance &&
               !header.HasExplicitThis &&
               !header.IsGeneric &&
               signature.ReadCompressedInteger() == 0 &&
               signature.ReadSignatureTypeCode() == SignatureTypeCode.Void &&
               signature.RemainingBytes == 0;
    }

    private static OpCode ReadIlOpCode(byte[] il, ref int offset)
    {
        EnsureIlRange(il, offset, 1);
        ushort value = il[offset++];
        if (value == 0xFE)
        {
            EnsureIlRange(il, offset, 1);
            value = (ushort)(0xFE00 | il[offset++]);
        }
        if (!IlOpCodes.TryGetValue(value, out var opCode))
            throw new InvalidDataException("Unknown IL opcode 0x" + value.ToString("X4") + ".");
        return opCode;
    }

    private static int? ReadLdcI4(OpCode opCode, byte[] il, int operandOffset)
    {
        if (opCode == OpCodes.Ldc_I4_M1)
            return -1;
        if (opCode == OpCodes.Ldc_I4_0)
            return 0;
        if (opCode == OpCodes.Ldc_I4_1)
            return 1;
        if (opCode == OpCodes.Ldc_I4_2)
            return 2;
        if (opCode == OpCodes.Ldc_I4_3)
            return 3;
        if (opCode == OpCodes.Ldc_I4_4)
            return 4;
        if (opCode == OpCodes.Ldc_I4_5)
            return 5;
        if (opCode == OpCodes.Ldc_I4_6)
            return 6;
        if (opCode == OpCodes.Ldc_I4_7)
            return 7;
        if (opCode == OpCodes.Ldc_I4_8)
            return 8;
        if (opCode == OpCodes.Ldc_I4_S)
            return unchecked((sbyte)il[operandOffset]);
        return opCode == OpCodes.Ldc_I4
            ? BitConverter.ToInt32(il, operandOffset)
            : null;
    }

    private static int GetIlOperandSize(OpCode opCode, byte[] il, int operandOffset)
    {
        return opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineI or OperandType.ShortInlineVar or
                OperandType.ShortInlineBrTarget => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI or OperandType.ShortInlineR or
                OperandType.InlineBrTarget or OperandType.InlineString or
                OperandType.InlineField or OperandType.InlineMethod or
                OperandType.InlineType or OperandType.InlineTok or
                OperandType.InlineSig => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => ReadSwitchOperandSize(il, operandOffset),
            _ => throw new InvalidDataException(
                "Unsupported IL operand type " + opCode.OperandType + ".")
        };
    }

    private static int ReadSwitchOperandSize(byte[] il, int operandOffset)
    {
        EnsureIlRange(il, operandOffset, sizeof(int));
        var count = BitConverter.ToInt32(il, operandOffset);
        if (count < 0 || count > (il.Length - operandOffset - sizeof(int)) / sizeof(int))
            throw new InvalidDataException("Invalid IL switch operand.");
        return sizeof(int) + count * sizeof(int);
    }

    private static void EnsureIlRange(byte[] il, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > il.Length - count)
            throw new InvalidDataException("Truncated IL while reading the game revision.");
    }

    private static void AddProfileTransformation(
        IReadOnlyList<BannerlordModule> installedModules,
        IReadOnlyCollection<string> preparedIds,
        string bridgeId,
        string serverRoot,
        ICollection<PendingChange> proposed)
    {
        var entries = installedModules
            .Where(module => !module.Id.Equals(bridgeId, StringComparison.OrdinalIgnoreCase))
            .Select(module => new GeneratedModuleEntry
            {
                Id = module.Id,
                Enabled = module.Id.StartsWith(
                              CoopBridgePackageBuilder.BridgeIdPrefix,
                              StringComparison.OrdinalIgnoreCase)
                    ? false
                    : module.Enabled ||
                      preparedIds.Contains(module.Id, StringComparer.OrdinalIgnoreCase) ||
                      ProtectedModuleIds.Contains(module.Id)
            })
            .ToList();

        var coop = entries.FirstOrDefault(entry =>
            entry.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase));
        if (coop is null)
            throw new InvalidDataException("Cannot build a bridge load order without Coop.");
        coop.Enabled = true;
        NormalizePreparedProfileOrder(entries, installedModules, preparedIds);
        entries.Add(new GeneratedModuleEntry { Id = bridgeId, Enabled = true });

        var profile = new GeneratedModuleProfile
        {
            SchemaVersion = 1,
            Modules = entries
        };
        var bytes = Utf8NoBom.GetBytes(
            JsonSerializer.Serialize(profile, JsonOptions()) + Environment.NewLine);
        AddPendingChange(
            proposed,
            Path.Combine(serverRoot, "bcs-server-modules.json"),
            bytes,
            "Enable bridge-managed modules, place Coop before them, and place the generated bridge last");
    }

    private static void NormalizePreparedProfileOrder(
        List<GeneratedModuleEntry> entries,
        IReadOnlyList<BannerlordModule> installedModules,
        IReadOnlyCollection<string> preparedIds)
    {
        var active = entries.Where(entry => entry.Enabled).ToArray();
        var activeById = active.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var moduleById = installedModules
            .Where(module => activeById.ContainsKey(module.Id))
            .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var originalIndex = active
            .Select((entry, index) => (entry.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var outgoing = active.ToDictionary(
            entry => entry.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var incoming = active.ToDictionary(
            entry => entry.Id,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        void AddEdge(string before, string after)
        {
            if (before.Equals(after, StringComparison.OrdinalIgnoreCase) ||
                !activeById.ContainsKey(before) ||
                !activeById.ContainsKey(after) ||
                !outgoing[before].Add(after))
            {
                return;
            }
            incoming[after]++;
        }

        foreach (var module in moduleById.Values)
        {
            var isPrepared = preparedIds.Contains(module.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in module.Dependencies.Concat(module.MustLoadAfter))
            {
                if (!ClientOnlyOfficialDependencies.Contains(dependency) &&
                    !module.MustLoadBefore.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                    AddEdge(dependency, module.Id);
            }
            foreach (var later in module.MustLoadBefore)
            {
                if (isPrepared && later.Equals("Coop", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddEdge(module.Id, later);
            }
        }
        AddEdge("Sandbox", "Coop");
        foreach (var preparedId in preparedIds)
        {
            if (!ProtectedModuleIds.Contains(preparedId) &&
                moduleById.TryGetValue(preparedId, out var preparedModule) &&
                !IsEarlyFramework(preparedModule))
            {
                AddEdge("Sandbox", preparedId);
                AddEdge("Coop", preparedId);
            }
        }

        var ready = new List<string>(incoming
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key));
        var orderedIds = new List<string>(active.Length);
        while (ready.Count > 0)
        {
            ready.Sort((left, right) => originalIndex[left].CompareTo(originalIndex[right]));
            var next = ready[0];
            ready.RemoveAt(0);
            orderedIds.Add(next);
            foreach (var after in outgoing[next].OrderBy(id => originalIndex[id]))
            {
                incoming[after]--;
                if (incoming[after] == 0)
                    ready.Add(after);
            }
        }
        if (orderedIds.Count != active.Length)
            throw new InvalidDataException("Bridge-managed module dependencies contain a load-order cycle.");

        var orderedActive = orderedIds.Select(id => activeById[id]).ToArray();
        var activeIndex = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Enabled)
                entries[index] = orderedActive[activeIndex++];
        }

        var hostIndex = entries.FindIndex(entry =>
            entry.Id.Equals("DedicatedServer.Windows", StringComparison.OrdinalIgnoreCase));
        var nativeIndex = entries.FindIndex(entry =>
            entry.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));
        if (hostIndex >= 0 && nativeIndex >= 0 && hostIndex != nativeIndex + 1)
        {
            var host = entries[hostIndex];
            entries.RemoveAt(hostIndex);
            nativeIndex = entries.FindIndex(entry =>
                entry.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));
            entries.Insert(nativeIndex + 1, host);
        }
    }

    private static void AddClientDllProjection(
        BannerlordModule module,
        ICollection<PendingChange> proposed,
        IReadOnlyCollection<string>? includedDlls = null)
    {
        var source = Path.Combine(module.Path, "bin", "Win64_Shipping_Client");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Client managed bin was not found: {source}");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Linked client bin is not safe to project: {source}");

        var target = Path.Combine(module.Path, "bin", "Win64_Shipping_Server");
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (includedDlls is not null &&
                !includedDlls.Contains(Path.GetFileName(sourceFile), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if ((File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked assembly is not safe to project: {sourceFile}");
            var targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
            AddPendingChange(
                proposed,
                targetFile,
                File.ReadAllBytes(sourceFile),
                $"Project {Path.GetFileName(sourceFile)} into the server bin");
        }
    }

    private static bool IsEarlyFramework(BannerlordModule module) =>
        FrameworkModuleIds.Contains(module.Id) ||
        module.MustLoadBefore.Any(CoreRuntimeModuleIds.Contains);

    private static void AddPendingChange(
        ICollection<PendingChange> proposed,
        string targetPath,
        byte[] proposedBytes,
        string description)
    {
        var target = Path.GetFullPath(targetPath);
        var originalExists = File.Exists(target);
        var originalHash = originalExists ? HashFile(target) : string.Empty;
        var proposedHash = Hash(proposedBytes);
        if (originalExists && originalHash.Equals(proposedHash, StringComparison.Ordinal))
            return;

        if (proposed.Any(change =>
                change.TargetPath.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Compatibility plan contains duplicate target: {target}");
        }

        proposed.Add(new PendingChange(
            target,
            proposedBytes,
            originalExists,
            new CoopPreparationChange(
                originalExists
                    ? CoopPreparationChangeKind.ReplaceFile
                    : CoopPreparationChangeKind.CreateFile,
                target,
                description,
                originalHash,
                proposedHash)));
    }

    private static void VerifyUnchanged(PendingChange change)
    {
        if (change.OriginalExists)
        {
            if (!File.Exists(change.TargetPath) ||
                !HashFile(change.TargetPath).Equals(
                    change.PublicChange.OriginalSha256,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Compatibility target changed after analysis: {change.TargetPath}");
            }
        }
        else if (File.Exists(change.TargetPath))
        {
            throw new IOException(
                $"Compatibility target was created after analysis: {change.TargetPath}");
        }
    }

    private static void ReplaceFileSafely(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, bytes);
        try
        {
            if (File.Exists(path))
                File.Replace(temporary, path, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static IReadOnlyList<Exception> RollBackApplied(
        string serverRoot,
        string backupDirectory,
        IEnumerable<PendingChange> applied)
    {
        var errors = new List<Exception>();
        foreach (var change in applied.Reverse())
        {
            try
            {
                if (change.OriginalExists)
                {
                    var relative = Path.GetRelativePath(serverRoot, change.TargetPath);
                    var backup = Path.Combine(backupDirectory, "files", relative);
                    if (File.Exists(backup))
                        ReplaceFileSafely(change.TargetPath, File.ReadAllBytes(backup));
                }
                else if (File.Exists(change.TargetPath))
                {
                    File.Delete(change.TargetPath);
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return errors;
    }

    private static XmlDocument LoadManifest(string path)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestCharacters
        });
        document.Load(reader);
        if (document.DocumentElement?.LocalName != "Module")
            throw new InvalidDataException($"Manifest has no Module root: {path}");
        return document;
    }

    private static IEnumerable<string> DeclaredDllNames(XmlDocument document)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document
                     .SelectNodes("/Module/SubModules/SubModule/DLLName")!
                     .OfType<XmlElement>())
        {
            var name = element.GetAttribute("value");
            if (!Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
                !Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                !name.Equals(name.Trim(), StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"Unsafe declared DLL name: {name}");
            }
            if (!seen.Add(name))
                throw new InvalidDataException($"Duplicate declared DLL name: {name}");
            names.Add(name);
        }
        return names;
    }

    private static (
        IReadOnlyList<string> Selected,
        IReadOnlyList<string> Disabled) ResolveBridgeDllSelection(
        BannerlordModule module,
        BridgeDllSelection? selection)
    {
        var manifest = LoadManifest(Path.Combine(module.Path, "SubModule.xml"));
        var declared = DeclaredDllNames(manifest).ToArray();
        if (selection is null)
            return (declared, Array.Empty<string>());

        if (!selection.ModuleId.Equals(module.Id, StringComparison.OrdinalIgnoreCase) ||
            !selection.ModuleVersion.Equals(module.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The bridge DLL selection belongs to a different module or module version.");
        }

        var available = selection.AvailableDllNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (available.Count != declared.Length ||
            declared.Any(dllName => !available.Contains(dllName)))
        {
            throw new InvalidDataException(
                $"The declared DLL set changed after bridge options were selected for {module.Id}. " +
                "Reopen Bridge DLL Options and review the current manifest.");
        }

        var selected = declared
            .Where(selection.IsSelected)
            .ToArray();
        if (selected.Length != selection.SelectedDllNames.Count)
        {
            throw new InvalidDataException(
                $"The bridge DLL selection contains an undeclared DLL for {module.Id}.");
        }

        return (
            selected,
            declared.Where(dllName => !selection.IsSelected(dllName)).ToArray());
    }

    internal static IReadOnlyList<string> SelectEurope1700ServerDlls(XmlDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return DeclaredDllNames(manifest)
            .Where(dllName => !Europe1700ClientOnlyDlls.Contains(
                dllName,
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static (
        IReadOnlyList<string> Enabled,
        IReadOnlyList<string> Disabled) ValidateEurope1700DllSelection(
        XmlDocument manifest,
        IReadOnlyCollection<string>? selectedDllNames,
        IReadOnlyCollection<string>? disabledDllNames)
    {
        var declared = DeclaredDllNames(manifest).ToArray();
        var declaredSet = declared.ToHashSet(StringComparer.OrdinalIgnoreCase);

        static HashSet<string> ValidateSubset(
            IReadOnlyCollection<string> values,
            IReadOnlySet<string> declaredValues,
            string description)
        {
            var result = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (result.Count != values.Count)
                throw new InvalidDataException($"EOE {description} DLL selection contains duplicates.");
            var unknown = result.FirstOrDefault(value => !declaredValues.Contains(value));
            if (unknown is not null)
            {
                throw new InvalidDataException(
                    $"EOE {description} DLL selection is not declared by the module: {unknown}");
            }
            return result;
        }

        var disabledSet = disabledDllNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : ValidateSubset(disabledDllNames, declaredSet, "disabled");
        var enabledSet = selectedDllNames is null
            ? declaredSet.Where(value => !disabledSet.Contains(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : ValidateSubset(selectedDllNames, declaredSet, "enabled");
        if (disabledDllNames is null)
        {
            disabledSet = declaredSet.Where(value => !enabledSet.Contains(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (enabledSet.Overlaps(disabledSet) ||
            enabledSet.Count + disabledSet.Count != declaredSet.Count)
        {
            throw new InvalidDataException(
                "EOE enabled and disabled DLL selections must form an exact partition of declared DLLs.");
        }

        return (
            declared.Where(enabledSet.Contains).ToArray(),
            declared.Where(disabledSet.Contains).ToArray());
    }

    private static string? FindDeclaredDll(string modulePath, string dllName)
    {
        foreach (var bin in new[]
                 {
                     Path.Combine(modulePath, "bin", "Win64_Shipping_Server"),
                     Path.Combine(modulePath, "bin", "Win64_Shipping_Client"),
                     Path.Combine(modulePath, "bin", "Gaming.Desktop.x64_Shipping_Client")
                 })
        {
            var candidate = Path.Combine(bin, dllName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindAssembly(
        IEnumerable<BannerlordModule> modules,
        string modulesRoot,
        string assemblyName)
    {
        foreach (var module in modules)
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(module.Path, "bin", "Win64_Shipping_Server"),
                         Path.Combine(module.Path, "bin", "Win64_Shipping_Client"),
                         Path.Combine(module.Path, "bin", "Gaming.Desktop.x64_Shipping_Client")
                     })
            {
                var candidate = Path.Combine(directory, assemblyName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var clientModules = TryFindClientModules(modulesRoot);
        if (clientModules is null)
            return null;
        foreach (var moduleDirectory in Directory.EnumerateDirectories(
                     clientModules,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(moduleDirectory) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var bin in new[]
                     {
                         "Win64_Shipping_Client",
                         "Gaming.Desktop.x64_Shipping_Client",
                         "Win64_Shipping_Server"
                     })
            {
                var candidate = Path.Combine(moduleDirectory, "bin", bin, assemblyName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? TryFindClientModules(string serverModulesRoot)
    {
        for (var current = new DirectoryInfo(serverModulesRoot);
             current is not null;
             current = current.Parent)
        {
            if (!current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(
                current.FullName,
                "common",
                "Mount & Blade II Bannerlord",
                "Modules");
            return Directory.Exists(candidate) ? candidate : null;
        }

        return null;
    }

    private static string Value(XmlElement parent, string childName) =>
        parent.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(element =>
                element.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase))
            ?.GetAttribute("value") ?? string.Empty;

    private static bool IsRealmOfThronesModule(string id) =>
        RealmOfThronesModuleIds.Contains(id, StringComparer.OrdinalIgnoreCase) ||
        id.Equals("ROT-Map", StringComparison.OrdinalIgnoreCase);

    private static void EnsureDirectChild(string path, string root, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException($"{description} has no installed path.");

        var parent = Directory.GetParent(Path.GetFullPath(path))?.FullName;
        if (parent is null ||
            !parent.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{description} must be a direct child of the dedicated-server Modules directory: {path}");
        }
    }

    private static void ValidateRuntimeAssembly(
        string path,
        string trustedRoot,
        string expectedAssemblyName,
        string description,
        ICollection<string> blockers)
    {
        if (!IsRegularUnlinkedFileWithin(path, trustedRoot))
        {
            blockers.Add($"{description} is missing, linked, or outside its trusted root: {path}");
            return;
        }

        string? actualAssemblyName;
        try
        {
            actualAssemblyName = AssemblyName.GetAssemblyName(path).Name;
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
        {
            blockers.Add($"{description} is not a readable managed assembly: {path}");
            return;
        }
        if (!expectedAssemblyName.Equals(actualAssemblyName, StringComparison.Ordinal))
        {
            blockers.Add(
                $"{description} assembly identity mismatch. Expected {expectedAssemblyName}, " +
                $"found {actualAssemblyName ?? "<null>"}.");
            return;
        }

    }

    private static bool IsRegularUnlinkedFileWithin(string path, string trustedRoot)
    {
        if (!File.Exists(path))
            return false;

        var canonicalPath = Path.GetFullPath(path);
        var canonicalRoot = Path.GetFullPath(trustedRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(canonicalPath) &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return false;
        }

        for (var parent = Directory.GetParent(canonicalPath); parent is not null; parent = parent.Parent)
        {
            var parentPath = Path.GetFullPath(parent.FullName).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                return false;
            if (parentPath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void EnsureWithin(string path, string root, string description)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        EnsureSafeRelativePath(relative);
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException($"{description} escapes the server root: {path}");
    }

    private static void EnsureSafeRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Unsafe backup path: {relative}");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static IReadOnlyList<string> ReadEnabledModuleIds(string serverRoot)
    {
        var profilePath = Path.Combine(serverRoot, "bcs-server-modules.json");
        var profile = JsonSerializer.Deserialize<GeneratedModuleProfile>(
                          File.ReadAllText(profilePath, Utf8NoBom),
                          JsonOptions())
                      ?? throw new InvalidDataException($"Module profile is empty: {profilePath}");
        if (profile.SchemaVersion != 1)
            throw new InvalidDataException($"Module profile has an unsupported schema: {profilePath}");
        return profile.Modules
            .Where(entry => entry.Enabled)
            .Select(entry => entry.Id)
            .ToArray();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    private sealed record PendingPlan(
        CoopPreparationPlan PublicPlan,
        IReadOnlyList<PendingChange> Changes);

    internal sealed record PendingChange(
        string TargetPath,
        byte[] ProposedBytes,
        bool OriginalExists,
        CoopPreparationChange PublicChange);

    private sealed record BlockedAssemblyMarker(
        string AssemblyPath,
        string AssemblySha256,
        string ZoneIdentifierPath,
        byte[] ZoneIdentifierBytes);

    private sealed class BackupManifest
    {
        public int SchemaVersion { get; set; }
        public string PlanId { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public string ServerRoot { get; set; } = string.Empty;
        public string RuleId { get; set; } = string.Empty;
        public string[] ModuleIds { get; set; } = [];
        public List<BackupFileEntry> Files { get; set; } = [];
    }

    private sealed class BackupFileEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public bool OriginalExisted { get; set; }
        public string OriginalSha256 { get; set; } = string.Empty;
        public string AppliedSha256 { get; set; } = string.Empty;
    }

    private sealed class GeneratedModuleProfile
    {
        public int SchemaVersion { get; set; }
        public List<GeneratedModuleEntry> Modules { get; set; } = [];
    }

    private sealed class GeneratedManagedDependencyProfile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GeneratedManagedDependencyDirectory> Directories { get; set; } = [];
    }

    private sealed class GeneratedManagedDependencyDirectory
    {
        public string Path { get; set; } = string.Empty;
        public List<GeneratedManagedDependencyFile> RequiredFiles { get; set; } = [];
    }

    private sealed class GeneratedManagedDependencyFile
    {
        public string Name { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class GeneratedModuleEntry
    {
        public string Id { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }
}
