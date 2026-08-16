using System.Security.Cryptography;
using System.Text;
using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;

internal static class BridgePopulationSettingsRegression
{
    internal static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bcs-bridge-population-" + Guid.NewGuid().ToString("N"));
        var modulesRoot = Path.Combine(root, "engine", "Modules");
        Directory.CreateDirectory(modulesRoot);

        try
        {
            var battleSceneCatalog = BattleSceneCatalogContractRegistry.CreateEurope1700();
            var configuration = Encoding.UTF8.GetBytes(
                "BCS-COOP-BRIDGE|3\n" +
                "RUNTIME_FEATURE|" +
                Encode("ClientDeterministicBattleSceneProjection") + "\n" +
                "BATTLE_SCENE_CATALOG_CONTRACT|" +
                Encode(battleSceneCatalog.TargetModuleId) + "|" +
                Encode(battleSceneCatalog.TargetVersion) + "|" +
                Encode(battleSceneCatalog.BaseModuleId) + "|" +
                Encode(battleSceneCatalog.BaseVersion) + "|" +
                Encode(battleSceneCatalog.RelativePath) + "|" +
                battleSceneCatalog.Sha256 + "|WARN_ONLY\n" +
                "MODULE|Q29vcA==|djEuMC4w||\n" +
                "MODULE|RXVyb3BlMTcwMA==|djEuNC43LjE=||\n" +
                "MODULE|U2FuZEJveENvcmU=|djEuNC44||\n");
            var assetRoot = Path.Combine(
                Directory.GetCurrentDirectory(),
                "BCSTool",
                "Assets",
                "CoopBridge");
            var serverRuntime = File.ReadAllBytes(
                Path.Combine(assetRoot, "BCS.CoopBridge.Server.dll"));
            var clientRuntime = File.ReadAllBytes(
                Path.Combine(assetRoot, "BCS.CoopBridge.Client.dll"));
            var bridgeId = CoopBridgePackageBuilder.ComputeBridgeId(
                configuration,
                battleSceneCatalog.Content);
            var bridgeRoot = Path.Combine(modulesRoot, bridgeId);
            var serverBin = Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Server");
            var clientBin = Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(serverBin);
            Directory.CreateDirectory(clientBin);
            File.WriteAllBytes(
                Path.Combine(bridgeRoot, "bcs-coop-bridge.config"),
                configuration);
            var battleSceneCatalogPath = Path.Combine(
                bridgeRoot,
                battleSceneCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(battleSceneCatalogPath)!);
            File.WriteAllBytes(battleSceneCatalogPath, battleSceneCatalog.Content);
            File.WriteAllBytes(Path.Combine(serverBin, "BCS.CoopBridge.dll"), serverRuntime);
            File.WriteAllBytes(Path.Combine(clientBin, "BCS.CoopBridge.dll"), clientRuntime);

            var coop = CreateModule("Coop", Path.Combine(modulesRoot, "Coop"), "v1.0.0");
            var sandboxCore = CreateModule(
                "SandBoxCore",
                Path.Combine(modulesRoot, "SandBoxCore"),
                "v1.4.8");
            var overhaulRoot = Path.Combine(modulesRoot, "3231544373");
            var settlementPath = Path.Combine(overhaulRoot, "ModuleData", "settlements.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(settlementPath)!);
            WriteSettlementFixture(settlementPath, townCount: 2, castleCount: 1, villageCount: 3);
            var overhaul = CreateModule(
                "Europe1700",
                overhaulRoot,
                "v1.4.7.1");
            var bridge = CreateModule(
                bridgeId,
                bridgeRoot,
                CoopBridgePackageBuilder.BridgeVersion,
                ["Coop", "Europe1700"]);
            var modules = new[] { coop, sandboxCore, overhaul, bridge };
            var service = new BridgePopulationSettingsService(root);

            var defaults = service.Load();
            Assert(defaults.MaximumAutomaticCaravans is null,
                "Missing population settings did not preserve native caravans.");
            Assert(
                defaults.AutomaticNpcCaravansPerTown ==
                BridgePopulationSettings.DefaultAutomaticNpcCaravansPerTown,
                "Missing population settings did not apply the two-caravans-per-town bridge default.");
            Assert(Approximately(defaults.CaravanCapacityMultiplier, 1d) &&
                   Approximately(defaults.CaravanTradeBudgetMultiplier, 1d) &&
                   Approximately(defaults.CaravanDestinationAgeMaxBonus, 0d) &&
                   defaults.CaravanDestinationAgeHorizonDays == 30,
                "Missing population settings did not preserve neutral caravan economy defaults.");
            Assert(defaults.MaximumActiveVillagerParties is null,
                "Missing population settings did not preserve native villagers.");
            Assert(Approximately(defaults.VillagerPartyCapacityMultiplier, 1d) &&
                   !defaults.VirtualVillagerShipmentsEnabled &&
                   Approximately(defaults.VirtualVillagerCargoMultiplier, 1d) &&
                   defaults.VirtualVillagerCooldownDays == 7 &&
                   Approximately(defaults.VirtualVillagerTravelTimeMultiplier, 1d),
                "Missing population settings did not preserve neutral villager economy defaults.");
            Assert(Approximately(defaults.BanditPartiesAroundHideoutMultiplier, 1d),
                "Missing population settings did not preserve native bandit density.");
            Assert(HasDefaultRegionalSettings(defaults),
                "Missing population settings did not default every regional family off at radius 0.5.");

            var target = service.ValidateSelectedBridge(bridge, modules);
            Assert(target.BridgeModuleId == bridgeId && target.OverhaulModuleId == "Europe1700",
                "Catalog-bearing active bridge was not recognized for population settings.");
            var overhaulTarget = service.ValidateSelectedBridge(overhaul, modules);
            Assert(overhaulTarget.BridgeModuleId == bridgeId &&
                   overhaulTarget.OverhaulModuleId == overhaul.Id,
                "Selecting the bridge-managed overhaul did not resolve its active generated bridge.");
            VerifyOverhaulRowOpensSettings(
                root,
                modulesRoot,
                modules,
                overhaul,
                service,
                bridgeId);
            Assert(target.PopulationGuide is
                {
                    TotalSettlements: 7,
                    TownCount: 2,
                    CastleCount: 1,
                    VillageCount: 3,
                    CalculatedAutomaticCaravanBaseline: 4,
                    CalculatedActiveVillagerPartyCeiling: 3
                },
                "The population guide did not calculate native-scale limits from settlement data.");
            var nativeSnapshot = service.CreateSnapshot(defaults);
            defaults.AutomaticCaravanLimit =
                target.PopulationGuide!.CalculatedAutomaticCaravanBaseline;
            defaults.ActiveVillagerPartyLimit =
                target.PopulationGuide.CalculatedActiveVillagerPartyCeiling;
            Assert(defaults.MaximumAutomaticCaravans is null &&
                   defaults.MaximumActiveVillagerParties is null &&
                   service.CreateSnapshot(defaults).Equals(nativeSnapshot, StringComparison.Ordinal),
                "Displaying guide values changed native population settings or persisted identity.");

            WriteSettlementFixture(settlementPath, townCount: 3, castleCount: 1, villageCount: 4);
            var updatedGuide = service.ValidateSelectedBridge(bridge, modules).PopulationGuide;
            Assert(updatedGuide is
                {
                    TotalSettlements: 9,
                    CalculatedAutomaticCaravanBaseline: 6,
                    CalculatedActiveVillagerPartyCeiling: 4
                },
                "The population guide did not update when the overhaul settlement data changed.");

            File.WriteAllText(
                settlementPath,
                "<!DOCTYPE Settlements [<!ENTITY xxe SYSTEM 'file:///C:/Windows/win.ini'>]>" +
                "<Settlements><Settlement id='town_1'>&xxe;</Settlement></Settlements>");
            var unsafeGuideTarget = service.ValidateSelectedBridge(bridge, modules);
            Assert(unsafeGuideTarget.PopulationGuide is null &&
                   unsafeGuideTarget.PopulationGuideMessage.Contains(
                       "could not be calculated",
                       StringComparison.OrdinalIgnoreCase),
                "Unsafe settlement XML did not make the advisory guide unavailable.");
            WriteSettlementFixture(settlementPath, townCount: 2, castleCount: 1, villageCount: 3);

            File.WriteAllText(
                service.SettingsPath,
                "BCS-BRIDGE-POPULATION|1\n" +
                "MAXIMUM_AUTOMATIC_CARAVANS|240\n" +
                "MAXIMUM_ACTIVE_VILLAGER_PARTIES|500\n" +
                "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|0.5\n");
            var migratedV1 = service.Load();
            Assert(migratedV1.MaximumAutomaticCaravans == 240 &&
                   migratedV1.AutomaticNpcCaravansPerTown == 2 &&
                   migratedV1.MaximumActiveVillagerParties == 500 &&
                   Approximately(migratedV1.BanditPartiesAroundHideoutMultiplier, 0.5) &&
                   HasNeutralEconomySettings(migratedV1) &&
                   HasDefaultRegionalSettings(migratedV1),
                "Schema v1 did not preserve its values while migrating every regional family off.");
            var migratedSnapshot = service.CreateSnapshot(migratedV1);
            Assert(migratedSnapshot.StartsWith(
                       "BCS-BRIDGE-POPULATION|3\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "MAXIMUM_AUTOMATIC_CARAVANS|240\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "AUTOMATIC_NPC_CARAVANS_PER_TOWN|2\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "BCS-BRIDGE-ECONOMY|1\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "CARAVAN_CAPACITY_MULTIPLIER|1\n",
                       StringComparison.Ordinal) &&
                    migratedSnapshot.Contains(
                        "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|FALSE\n",
                        StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS|0.5\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|FALSE\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "REGIONAL_VILLAGER_TRADE_ENABLED|FALSE\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "REGIONAL_SETTLEMENT_PATROL_SPAWNS_ENABLED|FALSE\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED|FALSE\n",
                        StringComparison.Ordinal),
                "Schema v1 did not migrate to population v3 plus neutral economy-sidecar values.");

            File.WriteAllText(
                service.SettingsPath,
                "BCS-BRIDGE-POPULATION|2\n" +
                "MAXIMUM_AUTOMATIC_CARAVANS|236\n" +
                "AUTOMATIC_NPC_CARAVANS_PER_TOWN|1\n" +
                "MAXIMUM_ACTIVE_VILLAGER_PARTIES|400\n" +
                "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|0.75\n");
            var migratedV2 = service.Load();
            Assert(migratedV2.MaximumAutomaticCaravans == 236 &&
                   migratedV2.AutomaticNpcCaravansPerTown == 1 &&
                   migratedV2.MaximumActiveVillagerParties == 400 &&
                   Approximately(migratedV2.BanditPartiesAroundHideoutMultiplier, 0.75) &&
                   HasNeutralEconomySettings(migratedV2) &&
                   HasDefaultRegionalSettings(migratedV2),
                "Schema v2 did not preserve its values while migrating every regional family off.");

            File.WriteAllText(
                service.SettingsPath,
                "BCS-BRIDGE-POPULATION|1\n" +
                "MAXIMUM_AUTOMATIC_CARAVANS|240\n" +
                "MAXIMUM_ACTIVE_VILLAGER_PARTIES|500\n" +
                "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|0.5\n" +
                "UNEXPECTED|1\n");
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Schema v1 accepted an extra setting instead of remaining strict.");

            var configured = new BridgePopulationSettings
            {
                MaximumAutomaticCaravans = 240,
                AutomaticNpcCaravansPerTown = 1,
                CaravanCapacityMultiplier = 2.0,
                CaravanTradeBudgetMultiplier = 1.75,
                CaravanDestinationAgeMaxBonus = 1.25,
                CaravanDestinationAgeHorizonDays = 45,
                MaximumActiveVillagerParties = 500,
                VillagerPartyCapacityMultiplier = 1.5,
                VirtualVillagerShipmentsEnabled = true,
                VirtualVillagerCargoMultiplier = 0.75,
                VirtualVillagerCooldownDays = 9,
                VirtualVillagerTravelTimeMultiplier = 1.25,
                BanditPartiesAroundHideoutMultiplier = 0.5,
                PlayerActiveSpawnRadiusBanditTravelDays = 2.5,
                RegionalAmbientOutlawSpawnsEnabled = true,
                RegionalVillagerTradeEnabled = true,
                RegionalSettlementPatrolSpawnsEnabled = true,
                RegionalBattleDeserterSpawnsEnabled = true
            };
            service.Save(configured);
            var savedPopulationBytes = File.ReadAllBytes(service.SettingsPath);
            var savedEconomyBytes = File.ReadAllBytes(service.EconomySettingsPath);
            var savedPopulationText = Encoding.UTF8.GetString(savedPopulationBytes);
            var savedEconomyText = Encoding.UTF8.GetString(savedEconomyBytes);
            AssertPopulationV3File(
                savedPopulationText,
                maximumCaravans: "240",
                caravansPerTown: "1",
                maximumVillagers: "500",
                banditMultiplier: "0.5",
                regionalRadius: "2.5",
                regionalOutlaws: "TRUE",
                regionalVillagers: "TRUE",
                regionalPatrols: "TRUE",
                regionalDeserters: "TRUE");
            Assert(!savedPopulationText.Contains("CARAVAN_CAPACITY_MULTIPLIER", StringComparison.Ordinal) &&
                   !savedPopulationText.Contains("VIRTUAL_VILLAGER", StringComparison.Ordinal),
                "Economy-sidecar settings leaked into population schema v3.");
            Assert(savedEconomyText.Equals(CreateEconomyV1Fixture(), StringComparison.Ordinal),
                "Economy settings did not serialize to the exact strict sidecar schema.");
            var loaded = service.Load();
            Assert(loaded.MaximumAutomaticCaravans == 240 &&
                   loaded.AutomaticNpcCaravansPerTown == 1 &&
                   Approximately(loaded.CaravanCapacityMultiplier, 2.0) &&
                   Approximately(loaded.CaravanTradeBudgetMultiplier, 1.75) &&
                   Approximately(loaded.CaravanDestinationAgeMaxBonus, 1.25) &&
                   loaded.CaravanDestinationAgeHorizonDays == 45 &&
                   loaded.MaximumActiveVillagerParties == 500 &&
                   Approximately(loaded.VillagerPartyCapacityMultiplier, 1.5) &&
                   loaded.VirtualVillagerShipmentsEnabled &&
                   Approximately(loaded.VirtualVillagerCargoMultiplier, 0.75) &&
                   loaded.VirtualVillagerCooldownDays == 9 &&
                   Approximately(loaded.VirtualVillagerTravelTimeMultiplier, 1.25) &&
                   Approximately(loaded.BanditPartiesAroundHideoutMultiplier, 0.5) &&
                   Approximately(loaded.PlayerActiveSpawnRadiusBanditTravelDays, 2.5) &&
                   loaded.RegionalAmbientOutlawSpawnsEnabled &&
                   loaded.RegionalVillagerTradeEnabled &&
                   loaded.RegionalSettlementPatrolSpawnsEnabled &&
                   loaded.RegionalBattleDeserterSpawnsEnabled,
                "Population settings did not round-trip exactly.");
            VerifyTransactionalSettingsWrites(
                root,
                service,
                configured,
                savedPopulationBytes,
                savedEconomyBytes);
            VerifyIndependentRegionalSwitches(service);
            Assert(service.ValidateSelectedBridge(bridge, modules).BridgeModuleId == bridgeId,
                "Server-only population settings changed bridge validity or identity.");

            configured.AutomaticNpcCaravansPerTown = -1;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A negative per-town caravan limit was accepted.");
            configured.AutomaticNpcCaravansPerTown = 11;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A per-town caravan limit above 10 was accepted.");
            configured.AutomaticNpcCaravansPerTown = 1;

            configured.CaravanCapacityMultiplier = 0.09;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A caravan capacity multiplier below 0.1 was accepted.");
            configured.CaravanCapacityMultiplier = 10.01;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A caravan capacity multiplier above 10 was accepted.");
            configured.CaravanCapacityMultiplier = 2.0;

            configured.CaravanTradeBudgetMultiplier = double.NaN;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A non-finite caravan trade budget multiplier was accepted.");
            configured.CaravanTradeBudgetMultiplier = 1.75;

            configured.CaravanDestinationAgeMaxBonus = -0.01;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A negative caravan destination-age bonus was accepted.");
            configured.CaravanDestinationAgeMaxBonus = 4.01;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A caravan destination-age bonus above 4 was accepted.");
            configured.CaravanDestinationAgeMaxBonus = 1.25;

            configured.CaravanDestinationAgeHorizonDays = 0;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A zero-day caravan destination-age horizon was accepted.");
            configured.CaravanDestinationAgeHorizonDays = 3651;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A caravan destination-age horizon above 3650 days was accepted.");
            configured.CaravanDestinationAgeHorizonDays = 45;

            configured.VillagerPartyCapacityMultiplier = 0.0;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A villager party capacity multiplier below 0.1 was accepted.");
            configured.VillagerPartyCapacityMultiplier = 1.5;

            configured.VirtualVillagerCargoMultiplier = double.PositiveInfinity;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A non-finite virtual villager cargo multiplier was accepted.");
            configured.VirtualVillagerCargoMultiplier = 0.75;

            configured.VirtualVillagerCooldownDays = 0;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A zero-day virtual villager cooldown was accepted.");
            configured.VirtualVillagerCooldownDays = 3651;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A virtual villager cooldown above 3650 days was accepted.");
            configured.VirtualVillagerCooldownDays = 9;

            configured.VirtualVillagerTravelTimeMultiplier = 10.01;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A virtual villager travel-time multiplier above 10 was accepted.");
            configured.VirtualVillagerTravelTimeMultiplier = 1.25;

            configured.PlayerActiveSpawnRadiusBanditTravelDays = double.NaN;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A non-finite player-active spawn radius was accepted.");
            configured.PlayerActiveSpawnRadiusBanditTravelDays = 0.09;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A player-active spawn radius below 0.1 was accepted.");
            configured.PlayerActiveSpawnRadiusBanditTravelDays = 30.01;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "A player-active spawn radius above 30 was accepted.");
            configured.PlayerActiveSpawnRadiusBanditTravelDays = 2.5;

            configured.VirtualVillagerShipmentsEnabled = false;
            AssertThrows<InvalidDataException>(
                () => service.Save(configured),
                "Regional villager trade was accepted without virtual shipments.");
            configured.VirtualVillagerShipmentsEnabled = true;

            var reset = new BridgePopulationSettings
            {
                MaximumAutomaticCaravans = 1,
                AutomaticNpcCaravansPerTown = 1,
                CaravanCapacityMultiplier = 2,
                CaravanTradeBudgetMultiplier = 2,
                CaravanDestinationAgeMaxBonus = 1,
                CaravanDestinationAgeHorizonDays = 60,
                MaximumActiveVillagerParties = 1,
                VillagerPartyCapacityMultiplier = 2,
                VirtualVillagerShipmentsEnabled = true,
                VirtualVillagerCargoMultiplier = 2,
                VirtualVillagerCooldownDays = 14,
                VirtualVillagerTravelTimeMultiplier = 2,
                BanditPartiesAroundHideoutMultiplier = 2,
                PlayerActiveSpawnRadiusBanditTravelDays = 4,
                RegionalAmbientOutlawSpawnsEnabled = true,
                RegionalVillagerTradeEnabled = true,
                RegionalSettlementPatrolSpawnsEnabled = true,
                RegionalBattleDeserterSpawnsEnabled = true
            };
            reset.ResetToDefaults();
            Assert(reset.MaximumAutomaticCaravans is null &&
                   reset.AutomaticNpcCaravansPerTown == 2 &&
                   reset.MaximumActiveVillagerParties is null &&
                   HasNeutralEconomySettings(reset) &&
                   Approximately(reset.BanditPartiesAroundHideoutMultiplier, 1d) &&
                   HasDefaultRegionalSettings(reset),
                "Restore Defaults did not restore every schema-v3 value.");

            var duplicateBridge = CreateModule(
                CoopBridgePackageBuilder.BridgeIdPrefix + new string('a', 24),
                Path.Combine(modulesRoot, "duplicate"),
                CoopBridgePackageBuilder.BridgeVersion,
                ["Coop", "Europe1700"]);
            AssertThrows<InvalidDataException>(
                () => service.ValidateSelectedBridge(
                    bridge,
                    modules.Concat([duplicateBridge]).ToArray()),
                "Multiple enabled generated bridges were accepted by the editor.");
            AssertThrows<InvalidDataException>(
                () => service.ValidateSelectedBridge(
                    overhaul,
                    modules.Concat([duplicateBridge]).ToArray()),
                "The overhaul row accepted multiple enabled generated bridges.");

            bridge.Enabled = false;
            AssertThrows<InvalidDataException>(
                () => service.ValidateSelectedBridge(bridge, modules),
                "A disabled generated bridge was accepted by the editor.");
            bridge.Enabled = true;

            File.AppendAllText(Path.Combine(serverBin, "BCS.CoopBridge.dll"), "tamper");
            AssertThrows<InvalidDataException>(
                () => service.ValidateSelectedBridge(bridge, modules),
                "A bridge with a changed runtime was accepted by the editor.");
            File.WriteAllBytes(Path.Combine(serverBin, "BCS.CoopBridge.dll"), serverRuntime);

            File.WriteAllBytes(battleSceneCatalogPath, [1]);
            AssertThrows<InvalidDataException>(
                () => service.ValidateSelectedBridge(overhaul, modules),
                "A bridge with a changed battle-scene catalog was accepted by the editor.");
            File.WriteAllBytes(battleSceneCatalogPath, battleSceneCatalog.Content);

            var displacedCatalogPath = battleSceneCatalogPath + ".missing";
            File.Move(battleSceneCatalogPath, displacedCatalogPath);
            try
            {
                AssertThrows<FileNotFoundException>(
                    () => service.ValidateSelectedBridge(overhaul, modules),
                    "A bridge with a missing battle-scene catalog was accepted by the editor.");
            }
            finally
            {
                File.Move(displacedCatalogPath, battleSceneCatalogPath);
            }

            var validEconomy = CreateEconomyV1Fixture();
            File.WriteAllText(
                service.EconomySettingsPath,
                validEconomy.Replace(
                    "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|TRUE",
                    "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|true",
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Economy schema v1 accepted a non-canonical boolean token.");

            File.WriteAllText(
                service.EconomySettingsPath,
                validEconomy.Replace(
                    "CARAVAN_CAPACITY_MULTIPLIER|2",
                    "CARAVAN_CAPACITY_MULTIPLIER|NaN",
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Economy schema v1 accepted a non-finite multiplier.");

            File.WriteAllText(
                service.EconomySettingsPath,
                validEconomy.Replace(
                    "VIRTUAL_VILLAGER_COOLDOWN_DAYS|9\n",
                    string.Empty,
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Economy schema v1 accepted a missing required setting.");

            File.WriteAllText(service.EconomySettingsPath, validEconomy + "UNEXPECTED|1\n");
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Economy schema v1 accepted an extra setting.");

            File.WriteAllBytes(service.EconomySettingsPath, new byte[64 * 1024 + 1]);
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Oversized economy settings were accepted.");
            File.WriteAllBytes(service.EconomySettingsPath, savedEconomyBytes);

            File.WriteAllText(
                service.SettingsPath,
                savedPopulationText.Replace(
                    "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|TRUE",
                    "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|true",
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Population schema v3 accepted a non-canonical regional boolean token.");

            File.WriteAllText(
                service.SettingsPath,
                savedPopulationText.Replace(
                    "PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS|2.5",
                    "PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS|NaN",
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Population schema v3 accepted a non-finite regional radius.");

            File.WriteAllText(
                service.SettingsPath,
                savedPopulationText.Replace(
                    "REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED|TRUE\n",
                    string.Empty,
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Population schema v3 accepted a missing regional setting.");

            File.WriteAllText(service.SettingsPath, savedPopulationText + "UNEXPECTED|1\n");
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Population schema v3 accepted an extra setting.");

            File.WriteAllBytes(service.SettingsPath, savedPopulationBytes);
            File.WriteAllText(
                service.EconomySettingsPath,
                validEconomy.Replace(
                    "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|TRUE",
                    "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|FALSE",
                    StringComparison.Ordinal));
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Population schema v3 loaded regional villager trade without virtual shipments.");
            File.WriteAllBytes(service.EconomySettingsPath, savedEconomyBytes);

            File.WriteAllText(
                service.SettingsPath,
                "BCS-BRIDGE-POPULATION|2\n" +
                "MAXIMUM_AUTOMATIC_CARAVANS|NATIVE\n" +
                "AUTOMATIC_NPC_CARAVANS_PER_TOWN|2\n" +
                "MAXIMUM_ACTIVE_VILLAGER_PARTIES|NATIVE\n" +
                "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|NaN\n");
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Non-finite population settings were accepted.");

            File.WriteAllBytes(service.SettingsPath, new byte[64 * 1024 + 1]);
            AssertThrows<InvalidDataException>(
                () => service.Load(),
                "Oversized population settings were accepted.");

            File.WriteAllBytes(service.SettingsPath, savedPopulationBytes);
            File.WriteAllBytes(service.EconomySettingsPath, savedEconomyBytes);
            var restored = service.Load();
            Assert(restored.MaximumAutomaticCaravans == 240 &&
                   Approximately(restored.CaravanCapacityMultiplier, 2d) &&
                   restored.VirtualVillagerShipmentsEnabled &&
                   Approximately(restored.PlayerActiveSpawnRadiusBanditTravelDays, 2.5) &&
                   restored.RegionalAmbientOutlawSpawnsEnabled &&
                   restored.RegionalVillagerTradeEnabled &&
                   restored.RegionalSettlementPatrolSpawnsEnabled &&
                   restored.RegionalBattleDeserterSpawnsEnabled,
                "Valid population and economy settings could not be restored after rejected inputs.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        VerifyMultiDependencyBridgeExposesServerWideSettings();
    }

    private static void VerifyMultiDependencyBridgeExposesServerWideSettings()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bcs-bridge-population-multi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var modulesRoot = Path.Combine(root, "engine", "Modules");
            Directory.CreateDirectory(modulesRoot);
            var executable = Path.Combine(root, "BannerlordCoopServer.exe");
            File.WriteAllBytes(executable, [1]);
            var configuration = Encoding.UTF8.GetBytes(
                "BCS-COOP-BRIDGE|2\n" +
                "MODULE|" + Encode("Coop") + "|" + Encode("v1.0.0") + "||\n" +
                "MODULE|" + Encode("GenericRoot") + "|" + Encode("v2.0.0") + "||\n" +
                "MODULE|" + Encode("Bannerlord.Harmony") + "|" + Encode("v2.3.3") + "||\n");
            var assetRoot = Path.Combine(
                Directory.GetCurrentDirectory(),
                "BCSTool",
                "Assets",
                "CoopBridge");
            var serverRuntime = File.ReadAllBytes(
                Path.Combine(assetRoot, "BCS.CoopBridge.Server.dll"));
            var clientRuntime = File.ReadAllBytes(
                Path.Combine(assetRoot, "BCS.CoopBridge.Client.dll"));
            var bridgeId = CoopBridgePackageBuilder.ComputeBridgeId(configuration);
            var bridgeRoot = Path.Combine(modulesRoot, bridgeId);
            var serverBin = Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Server");
            var clientBin = Path.Combine(bridgeRoot, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(serverBin);
            Directory.CreateDirectory(clientBin);
            File.WriteAllBytes(
                Path.Combine(bridgeRoot, "bcs-coop-bridge.config"),
                configuration);
            File.WriteAllBytes(Path.Combine(serverBin, "BCS.CoopBridge.dll"), serverRuntime);
            File.WriteAllBytes(Path.Combine(clientBin, "BCS.CoopBridge.dll"), clientRuntime);

            var coop = CreateModule("Coop", Path.Combine(modulesRoot, "Coop"), "v1.0.0");
            var rootModule = CreateModule(
                "GenericRoot",
                Path.Combine(modulesRoot, "GenericRoot"),
                "v2.0.0");
            var framework = CreateModule(
                "Bannerlord.Harmony",
                Path.Combine(modulesRoot, "Bannerlord.Harmony"),
                "v2.3.3");
            var bridge = CreateModule(
                bridgeId,
                bridgeRoot,
                CoopBridgePackageBuilder.BridgeVersion,
                ["Coop", "GenericRoot", "Bannerlord.Harmony"]);
            var modules = new[] { coop, rootModule, framework, bridge };
            var service = new BridgePopulationSettingsService(root);
            var target = service.ValidateSelectedBridge(bridge, modules);
            Assert(target.BridgeModuleId == bridgeId &&
                   target.OverhaulModuleId.Length == 0 &&
                   target.PopulationGuide is null &&
                   target.PopulationGuideMessage.Contains(
                       "multiple campaign-module dependencies",
                       StringComparison.Ordinal),
                "Multi-dependency bridge did not expose server-wide settings without inventing a guide target.");

            var scanner = new ModuleScanner();
            var manager = new ModuleManager(executable, scanner);
            var installationService = new BridgeInstallationService(
                scanner,
                new CoopCompatibilityPatcher());
            var viewModel = new ModManagerViewModel(
                manager,
                new ModuleImporter(modulesRoot, scanner),
                new ModuleRemovalService(manager, new RejectingRecycler(), installationService),
                new DependencyValidator(),
                new CoopCompatibilityAnalyzer(),
                installationService,
                service);
            foreach (var module in modules)
                viewModel.Modules.Add(module);
            viewModel.SelectedModule = bridge;
            BridgePopulationSettingsTarget? requested = null;
            viewModel.BridgePopulationSettingsRequested += value => requested = value;
            Assert(viewModel.OpenBridgePopulationSettingsCommand.CanExecute(null),
                "Multi-dependency generated bridge row kept Population & Trade Settings disabled.");
            viewModel.OpenBridgePopulationSettingsCommand.Execute(null);
            Assert(requested?.BridgeModuleId == bridgeId,
                "Multi-dependency generated bridge row did not open server-wide settings.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static BannerlordModule CreateModule(
        string id,
        string path,
        string version,
        IReadOnlyList<string>? dependencies = null)
    {
        var module = new BannerlordModule
        {
            Name = id,
            Id = id,
            Version = version,
            Path = path,
            IsInstalled = true,
            IsRequired = false,
            IsServerCompatible = true,
            Dependencies = dependencies ?? Array.Empty<string>(),
            MustLoadAfter = Array.Empty<string>(),
            MustLoadBefore = Array.Empty<string>(),
            IncompatibleModules = Array.Empty<string>()
        };
        module.Enabled = true;
        return module;
    }

    private static void VerifyOverhaulRowOpensSettings(
        string serverRoot,
        string modulesRoot,
        IReadOnlyList<BannerlordModule> modules,
        BannerlordModule overhaul,
        BridgePopulationSettingsService settingsService,
        string expectedBridgeId)
    {
        var executable = Path.Combine(serverRoot, "BannerlordCoopServer.exe");
        File.WriteAllBytes(executable, [1]);
        var scanner = new ModuleScanner();
        var manager = new ModuleManager(executable, scanner);
        var installationService = new BridgeInstallationService(
            scanner,
            new CoopCompatibilityPatcher());
        var viewModel = new ModManagerViewModel(
            manager,
            new ModuleImporter(modulesRoot, scanner),
            new ModuleRemovalService(manager, new RejectingRecycler(), installationService),
            new DependencyValidator(),
            new CoopCompatibilityAnalyzer(),
            installationService,
            settingsService);
        foreach (var module in modules)
            viewModel.Modules.Add(module);

        viewModel.SelectedModule = overhaul;
        Assert(viewModel.OpenBridgePopulationSettingsCommand.CanExecute(null),
            "The Population & Trade Settings command stayed disabled on the overhaul row.");
        BridgePopulationSettingsTarget? requested = null;
        var requestCount = 0;
        viewModel.BridgePopulationSettingsRequested += target =>
        {
            requested = target;
            requestCount++;
        };
        viewModel.OpenBridgePopulationSettingsCommand.Execute(null);
        Assert(requested?.BridgeModuleId == expectedBridgeId &&
               requested.OverhaulModuleId == overhaul.Id &&
               requestCount == 1,
            "The overhaul-row command did not open settings for its generated bridge.");

        viewModel.SelectedModule = modules.Single(module =>
            module.Id.Equals(expectedBridgeId, StringComparison.Ordinal));
        Assert(viewModel.OpenBridgePopulationSettingsCommand.CanExecute(null),
            "The enabled current generated bridge row did not expose server-wide settings.");
        viewModel.OpenBridgePopulationSettingsCommand.Execute(null);
        Assert(requested?.BridgeModuleId == expectedBridgeId &&
               requested.OverhaulModuleId == overhaul.Id &&
               requestCount == 2,
            "The generated bridge row did not open the same server-wide settings target.");

        var duplicateBridge = CreateModule(
            CoopBridgePackageBuilder.BridgeIdPrefix + new string('a', 24),
            Path.Combine(modulesRoot, "duplicate"),
            CoopBridgePackageBuilder.BridgeVersion,
            ["Coop", overhaul.Id]);
        viewModel.Modules.Add(duplicateBridge);
        viewModel.SelectedModule = overhaul;
        Assert(!viewModel.OpenBridgePopulationSettingsCommand.CanExecute(null) &&
               requested?.BridgeModuleId == expectedBridgeId &&
               requestCount == 2,
            "Ambiguous enabled bridges did not disable the overhaul-row settings command.");
    }

    private static void WriteSettlementFixture(
        string path,
        int townCount,
        int castleCount,
        int villageCount)
    {
        var builder = new StringBuilder("<Settlements>");
        for (var index = 0; index < townCount; index++)
        {
            builder.Append("<Settlement id='town_")
                .Append(index)
                .Append("'><Components><Town is_castle='false'/></Components></Settlement>");
        }
        for (var index = 0; index < castleCount; index++)
        {
            builder.Append("<Settlement id='castle_")
                .Append(index)
                .Append("'><Components><Town is_castle='true'/></Components></Settlement>");
        }
        for (var index = 0; index < villageCount; index++)
        {
            builder.Append("<Settlement id='village_")
                .Append(index)
                .Append("'><Components><Village/></Components></Settlement>");
        }
        builder.Append("<Settlement id='hideout_0'><Components><Hideout/></Components></Settlement>")
            .Append("</Settlements>");
        File.WriteAllText(path, builder.ToString());
    }

    private static string CreateEconomyV1Fixture() =>
        "BCS-BRIDGE-ECONOMY|1\n" +
        "CARAVAN_CAPACITY_MULTIPLIER|2\n" +
        "CARAVAN_TRADE_BUDGET_MULTIPLIER|1.75\n" +
        "CARAVAN_DESTINATION_AGE_MAX_BONUS|1.25\n" +
        "CARAVAN_DESTINATION_AGE_HORIZON_DAYS|45\n" +
        "VILLAGER_PARTY_CAPACITY_MULTIPLIER|1.5\n" +
        "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|TRUE\n" +
        "VIRTUAL_VILLAGER_CARGO_MULTIPLIER|0.75\n" +
        "VIRTUAL_VILLAGER_COOLDOWN_DAYS|9\n" +
        "VIRTUAL_VILLAGER_TRAVEL_TIME_MULTIPLIER|1.25\n";

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static void AssertPopulationV3File(
        string text,
        string maximumCaravans,
        string caravansPerTown,
        string maximumVillagers,
        string banditMultiplier,
        string regionalRadius,
        string regionalOutlaws,
        string regionalVillagers,
        string regionalPatrols,
        string regionalDeserters)
    {
        var expected =
            "BCS-BRIDGE-POPULATION|3\n" +
            "MAXIMUM_AUTOMATIC_CARAVANS|" + maximumCaravans + "\n" +
            "AUTOMATIC_NPC_CARAVANS_PER_TOWN|" + caravansPerTown + "\n" +
            "MAXIMUM_ACTIVE_VILLAGER_PARTIES|" + maximumVillagers + "\n" +
            "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|" + banditMultiplier + "\n" +
            "PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS|" + regionalRadius + "\n" +
            "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|" + regionalOutlaws + "\n" +
            "REGIONAL_VILLAGER_TRADE_ENABLED|" + regionalVillagers + "\n" +
            "REGIONAL_SETTLEMENT_PATROL_SPAWNS_ENABLED|" + regionalPatrols + "\n" +
            "REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED|" + regionalDeserters + "\n";
        Assert(text.Equals(expected, StringComparison.Ordinal),
            "Saved population settings do not match strict schema v3.");
    }

    private static void VerifyIndependentRegionalSwitches(
        BridgePopulationSettingsService service)
    {
        AssertRegionalSwitchSnapshot(
            service,
            new BridgePopulationSettings
            {
                RegionalAmbientOutlawSpawnsEnabled = true
            },
            regionalOutlaws: true,
            regionalVillagers: false,
            regionalPatrols: false,
            regionalDeserters: false);
        AssertRegionalSwitchSnapshot(
            service,
            new BridgePopulationSettings
            {
                VirtualVillagerShipmentsEnabled = true,
                RegionalVillagerTradeEnabled = true
            },
            regionalOutlaws: false,
            regionalVillagers: true,
            regionalPatrols: false,
            regionalDeserters: false);
        AssertRegionalSwitchSnapshot(
            service,
            new BridgePopulationSettings
            {
                RegionalSettlementPatrolSpawnsEnabled = true
            },
            regionalOutlaws: false,
            regionalVillagers: false,
            regionalPatrols: true,
            regionalDeserters: false);
        AssertRegionalSwitchSnapshot(
            service,
            new BridgePopulationSettings
            {
                RegionalBattleDeserterSpawnsEnabled = true
            },
            regionalOutlaws: false,
            regionalVillagers: false,
            regionalPatrols: false,
            regionalDeserters: true);
    }

    private static void VerifyTransactionalSettingsWrites(
        string root,
        BridgePopulationSettingsService service,
        BridgePopulationSettings baseline,
        byte[] baselinePopulationBytes,
        byte[] baselineEconomyBytes)
    {
        var rejected = CreateTransactionVariant(generation: 1);
        using (var economyLock = new FileStream(
                   service.EconomySettingsPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            AssertThrows<IOException>(
                () => service.Save(rejected),
                "A failed second settings commit did not surface an I/O error.");
        }

        Assert(
            File.ReadAllBytes(service.SettingsPath).SequenceEqual(baselinePopulationBytes) &&
            File.ReadAllBytes(service.EconomySettingsPath).SequenceEqual(baselineEconomyBytes),
            "A failed economy commit left a mixed population/economy generation.");
        AssertNoSettingsTransactionResidue(root, service);

        var generationA = CreateTransactionVariant(generation: 2);
        var generationB = CreateTransactionVariant(generation: 3);
        service.Save(generationA);
        var populationA = File.ReadAllBytes(service.SettingsPath);
        var economyA = File.ReadAllBytes(service.EconomySettingsPath);
        service.Save(generationB);
        var populationB = File.ReadAllBytes(service.SettingsPath);
        var economyB = File.ReadAllBytes(service.EconomySettingsPath);
        service.Save(baseline);

        var precommitMutation = new BridgePopulationSettingsService(
            root,
            beforeCommittedCleanup: null,
            beforeFirstCommit: () =>
                File.WriteAllBytes(service.SettingsPath, populationB));
        AssertThrows<AggregateException>(
            () => precommitMutation.Save(generationA),
            "A destination changed after snapshot was overwritten during commit.");
        Assert(File.ReadAllBytes(service.SettingsPath).SequenceEqual(populationB),
            "Precommit validation changed the unexpected destination generation.");
        DeleteSettingsTransactionResidue(root, service);
        File.WriteAllBytes(service.SettingsPath, baselinePopulationBytes);
        File.WriteAllBytes(service.EconomySettingsPath, baselineEconomyBytes);

        File.WriteAllBytes(service.SettingsPath, populationA);
        File.WriteAllText(
            Path.Combine(root, ".bcs-bridge-settings.transaction"),
            CreateSettingsTransactionJournal(
                baselinePopulationBytes,
                populationA,
                baselineEconomyBytes,
                economyA));
        _ = service.Load();
        Assert(
            File.ReadAllBytes(service.SettingsPath).SequenceEqual(baselinePopulationBytes) &&
            File.ReadAllBytes(service.EconomySettingsPath).SequenceEqual(baselineEconomyBytes),
            "Startup recovery did not roll a mixed settings generation back atomically.");
        AssertNoSettingsTransactionResidue(root, service);

        File.WriteAllBytes(service.SettingsPath, populationB);
        File.WriteAllText(
            Path.Combine(root, ".bcs-bridge-settings.transaction"),
            CreateSettingsTransactionJournal(
                baselinePopulationBytes,
                populationA,
                baselineEconomyBytes,
                economyA));
        AssertThrows<InvalidDataException>(
            () => service.Load(),
            "Recovery overwrote a population generation outside the journal's old/new states.");
        Assert(File.ReadAllBytes(service.SettingsPath).SequenceEqual(populationB),
            "Recovery changed externally modified population bytes before failing closed.");
        File.Delete(Path.Combine(root, ".bcs-bridge-settings.transaction"));
        File.WriteAllBytes(service.SettingsPath, baselinePopulationBytes);

        var cleanupFault = new BridgePopulationSettingsService(
            root,
            () => throw new IOException("Injected committed-cleanup failure."));
        var cleanupResult = cleanupFault.Save(generationA);
        Assert(cleanupResult.CleanupPending && cleanupResult.WarningMessage is not null,
            "A post-commit cleanup failure was reported as an ordinary failed save.");
        Assert(
            File.ReadAllBytes(service.SettingsPath).SequenceEqual(populationA) &&
            File.ReadAllBytes(service.EconomySettingsPath).SequenceEqual(economyA),
            "A post-commit cleanup failure changed the already committed settings pair.");
        _ = service.Load();
        Assert(
            File.ReadAllBytes(service.SettingsPath).SequenceEqual(populationA) &&
            File.ReadAllBytes(service.EconomySettingsPath).SequenceEqual(economyA),
            "Deferred transaction cleanup changed the committed settings pair.");
        AssertNoSettingsTransactionResidue(root, service);
        service.Save(baseline);

        var first = new BridgePopulationSettingsService(root);
        var second = new BridgePopulationSettingsService(root);
        Task.WaitAll(
            Task.Run(() => first.Save(generationA)),
            Task.Run(() => second.Save(generationB)));

        var actualPopulation = File.ReadAllBytes(service.SettingsPath);
        var actualEconomy = File.ReadAllBytes(service.EconomySettingsPath);
        var matchesA = actualPopulation.SequenceEqual(populationA) &&
                       actualEconomy.SequenceEqual(economyA);
        var matchesB = actualPopulation.SequenceEqual(populationB) &&
                       actualEconomy.SequenceEqual(economyB);
        Assert(matchesA || matchesB,
            "Concurrent settings saves interleaved population and economy generations.");
        AssertNoSettingsTransactionResidue(root, service);

        service.Save(baseline);
        Assert(
            File.ReadAllBytes(service.SettingsPath).SequenceEqual(baselinePopulationBytes) &&
            File.ReadAllBytes(service.EconomySettingsPath).SequenceEqual(baselineEconomyBytes),
            "Transactional settings regression did not restore its baseline generation.");
    }

    private static BridgePopulationSettings CreateTransactionVariant(int generation) =>
        new()
        {
            MaximumAutomaticCaravans = 100 + generation,
            AutomaticNpcCaravansPerTown = generation,
            CaravanCapacityMultiplier = 1.0 + generation * 0.1,
            CaravanTradeBudgetMultiplier = 1.1 + generation * 0.1,
            CaravanDestinationAgeMaxBonus = generation * 0.25,
            CaravanDestinationAgeHorizonDays = 30 + generation,
            MaximumActiveVillagerParties = 200 + generation,
            VillagerPartyCapacityMultiplier = 1.2 + generation * 0.1,
            VirtualVillagerShipmentsEnabled = true,
            VirtualVillagerCargoMultiplier = 1.3 + generation * 0.1,
            VirtualVillagerCooldownDays = 7 + generation,
            VirtualVillagerTravelTimeMultiplier = 1.4 + generation * 0.1,
            BanditPartiesAroundHideoutMultiplier = 0.5 + generation * 0.1,
            PlayerActiveSpawnRadiusBanditTravelDays = 0.5 + generation * 0.25,
            RegionalAmbientOutlawSpawnsEnabled = generation % 2 == 0,
            RegionalVillagerTradeEnabled = generation % 2 != 0,
            RegionalSettlementPatrolSpawnsEnabled = generation % 2 == 0,
            RegionalBattleDeserterSpawnsEnabled = generation % 2 != 0
        };

    private static string CreateSettingsTransactionJournal(
        byte[] originalPopulation,
        byte[] newPopulation,
        byte[] originalEconomy,
        byte[] newEconomy) =>
        "BCS-BRIDGE-SETTINGS-TRANSACTION|1\n" +
        CreateSettingsTransactionEntry("POPULATION", originalPopulation, newPopulation) + "\n" +
        CreateSettingsTransactionEntry("ECONOMY", originalEconomy, newEconomy) + "\n";

    private static string CreateSettingsTransactionEntry(
        string name,
        byte[] originalBytes,
        byte[] newBytes) =>
        name + "|TRUE|" +
        Convert.ToHexString(SHA256.HashData(originalBytes)) + "|" +
        Convert.ToBase64String(originalBytes) + "|" +
        Convert.ToHexString(SHA256.HashData(newBytes));

    private static void AssertNoSettingsTransactionResidue(
        string root,
        BridgePopulationSettingsService service)
    {
        Assert(GetSettingsTransactionResiduePaths(root, service).All(path => !File.Exists(path)),
            "Bridge settings transaction left journal, staged, or recovery residue.");
    }

    private static void DeleteSettingsTransactionResidue(
        string root,
        BridgePopulationSettingsService service)
    {
        foreach (var path in GetSettingsTransactionResiduePaths(root, service))
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string[] GetSettingsTransactionResiduePaths(
        string root,
        BridgePopulationSettingsService service) =>
        new[]
        {
            Path.Combine(root, ".bcs-bridge-settings.transaction"),
            Path.Combine(root, ".bcs-bridge-settings.transaction.new"),
            service.SettingsPath + ".bcs-txn-new",
            service.EconomySettingsPath + ".bcs-txn-new",
            service.SettingsPath + ".bcs-txn-new.recovery",
            service.EconomySettingsPath + ".bcs-txn-new.recovery"
        };

    private static void AssertRegionalSwitchSnapshot(
        BridgePopulationSettingsService service,
        BridgePopulationSettings settings,
        bool regionalOutlaws,
        bool regionalVillagers,
        bool regionalPatrols,
        bool regionalDeserters)
    {
        var snapshot = service.CreateSnapshot(settings);
        Assert(
            snapshot.Contains(
                "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|" +
                (regionalOutlaws ? "TRUE\n" : "FALSE\n"),
                StringComparison.Ordinal) &&
            snapshot.Contains(
                "REGIONAL_VILLAGER_TRADE_ENABLED|" +
                (regionalVillagers ? "TRUE\n" : "FALSE\n"),
                StringComparison.Ordinal) &&
            snapshot.Contains(
                "REGIONAL_SETTLEMENT_PATROL_SPAWNS_ENABLED|" +
                (regionalPatrols ? "TRUE\n" : "FALSE\n"),
                StringComparison.Ordinal) &&
            snapshot.Contains(
                "REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED|" +
                (regionalDeserters ? "TRUE\n" : "FALSE\n"),
                StringComparison.Ordinal),
            "Regional family switches were not serialized independently.");
    }

    private static bool HasNeutralEconomySettings(BridgePopulationSettings settings) =>
        Approximately(settings.CaravanCapacityMultiplier, 1d) &&
        Approximately(settings.CaravanTradeBudgetMultiplier, 1d) &&
        Approximately(settings.CaravanDestinationAgeMaxBonus, 0d) &&
        settings.CaravanDestinationAgeHorizonDays == 30 &&
        Approximately(settings.VillagerPartyCapacityMultiplier, 1d) &&
        !settings.VirtualVillagerShipmentsEnabled &&
        Approximately(settings.VirtualVillagerCargoMultiplier, 1d) &&
        settings.VirtualVillagerCooldownDays == 7 &&
        Approximately(settings.VirtualVillagerTravelTimeMultiplier, 1d);

    private static bool HasDefaultRegionalSettings(BridgePopulationSettings settings) =>
        Approximately(
            settings.PlayerActiveSpawnRadiusBanditTravelDays,
            BridgePopulationSettings.DefaultPlayerActiveSpawnRadiusBanditTravelDays) &&
        !settings.RegionalAmbientOutlawSpawnsEnabled &&
        !settings.RegionalVillagerTradeEnabled &&
        !settings.RegionalSettlementPatrolSpawnsEnabled &&
        !settings.RegionalBattleDeserterSpawnsEnabled;

    private static bool Approximately(double actual, double expected) =>
        Math.Abs(actual - expected) < 0.000001;

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RejectingRecycler : IModuleDirectoryRecycler
    {
        public void Recycle(string directoryPath) =>
            throw new InvalidOperationException(
                "Recycler must not run in population-settings regression.");
    }
}
