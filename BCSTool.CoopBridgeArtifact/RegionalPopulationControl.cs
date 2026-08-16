#if BCS_SERVER
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.States;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BCS.CoopBridge
{
    /// <summary>
    /// Server-wide, operator-owned regional admission policy. It patches only
    /// the four reviewed Bannerlord systemic spawn behaviors and never a shared
    /// party factory, existing party, or mod-owned creation path.
    /// </summary>
    internal static class RegionalPopulationControl
    {
        internal const string HarmonyOwner =
            "BCS.CoopBridge.regional-population-control";

        private static readonly object Sync = new object();
        private static readonly Dictionary<RegionalFamily, FamilyCounters> Counters =
            new Dictionary<RegionalFamily, FamilyCounters>();

        [ThreadStatic]
        private static List<CampaignVec2> activeOutlawPlayerPositions;
        [ThreadStatic]
        private static bool outlawOriginAdmitted;
        [ThreadStatic]
        private static bool outlawOriginRejectedOutside;
        [ThreadStatic]
        private static Village admittedVillagerDeparture;

        private static readonly HashSet<Village> pendingPhysicalVillagerDepartures =
            new HashSet<Village>();
        // A live party unknown to this runtime is one recovered in-flight
        // physical cycle. Remembering its identity prevents later departures
        // by the same reusable native party from bypassing regional admission.
        private static readonly Dictionary<Village, MobileParty> trackedVillagerParties =
            new Dictionary<Village, MobileParty>();
        private static Campaign trackedVillagerCampaign;

        private static bool installed;
        private static bool abiSmokeInstalling;
        private static double radiusBanditTravelDays;
        private static bool ambientOutlawsEnabled;
        private static bool villagerTradeEnabled;
        private static bool settlementPatrolsEnabled;
        private static bool battleDesertersEnabled;
        private static IConnectionCollection connectionCollection;
        private static IPlayerManager playerManager;
        private static IObjectManager objectManager;
        private static FieldInfo patrolGenerationQueueField;
        private static PatrolPartiesCampaignBehavior trackedPatrolBehavior;
        private static readonly HashSet<Settlement> trackedPatrolTimers =
            new HashSet<Settlement>();
        private static bool patrolTrackingInitialized;
        private static long virtualCyclesScheduled;
        private static long patrolTimersDiscarded;
        private static long deserterGroupsSuppressed;

        internal static bool AnyFamilyEnabled
        {
            get
            {
                return ambientOutlawsEnabled || villagerTradeEnabled ||
                       settlementPatrolsEnabled || battleDesertersEnabled;
            }
        }

        internal static void Install(
            double configuredRadiusBanditTravelDays,
            bool configuredAmbientOutlawsEnabled,
            bool configuredVillagerTradeEnabled,
            bool configuredSettlementPatrolsEnabled,
            bool configuredBattleDesertersEnabled,
            bool virtualVillagerShipmentsEnabled,
            IConnectionCollection connections,
            IPlayerManager players,
            IObjectManager objects)
        {
            var anyConfiguredFamily = configuredAmbientOutlawsEnabled ||
                                      configuredVillagerTradeEnabled ||
                                      configuredSettlementPatrolsEnabled ||
                                      configuredBattleDesertersEnabled;
            if (anyConfiguredFamily && !abiSmokeInstalling)
            {
                if (connections == null)
                    throw new ArgumentNullException("connections");
                if (players == null)
                    throw new ArgumentNullException("players");
                if (objects == null)
                    throw new ArgumentNullException("objects");
            }
            if (double.IsNaN(configuredRadiusBanditTravelDays) ||
                double.IsInfinity(configuredRadiusBanditTravelDays) ||
                configuredRadiusBanditTravelDays < 0.1d ||
                configuredRadiusBanditTravelDays > 30d)
            {
                throw new InvalidOperationException(
                    "Regional spawn radius must be between 0.1 and 30 bandit travel-days.");
            }
            if (configuredVillagerTradeEnabled && !virtualVillagerShipmentsEnabled)
            {
                throw new InvalidOperationException(
                    "Regional villager trade requires virtual villager shipments.");
            }

            lock (Sync)
            {
                if (installed)
                    return;

                radiusBanditTravelDays = configuredRadiusBanditTravelDays;
                ambientOutlawsEnabled = configuredAmbientOutlawsEnabled;
                villagerTradeEnabled = configuredVillagerTradeEnabled;
                settlementPatrolsEnabled = configuredSettlementPatrolsEnabled;
                battleDesertersEnabled = configuredBattleDesertersEnabled;
                connectionCollection = connections;
                playerManager = players;
                objectManager = objects;
                Counters.Clear();
                foreach (RegionalFamily family in Enum.GetValues(typeof(RegionalFamily)))
                    Counters.Add(family, new FamilyCounters());

                var harmony = new Harmony(HarmonyOwner);
                var patchedTargets = new List<MethodBase>();
                try
                {
                    // Resolve every enabled target before the first patch. A
                    // signature drift therefore fails startup before policy
                    // installation can become partial.
                    var targets = PreflightTargets();
                    ApplyPatches(harmony, targets, patchedTargets);
                    VerifyOwnedPatches(patchedTargets);
                    installed = true;
                    Console.WriteLine(
                        "[BCS Coop Bridge] Regional population policy loaded: radius=" +
                        radiusBanditTravelDays.ToString("0.###", CultureInfo.InvariantCulture) +
                        " bandit-travel-days, ambient-outlaws=" + FormatSwitch(ambientOutlawsEnabled) +
                        ", villager-trade=" + FormatSwitch(villagerTradeEnabled) +
                        ", settlement-patrols=" + FormatSwitch(settlementPatrolsEnabled) +
                        ", battle-deserters=" + FormatSwitch(battleDesertersEnabled) +
                        ". Settings are restart-bound; existing parties are untouched.");
                }
                catch (Exception exception)
                {
                    harmony.UnpatchAll(HarmonyOwner);
                    ClearRuntimeState();
                    Console.WriteLine(
                        "[BCS Coop Bridge] ERROR: Regional population policy startup failed: " +
                        exception);
                    throw;
                }
            }
        }

        // Installed-ABI smoke seam: applies every reviewed Harmony target while
        // deliberately avoiding campaign callbacks. Production installation
        // always arrives through the strongly typed Coop handler constructor.
        internal static void InstallForAbiSmoke()
        {
            SetAbiSmokeMode(true);
            try
            {
                Install(0.5d, true, true, true, true, true, null, null, null);
            }
            finally
            {
                SetAbiSmokeMode(false);
            }
        }

        internal static void SetAbiSmokeMode(bool enabled)
        {
            abiSmokeInstalling = enabled;
        }

        internal static void Uninstall()
        {
            lock (Sync)
            {
                new Harmony(HarmonyOwner).UnpatchAll(HarmonyOwner);
                ClearRuntimeState();
            }
        }

        internal static void RecordVirtualCycleScheduled()
        {
            lock (Sync)
                virtualCyclesScheduled++;
        }

        internal static void WriteDailySummary(int pendingVirtualCycles)
        {
            lock (Sync)
            {
                if (!installed || !AnyFamilyEnabled)
                    return;

                Console.WriteLine(
                    "[BCS Coop Bridge] Regional population daily: " +
                    FormatFamily("ambient-outlaws", RegionalFamily.AmbientOutlaws,
                        ambientOutlawsEnabled) + "; " +
                    FormatFamily("villager-trade", RegionalFamily.VillagerTrade,
                        villagerTradeEnabled) + "; " +
                    FormatFamily("settlement-patrols", RegionalFamily.SettlementPatrols,
                        settlementPatrolsEnabled) + "; " +
                    FormatFamily("battle-deserters", RegionalFamily.BattleDeserters,
                        battleDesertersEnabled) + "; virtual-scheduled=" +
                    virtualCyclesScheduled.ToString(CultureInfo.InvariantCulture) +
                    ", virtual-pending=" + Math.Max(0, pendingVirtualCycles)
                        .ToString(CultureInfo.InvariantCulture) +
                    ", patrol-timers-discarded=" +
                    patrolTimersDiscarded.ToString(CultureInfo.InvariantCulture) +
                    ", deserter-groups-suppressed=" +
                    deserterGroupsSuppressed.ToString(CultureInfo.InvariantCulture) + ".");

                foreach (var counter in Counters.Values)
                    counter.Reset();
                virtualCyclesScheduled = 0;
                patrolTimersDiscarded = 0;
                deserterGroupsSuppressed = 0;
            }
        }

        private static RegionalTargets PreflightTargets()
        {
            var targets = new RegionalTargets();
            if (ambientOutlawsEnabled)
            {
                targets.SpawnBanditParty = ResolveExactMethod(
                    typeof(BanditSpawnCampaignBehavior),
                    "SpawnBanditParty",
                    typeof(void),
                    new[] { typeof(Clan) });
                targets.SpawnLooterParty = ResolveExactMethod(
                    typeof(BanditSpawnCampaignBehavior),
                    "SpawnLooterParty",
                    typeof(void),
                    new[] { typeof(Clan), typeof(bool) });
                targets.SelectBanditHideout = ResolveExactMethod(
                    typeof(BanditSpawnCampaignBehavior),
                    "SelectBanditHideout",
                    typeof(Hideout),
                    new[] { typeof(Clan) });
                targets.SelectBanditFallback = ResolveExactMethod(
                    typeof(BanditSpawnCampaignBehavior),
                    "SelectAHideoutByCheckingCultureAndInfestedState",
                    typeof(Hideout),
                    new[] { typeof(Clan) });
                targets.SelectLooterSettlement = ResolveExactMethod(
                    typeof(BanditSpawnCampaignBehavior),
                    "SelectARandomSettlementForLooterParty",
                    typeof(Settlement),
                    new[] { typeof(bool) });
            }
            if (villagerTradeEnabled)
            {
                targets.ThinkAboutSendingItemToTown = ResolveExactMethod(
                    typeof(VillagerCampaignBehavior),
                    "ThinkAboutSendingItemToTown",
                    typeof(void),
                    new[] { typeof(Village) });
                targets.LoadAndSendVillagerParty = ResolveExactMethod(
                    typeof(VillagerCampaignBehavior),
                    "LoadAndSendVillagerParty",
                    typeof(void),
                    new[] { typeof(Village), typeof(MobileParty) });
            }
            if (settlementPatrolsEnabled)
            {
                targets.DailyTickSettlement = ResolveExactMethod(
                    typeof(PatrolPartiesCampaignBehavior),
                    "DailyTickSettlement",
                    typeof(void),
                    new[] { typeof(Settlement) });
                targets.UpdateSettlementQueue = ResolveExactMethod(
                    typeof(PatrolPartiesCampaignBehavior),
                    "UpdateSettlementQueue",
                    typeof(void),
                    new[] { typeof(Settlement), typeof(CampaignTime) });
                targets.SpawnPatrolParty = ResolveExactMethod(
                    typeof(PatrolPartiesCampaignBehavior),
                    "SpawnPatrolParty",
                    typeof(void),
                    new[] { typeof(Settlement) });
                patrolGenerationQueueField = typeof(PatrolPartiesCampaignBehavior).GetField(
                    "_partyGenerationQueue",
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (patrolGenerationQueueField == null ||
                    patrolGenerationQueueField.FieldType !=
                    typeof(Dictionary<Settlement, CampaignTime>))
                {
                    throw new MissingFieldException(
                        typeof(PatrolPartiesCampaignBehavior).FullName,
                        "_partyGenerationQueue with the required regional-patrol type");
                }
            }
            if (battleDesertersEnabled)
            {
                targets.SelectDeserterSettlements = ResolveExactMethod(
                    typeof(DesertersCampaignBehavior),
                    "SelectRandomSettlementsForDeserters",
                    typeof(List<Settlement>),
                    new[] { typeof(MapEvent), typeof(int) });
                targets.TrySpawnDeserters = ResolveExactMethod(
                    typeof(DesertersCampaignBehavior),
                    "TrySpawnDeserters",
                    typeof(void),
                    new[]
                    {
                        typeof(MapEvent),
                        typeof(TaleWorlds.CampaignSystem.Roster.TroopRoster)
                    });
            }
            return targets;
        }

        private static void ApplyPatches(
            Harmony harmony,
            RegionalTargets targets,
            ICollection<MethodBase> patchedTargets)
        {
            if (ambientOutlawsEnabled)
            {
                Patch(harmony, targets.SpawnBanditParty, patchedTargets,
                    prefix: HarmonyMethod(nameof(BeforeSpawnBanditParty), Priority.First),
                    finalizer: HarmonyMethod(nameof(FinishOutlawAttempt), Priority.Last));
                Patch(harmony, targets.SpawnLooterParty, patchedTargets,
                    prefix: HarmonyMethod(nameof(BeforeSpawnLooterParty), Priority.First),
                    finalizer: HarmonyMethod(nameof(FinishOutlawAttempt), Priority.Last));
                Patch(harmony, targets.SelectBanditHideout, patchedTargets,
                    postfix: HarmonyMethod(nameof(AfterSelectBanditOrigin), Priority.Last),
                    transpiler: HarmonyMethod(nameof(TranspileSelectBanditHideout), Priority.First));
                Patch(harmony, targets.SelectBanditFallback, patchedTargets,
                    transpiler: HarmonyMethod(nameof(TranspileSelectBanditFallback), Priority.First));
                Patch(harmony, targets.SelectLooterSettlement, patchedTargets,
                    postfix: HarmonyMethod(nameof(AfterSelectLooterOrigin), Priority.Last),
                    transpiler: HarmonyMethod(nameof(TranspileSelectLooterSettlement), Priority.First));
            }
            if (villagerTradeEnabled)
            {
                Patch(harmony, targets.ThinkAboutSendingItemToTown, patchedTargets,
                    transpiler: HarmonyMethod(nameof(TranspileVillagerDeparture), Priority.First),
                    finalizer: HarmonyMethod(nameof(FinishVillagerDeparture), Priority.Last));
                Patch(harmony, targets.LoadAndSendVillagerParty, patchedTargets,
                    prefix: HarmonyMethod(nameof(BeforeLoadAndSendVillagerParty), Priority.First),
                    postfix: HarmonyMethod(nameof(AfterLoadAndSendVillagerParty), Priority.Last),
                    finalizer: HarmonyMethod(nameof(FinishLoadAndSendVillagerParty), Priority.Last));
            }
            if (settlementPatrolsEnabled)
            {
                Patch(harmony, targets.DailyTickSettlement, patchedTargets,
                    prefix: HarmonyMethod(nameof(BeforePatrolDailyTick), Priority.First));
                Patch(harmony, targets.UpdateSettlementQueue, patchedTargets,
                    prefix: HarmonyMethod(nameof(BeforeUpdatePatrolQueue), Priority.First),
                    postfix: HarmonyMethod(nameof(AfterUpdatePatrolQueue), Priority.Last));
                Patch(harmony, targets.SpawnPatrolParty, patchedTargets,
                    prefix: HarmonyMethod(nameof(BeforeSpawnPatrolParty), Priority.First));
            }
            if (battleDesertersEnabled)
            {
                Patch(harmony, targets.SelectDeserterSettlements, patchedTargets,
                    transpiler: HarmonyMethod(nameof(TranspileDeserterSettlementSelection),
                        Priority.First));
                Patch(harmony, targets.TrySpawnDeserters, patchedTargets,
                    transpiler: HarmonyMethod(nameof(TranspileTrySpawnDeserters), Priority.First));
            }
        }

        private static void Patch(
            Harmony harmony,
            MethodInfo target,
            ICollection<MethodBase> patchedTargets,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null,
            HarmonyMethod finalizer = null)
        {
            harmony.Patch(target, prefix, postfix, transpiler, finalizer);
            patchedTargets.Add(target);
        }

        private static void VerifyOwnedPatches(IEnumerable<MethodBase> targets)
        {
            foreach (var target in targets)
            {
                var patchInfo = Harmony.GetPatchInfo(target);
                if (patchInfo == null || !patchInfo.Owners.Contains(HarmonyOwner))
                {
                    throw new InvalidOperationException(
                        "Regional population patch ownership verification failed for " +
                        target.DeclaringType.FullName + "." + target.Name + ".");
                }
            }
        }

        private static HarmonyMethod HarmonyMethod(string methodName, int priority)
        {
            var method = typeof(RegionalPopulationControl).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(typeof(RegionalPopulationControl).FullName,
                    methodName);
            return new HarmonyMethod(method) { priority = priority };
        }

        private static MethodInfo ResolveExactMethod(
            Type declaringType,
            string methodName,
            Type returnType,
            Type[] parameterTypes)
        {
            var matches = declaringType
                .GetMethods(BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, methodName, StringComparison.Ordinal) &&
                    method.ReturnType == returnType &&
                    ParametersMatch(method.GetParameters(), parameterTypes))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new MissingMethodException(
                    declaringType.FullName,
                    methodName + " with the required regional-population signature");
            }
            return matches[0];
        }

        private static bool ParametersMatch(ParameterInfo[] parameters, Type[] expected)
        {
            if (parameters.Length != expected.Length)
                return false;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType != expected[index])
                    return false;
            }
            return true;
        }

        private static bool BeforeSpawnBanditParty()
        {
            return BeginOutlawAttempt(
                Hideout.All.Select(hideout =>
                    hideout == null ? null : hideout.Settlement));
        }

        private static bool BeforeSpawnLooterParty()
        {
            return BeginOutlawAttempt(
                Settlement.All.Where(settlement =>
                    settlement != null && (settlement.IsTown || settlement.IsVillage)));
        }

        private static bool BeginOutlawAttempt(IEnumerable<Settlement> origins)
        {
            activeOutlawPlayerPositions = null;
            outlawOriginAdmitted = false;
            outlawOriginRejectedOutside = false;
            List<CampaignVec2> positions;
            if (!TryResolvePlayerPositions(out positions))
            {
                Record(RegionalFamily.AmbientOutlaws, RegionalOutcome.Missing);
                return false;
            }

            var sawValidOrigin = false;
            foreach (var origin in origins)
            {
                CampaignVec2 position;
                if (!TryGetOriginPosition(origin, out position))
                    continue;
                sawValidOrigin = true;
                if (IsInsideAnyRegion(position, positions))
                {
                    activeOutlawPlayerPositions = positions;
                    outlawOriginAdmitted = false;
                    return true;
                }
            }

            Record(
                RegionalFamily.AmbientOutlaws,
                sawValidOrigin ? RegionalOutcome.Outside : RegionalOutcome.Missing);
            return false;
        }

        private static Exception FinishOutlawAttempt(Exception __exception)
        {
            if (activeOutlawPlayerPositions != null)
            {
                if (outlawOriginAdmitted)
                    Record(RegionalFamily.AmbientOutlaws, RegionalOutcome.Admitted);
                else if (outlawOriginRejectedOutside)
                    Record(RegionalFamily.AmbientOutlaws, RegionalOutcome.Outside);
                else
                    RecordEvaluated(RegionalFamily.AmbientOutlaws);
            }
            activeOutlawPlayerPositions = null;
            outlawOriginAdmitted = false;
            outlawOriginRejectedOutside = false;
            return __exception;
        }

        private static void AfterSelectBanditOrigin(Hideout __result)
        {
            outlawOriginAdmitted = IsRegionalHideoutOrigin(__result);
        }

        private static void AfterSelectLooterOrigin(Settlement __result)
        {
            outlawOriginAdmitted = IsRegionalSettlementOrigin(__result);
        }

        private static bool IsRegionalHideoutOrigin(Hideout hideout)
        {
            return hideout != null &&
                   IsRegionalSettlementOrigin(hideout.Settlement);
        }

        private static bool IsRegionalSettlementOrigin(Settlement settlement)
        {
            CampaignVec2 position;
            if (activeOutlawPlayerPositions == null ||
                !TryGetOriginPosition(settlement, out position))
            {
                return false;
            }

            var admitted = IsInsideAnyRegion(position, activeOutlawPlayerPositions);
            if (!admitted)
                outlawOriginRejectedOutside = true;
            return admitted;
        }

        private static IEnumerable<CodeInstruction> TranspileSelectBanditHideout(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            return InsertOriginFilterBeforeOnlyAdd(
                instructions,
                generator,
                new CodeInstruction(OpCodes.Ldloc_2),
                GetPrivateMethod(nameof(IsRegionalHideoutOrigin)),
                "BanditSpawnCampaignBehavior.SelectBanditHideout");
        }

        private static IEnumerable<CodeInstruction> TranspileSelectBanditFallback(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var codes = instructions.ToList();
            var currentCalls = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsCallNamed(value.instruction, "get_Current"))
                .Select(value => value.index)
                .ToArray();
            var moveNextCalls = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsCallNamed(value.instruction, "MoveNext"))
                .Select(value => value.index)
                .ToArray();
            if (currentCalls.Length != 1 || moveNextCalls.Length != 1 ||
                currentCalls[0] + 2 >= codes.Count || moveNextCalls[0] < 1 ||
                codes[currentCalls[0] + 1].opcode != OpCodes.Stloc_S)
            {
                throw new InvalidOperationException(
                    "BanditSpawnCampaignBehavior.SelectAHideoutByCheckingCultureAndInfestedState " +
                    "IL shape changed (enumerator)." );
            }

            var insertionIndex = currentCalls[0] + 2;
            var loopNext = codes[moveNextCalls[0] - 1];
            var loopNextLabel = generator.DefineLabel();
            loopNext.labels.Add(loopNextLabel);
            var first = new CodeInstruction(OpCodes.Ldloc_S, (byte)4);
            first.labels.AddRange(codes[insertionIndex].labels);
            codes[insertionIndex].labels.Clear();
            first.blocks.AddRange(codes[insertionIndex].blocks);
            codes[insertionIndex].blocks.Clear();
            codes.InsertRange(
                insertionIndex,
                new[]
                {
                    first,
                    new CodeInstruction(OpCodes.Call,
                        GetPrivateMethod(nameof(IsRegionalHideoutOrigin))),
                    new CodeInstruction(OpCodes.Brfalse, loopNextLabel)
                });
            return codes;
        }

        private static IEnumerable<CodeInstruction> TranspileSelectLooterSettlement(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            return InsertOriginFilterBeforeOnlyAdd(
                instructions,
                generator,
                new CodeInstruction(OpCodes.Ldloc_2),
                GetPrivateMethod(nameof(IsRegionalSettlementOrigin)),
                "BanditSpawnCampaignBehavior.SelectARandomSettlementForLooterParty");
        }

        private static IEnumerable<CodeInstruction> InsertOriginFilterBeforeOnlyAdd(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            CodeInstruction loadCandidate,
            MethodInfo predicate,
            string description)
        {
            var codes = instructions.ToList();
            var addIndexes = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsListAdd(value.instruction))
                .Select(value => value.index)
                .ToArray();
            if (addIndexes.Length != 1)
                throw new InvalidOperationException(description + " IL shape changed (Add).");

            var addIndex = addIndexes[0];
            var insertionIndex = -1;
            for (var index = addIndex - 1; index >= 0; index--)
            {
                if (codes[index].opcode == OpCodes.Ldloc_0)
                {
                    insertionIndex = index;
                    break;
                }
            }
            if (insertionIndex < 0 || addIndex + 1 >= codes.Count)
                throw new InvalidOperationException(description + " IL shape changed (candidate).");

            var loopNext = codes[addIndex + 1];
            var loopNextLabel = generator.DefineLabel();
            loopNext.labels.Add(loopNextLabel);
            var first = loadCandidate;
            first.labels.AddRange(codes[insertionIndex].labels);
            codes[insertionIndex].labels.Clear();
            first.blocks.AddRange(codes[insertionIndex].blocks);
            codes[insertionIndex].blocks.Clear();
            codes.InsertRange(
                insertionIndex,
                new[]
                {
                    first,
                    new CodeInstruction(OpCodes.Call, predicate),
                    new CodeInstruction(OpCodes.Brfalse, loopNextLabel)
                });
            return codes;
        }

        private static bool IsListAdd(CodeInstruction instruction)
        {
            var method = instruction.operand as MethodInfo;
            return method != null &&
                   string.Equals(method.Name, "Add", StringComparison.Ordinal) &&
                   method.DeclaringType != null &&
                   method.DeclaringType.IsGenericType &&
                   method.DeclaringType.GetGenericTypeDefinition() == typeof(List<>);
        }

        private static IEnumerable<CodeInstruction> TranspileVillagerDeparture(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var codes = instructions.ToList();
            var createCalls = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsCallTo(
                    value.instruction,
                    typeof(VillagerCampaignBehavior),
                    "CreateVillagerParty"))
                .Select(value => value.index)
                .ToArray();
            if (createCalls.Length != 1)
            {
                throw new InvalidOperationException(
                    "VillagerCampaignBehavior.ThinkAboutSendingItemToTown IL shape changed (create)." );
            }
            var loadAndSendCalls = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsCallTo(
                    value.instruction,
                    typeof(VillagerCampaignBehavior),
                    "LoadAndSendVillagerParty"))
                .Select(value => value.index)
                .ToArray();
            if (loadAndSendCalls.Length != 1)
            {
                throw new InvalidOperationException(
                    "VillagerCampaignBehavior.ThinkAboutSendingItemToTown IL shape changed (load)." );
            }

            var createCall = createCalls[0];
            var loadAndSendCall = loadAndSendCalls[0];
            var createAnchor = createCall - 2;
            var loadAndSendAnchor = loadAndSendCall - 3;
            var finalReturn = codes.LastOrDefault(code => code.opcode == OpCodes.Ret);
            var minimumPartySizeCall = codes.FindIndex(instruction =>
                IsCallNamed(
                    instruction,
                    "TaleWorlds.CampaignSystem.ComponentInterfaces.PartySizeLimitModel",
                    "get_MinimumNumberOfVillagersAtVillagerParty"));
            var hearthCall = codes.FindIndex(instruction =>
                IsCallNamed(
                    instruction,
                    typeof(Village).FullName,
                    "get_Hearth"));
            var hearthGuard = minimumPartySizeCall < 0 ||
                              createAnchor <= minimumPartySizeCall + 1
                ? -1
                : codes.FindIndex(
                    minimumPartySizeCall + 1,
                    createAnchor - minimumPartySizeCall - 1,
                    instruction => instruction.opcode == OpCodes.Ble_Un ||
                                   instruction.opcode == OpCodes.Ble_Un_S);
            if (finalReturn == null ||
                hearthCall < 0 ||
                minimumPartySizeCall <= hearthCall ||
                hearthGuard <= minimumPartySizeCall ||
                createAnchor <= hearthGuard ||
                createAnchor < 0 ||
                codes[createAnchor].opcode != OpCodes.Ldarg_0 ||
                codes[createAnchor + 1].opcode != OpCodes.Ldarg_1 ||
                loadAndSendAnchor <= createCall ||
                codes[loadAndSendAnchor].opcode != OpCodes.Ldarg_0 ||
                codes[loadAndSendAnchor + 1].opcode != OpCodes.Ldarg_1 ||
                codes[loadAndSendAnchor + 2].opcode != OpCodes.Ldloc_0 ||
                (codes[createCall + 1].opcode != OpCodes.Br &&
                 codes[createCall + 1].opcode != OpCodes.Br_S))
            {
                throw new InvalidOperationException(
                    "VillagerCampaignBehavior.ThinkAboutSendingItemToTown IL shape changed (departure)." );
            }
            var finalReturnLabel = generator.DefineLabel();
            finalReturn.labels.Add(finalReturnLabel);

            InsertVillagerAdmissionGuard(
                codes,
                loadAndSendAnchor,
                finalReturnLabel,
                GetPrivateMethod(nameof(TryAdmitVillagerLoadFromThink)));
            codes.InsertRange(
                createCall + 1,
                new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(
                        OpCodes.Call,
                        GetPrivateMethod(nameof(LatchCreatedVillagerParty)))
                });
            InsertVillagerAdmissionGuard(
                codes,
                createAnchor,
                finalReturnLabel,
                GetPrivateMethod(nameof(TryAdmitVillagerCreation)));
            return codes;
        }

        private static void InsertVillagerAdmissionGuard(
            IList<CodeInstruction> codes,
            int insertionIndex,
            Label finalReturnLabel,
            MethodInfo predicate)
        {
            var first = new CodeInstruction(OpCodes.Ldarg_1);
            first.labels.AddRange(codes[insertionIndex].labels);
            codes[insertionIndex].labels.Clear();
            first.blocks.AddRange(codes[insertionIndex].blocks);
            codes[insertionIndex].blocks.Clear();
            codes.Insert(
                insertionIndex,
                new CodeInstruction(OpCodes.Brfalse, finalReturnLabel));
            codes.Insert(
                insertionIndex,
                new CodeInstruction(OpCodes.Call, predicate));
            codes.Insert(insertionIndex, first);
        }

        private static bool IsCallNamed(
            CodeInstruction instruction,
            string declaringTypeName,
            string methodName)
        {
            var method = instruction.operand as MethodInfo;
            return method != null &&
                   string.Equals(method.Name, methodName, StringComparison.Ordinal) &&
                   method.DeclaringType != null &&
                   string.Equals(
                       method.DeclaringType.FullName,
                       declaringTypeName,
                       StringComparison.Ordinal);
        }

        private static bool TryAdmitVillagerCreation(Village village)
        {
            var behavior = GetEconomyBehavior();
            if (behavior != null && behavior.HasPendingVirtualShipment(village))
                return false;

            RemoveStaleVillagerDepartureLatch(village, behavior);

            var outcome = EvaluateOrigin(village == null ? null : village.Settlement);
            if (outcome == RegionalOutcome.Admitted)
            {
                Record(RegionalFamily.VillagerTrade, RegionalOutcome.Admitted);
                return true;
            }
            if (outcome == RegionalOutcome.Outside && behavior != null)
                behavior.TryScheduleVirtualShipment(village);
            else if (outcome == RegionalOutcome.Outside)
                outcome = RegionalOutcome.Missing;
            Record(RegionalFamily.VillagerTrade, outcome);
            return false;
        }

        private static void LatchCreatedVillagerParty(Village village)
        {
            var behavior = GetEconomyBehavior();
            if (HasLiveVillagerParty(village) &&
                (behavior == null || !behavior.HasPendingVirtualShipment(village)))
            {
                AddVillagerDepartureLatch(village);
                return;
            }
            RemoveVillagerDepartureLatch(village);
        }

        private static bool TryAdmitVillagerLoadFromThink(Village village)
        {
            var admitted = TryAdmitVillagerLoad(village);
            admittedVillagerDeparture = admitted ? village : null;
            return admitted;
        }

        private static bool TryAdmitVillagerLoad(Village village)
        {
            var behavior = GetEconomyBehavior();
            if (behavior != null && behavior.HasPendingVirtualShipment(village))
            {
                RememberLiveVillagerParty(village);
                RemoveVillagerDepartureLatch(village);
                return false;
            }

            if (HasVillagerDepartureLatch(village) ||
                TryRecoverVillagerDepartureLatch(village))
                return true;

            var outcome = EvaluateOrigin(village == null ? null : village.Settlement);
            if (outcome == RegionalOutcome.Admitted)
            {
                AddVillagerDepartureLatch(village);
                Record(RegionalFamily.VillagerTrade, RegionalOutcome.Admitted);
                return true;
            }
            if (outcome == RegionalOutcome.Outside && behavior != null)
                behavior.TryScheduleVirtualShipment(village);
            else if (outcome == RegionalOutcome.Outside)
                outcome = RegionalOutcome.Missing;
            Record(RegionalFamily.VillagerTrade, outcome);
            return false;
        }

        private static bool BeforeLoadAndSendVillagerParty(
            Village __0,
            out VillagerLoadPatchState __state)
        {
            var admittedByThink = ReferenceEquals(admittedVillagerDeparture, __0);
            admittedVillagerDeparture = null;
            var admitted = admittedByThink || TryAdmitVillagerLoad(__0);
            __state = admitted ? new VillagerLoadPatchState(__0) : null;
            return admitted;
        }

        private static void AfterLoadAndSendVillagerParty(
            bool __runOriginal,
            VillagerLoadPatchState __state)
        {
            admittedVillagerDeparture = null;
            if (__runOriginal && __state != null)
                RemoveVillagerDepartureLatch(__state.Village);
        }

        private static Exception FinishLoadAndSendVillagerParty(
            Exception __exception,
            VillagerLoadPatchState __state)
        {
            admittedVillagerDeparture = null;
            return __exception;
        }

        private static Exception FinishVillagerDeparture(Exception __exception)
        {
            admittedVillagerDeparture = null;
            return __exception;
        }

        private static void BeforePatrolDailyTick(
            PatrolPartiesCampaignBehavior __instance,
            Settlement __0)
        {
            var queue = CapturePatrolQueue(__instance);
            if (queue == null || __0 == null || !queue.ContainsKey(__0))
                return;
            var outcome = EvaluateOrigin(__0);
            if (outcome == RegionalOutcome.Admitted)
            {
                trackedPatrolTimers.Add(__0);
                return;
            }
            DiscardPatrolTimer(queue, __0);
        }

        private static bool BeforeUpdatePatrolQueue(
            PatrolPartiesCampaignBehavior __instance,
            Settlement __0)
        {
            var queue = CapturePatrolQueue(__instance);
            var outcome = EvaluateOrigin(__0);
            Record(RegionalFamily.SettlementPatrols, outcome);
            if (outcome != RegionalOutcome.Admitted)
                DiscardPatrolTimer(queue, __0);
            return outcome == RegionalOutcome.Admitted;
        }

        private static void AfterUpdatePatrolQueue(
            PatrolPartiesCampaignBehavior __instance,
            Settlement __0)
        {
            var queue = CapturePatrolQueue(__instance);
            if (queue != null && __0 != null && queue.ContainsKey(__0))
                trackedPatrolTimers.Add(__0);
        }

        private static bool BeforeSpawnPatrolParty(
            PatrolPartiesCampaignBehavior __instance,
            Settlement __0)
        {
            var queue = CapturePatrolQueue(__instance);
            var outcome = EvaluateOrigin(__0);
            Record(RegionalFamily.SettlementPatrols, outcome);
            if (outcome == RegionalOutcome.Admitted)
            {
                if (queue != null && __0 != null && queue.ContainsKey(__0))
                    trackedPatrolTimers.Add(__0);
                return true;
            }
            DiscardPatrolTimer(queue, __0);
            return false;
        }

        internal static void SweepPatrolRegionTransitions()
        {
            if (!installed || !settlementPatrolsEnabled)
                return;

            var campaign = Campaign.Current;
            var behavior = campaign == null
                ? null
                : campaign.GetCampaignBehavior<PatrolPartiesCampaignBehavior>();
            var queue = CapturePatrolQueue(behavior);
            if (queue == null)
                return;

            if (!patrolTrackingInitialized)
            {
                patrolTrackingInitialized = true;
                List<CampaignVec2> initialPositions;
                float initialRadius = 0f;
                var hasInitialRegion =
                    TryResolvePlayerPositions(out initialPositions) &&
                    TryGetMapRadius(out initialRadius);
                var initialRadiusSquared = hasInitialRegion
                    ? initialRadius * initialRadius
                    : 0f;
                foreach (var settlement in queue.Keys.ToArray())
                {
                    var outcome = EvaluateOriginAgainstSnapshot(
                        settlement,
                        hasInitialRegion,
                        initialPositions,
                        initialRadiusSquared);
                    if (outcome == RegionalOutcome.Admitted)
                    {
                        trackedPatrolTimers.Add(settlement);
                    }
                    else
                    {
                        DiscardPatrolTimer(queue, settlement);
                    }
                }
                return;
            }

            if (trackedPatrolTimers.Count == 0)
                return;

            List<CampaignVec2> positions;
            float radius = 0f;
            var hasRegion = TryResolvePlayerPositions(out positions) &&
                            TryGetMapRadius(out radius);
            var radiusSquared = hasRegion ? radius * radius : 0f;
            foreach (var settlement in trackedPatrolTimers.ToArray())
            {
                if (!queue.ContainsKey(settlement))
                {
                    trackedPatrolTimers.Remove(settlement);
                    continue;
                }

                var outcome = EvaluateOriginAgainstSnapshot(
                    settlement,
                    hasRegion,
                    positions,
                    radiusSquared);

                if (outcome == RegionalOutcome.Admitted)
                    continue;
                DiscardPatrolTimer(queue, settlement);
            }
        }

        internal static void SweepVillagerDepartureLatches()
        {
            if (!installed || !villagerTradeEnabled)
                return;

            var behavior = GetEconomyBehavior();
            Village[] villages;
            lock (Sync)
            {
                EnsureVillagerCampaignLocked();
                villages = pendingPhysicalVillagerDepartures.ToArray();
            }
            foreach (var village in villages)
                RemoveStaleVillagerDepartureLatch(village, behavior);
        }

        private static bool HasVillagerDepartureLatch(Village village)
        {
            var party = GetLiveVillagerParty(village);
            if (party == null)
            {
                RemoveVillagerDepartureState(village);
                return false;
            }
            lock (Sync)
            {
                EnsureVillagerCampaignLocked();
                MobileParty trackedParty;
                if (!pendingPhysicalVillagerDepartures.Contains(village) ||
                    !trackedVillagerParties.TryGetValue(village, out trackedParty) ||
                    !ReferenceEquals(trackedParty, party))
                {
                    pendingPhysicalVillagerDepartures.Remove(village);
                    return false;
                }
                return true;
            }
        }

        private static void AddVillagerDepartureLatch(Village village)
        {
            var party = GetLiveVillagerParty(village);
            if (party == null)
            {
                RemoveVillagerDepartureState(village);
                return;
            }
            lock (Sync)
            {
                if (EnsureVillagerCampaignLocked())
                {
                    trackedVillagerParties[village] = party;
                    pendingPhysicalVillagerDepartures.Add(village);
                }
            }
        }

        private static bool TryRecoverVillagerDepartureLatch(Village village)
        {
            var party = GetLiveVillagerParty(village);
            if (party == null)
            {
                RemoveVillagerDepartureState(village);
                return false;
            }
            lock (Sync)
            {
                if (!EnsureVillagerCampaignLocked())
                    return false;
                MobileParty trackedParty;
                if (trackedVillagerParties.TryGetValue(village, out trackedParty) &&
                    ReferenceEquals(trackedParty, party))
                {
                    return false;
                }
                trackedVillagerParties[village] = party;
                pendingPhysicalVillagerDepartures.Add(village);
                return true;
            }
        }

        private static void RememberLiveVillagerParty(Village village)
        {
            var party = GetLiveVillagerParty(village);
            if (party == null)
            {
                RemoveVillagerDepartureState(village);
                return;
            }
            lock (Sync)
            {
                if (EnsureVillagerCampaignLocked())
                    trackedVillagerParties[village] = party;
            }
        }

        private static void RemoveVillagerDepartureLatch(Village village)
        {
            if (village == null)
                return;
            lock (Sync)
                pendingPhysicalVillagerDepartures.Remove(village);
        }

        private static void RemoveVillagerDepartureState(Village village)
        {
            if (village == null)
                return;
            lock (Sync)
            {
                pendingPhysicalVillagerDepartures.Remove(village);
                trackedVillagerParties.Remove(village);
            }
        }

        private static void RemoveStaleVillagerDepartureLatch(
            Village village,
            BridgeEconomyCampaignBehavior behavior)
        {
            var party = GetLiveVillagerParty(village);
            if (party == null)
            {
                RemoveVillagerDepartureState(village);
                return;
            }
            var hasPendingVirtual = behavior != null &&
                                    behavior.HasPendingVirtualShipment(village);
            lock (Sync)
            {
                EnsureVillagerCampaignLocked();
                MobileParty trackedParty;
                if (trackedVillagerParties.TryGetValue(village, out trackedParty) &&
                    !ReferenceEquals(trackedParty, party))
                {
                    pendingPhysicalVillagerDepartures.Remove(village);
                    trackedVillagerParties.Remove(village);
                }
                if (hasPendingVirtual)
                {
                    pendingPhysicalVillagerDepartures.Remove(village);
                    trackedVillagerParties[village] = party;
                }
            }
        }

        private static bool HasLiveVillagerParty(Village village)
        {
            return GetLiveVillagerParty(village) != null;
        }

        private static MobileParty GetLiveVillagerParty(Village village)
        {
            var component = village == null ? null : village.VillagerPartyComponent;
            var party = component == null ? null : component.MobileParty;
            return party != null && party.IsActive ? party : null;
        }

        private static bool EnsureVillagerCampaignLocked()
        {
            var campaign = Campaign.Current;
            if (!ReferenceEquals(trackedVillagerCampaign, campaign))
            {
                trackedVillagerCampaign = campaign;
                pendingPhysicalVillagerDepartures.Clear();
                trackedVillagerParties.Clear();
            }
            return campaign != null;
        }

        private static RegionalOutcome EvaluateOriginAgainstSnapshot(
            Settlement settlement,
            bool hasRegion,
            IEnumerable<CampaignVec2> positions,
            float radiusSquared)
        {
            CampaignVec2 origin;
            if (!TryGetOriginPosition(settlement, out origin) || !hasRegion)
                return RegionalOutcome.Missing;
            return IsInsideAnyRegion(origin, positions, radiusSquared)
                ? RegionalOutcome.Admitted
                : RegionalOutcome.Outside;
        }

        private static Dictionary<Settlement, CampaignTime> CapturePatrolQueue(
            PatrolPartiesCampaignBehavior behavior)
        {
            if (!ReferenceEquals(trackedPatrolBehavior, behavior))
            {
                trackedPatrolBehavior = behavior;
                trackedPatrolTimers.Clear();
                patrolTrackingInitialized = false;
            }
            return GetPatrolQueue(behavior);
        }

        private static void DiscardPatrolTimer(
            IDictionary<Settlement, CampaignTime> queue,
            Settlement settlement)
        {
            trackedPatrolTimers.Remove(settlement);
            if (queue == null || settlement == null || !queue.Remove(settlement))
                return;
            lock (Sync)
                patrolTimersDiscarded++;
        }

        private static Dictionary<Settlement, CampaignTime> GetPatrolQueue(
            PatrolPartiesCampaignBehavior behavior)
        {
            return behavior == null || patrolGenerationQueueField == null
                ? null
                : patrolGenerationQueueField.GetValue(behavior) as
                    Dictionary<Settlement, CampaignTime>;
        }

        private static IEnumerable<CodeInstruction> TranspileDeserterSettlementSelection(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var codes = instructions.ToList();
            var calls = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsCallNamed(value.instruction, "FindSettlementsAroundPoint"))
                .Select(value => value.index)
                .ToArray();
            if (calls.Length != 1 || calls[0] + 2 >= codes.Count ||
                codes[calls[0] + 1].opcode != OpCodes.Stloc_0)
            {
                throw new InvalidOperationException(
                    "DesertersCampaignBehavior.SelectRandomSettlementsForDeserters IL shape changed." );
            }

            var insertionIndex = calls[0] + 2;
            var continueInstruction = codes[insertionIndex];
            var continueLabel = generator.DefineLabel();
            continueInstruction.labels.Add(continueLabel);
            codes.InsertRange(
                insertionIndex,
                new[]
                {
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Ldarg_2),
                    new CodeInstruction(OpCodes.Call,
                        GetPrivateMethod(nameof(FilterDeserterCandidates))),
                    new CodeInstruction(OpCodes.Brtrue, continueLabel),
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Ret)
                });
            return codes;
        }

        private static bool FilterDeserterCandidates(
            List<Settlement> candidates,
            int requestedCount)
        {
            List<CampaignVec2> positions;
            if (candidates == null || !TryResolvePlayerPositions(out positions))
            {
                if (candidates != null)
                    candidates.Clear();
                Record(RegionalFamily.BattleDeserters, RegionalOutcome.Missing);
                RecordDeserterSuppression(requestedCount);
                return false;
            }

            var sawValidOrigin = false;
            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                CampaignVec2 position;
                if (!TryGetOriginPosition(candidates[index], out position))
                {
                    candidates.RemoveAt(index);
                    continue;
                }
                sawValidOrigin = true;
                if (!IsInsideAnyRegion(position, positions))
                    candidates.RemoveAt(index);
            }
            if (candidates.Count > 0)
            {
                Record(RegionalFamily.BattleDeserters, RegionalOutcome.Admitted);
                return true;
            }

            Record(
                RegionalFamily.BattleDeserters,
                sawValidOrigin ? RegionalOutcome.Outside : RegionalOutcome.Missing);
            RecordDeserterSuppression(requestedCount);
            return false;
        }

        private static void RecordDeserterSuppression(int requestedCount)
        {
            lock (Sync)
                deserterGroupsSuppressed += Math.Max(0, requestedCount);
        }

        private static IEnumerable<CodeInstruction> TranspileTrySpawnDeserters(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var codes = instructions.ToList();
            var calls = codes
                .Select((instruction, index) => new { instruction, index })
                .Where(value => IsCallTo(
                    value.instruction,
                    typeof(DesertersCampaignBehavior),
                    "SelectRandomSettlementsForDeserters"))
                .Select(value => value.index)
                .ToArray();
            if (calls.Length != 1 || calls[0] + 2 >= codes.Count ||
                codes[calls[0] + 1].opcode != OpCodes.Stloc_2)
            {
                throw new InvalidOperationException(
                    "DesertersCampaignBehavior.TrySpawnDeserters IL shape changed." );
            }

            var insertionIndex = calls[0] + 2;
            var continueInstruction = codes[insertionIndex];
            var continueLabel = generator.DefineLabel();
            continueInstruction.labels.Add(continueLabel);
            codes.InsertRange(
                insertionIndex,
                new[]
                {
                    new CodeInstruction(OpCodes.Ldloc_2),
                    new CodeInstruction(OpCodes.Call,
                        GetPrivateMethod(nameof(HasDeserterCandidates))),
                    new CodeInstruction(OpCodes.Brtrue, continueLabel),
                    new CodeInstruction(OpCodes.Ret)
                });
            return codes;
        }

        private static bool HasDeserterCandidates(List<Settlement> candidates)
        {
            return candidates != null && candidates.Count > 0;
        }

        private static RegionalOutcome EvaluateOrigin(Settlement origin)
        {
            CampaignVec2 originPosition;
            if (!TryGetOriginPosition(origin, out originPosition))
                return RegionalOutcome.Missing;
            List<CampaignVec2> positions;
            if (!TryResolvePlayerPositions(out positions))
                return RegionalOutcome.Missing;
            return IsInsideAnyRegion(originPosition, positions)
                ? RegionalOutcome.Admitted
                : RegionalOutcome.Outside;
        }

        private static bool TryResolvePlayerPositions(out List<CampaignVec2> positions)
        {
            positions = new List<CampaignVec2>();
            var connections = connectionCollection;
            var players = playerManager;
            var objects = objectManager;
            if (connections == null || players == null || objects == null)
                return false;

            foreach (var connection in connections)
            {
                if (connection == null ||
                    !(connection.State is CampaignState) &&
                    !(connection.State is MissionState))
                {
                    continue;
                }

                GameInterface.Services.Players.Data.Player player;
                if (!players.TryGetPlayer(connection.Peer, out player) ||
                    player == null || !players.IsConnected(player) ||
                    string.IsNullOrWhiteSpace(player.MobilePartyId))
                {
                    continue;
                }

                MobileParty party;
                if (!objects.TryGetObject<MobileParty>(player.MobilePartyId, out party) ||
                    party == null ||
                    !party.IsActive)
                {
                    continue;
                }
                var position = party.Position;
                if (position.IsValid())
                    positions.Add(position);
            }
            return positions.Count > 0;
        }

        private static bool TryGetOriginPosition(
            Settlement settlement,
            out CampaignVec2 position)
        {
            position = default(CampaignVec2);
            if (settlement == null)
                return false;
            position = settlement.Position;
            return position.IsValid();
        }

        private static bool IsInsideAnyRegion(
            CampaignVec2 origin,
            IEnumerable<CampaignVec2> playerPositions)
        {
            float radius;
            if (!TryGetMapRadius(out radius))
                return false;
            var radiusSquared = radius * radius;
            return IsInsideAnyRegion(origin, playerPositions, radiusSquared);
        }

        private static bool IsInsideAnyRegion(
            CampaignVec2 origin,
            IEnumerable<CampaignVec2> playerPositions,
            float radiusSquared)
        {
            foreach (var playerPosition in playerPositions)
            {
                if (origin.DistanceSquared(playerPosition) <= radiusSquared)
                    return true;
            }
            return false;
        }

        private static bool TryGetMapRadius(out float radius)
        {
            radius = 0f;
            var campaign = Campaign.Current;
            if (campaign == null)
                return false;
            var speed = campaign.EstimatedAverageBanditPartySpeed;
            var computed = radiusBanditTravelDays * speed * CampaignTime.HoursInDay;
            if (computed <= 0d || double.IsNaN(computed) ||
                double.IsInfinity(computed) || computed >= float.MaxValue)
            {
                return false;
            }
            radius = (float)computed;
            return true;
        }

        private static BridgeEconomyCampaignBehavior GetEconomyBehavior()
        {
            var campaign = Campaign.Current;
            return campaign == null
                ? null
                : campaign.GetCampaignBehavior<BridgeEconomyCampaignBehavior>();
        }

        private static MethodInfo GetPrivateMethod(string name)
        {
            var method = typeof(RegionalPopulationControl).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(typeof(RegionalPopulationControl).FullName, name);
            return method;
        }

        private static bool IsCallTo(
            CodeInstruction instruction,
            Type declaringType,
            string name)
        {
            var method = instruction.operand as MethodInfo;
            return method != null && method.DeclaringType == declaringType &&
                   string.Equals(method.Name, name, StringComparison.Ordinal);
        }

        private static bool IsCallNamed(CodeInstruction instruction, string name)
        {
            var method = instruction.operand as MethodInfo;
            return method != null &&
                   string.Equals(method.Name, name, StringComparison.Ordinal);
        }

        private static void Record(RegionalFamily family, RegionalOutcome outcome)
        {
            lock (Sync)
            {
                FamilyCounters counter;
                if (!Counters.TryGetValue(family, out counter))
                    return;
                counter.Evaluated++;
                switch (outcome)
                {
                    case RegionalOutcome.Admitted:
                        counter.Admitted++;
                        break;
                    case RegionalOutcome.Outside:
                        counter.Outside++;
                        break;
                    default:
                        counter.Missing++;
                        break;
                }
            }
        }

        private static void RecordEvaluated(RegionalFamily family)
        {
            lock (Sync)
            {
                FamilyCounters counter;
                if (Counters.TryGetValue(family, out counter))
                    counter.Evaluated++;
            }
        }

        private static string FormatFamily(
            string name,
            RegionalFamily family,
            bool enabled)
        {
            if (!enabled)
                return name + "=disabled";
            var counter = Counters[family];
            return name + "={evaluated=" +
                   counter.Evaluated.ToString(CultureInfo.InvariantCulture) +
                   ", admitted=" + counter.Admitted.ToString(CultureInfo.InvariantCulture) +
                   ", outside=" + counter.Outside.ToString(CultureInfo.InvariantCulture) +
                   ", missing=" + counter.Missing.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string FormatSwitch(bool enabled)
        {
            return enabled ? "enabled" : "disabled";
        }

        private static void ClearRuntimeState()
        {
            installed = false;
            ambientOutlawsEnabled = false;
            villagerTradeEnabled = false;
            settlementPatrolsEnabled = false;
            battleDesertersEnabled = false;
            connectionCollection = null;
            playerManager = null;
            objectManager = null;
            patrolGenerationQueueField = null;
            trackedPatrolBehavior = null;
            trackedPatrolTimers.Clear();
            patrolTrackingInitialized = false;
            activeOutlawPlayerPositions = null;
            outlawOriginAdmitted = false;
            outlawOriginRejectedOutside = false;
            admittedVillagerDeparture = null;
            trackedVillagerCampaign = null;
            pendingPhysicalVillagerDepartures.Clear();
            trackedVillagerParties.Clear();
            Counters.Clear();
            virtualCyclesScheduled = 0;
            patrolTimersDiscarded = 0;
            deserterGroupsSuppressed = 0;
        }

        private enum RegionalFamily
        {
            AmbientOutlaws,
            VillagerTrade,
            SettlementPatrols,
            BattleDeserters
        }

        private enum RegionalOutcome
        {
            Admitted,
            Outside,
            Missing
        }

        private sealed class FamilyCounters
        {
            internal long Evaluated;
            internal long Admitted;
            internal long Outside;
            internal long Missing;

            internal void Reset()
            {
                Evaluated = 0;
                Admitted = 0;
                Outside = 0;
                Missing = 0;
            }
        }

        private sealed class VillagerLoadPatchState
        {
            internal VillagerLoadPatchState(Village village)
            {
                Village = village;
            }

            internal Village Village { get; private set; }
        }

        private sealed class RegionalTargets
        {
            internal MethodInfo SpawnBanditParty;
            internal MethodInfo SpawnLooterParty;
            internal MethodInfo SelectBanditHideout;
            internal MethodInfo SelectBanditFallback;
            internal MethodInfo SelectLooterSettlement;
            internal MethodInfo ThinkAboutSendingItemToTown;
            internal MethodInfo LoadAndSendVillagerParty;
            internal MethodInfo DailyTickSettlement;
            internal MethodInfo UpdateSettlementQueue;
            internal MethodInfo SpawnPatrolParty;
            internal MethodInfo SelectDeserterSettlements;
            internal MethodInfo TrySpawnDeserters;
        }
    }
}
#endif
