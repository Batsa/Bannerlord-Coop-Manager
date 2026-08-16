using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static class BridgeSmokeHost
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: BridgeSmokeHost <bridge.dll> <dependency-dir> [...]");
            return 2;
        }

        var dependencyDirectories = new string[args.Length - 1];
        Array.Copy(args, 1, dependencyDirectories, 0, dependencyDirectories.Length);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            var fileName = new AssemblyName(eventArgs.Name).Name + ".dll";
            foreach (var directory in dependencyDirectories)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        try
        {
            var preloadAssemblyPath = Environment.GetEnvironmentVariable(
                "BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY");
            if (!string.IsNullOrWhiteSpace(preloadAssemblyPath))
            {
                foreach (var path in preloadAssemblyPath.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        Assembly.LoadFrom(Path.GetFullPath(path));
                }
            }
            var workingDirectory = Environment.GetEnvironmentVariable(
                "BCS_BRIDGE_SMOKE_WORKING_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                Directory.SetCurrentDirectory(Path.GetFullPath(workingDirectory));
            InitializeActiveModuleFixtureIfRequested();

            PreloadEconomySaveSchemaDependenciesIfRequested();
            var bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            VerifyEconomySaveSchemaIfRequested(bridge);
            if (VerifyInstalledBattleSceneAbiOnlyIfRequested(bridge))
                return 0;
            if (VerifyEurope1700ShieldProductionSuppressionOnlyIfRequested(bridge))
                return 0;
            var runtime = bridge.GetType("BCS.CoopBridge.BridgeRuntime", true);
            var validate = runtime.GetMethod(
                "ValidateInstalledPackage",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (validate == null)
                throw new MissingMethodException(runtime.FullName, "ValidateInstalledPackage");
            validate.Invoke(null, null);
            VerifyRuntimeFeaturesIfRequested(runtime);
            VerifyDisabledRuntimeFeaturesIfRequested(runtime);
            VerifyEconomyControlHooksIfRequested(bridge);
            VerifyRegionalPopulationHooksIfRequested(bridge);
            VerifyBattleSceneRandomScopeIfRequested(bridge);
            VerifyCharacterCreationLifecycleGateIfRequested(bridge);
            VerifyGameVersionCompatibilityIfRequested(args[0]);
            VerifyAuthorityRuleIfPresent(args[0]);
            Console.WriteLine("PASS: bridge runtime accepted the exact generated package.");
            return 0;
        }
        catch (TargetInvocationException exception)
        {
            Console.Error.WriteLine(exception.InnerException ?? exception);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static bool VerifyInstalledBattleSceneAbiOnlyIfRequested(Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_INSTALLED_ABI"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        var coopModuleRoot = Environment.GetEnvironmentVariable(
            "BCS_BRIDGE_SMOKE_COOP_MODULE_ROOT");
        if (string.IsNullOrWhiteSpace(coopModuleRoot))
            throw new InvalidOperationException("Installed Coop module root was not supplied to ABI smoke.");

        var runtime = bridge.GetType("BCS.CoopBridge.BridgeRuntime", true);
        var projection = bridge.GetType(
            "BCS.CoopBridge.ClientDeterministicBattleSceneProjection",
            true);
        SeedValidatedBattleSceneContractForInstalledAbiSmoke(bridge, runtime);

        const string bridgeId = "BCS.CoopBridge.installed-abi-smoke";
        var install = projection.GetMethod(
            "Install",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(string) },
            null);
        if (install == null)
            throw new MissingMethodException(projection.FullName, "Install(string, string)");
        install.Invoke(null, new object[] { Path.GetFullPath(coopModuleRoot), bridgeId });

        var gameInterface = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => string.Equals(
                assembly.GetName().Name,
                "GameInterface",
                StringComparison.Ordinal));
        var initializer = gameInterface.GetType(
            "GameInterface.Services.MapEvents.FieldBattleMissionInitializer",
            true,
            false);
        var target = initializer.GetMethods(
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(method =>
                string.Equals(method.Name, "Create", StringComparison.Ordinal) &&
                method.IsPublic &&
                !method.IsStatic &&
                method.GetParameters().Length == 3);
        VerifyExactHarmonyScope(target, bridgeId +
            ".client-deterministic-battle-scene-projection");
        VerifyActualMbFastRandomScope(projection);

        Console.WriteLine(
            "PASS: installed Coop ABI has one exact Harmony scope, uses MBFastRandom, and " +
            "restores identical RNG state on normal and exception paths.");
        return true;
    }

    private static void SeedValidatedBattleSceneContractForInstalledAbiSmoke(
        Assembly bridge,
        Type runtime)
    {
        var ruleType = bridge.GetType(
            "BCS.CoopBridge.BattleSceneCatalogContractRule",
            true);
        var candidateType = bridge.GetType(
            "BCS.CoopBridge.BattleSceneCatalogCandidate",
            true);
        var validationType = bridge.GetType(
            "BCS.CoopBridge.BattleSceneCatalogValidationResult",
            true);
        var rule = Activator.CreateInstance(
            ruleType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[]
            {
                "Europe1700",
                "v1.4.7.1",
                "SandBoxCore",
                "v1.4.8",
                "BattleSceneCatalog/smoke.bcs",
                "smoke.bcs",
                new string('A', 64)
            },
            null);
        var validationConstructor = validationType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 5);
        var validation = validationConstructor.Invoke(new object[]
        {
            rule,
            new string('A', 64),
            Array.CreateInstance(candidateType, 0),
            0,
            string.Empty
        });

        var activeFeaturesField = runtime.GetField(
            "activeRuntimeFeatures",
            BindingFlags.Static | BindingFlags.NonPublic);
        var validationField = runtime.GetField(
            "battleSceneCatalogValidation",
            BindingFlags.Static | BindingFlags.NonPublic);
        var validatedField = runtime.GetField(
            "validated",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (activeFeaturesField == null || validationField == null || validatedField == null)
            throw new MissingMemberException(runtime.FullName, "battle-scene validation state");
        var activeFeatures = activeFeaturesField.GetValue(null);
        var addFeature = activeFeatures.GetType().GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(string) },
            null);
        if (addFeature == null)
            throw new MissingMethodException(activeFeatures.GetType().FullName, "Add(string)");
        addFeature.Invoke(activeFeatures, new object[]
        {
            "ClientDeterministicBattleSceneProjection"
        });
        validationField.SetValue(null, validation);
        validatedField.SetValue(null, true);
    }

    private static void VerifyExactHarmonyScope(MethodInfo target, string owner)
    {
        var harmonyType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("HarmonyLib.Harmony", false, false))
            .FirstOrDefault(type => type != null);
        if (harmonyType == null)
            throw new TypeLoadException("HarmonyLib.Harmony was not loaded by installed ABI smoke.");
        var getPatchInfo = harmonyType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
                string.Equals(method.Name, "GetPatchInfo", StringComparison.Ordinal) &&
                method.GetParameters().Length == 1 &&
                typeof(MethodBase).IsAssignableFrom(method.GetParameters()[0].ParameterType));
        var patchInfo = getPatchInfo.Invoke(null, new object[] { target });
        if (patchInfo == null)
            throw new InvalidOperationException("Installed Coop initializer has no Harmony patch info.");
        var owners = ((IEnumerable)ReadMember(patchInfo, "Owners"))
            .Cast<object>()
            .Select(value => value.ToString())
            .ToArray();
        if (owners.Length != 1 || !string.Equals(owners[0], owner, StringComparison.Ordinal) ||
            CountOwnedPatches(patchInfo, "Prefixes", owner) != 1 ||
            CountOwnedPatches(patchInfo, "Postfixes", owner) != 1 ||
            CountOwnedPatches(patchInfo, "Finalizers", owner) != 1 ||
            CountOwnedPatches(patchInfo, "Transpilers", owner) != 0)
        {
            throw new InvalidOperationException(
                "Installed Coop initializer does not have one exact bridge Harmony scope.");
        }
    }

    private static int CountOwnedPatches(object patchInfo, string memberName, string owner)
    {
        var count = 0;
        foreach (var patch in (IEnumerable)ReadMember(patchInfo, memberName))
        {
            var patchOwner = ReadMember(patch, "owner").ToString();
            if (string.Equals(patchOwner, owner, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static object ReadMember(object instance, string memberName)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = instance.GetType().GetProperty(memberName, flags);
        if (property != null)
            return property.GetValue(instance, null);
        var field = instance.GetType().GetField(memberName, flags);
        if (field != null)
            return field.GetValue(instance);
        throw new MissingMemberException(instance.GetType().FullName, memberName);
    }

    private static void VerifyActualMbFastRandomScope(Type projection)
    {
        var begin = projection.GetMethod(
            "BeginRandomScopeForSmoke",
            BindingFlags.Static | BindingFlags.NonPublic);
        var create = projection.GetMethod(
            "CreateGameRandom",
            BindingFlags.Static | BindingFlags.NonPublic);
        var finalizer = projection.GetMethod(
            "FinalizeCreate",
            BindingFlags.Static | BindingFlags.Public);
        if (begin == null || create == null || finalizer == null)
            throw new MissingMemberException(projection.FullName, "installed RNG smoke seams");

        var fixture = new RandomScopeFixture();
        var originalRandom = create.Invoke(null, new object[] { 17 });
        fixture.Random = originalRandom;
        Func<object> getCurrentGame = delegate { return fixture; };
        Func<object, object> getRandom = delegate(object game)
        {
            return ((RandomScopeFixture)game).Random;
        };
        Action<object, object> setRandom = delegate(object game, object random)
        {
            ((RandomScopeFixture)game).Random = random;
        };
        Func<int, object> createRandom = delegate(int seed)
        {
            return create.Invoke(null, new object[] { seed });
        };

        var first = SampleActualScopedRandom(
            begin, fixture, originalRandom, 7312,
            getCurrentGame, getRandom, setRandom, createRandom);
        var repeated = SampleActualScopedRandom(
            begin, fixture, originalRandom, 7312,
            getCurrentGame, getRandom, setRandom, createRandom);
        var different = SampleActualScopedRandom(
            begin, fixture, originalRandom, 7313,
            getCurrentGame, getRandom, setRandom, createRandom);
        if (!first.SequenceEqual(repeated) || first.SequenceEqual(different))
            throw new InvalidOperationException("Installed MBFastRandom seed behavior is not deterministic.");

        var exceptionScope = begin.Invoke(
            null,
            new object[]
            {
                7314,
                getCurrentGame,
                getRandom,
                setRandom,
                createRandom
            });
        var injected = new InvalidOperationException("installed-abi-smoke-probe");
        var returned = finalizer.Invoke(null, new[] { (object)injected, exceptionScope });
        if (!ReferenceEquals(returned, injected) ||
            !ReferenceEquals(fixture.Random, originalRandom))
        {
            throw new InvalidOperationException(
                "Installed MBFastRandom exception path did not restore identical RNG state.");
        }
    }

    private static int[] SampleActualScopedRandom(
        MethodInfo begin,
        RandomScopeFixture fixture,
        object originalRandom,
        int seed,
        Func<object> getCurrentGame,
        Func<object, object> getRandom,
        Action<object, object> setRandom,
        Func<int, object> createRandom)
    {
        var scope = begin.Invoke(
            null,
            new object[] { seed, getCurrentGame, getRandom, setRandom, createRandom });
        var random = fixture.Random;
        if (!string.Equals(
                random.GetType().FullName,
                "TaleWorlds.Core.MBFastRandom",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Production scope did not install MBFastRandom; actual type is " +
                random.GetType().FullName + ".");
        }
        var next = random.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method =>
                string.Equals(method.Name, "Next", StringComparison.Ordinal) &&
                method.ReturnType == typeof(int) &&
                method.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(new[] { typeof(int) }));
        var sample = Enumerable.Range(0, 12)
            .Select(delegate(int _) { return (int)next.Invoke(random, new object[] { 196 }); })
            .ToArray();
        ((IDisposable)scope).Dispose();
        if (!ReferenceEquals(fixture.Random, originalRandom))
            throw new InvalidOperationException("Production scope did not restore identical RNG state.");
        return sample;
    }

    private static void VerifyRuntimeFeaturesIfRequested(Type runtime)
    {
        var expectedText = Environment.GetEnvironmentVariable(
            "BCS_BRIDGE_SMOKE_EXPECTED_FEATURES");
        if (expectedText == null)
            return;

        var isEnabled = runtime.GetMethod(
            "IsRuntimeFeatureEnabled",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (isEnabled == null)
            throw new MissingMethodException(runtime.FullName, "IsRuntimeFeatureEnabled");
        var expected = expectedText.Split(
            new[] { '|' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var feature in expected)
        {
            if (!(bool)isEnabled.Invoke(null, new object[] { feature }))
                throw new InvalidOperationException("Expected runtime feature is disabled: " + feature);
        }
        Console.WriteLine(
            "PASS: bridge runtime enabled " + expected.Length + " requested compatibility features.");
    }

    private static void VerifyDisabledRuntimeFeaturesIfRequested(Type runtime)
    {
        var disabledText = Environment.GetEnvironmentVariable(
            "BCS_BRIDGE_SMOKE_DISABLED_FEATURES");
        if (disabledText == null)
            return;

        var isEnabled = runtime.GetMethod(
            "IsRuntimeFeatureEnabled",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (isEnabled == null)
            throw new MissingMethodException(runtime.FullName, "IsRuntimeFeatureEnabled");
        var disabled = disabledText.Split(
            new[] { '|' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var feature in disabled)
        {
            if ((bool)isEnabled.Invoke(null, new object[] { feature }))
                throw new InvalidOperationException("Unexpected runtime feature is enabled: " + feature);
        }
        Console.WriteLine(
            "PASS: bridge runtime kept " + disabled.Length + " compatibility features disabled.");
    }

    private static void VerifyEconomyControlHooksIfRequested(Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_HOOKS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var control = bridge.GetType(
            "BCS.CoopBridge.ServerPopulationControl",
            true);
        var behavior = bridge.GetType(
            "BCS.CoopBridge.BridgeEconomyCampaignBehavior",
            true);
        var validateRegional = string.Equals(
            Environment.GetEnvironmentVariable(
                "BCS_BRIDGE_SMOKE_VALIDATE_REGIONAL_POPULATION_HOOKS"),
            "1",
            StringComparison.Ordinal);
        var install = control.GetMethod(
            validateRegional ? "InstallForAbiSmoke" : "Install",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (install == null)
            throw new MissingMethodException(
                control.FullName,
                validateRegional ? "InstallForAbiSmoke" : "Install");
        install.Invoke(null, null);

        var capacity = control.GetField(
            "caravanCapacityMultiplier",
            BindingFlags.Static | BindingFlags.NonPublic);
        var budget = control.GetField(
            "caravanTradeBudgetMultiplier",
            BindingFlags.Static | BindingFlags.NonPublic);
        var ageBonus = control.GetField(
            "caravanDestinationAgeMaxBonus",
            BindingFlags.Static | BindingFlags.NonPublic);
        var virtualEnabled = control.GetField(
            "virtualVillagerShipmentsEnabled",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (capacity == null || budget == null || ageBonus == null || virtualEnabled == null ||
            Math.Abs((double)capacity.GetValue(null) - 2d) > 0.000001d ||
            Math.Abs((double)budget.GetValue(null) - 2d) > 0.000001d ||
            Math.Abs((double)ageBonus.GetValue(null) - 1d) > 0.000001d ||
            !((bool)virtualEnabled.GetValue(null)))
        {
            throw new InvalidOperationException(
                "Server economy sidecar values were not loaded exactly.");
        }
        if (behavior.GetMethod(
                "SyncData",
                BindingFlags.Instance | BindingFlags.Public) == null ||
            behavior.GetMethod(
                "TryScheduleVirtualShipment",
                BindingFlags.Instance | BindingFlags.NonPublic) == null)
        {
            throw new InvalidOperationException(
                "Server economy campaign behavior lost save or virtual-shipment seams.");
        }
        VerifyEurope1700ShieldProductionSuppression(bridge);
        Console.WriteLine(
            "PASS: strict economy sidecar loaded and every enabled server economy hook installed against the target ABI.");
    }

    private static void VerifyRegionalPopulationHooksIfRequested(Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_REGIONAL_POPULATION_HOOKS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var regional = bridge.GetType(
            "BCS.CoopBridge.RegionalPopulationControl",
            true);
        var install = regional.GetMethod(
            "InstallForAbiSmoke",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (install == null)
            throw new MissingMethodException(regional.FullName, "InstallForAbiSmoke");
        install.Invoke(null, null);

        var campaignAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => string.Equals(
                assembly.GetName().Name,
                "TaleWorlds.CampaignSystem",
                StringComparison.Ordinal));
        VerifyVillagerNativeDepartureOrder(campaignAssembly);
        VerifyVillagerPhysicalRecoveryOrder(regional);
        var handler = bridge.GetType(
            "GameInterface.BCSCoopBridge.Registration.DiscoveredBridgeHandler",
            true);
        var handlerParameters = handler.GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType.FullName)
            .ToArray();
        var expectedHandlerParameters = new[]
        {
            "Common.Messaging.IMessageBroker",
            "Coop.Core.Server.Connections.IConnectionCollection",
            "GameInterface.Services.Players.IPlayerManager",
            "GameInterface.Services.ObjectManager.IObjectManager"
        };
        if (!handlerParameters.SequenceEqual(expectedHandlerParameters))
        {
            throw new InvalidOperationException(
                "Bridge handler does not require the exact authoritative player-region services.");
        }

        VerifyMethodMemberReferences(
            regional.GetMethod(
                "TryResolvePlayerPositions",
                BindingFlags.Static | BindingFlags.NonPublic),
            new[]
            {
                "Coop.Core.Server.Connections.IConnectionLogic.get_State",
                "Coop.Core.Server.Connections.States.CampaignState",
                "Coop.Core.Server.Connections.States.MissionState",
                "GameInterface.Services.Players.IPlayerManager.TryGetPlayer",
                "GameInterface.Services.Players.IPlayerManager.IsConnected",
                "GameInterface.Services.ObjectManager.IObjectManager.TryGetObject",
                "TaleWorlds.CampaignSystem.Party.MobileParty.get_IsActive",
                "TaleWorlds.CampaignSystem.Party.MobileParty.get_Position",
                "TaleWorlds.CampaignSystem.CampaignVec2.IsValid"
            });
        VerifyMethodMemberReferences(
            regional.GetMethod(
                "TryGetMapRadius",
                BindingFlags.Static | BindingFlags.NonPublic),
            new[]
            {
                "TaleWorlds.CampaignSystem.Campaign.get_EstimatedAverageBanditPartySpeed"
            });
        var economyBehavior = bridge.GetType(
            "BCS.CoopBridge.BridgeEconomyCampaignBehavior",
            true);
        VerifyMethodMemberReferences(
            economyBehavior.GetMethod(
                "RegisterEvents",
                BindingFlags.Instance | BindingFlags.Public),
            new[]
            {
                "TaleWorlds.CampaignSystem.CampaignEvents.get_QuarterHourlyTickEvent"
            });
        VerifyMethodMemberReferences(
            economyBehavior.GetMethod(
                "OnRegionalPopulationQuarterHour",
                BindingFlags.Instance | BindingFlags.NonPublic),
            new[]
            {
                "BCS.CoopBridge.RegionalPopulationControl.SweepPatrolRegionTransitions",
                "BCS.CoopBridge.RegionalPopulationControl.SweepVillagerDepartureLatches"
            });
        VerifyMethodMemberReferences(
            regional.GetMethod(
                "GetLiveVillagerParty",
                BindingFlags.Static | BindingFlags.NonPublic),
            new[]
            {
                "TaleWorlds.CampaignSystem.Settlements.Village.VillagerPartyComponent",
                "TaleWorlds.CampaignSystem.Party.PartyComponents.PartyComponent.get_MobileParty",
                "TaleWorlds.CampaignSystem.Party.MobileParty.get_IsActive"
            });
        VerifyMethodMemberReferences(
            regional.GetMethod(
                "LatchCreatedVillagerParty",
                BindingFlags.Static | BindingFlags.NonPublic),
            new[]
            {
                "BCS.CoopBridge.RegionalPopulationControl.HasLiveVillagerParty",
                "BCS.CoopBridge.RegionalPopulationControl.AddVillagerDepartureLatch",
                "BCS.CoopBridge.RegionalPopulationControl.RemoveVillagerDepartureLatch",
                "BCS.CoopBridge.BridgeEconomyCampaignBehavior.HasPendingVirtualShipment"
            });
        VerifyMethodMemberReferences(
            regional.GetMethod(
                "AfterLoadAndSendVillagerParty",
                BindingFlags.Static | BindingFlags.NonPublic),
            new[]
            {
                "BCS.CoopBridge.RegionalPopulationControl.RemoveVillagerDepartureLatch"
            });
        VerifyMethodMemberReferences(
            regional.GetMethod(
                "TryRecoverVillagerDepartureLatch",
                BindingFlags.Static | BindingFlags.NonPublic),
            new[]
            {
                "BCS.CoopBridge.RegionalPopulationControl.GetLiveVillagerParty",
                "BCS.CoopBridge.RegionalPopulationControl.trackedVillagerParties",
                "BCS.CoopBridge.RegionalPopulationControl.pendingPhysicalVillagerDepartures"
            });

        const string owner = "BCS.CoopBridge.regional-population-control";
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior",
            "SpawnBanditParty", owner, 1, 0, 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior",
            "SpawnLooterParty", owner, 1, 0, 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior",
            "SelectBanditHideout", owner, 0, 1, 0, expectedPostfixes: 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior",
            "SelectAHideoutByCheckingCultureAndInfestedState", owner, 0, 1, 0);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditSpawnCampaignBehavior",
            "SelectARandomSettlementForLooterParty", owner, 0, 1, 0,
            expectedPostfixes: 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.VillagerCampaignBehavior",
            "ThinkAboutSendingItemToTown", owner, 0, 1, 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.VillagerCampaignBehavior",
            "LoadAndSendVillagerParty", owner, 1, 0, 1, expectedPostfixes: 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.PatrolPartiesCampaignBehavior",
            "DailyTickSettlement", owner, 1, 0, 0);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.PatrolPartiesCampaignBehavior",
            "UpdateSettlementQueue", owner, 1, 0, 0, expectedPostfixes: 1);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.PatrolPartiesCampaignBehavior",
            "SpawnPatrolParty", owner, 1, 0, 0);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.DesertersCampaignBehavior",
            "SelectRandomSettlementsForDeserters", owner, 0, 1, 0);
        VerifyOwnedRegionalPatch(campaignAssembly,
            "TaleWorlds.CampaignSystem.CampaignBehaviors.DesertersCampaignBehavior",
            "TrySpawnDeserters", owner, 0, 1, 0);

        Console.WriteLine(
            "PASS: all four regional families installed atomically on exact Bannerlord and Coop ABI seams.");
    }

    private static void VerifyOwnedRegionalPatch(
        Assembly campaignAssembly,
        string typeName,
        string methodName,
        string owner,
        int expectedPrefixes,
        int expectedTranspilers,
        int expectedFinalizers,
        int expectedPostfixes = 0)
    {
        var type = campaignAssembly.GetType(typeName, true, false);
        var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        if (methods.Length != 1)
            throw new MissingMethodException(type.FullName, methodName + " exact ABI smoke target");

        var harmonyType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("HarmonyLib.Harmony", false, false))
            .FirstOrDefault(typeValue => typeValue != null);
        if (harmonyType == null)
            throw new TypeLoadException("HarmonyLib.Harmony was not loaded by regional ABI smoke.");
        var getPatchInfo = harmonyType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
                string.Equals(method.Name, "GetPatchInfo", StringComparison.Ordinal) &&
                method.GetParameters().Length == 1 &&
                typeof(MethodBase).IsAssignableFrom(method.GetParameters()[0].ParameterType));
        var patchInfo = getPatchInfo.Invoke(null, new object[] { methods[0] });
        if (patchInfo == null ||
            CountOwnedPatches(patchInfo, "Prefixes", owner) != expectedPrefixes ||
            CountOwnedPatches(patchInfo, "Transpilers", owner) != expectedTranspilers ||
            CountOwnedPatches(patchInfo, "Finalizers", owner) != expectedFinalizers ||
            CountOwnedPatches(patchInfo, "Postfixes", owner) != expectedPostfixes)
        {
            throw new InvalidOperationException(
                "Regional owner has the wrong patch shape on " + typeName + "." + methodName + ".");
        }
    }

    private static void VerifyMethodMemberReferences(
        MethodInfo method,
        IEnumerable<string> expectedMembers)
    {
        if (method == null || method.GetMethodBody() == null)
            throw new MissingMethodException("Regional policy method body was not available.");
        var referenced = ReadReferencedMembers(method);
        var missing = expectedMembers
            .Where(expected => !referenced.Contains(expected))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "Regional policy lost exact authority/radius references: " +
                string.Join(", ", missing) + ".");
        }
    }

    private static HashSet<string> ReadReferencedMembers(MethodInfo method)
    {
        return new HashSet<string>(
            ReadReferencedMembersInOrder(method),
            StringComparer.Ordinal);
    }

    private static List<string> ReadReferencedMembersInOrder(MethodInfo method)
    {
        var opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Static | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(opcode => opcode.Value);
        var result = new List<string>();
        var bytes = method.GetMethodBody().GetILAsByteArray();
        for (var index = 0; index < bytes.Length;)
        {
            short value = bytes[index++] == 0xFE
                ? unchecked((short)(0xFE00 | bytes[index++]))
                : (short)bytes[index - 1];
            OpCode opcode;
            if (!opcodes.TryGetValue(value, out opcode))
                throw new InvalidDataException("Unknown IL opcode in regional smoke.");

            var operandSize = GetOperandSize(opcode.OperandType, bytes, index);
            if (opcode.OperandType == OperandType.InlineMethod ||
                opcode.OperandType == OperandType.InlineField ||
                opcode.OperandType == OperandType.InlineType ||
                opcode.OperandType == OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(bytes, index);
                var member = method.Module.ResolveMember(token);
                var type = member as Type;
                if (type != null)
                    result.Add(type.FullName);
                else if (member.DeclaringType != null)
                    result.Add(member.DeclaringType.FullName + "." + member.Name);
            }
            index += operandSize;
        }
        return result;
    }

    private static void VerifyVillagerNativeDepartureOrder(Assembly campaignAssembly)
    {
        var behavior = campaignAssembly.GetType(
            "TaleWorlds.CampaignSystem.CampaignBehaviors.VillagerCampaignBehavior",
            true,
            false);
        var departure = behavior.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Single(method =>
                string.Equals(
                    method.Name,
                    "ThinkAboutSendingItemToTown",
                    StringComparison.Ordinal) &&
                method.GetParameters().Length == 1 &&
                string.Equals(
                    method.GetParameters()[0].ParameterType.FullName,
                    "TaleWorlds.CampaignSystem.Settlements.Village",
                    StringComparison.Ordinal));
        var referenced = ReadReferencedMembersInOrder(departure);
        var hearth = referenced.IndexOf(
            "TaleWorlds.CampaignSystem.Settlements.Village.get_Hearth");
        var minimumPartySize = referenced.IndexOf(
            "TaleWorlds.CampaignSystem.ComponentInterfaces.PartySizeLimitModel.get_MinimumNumberOfVillagersAtVillagerParty");
        var create = referenced.IndexOf(
            "TaleWorlds.CampaignSystem.CampaignBehaviors.VillagerCampaignBehavior.CreateVillagerParty");
        var loadAndSend = referenced.IndexOf(
            "TaleWorlds.CampaignSystem.CampaignBehaviors.VillagerCampaignBehavior.LoadAndSendVillagerParty");
        if (hearth < 0 ||
            minimumPartySize <= hearth ||
            create <= minimumPartySize ||
            loadAndSend <= create)
        {
            throw new InvalidOperationException(
                "Regional villager admission no longer follows native hearth and action ordering.");
        }
    }

    private static void VerifyVillagerPhysicalRecoveryOrder(Type regional)
    {
        var admission = regional.GetMethod(
            "TryAdmitVillagerLoad",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (admission == null)
            throw new MissingMethodException(regional.FullName, "TryAdmitVillagerLoad");
        var referenced = ReadReferencedMembersInOrder(admission);
        var pendingVirtual = referenced.IndexOf(
            "BCS.CoopBridge.BridgeEconomyCampaignBehavior.HasPendingVirtualShipment");
        var existingLatch = referenced.IndexOf(
            "BCS.CoopBridge.RegionalPopulationControl.HasVillagerDepartureLatch");
        var recoverPhysical = referenced.IndexOf(
            "BCS.CoopBridge.RegionalPopulationControl.TryRecoverVillagerDepartureLatch");
        var evaluateRegion = referenced.IndexOf(
            "BCS.CoopBridge.RegionalPopulationControl.EvaluateOrigin");
        if (pendingVirtual < 0 ||
            existingLatch <= pendingVirtual ||
            recoverPhysical <= existingLatch ||
            evaluateRegion <= recoverPhysical)
        {
            throw new InvalidOperationException(
                "Regional villager load no longer preserves virtual mode, reuses or " +
                "recovers physical mode, then evaluates a new departure in exact order.");
        }
    }

    private static int GetOperandSize(
        OperandType operandType,
        byte[] bytes,
        int operandIndex)
    {
        switch (operandType)
        {
            case OperandType.InlineNone:
                return 0;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                return 1;
            case OperandType.InlineVar:
                return 2;
            case OperandType.InlineI:
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                return 4;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                return 8;
            case OperandType.InlineSwitch:
                return 4 + BitConverter.ToInt32(bytes, operandIndex) * 4;
            default:
                throw new InvalidDataException("Unsupported IL operand in regional smoke.");
        }
    }

    private static void VerifyEconomySaveSchemaIfRequested(Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var saveSystem = Assembly.Load(new AssemblyName("TaleWorlds.SaveSystem"));
        var campaignSystem = Assembly.Load(new AssemblyName("TaleWorlds.CampaignSystem"));
        var core = Assembly.Load(new AssemblyName("TaleWorlds.Core"));
        var definer = bridge.GetType(
            "BCS.CoopBridge.BridgeEconomySaveableTypeDefiner",
            true);
        if (!definer.IsPublic || definer.IsAbstract ||
            definer.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new InvalidOperationException(
                "Bridge economy save-type definer is not discoverable.");
        }

        var contextType = saveSystem.GetType(
            "TaleWorlds.SaveSystem.Definition.DefinitionContext",
            true);
        var context = Activator.CreateInstance(contextType);
        var fill = contextType.GetMethod(
            "FillWithCurrentTypes",
            BindingFlags.Instance | BindingFlags.Public);
        var hasDefinition = contextType.GetMethod(
            "HasDefinition",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (fill == null || hasDefinition == null)
            throw new MissingMethodException(contextType.FullName, "FillWithCurrentTypes/HasDefinition");
        fill.Invoke(context, null);

        var village = campaignSystem.GetType(
            "TaleWorlds.CampaignSystem.Settlements.Village",
            true);
        var settlement = campaignSystem.GetType(
            "TaleWorlds.CampaignSystem.Settlements.Settlement",
            true);
        var equipmentElement = core.GetType(
            "TaleWorlds.Core.EquipmentElement",
            true);
        var equipmentList = typeof(List<>).MakeGenericType(equipmentElement);
        var integerList = typeof(List<>).MakeGenericType(typeof(int));
        var requiredContainers = new[]
        {
            typeof(Dictionary<,>).MakeGenericType(village, typeof(int)),
            typeof(Dictionary<,>).MakeGenericType(village, settlement),
            typeof(Dictionary<,>).MakeGenericType(village, equipmentList),
            typeof(Dictionary<,>).MakeGenericType(village, integerList)
        };
        foreach (var container in requiredContainers)
        {
            if (!(bool)hasDefinition.Invoke(context, new object[] { container }))
            {
                throw new InvalidOperationException(
                    "Bannerlord did not register bridge save container: " + container.FullName);
            }
        }
        Console.WriteLine(
            "PASS: Bannerlord SaveSystem discovered all four bridge economy containers.");
    }

    private static void PreloadEconomySaveSchemaDependenciesIfRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_ECONOMY_SAVE_SCHEMA"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        foreach (var assemblyName in new[]
                 {
                     "TaleWorlds.SaveSystem",
                     "TaleWorlds.Library",
                     "TaleWorlds.Localization",
                     "TaleWorlds.ObjectSystem",
                     "TaleWorlds.Core",
                     "TaleWorlds.CampaignSystem"
                 })
        {
            Assembly.Load(new AssemblyName(assemblyName));
        }
    }

    private static void VerifyEurope1700ShieldProductionSuppression(Assembly bridge)
    {
        var compatibility = bridge.GetType(
            "BCS.CoopBridge.ServerFailedIdCompatibility",
            true);
        var suppress = compatibility.GetMethod(
            "DisableEurope1700InheritedShieldProductions",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (suppress == null)
        {
            throw new MissingMethodException(
                compatibility.FullName,
                "DisableEurope1700InheritedShieldProductions");
        }

        var campaignAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => string.Equals(
                assembly.GetName().Name,
                "TaleWorlds.CampaignSystem",
                StringComparison.Ordinal));
        var coreAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => string.Equals(
                assembly.GetName().Name,
                "TaleWorlds.Core",
                StringComparison.Ordinal));
        var workshopType = campaignAssembly.GetType(
            "TaleWorlds.CampaignSystem.Settlements.Workshops.WorkshopType",
            true,
            false);
        var productionType = workshopType.GetNestedType(
            "Production",
            BindingFlags.Public | BindingFlags.NonPublic);
        var itemCategoryType = coreAssembly.GetType(
            "TaleWorlds.Core.ItemCategory",
            true,
            false);
        if (productionType == null)
            throw new TypeLoadException(workshopType.FullName + "+Production");

        var fixture = Activator.CreateInstance(workshopType);
        workshopType.GetProperty("StringId").SetValue(
            fixture,
            "wood_WorkshopType",
            null);
        var productionsField = workshopType.GetField(
            "_productions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (productionsField == null)
            throw new MissingFieldException(workshopType.FullName, "_productions");
        var productions = (IList)productionsField.GetValue(fixture);
        if (productions == null)
        {
            productions = (IList)Activator.CreateInstance(productionsField.FieldType);
            productionsField.SetValue(fixture, productions);
        }

        AddProduction(productions, productionType, itemCategoryType, "shield");
        AddProduction(
            productions,
            productionType,
            itemCategoryType,
            "tools",
            "shield_5");
        AddProduction(productions, productionType, itemCategoryType, "tools");
        AddProduction(productions, productionType, itemCategoryType, "shield_wall");

        var fixtures = Array.CreateInstance(workshopType, 1);
        fixtures.SetValue(fixture, 0);
        var removed = (int)suppress.Invoke(null, new object[] { fixtures });
        var repeated = (int)suppress.Invoke(null, new object[] { fixtures });
        var remainingCategories = productions
            .Cast<object>()
            .SelectMany(production =>
                ((IEnumerable)productionType.GetProperty("Outputs").GetValue(
                    production,
                    null))
                .Cast<object>())
            .Select(output => ReadMember(output, "Item1"))
            .Select(category => ReadMember(category, "StringId").ToString())
            .ToArray();
        if (removed != 2 || repeated != 0 || productions.Count != 2 ||
            !remainingCategories.SequenceEqual(new[] { "tools", "shield_wall" }))
        {
            throw new InvalidOperationException(
                "EOE shield suppression did not remove exact shield-category productions.");
        }
        Console.WriteLine(
            "PASS: EOE suppression removes all exact shield-output productions and preserves unrelated outputs.");
    }

    private static bool VerifyEurope1700ShieldProductionSuppressionOnlyIfRequested(
        Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_EOE_SHIELD_SUPPRESSION"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }
        VerifyEurope1700ShieldProductionSuppression(bridge);
        return true;
    }

    private static void AddProduction(
        IList productions,
        Type productionType,
        Type itemCategoryType,
        params string[] categoryIds)
    {
        var production = Activator.CreateInstance(productionType, new object[] { 1f });
        var addOutput = productionType.GetMethod(
            "AddOutput",
            BindingFlags.Instance | BindingFlags.Public);
        if (addOutput == null)
            throw new MissingMethodException(productionType.FullName, "AddOutput");
        foreach (var categoryId in categoryIds)
        {
            var category = Activator.CreateInstance(
                itemCategoryType,
                new object[] { categoryId });
            addOutput.Invoke(production, new[] { category, (object)1 });
        }
        productions.Add(production);
    }

    private static void VerifyBattleSceneRandomScopeIfRequested(Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_BATTLE_SCENE_RANDOM_SCOPE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var projection = bridge.GetType(
            "BCS.CoopBridge.ClientDeterministicBattleSceneProjection",
            true);
        var begin = projection.GetMethod(
            "BeginRandomScopeForSmoke",
            BindingFlags.Static | BindingFlags.NonPublic);
        var finalizer = projection.GetMethod(
            "FinalizeCreate",
            BindingFlags.Static | BindingFlags.Public);
        if (begin == null || finalizer == null)
        {
            throw new MissingMemberException(
                projection.FullName,
                "battle-scene random scope smoke seams");
        }

        var fixture = new RandomScopeFixture();
        var originalRandom = new object();
        fixture.Random = originalRandom;
        Func<object> getCurrentGame = delegate { return fixture; };
        Func<object, object> getRandom = delegate(object game)
        {
            return ((RandomScopeFixture)game).Random;
        };
        Action<object, object> setRandom = delegate(object game, object random)
        {
            ((RandomScopeFixture)game).Random = random;
        };
        Func<int, object> createRandom = delegate(int seed)
        {
            return new DeterministicRandomFixture(seed);
        };

        var first = SampleScopedRandom(
            begin,
            fixture,
            originalRandom,
            73129,
            getCurrentGame,
            getRandom,
            setRandom,
            createRandom);
        var repeated = SampleScopedRandom(
            begin,
            fixture,
            originalRandom,
            73129,
            getCurrentGame,
            getRandom,
            setRandom,
            createRandom);
        if (!first.SequenceEqual(repeated))
            throw new InvalidOperationException("Same battle seed produced different scoped RNG output.");

        var different = SampleScopedRandom(
            begin,
            fixture,
            originalRandom,
            73130,
            getCurrentGame,
            getRandom,
            setRandom,
            createRandom);
        if (first.SequenceEqual(different))
            throw new InvalidOperationException("Different battle seeds produced identical scoped RNG output.");

        var exceptionScope = begin.Invoke(
            null,
            new object[]
            {
                73131,
                getCurrentGame,
                getRandom,
                setRandom,
                createRandom
            });
        if (ReferenceEquals(fixture.Random, originalRandom))
            throw new InvalidOperationException("Battle-scene RNG scope did not replace current RNG.");
        var injected = new InvalidOperationException("battle-scene-smoke-probe");
        var returned = finalizer.Invoke(null, new[] { (object)injected, exceptionScope });
        if (!ReferenceEquals(returned, injected))
            throw new InvalidOperationException("Battle-scene finalizer did not preserve original exception.");
        if (!ReferenceEquals(fixture.Random, originalRandom))
            throw new InvalidOperationException("Battle-scene finalizer did not restore original RNG.");

        Console.WriteLine(
            "PASS: battle-scene RNG scope is same-seed deterministic, different-seed variable, " +
            "and restores identical RNG state on normal and exception paths.");
    }

    private static int[] SampleScopedRandom(
        MethodInfo begin,
        RandomScopeFixture fixture,
        object originalRandom,
        int seed,
        Func<object> getCurrentGame,
        Func<object, object> getRandom,
        Action<object, object> setRandom,
        Func<int, object> createRandom)
    {
        var scope = begin.Invoke(
            null,
            new object[] { seed, getCurrentGame, getRandom, setRandom, createRandom });
        var disposable = scope as IDisposable;
        if (disposable == null)
            throw new InvalidOperationException("Battle-scene RNG smoke scope is not disposable.");
        var random = fixture.Random as DeterministicRandomFixture;
        if (random == null)
            throw new InvalidOperationException("Battle-scene RNG scope did not install seeded RNG.");
        var sample = Enumerable.Range(0, 12)
            .Select(delegate(int _) { return random.Next(196); })
            .ToArray();
        disposable.Dispose();
        if (!ReferenceEquals(fixture.Random, originalRandom))
            throw new InvalidOperationException("Normal battle-scene scope disposal did not restore RNG.");
        return sample;
    }

    private sealed class RandomScopeFixture
    {
        internal object Random;
    }

    private sealed class DeterministicRandomFixture
    {
        private uint state;

        internal DeterministicRandomFixture(int seed)
        {
            state = unchecked((uint)seed);
        }

        internal int Next(int exclusiveMaximum)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            return (int)(state % (uint)exclusiveMaximum);
        }
    }

    private static void VerifyCharacterCreationLifecycleGateIfRequested(Assembly bridge)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "BCS_BRIDGE_SMOKE_VALIDATE_CHARACTER_CREATION_GATE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var gateType = bridge.GetType(
            "BCS.CoopBridge.ClientCharacterCreationLifecycleGate",
            true);
        var gate = Activator.CreateInstance(gateType, true);
        var arm = gateType.GetMethod(
            "Arm",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var cancel = gateType.GetMethod(
            "Cancel",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var peek = gateType.GetMethod(
            "Peek",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var tryClaim = gateType.GetMethod(
            "TryClaim",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var tryClaimIntroLoadingOverlayRelease = gateType.GetMethod(
            "TryClaimIntroLoadingOverlayRelease",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (arm == null || cancel == null || peek == null || tryClaim == null ||
            tryClaimIntroLoadingOverlayRelease == null)
            throw new MissingMemberException(gateType.FullName, "lifecycle gate methods");

        var firstState = new object();
        var otherState = new object();
        if ((bool)tryClaim.Invoke(gate, new[] { firstState }))
            throw new InvalidOperationException("Unarmed lifecycle fallback was claimable.");
        if ((bool)tryClaimIntroLoadingOverlayRelease.Invoke(gate, new[] { firstState }))
            throw new InvalidOperationException("Unarmed intro loading-overlay release was claimable.");

        arm.Invoke(gate, new[] { firstState });
        cancel.Invoke(gate, new[] { firstState });
        if (peek.Invoke(gate, null) != null ||
            (bool)tryClaim.Invoke(gate, new[] { firstState }) ||
            (bool)tryClaimIntroLoadingOverlayRelease.Invoke(gate, new[] { firstState }))
        {
            throw new InvalidOperationException(
                "Normal upstream lifecycle notification did not suppress the fallback.");
        }

        arm.Invoke(gate, new[] { firstState });
        cancel.Invoke(gate, new[] { otherState });
        if ((bool)tryClaim.Invoke(gate, new[] { otherState }) ||
            (bool)tryClaimIntroLoadingOverlayRelease.Invoke(gate, new[] { otherState }) ||
            !(bool)tryClaimIntroLoadingOverlayRelease.Invoke(gate, new[] { firstState }) ||
            (bool)tryClaimIntroLoadingOverlayRelease.Invoke(gate, new[] { firstState }) ||
            !(bool)tryClaim.Invoke(gate, new[] { firstState }) ||
            (bool)tryClaim.Invoke(gate, new[] { firstState }))
        {
            throw new InvalidOperationException(
                "Character-creation lifecycle fallback was not state-scoped and one-shot.");
        }

        cancel.Invoke(gate, new[] { firstState });
        arm.Invoke(gate, new[] { firstState });
        if (!(bool)tryClaim.Invoke(gate, new[] { firstState }) ||
            !(bool)tryClaimIntroLoadingOverlayRelease.Invoke(gate, new[] { firstState }))
        {
            throw new InvalidOperationException(
                "A new ValidateModuleState activation did not re-arm the lifecycle fallback.");
        }
        Console.WriteLine(
            "PASS: character-creation lifecycle fallback and intro overlay release are independent, state-scoped, one-shot, and preserve the normal path.");
    }

    private static void VerifyGameVersionCompatibilityIfRequested(string bridgePath)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var versionRecord = ReadGameVersionRecord(bridgePath);
        var serverBaseVersion = versionRecord[0];
        var clientBaseVersion = versionRecord[1];
        var serverRuntimeVersion = versionRecord[2];
        var clientRuntimeVersion = versionRecord[3];
        if (!serverRuntimeVersion.StartsWith(
                serverBaseVersion + ".",
                StringComparison.OrdinalIgnoreCase) ||
            !clientRuntimeVersion.StartsWith(
                clientBaseVersion + ".",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Bridge smoke runtime versions do not match their declared base versions.");
        }

        var gameInterface = AppDomain.CurrentDomain.GetAssemblies().First(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "GameInterface",
                StringComparison.OrdinalIgnoreCase));
        var moduleInfo = gameInterface.GetType(
            "GameInterface.Services.Modules.ModuleInfo",
            true);
        var validator = gameInterface.GetType(
            "GameInterface.Services.Modules.Validators.ModuleValidator",
            true);
        var applicationVersion = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(
                "TaleWorlds.Library.ApplicationVersion",
                false))
            .First(type => type != null);
        var fromString = applicationVersion.GetMethod(
            "FromString",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(string), typeof(int) },
            null);
        var moduleConstructor = moduleInfo.GetConstructor(new[]
        {
            typeof(string),
            typeof(bool),
            typeof(bool),
            applicationVersion
        });
        var validate = validator.GetMethod(
            "ValidateGameVersion",
            BindingFlags.Static | BindingFlags.NonPublic);
        var validateModules = validator.GetMethod(
            "Validate",
            BindingFlags.Instance | BindingFlags.Public);
        if (fromString == null || moduleConstructor == null ||
            validate == null || validateModules == null)
            throw new MissingMemberException("Released Coop game-version validation fixture is incomplete.");

        var server = CreateModuleInfoArray(
            moduleInfo,
            moduleConstructor,
            fromString,
            serverRuntimeVersion);
        var client = CreateModuleInfoArray(
            moduleInfo,
            moduleConstructor,
            fromString,
            clientRuntimeVersion);
        var arguments = new object[] { server, client, null };
        var accepted = (bool)validate.Invoke(null, arguments);
        if (!accepted || arguments[2] != null)
            throw new InvalidOperationException("Supported released game-version pair was not accepted.");

        var unsupportedServer = CreateModuleInfoArray(
            moduleInfo,
            moduleConstructor,
            fromString,
            CreateUnsupportedRuntimeVersion(serverRuntimeVersion));
        arguments = new object[] { unsupportedServer, client, null };
        accepted = (bool)validate.Invoke(null, arguments);
        if (accepted || string.IsNullOrWhiteSpace(arguments[2] as string))
            throw new InvalidOperationException("Unsupported game-version pair bypassed Coop validation.");

        VerifyCurrentVersionRepair(
            bridgePath,
            applicationVersion,
            fromString,
            serverRuntimeVersion);

        if (bridgePath.IndexOf(
                "Win64_Shipping_Server",
                StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.WriteLine(
                "PASS: game-version adapter accepted only the supported release pair.");
            return;
        }

        var serverNative = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Native",
            true,
            serverRuntimeVersion);
        var clientNative = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Native",
            true,
            clientRuntimeVersion);
        var harmony = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Bannerlord.Harmony",
            false,
            "v2.4.2.248");
        var moduleServer = CreateModuleInfoArray(moduleInfo, serverNative);
        var moduleClient = CreateModuleInfoArray(moduleInfo, clientNative, harmony);
        arguments = new object[] { moduleServer, moduleClient, null };
        accepted = (bool)validateModules.Invoke(
            Activator.CreateInstance(validator),
            arguments);
        if (!accepted || arguments[2] != null)
        {
            throw new InvalidOperationException(
                "Supported client-only Harmony module was not accepted.");
        }

        var unsupportedHarmony = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Bannerlord.Harmony",
            false,
            "v2.4.3.0");
        moduleClient = CreateModuleInfoArray(
            moduleInfo,
            clientNative,
            unsupportedHarmony);
        arguments = new object[] { moduleServer, moduleClient, null };
        accepted = (bool)validateModules.Invoke(
            Activator.CreateInstance(validator),
            arguments);
        if (accepted || string.IsNullOrWhiteSpace(arguments[2] as string))
        {
            throw new InvalidOperationException(
                "Unsupported client-only Harmony version bypassed Coop validation.");
        }
        Console.WriteLine(
            "PASS: game-version and client-only-module adapters accepted only supported releases.");
    }

    private static string[] ReadGameVersionRecord(string bridgePath)
    {
        var directory = Directory.GetParent(Path.GetFullPath(bridgePath));
        string configurationPath = null;
        for (var depth = 0; depth < 4 && directory != null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "bcs-coop-bridge.config");
            if (File.Exists(candidate))
            {
                configurationPath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        if (configurationPath == null)
            throw new FileNotFoundException("Bridge smoke configuration was not found.");

        var records = File.ReadAllLines(configurationPath)
            .Where(line => line.StartsWith("GAME_VERSION_COMPAT|", StringComparison.Ordinal))
            .ToArray();
        if (records.Length != 1)
            throw new InvalidDataException("Bridge smoke requires one game-version compatibility record.");
        var fields = records[0].Split('|');
        if (fields.Length != 5)
            throw new InvalidDataException("Bridge game-version compatibility record is malformed.");
        return new[]
        {
            Decode(fields[1]),
            Decode(fields[2]),
            Decode(fields[3]),
            Decode(fields[4])
        };
    }

    private static string Decode(string value)
    {
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private static string CreateUnsupportedRuntimeVersion(string value)
    {
        var components = value.Substring(1).Split('.');
        int major;
        if (components.Length != 4 ||
            !int.TryParse(components[0], out major) ||
            major == int.MaxValue)
        {
            throw new InvalidDataException("Could not derive an unsupported game version for smoke testing.");
        }
        components[0] = (major + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value[0] + string.Join(".", components);
    }

    private static void VerifyCurrentVersionRepair(
        string bridgePath,
        Type applicationVersion,
        MethodInfo fromString,
        string serverRuntimeVersion)
    {
        var bridge = Assembly.LoadFrom(Path.GetFullPath(bridgePath));
        var repairType = bridge.GetType(
            "BCS.CoopBridge.ServerCurrentVersionCompatibility",
            false);
        var isServer = bridgePath.IndexOf(
            "Win64_Shipping_Server",
            StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isServer)
        {
            if (repairType != null)
                throw new InvalidOperationException("Client bridge contains the server CurrentVersion repair.");
            return;
        }
        if (repairType == null)
            throw new TypeLoadException("Server bridge CurrentVersion repair type was not found.");

        var repair = repairType.GetMethod(
            "Repair",
            BindingFlags.Static | BindingFlags.Public);
        var empty = applicationVersion.GetField(
            "Empty",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (repair == null || empty == null)
            throw new MissingMemberException("Server CurrentVersion repair smoke fixture is incomplete.");

        var valid = fromString.Invoke(null, new object[] { serverRuntimeVersion, 0 });
        var validArguments = new[] { valid };
        repair.Invoke(null, validArguments);
        if (!string.Equals(
                validArguments[0].ToString(),
                serverRuntimeVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CurrentVersion repair changed a valid version.");
        }

        var emptyArguments = new[] { empty.GetValue(null) };
        repair.Invoke(null, emptyArguments);
        if (!string.Equals(
                emptyArguments[0].ToString(),
                serverRuntimeVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CurrentVersion repair did not replace ApplicationVersion.Empty.");
        }
        Console.WriteLine("PASS: server CurrentVersion repair is empty-only and semantic-version scoped.");
    }

    private static Array CreateModuleInfoArray(
        Type moduleInfo,
        ConstructorInfo constructor,
        MethodInfo fromString,
        string versionText)
    {
        return CreateModuleInfoArray(
            moduleInfo,
            CreateModuleInfo(
                constructor,
                fromString,
                "Native",
                true,
                versionText));
    }

    private static object CreateModuleInfo(
        ConstructorInfo constructor,
        MethodInfo fromString,
        string id,
        bool isOfficial,
        string versionText)
    {
        var version = fromString.Invoke(null, new object[] { versionText, 0 });
        return constructor.Invoke(new[]
        {
            (object)id,
            isOfficial,
            false,
            version
        });
    }

    private static Array CreateModuleInfoArray(Type moduleInfo, params object[] modules)
    {
        var array = Array.CreateInstance(moduleInfo, modules.Length);
        for (var index = 0; index < modules.Length; index++)
            array.SetValue(modules[index], index);
        return array;
    }

    private static void InitializeActiveModuleFixtureIfRequested()
    {
        var idsText = Environment.GetEnvironmentVariable("BCS_BRIDGE_SMOKE_ACTIVE_IDS");
        var pathsText = Environment.GetEnvironmentVariable("BCS_BRIDGE_SMOKE_ACTIVE_PATHS");
        if (string.IsNullOrWhiteSpace(idsText) || string.IsNullOrWhiteSpace(pathsText))
            return;

        var moduleManager = AppDomain.CurrentDomain.GetAssemblies().First(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "TaleWorlds.ModuleManager",
                StringComparison.OrdinalIgnoreCase));
        var helper = moduleManager.GetType("TaleWorlds.ModuleManager.ModuleHelper", true);
        var initialize = helper.GetMethod(
            "InitializeModules",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(string[]), typeof(string[]) },
            null);
        if (initialize == null)
            throw new MissingMethodException(helper.FullName, "InitializeModules");
        var ids = idsText.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var paths = pathsText.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        initialize.Invoke(null, new object[] { ids, paths });

        var overrideRoot = Environment.GetEnvironmentVariable(
            "BCS_BRIDGE_SMOKE_OVERRIDE_ACTIVE_ROOT");
        if (string.IsNullOrWhiteSpace(overrideRoot))
            return;
        var getActive = helper.GetMethod(
            "GetActiveModules",
            BindingFlags.Static | BindingFlags.Public,
            null,
            Type.EmptyTypes,
            null);
        var active = (IEnumerable)getActive.Invoke(null, null);
        foreach (var module in active)
        {
            var id = module.GetType().GetProperty("Id").GetValue(module, null) as string;
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;
            var folder = module.GetType().GetProperty("FolderPath");
            folder.GetSetMethod(true).Invoke(module, new object[] { overrideRoot });
        }
    }

    private static void VerifyAuthorityRuleIfPresent(string bridgePath)
    {
        var fixture = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("AuthoritySmokeFixture.Target", false))
            .FirstOrDefault(type => type != null);
        if (fixture == null)
            return;

        var reset = fixture.GetMethod("Reset", BindingFlags.Static | BindingFlags.Public);
        var invoke = fixture.GetMethod("Invoke", BindingFlags.Static | BindingFlags.Public);
        var invokeClientOnly = fixture.GetMethod(
            "InvokeClientOnly",
            BindingFlags.Static | BindingFlags.Public);
        var count = fixture.GetProperty("Count", BindingFlags.Static | BindingFlags.Public);
        var clientOnlyCount = fixture.GetProperty(
            "ClientOnlyCount",
            BindingFlags.Static | BindingFlags.Public);
        if (reset == null || invoke == null || invokeClientOnly == null || count == null ||
            clientOnlyCount == null)
            throw new MissingMemberException("Authority smoke fixture surface is incomplete.");

        var expectedServer = string.Equals(
            new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(bridgePath))).Name,
            "Win64_Shipping_Server",
            StringComparison.OrdinalIgnoreCase);
        reset.Invoke(null, null);
        invoke.Invoke(null, null);
        invokeClientOnly.Invoke(null, null);
        var expectedServerCount = expectedServer ? 1 : 0;
        var expectedClientCount = expectedServer ? 0 : 1;
        if ((int)count.GetValue(null, null) != expectedServerCount)
            throw new InvalidOperationException("Server-only authority scope did not match runtime role.");
        if ((int)clientOnlyCount.GetValue(null, null) != expectedClientCount)
            throw new InvalidOperationException("Client-only authority scope did not match runtime role.");
        Console.WriteLine(
            "PASS: authority adapter enforced " + (expectedServer ? "server" : "client") +
            " runtime scope.");
    }
}
