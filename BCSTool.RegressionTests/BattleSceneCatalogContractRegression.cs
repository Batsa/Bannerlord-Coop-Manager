using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BCSTool.Models;
using BCSTool.Services;

internal static class BattleSceneCatalogContractRegression
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    internal static void Run()
    {
        VerifyPackageContract();
        VerifyCompatibilityLifecycle();
    }

    private static void VerifyPackageContract()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bcs-battle-scene-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var modulesRoot = Path.Combine(root, "engine", "Modules");
            Directory.CreateDirectory(modulesRoot);
            CreateBaseModule(modulesRoot);
            var module = CreateModule(modulesRoot);
            var builder = new CoopBridgePackageBuilder();
            var contract = BattleSceneCatalogContractRegistry.CreateEurope1700();
            var features = new[]
            {
                BridgeRuntimeFeature.ClientDeterministicBattleSceneProjection
            };

            var package = builder.Build(
                [module],
                runtimeFeatures: features,
                battleSceneCatalogContract: contract);
            var repeated = builder.Build(
                [module],
                runtimeFeatures: features,
                battleSceneCatalogContract:
                    BattleSceneCatalogContractRegistry.CreateEurope1700());

            Assert(package.Configuration.AsSpan().StartsWith(
                    Utf8NoBom.GetBytes("BCS-COOP-BRIDGE|3\n")),
                "Battle-scene contract package did not opt into schema 3.");
            Assert(package.ClientPackageZip.SequenceEqual(repeated.ClientPackageZip),
                "Battle-scene contract package was not deterministic.");
            Assert(package.ModuleId.Equals(repeated.ModuleId, StringComparison.Ordinal),
                "Battle-scene contract package identity was not deterministic.");

            var configLines = Utf8NoBom.GetString(package.Configuration).Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries);
            var featureLine = "RUNTIME_FEATURE|" + Encode(
                nameof(BridgeRuntimeFeature.ClientDeterministicBattleSceneProjection));
            Assert(configLines.Count(line => line.Equals(featureLine, StringComparison.Ordinal)) == 1,
                "Schema 3 package did not declare the deterministic scene feature exactly once.");
            var contractRecord = configLines.Single(line => line.StartsWith(
                "BATTLE_SCENE_CATALOG_CONTRACT|",
                StringComparison.Ordinal));
            Assert(contractRecord.Equals(
                    "BATTLE_SCENE_CATALOG_CONTRACT|" + Encode(contract.TargetModuleId) + "|" +
                    Encode(contract.TargetVersion) + "|" + Encode(contract.BaseModuleId) + "|" +
                    Encode(contract.BaseVersion) + "|" + Encode(contract.RelativePath) + "|" +
                    contract.Sha256 + "|WARN_ONLY",
                    StringComparison.Ordinal),
                "Schema 3 contract record changed identity or warning policy.");

            var contractLines = Utf8NoBom.GetString(contract.Content).Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries);
            var candidates = contractLines.Where(line => line.StartsWith(
                "CANDIDATE|",
                StringComparison.Ordinal)).ToArray();
            var assets = contractLines.Where(line => line.StartsWith(
                "ASSET|",
                StringComparison.Ordinal)).ToArray();
            Assert(candidates.Length == 196,
                "Pinned EOE contract does not preserve all 196 ordered candidates.");
            Assert(assets.Length == 559,
                "Pinned EOE contract does not fingerprint exactly 559 runtime scene assets.");
            Assert(candidates.Select(CandidateIdentity).Distinct(StringComparer.Ordinal).Count() <
                   candidates.Length,
                "Pinned EOE contract unexpectedly discarded duplicate candidate weighting.");
            for (var index = 0; index < candidates.Length; index++)
            {
                Assert(candidates[index].Split('|')[1].Equals(
                        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal),
                    "Pinned EOE candidate ordinals are not contiguous and ordered.");
            }

            using (var archiveStream = new MemoryStream(package.ClientPackageZip, writable: false))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(
                    $"Modules/{package.ModuleId}/{contract.RelativePath}");
                Assert(entry is not null,
                    "Client ZIP omitted the pinned battle-scene catalog contract.");
                using var payload = new MemoryStream();
                using (var source = entry!.Open())
                    source.CopyTo(payload);
                Assert(payload.ToArray().SequenceEqual(contract.Content),
                    "Client ZIP changed the pinned battle-scene catalog contract bytes.");
            }

            var identityPayload = package.Configuration
                .Concat(contract.Content)
                .Concat(package.Assembly)
                .Concat(package.ClientAssembly)
                .ToArray();
            var expectedId = CoopBridgePackageBuilder.BridgeIdPrefix +
                Convert.ToHexString(SHA256.HashData(identityPayload))[..24].ToLowerInvariant();
            Assert(package.ModuleId.Equals(expectedId, StringComparison.Ordinal),
                "Bridge identity does not bind the exact contract bytes.");

            InstallBridge(modulesRoot, package);
            var installed = new ModuleScanner().Scan(modulesRoot);
            var installedTarget = installed.Single(value => value.Id == "Europe1700");
            var installedBridge = installed.Single(value => value.Id == package.ModuleId);
            installedTarget.SetInitialEnabled(true);
            installedBridge.SetInitialEnabled(true);
            var selection = new BridgeDllSelectionService().Resolve(
                installedTarget,
                installed,
                root);
            Assert(selection.SelectedDllNames.SequenceEqual(
                    ["Bannerlord Coop Manager.dll"],
                    StringComparer.OrdinalIgnoreCase),
                "Schema 3 bridge did not preserve its explicit target DLL selection.");

            var installedContractPath = Path.Combine(
                installedBridge.Path,
                contract.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var installedContract = File.ReadAllBytes(installedContractPath);
            installedContract[^2] ^= 1;
            File.WriteAllBytes(installedContractPath, installedContract);
            AssertThrowsInvalidData(
                () => new BridgeDllSelectionService().Resolve(
                    installedTarget,
                    installed,
                    root),
                "Schema 3 DLL recovery accepted a modified installed contract.");

            var generic = builder.Build([module]);
            Assert(generic.Configuration.AsSpan().StartsWith(
                    Utf8NoBom.GetBytes("BCS-COOP-BRIDGE|2\n")),
                "Contract-free package no longer uses schema 2.");
            Assert(generic.BattleSceneCatalogContract is null &&
                   !Utf8NoBom.GetString(generic.Configuration).Contains(
                       "BATTLE_SCENE_CATALOG_CONTRACT",
                       StringComparison.Ordinal) &&
                   !Utf8NoBom.GetString(generic.Configuration).Contains(
                       nameof(BridgeRuntimeFeature.ClientDeterministicBattleSceneProjection),
                       StringComparison.Ordinal),
                "Generic package accidentally enabled EOE battle-scene behavior.");

            AssertThrowsInvalidData(
                () => builder.Build([module], runtimeFeatures: features),
                "Builder accepted deterministic scene projection without a pinned contract.");
            AssertThrowsInvalidData(
                () => builder.Build([module], battleSceneCatalogContract: contract),
                "Builder accepted a scene contract without its explicit runtime feature.");
            var changedContent = contract.Content.ToArray();
            changedContent[^2] ^= 1;
            AssertThrowsInvalidData(
                () => builder.Build(
                    [module],
                    runtimeFeatures: features,
                    battleSceneCatalogContract: contract with { Content = changedContent }),
                "Builder accepted scene-contract bytes that did not match the pinned hash.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyCompatibilityLifecycle()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bcs-battle-scene-lifecycle-" + Guid.NewGuid().ToString("N"));
        var modulesRoot = Path.Combine(root, "engine", "Modules");
        Directory.CreateDirectory(modulesRoot);
        try
        {
            CreateLifecycleFixture(root, modulesRoot);
            var scanner = new ModuleScanner();
            var modules = scanner.Scan(modulesRoot);
            var selected = modules.Single(module => module.Id == "Europe1700");
            var patcher = new CoopCompatibilityPatcher();
            var originalFiles = FingerprintFiles(root, excludeBackups: true);

            var plan = patcher.CreatePlan(selected, modules, root);
            Assert(plan.CanApply, plan.Summary);
            var bridgeId = plan.ModuleIds.Single(id => id.StartsWith(
                CoopBridgePackageBuilder.BridgeIdPrefix,
                StringComparison.Ordinal));
            var bridgeRoot = Path.Combine(modulesRoot, bridgeId);
            var contract = BattleSceneCatalogContractRegistry.CreateEurope1700();
            var contractPath = Path.Combine(
                bridgeRoot,
                contract.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var configPath = Path.Combine(bridgeRoot, "bcs-coop-bridge.config");
            var zipPath = Path.Combine(root, "bcs-client-packages", bridgeId + ".zip");
            var profilePath = Path.Combine(root, "bcs-server-modules.json");
            Assert(plan.Changes.Any(change => Path.GetFullPath(change.TargetPath) ==
                                              Path.GetFullPath(contractPath)) &&
                   plan.Changes.Any(change => Path.GetFullPath(change.TargetPath) ==
                                              Path.GetFullPath(configPath)) &&
                   plan.Changes.Any(change => Path.GetFullPath(change.TargetPath) ==
                                              Path.GetFullPath(zipPath)) &&
                   plan.Changes.Any(change => Path.GetFullPath(change.TargetPath) ==
                                              Path.GetFullPath(profilePath)),
                "Schema 3 plan omitted contract, configuration, client ZIP, or module profile lifecycle state.");

            var applied = patcher.Apply(plan);
            Assert(File.ReadAllBytes(contractPath).SequenceEqual(contract.Content),
                "Applied bridge contract differs from the pinned release artifact.");
            var configuration = File.ReadAllBytes(configPath);
            var configurationText = Utf8NoBom.GetString(configuration);
            Assert(configurationText.StartsWith("BCS-COOP-BRIDGE|3\n", StringComparison.Ordinal) &&
                   configurationText.Contains(contract.Sha256 + "|WARN_ONLY", StringComparison.Ordinal),
                "Applied schema 3 configuration lost contract identity or warning policy.");
            var serverRuntime = File.ReadAllBytes(Path.Combine(
                bridgeRoot, "bin", "Win64_Shipping_Server", "BCS.CoopBridge.dll"));
            var clientRuntime = File.ReadAllBytes(Path.Combine(
                bridgeRoot, "bin", "Win64_Shipping_Client", "BCS.CoopBridge.dll"));
            var expectedBridgeId = CoopBridgePackageBuilder.BridgeIdPrefix +
                Convert.ToHexString(SHA256.HashData(
                    configuration.Concat(contract.Content)
                        .Concat(serverRuntime)
                        .Concat(clientRuntime)
                        .ToArray()))[..24].ToLowerInvariant();
            Assert(bridgeId.Equals(expectedBridgeId, StringComparison.Ordinal),
                "Applied bridge ID does not bind config, contract, and both runtimes.");
            AssertClientZip(zipPath, bridgeId, configuration, contract);
            AssertProfile(profilePath, bridgeId);

            var appliedFingerprint = FingerprintFiles(root, excludeBackups: true);
            modules = scanner.Scan(modulesRoot);
            selected = modules.Single(module => module.Id == "Europe1700");
            var replan = patcher.CreatePlan(selected, modules, root);
            Assert(replan.Blockers.Count == 0 && replan.Changes.Count == 0,
                "Unchanged applied schema 3 package produced a rewrite on replan: " + replan.Summary);
            Assert(replan.ModuleIds.Contains(bridgeId, StringComparer.Ordinal) &&
                   FingerprintFiles(root, excludeBackups: true).SequenceEqual(appliedFingerprint),
                "No-op schema 3 replan changed files or selected another BridgeId.");

            patcher.Revert(applied.ManifestPath);
            Assert(FingerprintFiles(root, excludeBackups: true).SequenceEqual(originalFiles),
                "Schema 3 revert left package, contract, ZIP, profile, overlay, or source-file residue.");
            Assert(!Directory.Exists(bridgeRoot) ||
                   !Directory.EnumerateFiles(bridgeRoot, "*", SearchOption.AllDirectories).Any(),
                "Schema 3 revert left generated bridge files.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertClientZip(
        string zipPath,
        string bridgeId,
        byte[] configuration,
        BridgeBattleSceneCatalogContract contract)
    {
        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert(ReadEntry($"Modules/{bridgeId}/bcs-coop-bridge.config").SequenceEqual(configuration) &&
               ReadEntry($"Modules/{bridgeId}/{contract.RelativePath}").SequenceEqual(contract.Content),
            "Applied client ZIP omitted or changed schema 3 package inputs.");

        byte[] ReadEntry(string path)
        {
            var entry = archive.GetEntry(path)
                ?? throw new InvalidOperationException("Client ZIP entry is missing: " + path);
            using var result = new MemoryStream();
            using (var source = entry.Open())
                source.CopyTo(result);
            return result.ToArray();
        }
    }

    private static void AssertProfile(string profilePath, string bridgeId)
    {
        using var profile = JsonDocument.Parse(File.ReadAllBytes(profilePath));
        var entries = profile.RootElement.GetProperty("Modules").EnumerateArray().ToArray();
        Assert(entries[^1].GetProperty("Id").GetString() == bridgeId &&
               entries[^1].GetProperty("Enabled").GetBoolean(),
            "Applied module profile did not enable the generated bridge last.");
    }

    private static BannerlordModule CreateModule(string root)
    {
        var moduleRoot = Path.Combine(root, "Europe1700");
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            """
            <Module>
              <Name value="Empires of Europe 1700" />
              <Id value="Europe1700" />
              <Version value="v1.4.7.1" />
              <DependedModules />
              <SubModules>
                <SubModule>
                  <Name value="EOE test" />
                  <DLLName value="Bannerlord Coop Manager.dll" />
                  <SubModuleClassType value="BCSTool.App" />
                </SubModule>
              </SubModules>
            </Module>
            """,
            Utf8NoBom);
        var bin = Path.Combine(moduleRoot, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(bin);
        File.Copy(
            typeof(CoopBridgePackageBuilder).Assembly.Location,
            Path.Combine(bin, "Bannerlord Coop Manager.dll"));
        return new ModuleScanner().Scan(root).Single(module => module.Id == "Europe1700");
    }

    private static void CreateBaseModule(string modulesRoot)
    {
        var moduleRoot = Path.Combine(modulesRoot, "SandBoxCore");
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            """
            <Module>
              <Name value="SandBox Core" />
              <Id value="SandBoxCore" />
              <Version value="v1.4.8" />
              <DependedModules />
              <SubModules />
            </Module>
            """,
            Utf8NoBom);
    }

    private static void InstallBridge(string modulesRoot, CoopBridgePackage package)
    {
        var bridgeRoot = Path.Combine(modulesRoot, package.ModuleId);
        Directory.CreateDirectory(bridgeRoot);
        File.WriteAllBytes(Path.Combine(bridgeRoot, "SubModule.xml"), package.Manifest);
        File.WriteAllBytes(
            Path.Combine(bridgeRoot, "bcs-coop-bridge.config"),
            package.Configuration);
        var serverBin = Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Server");
        var clientBin = Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(serverBin);
        Directory.CreateDirectory(clientBin);
        File.WriteAllBytes(Path.Combine(serverBin, "BCS.CoopBridge.dll"), package.Assembly);
        File.WriteAllBytes(Path.Combine(clientBin, "BCS.CoopBridge.dll"), package.ClientAssembly);
        var contract = package.BattleSceneCatalogContract
            ?? throw new InvalidOperationException("Test package omitted its scene contract.");
        var contractPath = Path.Combine(
            bridgeRoot,
            contract.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(contractPath)!);
        File.WriteAllBytes(contractPath, contract.Content);
    }

    private static void CreateLifecycleFixture(string root, string modulesRoot)
    {
        CreatePlainModule(modulesRoot, "Native", "v1.4.8");
        CreatePlainModule(modulesRoot, "SandBoxCore", "v1.4.8", ["Native"]);
        var sandbox = CreatePlainModule(modulesRoot, "Sandbox", "v1.4.8", ["SandBoxCore"]);
        CreatePlainModule(modulesRoot, "Coop", "v0.1.2", ["Sandbox"]);
        var eoe = CreatePlainModule(modulesRoot, "Europe1700", "v1.4.7.1", ["Sandbox"]);
        Directory.CreateDirectory(Path.Combine(eoe, "bin", "Win64_Shipping_Client"));

        WriteManagedAssembly(
            Path.Combine(root, "engine", "bin", "Win64_Shipping_Server", "DedicatedServer.Core.dll"),
            "DedicatedServer.Core");
        WriteManagedAssembly(
            Path.Combine(sandbox, "bin", "Win64_Shipping_Server", "SandBox.dll"),
            "SandBox");
        WriteText(Path.Combine(eoe, "SceneObj", "Main_map", "scene.xscene"), "<scene />");
        WriteBytes(Path.Combine(
            eoe,
            "ModuleData",
            "DistanceCaches",
            "settlements_distance_cache_Default.bin"), [1, 7, 0, 0]);
        WriteText(Path.Combine(eoe, "ModuleData", "collision_infos.xml"), "<base />");
        WriteText(Path.Combine(eoe, "ModuleData", "action_sets.xml"), ActionSetsFixture);
        WriteText(
            Path.Combine(eoe, "ModuleData", "action_types.xml"),
            "<action_types><action name=\"cla_reload_bomb\" /></action_types>");
        WriteText(
            Path.Combine(eoe, "Prefabs", "props_siege_trebuchets.xml"),
            "<prefabs>" + string.Concat(Enumerable.Repeat(
                "<variable name=\"ProjectileSpeed\" value=\"53.500\"/>", 4)) + "</prefabs>");
        WriteText(Path.Combine(root, "engine", "XmlSchemas", "Items.xsd"), ItemsSchemaFixture);
        WriteText(
            Path.Combine(eoe, "ModuleData", "items", "items_guns.xml"),
            "<Items><Item id=\"musket\"><ItemComponent><Weapon/><Weapon/></ItemComponent></Item></Items>");

        var equipmentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sandboxcore_equipment_sets_western_npcs.xml"] = 461,
            ["sandboxcore_equipment_sets_muslim_npcs.xml"] = 412,
            ["sandboxcore_equipment_sets_turkic_npcs.xml"] = 444,
            ["sandboxcore_equipment_sets_northern_npcs.xml"] = 455,
            ["sandboxcore_equipment_sets_eastern_npcs.xml"] = 383,
            ["sandboxcore_equipment_sets_lords_aserai.xml"] = 69,
            ["sandboxcore_equipment_sets_lords_italian.xml"] = 11,
            ["sandboxcore_equipment_sets_lords_khuzait.xml"] = 63
        };
        foreach (var entry in equipmentCounts)
        {
            WriteText(
                Path.Combine(eoe, "ModuleData", "lord_equipment_sets", entry.Key),
                "<root>" + string.Concat(Enumerable.Repeat("<equipment />", entry.Value)) + "</root>");
        }
        WriteText(
            Path.Combine(eoe, "ModuleData", "lords_main", "lords_ottoman_extra.xml"),
            "<root>" + string.Concat(Enumerable.Repeat("<traits></traits>", 24)) + "</root>");
        WriteText(
            Path.Combine(eoe, "ModuleData", "npccharacters", "spnpccharacters_scottish.xml"),
            "<root><EquipmentSet equipmentType=\"Civilian\" />/></root>");
        WriteText(
            Path.Combine(eoe, "ModuleData", "sandbox_core_equipment_sets", "sandboxcore_equipment_sets.xslt"),
            IdentityXsltFixture);
        WriteText(
            Path.Combine(eoe, "ModuleData", "trooptrees", "spnpccharacters.xslt"),
            IdentityXsltFixture);

        var bearskinCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sandboxcore_equipment_sets_baltic.xml"] = 1,
            ["sandboxcore_equipment_sets_battania.xml"] = 9,
            ["sandboxcore_equipment_sets_cossack.xml"] = 1,
            ["sandboxcore_equipment_sets_finnic.xml"] = 1,
            ["sandboxcore_equipment_sets_rus.xml"] = 1,
            ["sandboxcore_equipment_sets_scottish.xml"] = 9,
            ["sandboxcore_equipment_sets_sturgia.xml"] = 1,
            ["sandboxcore_equipment_sets_welsh.xml"] = 9
        };
        foreach (var entry in bearskinCounts)
        {
            WriteText(
                Path.Combine(eoe, "ModuleData", "sandbox_core_equipment_sets", entry.Key),
                "<root>" + string.Concat(Enumerable.Repeat(
                    "<Equipment slot=\"Cape\" id=\"Item.bearskin\" />",
                    entry.Value)) + "</root>");
        }
        WriteText(
            Path.Combine(eoe, "ModuleData", "spworkshops.xml"),
            "<root><Output output=\"ItemCategory.ranged_weapons\"/>" +
            "<Output output=\"ItemCategory.ranged_weapons_2\"/>" +
            "<Output output=\"ItemCategory.ranged_weapons_3\"/>" +
            "<Output output=\"ItemCategory.ranged_weapons_3\"/>" +
            "<Output output=\"ItemCategory.ranged_weapons_4\"/></root>");
    }

    private static string CreatePlainModule(
        string modulesRoot,
        string id,
        string version,
        IReadOnlyList<string>? dependencies = null)
    {
        var moduleRoot = Path.Combine(modulesRoot, id);
        Directory.CreateDirectory(moduleRoot);
        var dependencyXml = string.Concat((dependencies ?? []).Select(
            dependency => $"<DependedModule Id=\"{dependency}\" />"));
        WriteText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            $"<Module><Name value=\"{id}\"/><Id value=\"{id}\"/><Version value=\"{version}\"/>" +
            $"<DependedModules>{dependencyXml}</DependedModules><SubModules/></Module>");
        return moduleRoot;
    }

    private static void WriteManagedAssembly(string path, string assemblyName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var module = builder.DefineDynamicModule(assemblyName);
        module.DefineType(
                assemblyName + ".Marker",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed)
            .CreateType();
        builder.Save(path);
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static IReadOnlyList<string> FingerprintFiles(string root, bool excludeBackups) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !excludeBackups || !Path.GetRelativePath(root, path).StartsWith(
                "bcs-compatibility-backups" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.GetRelativePath(root, path) + ":" +
                            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .ToArray();

    private static string CandidateIdentity(string line)
    {
        var fields = line.Split('|');
        return string.Join('|', fields.Skip(2));
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Utf8NoBom.GetBytes(value));

    private const string ActionSetsFixture = """
        <action_sets><action_set>
        <action type="act_ready_musket_cla" animation="1_cla_ready_musket" />
        <action type="act_release_musket_cla" animation="1_cla_release_musket" />
        <action type="act_ready_continue_musket_cla" animation="1_cla_ready_continue_musket" />
        <action type="act_reload_musket_cla" animation="reznov_anim_reload_musket" />
        <action type="act_reload_musket_continue_cla" animation="reznov_anim_reload_musket_continue" />
        <action type="act_ready_cannon_cla" animation="2_cla_ready_cannon" />
        <action type="act_release_cannon_cla" animation="2_cla_release_cannon" />
        <action type="act_ready_continue_cannon_cla" animation="2_cla_ready_continue_cannon" />
        <action type="act_reload_cannon_cla" animation="2_cla_reload_cannon" />
        <action type="act_reload_cannon_continue_cla" animation="2_cla_reload_cannon_continue" />
        <action type="act_reload_cannon_horseback_cla" animation="2_cla_reload_cannon_horseback" />
        <action type="act_reload_cannon_continue_horseback_cla" animation="2_cla_reload_cannon_continue_horseback" />
        <action type="act_ready_pistol_cla" animation="3_cla_ready_pistol" />
        <action type="act_release_pistol_cla" animation="3_cla_release_pistol" />
        <action type="act_ready_continue_pistol_cla" animation="3_cla_ready_continue_pistol" />
        <action type="act_reload_musket_fast_cla" animation="1b_cla_reload_musket_fast" />
        <action type="act_reload_musket_continue_fast_cla" animation="1b_cla_reload_musket_continue_fast" />
        <action type="act_release_revolver_cla" animation="7_cla_release_revolver" />
        <action type="act_release_rifle_cla" animation="5_cla_release_rifle" />
        <action type="act_release_bolt_rifle_cla" animation="6_cla_release_bolt_rifle" />
        <action type="act_reload_rifle_cla" animation="5_cla_reload_rifle" />
        <action type="act_reload_bolt_rifle_continue_cla" animation="6_cla_reload_bolt_rifle_continue" />
        <action type="cla_act_reload_bomb" animation="cla_reload_bomb" />
        <action type="cla_act_cla_spear_idle_1" animation="cla_spear_idle_1" />
        </action_set></action_sets>
        """;

    private const string ItemsSchemaFixture = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Items"><xs:complexType><xs:choice minOccurs="0" maxOccurs="unbounded">
            <xs:element name="Item"><xs:complexType><xs:sequence>
              <xs:element name="ItemComponent" minOccurs="0" maxOccurs="1"><xs:complexType><xs:choice>
                <xs:element name="Weapon" minOccurs="0" maxOccurs="1"><xs:complexType><xs:anyAttribute processContents="skip" /></xs:complexType></xs:element>
              </xs:choice></xs:complexType></xs:element>
            </xs:sequence><xs:attribute name="id" use="required" /></xs:complexType></xs:element>
          </xs:choice></xs:complexType></xs:element>
        </xs:schema>
        """;

    private const string IdentityXsltFixture = """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:template match="@*|node()"><xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy></xsl:template>
        </xsl:stylesheet>
        """;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertThrowsInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
