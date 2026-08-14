using System.Security.Cryptography;
using System.Text;
using BCSTool.Models;
using BCSTool.Services;

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
            var configuration = Encoding.UTF8.GetBytes(
                "BCS-COOP-BRIDGE|1\n" +
                "MODULE|Q29vcA==|djEuMC4w|||\n" +
                "MODULE|RXVyb3BlMTcwMA==|djEuNC43LjE=|||\n");
            var assetRoot = Path.Combine(
                Directory.GetCurrentDirectory(),
                "BCSTool",
                "Assets",
                "CoopBridge");
            var serverRuntime = File.ReadAllBytes(
                Path.Combine(assetRoot, "BCS.CoopBridge.Server.dll"));
            var clientRuntime = File.ReadAllBytes(
                Path.Combine(assetRoot, "BCS.CoopBridge.Client.dll"));
            var identityPayload = new byte[
                configuration.Length + serverRuntime.Length + clientRuntime.Length];
            Buffer.BlockCopy(configuration, 0, identityPayload, 0, configuration.Length);
            Buffer.BlockCopy(
                serverRuntime,
                0,
                identityPayload,
                configuration.Length,
                serverRuntime.Length);
            Buffer.BlockCopy(
                clientRuntime,
                0,
                identityPayload,
                configuration.Length + serverRuntime.Length,
                clientRuntime.Length);
            var bridgeId = CoopBridgePackageBuilder.BridgeIdPrefix +
                           Convert.ToHexString(SHA256.HashData(identityPayload))[..24]
                               .ToLowerInvariant();
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
            var modules = new[] { coop, overhaul, bridge };
            var service = new BridgePopulationSettingsService(root);

            var defaults = service.Load();
            Assert(defaults.MaximumAutomaticCaravans is null,
                "Missing population settings did not preserve native caravans.");
            Assert(
                defaults.AutomaticNpcCaravansPerTown ==
                BridgePopulationSettings.DefaultAutomaticNpcCaravansPerTown,
                "Missing population settings did not apply the two-caravans-per-town bridge default.");
            Assert(defaults.MaximumActiveVillagerParties is null,
                "Missing population settings did not preserve native villagers.");
            Assert(Math.Abs(defaults.BanditPartiesAroundHideoutMultiplier - 1d) < 0.000001,
                "Missing population settings did not preserve native bandit density.");

            var target = service.ValidateSelectedBridge(bridge, modules);
            Assert(target.BridgeModuleId == bridgeId && target.OverhaulModuleId == "Europe1700",
                "Current active bridge was not recognized for population settings.");
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
                   Math.Abs(migratedV1.BanditPartiesAroundHideoutMultiplier - 0.5) < 0.000001,
                "Schema v1 did not preserve its global limits while adding the per-town default.");
            var migratedSnapshot = service.CreateSnapshot(migratedV1);
            Assert(migratedSnapshot.StartsWith(
                       "BCS-BRIDGE-POPULATION|2\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "MAXIMUM_AUTOMATIC_CARAVANS|240\n",
                       StringComparison.Ordinal) &&
                   migratedSnapshot.Contains(
                       "AUTOMATIC_NPC_CARAVANS_PER_TOWN|2\n",
                       StringComparison.Ordinal),
                "Schema v1 did not serialize forward to strict schema v2 without losing its global limit.");

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
                MaximumActiveVillagerParties = 500,
                BanditPartiesAroundHideoutMultiplier = 0.5
            };
            service.Save(configured);
            var savedBytes = File.ReadAllBytes(service.SettingsPath);
            var loaded = service.Load();
            Assert(loaded.MaximumAutomaticCaravans == 240 &&
                   loaded.AutomaticNpcCaravansPerTown == 1 &&
                   loaded.MaximumActiveVillagerParties == 500 &&
                   Math.Abs(loaded.BanditPartiesAroundHideoutMultiplier - 0.5) < 0.000001,
                "Population settings did not round-trip exactly.");
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

            File.WriteAllBytes(service.SettingsPath, savedBytes);
            Assert(service.Load().MaximumAutomaticCaravans == 240,
                "A valid settings file could not be restored after rejected inputs.");
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
}
