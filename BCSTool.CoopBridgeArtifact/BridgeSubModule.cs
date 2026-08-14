#if BCS_SERVER
using Common;
using Common.Messaging;
using GameInterface.Services.Modules;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
#endif
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
#if !BCS_SERVER
using System.Reflection.Emit;
#endif
using System.Security.Cryptography;
using System.Text;
using System.Xml;
#if !BCS_SERVER
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
#endif
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

[assembly: AssemblyVersion("0.6.67.0")]
[assembly: AssemblyFileVersion("0.6.67.0")]
[assembly: AssemblyInformationalVersion("0.6.67")]

namespace BCS.CoopBridge
{
    public sealed class BridgeSubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            BridgeRuntime.RecordStartupProgress("OnSubModuleLoad entered");
            try
            {
                BridgeRuntime.ValidateInstalledPackage();
                BridgeRuntime.RecordStartupProgress("OnSubModuleLoad completed");
            }
            catch (Exception exception)
            {
                BridgeRuntime.RecordStartupFailure(exception);
                throw;
            }
        }
    }

#if !BCS_SERVER
    internal static class ClientMapEventCompatibility
    {
        internal static void Install(string bridgeId)
        {
            var leaderTarget = typeof(MapEvent).GetMethod(
                "GetLeaderParty",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(BattleSideEnum) },
                null);
            if (leaderTarget == null)
                throw new MissingMethodException(typeof(MapEvent).FullName, "GetLeaderParty(BattleSideEnum)");
            var leaderPrefix = new HarmonyMethod(
                typeof(ClientMapEventCompatibility),
                nameof(BeforeGetLeaderParty))
            {
                priority = Priority.First
            };
            var involvedMenTarget = typeof(MapEvent).GetMethod(
                "GetNumberOfInvolvedMen",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(BattleSideEnum) },
                null);
            if (involvedMenTarget == null)
                throw new MissingMethodException(typeof(MapEvent).FullName, "GetNumberOfInvolvedMen(BattleSideEnum)");
            var involvedMenPrefix = new HarmonyMethod(
                typeof(ClientMapEventCompatibility),
                nameof(BeforeGetNumberOfInvolvedMen))
            {
                priority = Priority.First
            };
            var harmony = new Harmony(bridgeId + ".client-map-event-compatibility");
            harmony.Patch(leaderTarget, prefix: leaderPrefix);
            harmony.Patch(involvedMenTarget, prefix: involvedMenPrefix);
        }

        private static bool BeforeGetLeaderParty(BattleSideEnum side, ref PartyBase __result)
        {
            if (side == BattleSideEnum.Attacker || side == BattleSideEnum.Defender)
                return true;

            // Native menu conditions use None when the player is not part of the encounter.
            // The current Coop postfix indexes MapEvent._sides before that native null result
            // can be observed, so preserve the native null contract without invoking the postfix.
            __result = null;
            return false;
        }

        private static bool BeforeGetNumberOfInvolvedMen(
            BattleSideEnum side,
            ref int __result)
        {
            if (side == BattleSideEnum.Attacker || side == BattleSideEnum.Defender)
                return true;

            // A newly joined Coop party is not part of a map event yet and therefore
            // legitimately reports None. Native EncounterLeaveConsequence forwards
            // that side into MapEvent._sides; preserve the intended empty-side result.
            __result = 0;
            return false;
        }
    }

    internal static class ClientMapEventPositionAuthority
    {
        private static readonly object Sync = new object();
        private static Func<IDisposable> createAllowedScope;
        private static Action<object, object> removeInvolvedPartyInternal;
        private static MethodInfo removeInvolvedPartyInternalMethod;
        private static MethodInfo invokeRemoveAllowedMethod;
        private static bool installed;

        internal static void Install(string coopModuleRoot, string bridgeId)
        {
            lock (Sync)
            {
                if (installed)
                    return;

                var gameInterface = LoadRequiredCoopAssembly(
                    coopModuleRoot,
                    "GameInterface.dll",
                    "GameInterface");
                var common = LoadRequiredCoopAssembly(
                    coopModuleRoot,
                    "Common.dll",
                    "Common");
                ValidateRequiredAssembly(
                    typeof(Harmony).Assembly,
                    Path.Combine(
                        coopModuleRoot,
                        "bin",
                        "Win64_Shipping_Client",
                        "0Harmony.dll"),
                    "0Harmony");
                var campaignSystem = ResolveRequiredLoadedAssembly(
                    "TaleWorlds.CampaignSystem");

                var patchType = RequireType(
                    gameInterface,
                    "GameInterface.Services.MapEvents.Patches.MapEventSideDestructionPatches");
                var prefix = FindRequiredMethod(
                    patchType,
                    "Prefix",
                    typeof(bool),
                    true,
                    "TaleWorlds.CampaignSystem.MapEvents.MapEventSide",
                    "TaleWorlds.CampaignSystem.Party.PartyBase");

                var policyType = RequireType(
                    gameInterface,
                    "GameInterface.Policies.CallOriginalPolicy");
                FindRequiredMethod(
                    policyType,
                    "IsOriginalAllowed",
                    typeof(bool),
                    true);

                var allowedThreadType = RequireType(common, "Common.Util.AllowedThread");
                if (!typeof(IDisposable).IsAssignableFrom(allowedThreadType))
                {
                    throw new InvalidDataException(
                        "Required Coop AllowedThread type does not implement IDisposable.");
                }
                var allowedThreadConstructor = allowedThreadType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (allowedThreadConstructor == null ||
                    allowedThreadConstructor.DeclaringType != allowedThreadType ||
                    !allowedThreadConstructor.IsPublic ||
                    allowedThreadConstructor.GetParameters().Length != 0)
                {
                    throw new InvalidDataException(
                        "Required Coop AllowedThread constructor has an incompatible signature.");
                }
                FindRequiredMethod(
                    allowedThreadType,
                    "Dispose",
                    typeof(void),
                    false);

                var mapEventType = RequireType(
                    campaignSystem,
                    "TaleWorlds.CampaignSystem.MapEvents.MapEvent");
                var removeMethod = FindRequiredMethod(
                    mapEventType,
                    "RemoveInvolvedPartyInternal",
                    typeof(void),
                    false,
                    "TaleWorlds.CampaignSystem.MapEvents.MapEventParty");

                createAllowedScope = BuildAllowedScopeFactory(allowedThreadConstructor);
                removeInvolvedPartyInternal = BuildRemoveDelegate(removeMethod);
                removeInvolvedPartyInternalMethod = removeMethod;
                invokeRemoveAllowedMethod = typeof(ClientMapEventPositionAuthority).GetMethod(
                    nameof(InvokeRemoveAllowed),
                    BindingFlags.Static | BindingFlags.Public);
                if (invokeRemoveAllowedMethod == null)
                    throw new MissingMethodException(
                        typeof(ClientMapEventPositionAuthority).FullName,
                        nameof(InvokeRemoveAllowed));

                var harmonyId = bridgeId + ".client-map-event-position-authority";
                var existing = Harmony.GetPatchInfo(prefix);
                if (existing != null)
                {
                    if (existing.Transpilers.Any(patch =>
                            string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)))
                    {
                        installed = true;
                        return;
                    }
                    if (existing.Owners.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "Released Coop map-event destruction prefix already has a foreign Harmony patch: " +
                            string.Join(", ", existing.Owners) + ".");
                    }
                }

                var transpiler = new HarmonyMethod(
                    typeof(ClientMapEventPositionAuthority),
                    nameof(Transpile))
                {
                    priority = Priority.First
                };
                new Harmony(harmonyId).Patch(prefix, transpiler: transpiler);
                var applied = Harmony.GetPatchInfo(prefix);
                if (applied == null ||
                    applied.Transpilers.Count(patch =>
                        string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) != 1)
                {
                    throw new InvalidOperationException(
                        "BCS Coop bridge map-event position-authority transpiler was not installed exactly once.");
                }

                installed = true;
                Console.WriteLine(
                    "[BCS Coop Bridge] Installed client map-event position-authority scope.");
            }
        }

        public static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions)
        {
            var rewritten = instructions.ToList();
            var replacementCount = 0;
            foreach (var instruction in rewritten)
            {
                var method = instruction.operand as MethodInfo;
                if (instruction.opcode != OpCodes.Callvirt ||
                    method == null ||
                    !method.Equals(removeInvolvedPartyInternalMethod))
                {
                    continue;
                }

                instruction.opcode = OpCodes.Call;
                instruction.operand = invokeRemoveAllowedMethod;
                replacementCount++;
            }
            if (replacementCount != 1)
            {
                throw new InvalidDataException(
                    "Released Coop map-event destruction prefix contained " + replacementCount +
                    " required RemoveInvolvedPartyInternal calls; expected exactly one.");
            }
            return rewritten;
        }

        public static void InvokeRemoveAllowed(object mapEvent, object mapEventParty)
        {
            var scopeFactory = createAllowedScope;
            var remove = removeInvolvedPartyInternal;
            if (scopeFactory == null || remove == null)
                throw new InvalidOperationException("Client map-event position-authority scope is not configured.");

            var scope = scopeFactory();
            if (scope == null)
                throw new InvalidOperationException("Released Coop AllowedThread returned a null scope.");
            try
            {
                remove(mapEvent, mapEventParty);
            }
            finally
            {
                scope.Dispose();
            }
        }

        internal static Assembly LoadRequiredCoopAssembly(
            string coopModuleRoot,
            string fileName,
            string assemblyName)
        {
            var path = Path.Combine(
                coopModuleRoot,
                "bin",
                "Win64_Shipping_Client",
                fileName);
            var assembly = ClientCoopHandlerRegistration.LoadExactCoopAssembly(
                path,
                assemblyName);
            ValidateRequiredAssembly(
                assembly,
                path,
                assemblyName);
            return assembly;
        }

        internal static Assembly ResolveRequiredLoadedAssembly(string assemblyName)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies().Where(candidate =>
                    string.Equals(
                        candidate.GetName().Name,
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new FileLoadException(
                    "Expected exactly one loaded " + assemblyName + " assembly, found " +
                    matches.Length + ".");
            }
            ValidateRequiredAssembly(
                matches[0],
                matches[0].Location,
                assemblyName);
            return matches[0];
        }

        private static void ValidateRequiredAssembly(
            Assembly assembly,
            string expectedPath,
            string expectedName)
        {
            var canonicalPath = Path.GetFullPath(expectedPath);
            if (!File.Exists(canonicalPath))
                throw new FileNotFoundException("Required assembly is missing.", canonicalPath);
            if (!string.Equals(
                    assembly.GetName().Name,
                    expectedName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Path.GetFullPath(assembly.Location),
                    canonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException(
                    "Loaded " + expectedName + " assembly did not match its required location.",
                    assembly.Location);
            }
        }

        internal static Type RequireType(Assembly assembly, string fullName)
        {
            var type = assembly.GetType(fullName, false, false);
            if (type == null)
                throw new TypeLoadException("Required type is missing: " + fullName + ".");
            return type;
        }

        internal static MethodInfo FindRequiredMethod(
            Type declaringType,
            string name,
            Type returnType,
            bool isStatic,
            params string[] parameterTypeNames)
        {
            var matches = declaringType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, name, StringComparison.Ordinal) &&
                    method.ReturnType == returnType &&
                    method.IsStatic == isStatic &&
                    method.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                        .SequenceEqual(parameterTypeNames, StringComparer.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    "Required method did not resolve exactly once: " +
                    declaringType.FullName + "::" + name + ".");
            }
            return matches[0];
        }

        internal static void RequireMethod(
            MethodInfo method,
            Type declaringType,
            string name,
            Type returnType,
            bool isStatic,
            params string[] parameterTypeNames)
        {
            if (method == null ||
                method.DeclaringType != declaringType ||
                !string.Equals(method.Name, name, StringComparison.Ordinal) ||
                method.ReturnType != returnType ||
                method.IsStatic != isStatic)
            {
                throw new InvalidDataException(
                    "Released Coop method did not match its required signature: " +
                    declaringType.FullName + "::" + name + ".");
            }
            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypeNames.Length)
            {
                throw new InvalidDataException(
                    "Released Coop method parameter count did not match its required signature: " +
                    declaringType.FullName + "::" + name + ".");
            }
            for (var index = 0; index < parameters.Length; index++)
            {
                if (!string.Equals(
                        parameters[index].ParameterType.FullName,
                        parameterTypeNames[index],
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Released Coop method parameter did not match its required signature: " +
                        declaringType.FullName + "::" + name + ".");
                }
            }
        }

        private static Func<IDisposable> BuildAllowedScopeFactory(
            ConstructorInfo constructor)
        {
            var dynamicMethod = new DynamicMethod(
                "BCS_CreateAllowedThreadScope",
                typeof(IDisposable),
                Type.EmptyTypes,
                constructor.Module,
                true);
            var il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Ret);
            return (Func<IDisposable>)dynamicMethod.CreateDelegate(typeof(Func<IDisposable>));
        }

        private static Action<object, object> BuildRemoveDelegate(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var dynamicMethod = new DynamicMethod(
                "BCS_RemoveInvolvedPartyInternal",
                typeof(void),
                new[] { typeof(object), typeof(object) },
                method.Module,
                true);
            var il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, method.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, parameters[0].ParameterType);
            il.Emit(OpCodes.Callvirt, method);
            il.Emit(OpCodes.Ret);
            return (Action<object, object>)dynamicMethod.CreateDelegate(
                typeof(Action<object, object>));
        }
    }

    internal static class ClientTroopUpgradeTrackerLoadRepair
    {
        private static readonly object Sync = new object();
        private static Func<bool> getRestoringTracker;
        private static Action<bool> setRestoringTracker;
        private static bool installed;

        internal static void Install(string coopModuleRoot, string bridgeId)
        {
            lock (Sync)
            {
                if (installed)
                    return;

                var gameInterface = ClientMapEventPositionAuthority.LoadRequiredCoopAssembly(
                    coopModuleRoot,
                    "GameInterface.dll",
                    "GameInterface");
                var campaignSystem = ClientMapEventPositionAuthority.ResolveRequiredLoadedAssembly(
                    "TaleWorlds.CampaignSystem");

                var robustnessPatchType = ClientMapEventPositionAuthority.RequireType(
                    gameInterface,
                    "GameInterface.Services.MapEvents.Patches.MapEventRobustnessPatches");

                var restoringField = robustnessPatchType.GetField(
                    "restoringTroopUpgradeTracker",
                    BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (restoringField == null ||
                    restoringField.DeclaringType != robustnessPatchType ||
                    !string.Equals(
                        restoringField.Name,
                        "restoringTroopUpgradeTracker",
                        StringComparison.Ordinal) ||
                    restoringField.FieldType != typeof(bool) ||
                    !restoringField.IsStatic ||
                    !restoringField.IsPrivate ||
                    restoringField.IsInitOnly ||
                    restoringField.IsLiteral)
                {
                    throw new InvalidDataException(
                        "Released Coop tracker restoration guard has an incompatible signature.");
                }

                var mapEventType = ClientMapEventPositionAuthority.RequireType(
                    campaignSystem,
                    "TaleWorlds.CampaignSystem.MapEvents.MapEvent");
                if (mapEventType != typeof(MapEvent))
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds MapEvent type did not match the loaded runtime type.");
                }
                var trackerType = ClientMapEventPositionAuthority.RequireType(
                    campaignSystem,
                    "TaleWorlds.CampaignSystem.TroopUpgradeTracker");

                var trackerPostfix = ClientMapEventPositionAuthority.FindRequiredMethod(
                    robustnessPatchType,
                    "PostfixTroopUpgradeTracker",
                    typeof(void),
                    true,
                    mapEventType.FullName,
                    trackerType.MakeByRefType().FullName);
                if (!trackerPostfix.IsPrivate)
                {
                    throw new InvalidDataException(
                        "Released Coop tracker robustness postfix visibility is incompatible.");
                }

                var onAfterLoad = ClientMapEventPositionAuthority.FindRequiredMethod(
                    mapEventType,
                    "OnAfterLoad",
                    typeof(void),
                    false);
                if (!onAfterLoad.IsAssembly)
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds MapEvent.OnAfterLoad visibility is incompatible.");
                }

                var trackerGetter = ClientMapEventPositionAuthority.FindRequiredMethod(
                    mapEventType,
                    "get_TroopUpgradeTracker",
                    trackerType,
                    false);
                if (!trackerGetter.IsPublic || !trackerGetter.IsSpecialName)
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds tracker getter visibility is incompatible.");
                }

                getRestoringTracker = BuildBooleanFieldGetter(restoringField);
                setRestoringTracker = BuildBooleanFieldSetter(restoringField);

                var harmonyId = bridgeId + ".client-map-event-troop-upgrade-load-repair";
                var harmony = new Harmony(harmonyId);
                var prefixMethod = typeof(ClientTroopUpgradeTrackerLoadRepair).GetMethod(
                    nameof(BeforeOnAfterLoad),
                    BindingFlags.Static | BindingFlags.Public);
                var finalizerMethod = typeof(ClientTroopUpgradeTrackerLoadRepair).GetMethod(
                    nameof(FinalizeOnAfterLoad),
                    BindingFlags.Static | BindingFlags.Public);
                if (prefixMethod == null || finalizerMethod == null)
                {
                    throw new MissingMethodException(
                        typeof(ClientTroopUpgradeTrackerLoadRepair).FullName,
                        "OnAfterLoad Harmony hooks");
                }

                var existing = Harmony.GetPatchInfo(onAfterLoad);
                var existingPrefixCount = CountOwnedPatch(
                    existing == null ? null : existing.Prefixes,
                    harmonyId,
                    prefixMethod);
                var existingFinalizerCount = CountOwnedPatch(
                    existing == null ? null : existing.Finalizers,
                    harmonyId,
                    finalizerMethod);
                var existingOwnerCount = existing == null
                    ? 0
                    : existing.Prefixes.Count(patch =>
                          string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) +
                      existing.Postfixes.Count(patch =>
                          string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) +
                      existing.Transpilers.Count(patch =>
                          string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) +
                      existing.Finalizers.Count(patch =>
                          string.Equals(patch.owner, harmonyId, StringComparison.Ordinal));
                if (existingPrefixCount == 1 &&
                    existingFinalizerCount == 1 &&
                    existingOwnerCount == 2)
                {
                    installed = true;
                    return;
                }
                if (existingOwnerCount != 0)
                    harmony.Unpatch(onAfterLoad, HarmonyPatchType.All, harmonyId);

                try
                {
                    var prefix = new HarmonyMethod(prefixMethod)
                    {
                        priority = Priority.First
                    };
                    var finalizer = new HarmonyMethod(finalizerMethod)
                    {
                        priority = Priority.Last
                    };
                    harmony.Patch(onAfterLoad, prefix: prefix, finalizer: finalizer);

                    var applied = Harmony.GetPatchInfo(onAfterLoad);
                    var appliedPrefixCount = CountOwnedPatch(
                        applied == null ? null : applied.Prefixes,
                        harmonyId,
                        prefixMethod);
                    var appliedFinalizerCount = CountOwnedPatch(
                        applied == null ? null : applied.Finalizers,
                        harmonyId,
                        finalizerMethod);
                    var appliedOwnerCount = applied == null
                        ? 0
                        : applied.Prefixes.Count(patch =>
                              string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) +
                          applied.Postfixes.Count(patch =>
                              string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) +
                          applied.Transpilers.Count(patch =>
                              string.Equals(patch.owner, harmonyId, StringComparison.Ordinal)) +
                          applied.Finalizers.Count(patch =>
                              string.Equals(patch.owner, harmonyId, StringComparison.Ordinal));
                    if (appliedPrefixCount != 1 ||
                        appliedFinalizerCount != 1 ||
                        appliedOwnerCount != 2)
                    {
                        throw new InvalidOperationException(
                            "BCS Coop bridge tracker load repair was not installed exactly once.");
                    }

                    installed = true;
                    Console.WriteLine(
                        "[BCS Coop Bridge] Installed client troop-upgrade tracker load repair.");
                }
                catch (Exception installException)
                {
                    try
                    {
                        harmony.Unpatch(onAfterLoad, HarmonyPatchType.All, harmonyId);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new AggregateException(
                            "BCS Coop bridge tracker load repair installation and cleanup both failed.",
                            installException,
                            cleanupException);
                    }
                    throw;
                }
            }
        }

        public static void BeforeOnAfterLoad(ref GuardState __state)
        {
            __state = default(GuardState);
            var getGuard = getRestoringTracker;
            var setGuard = setRestoringTracker;
            if (getGuard == null || setGuard == null)
            {
                throw new InvalidOperationException(
                    "Client troop-upgrade tracker load repair is not configured.");
            }

            var previousValue = getGuard();
            setGuard(true);
            __state.PreviousValue = previousValue;
            __state.Armed = true;
        }

        public static Exception FinalizeOnAfterLoad(
            Exception __exception,
            GuardState __state)
        {
            if (!__state.Armed)
                return __exception;

            try
            {
                var setGuard = setRestoringTracker;
                if (setGuard == null)
                {
                    throw new InvalidOperationException(
                        "Client troop-upgrade tracker load repair lost its restoration delegate.");
                }
                setGuard(__state.PreviousValue);
                return __exception;
            }
            catch (Exception restorationException)
            {
                if (__exception == null)
                    return restorationException;
                return new AggregateException(
                    "MapEvent.OnAfterLoad and Coop tracker-guard restoration both failed.",
                    __exception,
                    restorationException);
            }
        }

        private static int CountOwnedPatch(
            IEnumerable<Patch> patches,
            string harmonyId,
            MethodInfo patchMethod)
        {
            if (patches == null)
                return 0;
            return patches.Count(patch =>
                string.Equals(patch.owner, harmonyId, StringComparison.Ordinal) &&
                patch.PatchMethod == patchMethod);
        }

        private static Func<bool> BuildBooleanFieldGetter(FieldInfo field)
        {
            var method = new DynamicMethod(
                "BCS_GetTrackerRestorationGuard",
                typeof(bool),
                Type.EmptyTypes,
                field.Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<bool>)method.CreateDelegate(typeof(Func<bool>));
        }

        private static Action<bool> BuildBooleanFieldSetter(FieldInfo field)
        {
            var method = new DynamicMethod(
                "BCS_SetTrackerRestorationGuard",
                typeof(void),
                new[] { typeof(bool) },
                field.Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stsfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<bool>)method.CreateDelegate(typeof(Action<bool>));
        }

        public struct GuardState
        {
            internal bool PreviousValue;
            internal bool Armed;
        }
    }

    internal static class ClientDiagnosticSupport
    {
        private const int MaxFailureKeys = 16;
        private static readonly object FailureSync = new object();
        private static readonly ISet<string> FailureKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static bool failureSuppressionReported;

        internal static GameThreadProbe CreateGameThreadProbe(Assembly common)
        {
            var gameThreadType = ClientMapEventPositionAuthority.RequireType(
                common,
                "Common.GameThread");

            var instanceGetter = ClientMapEventPositionAuthority.FindRequiredMethod(
                gameThreadType,
                "get_Instance",
                gameThreadType,
                true);
            var isGameThreadGetter = ClientMapEventPositionAuthority.FindRequiredMethod(
                gameThreadType,
                "get_IsGameThread",
                typeof(bool),
                false);
            if (!instanceGetter.IsPublic ||
                !instanceGetter.IsSpecialName ||
                !isGameThreadGetter.IsPublic ||
                !isGameThreadGetter.IsSpecialName)
            {
                throw new InvalidDataException(
                    "Released Coop GameThread accessors have incompatible visibility.");
            }
            return new GameThreadProbe(instanceGetter, isGameThreadGetter);
        }

        internal static string CurrentThreadDescription()
        {
            try
            {
                var thread = System.Threading.Thread.CurrentThread;
                var name = thread.Name;
                return thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) +
                       (string.IsNullOrWhiteSpace(name) ? string.Empty : ":" + name);
            }
            catch (Exception exception)
            {
                ReportFailure("thread-description", exception);
                return "unknown";
            }
        }

        internal static string FirstExternalCaller(MethodBase target)
        {
            try
            {
                var frames = new StackTrace(1, false).GetFrames();
                if (frames == null)
                    return "unknown";
                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    var declaringType = method == null ? null : method.DeclaringType;
                    if (method == null || declaringType == null)
                        continue;
                    if (target != null &&
                        method.Equals(target))
                    {
                        continue;
                    }

                    var assemblyName = declaringType.Assembly.GetName().Name;
                    var typeName = declaringType.FullName ?? declaringType.Name;
                    if (string.Equals(assemblyName, "0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            assemblyName,
                            typeof(ClientDiagnosticSupport).Assembly.GetName().Name,
                            StringComparison.OrdinalIgnoreCase) ||
                        typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
                        typeName.StartsWith("BCS.CoopBridge.", StringComparison.Ordinal) ||
                        method.Name.IndexOf("SetDisorganized_Patch", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }
                    return typeName + "::" + method.Name;
                }
            }
            catch (Exception exception)
            {
                ReportFailure("caller-capture", exception);
            }
            return "unknown";
        }

        internal static string Quote(string value)
        {
            if (value == null)
                value = "<null>";
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }

        internal static void Write(string message)
        {
            try
            {
                Trace.WriteLine(message);
            }
            catch
            {
                // Diagnostic output must not affect the game.
            }
            try
            {
                Console.WriteLine(message);
            }
            catch
            {
                // Diagnostic output must not affect the game.
            }
        }

        internal static void ReportFailure(string context, Exception exception)
        {
            try
            {
                string message = null;
                lock (FailureSync)
                {
                    if (FailureKeys.Contains(context))
                        return;
                    if (FailureKeys.Count >= MaxFailureKeys)
                    {
                        if (failureSuppressionReported)
                            return;
                        failureSuppressionReported = true;
                        message =
                            "[BCS Coop Bridge][ClientDiagnostic] Further distinct diagnostic failures suppressed.";
                    }
                    else
                    {
                        FailureKeys.Add(context);
                        var root = exception == null ? null : exception.GetBaseException();
                        message =
                            "[BCS Coop Bridge][ClientDiagnostic] context=" + Quote(context) +
                            " error=" + Quote(
                                root == null
                                    ? "unknown"
                                    : root.GetType().FullName + ": " + root.Message);
                    }
                }
                Write(message);
            }
            catch
            {
                // Failure reporting must not affect the game.
            }
        }

        internal static int CountOwnedPatches(Patches patches, string owner)
        {
            if (patches == null)
                return 0;
            return patches.Prefixes.Count(patch =>
                       string.Equals(patch.owner, owner, StringComparison.Ordinal)) +
                   patches.Postfixes.Count(patch =>
                       string.Equals(patch.owner, owner, StringComparison.Ordinal)) +
                   patches.Transpilers.Count(patch =>
                       string.Equals(patch.owner, owner, StringComparison.Ordinal)) +
                   patches.Finalizers.Count(patch =>
                       string.Equals(patch.owner, owner, StringComparison.Ordinal));
        }

        internal sealed class GameThreadProbe
        {
            private readonly MethodInfo instanceGetter;
            private readonly MethodInfo isGameThreadGetter;

            internal GameThreadProbe(
                MethodInfo instanceGetter,
                MethodInfo isGameThreadGetter)
            {
                this.instanceGetter = instanceGetter;
                this.isGameThreadGetter = isGameThreadGetter;
            }

            internal string Read()
            {
                try
                {
                    var instance = instanceGetter.Invoke(null, null);
                    if (instance == null)
                        return "unknown";
                    var result = isGameThreadGetter.Invoke(instance, null);
                    return result is bool
                        ? ((bool)result ? "true" : "false")
                        : "unknown";
                }
                catch (Exception exception)
                {
                    ReportFailure("game-thread-probe", exception);
                    return "unknown";
                }
            }
        }
    }

    internal static class ClientSetDisorganizedDiagnostic
    {
        private const int MaxRecords = 256;
        private const int MaxDedupeKeys = 128;
        private static readonly long DedupeTicks = Stopwatch.Frequency * 2L;
        private static readonly object Sync = new object();
        private static readonly IDictionary<string, long> Recent =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static MethodInfo targetMethod;
        private static MethodInfo isDisorganizedGetter;
        private static MethodInfo isMainPartyGetter;
        private static MethodInfo stringIdGetter;
        private static ClientDiagnosticSupport.GameThreadProbe gameThreadProbe;
        private static bool attempted;
        private static bool installed;
        private static bool saturated;
        private static bool suppressionSummaryReported;
        private static int recordCount;
        private static long sequence;

        internal static void Install(string coopModuleRoot, string bridgeId)
        {
            lock (Sync)
            {
                if (attempted)
                    return;
                attempted = true;

                Harmony harmony = null;
                MethodInfo resolvedTarget = null;
                var harmonyId = bridgeId + ".client-set-disorganized-diagnostic";
                try
                {
                    var common = ClientMapEventPositionAuthority.LoadRequiredCoopAssembly(
                        coopModuleRoot,
                        "Common.dll",
                        "Common");
                    var campaignSystem =
                        ClientMapEventPositionAuthority.ResolveRequiredLoadedAssembly(
                            "TaleWorlds.CampaignSystem");

                    var mobilePartyType = ClientMapEventPositionAuthority.RequireType(
                        campaignSystem,
                        "TaleWorlds.CampaignSystem.Party.MobileParty");

                    resolvedTarget = ClientMapEventPositionAuthority.FindRequiredMethod(
                        mobilePartyType,
                        "SetDisorganized",
                        typeof(void),
                        false,
                        typeof(bool).FullName);
                    if (!resolvedTarget.IsPublic)
                    {
                        throw new InvalidDataException(
                            "Released TaleWorlds MobileParty.SetDisorganized visibility is incompatible.");
                    }

                    var resolvedIsDisorganizedGetter =
                        ClientMapEventPositionAuthority.FindRequiredMethod(
                        mobilePartyType,
                        "get_IsDisorganized",
                        typeof(bool),
                        false);
                    var resolvedIsMainPartyGetter =
                        ClientMapEventPositionAuthority.FindRequiredMethod(
                        mobilePartyType,
                        "get_IsMainParty",
                        typeof(bool),
                        false);
                    if (!resolvedIsDisorganizedGetter.IsPublic ||
                        !resolvedIsDisorganizedGetter.IsSpecialName ||
                        !resolvedIsMainPartyGetter.IsPublic ||
                        !resolvedIsMainPartyGetter.IsSpecialName)
                    {
                        throw new InvalidDataException(
                            "Released TaleWorlds MobileParty diagnostic getters have incompatible visibility.");
                    }

                    var stringIdProperty = mobilePartyType.GetProperty(
                        "StringId",
                        BindingFlags.Instance | BindingFlags.Public);
                    var resolvedStringIdGetter = stringIdProperty == null
                        ? null
                        : stringIdProperty.GetGetMethod(false);
                    if (resolvedStringIdGetter == null ||
                        resolvedStringIdGetter.IsStatic ||
                        resolvedStringIdGetter.ReturnType != typeof(string) ||
                        resolvedStringIdGetter.GetParameters().Length != 0 ||
                        resolvedStringIdGetter.DeclaringType == null ||
                        !string.Equals(
                            resolvedStringIdGetter.DeclaringType.FullName,
                            "TaleWorlds.ObjectSystem.MBObjectBase",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released TaleWorlds MobileParty.StringId getter has an incompatible signature.");
                    }

                    targetMethod = resolvedTarget;
                    isDisorganizedGetter = resolvedIsDisorganizedGetter;
                    isMainPartyGetter = resolvedIsMainPartyGetter;
                    stringIdGetter = resolvedStringIdGetter;
                    gameThreadProbe = ClientDiagnosticSupport.CreateGameThreadProbe(common);

                    var prefixMethod = typeof(ClientSetDisorganizedDiagnostic).GetMethod(
                        nameof(BeforeSetDisorganized),
                        BindingFlags.Static | BindingFlags.Public);
                    var postfixMethod = typeof(ClientSetDisorganizedDiagnostic).GetMethod(
                        nameof(AfterSetDisorganized),
                        BindingFlags.Static | BindingFlags.Public);
                    if (prefixMethod == null || postfixMethod == null)
                    {
                        throw new MissingMethodException(
                            typeof(ClientSetDisorganizedDiagnostic).FullName,
                            "SetDisorganized diagnostic Harmony hooks");
                    }

                    harmony = new Harmony(harmonyId);
                    var existing = Harmony.GetPatchInfo(resolvedTarget);
                    if (IsExactlyInstalled(
                            existing,
                            harmonyId,
                            prefixMethod,
                            postfixMethod))
                    {
                        installed = true;
                        return;
                    }
                    if (ClientDiagnosticSupport.CountOwnedPatches(existing, harmonyId) != 0)
                    {
                        harmony.Unpatch(
                            resolvedTarget,
                            HarmonyPatchType.All,
                            harmonyId);
                    }

                    harmony.Patch(
                        resolvedTarget,
                        prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First },
                        postfix: new HarmonyMethod(postfixMethod) { priority = Priority.Last });
                    var applied = Harmony.GetPatchInfo(resolvedTarget);
                    if (!IsExactlyInstalled(
                            applied,
                            harmonyId,
                            prefixMethod,
                            postfixMethod))
                    {
                        throw new InvalidOperationException(
                            "BCS Coop bridge SetDisorganized diagnostic was not installed exactly once.");
                    }

                    installed = true;
                    ClientDiagnosticSupport.Write(
                        "[BCS Coop Bridge] Installed client SetDisorganized diagnostic.");
                }
                catch (Exception exception)
                {
                    if (harmony != null && resolvedTarget != null)
                    {
                        try
                        {
                            harmony.Unpatch(
                                resolvedTarget,
                                HarmonyPatchType.All,
                                harmonyId);
                        }
                        catch (Exception cleanupException)
                        {
                            ClientDiagnosticSupport.ReportFailure(
                                "set-disorganized-diagnostic-cleanup",
                                cleanupException);
                        }
                    }
                    installed = false;
                    ClientDiagnosticSupport.ReportFailure(
                        "set-disorganized-diagnostic-disabled",
                        exception);
                }
            }
        }

        public static void BeforeSetDisorganized(
            object __instance,
            bool __0,
            ref ObservationState __state)
        {
            __state = default(ObservationState);
            try
            {
                if (!installed || IsSaturated() || __instance == null)
                    return;
                __state.PartyId = stringIdGetter.Invoke(__instance, null) as string;
                __state.IsMainParty = (bool)isMainPartyGetter.Invoke(__instance, null);
                __state.OldValue = (bool)isDisorganizedGetter.Invoke(__instance, null);
                __state.RequestedValue = __0;
                __state.Thread = ClientDiagnosticSupport.CurrentThreadDescription();
                __state.IsGameThread = gameThreadProbe == null
                    ? "unknown"
                    : gameThreadProbe.Read();
                __state.Caller = ClientDiagnosticSupport.FirstExternalCaller(targetMethod);
                __state.Armed = true;
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "set-disorganized-diagnostic-prefix",
                    exception);
            }
        }

        public static void AfterSetDisorganized(
            object __instance,
            ObservationState __state)
        {
            try
            {
                if (!installed || !__state.Armed || __instance == null)
                    return;
                var actual = (bool)isDisorganizedGetter.Invoke(__instance, null);
                var changed = actual != __state.OldValue;
                var mismatch = actual != __state.RequestedValue;
                if (!changed && !mismatch)
                    return;
                Emit(__state, actual, changed, mismatch);
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "set-disorganized-diagnostic-postfix",
                    exception);
            }
        }

        private static bool IsExactlyInstalled(
            Patches patches,
            string owner,
            MethodInfo prefixMethod,
            MethodInfo postfixMethod)
        {
            return patches != null &&
                   patches.Prefixes.Count(patch =>
                       string.Equals(patch.owner, owner, StringComparison.Ordinal) &&
                       patch.PatchMethod == prefixMethod) == 1 &&
                   patches.Postfixes.Count(patch =>
                       string.Equals(patch.owner, owner, StringComparison.Ordinal) &&
                       patch.PatchMethod == postfixMethod) == 1 &&
                   ClientDiagnosticSupport.CountOwnedPatches(patches, owner) == 2;
        }

        private static bool IsSaturated()
        {
            lock (Sync)
            {
                return saturated;
            }
        }

        private static void Emit(
            ObservationState state,
            bool actual,
            bool changed,
            bool mismatch)
        {
            string line = null;
            string summary = null;
            lock (Sync)
            {
                if (saturated)
                    return;

                var now = Stopwatch.GetTimestamp();
                RemoveExpiredDedupeEntries(now);
                var eventName = changed
                    ? (mismatch ? "changed+mismatch" : "changed")
                    : "mismatch";
                var key = string.Join(
                    "\u001f",
                    new[]
                    {
                        state.PartyId ?? string.Empty,
                        state.IsMainParty ? "1" : "0",
                        state.OldValue ? "1" : "0",
                        state.RequestedValue ? "1" : "0",
                        actual ? "1" : "0",
                        state.Thread ?? string.Empty,
                        state.IsGameThread ?? string.Empty,
                        state.Caller ?? string.Empty,
                        eventName
                    });
                long previous;
                if (Recent.TryGetValue(key, out previous) &&
                    now - previous >= 0 &&
                    now - previous < DedupeTicks)
                {
                    return;
                }
                if (!Recent.ContainsKey(key) && Recent.Count >= MaxDedupeKeys)
                    RemoveOldestDedupeEntry();
                Recent[key] = now;

                sequence++;
                recordCount++;
                line =
                    "[BCS Coop Bridge][SetDisorganizedDiagnostic] seq=" +
                    sequence.ToString(CultureInfo.InvariantCulture) +
                    " event=" + eventName +
                    " party=" + ClientDiagnosticSupport.Quote(state.PartyId) +
                    " main_party=" + (state.IsMainParty ? "true" : "false") +
                    " old=" + (state.OldValue ? "true" : "false") +
                    " requested=" + (state.RequestedValue ? "true" : "false") +
                    " actual=" + (actual ? "true" : "false") +
                    " thread=" + ClientDiagnosticSupport.Quote(state.Thread) +
                    " is_game_thread=" + state.IsGameThread +
                    " caller=" + ClientDiagnosticSupport.Quote(state.Caller);
                if (recordCount >= MaxRecords)
                {
                    saturated = true;
                    if (!suppressionSummaryReported)
                    {
                        suppressionSummaryReported = true;
                        summary =
                            "[BCS Coop Bridge][SetDisorganizedDiagnostic] suppression=record-cap" +
                            " emitted=" + MaxRecords.ToString(CultureInfo.InvariantCulture) +
                            " further_qualifying_events_suppressed=true";
                    }
                }
            }
            ClientDiagnosticSupport.Write(line);
            if (summary != null)
                ClientDiagnosticSupport.Write(summary);
        }

        private static void RemoveExpiredDedupeEntries(long now)
        {
            List<string> expired = null;
            foreach (var pair in Recent)
            {
                if (now - pair.Value < 0 || now - pair.Value >= DedupeTicks)
                {
                    if (expired == null)
                        expired = new List<string>();
                    expired.Add(pair.Key);
                }
            }
            if (expired == null)
                return;
            foreach (var key in expired)
                Recent.Remove(key);
        }

        private static void RemoveOldestDedupeEntry()
        {
            string oldestKey = null;
            var oldestTimestamp = long.MaxValue;
            foreach (var pair in Recent)
            {
                if (pair.Value >= oldestTimestamp)
                    continue;
                oldestTimestamp = pair.Value;
                oldestKey = pair.Key;
            }
            if (oldestKey != null)
                Recent.Remove(oldestKey);
        }

        public struct ObservationState
        {
            internal string PartyId;
            internal bool IsMainParty;
            internal bool OldValue;
            internal bool RequestedValue;
            internal string Thread;
            internal string IsGameThread;
            internal string Caller;
            internal bool Armed;
        }
    }

    internal static class ClientTroopRosterSequenceDiagnostic
    {
        private const int MaxRecords = 512;
        private const int MaxRosterIds = 128;
        private static readonly long DedupeTicks =
            Math.Max(1L, Stopwatch.Frequency / 4L);
        private static readonly object Sync = new object();
        private static readonly ISet<string> RosterIds =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly IDictionary<string, long> Recent =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static IDictionary<MethodBase, PayloadIdAccessor> payloadAccessors;
        private static IDictionary<MethodBase, string> deltaTypes;
        private static FieldInfo autoRegistryObjectManagerField;
        private static FieldInfo deltaObjectManagerField;
        private static MethodInfo objectManagerContainsId;
        private static MethodInfo createClientInstanceMethod;
        private static ClosureAccessor applyClosureAccessor;
        private static ClosureAccessor removeClosureAccessor;
        private static ClientDiagnosticSupport.GameThreadProbe gameThreadProbe;
        private static bool attempted;
        private static bool installed;
        private static bool saturated;
        private static bool suppressionSummaryReported;
        private static int recordCount;
        private static long sequence;

        internal static void Install(string coopModuleRoot, string bridgeId)
        {
            lock (Sync)
            {
                if (attempted)
                    return;
                attempted = true;

                Harmony harmony = null;
                var targets = new List<MethodInfo>();
                var harmonyId = bridgeId + ".client-troop-roster-sequence-diagnostic";
                try
                {
                    var gameInterface =
                        ClientMapEventPositionAuthority.LoadRequiredCoopAssembly(
                            coopModuleRoot,
                            "GameInterface.dll",
                            "GameInterface");
                    var common = ClientMapEventPositionAuthority.LoadRequiredCoopAssembly(
                        coopModuleRoot,
                        "Common.dll",
                        "Common");
                    var campaignSystem =
                        ClientMapEventPositionAuthority.ResolveRequiredLoadedAssembly(
                            "TaleWorlds.CampaignSystem");

                    var troopRosterType = ClientMapEventPositionAuthority.RequireType(
                        campaignSystem,
                        "TaleWorlds.CampaignSystem.Roster.TroopRoster");

                    var openAutoRegistryHandler = ClientMapEventPositionAuthority.RequireType(
                        gameInterface,
                        "GameInterface.Registry.Auto.AutoRegistryHandler`1");
                    if (!openAutoRegistryHandler.IsGenericTypeDefinition ||
                        openAutoRegistryHandler.GetGenericArguments().Length != 1 ||
                        !string.Equals(openAutoRegistryHandler.FullName,
                            "GameInterface.Registry.Auto.AutoRegistryHandler`1",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released Coop AutoRegistryHandler type has an incompatible generic signature.");
                    }
                    var openCreateMethod = FindAutoRegistryCreateMethod(openAutoRegistryHandler);
                    RequireAutoRegistryCreateMethod(
                        openCreateMethod,
                        openAutoRegistryHandler);
                    var closedAutoRegistryHandler = openAutoRegistryHandler.MakeGenericType(
                        troopRosterType);
                    var createClientInstance = FindClosedMethod(
                        closedAutoRegistryHandler,
                        "CreateClientInstance");
                    RequireClosedAutoRegistryCreateMethod(
                        createClientInstance,
                        closedAutoRegistryHandler);
                    targets.Add(createClientInstance);

                    var resolvedAutoRegistryObjectManagerField = FindClosedField(
                        closedAutoRegistryHandler,
                        "<ObjectManager>k__BackingField");
                    RequireObjectManagerField(
                        resolvedAutoRegistryObjectManagerField,
                        closedAutoRegistryHandler,
                        "<ObjectManager>k__BackingField");

                    var deltaHandlerType = ClientMapEventPositionAuthority.RequireType(
                        gameInterface,
                        "GameInterface.Services.TroopRosters.Handlers.TroopRosterDeltaHandler");
                    var resolvedDeltaObjectManagerField =
                        deltaHandlerType.GetField(
                            "objectManager",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    RequireObjectManagerField(
                        resolvedDeltaObjectManagerField,
                        deltaHandlerType,
                        "objectManager");

                    var setNumber = RequireDeltaMethod(
                        deltaHandlerType,
                        "Handle_NetworkSetNumber");
                    var setWounded = RequireDeltaMethod(
                        deltaHandlerType,
                        "Handle_NetworkSetWoundedNumber");
                    var elementBatch = RequireDeltaMethod(
                        deltaHandlerType,
                        "Handle_NetworkElementBatch");
                    var removeZeroCounts = RequireDeltaMethod(
                        deltaHandlerType,
                        "Handle_NetworkRemoveZeroCounts");
                    targets.Add(setNumber);
                    targets.Add(setWounded);
                    targets.Add(elementBatch);
                    targets.Add(removeZeroCounts);

                    var resolvedPayloadAccessors =
                        new Dictionary<MethodBase, PayloadIdAccessor>();
                    resolvedPayloadAccessors.Add(
                        createClientInstance,
                        BuildPayloadIdAccessor(
                            createClientInstance,
                            "GameInterface.Registry.Auto.NetworkCreateInstance`1",
                            troopRosterType,
                            "InstanceId"));
                    resolvedPayloadAccessors.Add(
                        setNumber,
                        BuildPayloadIdAccessor(
                            setNumber,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterSetNumber",
                            null,
                            "RosterId"));
                    resolvedPayloadAccessors.Add(
                        setWounded,
                        BuildPayloadIdAccessor(
                            setWounded,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterSetWoundedNumber",
                            null,
                            "RosterId"));
                    resolvedPayloadAccessors.Add(
                        elementBatch,
                        BuildPayloadIdAccessor(
                            elementBatch,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterElementBatch",
                            null,
                            "RosterId"));
                    resolvedPayloadAccessors.Add(
                        removeZeroCounts,
                        BuildPayloadIdAccessor(
                            removeZeroCounts,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterRemoveZeroCounts",
                            null,
                            "RosterId"));

                    var resolvedDeltaTypes = new Dictionary<MethodBase, string>
                    {
                        { setNumber, "NetworkTroopRosterSetNumber" },
                        { setWounded, "NetworkTroopRosterSetWoundedNumber" },
                        { elementBatch, "NetworkTroopRosterElementBatch" },
                        { removeZeroCounts, "NetworkTroopRosterRemoveZeroCounts" }
                    };

                    var resolvedApplyClosure = BuildClosureAccessor(
                        gameInterface,
                        deltaHandlerType,
                        "GameInterface.Services.TroopRosters.Handlers.TroopRosterDeltaHandler+<>c__DisplayClass22_0",
                        "<Apply>b__0",
                        "apply",
                        "Apply");
                    var resolvedRemoveClosure = BuildClosureAccessor(
                        gameInterface,
                        deltaHandlerType,
                        "GameInterface.Services.TroopRosters.Handlers.TroopRosterDeltaHandler+<>c__DisplayClass21_0",
                        "<Handle_NetworkRemoveZeroCounts>b__0",
                        null,
                        "NetworkTroopRosterRemoveZeroCounts");
                    targets.Add(resolvedApplyClosure.Method);
                    targets.Add(resolvedRemoveClosure.Method);

                    var objectManagerType = ClientMapEventPositionAuthority.RequireType(
                        gameInterface,
                        "GameInterface.Services.ObjectManager.IObjectManager");
                    if (!objectManagerType.IsInterface)
                    {
                        throw new InvalidDataException(
                            "Released Coop IObjectManager type is not an interface.");
                    }
                    var resolvedContainsId = ClientMapEventPositionAuthority.FindRequiredMethod(
                        objectManagerType,
                        "Contains",
                        typeof(bool),
                        false,
                        typeof(string).FullName);
                    if (!resolvedContainsId.IsPublic || !resolvedContainsId.IsAbstract)
                    {
                        throw new InvalidDataException(
                            "Released Coop IObjectManager.Contains(string) visibility is incompatible.");
                    }

                    payloadAccessors = resolvedPayloadAccessors;
                    deltaTypes = resolvedDeltaTypes;
                    autoRegistryObjectManagerField =
                        resolvedAutoRegistryObjectManagerField;
                    deltaObjectManagerField = resolvedDeltaObjectManagerField;
                    objectManagerContainsId = resolvedContainsId;
                    createClientInstanceMethod = createClientInstance;
                    applyClosureAccessor = resolvedApplyClosure;
                    removeClosureAccessor = resolvedRemoveClosure;
                    gameThreadProbe = ClientDiagnosticSupport.CreateGameThreadProbe(common);

                    var createPrefix = typeof(ClientTroopRosterSequenceDiagnostic).GetMethod(
                        nameof(BeforeCreateClientInstance),
                        BindingFlags.Static | BindingFlags.Public);
                    var createPostfix = typeof(ClientTroopRosterSequenceDiagnostic).GetMethod(
                        nameof(AfterCreateClientInstance),
                        BindingFlags.Static | BindingFlags.Public);
                    var deltaPrefix = typeof(ClientTroopRosterSequenceDiagnostic).GetMethod(
                        nameof(BeforeDeltaReceive),
                        BindingFlags.Static | BindingFlags.Public);
                    var closurePrefix = typeof(ClientTroopRosterSequenceDiagnostic).GetMethod(
                        nameof(BeforeGameThreadClosure),
                        BindingFlags.Static | BindingFlags.Public);
                    if (createPrefix == null ||
                        createPostfix == null ||
                        deltaPrefix == null ||
                        closurePrefix == null)
                    {
                        throw new MissingMethodException(
                            typeof(ClientTroopRosterSequenceDiagnostic).FullName,
                            "TroopRoster sequence diagnostic Harmony hooks");
                    }

                    harmony = new Harmony(harmonyId);
                    if (IsExactlyInstalled(
                            createClientInstance,
                            new[] { setNumber, setWounded, elementBatch, removeZeroCounts },
                            new[]
                            {
                                resolvedApplyClosure.Method,
                                resolvedRemoveClosure.Method
                            },
                            harmonyId,
                            createPrefix,
                            createPostfix,
                            deltaPrefix,
                            closurePrefix))
                    {
                        installed = true;
                        return;
                    }
                    UnpatchOwner(harmony, targets, harmonyId);

                    harmony.Patch(
                        createClientInstance,
                        prefix: new HarmonyMethod(createPrefix) { priority = Priority.First },
                        postfix: new HarmonyMethod(createPostfix) { priority = Priority.Last });
                    foreach (var method in new[]
                             {
                                 setNumber,
                                 setWounded,
                                 elementBatch,
                                 removeZeroCounts
                             })
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(deltaPrefix)
                            {
                                priority = Priority.First
                            });
                    }
                    foreach (var method in new[]
                             {
                                 resolvedApplyClosure.Method,
                                 resolvedRemoveClosure.Method
                             })
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(closurePrefix)
                            {
                                priority = Priority.First
                            });
                    }

                    if (!IsExactlyInstalled(
                            createClientInstance,
                            new[] { setNumber, setWounded, elementBatch, removeZeroCounts },
                            new[]
                            {
                                resolvedApplyClosure.Method,
                                resolvedRemoveClosure.Method
                            },
                            harmonyId,
                            createPrefix,
                            createPostfix,
                            deltaPrefix,
                            closurePrefix))
                    {
                        throw new InvalidOperationException(
                            "BCS Coop bridge TroopRoster sequence diagnostic was not installed exactly once.");
                    }

                    installed = true;
                    ClientDiagnosticSupport.Write(
                        "[BCS Coop Bridge] Installed client TroopRoster sequence diagnostic.");
                }
                catch (Exception exception)
                {
                    if (harmony != null)
                    {
                        try
                        {
                            UnpatchOwner(harmony, targets, harmonyId);
                        }
                        catch (Exception cleanupException)
                        {
                            ClientDiagnosticSupport.ReportFailure(
                                "troop-roster-sequence-diagnostic-cleanup",
                                cleanupException);
                        }
                    }
                    installed = false;
                    ClientDiagnosticSupport.ReportFailure(
                        "troop-roster-sequence-diagnostic-disabled",
                        exception);
                }
            }
        }

        public static void BeforeCreateClientInstance(
            object __instance,
            object __0,
            ref CreateObservationState __state)
        {
            __state = default(CreateObservationState);
            try
            {
                if (!installed || IsSaturated())
                    return;
                var instanceId = ReadPayloadId(createClientInstanceMethod, __0);
                __state.RosterId = string.IsNullOrEmpty(instanceId)
                    ? instanceId
                    : "TroopRoster_" + instanceId;
                __state.Armed = true;
                Record(
                    "create-enter",
                    __state.RosterId,
                    "NetworkCreateInstance<TroopRoster>",
                    RegistrationState(
                        __instance,
                        autoRegistryObjectManagerField,
                        __state.RosterId));
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "troop-roster-sequence-create-prefix",
                    exception);
            }
        }

        public static void AfterCreateClientInstance(
            object __instance,
            CreateObservationState __state)
        {
            try
            {
                if (!installed || !__state.Armed)
                    return;
                Record(
                    "create-exit",
                    __state.RosterId,
                    "NetworkCreateInstance<TroopRoster>",
                    RegistrationState(
                        __instance,
                        autoRegistryObjectManagerField,
                        __state.RosterId));
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "troop-roster-sequence-create-postfix",
                    exception);
            }
        }

        public static void BeforeDeltaReceive(
            object __instance,
            object __0,
            MethodBase __originalMethod)
        {
            try
            {
                if (!installed || IsSaturated() || __originalMethod == null)
                    return;
                string deltaType;
                if (!deltaTypes.TryGetValue(__originalMethod, out deltaType))
                    throw new InvalidDataException("Unexpected TroopRoster delta hook method.");
                var rosterId = ReadPayloadId(__originalMethod, __0);
                Record(
                    "delta-receive",
                    rosterId,
                    deltaType,
                    RegistrationState(
                        __instance,
                        deltaObjectManagerField,
                        rosterId));
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "troop-roster-sequence-delta-prefix",
                    exception);
            }
        }

        public static void BeforeGameThreadClosure(
            object __instance,
            MethodBase __originalMethod)
        {
            try
            {
                if (!installed || IsSaturated() || __originalMethod == null)
                    return;
                var accessor = MatchesMethod(__originalMethod, applyClosureAccessor.Method)
                    ? applyClosureAccessor
                    : MatchesMethod(__originalMethod, removeClosureAccessor.Method)
                        ? removeClosureAccessor
                        : null;
                if (accessor == null)
                    throw new InvalidDataException("Unexpected TroopRoster closure hook method.");
                var rosterId = accessor.ReadRosterId(__instance);
                var handler = accessor.ReadOuter(__instance);
                Record(
                    "game-thread-apply",
                    rosterId,
                    accessor.ReadDeltaType(__instance),
                    RegistrationState(
                        handler,
                        deltaObjectManagerField,
                        rosterId));
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "troop-roster-sequence-closure-prefix",
                    exception);
            }
        }

        private static void RequireAutoRegistryCreateMethod(
            MethodInfo method,
            Type openDeclaringType)
        {
            if (method == null ||
                method.DeclaringType != openDeclaringType ||
                !string.Equals(method.Name, "CreateClientInstance", StringComparison.Ordinal) ||
                method.ReturnType != typeof(void) ||
                method.IsStatic ||
                !method.IsPrivate)
            {
                throw new InvalidDataException(
                    "Released Coop AutoRegistryHandler.CreateClientInstance has an incompatible signature.");
            }
            var parameters = method.GetParameters();
            if (parameters.Length != 1 ||
                !parameters[0].ParameterType.IsGenericType ||
                !string.Equals(
                    parameters[0].ParameterType.GetGenericTypeDefinition().FullName,
                    "Common.Messaging.MessagePayload`1",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Released Coop AutoRegistryHandler.CreateClientInstance payload has an incompatible signature.");
            }
            var messageType = parameters[0].ParameterType.GetGenericArguments()[0];
            if (!messageType.IsGenericType ||
                !string.Equals(
                    messageType.GetGenericTypeDefinition().FullName,
                    "GameInterface.Registry.Auto.NetworkCreateInstance`1",
                    StringComparison.Ordinal) ||
                messageType.GetGenericArguments().Length != 1 ||
                messageType.GetGenericArguments()[0] !=
                    openDeclaringType.GetGenericArguments()[0])
            {
                throw new InvalidDataException(
                    "Released Coop NetworkCreateInstance payload has an incompatible signature.");
            }
        }

        private static void RequireClosedAutoRegistryCreateMethod(
            MethodInfo method,
            Type closedDeclaringType)
        {
            if (method == null ||
                method.DeclaringType != closedDeclaringType ||
                method.ContainsGenericParameters)
            {
                throw new InvalidDataException(
                    "Released Coop closed TroopRoster registry method has an incompatible signature.");
            }
            RequireAutoRegistryCreateMethod(method, closedDeclaringType);
        }

        private static MethodInfo FindAutoRegistryCreateMethod(Type declaringType)
        {
            var matches = declaringType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => string.Equals(
                    method.Name,
                    "CreateClientInstance",
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException(
                    "Required Coop AutoRegistryHandler.CreateClientInstance did not resolve exactly once.");
            return matches[0];
        }

        private static MethodInfo FindClosedMethod(Type closedType, string name)
        {
            var matches = closedType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static FieldInfo FindClosedField(Type closedType, string name)
        {
            var matches = closedType.GetFields(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(field => string.Equals(field.Name, name, StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static void RequireObjectManagerField(
            FieldInfo field,
            Type declaringType,
            string name)
        {
            if (field == null ||
                field.DeclaringType != declaringType ||
                !string.Equals(field.Name, name, StringComparison.Ordinal) ||
                !string.Equals(
                    field.FieldType.FullName,
                    "GameInterface.Services.ObjectManager.IObjectManager",
                    StringComparison.Ordinal) ||
                field.IsStatic ||
                !field.IsPrivate ||
                !field.IsInitOnly)
            {
                throw new InvalidDataException(
                    "Released Coop object-manager field has an incompatible signature: " +
                    declaringType.FullName + "::" + name + ".");
            }
        }

        private static MethodInfo RequireDeltaMethod(
            Type declaringType,
            string name)
        {
            var matches = declaringType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                .ToArray();
            var method = matches.Length == 1 ? matches[0] : null;
            if (method == null ||
                method.DeclaringType != declaringType ||
                method.ReturnType != typeof(void) ||
                method.IsStatic ||
                !method.IsPrivate ||
                method.GetParameters().Length != 1)
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster delta method has an incompatible signature: " + name + ".");
            }
            return method;
        }

        private static PayloadIdAccessor BuildPayloadIdAccessor(
            MethodInfo method,
            string expectedMessageTypeName,
            Type expectedGenericArgument,
            string idFieldName)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                throw new InvalidDataException("Required Coop payload parameter count changed.");
            var payloadType = parameters[0].ParameterType;
            if (!payloadType.IsGenericType ||
                !string.Equals(
                    payloadType.GetGenericTypeDefinition().FullName,
                    "Common.Messaging.MessagePayload`1",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Required Coop payload type changed.");
            }
            var messageType = payloadType.GetGenericArguments()[0];
            if (expectedGenericArgument == null)
            {
                if (!string.Equals(
                        messageType.FullName,
                        expectedMessageTypeName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Required Coop message type changed.");
                }
            }
            else if (!messageType.IsGenericType ||
                     !string.Equals(
                         messageType.GetGenericTypeDefinition().FullName,
                         expectedMessageTypeName,
                         StringComparison.Ordinal) ||
                     messageType.GetGenericArguments().Length != 1 ||
                     messageType.GetGenericArguments()[0] != expectedGenericArgument)
            {
                throw new InvalidDataException("Required Coop generic message type changed.");
            }

            var whatProperty = payloadType.GetProperty(
                "What",
                BindingFlags.Instance | BindingFlags.Public);
            var whatGetter = whatProperty == null
                ? null
                : whatProperty.GetGetMethod(false);
            if (whatGetter == null ||
                whatGetter.IsStatic ||
                whatGetter.ReturnType != messageType ||
                whatGetter.GetParameters().Length != 0)
            {
                throw new InvalidDataException("Required Coop MessagePayload.What getter changed.");
            }

            var idField = messageType.GetFields(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .SingleOrDefault(field => string.Equals(
                    field.Name,
                    idFieldName,
                    StringComparison.Ordinal));
            if (idField == null ||
                !string.Equals(idField.Name, idFieldName, StringComparison.Ordinal) ||
                idField.FieldType != typeof(string) ||
                idField.IsStatic ||
                !idField.IsPublic ||
                !idField.IsInitOnly)
            {
                throw new InvalidDataException("Required Coop payload identifier field changed.");
            }
            return new PayloadIdAccessor(whatGetter, idField);
        }

        private static ClosureAccessor BuildClosureAccessor(
            Assembly gameInterface,
            Type deltaHandlerType,
            string typeName,
            string methodName,
            string delegateFieldName,
            string fallbackDeltaType)
        {
            var closureType = ClientMapEventPositionAuthority.RequireType(
                gameInterface,
                typeName);
            var method = ClientMapEventPositionAuthority.FindRequiredMethod(
                closureType,
                methodName,
                typeof(void),
                false);
            if (!method.IsAssembly)
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster closure method visibility changed.");
            }

            var outerField = closureType.GetField(
                "<>4__this",
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (outerField == null ||
                outerField.DeclaringType != closureType ||
                !string.Equals(outerField.Name, "<>4__this", StringComparison.Ordinal) ||
                outerField.FieldType != deltaHandlerType ||
                outerField.IsStatic ||
                !outerField.IsPublic)
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster closure outer field changed.");
            }
            var rosterIdField = closureType.GetField(
                "rosterId",
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (rosterIdField == null ||
                rosterIdField.DeclaringType != closureType ||
                !string.Equals(rosterIdField.Name, "rosterId", StringComparison.Ordinal) ||
                rosterIdField.FieldType != typeof(string) ||
                rosterIdField.IsStatic ||
                !rosterIdField.IsPublic)
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster closure roster ID field changed.");
            }

            FieldInfo delegateField = null;
            if (!string.IsNullOrEmpty(delegateFieldName))
            {
                delegateField = closureType.GetField(
                    delegateFieldName,
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (delegateField == null ||
                    delegateField.DeclaringType != closureType ||
                    !string.Equals(delegateField.Name, delegateFieldName, StringComparison.Ordinal) ||
                    delegateField.IsStatic ||
                    !delegateField.IsPublic ||
                    !typeof(Delegate).IsAssignableFrom(delegateField.FieldType))
                {
                    throw new InvalidDataException(
                        "Released Coop TroopRoster closure delegate field changed.");
                }
            }
            return new ClosureAccessor(
                method,
                outerField,
                rosterIdField,
                delegateField,
                fallbackDeltaType);
        }

        private static bool MatchesMethod(MethodBase first, MethodBase second)
        {
            return first != null && second != null && first.Equals(second);
        }

        private static bool IsExactlyInstalled(
            MethodInfo createMethod,
            IEnumerable<MethodInfo> deltaMethods,
            IEnumerable<MethodInfo> closureMethods,
            string owner,
            MethodInfo createPrefix,
            MethodInfo createPostfix,
            MethodInfo deltaPrefix,
            MethodInfo closurePrefix)
        {
            var createPatches = Harmony.GetPatchInfo(createMethod);
            if (createPatches == null ||
                createPatches.Prefixes.Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal) &&
                    patch.PatchMethod == createPrefix) != 1 ||
                createPatches.Postfixes.Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal) &&
                    patch.PatchMethod == createPostfix) != 1 ||
                ClientDiagnosticSupport.CountOwnedPatches(createPatches, owner) != 2)
            {
                return false;
            }
            foreach (var method in deltaMethods)
            {
                var patches = Harmony.GetPatchInfo(method);
                if (patches == null ||
                    patches.Prefixes.Count(patch =>
                        string.Equals(patch.owner, owner, StringComparison.Ordinal) &&
                        patch.PatchMethod == deltaPrefix) != 1 ||
                    ClientDiagnosticSupport.CountOwnedPatches(patches, owner) != 1)
                {
                    return false;
                }
            }
            foreach (var method in closureMethods)
            {
                var patches = Harmony.GetPatchInfo(method);
                if (patches == null ||
                    patches.Prefixes.Count(patch =>
                        string.Equals(patch.owner, owner, StringComparison.Ordinal) &&
                        patch.PatchMethod == closurePrefix) != 1 ||
                    ClientDiagnosticSupport.CountOwnedPatches(patches, owner) != 1)
                {
                    return false;
                }
            }
            return true;
        }

        private static void UnpatchOwner(
            Harmony harmony,
            IEnumerable<MethodInfo> methods,
            string owner)
        {
            foreach (var method in methods.Distinct())
            {
                if (ClientDiagnosticSupport.CountOwnedPatches(
                        Harmony.GetPatchInfo(method),
                        owner) != 0)
                {
                    harmony.Unpatch(method, HarmonyPatchType.All, owner);
                }
            }
        }

        private static string ReadPayloadId(MethodBase method, object payload)
        {
            if (payload == null)
                return null;
            PayloadIdAccessor accessor;
            if (payloadAccessors == null ||
                !payloadAccessors.TryGetValue(method, out accessor))
            {
                throw new InvalidOperationException(
                    "TroopRoster diagnostic payload accessor is unavailable.");
            }
            return accessor.Read(payload);
        }

        private static string RegistrationState(
            object handler,
            FieldInfo managerField,
            string rosterId)
        {
            if (handler == null || managerField == null || string.IsNullOrEmpty(rosterId))
                return "unknown";
            try
            {
                var manager = managerField.GetValue(handler);
                if (manager == null || objectManagerContainsId == null)
                    return "unknown";
                var result = objectManagerContainsId.Invoke(
                    manager,
                    new object[] { rosterId });
                return result is bool
                    ? ((bool)result ? "true" : "false")
                    : "unknown";
            }
            catch (Exception exception)
            {
                ClientDiagnosticSupport.ReportFailure(
                    "troop-roster-sequence-registration-state",
                    exception);
                return "unknown";
            }
        }

        private static bool IsSaturated()
        {
            lock (Sync)
            {
                return saturated;
            }
        }

        private static void Record(
            string eventName,
            string rosterId,
            string deltaType,
            string registeredState)
        {
            var normalizedId = string.IsNullOrEmpty(rosterId) ? "<unknown>" : rosterId;
            var thread = ClientDiagnosticSupport.CurrentThreadDescription();
            var isGameThread = gameThreadProbe == null
                ? "unknown"
                : gameThreadProbe.Read();
            string line = null;
            string summary = null;
            lock (Sync)
            {
                if (saturated)
                    return;
                if (!RosterIds.Contains(normalizedId))
                {
                    if (RosterIds.Count >= MaxRosterIds)
                    {
                        if (!suppressionSummaryReported)
                        {
                            suppressionSummaryReported = true;
                            summary =
                                "[BCS Coop Bridge][TroopRosterSequenceDiagnostic]" +
                                " suppression=roster-id-cap tracked_ids=" +
                                MaxRosterIds.ToString(CultureInfo.InvariantCulture) +
                                " new_roster_ids_suppressed=true";
                        }
                    }
                    else
                    {
                        RosterIds.Add(normalizedId);
                    }
                }
                if (!RosterIds.Contains(normalizedId))
                {
                    // A new ID beyond the cap is intentionally not retained.
                }
                else
                {
                    var now = Stopwatch.GetTimestamp();
                    RemoveExpiredDedupeEntries(now);
                    var key = string.Join(
                        "\u001f",
                        new[]
                        {
                            eventName ?? string.Empty,
                            normalizedId,
                            deltaType ?? string.Empty,
                            thread ?? string.Empty,
                            isGameThread ?? string.Empty,
                            registeredState ?? string.Empty
                        });
                    long previous;
                    if (!Recent.TryGetValue(key, out previous) ||
                        now - previous < 0 ||
                        now - previous >= DedupeTicks)
                    {
                        Recent[key] = now;
                        sequence++;
                        recordCount++;
                        line =
                            "[BCS Coop Bridge][TroopRosterSequenceDiagnostic] seq=" +
                            sequence.ToString(CultureInfo.InvariantCulture) +
                            " event=" + (eventName ?? "unknown") +
                            " roster=" + ClientDiagnosticSupport.Quote(normalizedId) +
                            " delta=" + ClientDiagnosticSupport.Quote(deltaType) +
                            " thread=" + ClientDiagnosticSupport.Quote(thread) +
                            " is_game_thread=" + isGameThread +
                            " registered=" + (registeredState ?? "unknown");
                        if (recordCount >= MaxRecords)
                        {
                            saturated = true;
                            if (!suppressionSummaryReported)
                            {
                                suppressionSummaryReported = true;
                                summary =
                                    "[BCS Coop Bridge][TroopRosterSequenceDiagnostic]" +
                                    " suppression=record-cap emitted=" +
                                    MaxRecords.ToString(CultureInfo.InvariantCulture) +
                                    " further_events_suppressed=true";
                            }
                        }
                    }
                }
            }
            if (line != null)
                ClientDiagnosticSupport.Write(line);
            if (summary != null)
                ClientDiagnosticSupport.Write(summary);
        }

        private static void RemoveExpiredDedupeEntries(long now)
        {
            List<string> expired = null;
            foreach (var pair in Recent)
            {
                if (now - pair.Value < 0 || now - pair.Value >= DedupeTicks)
                {
                    if (expired == null)
                        expired = new List<string>();
                    expired.Add(pair.Key);
                }
            }
            if (expired == null)
                return;
            foreach (var key in expired)
                Recent.Remove(key);
        }

        public struct CreateObservationState
        {
            internal string RosterId;
            internal bool Armed;
        }

        private sealed class PayloadIdAccessor
        {
            private readonly MethodInfo whatGetter;
            private readonly FieldInfo idField;

            internal PayloadIdAccessor(MethodInfo whatGetter, FieldInfo idField)
            {
                this.whatGetter = whatGetter;
                this.idField = idField;
            }

            internal string Read(object payload)
            {
                var message = whatGetter.Invoke(payload, null);
                return message == null ? null : idField.GetValue(message) as string;
            }
        }

        private sealed class ClosureAccessor
        {
            private readonly FieldInfo outerField;
            private readonly FieldInfo rosterIdField;
            private readonly FieldInfo delegateField;
            private readonly string fallbackDeltaType;

            internal ClosureAccessor(
                MethodInfo method,
                FieldInfo outerField,
                FieldInfo rosterIdField,
                FieldInfo delegateField,
                string fallbackDeltaType)
            {
                Method = method;
                this.outerField = outerField;
                this.rosterIdField = rosterIdField;
                this.delegateField = delegateField;
                this.fallbackDeltaType = fallbackDeltaType;
            }

            internal MethodInfo Method { get; private set; }

            internal object ReadOuter(object closure)
            {
                return closure == null ? null : outerField.GetValue(closure);
            }

            internal string ReadRosterId(object closure)
            {
                return closure == null ? null : rosterIdField.GetValue(closure) as string;
            }

            internal string ReadDeltaType(object closure)
            {
                if (closure == null || delegateField == null)
                    return fallbackDeltaType;
                try
                {
                    var apply = delegateField.GetValue(closure) as Delegate;
                    if (apply == null)
                        return fallbackDeltaType;
                    var methodName = apply.Method.Name;
                    if (methodName.IndexOf(
                            "Handle_NetworkSetNumber",
                            StringComparison.Ordinal) >= 0)
                    {
                        return "NetworkTroopRosterSetNumber";
                    }
                    if (methodName.IndexOf(
                            "Handle_NetworkSetWoundedNumber",
                            StringComparison.Ordinal) >= 0)
                    {
                        return "NetworkTroopRosterSetWoundedNumber";
                    }
                    if (methodName.IndexOf(
                            "Handle_NetworkElementBatch",
                            StringComparison.Ordinal) >= 0)
                    {
                        return "NetworkTroopRosterElementBatch";
                    }
                }
                catch (Exception exception)
                {
                    ClientDiagnosticSupport.ReportFailure(
                        "troop-roster-sequence-delta-inference",
                        exception);
                }
                return fallbackDeltaType;
            }
        }
    }
#endif

    internal static class CoopRegistryLifecycleCompatibility
    {
        private const string ArmyRegistryOwner =
            "BCS.CoopBridge.registry.army-initial-identity";
        private const string ArmyNullTargetSenderOwner =
            "BCS.CoopBridge.registry.army-null-target-sender";
        private const string ArmyNullTargetReceiverOwner =
            "BCS.CoopBridge.registry.army-null-target-receiver";
        private const string PartyComponentDeferredOwner =
            "BCS.CoopBridge.registry.party-component-deferred";
        private const string RegistryReadyOwner =
            "BCS.CoopBridge.registry.party-component-ready";
        private const string RegistryClearOwner =
            "BCS.CoopBridge.registry.party-component-clear";

        private static readonly object Sync = new object();
        private static readonly List<DeferredPartyComponentUpdate>
            DeferredPartyComponentUpdates = new List<DeferredPartyComponentUpdate>();
        private static readonly HashSet<object> PartyComponentReplayPayloads =
            new HashSet<object>(ReferenceComparer.Instance);

        private static bool armyCompatibilityInstalled;
        private static bool partyComponentCompatibilityInstalled;
        private static bool partyRegistriesReady;

        private static Type kingdomType;
        private static Type armyType;
        private static Type mobilePartyType;
        private static PropertyInfo campaignCurrentProperty;
        private static PropertyInfo campaignKingdomsProperty;
        private static PropertyInfo kingdomArmiesProperty;
        private static PropertyInfo kingdomStringIdProperty;
        private static PropertyInfo armyKingdomProperty;
        private static PropertyInfo armyLeaderPartyProperty;
        private static PropertyInfo mobilePartyStringIdProperty;
        private static PropertyInfo mobilePartyArmyProperty;
        private static MethodInfo registerExistingArmyMethod;

        private static FieldInfo armyHandlerObjectManagerField;
        private static FieldInfo armyHandlerNetworkField;
        private static PropertyInfo armyChangedPayloadWhatProperty;
        private static FieldInfo armyChangedArmyField;
        private static FieldInfo armyChangedTargetField;
        private static PropertyInfo networkArmyPayloadWhatProperty;
        private static FieldInfo networkArmyIdField;
        private static FieldInfo networkArmyTargetIdField;
        private static MethodInfo objectManagerTryGetIdMethod;
        private static MethodInfo objectManagerTryGetArmyMethod;
        private static MethodInfo objectManagerContainsMethod;
        private static ConstructorInfo networkArmyMessageConstructor;
        private static MethodInfo networkSendAllMethod;
        private static MethodInfo gameThreadRunSafeMethod;
        private static MethodInfo setArmyAiBehaviorObjectMethod;

        private static FieldInfo partyComponentHandlerObjectManagerField;
        private static PropertyInfo partyComponentPayloadWhatProperty;
        private static FieldInfo partyComponentInstanceField;
        private static FieldInfo partyComponentMobilePartyField;
        private static MethodInfo partyComponentHandlerMethod;
        private static MethodInfo autoRegistryRegisterAllMethod;
        private static MethodInfo registryReadyPublishMethod;
        private static MethodInfo replayDeferredPartyComponentUpdatesMethod;

        internal static void Install(bool includeServerLoadDeferral)
        {
            lock (Sync)
            {
                var gameInterface = ResolveSingleLoadedAssembly("GameInterface");
                var common = ResolveSingleLoadedAssembly("Common");

                if (!armyCompatibilityInstalled)
                {
                    InstallArmyCompatibility(gameInterface, common);
                    armyCompatibilityInstalled = true;
                }

                if (includeServerLoadDeferral &&
                    !partyComponentCompatibilityInstalled)
                {
                    InstallPartyComponentLoadDeferral(gameInterface);
                    partyComponentCompatibilityInstalled = true;
                }
            }

            Console.WriteLine(
                "[BCS Coop Bridge] Installed deterministic Army IDs and nullable Army AI targets" +
                (includeServerLoadDeferral
                    ? "; PartyComponent load deferral is active."
                    : "."));
        }

        private static void InstallArmyCompatibility(
            Assembly gameInterface,
            Assembly common)
        {
            var campaignSystem = ResolveSingleLoadedAssembly(
                "TaleWorlds.CampaignSystem");
            var campaignType = RequireType(
                campaignSystem,
                "TaleWorlds.CampaignSystem.Campaign");
            kingdomType = RequireType(
                campaignSystem,
                "TaleWorlds.CampaignSystem.Kingdom");
            armyType = RequireType(
                campaignSystem,
                "TaleWorlds.CampaignSystem.Army");
            mobilePartyType = RequireType(
                campaignSystem,
                "TaleWorlds.CampaignSystem.Party.MobileParty");

            campaignCurrentProperty = RequireProperty(
                campaignType,
                "Current",
                true,
                campaignType);
            campaignKingdomsProperty = RequireEnumerableProperty(
                campaignType,
                "Kingdoms",
                false);
            kingdomArmiesProperty = RequireEnumerableProperty(
                kingdomType,
                "Armies",
                false);
            kingdomStringIdProperty = RequireProperty(
                kingdomType,
                "StringId",
                false,
                typeof(string));
            armyKingdomProperty = RequireProperty(
                armyType,
                "Kingdom",
                false,
                kingdomType);
            armyLeaderPartyProperty = RequireProperty(
                armyType,
                "LeaderParty",
                false,
                mobilePartyType);
            mobilePartyStringIdProperty = RequireProperty(
                mobilePartyType,
                "StringId",
                false,
                typeof(string));
            mobilePartyArmyProperty = RequireProperty(
                mobilePartyType,
                "Army",
                false,
                armyType);

            var armyRegistryType = RequireType(
                gameInterface,
                "GameInterface.Services.Armies.ArmyRegistry");
            var registerAllArmies = RequireSingleMethod(
                armyRegistryType,
                "RegisterAllObjects",
                false,
                typeof(void),
                method => method.IsPublic && method.GetParameters().Length == 0);
            registerExistingArmyMethod = RequireSingleBaseMethod(
                armyRegistryType,
                "RegisterExistingObject",
                typeof(void),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType == armyType;
                });
            PatchPrefixOnce(
                registerAllArmies,
                ArmyRegistryOwner,
                nameof(BeforeRegisterAllArmies));

            InstallNullableArmyTargetCompatibility(gameInterface, common);
        }

        private static void InstallNullableArmyTargetCompatibility(
            Assembly gameInterface,
            Assembly common)
        {
            var handlerType = RequireType(
                gameInterface,
                "GameInterface.Services.Armies.Handlers.ArmyHandler");
            var changedType = RequireType(
                gameInterface,
                "GameInterface.Services.Armies.Messages.ArmyAiBehaviorObjectChanged");
            var networkType = RequireType(
                gameInterface,
                "GameInterface.Services.Armies.Messages.NetworkSetArmyAiBehaviorObject");
            var objectManagerType = RequireType(
                gameInterface,
                "GameInterface.Services.ObjectManager.IObjectManager");
            var armyPatchesType = RequireType(
                gameInterface,
                "GameInterface.Services.Armies.Patches.ArmyPatches");
            var networkInterface = RequireType(
                common,
                "Common.Network.INetwork");
            var messageInterface = RequireType(
                common,
                "Common.Messaging.IMessage");
            var gameThreadType = RequireType(common, "Common.GameThread");

            var sender = RequirePayloadHandler(
                handlerType,
                "HandleArmyAiBehaviorObjectChanged",
                changedType);
            var receiver = RequirePayloadHandler(
                handlerType,
                "HandleNetworkSetArmyAiBehaviorObject",
                networkType);

            armyHandlerObjectManagerField = RequireField(
                handlerType,
                "objectManager",
                false,
                objectManagerType);
            armyHandlerNetworkField = RequireField(
                handlerType,
                "network",
                false,
                networkInterface);
            armyChangedPayloadWhatProperty = RequireProperty(
                sender.GetParameters()[0].ParameterType,
                "What",
                false,
                changedType);
            armyChangedArmyField = RequireField(
                changedType,
                "Army",
                false,
                armyType);
            armyChangedTargetField = RequireField(
                changedType,
                "AiBehaviorObject",
                false,
                null);
            if (!string.Equals(
                    armyChangedTargetField.FieldType.FullName,
                    "TaleWorlds.CampaignSystem.Map.IMapPoint",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Army AI behavior target has an unexpected runtime type.");
            }

            networkArmyPayloadWhatProperty = RequireProperty(
                receiver.GetParameters()[0].ParameterType,
                "What",
                false,
                networkType);
            networkArmyIdField = RequireField(
                networkType,
                "ArmyId",
                false,
                typeof(string));
            networkArmyTargetIdField = RequireField(
                networkType,
                "AiBehaviorObjectId",
                false,
                typeof(string));
            RequireField(networkType, "IsSettlement", false, typeof(bool));

            objectManagerTryGetIdMethod = RequireSingleMethod(
                objectManagerType,
                "TryGetId",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return !method.IsGenericMethod &&
                           parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(object) &&
                           parameters[1].ParameterType ==
                               typeof(string).MakeByRefType();
                });
            var tryGetObjectDefinition = RequireSingleMethod(
                objectManagerType,
                "TryGetObject",
                false,
                typeof(bool),
                method =>
                {
                    if (!method.IsGenericMethodDefinition ||
                        method.GetGenericArguments().Length != 1)
                    {
                        return false;
                    }
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType.IsByRef &&
                           parameters[1].ParameterType.GetElementType().IsGenericParameter;
                });
            objectManagerTryGetArmyMethod =
                tryGetObjectDefinition.MakeGenericMethod(armyType);
            objectManagerContainsMethod = RequireSingleMethod(
                objectManagerType,
                "Contains",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                           parameters[0].ParameterType == typeof(object);
                });

            var constructors = networkType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic)
                .Where(constructor =>
                {
                    var parameters = constructor.GetParameters();
                    return parameters.Length == 3 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType == typeof(string) &&
                           parameters[2].ParameterType == typeof(bool);
                })
                .ToArray();
            if (constructors.Length != 1)
            {
                throw new MissingMethodException(
                    networkType.FullName,
                    ".ctor(string,string,bool) resolved " +
                    constructors.Length + " times");
            }
            networkArmyMessageConstructor = constructors[0];
            if (!messageInterface.IsAssignableFrom(networkType))
            {
                throw new InvalidDataException(
                    "Network Army AI behavior message does not implement IMessage.");
            }
            networkSendAllMethod = RequireSingleMethod(
                networkInterface,
                "SendAll",
                false,
                typeof(void),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                           parameters[0].ParameterType == messageInterface;
                });
            gameThreadRunSafeMethod = RequireSingleMethod(
                gameThreadType,
                "RunSafe",
                true,
                typeof(void),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 5 &&
                           parameters[0].ParameterType == typeof(Action) &&
                           parameters[1].ParameterType == typeof(bool) &&
                           parameters[2].ParameterType == typeof(string) &&
                           parameters[3].ParameterType == typeof(string) &&
                           parameters[4].ParameterType == typeof(string);
                });
            setArmyAiBehaviorObjectMethod = RequireSingleMethod(
                armyPatchesType,
                "SetAiBehaviorObject",
                true,
                typeof(void),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == armyType &&
                           parameters[1].ParameterType ==
                               armyChangedTargetField.FieldType;
                });

            PatchPrefixOnce(
                sender,
                ArmyNullTargetSenderOwner,
                nameof(BeforeArmyAiBehaviorObjectChanged));
            PatchPrefixOnce(
                receiver,
                ArmyNullTargetReceiverOwner,
                nameof(BeforeNetworkSetArmyAiBehaviorObject));
        }

        private static void InstallPartyComponentLoadDeferral(
            Assembly gameInterface)
        {
            var handlerType = RequireType(
                gameInterface,
                "GameInterface.Services.PartyComponents.Handlers.PartyComponentHandler");
            var messageType = RequireType(
                gameInterface,
                "GameInterface.Services.PartyComponents.Messages.PartyComponentMobilePartyUpdated");
            var objectManagerType = armyHandlerObjectManagerField.FieldType;
            var partyComponentType = RequireType(
                ResolveSingleLoadedAssembly("TaleWorlds.CampaignSystem"),
                "TaleWorlds.CampaignSystem.Party.PartyComponents.PartyComponent");

            partyComponentHandlerMethod = RequirePayloadHandler(
                handlerType,
                "Handle_PartyComponentMobilePartyUpdated",
                messageType);
            partyComponentHandlerObjectManagerField = RequireField(
                handlerType,
                "objectManager",
                false,
                objectManagerType);
            partyComponentPayloadWhatProperty = RequireProperty(
                partyComponentHandlerMethod.GetParameters()[0].ParameterType,
                "What",
                false,
                messageType);
            partyComponentInstanceField = RequireField(
                messageType,
                "Instance",
                false,
                partyComponentType);
            partyComponentMobilePartyField = RequireField(
                messageType,
                "MobileParty",
                false,
                mobilePartyType);

            var registryManagerType = RequireType(
                gameInterface,
                "GameInterface.Registry.RegistryManager");
            var autoRegistryFactoryType = RequireType(
                gameInterface,
                "GameInterface.Registry.Auto.IAutoRegistryFactory");
            var readyMessageType = RequireType(
                gameInterface,
                "GameInterface.Registry.Messages.AllGameObjectsRegistered");
            var messageBrokerType = RequireType(
                ResolveSingleLoadedAssembly("Common"),
                "Common.Messaging.IMessageBroker");
            var registerAll = RequireSingleMethod(
                registryManagerType,
                "RegisterAllGameObjects",
                false,
                typeof(void),
                method => method.IsPublic && method.GetParameters().Length == 0);
            autoRegistryRegisterAllMethod = RequireSingleMethod(
                autoRegistryFactoryType,
                "RegisterAll",
                false,
                typeof(void),
                method => method.IsPublic && method.GetParameters().Length == 0);
            var publishDefinition = RequireSingleMethod(
                messageBrokerType,
                "Publish",
                false,
                typeof(void),
                method =>
                {
                    if (!method.IsPublic || !method.IsGenericMethodDefinition)
                        return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(object) &&
                           parameters[1].ParameterType.IsGenericParameter;
                });
            registryReadyPublishMethod = publishDefinition.MakeGenericMethod(
                readyMessageType);
            RequireField(
                registryManagerType,
                "autoRegistryFactory",
                false,
                autoRegistryFactoryType);
            RequireField(
                registryManagerType,
                "messageBroker",
                false,
                messageBrokerType);
            var clearAll = RequireSingleMethod(
                registryManagerType,
                "ClearAllRegistries",
                false,
                typeof(void),
                method => method.IsPublic && method.GetParameters().Length == 0);
            replayDeferredPartyComponentUpdatesMethod =
                typeof(CoopRegistryLifecycleCompatibility).GetMethod(
                    nameof(ReplayDeferredPartyComponentUpdates),
                    BindingFlags.Static | BindingFlags.Public);
            if (replayDeferredPartyComponentUpdatesMethod == null)
            {
                throw new MissingMethodException(
                    typeof(CoopRegistryLifecycleCompatibility).FullName,
                    nameof(ReplayDeferredPartyComponentUpdates));
            }

            partyRegistriesReady = false;
            DeferredPartyComponentUpdates.Clear();
            PartyComponentReplayPayloads.Clear();
            PatchPrefixOnce(
                partyComponentHandlerMethod,
                PartyComponentDeferredOwner,
                nameof(BeforePartyComponentMobilePartyUpdated));
            PatchTranspilerOnce(
                registerAll,
                RegistryReadyOwner,
                nameof(TranspileRegisterAllGameObjects));
            PatchPrefixOnce(
                clearAll,
                RegistryClearOwner,
                nameof(BeforeClearAllRegistries));
        }

        public static bool BeforeRegisterAllArmies(object __instance)
        {
            if (__instance == null)
                throw new InvalidOperationException("ArmyRegistry instance is null.");

            var campaign = campaignCurrentProperty.GetValue(null, null);
            if (campaign == null)
                return false;

            var kingdoms = campaignKingdomsProperty.GetValue(campaign, null)
                as IEnumerable;
            if (kingdoms == null)
            {
                throw new InvalidDataException(
                    "Campaign.Kingdoms is not enumerable.");
            }

            var registrations = new List<ArmyRegistration>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var armies = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var kingdom in kingdoms)
            {
                if (kingdom == null || !kingdomType.IsInstanceOfType(kingdom))
                {
                    throw new InvalidDataException(
                        "Campaign contains a null or incompatible Kingdom.");
                }

                var kingdomId = kingdomStringIdProperty.GetValue(kingdom, null)
                    as string;
                if (string.IsNullOrWhiteSpace(kingdomId))
                {
                    throw new InvalidDataException(
                        "Cannot register an Army for a Kingdom without a stable StringId.");
                }

                var kingdomArmies = kingdomArmiesProperty.GetValue(kingdom, null)
                    as IEnumerable;
                if (kingdomArmies == null)
                {
                    throw new InvalidDataException(
                        "Kingdom.Armies is not enumerable for " + kingdomId + ".");
                }

                foreach (var army in kingdomArmies)
                {
                    if (army == null || !armyType.IsInstanceOfType(army))
                    {
                        throw new InvalidDataException(
                            "Kingdom " + kingdomId +
                            " contains a null or incompatible Army.");
                    }
                    if (!armies.Add(army))
                    {
                        throw new InvalidDataException(
                            "An Army is listed more than once in Campaign.Kingdoms.");
                    }
                    if (!ReferenceEquals(
                            armyKingdomProperty.GetValue(army, null),
                            kingdom))
                    {
                        throw new InvalidDataException(
                            "Army Kingdom ownership is inconsistent for " +
                            kingdomId + ".");
                    }

                    var leaderParty = armyLeaderPartyProperty.GetValue(army, null);
                    if (leaderParty == null ||
                        !mobilePartyType.IsInstanceOfType(leaderParty))
                    {
                        throw new InvalidDataException(
                            "Army in Kingdom " + kingdomId +
                            " has no stable leader party.");
                    }
                    if (!ReferenceEquals(
                            mobilePartyArmyProperty.GetValue(leaderParty, null),
                            army))
                    {
                        throw new InvalidDataException(
                            "Army leader-party ownership is inconsistent for Kingdom " +
                            kingdomId + ".");
                    }

                    var leaderId = mobilePartyStringIdProperty.GetValue(
                        leaderParty,
                        null) as string;
                    if (string.IsNullOrWhiteSpace(leaderId))
                    {
                        throw new InvalidDataException(
                            "Army in Kingdom " + kingdomId +
                            " has a leader party without a stable StringId.");
                    }

                    // Keep the suffix nonnumeric so AutoRegistryBase does not
                    // mistake a leader-party suffix for an Army counter.
                    var id = "v1_k" +
                             kingdomId.Length.ToString(CultureInfo.InvariantCulture) +
                             ":" + kingdomId + "_p" +
                             leaderId.Length.ToString(CultureInfo.InvariantCulture) +
                             ":" + leaderId + "_stable";
                    if (!ids.Add(id))
                    {
                        throw new InvalidDataException(
                            "Duplicate deterministic Army registry ID: " + id + ".");
                    }
                    registrations.Add(new ArmyRegistration(id, army));
                }
            }

            foreach (var registration in registrations)
            {
                Invoke(
                    registerExistingArmyMethod,
                    __instance,
                    new[] { (object)registration.Id, registration.Army },
                    "register deterministic Army identity");
            }
            return false;
        }

        public static bool BeforeArmyAiBehaviorObjectChanged(
            object __instance,
            object __0)
        {
            if (__instance == null || __0 == null)
                throw new InvalidOperationException("Army AI behavior payload is null.");

            var change = armyChangedPayloadWhatProperty.GetValue(__0, null);
            if (change == null)
                throw new InvalidDataException("Army AI behavior change is null.");
            if (armyChangedTargetField.GetValue(change) != null)
                return true;

            var army = armyChangedArmyField.GetValue(change);
            var objectManager = armyHandlerObjectManagerField.GetValue(__instance);
            if (army == null || objectManager == null)
                return true;

            var lookup = new[] { army, null };
            if (!(bool)Invoke(
                    objectManagerTryGetIdMethod,
                    objectManager,
                    lookup,
                    "resolve Army ID for nullable AI target"))
            {
                // Preserve Coop's original logged failure for an unresolved Army.
                return true;
            }
            var armyId = lookup[1] as string;
            if (string.IsNullOrEmpty(armyId))
            {
                throw new InvalidDataException(
                    "ObjectManager returned an empty ID for a registered Army.");
            }

            var message = networkArmyMessageConstructor.Invoke(
                new object[] { armyId, string.Empty, false });
            var network = armyHandlerNetworkField.GetValue(__instance);
            if (network == null)
                throw new InvalidOperationException("ArmyHandler network is null.");
            Invoke(
                networkSendAllMethod,
                network,
                new[] { message },
                "send nullable Army AI target");
            return false;
        }

        public static bool BeforeNetworkSetArmyAiBehaviorObject(
            object __instance,
            object __0)
        {
            if (__instance == null || __0 == null)
                throw new InvalidOperationException("Network Army AI behavior payload is null.");

            var message = networkArmyPayloadWhatProperty.GetValue(__0, null);
            if (message == null)
                throw new InvalidDataException("Network Army AI behavior message is null.");
            var targetId = networkArmyTargetIdField.GetValue(message) as string;
            if (!string.IsNullOrEmpty(targetId))
                return true;

            var armyId = networkArmyIdField.GetValue(message) as string;
            if (string.IsNullOrEmpty(armyId))
                return true;
            var objectManager = armyHandlerObjectManagerField.GetValue(__instance);
            if (objectManager == null)
                throw new InvalidOperationException("ArmyHandler ObjectManager is null.");

            var lookup = new object[] { armyId, null };
            if (!(bool)Invoke(
                    objectManagerTryGetArmyMethod,
                    objectManager,
                    lookup,
                    "resolve Army for nullable AI target"))
            {
                // Preserve Coop's original logged failure for an unresolved Army.
                return true;
            }
            var army = lookup[1];
            if (army == null)
            {
                throw new InvalidDataException(
                    "ObjectManager returned null for a registered Army.");
            }

            Action applyNullTarget = () => Invoke(
                setArmyAiBehaviorObjectMethod,
                null,
                new[] { army, null },
                "apply nullable Army AI target");
            Invoke(
                gameThreadRunSafeMethod,
                null,
                new object[]
                {
                    applyNullTarget,
                    false,
                    "BCS nullable Army AI target",
                    null,
                    null
                },
                "queue nullable Army AI target");
            return false;
        }

        public static bool BeforePartyComponentMobilePartyUpdated(
            object __instance,
            object __0)
        {
            if (__instance == null || __0 == null)
                throw new InvalidOperationException("PartyComponent update payload is null.");

            lock (Sync)
            {
                if (partyRegistriesReady ||
                    PartyComponentReplayPayloads.Contains(__0))
                    return true;
            }

            var update = partyComponentPayloadWhatProperty.GetValue(__0, null);
            if (update == null)
                throw new InvalidDataException("PartyComponent update is null.");
            var component = partyComponentInstanceField.GetValue(update);
            var mobileParty = partyComponentMobilePartyField.GetValue(update);
            if (component == null || mobileParty == null)
                return true;

            var objectManager = partyComponentHandlerObjectManagerField.GetValue(
                __instance);
            if (objectManager == null)
                throw new InvalidOperationException("PartyComponent ObjectManager is null.");
            var componentRegistered = (bool)Invoke(
                objectManagerContainsMethod,
                objectManager,
                new[] { component },
                "check PartyComponent registration");
            var mobilePartyRegistered = (bool)Invoke(
                objectManagerContainsMethod,
                objectManager,
                new[] { mobileParty },
                "check MobileParty registration");
            if (!componentRegistered || mobilePartyRegistered)
                return true;

            lock (Sync)
            {
                if (partyRegistriesReady ||
                    PartyComponentReplayPayloads.Contains(__0))
                    return true;
                DeferredPartyComponentUpdates.Add(
                    new DeferredPartyComponentUpdate(
                        __instance,
                        __0,
                        component,
                        mobileParty));
            }
            return false;
        }

        public static IEnumerable<CodeInstruction> TranspileRegisterAllGameObjects(
            IEnumerable<CodeInstruction> instructions)
        {
            var rewritten = instructions.ToList();
            var registerAllIndices = rewritten
                .Select((instruction, index) => new { instruction, index })
                .Where(candidate =>
                    candidate.instruction.opcode ==
                        System.Reflection.Emit.OpCodes.Callvirt &&
                    Equals(
                        candidate.instruction.operand as MethodInfo,
                        autoRegistryRegisterAllMethod))
                .Select(candidate => candidate.index)
                .ToArray();
            var readyPublishIndices = rewritten
                .Select((instruction, index) => new { instruction, index })
                .Where(candidate =>
                    candidate.instruction.opcode ==
                        System.Reflection.Emit.OpCodes.Callvirt &&
                    Equals(
                        candidate.instruction.operand as MethodInfo,
                        registryReadyPublishMethod))
                .Select(candidate => candidate.index)
                .ToArray();
            if (registerAllIndices.Length != 1 || readyPublishIndices.Length != 1)
            {
                throw new InvalidDataException(
                    "Released RegistryManager.RegisterAllGameObjects must contain exactly " +
                    "one IAutoRegistryFactory.RegisterAll call and one " +
                    "AllGameObjectsRegistered publish.");
            }
            if (registerAllIndices[0] >= readyPublishIndices[0])
            {
                throw new InvalidDataException(
                    "Released RegistryManager.RegisterAllGameObjects publishes readiness " +
                    "before registry registration completes.");
            }

            rewritten.Insert(
                registerAllIndices[0] + 1,
                new CodeInstruction(
                    System.Reflection.Emit.OpCodes.Call,
                    replayDeferredPartyComponentUpdatesMethod));
            return rewritten;
        }

        public static void ReplayDeferredPartyComponentUpdates()
        {
            DeferredPartyComponentUpdate[] deferred;
            lock (Sync)
            {
                if (partyRegistriesReady)
                    return;
                deferred = DeferredPartyComponentUpdates.ToArray();
            }

            foreach (var update in deferred)
            {
                var objectManager = partyComponentHandlerObjectManagerField.GetValue(
                    update.Handler);
                if (objectManager == null ||
                    !(bool)Invoke(
                        objectManagerContainsMethod,
                        objectManager,
                        new[] { update.Component },
                        "validate deferred PartyComponent registration") ||
                    !(bool)Invoke(
                        objectManagerContainsMethod,
                        objectManager,
                        new[] { update.MobileParty },
                        "validate deferred MobileParty registration"))
                {
                    throw new InvalidOperationException(
                        "Deferred PartyComponent update remained unresolved after " +
                        "IAutoRegistryFactory.RegisterAll.");
                }
            }
            foreach (var update in deferred)
            {
                lock (Sync)
                {
                    if (update.Replayed)
                        continue;
                    if (!DeferredPartyComponentUpdates.Contains(update))
                    {
                        throw new InvalidOperationException(
                            "Deferred PartyComponent queue changed during replay.");
                    }
                    if (!PartyComponentReplayPayloads.Add(update.Payload))
                    {
                        throw new InvalidOperationException(
                            "Deferred PartyComponent payload is already replaying.");
                    }
                }

                var replayed = false;
                try
                {
                    Invoke(
                        partyComponentHandlerMethod,
                        update.Handler,
                        new[] { update.Payload },
                        "replay deferred PartyComponent update");
                    replayed = true;
                }
                finally
                {
                    lock (Sync)
                    {
                        PartyComponentReplayPayloads.Remove(update.Payload);
                        if (replayed)
                            update.Replayed = true;
                    }
                }
            }

            lock (Sync)
            {
                if (DeferredPartyComponentUpdates.Count != deferred.Length ||
                    DeferredPartyComponentUpdates
                        .Where((update, index) =>
                            !ReferenceEquals(update, deferred[index]))
                        .Any() ||
                    deferred.Any(update => !update.Replayed))
                {
                    throw new InvalidOperationException(
                        "Deferred PartyComponent queue changed during replay.");
                }

                partyRegistriesReady = true;
                DeferredPartyComponentUpdates.Clear();
            }

            if (deferred.Length != 0)
            {
                Console.WriteLine(
                    "[BCS Coop Bridge] Replayed " +
                    deferred.Length.ToString(CultureInfo.InvariantCulture) +
                    " PartyComponent updates before campaign-ready publication.");
            }
        }

        public static void BeforeClearAllRegistries()
        {
            int discarded;
            lock (Sync)
            {
                partyRegistriesReady = false;
                discarded = DeferredPartyComponentUpdates.Count;
                DeferredPartyComponentUpdates.Clear();
                PartyComponentReplayPayloads.Clear();
            }
            if (discarded != 0)
            {
                Console.WriteLine(
                    "[BCS Coop Bridge] WARNING: Registry clear discarded " +
                    discarded.ToString(CultureInfo.InvariantCulture) +
                    " deferred PartyComponent updates from an incomplete load.");
            }
        }

        private static MethodInfo RequirePayloadHandler(
            Type handlerType,
            string methodName,
            Type messageType)
        {
            return RequireSingleMethod(
                handlerType,
                methodName,
                false,
                typeof(void),
                method =>
                {
                    if (!method.IsPrivate)
                        return false;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 ||
                        !parameters[0].ParameterType.IsGenericType)
                    {
                        return false;
                    }
                    var payloadType = parameters[0].ParameterType;
                    return string.Equals(
                               payloadType.GetGenericTypeDefinition().FullName,
                               "Common.Messaging.MessagePayload`1",
                               StringComparison.Ordinal) &&
                           payloadType.GetGenericArguments()[0] == messageType;
                });
        }

        private static Assembly ResolveSingleLoadedAssembly(string simpleName)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => string.Equals(
                    assembly.GetName().Name,
                    simpleName,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new FileLoadException(
                    "Expected exactly one loaded " + simpleName +
                    " assembly, found " + matches.Length + ".");
            }
            return matches[0];
        }

        private static Type RequireType(Assembly assembly, string fullName)
        {
            var type = assembly.GetType(fullName, false, false);
            if (type == null)
                throw new TypeLoadException("Required type is missing: " + fullName + ".");
            return type;
        }

        private static PropertyInfo RequireEnumerableProperty(
            Type type,
            string name,
            bool isStatic)
        {
            var property = RequireProperty(type, name, isStatic, null);
            if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            {
                throw new InvalidDataException(
                    type.FullName + "." + name + " is not enumerable.");
            }
            return property;
        }

        private static PropertyInfo RequireProperty(
            Type type,
            string name,
            bool isStatic,
            Type propertyType)
        {
            var properties = type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Where(property =>
                {
                    var getter = property.GetGetMethod(true);
                    return string.Equals(property.Name, name, StringComparison.Ordinal) &&
                           getter != null && getter.IsStatic == isStatic &&
                           (propertyType == null ||
                            property.PropertyType == propertyType);
                })
                .ToArray();
            if (properties.Length != 1)
                throw new MissingMemberException(type.FullName, name);
            return properties[0];
        }

        private static FieldInfo RequireField(
            Type type,
            string name,
            bool isStatic,
            Type fieldType)
        {
            var fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(field =>
                    string.Equals(field.Name, name, StringComparison.Ordinal) &&
                    field.IsStatic == isStatic &&
                    (fieldType == null || field.FieldType == fieldType))
                .ToArray();
            if (fields.Length != 1)
                throw new MissingFieldException(type.FullName, name);
            return fields[0];
        }

        private static MethodInfo RequireSingleMethod(
            Type type,
            string name,
            bool isStatic,
            Type returnType,
            Func<MethodInfo, bool> predicate)
        {
            var methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, name, StringComparison.Ordinal) &&
                    method.IsStatic == isStatic &&
                    method.ReturnType == returnType &&
                    predicate(method))
                .ToArray();
            if (methods.Length != 1)
            {
                throw new MissingMethodException(
                    type.FullName,
                    name + " resolved " + methods.Length + " times");
            }
            return methods[0];
        }

        private static MethodInfo RequireSingleBaseMethod(
            Type type,
            string name,
            Type returnType,
            Func<MethodInfo, bool> predicate)
        {
            var methods = new List<MethodInfo>();
            for (var current = type.BaseType;
                 current != null;
                 current = current.BaseType)
            {
                methods.AddRange(current.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(method =>
                        string.Equals(method.Name, name, StringComparison.Ordinal) &&
                        !method.IsStatic && method.ReturnType == returnType &&
                        predicate(method)));
            }
            if (methods.Count != 1)
            {
                throw new MissingMethodException(
                    type.FullName,
                    name + " resolved " + methods.Count + " times in base types");
            }
            return methods[0];
        }

        private static void PatchPrefixOnce(
            MethodInfo target,
            string owner,
            string patchName)
        {
            var existing = Harmony.GetPatchInfo(target);
            var count = existing == null
                ? 0
                : existing.Prefixes.Count(candidate =>
                    string.Equals(candidate.owner, owner, StringComparison.Ordinal));
            if (count == 1)
                return;
            if (count != 0)
                throw new InvalidOperationException("Duplicate Harmony owner: " + owner + ".");

            var patchMethod = typeof(CoopRegistryLifecycleCompatibility).GetMethod(
                patchName,
                BindingFlags.Static | BindingFlags.Public);
            if (patchMethod == null)
            {
                throw new MissingMethodException(
                    typeof(CoopRegistryLifecycleCompatibility).FullName,
                    patchName);
            }
            new Harmony(owner).Patch(target, prefix: new HarmonyMethod(patchMethod));
            VerifyPatchOwner(target, owner, true);
        }

        private static void PatchPostfixOnce(
            MethodInfo target,
            string owner,
            string patchName)
        {
            var existing = Harmony.GetPatchInfo(target);
            var count = existing == null
                ? 0
                : existing.Postfixes.Count(candidate =>
                    string.Equals(candidate.owner, owner, StringComparison.Ordinal));
            if (count == 1)
                return;
            if (count != 0)
                throw new InvalidOperationException("Duplicate Harmony owner: " + owner + ".");

            var patchMethod = typeof(CoopRegistryLifecycleCompatibility).GetMethod(
                patchName,
                BindingFlags.Static | BindingFlags.Public);
            if (patchMethod == null)
            {
                throw new MissingMethodException(
                    typeof(CoopRegistryLifecycleCompatibility).FullName,
                    patchName);
            }
            new Harmony(owner).Patch(target, postfix: new HarmonyMethod(patchMethod));
            VerifyPatchOwner(target, owner, false);
        }

        private static void PatchTranspilerOnce(
            MethodInfo target,
            string owner,
            string patchName)
        {
            var existing = Harmony.GetPatchInfo(target);
            var existingTranspilers = existing == null
                ? 0
                : existing.Transpilers.Count(candidate =>
                    string.Equals(candidate.owner, owner, StringComparison.Ordinal));
            var existingOwnerCount = existing == null
                ? 0
                : existing.Prefixes.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal)) +
                  existing.Postfixes.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal)) +
                  existing.Transpilers.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal)) +
                  existing.Finalizers.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal));
            if (existingTranspilers == 1 && existingOwnerCount == 1)
                return;
            if (existingOwnerCount != 0)
                throw new InvalidOperationException("Duplicate Harmony owner: " + owner + ".");

            var patchMethod = typeof(CoopRegistryLifecycleCompatibility).GetMethod(
                patchName,
                BindingFlags.Static | BindingFlags.Public);
            if (patchMethod == null)
            {
                throw new MissingMethodException(
                    typeof(CoopRegistryLifecycleCompatibility).FullName,
                    patchName);
            }
            new Harmony(owner).Patch(
                target,
                transpiler: new HarmonyMethod(patchMethod) { priority = Priority.First });

            var applied = Harmony.GetPatchInfo(target);
            var appliedTranspilers = applied == null
                ? 0
                : applied.Transpilers.Count(candidate =>
                    string.Equals(candidate.owner, owner, StringComparison.Ordinal));
            var appliedOwnerCount = applied == null
                ? 0
                : applied.Prefixes.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal)) +
                  applied.Postfixes.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal)) +
                  applied.Transpilers.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal)) +
                  applied.Finalizers.Count(candidate =>
                      string.Equals(candidate.owner, owner, StringComparison.Ordinal));
            if (appliedTranspilers != 1 || appliedOwnerCount != 1)
            {
                throw new InvalidOperationException(
                    "Harmony transpiler was not installed exactly once: " + owner + ".");
            }
        }

        private static void VerifyPatchOwner(
            MethodInfo target,
            string owner,
            bool prefix)
        {
            var applied = Harmony.GetPatchInfo(target);
            var count = applied == null
                ? 0
                : (prefix ? applied.Prefixes : applied.Postfixes).Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal));
            if (count != 1)
            {
                throw new InvalidOperationException(
                    "Harmony patch was not installed exactly once: " + owner + ".");
            }
        }

        private static object Invoke(
            MethodInfo method,
            object instance,
            object[] arguments,
            string operation)
        {
            try
            {
                return method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "Failed to " + operation + ".",
                    exception.InnerException ?? exception);
            }
        }

        private sealed class ArmyRegistration
        {
            internal ArmyRegistration(string id, object army)
            {
                Id = id;
                Army = army;
            }

            internal string Id { get; private set; }
            internal object Army { get; private set; }
        }

        private sealed class DeferredPartyComponentUpdate
        {
            internal DeferredPartyComponentUpdate(
                object handler,
                object payload,
                object component,
                object mobileParty)
            {
                Handler = handler;
                Payload = payload;
                Component = component;
                MobileParty = mobileParty;
            }

            internal object Handler { get; private set; }
            internal object Payload { get; private set; }
            internal object Component { get; private set; }
            internal object MobileParty { get; private set; }
            internal bool Replayed { get; set; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance =
                new ReferenceComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }
    }

#if BCS_SERVER
    internal static class ServerPopulationControl
    {
        private const string SettingsFileName = "bcs-coop-bridge-population.config";
        private const string HeaderV1 = "BCS-BRIDGE-POPULATION|1";
        private const string HeaderV2 = "BCS-BRIDGE-POPULATION|2";
        private const string MaximumAutomaticCaravansKey =
            "MAXIMUM_AUTOMATIC_CARAVANS";
        private const string AutomaticNpcCaravansPerTownKey =
            "AUTOMATIC_NPC_CARAVANS_PER_TOWN";
        private const string MaximumActiveVillagerPartiesKey =
            "MAXIMUM_ACTIVE_VILLAGER_PARTIES";
        private const string BanditPartiesAroundHideoutMultiplierKey =
            "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER";
        private const long MaximumSettingsBytes = 64 * 1024;
        private const int MaximumPartyLimit = 1000000;
        private const int DefaultAutomaticNpcCaravansPerTown = 2;
        private const int MaximumAutomaticNpcCaravansPerTown = 10;
        private const double MaximumBanditMultiplier = 10d;
        private const string HarmonyOwner = "BCS.CoopBridge.population-control";

        private static readonly object Sync = new object();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static bool installed;
        private static int? maximumAutomaticCaravans;
        private static int automaticNpcCaravansPerTown =
            DefaultAutomaticNpcCaravansPerTown;
        private static int? maximumActiveVillagerParties;
        private static double banditPartiesAroundHideoutMultiplier = 1d;

        internal static void Install()
        {
            lock (Sync)
            {
                if (installed)
                    return;

                var settingsPath = ResolveSettingsPath();
                var settings = Load(settingsPath);
                maximumAutomaticCaravans = settings.MaximumAutomaticCaravans;
                automaticNpcCaravansPerTown = settings.AutomaticNpcCaravansPerTown;
                maximumActiveVillagerParties = settings.MaximumActiveVillagerParties;
                banditPartiesAroundHideoutMultiplier =
                    settings.BanditPartiesAroundHideoutMultiplier;

                var harmony = new Harmony(HarmonyOwner);
                harmony.Patch(
                    ResolveExactMethod(
                        typeof(CaravansCampaignBehavior),
                        "SpawnCaravan",
                        typeof(void),
                        new[] { typeof(Hero), typeof(bool) }),
                    prefix: CreateHarmonyMethod(nameof(BeforeSpawnCaravan), Priority.Last));

                if (maximumActiveVillagerParties.HasValue)
                {
                    harmony.Patch(
                        ResolveExactMethod(
                            typeof(VillagerCampaignBehavior),
                            "CreateVillagerParty",
                            typeof(void),
                            new[] { typeof(TaleWorlds.CampaignSystem.Settlements.Village) }),
                        prefix: CreateHarmonyMethod(nameof(BeforeCreateVillagerParty), Priority.Last));
                }

                if (Math.Abs(banditPartiesAroundHideoutMultiplier - 1d) > double.Epsilon)
                {
                    harmony.Patch(
                        ResolveExactMethod(
                            typeof(BanditSpawnCampaignBehavior),
                            "get__numberOfMaxBanditPartiesAroundEachHideout",
                            typeof(int),
                            Type.EmptyTypes),
                        postfix: CreateHarmonyMethod(
                            nameof(AfterGetMaximumBanditPartiesAroundEachHideout),
                            Priority.Last));
                }

                installed = true;
                Console.WriteLine(
                    "[BCS Coop Bridge] Population controls loaded: caravans=" +
                    FormatLimit(maximumAutomaticCaravans) +
                    ", npc-caravans-per-town=" +
                    automaticNpcCaravansPerTown.ToString(CultureInfo.InvariantCulture) +
                    ", villagers=" + FormatLimit(maximumActiveVillagerParties) +
                    ", bandits-around-hideouts=" +
                    banditPartiesAroundHideoutMultiplier.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) + "x. Existing parties were not removed.");
            }
        }

        private static HarmonyMethod CreateHarmonyMethod(string methodName, int priority)
        {
            var method = typeof(ServerPopulationControl).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(typeof(ServerPopulationControl).FullName, methodName);
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
                    methodName + " with the required population-control signature");
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

        private static bool BeforeSpawnCaravan(Hero hero)
        {
            var playerClan = Clan.PlayerClan;
            if (hero != null && playerClan != null && ReferenceEquals(hero.Clan, playerClan))
                return true;

            if (automaticNpcCaravansPerTown == 0)
                return false;

            var targetTown = ResolveCaravanTown(hero);
            var activeNpcCaravans = 0;
            var activeNpcCaravansForTown = 0;
            foreach (var party in MobileParty.AllCaravanParties)
            {
                if (party == null || !party.IsActive)
                    continue;
                if (playerClan != null &&
                    (ReferenceEquals(party.ActualClan, playerClan) ||
                     (party.Owner != null && ReferenceEquals(party.Owner.Clan, playerClan))))
                {
                    continue;
                }
                activeNpcCaravans++;
                if (targetTown != null &&
                    ReferenceEquals(ResolveCaravanTown(party), targetTown))
                {
                    activeNpcCaravansForTown++;
                }
            }

            if (maximumAutomaticCaravans.HasValue &&
                activeNpcCaravans >= maximumAutomaticCaravans.Value)
            {
                return false;
            }

            // Merchant notables normally resolve to a town. Preserve native
            // behavior for unusual modded heroes whose town cannot be resolved;
            // the explicit zero setting above still disables every NPC spawn.
            return targetTown == null ||
                   activeNpcCaravansForTown < automaticNpcCaravansPerTown;
        }

        private static Settlement ResolveCaravanTown(MobileParty party)
        {
            if (party == null)
                return null;

            var town = ResolveTown(party.HomeSettlement);
            return town ?? ResolveCaravanTown(party.Owner);
        }

        private static Settlement ResolveCaravanTown(Hero hero)
        {
            if (hero == null)
                return null;

            var town = ResolveTown(hero.HomeSettlement);
            return town ?? ResolveTown(hero.BornSettlement);
        }

        private static Settlement ResolveTown(Settlement settlement)
        {
            if (settlement == null)
                return null;
            if (settlement.IsTown)
                return settlement;
            if (settlement.IsVillage && settlement.Village != null)
                return settlement.Village.TradeBound;
            return null;
        }

        private static bool BeforeCreateVillagerParty()
        {
            if (!maximumActiveVillagerParties.HasValue)
                return true;

            var activeVillagerParties = 0;
            foreach (var party in MobileParty.AllVillagerParties)
            {
                if (party != null && party.IsActive)
                    activeVillagerParties++;
            }
            return activeVillagerParties < maximumActiveVillagerParties.Value;
        }

        private static void AfterGetMaximumBanditPartiesAroundEachHideout(ref int __result)
        {
            if (Math.Abs(banditPartiesAroundHideoutMultiplier - 1d) <= double.Epsilon)
                return;

            var scaled = Math.Floor(__result * banditPartiesAroundHideoutMultiplier);
            __result = scaled <= 0d
                ? 0
                : scaled >= int.MaxValue
                    ? int.MaxValue
                    : (int)scaled;
        }

        private static string ResolveSettingsPath()
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var directory = Directory.GetParent(assemblyPath);
            for (var index = 0; index < 5 && directory != null; index++)
                directory = directory.Parent;
            if (directory == null)
                throw new InvalidDataException("Could not resolve the dedicated-server root for population settings.");
            return Path.Combine(directory.FullName, SettingsFileName);
        }

        private static PopulationSettings Load(string path)
        {
            if (!File.Exists(path))
                return PopulationSettings.Defaults;

            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Bridge population settings file is linked: " + path);
            if (info.Length <= 0 || info.Length > MaximumSettingsBytes)
                throw new InvalidDataException("Bridge population settings file is empty or too large: " + path);

            var text = StrictUtf8.GetString(File.ReadAllBytes(path));
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0)
                throw new InvalidDataException("Unsupported bridge population settings schema.");

            var isV1 = string.Equals(lines[0], HeaderV1, StringComparison.Ordinal);
            var isV2 = string.Equals(lines[0], HeaderV2, StringComparison.Ordinal);
            if ((!isV1 && !isV2) ||
                (isV1 && lines.Count != 4) ||
                (isV2 && lines.Count != 5))
            {
                throw new InvalidDataException("Unsupported bridge population settings schema.");
            }

            int? caravanLimit = null;
            var caravansPerTown = DefaultAutomaticNpcCaravansPerTown;
            int? villagerLimit = null;
            var banditMultiplier = double.NaN;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < lines.Count; index++)
            {
                var fields = lines[index].Split('|');
                if (fields.Length != 2 || string.IsNullOrEmpty(fields[0]) ||
                    !seen.Add(fields[0]))
                {
                    throw new InvalidDataException("Malformed or duplicate bridge population setting.");
                }

                switch (fields[0])
                {
                    case MaximumAutomaticCaravansKey:
                        caravanLimit = ParseLimit(fields[1], MaximumAutomaticCaravansKey);
                        break;
                    case AutomaticNpcCaravansPerTownKey:
                        if (!isV2 ||
                            !int.TryParse(
                                fields[1],
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out caravansPerTown) ||
                            caravansPerTown < 0 ||
                            caravansPerTown > MaximumAutomaticNpcCaravansPerTown)
                        {
                            throw new InvalidDataException(
                                "Invalid " + AutomaticNpcCaravansPerTownKey + ".");
                        }
                        break;
                    case MaximumActiveVillagerPartiesKey:
                        villagerLimit = ParseLimit(fields[1], MaximumActiveVillagerPartiesKey);
                        break;
                    case BanditPartiesAroundHideoutMultiplierKey:
                        if (!double.TryParse(
                                fields[1],
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out banditMultiplier) ||
                            double.IsNaN(banditMultiplier) ||
                            double.IsInfinity(banditMultiplier) ||
                            banditMultiplier < 0d ||
                            banditMultiplier > MaximumBanditMultiplier)
                        {
                            throw new InvalidDataException(
                                "Invalid " + BanditPartiesAroundHideoutMultiplierKey + ".");
                        }
                        break;
                    default:
                        throw new InvalidDataException(
                            "Unknown bridge population setting: " + fields[0]);
                }
            }

            if (!seen.Contains(MaximumAutomaticCaravansKey) ||
                (isV2 && !seen.Contains(AutomaticNpcCaravansPerTownKey)) ||
                !seen.Contains(MaximumActiveVillagerPartiesKey) ||
                !seen.Contains(BanditPartiesAroundHideoutMultiplierKey))
            {
                throw new InvalidDataException("Bridge population settings file is incomplete.");
            }
            return new PopulationSettings(
                caravanLimit,
                caravansPerTown,
                villagerLimit,
                banditMultiplier);
        }

        private static int? ParseLimit(string value, string key)
        {
            if (string.Equals(value, "NATIVE", StringComparison.Ordinal))
                return null;
            int parsed;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) ||
                parsed < 0 || parsed > MaximumPartyLimit)
            {
                throw new InvalidDataException("Invalid " + key + ".");
            }
            return parsed;
        }

        private static string FormatLimit(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : "native";
        }

        private sealed class PopulationSettings
        {
            internal static readonly PopulationSettings Defaults =
                new PopulationSettings(
                    null,
                    DefaultAutomaticNpcCaravansPerTown,
                    null,
                    1d);

            internal PopulationSettings(
                int? maximumAutomaticCaravans,
                int automaticNpcCaravansPerTown,
                int? maximumActiveVillagerParties,
                double banditPartiesAroundHideoutMultiplier)
            {
                MaximumAutomaticCaravans = maximumAutomaticCaravans;
                AutomaticNpcCaravansPerTown = automaticNpcCaravansPerTown;
                MaximumActiveVillagerParties = maximumActiveVillagerParties;
                BanditPartiesAroundHideoutMultiplier = banditPartiesAroundHideoutMultiplier;
            }

            internal int? MaximumAutomaticCaravans { get; private set; }
            internal int AutomaticNpcCaravansPerTown { get; private set; }
            internal int? MaximumActiveVillagerParties { get; private set; }
            internal double BanditPartiesAroundHideoutMultiplier { get; private set; }
        }
    }

    internal static class ServerFailedIdCompatibility
    {
        private const string PartyVisualOwner =
            "BCS.CoopBridge.failed-id.party-visual";
        private const string WarehouseRosterOwner =
            "BCS.CoopBridge.failed-id.workshop-warehouse-roster";
        private const string WorkshopOutputCategoryOwner =
            "BCS.CoopBridge.workshop-output-category-cache";
        private const string MapEventVisualOwner =
            "BCS.CoopBridge.failed-id.headless-map-event-visual";
        private const string AutoSyncDeferredOwner =
            "BCS.CoopBridge.failed-id.autosync-ready";
        private const string ItemRosterDiagnosticOwner =
            "BCS.CoopBridge.failed-id.item-roster-diagnostic";
        private const int MaxItemRosterDiagnosticRecords = 32;

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Warnings =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> WorkshopCategoryNotices =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            object,
            Dictionary<string, int>> WorkshopUnresolvedCategoryCounts =
            new System.Runtime.CompilerServices.ConditionalWeakTable<
                object,
                Dictionary<string, int>>();
        private static readonly HashSet<object> DiagnosedItemRosters =
            new HashSet<object>(ReferenceComparer.Instance);

        private static bool immediateInstallAttempted;
        private static bool mapEventVisualInstalled;
        private static bool itemRosterDiagnosticInstalled;
        private static bool itemRosterDiagnosticCapReported;
        private static int itemRosterDiagnosticSequence;

        private static FieldInfo skippedVisualIdsField;
        private static FieldInfo partyVisualObjectManagerField;
        private static PropertyInfo destroyedPayloadWhatProperty;
        private static PropertyInfo destroyedVisualProperty;
        private static MethodInfo skippedVisualTryGetValueMethod;
        private static MethodInfo skippedVisualRemoveMethod;
        private static MethodInfo objectManagerContainsObjectMethod;
        private static FieldInfo workshopItemsInCategoryField;

        private static Type mapEventVisualInterface;
        private static Type headlessMapEventVisualType;
        private static FieldInfo mapEventVisualField;

        private static Type itemRosterHandlerType;
        private static FieldInfo itemRosterHandlerObjectManagerField;
        private static MethodInfo itemRosterObjectManagerTryGetIdMethod;
        private static MethodInfo itemRosterUpdatedHandlerMethod;
        private static MethodInfo itemRosterClearedHandlerMethod;
        private static PropertyInfo itemRosterUpdatedPayloadWhatProperty;
        private static PropertyInfo itemRosterClearedPayloadWhatProperty;
        private static FieldInfo itemRosterUpdatedInstanceField;
        private static FieldInfo itemRosterClearedInstanceField;
        private static FieldInfo workshopWarehouseRostersField;

        internal static void Install()
        {
            lock (Sync)
            {
                if (immediateInstallAttempted)
                    return;
                immediateInstallAttempted = true;
            }

            var partyVisualInstalled = TryInstall(
                "pre-registration party-visual cleanup",
                InstallPartyVisualCleanup);
            var warehouseRosterInstalled = TryInstall(
                "workshop warehouse ItemRoster construction scope",
                InstallWarehouseRosterConstructionScope);
            var workshopCategoryInstalled = TryInstall(
                "workshop output category cache repair",
                InstallWorkshopOutputCategoryRepair);
            var itemRosterDiagnosticInstalledNow = TryInstall(
                "ItemRoster missing-ID diagnostic",
                InstallItemRosterMissingIdDiagnostic);

            var mapStatus = TryInstallMapEventVisualCompatibility()
                ? "installed"
                : TryInstall(
                    "deferred AutoSync readiness hook",
                    InstallAutoSyncReadyHook)
                    ? "deferred"
                    : "unavailable";

            Console.WriteLine(
                "[BCS Coop Bridge] Targeted Coop ID compatibility: " +
                "partyVisual=" + (partyVisualInstalled ? "installed" : "unavailable") +
                ", workshopWarehouse=" + (warehouseRosterInstalled ? "installed" : "unavailable") +
                ", workshopOutputCategory=" +
                (workshopCategoryInstalled ? "installed" : "unavailable") +
                ", itemRosterDiagnostic=" +
                (itemRosterDiagnosticInstalledNow ? "installed" : "unavailable") +
                ", headlessMapEventVisual=" + mapStatus + ".");
        }

        private static bool TryInstall(string description, Action installer)
        {
            try
            {
                installer();
                return true;
            }
            catch (Exception exception)
            {
                WarnOnce(description, exception);
                return false;
            }
        }

        private static void InstallPartyVisualCleanup()
        {
            var gameInterface = ResolveSingleLoadedAssembly("GameInterface");
            var handlerType = RequireType(
                gameInterface,
                "GameInterface.Services.PartyVisuals.Handlers.PartyVisualLifetimeHandler");
            var destroyedType = RequireType(
                gameInterface,
                "GameInterface.Services.PartyVisuals.Messages.PartyVisualDestroyed");
            var target = FindSinglePayloadHandler(
                handlerType,
                destroyedType,
                false);

            skippedVisualIdsField = RequireField(
                handlerType,
                "skippedVisualIds",
                false);
            partyVisualObjectManagerField = RequireField(
                handlerType,
                "objectManager",
                false);
            destroyedPayloadWhatProperty = RequireReadableProperty(
                target.GetParameters()[0].ParameterType,
                "What");
            destroyedVisualProperty = RequireReadableProperty(
                destroyedType,
                "MobilePartyVisual");

            var tableType = skippedVisualIdsField.FieldType;
            skippedVisualTryGetValueMethod = RequireSingleMethod(
                tableType,
                "TryGetValue",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == destroyedVisualProperty.PropertyType &&
                           parameters[1].ParameterType == typeof(string).MakeByRefType();
                });
            skippedVisualRemoveMethod = RequireSingleMethod(
                tableType,
                "Remove",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                           parameters[0].ParameterType == destroyedVisualProperty.PropertyType;
                });
            objectManagerContainsObjectMethod = RequireSingleMethod(
                partyVisualObjectManagerField.FieldType,
                "Contains",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                           parameters[0].ParameterType == typeof(object);
                });

            PatchPrefixOnce(
                target,
                PartyVisualOwner,
                nameof(BeforePartyVisualDestroyed));
        }

        public static bool BeforePartyVisualDestroyed(object __instance, object __0)
        {
            try
            {
                if (__instance == null || __0 == null)
                    return true;

                var message = destroyedPayloadWhatProperty.GetValue(__0, null);
                if (message == null)
                    return true;
                var visual = destroyedVisualProperty.GetValue(message, null);
                if (visual == null)
                    return true;

                var skipped = skippedVisualIdsField.GetValue(__instance);
                var objectManager = partyVisualObjectManagerField.GetValue(__instance);
                if (skipped == null || objectManager == null)
                    return true;

                var lookupArguments = new[] { visual, null };
                var wasSkipped = (bool)skippedVisualTryGetValueMethod.Invoke(
                    skipped,
                    lookupArguments);
                if (!wasSkipped ||
                    (bool)objectManagerContainsObjectMethod.Invoke(
                        objectManager,
                        new[] { visual }))
                {
                    return true;
                }

                // This visual's create was intentionally omitted before registry setup.
                // If it was not registered later, there is no remote object to destroy.
                return !(bool)skippedVisualRemoveMethod.Invoke(
                    skipped,
                    new[] { visual });
            }
            catch (Exception exception)
            {
                WarnOnce("party-visual cleanup runtime", exception);
                return true;
            }
        }

        private static void InstallWarehouseRosterConstructionScope()
        {
            var gameInterface = ResolveSingleLoadedAssembly("GameInterface");
            var interfaceType = RequireType(
                gameInterface,
                "GameInterface.Services.Workshops.Interfaces.WorkshopsCampaignBehaviorInterface");
            var target = RequireSingleMethod(
                interfaceType,
                "GetWarehouseRoster",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return method.IsPrivate &&
                           parameters.Length == 3 &&
                           string.Equals(
                               parameters[0].ParameterType.FullName,
                               "TaleWorlds.CampaignSystem.Hero",
                               StringComparison.Ordinal) &&
                           string.Equals(
                               parameters[1].ParameterType.FullName,
                               "TaleWorlds.CampaignSystem.Settlements.Settlement",
                               StringComparison.Ordinal) &&
                           parameters[2].IsOut &&
                           parameters[2].ParameterType.IsByRef &&
                           string.Equals(
                               parameters[2].ParameterType.GetElementType().FullName,
                               "TaleWorlds.CampaignSystem.Roster.ItemRoster",
                               StringComparison.Ordinal);
                });

            PatchScopeOnce(
                target,
                WarehouseRosterOwner,
                nameof(BeforeWorkshopWarehouseRosterConstruction),
                nameof(FinalizeWorkshopWarehouseRosterConstruction));
        }

        public static void BeforeWorkshopWarehouseRosterConstruction(
            ref IDisposable __state)
        {
            __state = new Common.Util.AllowedThread();
        }

        public static Exception FinalizeWorkshopWarehouseRosterConstruction(
            IDisposable __state,
            Exception __exception)
        {
            try
            {
                return __exception;
            }
            finally
            {
                if (__state != null)
                    __state.Dispose();
            }
        }

        private static void InstallItemRosterMissingIdDiagnostic()
        {
            var coopCore = ResolveSingleLoadedAssembly("Coop.Core");
            var gameInterface = ResolveSingleLoadedAssembly("GameInterface");
            var objectManagerType = RequireType(
                gameInterface,
                "GameInterface.Services.ObjectManager.IObjectManager");
            var updatedMessageType = RequireType(
                gameInterface,
                "GameInterface.Services.ItemRosters.Messages.ItemRosterUpdated");
            var clearedMessageType = RequireType(
                gameInterface,
                "GameInterface.Services.ItemRosters.Messages.ItemRosterCleared");
            var resolvedHandlerType = RequireType(
                coopCore,
                "Coop.Core.Server.Services.ItemRosters.Handlers.ItemRosterMessageHandler");
            var resolvedObjectManagerField = RequireField(
                resolvedHandlerType,
                "objectManager",
                false);
            if (!resolvedObjectManagerField.IsPrivate ||
                !resolvedObjectManagerField.IsInitOnly ||
                resolvedObjectManagerField.FieldType != objectManagerType)
            {
                throw new InvalidDataException(
                    "Released Coop ItemRoster handler object-manager field has an incompatible signature.");
            }

            var resolvedTryGetId = RequireSingleMethod(
                objectManagerType,
                "TryGetId",
                false,
                typeof(bool),
                method =>
                {
                    var parameters = method.GetParameters();
                    return method.IsPublic &&
                           parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(object) &&
                           parameters[1].IsOut &&
                           parameters[1].ParameterType == typeof(string).MakeByRefType();
                });
            var resolvedUpdatedHandler = FindSinglePayloadHandler(
                resolvedHandlerType,
                updatedMessageType,
                true);
            var resolvedClearedHandler = FindSinglePayloadHandler(
                resolvedHandlerType,
                clearedMessageType,
                true);
            RequireItemRosterHandlerSignature(
                resolvedUpdatedHandler,
                resolvedHandlerType);
            RequireItemRosterHandlerSignature(
                resolvedClearedHandler,
                resolvedHandlerType);

            var resolvedUpdatedWhat = RequireReadableProperty(
                resolvedUpdatedHandler.GetParameters()[0].ParameterType,
                "What");
            var resolvedClearedWhat = RequireReadableProperty(
                resolvedClearedHandler.GetParameters()[0].ParameterType,
                "What");
            RequireMessagePayloadGetter(resolvedUpdatedWhat, updatedMessageType);
            RequireMessagePayloadGetter(resolvedClearedWhat, clearedMessageType);

            var resolvedUpdatedInstance = RequireField(
                updatedMessageType,
                "Instance",
                false);
            var resolvedClearedInstance = RequireField(
                clearedMessageType,
                "ItemRoster",
                false);
            RequireItemRosterMessageField(resolvedUpdatedInstance, "Instance");
            RequireItemRosterMessageField(resolvedClearedInstance, "ItemRoster");
            var resolvedWarehouseRostersField = RequireField(
                typeof(WorkshopsCampaignBehavior),
                "_warehouseRosterPerSettlement",
                false);
            if (resolvedWarehouseRostersField.FieldType !=
                typeof(KeyValuePair<Settlement, ItemRoster>[]))
            {
                throw new InvalidDataException(
                    "Released TaleWorlds workshop warehouse-roster field has an incompatible signature.");
            }

            itemRosterHandlerType = resolvedHandlerType;
            itemRosterHandlerObjectManagerField = resolvedObjectManagerField;
            itemRosterObjectManagerTryGetIdMethod = resolvedTryGetId;
            itemRosterUpdatedHandlerMethod = resolvedUpdatedHandler;
            itemRosterClearedHandlerMethod = resolvedClearedHandler;
            itemRosterUpdatedPayloadWhatProperty = resolvedUpdatedWhat;
            itemRosterClearedPayloadWhatProperty = resolvedClearedWhat;
            itemRosterUpdatedInstanceField = resolvedUpdatedInstance;
            itemRosterClearedInstanceField = resolvedClearedInstance;
            workshopWarehouseRostersField = resolvedWarehouseRostersField;

            var prefix = typeof(ServerFailedIdCompatibility).GetMethod(
                nameof(BeforeMissingItemRosterId),
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
            {
                throw new MissingMethodException(
                    typeof(ServerFailedIdCompatibility).FullName,
                    nameof(BeforeMissingItemRosterId));
            }

            var harmony = new Harmony(ItemRosterDiagnosticOwner);
            try
            {
                PatchItemRosterDiagnosticPrefix(
                    harmony,
                    resolvedUpdatedHandler,
                    prefix);
                PatchItemRosterDiagnosticPrefix(
                    harmony,
                    resolvedClearedHandler,
                    prefix);
                itemRosterDiagnosticInstalled = true;
            }
            catch
            {
                itemRosterDiagnosticInstalled = false;
                UnpatchItemRosterDiagnostic(
                    harmony,
                    resolvedUpdatedHandler,
                    resolvedClearedHandler);
                throw;
            }
        }

        public static void BeforeMissingItemRosterId(
            object __instance,
            object __0,
            MethodBase __originalMethod)
        {
            try
            {
                if (!itemRosterDiagnosticInstalled ||
                    __instance == null ||
                    __0 == null ||
                    __originalMethod == null)
                {
                    return;
                }

                var roster = ReadItemRosterFromPayload(__0, __originalMethod);
                if (roster == null)
                    return;
                var objectManager = itemRosterHandlerObjectManagerField.GetValue(__instance);
                if (objectManager == null)
                    return;

                var lookupArguments = new object[] { roster, null };
                var lookupResult = itemRosterObjectManagerTryGetIdMethod.Invoke(
                    objectManager,
                    lookupArguments);
                if (lookupResult is bool && (bool)lookupResult)
                    return;

                RecordMissingItemRoster(roster, __originalMethod);
            }
            catch (Exception exception)
            {
                WarnItemRosterDiagnosticOnce("runtime", exception);
            }
        }

        private static object ReadItemRosterFromPayload(
            object payload,
            MethodBase originalMethod)
        {
            PropertyInfo whatProperty;
            FieldInfo rosterField;
            if (originalMethod.Equals(itemRosterUpdatedHandlerMethod))
            {
                whatProperty = itemRosterUpdatedPayloadWhatProperty;
                rosterField = itemRosterUpdatedInstanceField;
            }
            else if (originalMethod.Equals(itemRosterClearedHandlerMethod))
            {
                whatProperty = itemRosterClearedPayloadWhatProperty;
                rosterField = itemRosterClearedInstanceField;
            }
            else
            {
                throw new InvalidDataException(
                    "Unexpected Coop ItemRoster handler reached the missing-ID diagnostic.");
            }

            var message = whatProperty.GetValue(payload, null);
            return message == null ? null : rosterField.GetValue(message);
        }

        private static void RecordMissingItemRoster(
            object roster,
            MethodBase originalMethod)
        {
            int sequence;
            bool reportCap;
            lock (Sync)
            {
                if (DiagnosedItemRosters.Contains(roster))
                    return;
                if (DiagnosedItemRosters.Count >= MaxItemRosterDiagnosticRecords)
                {
                    if (!itemRosterDiagnosticCapReported)
                    {
                        itemRosterDiagnosticCapReported = true;
                        Console.WriteLine(
                            "[BCS Coop Bridge][ItemRosterMissingIdDiagnostic] " +
                            "suppression=roster-cap emitted=" +
                            MaxItemRosterDiagnosticRecords.ToString(
                                CultureInfo.InvariantCulture) +
                            " further_unique_rosters_suppressed=true");
                    }
                    return;
                }

                DiagnosedItemRosters.Add(roster);
                itemRosterDiagnosticSequence++;
                sequence = itemRosterDiagnosticSequence;
                reportCap = DiagnosedItemRosters.Count ==
                    MaxItemRosterDiagnosticRecords;
                if (reportCap)
                    itemRosterDiagnosticCapReported = true;
            }

            var eventName = originalMethod.Equals(itemRosterUpdatedHandlerMethod)
                ? "update"
                : "clear";
            var owner = ClassifyItemRosterOwner(roster);
            var caller = FirstMeaningfulItemRosterCaller(originalMethod);
            Console.WriteLine(
                "[BCS Coop Bridge][ItemRosterMissingIdDiagnostic] seq=" +
                sequence.ToString(CultureInfo.InvariantCulture) +
                " event=" + eventName +
                " roster_ref=" +
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(roster)
                    .ToString(CultureInfo.InvariantCulture) +
                " owner=" + QuoteDiagnostic(owner) +
                " caller=" + QuoteDiagnostic(caller) +
                " thread=" + QuoteDiagnostic(CurrentThreadDescription()));
            if (reportCap)
            {
                Console.WriteLine(
                    "[BCS Coop Bridge][ItemRosterMissingIdDiagnostic] " +
                    "suppression=roster-cap emitted=" +
                    MaxItemRosterDiagnosticRecords.ToString(
                        CultureInfo.InvariantCulture) +
                    " further_unique_rosters_suppressed=true");
            }
        }

        private static string ClassifyItemRosterOwner(object roster)
        {
            try
            {
                var campaign = Campaign.Current;
                var manager = campaign == null
                    ? null
                    : campaign.CampaignObjectManager;
                if (manager == null)
                    return "unknown:campaign-object-manager-unavailable";

                foreach (MobileParty party in manager.MobileParties)
                {
                    if (party != null && ReferenceEquals(party.ItemRoster, roster))
                    {
                        return "mobile-party:" +
                               (party.StringId ?? "<null>");
                    }
                }

                foreach (Settlement settlement in manager.Settlements)
                {
                    if (settlement == null)
                        continue;
                    if (ReferenceEquals(settlement.ItemRoster, roster))
                    {
                        return "settlement-market:" +
                               (settlement.StringId ?? "<null>");
                    }
                    if (ReferenceEquals(settlement.Stash, roster))
                    {
                        return "settlement-stash:" +
                               (settlement.StringId ?? "<null>");
                    }
                }

                var workshops = campaign.GetCampaignBehavior<WorkshopsCampaignBehavior>();
                var warehouses = workshops == null
                    ? null
                    : (KeyValuePair<Settlement, ItemRoster>[])
                        workshopWarehouseRostersField.GetValue(workshops);
                if (warehouses != null)
                {
                    for (var index = 0; index < warehouses.Length; index++)
                    {
                        if (!ReferenceEquals(warehouses[index].Value, roster))
                            continue;
                        var settlement = warehouses[index].Key;
                        return "workshop-warehouse:" +
                               (settlement == null
                                   ? "<null>"
                                   : settlement.StringId ?? "<null>");
                    }
                }
            }
            catch (Exception exception)
            {
                WarnItemRosterDiagnosticOnce("owner-classification", exception);
                return "unknown:classification-failed";
            }
            return "unknown";
        }

        private static string FirstMeaningfulItemRosterCaller(
            MethodBase originalMethod)
        {
            try
            {
                var frames = new StackTrace(1, false).GetFrames();
                if (frames == null)
                    return "unknown";
                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    var declaringType = method == null ? null : method.DeclaringType;
                    if (method == null || declaringType == null)
                        continue;
                    if (originalMethod != null && method.Equals(originalMethod))
                        continue;

                    var typeName = declaringType.FullName ?? declaringType.Name;
                    var assemblyName = declaringType.Assembly.GetName().Name;
                    if (string.Equals(
                            assemblyName,
                            typeof(ServerFailedIdCompatibility).Assembly.GetName().Name,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(assemblyName, "0Harmony", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(assemblyName, "Common", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(assemblyName, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(assemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase) ||
                        declaringType == itemRosterHandlerType ||
                        typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
                        typeName.StartsWith("BCS.CoopBridge.", StringComparison.Ordinal) ||
                        typeName.StartsWith(
                            "GameInterface.Services.ItemRosters.Patches.ItemRosterPatch",
                            StringComparison.Ordinal) ||
                        typeName.StartsWith(
                            "TaleWorlds.CampaignSystem.Roster.ItemRoster",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    return assemblyName + "!" + typeName + "::" + method.Name;
                }
            }
            catch (Exception exception)
            {
                WarnItemRosterDiagnosticOnce("caller-capture", exception);
            }
            return "unknown";
        }

        private static string CurrentThreadDescription()
        {
            var thread = System.Threading.Thread.CurrentThread;
            return thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) +
                   (string.IsNullOrWhiteSpace(thread.Name)
                       ? string.Empty
                       : ":" + thread.Name);
        }

        private static string QuoteDiagnostic(string value)
        {
            if (value == null)
                value = "<null>";
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }

        private static void WarnItemRosterDiagnosticOnce(
            string area,
            Exception exception)
        {
            var key = "ItemRoster diagnostic " + area;
            lock (Sync)
            {
                if (!Warnings.Add(key))
                    return;
            }
            var root = exception is TargetInvocationException &&
                       exception.InnerException != null
                ? exception.InnerException
                : exception;
            Console.WriteLine(
                "[BCS Coop Bridge][ItemRosterMissingIdDiagnostic] " +
                "status=error area=" + QuoteDiagnostic(area) +
                " error=" + QuoteDiagnostic(
                    root.GetType().FullName + ": " + root.Message));
        }

        private static void RequireItemRosterHandlerSignature(
            MethodInfo method,
            Type expectedHandlerType)
        {
            if (method == null ||
                method.DeclaringType != expectedHandlerType ||
                method.IsStatic ||
                !method.IsPublic ||
                method.ReturnType != typeof(void))
            {
                throw new InvalidDataException(
                    "Released Coop ItemRoster handler has an incompatible signature.");
            }
        }

        private static void RequireMessagePayloadGetter(
            PropertyInfo property,
            Type messageType)
        {
            var getter = property == null ? null : property.GetGetMethod(false);
            if (getter == null ||
                getter.IsStatic ||
                getter.ReturnType != messageType ||
                getter.GetParameters().Length != 0)
            {
                throw new InvalidDataException(
                    "Released Coop ItemRoster MessagePayload.What getter has an incompatible signature.");
            }
        }

        private static void RequireItemRosterMessageField(
            FieldInfo field,
            string expectedName)
        {
            if (field == null ||
                !string.Equals(field.Name, expectedName, StringComparison.Ordinal) ||
                field.FieldType != typeof(ItemRoster) ||
                field.IsStatic ||
                !field.IsPublic ||
                !field.IsInitOnly)
            {
                throw new InvalidDataException(
                    "Released Coop ItemRoster message field has an incompatible signature: " +
                    expectedName + ".");
            }
        }

        private static void PatchItemRosterDiagnosticPrefix(
            Harmony harmony,
            MethodInfo target,
            MethodInfo prefix)
        {
            var existing = Harmony.GetPatchInfo(target);
            var owned = existing == null
                ? new Patch[0]
                : existing.Prefixes
                    .Where(patch => string.Equals(
                        patch.owner,
                        ItemRosterDiagnosticOwner,
                        StringComparison.Ordinal))
                    .ToArray();
            var ownedTotal = CountOwnedItemRosterDiagnosticPatches(existing);
            if (owned.Length == 1 &&
                owned[0].PatchMethod == prefix &&
                ownedTotal == 1)
            {
                return;
            }
            if (ownedTotal != 0)
            {
                throw new InvalidOperationException(
                    "Unexpected ItemRoster diagnostic Harmony owner collision.");
            }

            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix) { priority = Priority.Last });
            var applied = Harmony.GetPatchInfo(target);
            if (applied == null ||
                applied.Prefixes.Count(patch =>
                    string.Equals(
                        patch.owner,
                        ItemRosterDiagnosticOwner,
                        StringComparison.Ordinal) &&
                    patch.PatchMethod == prefix) != 1 ||
                CountOwnedItemRosterDiagnosticPatches(applied) != 1)
            {
                throw new InvalidOperationException(
                    "ItemRoster missing-ID diagnostic prefix was not installed exactly once.");
            }
        }

        private static int CountOwnedItemRosterDiagnosticPatches(Patches patches)
        {
            if (patches == null)
                return 0;
            return patches.Prefixes.Count(patch => string.Equals(
                       patch.owner,
                       ItemRosterDiagnosticOwner,
                       StringComparison.Ordinal)) +
                   patches.Postfixes.Count(patch => string.Equals(
                       patch.owner,
                       ItemRosterDiagnosticOwner,
                       StringComparison.Ordinal)) +
                   patches.Transpilers.Count(patch => string.Equals(
                       patch.owner,
                       ItemRosterDiagnosticOwner,
                       StringComparison.Ordinal)) +
                   patches.Finalizers.Count(patch => string.Equals(
                       patch.owner,
                       ItemRosterDiagnosticOwner,
                       StringComparison.Ordinal));
        }

        private static void UnpatchItemRosterDiagnostic(
            Harmony harmony,
            params MethodInfo[] targets)
        {
            foreach (var target in targets)
            {
                if (target == null)
                    continue;
                var patches = Harmony.GetPatchInfo(target);
                if (patches == null ||
                    !patches.Prefixes.Any(patch => string.Equals(
                        patch.owner,
                        ItemRosterDiagnosticOwner,
                        StringComparison.Ordinal)))
                {
                    continue;
                }
                harmony.Unpatch(
                    target,
                    HarmonyPatchType.All,
                    ItemRosterDiagnosticOwner);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance =
                new ReferenceComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }

        private static void InstallWorkshopOutputCategoryRepair()
        {
            var behaviorType = typeof(WorkshopsCampaignBehavior);
            var target = RequireSingleMethod(
                behaviorType,
                "GetRandomItemAux",
                false,
                typeof(EquipmentElement),
                method =>
                {
                    var parameters = method.GetParameters();
                    return method.IsPrivate &&
                           parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(ItemCategory) &&
                           string.Equals(
                               parameters[1].ParameterType.FullName,
                               "TaleWorlds.CampaignSystem.Settlements.Town",
                               StringComparison.Ordinal);
                });
            workshopItemsInCategoryField = RequireField(
                behaviorType,
                "_itemsInCategory",
                false);
            if (workshopItemsInCategoryField.FieldType !=
                typeof(Dictionary<ItemCategory, List<ItemObject>>))
            {
                throw new InvalidDataException(
                    "Workshop item-category cache has an unexpected runtime type.");
            }

            PatchPrefixOnce(
                target,
                WorkshopOutputCategoryOwner,
                nameof(BeforeWorkshopOutputCategoryLookup));
        }

        public static void BeforeWorkshopOutputCategoryLookup(
            object __instance,
            ref ItemCategory __0)
        {
            try
            {
                if (__instance == null || __0 == null || Game.Current == null)
                    return;

                var categories =
                    (Dictionary<ItemCategory, List<ItemObject>>)
                    workshopItemsInCategoryField.GetValue(__instance);
                if (categories == null)
                    return;

                List<ItemObject> cached;
                if (categories.TryGetValue(__0, out cached) && cached != null)
                {
                    for (var index = 0; index < cached.Count; index++)
                    {
                        var item = cached[index];
                        if (item != null &&
                            ReferenceEquals(item.ItemCategory, __0) &&
                            !item.MultiplayerItem &&
                            !item.NotMerchandise &&
                            !item.IsCraftedByPlayer)
                        {
                            return;
                        }
                    }
                }

                var requestedCategory = __0;
                var requestedId = requestedCategory.StringId ?? string.Empty;
                var liveItems = Game.Current.ObjectManager
                    .GetObjectTypeList<ItemObject>();
                lock (Sync)
                {
                    var unresolved = WorkshopUnresolvedCategoryCounts
                        .GetOrCreateValue(__instance);
                    int previousObjectCount;
                    if (unresolved.TryGetValue(requestedId, out previousObjectCount) &&
                        previousObjectCount == liveItems.Count)
                    {
                        return;
                    }
                }
                var exactItems = new List<ItemObject>();
                var sameIdItems =
                    new Dictionary<ItemCategory, List<ItemObject>>();
                for (var index = 0; index < liveItems.Count; index++)
                {
                    var item = liveItems[index];
                    if (item == null || item.MultiplayerItem ||
                        item.NotMerchandise || item.IsCraftedByPlayer)
                    {
                        continue;
                    }

                    var itemCategory = item.ItemCategory;
                    if (itemCategory == null)
                        continue;
                    if (ReferenceEquals(itemCategory, requestedCategory))
                        exactItems.Add(item);
                    if (!string.Equals(
                        itemCategory.StringId,
                        requestedId,
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    List<ItemObject> matchingItems;
                    if (!sameIdItems.TryGetValue(itemCategory, out matchingItems))
                    {
                        matchingItems = new List<ItemObject>();
                        sameIdItems.Add(itemCategory, matchingItems);
                    }
                    matchingItems.Add(item);
                }

                if (exactItems.Count > 0)
                {
                    categories[requestedCategory] = exactItems;
                    ClearWorkshopCategoryUnresolved(__instance, requestedId);
                    WriteWorkshopCategoryNotice(
                        "rebuilt:" + requestedId,
                        "Rebuilt stale workshop output category '" + requestedId +
                        "' from " + exactItems.Count + " live producible item(s).");
                    return;
                }

                if (sameIdItems.Count == 1)
                {
                    var canonical = sameIdItems.First();
                    categories[canonical.Key] = canonical.Value;
                    __0 = canonical.Key;
                    ClearWorkshopCategoryUnresolved(__instance, requestedId);
                    WriteWorkshopCategoryNotice(
                        "canonicalized:" + requestedId,
                        "Canonicalized duplicate workshop output category '" +
                        requestedId + "' to " + canonical.Value.Count +
                        " live producible item(s).");
                    return;
                }

                lock (Sync)
                {
                    WorkshopUnresolvedCategoryCounts
                        .GetOrCreateValue(__instance)[requestedId] = liveItems.Count;
                }
                WriteWorkshopCategoryNotice(
                    "unresolved:" + requestedId,
                    "Workshop output category '" + requestedId +
                    "' remains unresolved (same-ID live category groups=" +
                    sameIdItems.Count +
                    "); native empty-item assertion remains enabled.");
            }
            catch (Exception exception)
            {
                WarnOnce("workshop output category cache runtime", exception);
            }
        }

        private static void ClearWorkshopCategoryUnresolved(
            object behavior,
            string categoryId)
        {
            lock (Sync)
            {
                Dictionary<string, int> unresolved;
                if (WorkshopUnresolvedCategoryCounts.TryGetValue(
                    behavior,
                    out unresolved))
                {
                    unresolved.Remove(categoryId);
                }
            }
        }

        private static void WriteWorkshopCategoryNotice(string key, string message)
        {
            lock (Sync)
            {
                if (!WorkshopCategoryNotices.Add(key))
                    return;
            }
            Console.WriteLine("[BCS Coop Bridge] " + message);
        }

        private static bool TryInstallMapEventVisualCompatibility()
        {
            lock (Sync)
            {
                if (mapEventVisualInstalled)
                    return true;
            }

            try
            {
                var autoSync = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => string.Equals(
                        assembly.GetName().Name,
                        "AutoSync",
                        StringComparison.Ordinal))
                    .ToArray();
                if (autoSync.Length == 0)
                    return false;
                if (autoSync.Length != 1)
                {
                    throw new InvalidDataException(
                        "Expected exactly one generated AutoSync assembly, found " +
                        autoSync.Length + ".");
                }

                var patchType = RequireType(
                    autoSync[0],
                    "AutoSync.MapEvent_DynamicPatches");
                var target = RequireSingleMethod(
                    patchType,
                    "MapEventVisual_Intercept",
                    true,
                    typeof(void),
                    method =>
                    {
                        var parameters = method.GetParameters();
                        return parameters.Length == 2 &&
                               string.Equals(
                                   parameters[0].ParameterType.FullName,
                                   "TaleWorlds.CampaignSystem.MapEvents.MapEvent",
                                   StringComparison.Ordinal) &&
                               string.Equals(
                                   parameters[1].ParameterType.FullName,
                                   "TaleWorlds.CampaignSystem.MapEvents.IMapEventVisual",
                                   StringComparison.Ordinal);
                    });
                var targetParameters = target.GetParameters();
                mapEventVisualInterface = targetParameters[1].ParameterType;
                mapEventVisualField = RequireField(
                    targetParameters[0].ParameterType,
                    "MapEventVisual",
                    false);
                if (mapEventVisualField.FieldType != mapEventVisualInterface)
                {
                    throw new InvalidDataException(
                        "MapEvent visual field does not match the generated AutoSync interceptor.");
                }
                var dedicatedServerCore = ResolveSingleLoadedAssembly("DedicatedServer.Core");
                var headlessVisualTypes = dedicatedServerCore.GetTypes()
                    .Where(type =>
                        !type.IsAbstract &&
                        !type.IsInterface &&
                        mapEventVisualInterface.IsAssignableFrom(type))
                    .ToArray();
                if (headlessVisualTypes.Length != 1)
                {
                    throw new InvalidDataException(
                        "Expected exactly one DedicatedServer.Core IMapEventVisual implementation, found " +
                        headlessVisualTypes.Length + ".");
                }
                headlessMapEventVisualType = headlessVisualTypes[0];

                PatchPrefixOnce(
                    target,
                    MapEventVisualOwner,
                    nameof(BeforeHeadlessMapEventVisual));
                lock (Sync)
                    mapEventVisualInstalled = true;
                Console.WriteLine(
                    "[BCS Coop Bridge] Installed headless map-event visual assignment compatibility.");
                return true;
            }
            catch (Exception exception)
            {
                WarnOnce("headless map-event visual compatibility", exception);
                return false;
            }
        }

        private static void InstallAutoSyncReadyHook()
        {
            var gameInterface = ResolveSingleLoadedAssembly("GameInterface");
            var patcherType = RequireType(
                gameInterface,
                "GameInterface.AutoSync.AutoSyncPatcher");
            var target = RequireSingleMethod(
                patcherType,
                "PatchAll",
                false,
                typeof(void),
                method => method.GetParameters().Length == 0);
            PatchPostfixOnce(
                target,
                AutoSyncDeferredOwner,
                nameof(AfterAutoSyncPatchAll));
        }

        public static void AfterAutoSyncPatchAll()
        {
            TryInstallMapEventVisualCompatibility();
        }

        public static bool BeforeHeadlessMapEventVisual(object __0, object __1)
        {
            try
            {
                if (__0 == null || __1 == null ||
                    __1.GetType() != headlessMapEventVisualType)
                {
                    return true;
                }

                // DedicatedServer.Core supplies a headless visual that has no client-side
                // registry identity. Keep the authoritative server assignment but do not
                // publish an unresolvable object reference through generic AutoSync.
                mapEventVisualField.SetValue(__0, __1);
                return false;
            }
            catch (Exception exception)
            {
                WarnOnce("headless map-event visual runtime", exception);
                return true;
            }
        }

        private static Assembly ResolveSingleLoadedAssembly(string simpleName)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => string.Equals(
                    assembly.GetName().Name,
                    simpleName,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new FileLoadException(
                    "Expected exactly one loaded " + simpleName +
                    " assembly, found " + matches.Length + ".");
            }
            return matches[0];
        }

        private static Type RequireType(Assembly assembly, string fullName)
        {
            var type = assembly.GetType(fullName, false, false);
            if (type == null)
                throw new TypeLoadException("Required type is missing: " + fullName + ".");
            return type;
        }

        private static MethodInfo FindSinglePayloadHandler(
            Type handlerType,
            Type messageType,
            bool isPublic)
        {
            return RequireSingleMethod(
                handlerType,
                "Handle",
                false,
                typeof(void),
                method =>
                {
                    if (method.IsPublic != isPublic)
                        return false;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 ||
                        !parameters[0].ParameterType.IsGenericType)
                    {
                        return false;
                    }
                    var payloadType = parameters[0].ParameterType;
                    return string.Equals(
                               payloadType.GetGenericTypeDefinition().FullName,
                               "Common.Messaging.MessagePayload`1",
                               StringComparison.Ordinal) &&
                           payloadType.GetGenericArguments()[0] == messageType;
                });
        }

        private static MethodInfo RequireSingleMethod(
            Type type,
            string name,
            bool isStatic,
            Type returnType,
            Func<MethodInfo, bool> predicate)
        {
            var methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, name, StringComparison.Ordinal) &&
                    method.IsStatic == isStatic &&
                    (returnType == null || method.ReturnType == returnType) &&
                    predicate(method))
                .ToArray();
            if (methods.Length != 1)
            {
                throw new MissingMethodException(
                    type.FullName,
                    name + " resolved " + methods.Length + " times");
            }
            return methods[0];
        }

        private static FieldInfo RequireField(
            Type type,
            string name,
            bool isStatic)
        {
            var fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(field =>
                    string.Equals(field.Name, name, StringComparison.Ordinal) &&
                    field.IsStatic == isStatic)
                .ToArray();
            if (fields.Length != 1)
                throw new MissingFieldException(type.FullName, name);
            return fields[0];
        }

        private static PropertyInfo RequireReadableProperty(
            Type type,
            string name,
            bool isStatic = false)
        {
            var properties = type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(property =>
                {
                    var getter = property.GetGetMethod(true);
                    return string.Equals(property.Name, name, StringComparison.Ordinal) &&
                           getter != null && getter.IsStatic == isStatic;
                })
                .ToArray();
            if (properties.Length != 1)
                throw new MissingMemberException(type.FullName, name);
            return properties[0];
        }

        private static void PatchPrefixOnce(
            MethodInfo target,
            string owner,
            string patchName)
        {
            var existing = Harmony.GetPatchInfo(target);
            if (existing != null &&
                existing.Prefixes.Any(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal)))
            {
                return;
            }

            var patchMethod = typeof(ServerFailedIdCompatibility).GetMethod(
                patchName,
                BindingFlags.Static | BindingFlags.Public);
            if (patchMethod == null)
                throw new MissingMethodException(typeof(ServerFailedIdCompatibility).FullName, patchName);
            new Harmony(owner).Patch(
                target,
                prefix: new HarmonyMethod(patchMethod));
            var applied = Harmony.GetPatchInfo(target);
            if (applied == null ||
                applied.Prefixes.Count(candidate =>
                    string.Equals(candidate.owner, owner, StringComparison.Ordinal)) != 1)
            {
                throw new InvalidOperationException(
                    "Harmony prefix was not installed exactly once: " + owner + ".");
            }
        }

        private static void PatchPostfixOnce(
            MethodInfo target,
            string owner,
            string patchName)
        {
            var existing = Harmony.GetPatchInfo(target);
            if (existing != null &&
                existing.Postfixes.Any(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal)))
            {
                return;
            }

            var patchMethod = typeof(ServerFailedIdCompatibility).GetMethod(
                patchName,
                BindingFlags.Static | BindingFlags.Public);
            if (patchMethod == null)
                throw new MissingMethodException(typeof(ServerFailedIdCompatibility).FullName, patchName);
            new Harmony(owner).Patch(
                target,
                postfix: new HarmonyMethod(patchMethod));
            var applied = Harmony.GetPatchInfo(target);
            if (applied == null ||
                applied.Postfixes.Count(candidate =>
                    string.Equals(candidate.owner, owner, StringComparison.Ordinal)) != 1)
            {
                throw new InvalidOperationException(
                    "Harmony postfix was not installed exactly once: " + owner + ".");
            }
        }

        private static void PatchScopeOnce(
            MethodInfo target,
            string owner,
            string prefixName,
            string finalizerName)
        {
            var existing = Harmony.GetPatchInfo(target);
            var prefixCount = existing == null
                ? 0
                : existing.Prefixes.Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal));
            var finalizerCount = existing == null
                ? 0
                : existing.Finalizers.Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal));
            if (prefixCount == 1 && finalizerCount == 1)
                return;
            if (prefixCount != 0 || finalizerCount != 0)
            {
                throw new InvalidOperationException(
                    "Harmony scope was only partially installed: " + owner + ".");
            }

            var prefix = typeof(ServerFailedIdCompatibility).GetMethod(
                prefixName,
                BindingFlags.Static | BindingFlags.Public);
            var finalizer = typeof(ServerFailedIdCompatibility).GetMethod(
                finalizerName,
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null || finalizer == null)
            {
                throw new MissingMethodException(
                    typeof(ServerFailedIdCompatibility).FullName,
                    prefix == null ? prefixName : finalizerName);
            }

            new Harmony(owner).Patch(
                target,
                prefix: new HarmonyMethod(prefix),
                finalizer: new HarmonyMethod(finalizer));
            var applied = Harmony.GetPatchInfo(target);
            if (applied == null ||
                applied.Prefixes.Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal)) != 1 ||
                applied.Finalizers.Count(patch =>
                    string.Equals(patch.owner, owner, StringComparison.Ordinal)) != 1)
            {
                throw new InvalidOperationException(
                    "Harmony scope was not installed exactly once: " + owner + ".");
            }
        }

        private static void WarnOnce(string area, Exception exception)
        {
            lock (Sync)
            {
                if (!Warnings.Add(area))
                    return;
            }
            var root = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            Console.WriteLine(
                "[BCS Coop Bridge] WARNING: Optional " + area +
                " was not applied; original Coop behavior remains active. " +
                root.GetType().Name + ": " + root.Message);
        }

    }
#endif

    internal static class BridgeRuntime
    {
        private const string BridgeIdPrefix = "BCS.CoopBridge.";
        private const string ConfigurationName = "bcs-coop-bridge.config";
        private const string BridgeAssemblyFileName = "BCS.CoopBridge.dll";
        internal const string ClientMapEventCompatibilityFeature = "ClientMapEventCompatibility";
        internal const string ClientRegistryLifecycleCompatibilityFeature =
            "ClientRegistryLifecycleCompatibility";
        internal const string ClientMapEventPositionAuthorityFeature =
            "ClientMapEventPositionAuthority";
        internal const string ClientTroopUpgradeLoadRepairFeature =
            "ClientTroopUpgradeLoadRepair";
        internal const string ClientSetDisorganizedDiagnosticFeature =
            "ClientSetDisorganizedDiagnostic";
        internal const string ClientTroopRosterSequenceDiagnosticFeature =
            "ClientTroopRosterSequenceDiagnostic";
        internal const string ClientCharacterCreationLifecycleCompatibilityFeature =
            "ClientCharacterCreationLifecycleCompatibility";
        internal const string ServerRegistryLifecycleCompatibilityFeature =
            "ServerRegistryLifecycleCompatibility";
        internal const string ServerPopulationControlFeature = "ServerPopulationControl";
        internal const string ServerFailedIdCompatibilityFeature = "ServerFailedIdCompatibility";
        private static readonly HashSet<string> SupportedRuntimeFeatures =
            new HashSet<string>(StringComparer.Ordinal)
            {
                ClientMapEventCompatibilityFeature,
                ClientRegistryLifecycleCompatibilityFeature,
                ClientMapEventPositionAuthorityFeature,
                ClientTroopUpgradeLoadRepairFeature,
                ClientSetDisorganizedDiagnosticFeature,
                ClientTroopRosterSequenceDiagnosticFeature,
                ClientCharacterCreationLifecycleCompatibilityFeature,
                ServerRegistryLifecycleCompatibilityFeature,
                ServerPopulationControlFeature,
                ServerFailedIdCompatibilityFeature
            };
        private static readonly object Sync = new object();
        private static HashSet<string> activeRuntimeFeatures =
            new HashSet<string>(StringComparer.Ordinal);
        private static bool validated;
        internal static void ValidateInstalledPackage()
        {
            lock (Sync)
            {
                if (validated)
                    return;

                var assemblyPath = typeof(BridgeSubModule).Assembly.Location;
                var moduleRoot = Directory.GetParent(assemblyPath);
                for (var index = 0; index < 2 && moduleRoot != null; index++)
                    moduleRoot = moduleRoot.Parent;
                if (moduleRoot == null)
                    throw new InvalidOperationException("BCS Coop bridge module root could not be resolved.");

                var configurationPath = Path.Combine(moduleRoot.FullName, ConfigurationName);
                if (!File.Exists(configurationPath))
                    throw new FileNotFoundException("BCS Coop bridge configuration is missing.", configurationPath);

                var configurationBytes = File.ReadAllBytes(configurationPath);
                var serverAssemblyPath = Path.Combine(
                    moduleRoot.FullName,
                    "bin",
                    "Win64_Shipping_Server",
                    BridgeAssemblyFileName);
                var clientAssemblyPath = Path.Combine(
                    moduleRoot.FullName,
                    "bin",
                    "Win64_Shipping_Client",
                    BridgeAssemblyFileName);
                if (!File.Exists(serverAssemblyPath))
                    throw new FileNotFoundException(
                        "BCS Coop bridge server runtime is missing.",
                        serverAssemblyPath);
                if (!File.Exists(clientAssemblyPath))
                    throw new FileNotFoundException(
                        "BCS Coop bridge client runtime is missing.",
                        clientAssemblyPath);
#if BCS_SERVER
                var expectedAssemblyPath = serverAssemblyPath;
#else
                var expectedAssemblyPath = clientAssemblyPath;
#endif
                if (!string.Equals(
                        Path.GetFullPath(assemblyPath),
                        Path.GetFullPath(expectedAssemblyPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "BCS Coop bridge runtime loaded from an unexpected package path: " +
                        assemblyPath + ".");
                }
                var expectedId = BridgeIdPrefix + BuildBridgeIdentity(
                    configurationBytes,
                    File.ReadAllBytes(serverAssemblyPath),
                    File.ReadAllBytes(clientAssemblyPath));
                var manifestPath = Path.Combine(moduleRoot.FullName, "SubModule.xml");
                var actualId = ReadModuleIdentity(manifestPath).Id;
                if (!string.Equals(actualId, expectedId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "BCS Coop bridge configuration does not match its module ID. Expected " +
                        expectedId + ", found " + actualId + ".");
                }

                var modulesRoot = moduleRoot.Parent;
                if (modulesRoot == null)
                    throw new InvalidOperationException("Bannerlord Modules directory could not be resolved.");

                var installed = ReadInstalledModules(modulesRoot.FullName);
                var lines = Encoding.UTF8.GetString(configurationBytes)
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                var configurationSchema = lines.Length == 0
                    ? 0
                    : string.Equals(lines[0], "BCS-COOP-BRIDGE|1", StringComparison.Ordinal)
                        ? 1
                        : string.Equals(lines[0], "BCS-COOP-BRIDGE|2", StringComparison.Ordinal)
                            ? 2
                            : 0;
                if (configurationSchema == 0)
                    throw new InvalidDataException("Unsupported BCS Coop bridge configuration schema.");

                var authorityRules = new List<AuthorityRule>();
                var serverFileRedirects = new List<ServerFileRedirect>();
                var serverXmlOverlays = new List<ServerXmlOverlay>();
                var clientAssemblyResolves = new List<ClientAssemblyResolveRule>();
                ServerMapTerrainSizeRule serverMapTerrainSize = null;
                GameVersionCompatibilityRule gameVersionCompatibility = null;
                var ignoredContent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var runtimeFeatures = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in lines.Skip(1))
                {
                    var fields = line.Split('|');
                    if (fields.Length == 2 &&
                        string.Equals(fields[0], "RUNTIME_FEATURE", StringComparison.Ordinal))
                    {
                        if (configurationSchema != 2)
                        {
                            throw new InvalidDataException(
                                "Runtime feature records require bridge configuration schema 2.");
                        }
                        var feature = Decode(fields[1]);
                        if (!SupportedRuntimeFeatures.Contains(feature))
                            throw new InvalidDataException("Unsupported bridge runtime feature: " + feature + ".");
                        if (!runtimeFeatures.Add(feature))
                            throw new InvalidDataException("Duplicate bridge runtime feature: " + feature + ".");
                        continue;
                    }
                    if (fields.Length == 5 &&
                        string.Equals(fields[0], "GAME_VERSION_COMPAT", StringComparison.Ordinal))
                    {
                        if (gameVersionCompatibility != null)
                            throw new InvalidDataException("Duplicate game-version compatibility record.");
                        gameVersionCompatibility = new GameVersionCompatibilityRule(
                            Decode(fields[1]),
                            Decode(fields[2]),
                            Decode(fields[3]),
                            Decode(fields[4]));
                        if (string.IsNullOrWhiteSpace(gameVersionCompatibility.ServerVersion) ||
                            string.IsNullOrWhiteSpace(gameVersionCompatibility.ClientVersion) ||
                            !IsSemanticGameVersion(
                                gameVersionCompatibility.ServerVersion,
                                3) ||
                            !IsSemanticGameVersion(
                                gameVersionCompatibility.ClientVersion,
                                3) ||
                            !IsSemanticGameVersion(
                                gameVersionCompatibility.ServerRuntimeVersion,
                                4) ||
                            !IsSemanticGameVersion(
                                gameVersionCompatibility.ClientRuntimeVersion,
                                4) ||
                            !gameVersionCompatibility.ServerRuntimeVersion.StartsWith(
                                gameVersionCompatibility.ServerVersion + ".",
                                StringComparison.OrdinalIgnoreCase) ||
                            !gameVersionCompatibility.ClientRuntimeVersion.StartsWith(
                                gameVersionCompatibility.ClientVersion + ".",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Game-version compatibility has invalid base or exact runtime versions.");
                        }
                        continue;
                    }

                    if (fields.Length == 4 &&
                        string.Equals(fields[0], "CLIENT_ASSEMBLY_RESOLVE", StringComparison.Ordinal))
                    {
                        var resolverModuleId = Decode(fields[1]);
                        var resolverRelativePath = Decode(fields[2]).Replace('\\', '/');
                        if (string.IsNullOrWhiteSpace(resolverModuleId) ||
                            string.IsNullOrWhiteSpace(resolverRelativePath) ||
                            Path.IsPathRooted(resolverRelativePath) ||
                            !string.Equals(
                                Path.GetExtension(resolverRelativePath),
                                ".dll",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Unsafe client assembly resolver record.");
                        }
#if !BCS_SERVER
                        ModuleIdentity resolverModule;
                        if (!installed.TryGetValue(resolverModuleId, out resolverModule))
                        {
                            throw new InvalidDataException(
                                "Client assembly resolver module is missing: " + resolverModuleId);
                        }
                        var resolverPath = ResolveSafeRelativePath(
                            resolverModule.RootPath,
                            resolverRelativePath);
                        ValidateRequiredRegularFile(
                            resolverPath,
                            "Client assembly resolver");
                        var resolverAssemblyName = AssemblyName.GetAssemblyName(resolverPath).Name;
                        var expectedAssemblyName = Path.GetFileNameWithoutExtension(resolverRelativePath);
                        if (!string.Equals(
                                resolverAssemblyName,
                                expectedAssemblyName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Client assembly resolver identity mismatch: " + resolverRelativePath);
                        }
                        clientAssemblyResolves.Add(new ClientAssemblyResolveRule(
                            expectedAssemblyName,
                            resolverPath));
#endif
                        continue;
                    }

                    if (fields.Length == 3 && string.Equals(fields[0], "IGNORE_CONTENT", StringComparison.Ordinal))
                    {
                        var ignoredModuleId = Decode(fields[1]);
                        var ignoredRelativePath = Decode(fields[2]).Replace('\\', '/');
                        ModuleIdentity ignoredModule;
                        if (!installed.TryGetValue(ignoredModuleId, out ignoredModule))
                            throw new InvalidDataException("Ignored-content module is missing: " + ignoredModuleId);
                        var ignoredPath = ResolveSafeRelativePath(ignoredModule.RootPath, ignoredRelativePath);
                        if (File.Exists(ignoredPath) &&
                            (File.GetAttributes(ignoredPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidDataException(
                                "Ignored visual-content file is linked: " + ignoredModuleId + "/" + ignoredRelativePath);
                        }
                        HashSet<string> moduleIgnored;
                        if (!ignoredContent.TryGetValue(ignoredModuleId, out moduleIgnored))
                        {
                            moduleIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ignoredContent.Add(ignoredModuleId, moduleIgnored);
                        }
                        if (!moduleIgnored.Add(ignoredRelativePath))
                            throw new InvalidDataException("Duplicate ignored-content record.");
                        continue;
                    }

                    if (fields.Length == 4 &&
                        string.Equals(fields[0], "SERVER_FILE_REDIRECT", StringComparison.Ordinal))
                    {
                        var redirectModuleId = Decode(fields[1]);
                        var redirectRelativePath = Decode(fields[2]).Replace('\\', '/');
                        ModuleIdentity redirectModule;
                        if (!installed.TryGetValue(redirectModuleId, out redirectModule))
                            throw new InvalidDataException("Server-file-redirect module is missing: " + redirectModuleId);
                        var redirectPath = ResolveSafeRelativePath(
                            redirectModule.RootPath,
                            redirectRelativePath);
                        if (!File.Exists(redirectPath) ||
                            (File.GetAttributes(redirectPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidDataException(
                                "Server file redirect is missing or linked: " +
                                redirectModuleId + "/" + redirectRelativePath);
                        }
                        if (serverFileRedirects.Any(rule => string.Equals(
                                Path.GetFileName(rule.SourcePath),
                                Path.GetFileName(redirectPath),
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidDataException(
                                "Duplicate server file redirect target: " + Path.GetFileName(redirectPath));
                        }
                        serverFileRedirects.Add(new ServerFileRedirect(
                            redirectModuleId,
                            redirectRelativePath,
                            redirectPath));
                        continue;
                    }

                    if (fields.Length == 10 &&
                        string.Equals(fields[0], "SERVER_MAP_TERRAIN_SIZE", StringComparison.Ordinal))
                    {
                        var terrainModuleId = Decode(fields[1]);
                        var terrainRelativePath = Decode(fields[2]).Replace('\\', '/');
                        var loaderAssemblyName = Decode(fields[6]);
                        var targetAssemblyName = Decode(fields[8]);
                        float terrainWidth;
                        float terrainHeight;
                        if (string.IsNullOrWhiteSpace(terrainModuleId) ||
                            string.IsNullOrWhiteSpace(terrainRelativePath) ||
                            !IsSafeAssemblySimpleName(loaderAssemblyName) ||
                            !IsSafeAssemblySimpleName(targetAssemblyName) ||
                            !float.TryParse(
                                fields[4],
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out terrainWidth) ||
                            !float.TryParse(
                                fields[5],
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out terrainHeight) ||
                            float.IsNaN(terrainWidth) ||
                            float.IsInfinity(terrainWidth) ||
                            terrainWidth <= 0f ||
                            terrainWidth > 1000000f ||
                            float.IsNaN(terrainHeight) ||
                            float.IsInfinity(terrainHeight) ||
                            terrainHeight <= 0f ||
                            terrainHeight > 1000000f)
                        {
                            throw new InvalidDataException("Invalid server map terrain size record.");
                        }
                        ModuleIdentity terrainModule;
                        if (!installed.TryGetValue(terrainModuleId, out terrainModule))
                        {
                            throw new InvalidDataException(
                                "Server map terrain size module is missing: " + terrainModuleId);
                        }
                        var terrainSourcePath = ResolveSafeRelativePath(
                            terrainModule.RootPath,
                            terrainRelativePath);
                        ValidateRequiredModuleRegularFile(
                            terrainModule.RootPath,
                            terrainSourcePath,
                            "Server map terrain size source");
                        var terrainRule = new ServerMapTerrainSizeRule(
                            terrainModuleId,
                            terrainRelativePath,
                            terrainSourcePath,
                            fields[3],
                            terrainWidth,
                            terrainHeight,
                            loaderAssemblyName,
                            fields[7],
                            targetAssemblyName,
                            fields[9]);
                        if (serverMapTerrainSize != null)
                        {
                            var duplicate = serverMapTerrainSize.Matches(terrainRule);
                            throw new InvalidDataException(
                                duplicate
                                    ? "Duplicate server map terrain size record."
                                    : "Conflicting server map terrain size records.");
                        }
                        serverMapTerrainSize = terrainRule;
                        continue;
                    }

                    if (fields.Length == 6 &&
                        string.Equals(fields[0], "SERVER_XML_OVERLAY", StringComparison.Ordinal))
                    {
                        var overlayModuleId = Decode(fields[1]);
                        var sourceRelativePath = Decode(fields[2]).Replace('\\', '/');
                        var overlayRelativePath = Decode(fields[4]).Replace('\\', '/');
                        ModuleIdentity overlayModule;
                        if (!installed.TryGetValue(overlayModuleId, out overlayModule))
                            throw new InvalidDataException(
                                "Server XML overlay module is missing: " + overlayModuleId);
                        var sourcePath = ResolveSafeRelativePath(
                            overlayModule.RootPath,
                            sourceRelativePath);
                        var overlayPath = ResolveSafeRelativePath(
                            moduleRoot.FullName,
                            overlayRelativePath);
                        if (!overlayRelativePath.StartsWith(
                                "bcs-server-overlays/",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Server XML overlay is outside bcs-server-overlays: " +
                                overlayRelativePath);
                        }
                        ValidateRequiredRegularFile(
                            sourcePath,
                            "Server XML overlay source");
#if BCS_SERVER
                        ValidateRequiredRegularFile(
                            overlayPath,
                            "Server XML overlay payload");
#endif
                        if (serverXmlOverlays.Any(rule => string.Equals(
                                rule.SourcePath,
                                sourcePath,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidDataException(
                                "Duplicate server XML overlay source: " + sourcePath);
                        }
                        serverXmlOverlays.Add(new ServerXmlOverlay(
                            overlayModuleId,
                            sourceRelativePath,
                            sourcePath,
                            overlayPath));
                        continue;
                    }

                    if ((fields.Length == 6 || fields.Length == 7) &&
                        string.Equals(fields[0], "AUTHORITY", StringComparison.Ordinal))
                    {
                        int parameterCount;
                        if (!int.TryParse(fields[5], out parameterCount) || parameterCount < 0)
                            throw new InvalidDataException("Invalid authority-rule parameter count.");
                        var scope = fields.Length == 6
                            ? AuthorityScope.ServerOnly
                            : ParseAuthorityScope(fields[6]);
                        authorityRules.Add(new AuthorityRule(
                            Decode(fields[1]),
                            Decode(fields[2]),
                            Decode(fields[3]),
                            Decode(fields[4]),
                            parameterCount,
                            scope));
                        continue;
                    }

                    if (fields.Length == 3 && string.Equals(fields[0], "CONTENT", StringComparison.Ordinal))
                    {
                        var contentModuleId = Decode(fields[1]);
                        ModuleIdentity contentModule;
                        if (!installed.TryGetValue(contentModuleId, out contentModule))
                            throw new InvalidDataException("Required Coop bridge module is missing: " + contentModuleId);
                        HashSet<string> moduleIgnored;
                        ignoredContent.TryGetValue(contentModuleId, out moduleIgnored);
                        continue;
                    }

                    if (fields.Length != 5 || !string.Equals(fields[0], "MODULE", StringComparison.Ordinal))
                        throw new InvalidDataException("Malformed BCS Coop bridge record.");

                    var moduleId = Decode(fields[1]);
                    var version = Decode(fields[2]);
                    var dllName = Decode(fields[3]);
                    ModuleIdentity module;
                    if (!installed.TryGetValue(moduleId, out module))
                        throw new InvalidDataException("Required Coop bridge module is missing: " + moduleId);
                    if (!string.Equals(module.Version, version, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Coop bridge module version mismatch for " + moduleId +
                            ". Expected " + version + ", found " + module.Version + ".");
                    }

                    if (dllName.Length == 0)
                        continue;
                    if (!string.Equals(Path.GetFileName(dllName), dllName, StringComparison.Ordinal) ||
                        !string.Equals(Path.GetExtension(dllName), ".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Unsafe DLL name in BCS Coop bridge configuration: " + dllName);
                    }

                    var dllPath = FindAssembly(module.RootPath, dllName);
                    if (dllPath == null)
                        throw new FileNotFoundException("Required Coop bridge assembly is missing: " + moduleId + "/" + dllName);
                    ValidateRequiredModuleAssembly(
                        module.RootPath,
                        dllPath,
                        dllName,
                        "Required Coop bridge assembly");
                }

                if (configurationSchema == 1)
                    runtimeFeatures.UnionWith(SupportedRuntimeFeatures);
                activeRuntimeFeatures = runtimeFeatures;

                ApplyClientAssemblyResolves(clientAssemblyResolves);
                ApplyGameVersionCompatibility(installed, gameVersionCompatibility, actualId);
#if BCS_SERVER
                ApplyServerMapTerrainSize(serverMapTerrainSize, actualId);
#endif
                ApplyServerFileRedirects(serverFileRedirects, actualId);
                ApplyServerXmlOverlays(serverXmlOverlays, actualId);
                ApplyAuthorityRules(installed, authorityRules, actualId);
#if !BCS_SERVER
                if (runtimeFeatures.Contains(ClientMapEventCompatibilityFeature))
                    ClientMapEventCompatibility.Install(actualId);
                ModuleIdentity coopModule;
                if (!installed.TryGetValue("Coop", out coopModule))
                    throw new InvalidDataException("Coop is missing for client handler registration.");
                ClientCoopHandlerRegistration.Install(coopModule.RootPath, actualId);
#endif
                validated = true;
                Trace.WriteLine("BCS Coop bridge package validated: " + actualId);
                Console.WriteLine("[BCS Coop Bridge] Package validated: " + actualId);
            }
        }

        internal static void RecordStartupFailure(Exception exception)
        {
            try
            {
                var path = Path.Combine(
                    Path.GetTempPath(),
                    "bcs-coop-bridge-startup-failure.log");
                File.WriteAllText(
                    path,
                    DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine + exception);
            }
            catch
            {
                // Preserve the original startup exception even if diagnostics cannot be written.
            }
        }

        internal static void RecordStartupProgress(string message)
        {
            try
            {
                var path = Path.Combine(
                    Path.GetTempPath(),
                    "bcs-coop-bridge-startup-progress.log");
                File.AppendAllText(
                    path,
                    DateTimeOffset.UtcNow.ToString("O") +
                    " [BCS Coop Bridge] " + message + Environment.NewLine);
            }
            catch
            {
                // Diagnostic tracing must not alter bridge startup behavior.
            }
        }

        internal static void MarkCoopContainerReady()
        {
            ValidateInstalledPackage();
            Trace.WriteLine("BCS Coop bridge handler discovered by Coop.");
            Console.WriteLine("[BCS Coop Bridge] Handler discovered by Coop.");
        }

        internal static bool IsRuntimeFeatureEnabled(string feature)
        {
            if (string.IsNullOrWhiteSpace(feature) || !SupportedRuntimeFeatures.Contains(feature))
                throw new InvalidDataException("Unknown bridge runtime feature: " + feature + ".");
            ValidateInstalledPackage();
            lock (Sync)
                return activeRuntimeFeatures.Contains(feature);
        }

        internal static bool IsServerProcess()
        {
#if BCS_SERVER
            if (ModInformation.IsServer)
                return true;
            var assemblyDirectory = Path.GetDirectoryName(typeof(BridgeSubModule).Assembly.Location);
            return string.Equals(
                Path.GetFileName(assemblyDirectory),
                "Win64_Shipping_Server",
                StringComparison.OrdinalIgnoreCase);
#else
            return false;
#endif
        }

        private static AuthorityScope ParseAuthorityScope(string value)
        {
            if (string.Equals(value, "SERVER_ONLY", StringComparison.Ordinal))
                return AuthorityScope.ServerOnly;
            if (string.Equals(value, "CLIENT_ONLY", StringComparison.Ordinal))
                return AuthorityScope.ClientOnly;
            if (string.Equals(value, "SERVER_SETTINGS_FALLBACK", StringComparison.Ordinal))
                return AuthorityScope.ServerSettingsFallback;
            throw new InvalidDataException("Invalid authority-rule scope: " + value);
        }

        private static bool IsSafeAssemblySimpleName(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length > 128 ||
                value[0] == '.' ||
                value[value.Length - 1] == '.' ||
                value.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            foreach (var character in value)
            {
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9') ||
                      character == '.' ||
                      character == '_' ||
                      character == '-'))
                {
                    return false;
                }
            }
            return true;
        }

        private static Dictionary<string, ModuleIdentity> ReadInstalledModules(string modulesRoot)
        {
#if BCS_SERVER
            return ReadModulesFromDirectory(modulesRoot);
#else
            var result = new Dictionary<string, ModuleIdentity>(StringComparer.OrdinalIgnoreCase);
            var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                         .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
            {
                string location;
                try
                {
                    location = assembly.Location;
                }
                catch (NotSupportedException)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(location))
                    continue;
                var moduleRoot = FindContainingModuleRoot(location);
                if (moduleRoot != null)
                    AddClientModule(result, priorities, moduleRoot, 1);
            }

            foreach (var directory in Directory.EnumerateDirectories(modulesRoot, "*", SearchOption.TopDirectoryOnly))
                AddClientModule(result, priorities, directory, 2);
            AddActiveClientModules(result, priorities);
            return result;
#endif
        }

        private static Dictionary<string, ModuleIdentity> ReadModulesFromDirectory(string modulesRoot)
        {
            var result = new Dictionary<string, ModuleIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(modulesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var manifest = Path.Combine(directory, "SubModule.xml");
                if (!File.Exists(manifest))
                    continue;
                var identity = ReadModuleIdentity(manifest);
                if (identity.Id.Length == 0)
                    continue;
                if (result.ContainsKey(identity.Id))
                    throw new InvalidDataException("Duplicate Bannerlord module ID: " + identity.Id);
                result.Add(identity.Id, new ModuleIdentity(identity.Id, identity.Version, directory));
            }
            return result;
        }

#if !BCS_SERVER
        private static void AddActiveClientModules(
            IDictionary<string, ModuleIdentity> result,
            IDictionary<string, int> priorities)
        {
            var moduleManagerAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    "TaleWorlds.ModuleManager",
                    StringComparison.OrdinalIgnoreCase));
            if (moduleManagerAssembly == null)
                return;

            var helper = moduleManagerAssembly.GetType(
                "TaleWorlds.ModuleManager.ModuleHelper",
                true,
                false);
            var getActiveModules = helper.GetMethod(
                "GetActiveModules",
                BindingFlags.Static | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (getActiveModules == null)
                throw new MissingMethodException(helper.FullName, "GetActiveModules");

            IEnumerable activeModules;
            try
            {
                activeModules = getActiveModules.Invoke(null, null) as IEnumerable;
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "Bannerlord active module registry could not be read.",
                    exception.InnerException ?? exception);
            }
            if (activeModules == null)
                throw new InvalidDataException("Bannerlord active module registry returned no module collection.");

            foreach (var module in activeModules)
            {
                if (module == null)
                    continue;
                var idProperty = module.GetType().GetProperty(
                    "Id",
                    BindingFlags.Instance | BindingFlags.Public);
                var folderPath = module.GetType().GetProperty(
                    "FolderPath",
                    BindingFlags.Instance | BindingFlags.Public);
                if (idProperty == null || idProperty.PropertyType != typeof(string))
                    throw new MissingMemberException(module.GetType().FullName, "Id");
                if (folderPath == null || folderPath.PropertyType != typeof(string))
                    throw new MissingMemberException(module.GetType().FullName, "FolderPath");
                var moduleId = idProperty.GetValue(module, null) as string;
                var moduleRoot = folderPath.GetValue(module, null) as string;
                if (string.IsNullOrWhiteSpace(moduleRoot) ||
                    !File.Exists(Path.Combine(moduleRoot, "SubModule.xml")))
                {
                    if (!string.IsNullOrWhiteSpace(moduleId) && result.ContainsKey(moduleId))
                        continue;
                    throw new InvalidDataException(
                        "Bannerlord active module '" + (moduleId ?? "<unknown>") +
                        "' has no readable SubModule.xml: " + (moduleRoot ?? "<null>"));
                }
                AddClientModule(result, priorities, moduleRoot, 0);
            }
        }

        private static string FindContainingModuleRoot(string assemblyPath)
        {
            var directory = Directory.GetParent(Path.GetFullPath(assemblyPath));
            for (var depth = 0; depth < 6 && directory != null; depth++)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SubModule.xml")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }

        private static void AddClientModule(
            IDictionary<string, ModuleIdentity> result,
            IDictionary<string, int> priorities,
            string directory,
            int priority)
        {
            var manifest = Path.Combine(directory, "SubModule.xml");
            if (!File.Exists(manifest))
                return;
            var identity = ReadModuleIdentity(manifest);
            if (identity.Id.Length == 0)
                return;
            var normalizedRoot = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            ModuleIdentity existing;
            if (!result.TryGetValue(identity.Id, out existing))
            {
                result.Add(identity.Id, new ModuleIdentity(identity.Id, identity.Version, normalizedRoot));
                priorities.Add(identity.Id, priority);
                return;
            }

            if (string.Equals(existing.RootPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return;

            var existingPriority = priorities[identity.Id];
            if (priority > existingPriority)
                return;
            if (priority == existingPriority)
                throw new InvalidDataException("Duplicate active Bannerlord module ID: " + identity.Id);

            result[identity.Id] = new ModuleIdentity(identity.Id, identity.Version, normalizedRoot);
            priorities[identity.Id] = priority;
        }
#endif

        private static ModuleIdentity ReadModuleIdentity(string manifestPath)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4 * 1024 * 1024
            }))
            {
                document.Load(reader);
            }

            var root = document.DocumentElement;
            if (root == null || !string.Equals(root.LocalName, "Module", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid Bannerlord module manifest: " + manifestPath);
            return new ModuleIdentity(
                Value(root, "Id"),
                Value(root, "Version"),
                Path.GetDirectoryName(manifestPath));
        }

        private static string Value(XmlElement root, string elementName)
        {
            foreach (XmlNode child in root.ChildNodes)
            {
                var element = child as XmlElement;
                if (element != null && string.Equals(element.LocalName, elementName, StringComparison.OrdinalIgnoreCase))
                    return element.GetAttribute("value");
            }
            return string.Empty;
        }

        private static bool IsSemanticGameVersion(string value, int componentCount)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2 ||
                (value[0] != 'a' && value[0] != 'b' && value[0] != 'd' &&
                 value[0] != 'e' && value[0] != 'v'))
            {
                return false;
            }

            var components = value.Substring(1).Split('.');
            if (components.Length != componentCount)
                return false;
            for (var index = 0; index < components.Length; index++)
            {
                int component;
                if (!int.TryParse(
                        components[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out component) ||
                    component < 0 ||
                    (index == 0 && component == 0) ||
                    (index == components.Length - 1 && componentCount == 4 && component == 0))
                {
                    return false;
                }
            }
            return true;
        }

        internal static string FindAssemblyForRegistration(string moduleRoot, string dllName)
        {
            return FindAssembly(moduleRoot, dllName);
        }

        private static string FindAssembly(string moduleRoot, string dllName)
        {
            var bins = new[]
            {
                "Win64_Shipping_Server",
                "Win64_Shipping_Client",
                "Gaming.Desktop.x64_Shipping_Client"
            };
            foreach (var bin in bins)
            {
                var candidate = Path.Combine(moduleRoot, "bin", bin, dllName);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static void ApplyAuthorityRules(
            IDictionary<string, ModuleIdentity> installed,
            IEnumerable<AuthorityRule> rules,
            string bridgeId)
        {
            var harmony = new Harmony(bridgeId);
            foreach (var rule in rules)
            {
                var prefixName = rule.Scope == AuthorityScope.ServerOnly
                    ? "ServerOnly"
                    : rule.Scope == AuthorityScope.ClientOnly
                        ? "ClientOnly"
                        : "ServerSettingsFallback";
                var prefix = typeof(AuthorityPrefix).GetMethod(
                    prefixName,
                    BindingFlags.Static | BindingFlags.Public);
                if (prefix == null)
                    throw new MissingMethodException(typeof(AuthorityPrefix).FullName, prefixName);

                ModuleIdentity module;
                if (!installed.TryGetValue(rule.ModuleId, out module))
                    throw new InvalidDataException("Authority-rule module is missing: " + rule.ModuleId);
                if (!string.Equals(Path.GetFileName(rule.DllName), rule.DllName, StringComparison.Ordinal) ||
                    !string.Equals(Path.GetExtension(rule.DllName), ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Unsafe authority-rule DLL name: " + rule.DllName);
                }

                var assemblyPath = FindAssembly(module.RootPath, rule.DllName);
                if (assemblyPath == null)
                    throw new FileNotFoundException(
                        "Authority-rule assembly is missing: " + rule.ModuleId + "/" + rule.DllName);
                var expectedName = AssemblyName.GetAssemblyName(assemblyPath).Name;
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                    string.Equals(candidate.GetName().Name, expectedName, StringComparison.OrdinalIgnoreCase))
                    ?? Assembly.LoadFrom(assemblyPath);
                var type = assembly.GetType(rule.TypeName, true, false);
                var methods = type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method =>
                        string.Equals(method.Name, rule.MethodName, StringComparison.Ordinal) &&
                        method.GetParameters().Length == rule.ParameterCount)
                    .ToArray();
                if (methods.Length != 1)
                {
                    throw new MissingMethodException(
                        rule.TypeName,
                        rule.MethodName + " with " + rule.ParameterCount + " parameter(s)");
                }

                harmony.Patch(
                    methods[0],
                    prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name));
                Trace.WriteLine(
                    "BCS Coop bridge invocation-scope rule installed: " +
                    rule.TypeName + "::" + rule.MethodName);
                Console.WriteLine(
                    "[BCS Coop Bridge] " + rule.Scope + " rule installed: " +
                    rule.TypeName + "::" + rule.MethodName +
                    " (server=" + IsServerProcess() + ")");
            }
        }

        private static void ApplyClientAssemblyResolves(
            IReadOnlyCollection<ClientAssemblyResolveRule> rules)
        {
#if BCS_SERVER
            if (rules.Count != 0)
                throw new InvalidDataException("Client assembly resolvers were activated on the server runtime.");
#else
            if (rules.Count == 0)
                return;
            ClientAssemblyResolver.Configure(rules);
            Console.WriteLine(
                "[BCS Coop Bridge] Installed " + rules.Count +
                " client assembly resolver(s).");
#endif
        }

        private static void ApplyGameVersionCompatibility(
            IDictionary<string, ModuleIdentity> installed,
            GameVersionCompatibilityRule rule,
            string bridgeId)
        {
            if (rule == null)
                return;

            RecordStartupProgress("Game-version compatibility validation started");

            ModuleIdentity native;
            if (!installed.TryGetValue("Native", out native))
                throw new InvalidDataException("Native module is missing for game-version compatibility.");
            var expectedVersion = IsServerProcess()
                ? rule.ServerVersion
                : rule.ClientVersion;
            if (!GameVersionCompatibilityPrefix.MatchesVersion(native.Version, expectedVersion))
            {
                throw new InvalidDataException(
                    "Bannerlord version mismatch. Expected " + expectedVersion +
                    ", found " + native.Version + ".");
            }

            var runtimeDirectory = Path.GetDirectoryName(typeof(MBSubModuleBase).Assembly.Location);
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new InvalidOperationException("Bannerlord runtime directory could not be resolved.");
            RecordStartupProgress("Game runtime directory validated");

            if (IsServerProcess())
            {
                Console.WriteLine(
                    "[BCS Coop Bridge] Server game runtime observed as " +
                    rule.ServerRuntimeVersion + ".");
            }

#if BCS_SERVER
            if (IsServerProcess())
            {
                ServerCurrentVersionCompatibility.Install(
                    rule.ServerRuntimeVersion,
                    bridgeId);
                RecordStartupProgress("MBSaveLoad current-version compatibility installed");
            }
#endif

            if (string.Equals(
                    rule.ServerVersion,
                    rule.ClientVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
#if BCS_SERVER
                if (IsServerProcess())
                {
                    var sameBaseValidatorType = ResolveReleasedModuleValidatorType();
                    RecordStartupProgress("Released ModuleValidator target resolved");
                    InstallSupportedClientOnlyModuleCompatibility(
                        sameBaseValidatorType,
                        bridgeId);
                }
#endif
                Console.WriteLine(
                    "[BCS Coop Bridge] Server/client base game versions match; " +
                    "Coop's game-version validator was not patched (runtime pair " +
                    rule.ServerRuntimeVersion + " -> " + rule.ClientRuntimeVersion + ").");
                return;
            }

            var validatorType = ResolveReleasedModuleValidatorType();
            var methods = validatorType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(method =>
                    string.Equals(method.Name, "ValidateGameVersion", StringComparison.Ordinal) &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3)
                .ToArray();
            if (methods.Length != 1)
            {
                throw new MissingMethodException(
                    validatorType.FullName,
                    "ValidateGameVersion with 3 parameter(s)");
            }
            RecordStartupProgress("Released ModuleValidator target resolved");
            var prefix = typeof(GameVersionCompatibilityPrefix).GetMethod(
                "Validate",
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
                throw new MissingMethodException(
                    typeof(GameVersionCompatibilityPrefix).FullName,
                    "Validate");
            GameVersionCompatibilityPrefix.Configure(
                rule.ServerRuntimeVersion,
                rule.ClientRuntimeVersion);
            RecordStartupProgress("Harmony game-version patch starting");
            new Harmony(bridgeId + ".game-version-compatibility")
                .Patch(
                    methods[0],
                    prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name));
#if BCS_SERVER
            if (IsServerProcess())
                InstallSupportedClientOnlyModuleCompatibility(validatorType, bridgeId);
#endif
            RecordStartupProgress("Harmony game-version patch completed");
            Console.WriteLine(
                "[BCS Coop Bridge] Installed version-scoped Coop game-version compatibility: " +
                rule.ServerRuntimeVersion + " -> " + rule.ClientRuntimeVersion + ".");
        }

        private static Type ResolveReleasedModuleValidatorType()
        {
            var validatorType = AccessTools.TypeByName(
                "GameInterface.Services.Modules.Validators.ModuleValidator");
#if BCS_SERVER
            if (validatorType == null)
            {
                try
                {
                    Assembly.Load(new AssemblyName("GameInterface"));
                }
                catch (FileNotFoundException)
                {
                    // The fail-closed type check below reports the stable bridge error.
                }
                validatorType = AccessTools.TypeByName(
                    "GameInterface.Services.Modules.Validators.ModuleValidator");
            }
#endif
            if (validatorType == null)
                throw new TypeLoadException("Released Coop ModuleValidator type was not found.");
            return validatorType;
        }

#if BCS_SERVER
        private static void InstallSupportedClientOnlyModuleCompatibility(
            Type validatorType,
            string bridgeId)
        {
            var methods = validatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method =>
                    string.Equals(method.Name, "Validate", StringComparison.Ordinal) &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3)
                .ToArray();
            if (methods.Length != 1)
            {
                throw new MissingMethodException(
                    validatorType.FullName,
                    "Validate with 3 parameter(s)");
            }

            var prefix = typeof(SupportedClientOnlyModulePrefix).GetMethod(
                "Filter",
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
            {
                throw new MissingMethodException(
                    typeof(SupportedClientOnlyModulePrefix).FullName,
                    "Filter");
            }

            new Harmony(bridgeId + ".client-only-module-compatibility")
                .Patch(
                    methods[0],
                    prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name));
            Console.WriteLine(
                "[BCS Coop Bridge] Installed exact client-only module compatibility.");
        }
#endif

        private static void ApplyServerFileRedirects(
            IEnumerable<ServerFileRedirect> rules,
            string bridgeId)
        {
            var redirects = rules.ToArray();
            if (redirects.Length == 0)
                return;
            if (!IsServerProcess())
            {
                Console.WriteLine("[BCS Coop Bridge] Server file redirects disabled on client.");
                return;
            }

            foreach (var rule in redirects)
            {
                CachePathRedirectPrefix.Configure(
                    Path.GetFileName(rule.SourcePath),
                    rule.SourcePath);
            }

            var openCacheType = AccessTools.TypeByName(
                "TaleWorlds.CampaignSystem.Map.DistanceCache.NavigationCache`1");
            var settlementType = AccessTools.TypeByName(
                "TaleWorlds.CampaignSystem.Settlements.Settlement");
            if (openCacheType == null || settlementType == null)
            {
                throw new TypeLoadException(
                    "Bannerlord navigation-cache types required by the server file redirect were not found.");
            }

            var cacheType = openCacheType.MakeGenericType(settlementType);
            var deserialize = cacheType.GetMethod(
                "Deserialize",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (deserialize == null)
                throw new MissingMethodException(cacheType.FullName, "Deserialize(string)");
            var prefix = typeof(CachePathRedirectPrefix).GetMethod(
                "Redirect",
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
                throw new MissingMethodException(typeof(CachePathRedirectPrefix).FullName, "Redirect");
            new Harmony(bridgeId + ".server-file-redirect")
                .Patch(
                    deserialize,
                    prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name));
            Console.WriteLine(
                "[BCS Coop Bridge] Installed " + redirects.Length + " server file redirect(s).");
        }

#if BCS_SERVER
        private static void ApplyServerMapTerrainSize(
            ServerMapTerrainSizeRule rule,
            string bridgeId)
        {
            if (rule == null)
                return;

            if (!string.Equals(
                    rule.LoaderAssemblyName,
                    "DedicatedServer.Core",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    rule.TargetAssemblyName,
                    "SandBox",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Server map terrain size requires the DedicatedServer.Core loader and SandBox target.");
            }

            ResolveRequiredRuntimeAssembly(
                rule.LoaderAssemblyName,
                true,
                "Server map terrain size loader");
            var targetAssembly = ResolveRequiredRuntimeAssembly(
                rule.TargetAssemblyName,
                false,
                "Server map terrain size target");
            var mapSceneType = targetAssembly.GetType("SandBox.MapScene", false, false);
            if (mapSceneType == null)
            {
                throw new TypeLoadException(
                    "Bannerlord SandBox assembly does not contain SandBox.MapScene.");
            }
            var targets = mapSceneType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, "GetTerrainSize", StringComparison.Ordinal) &&
                    !method.IsGenericMethodDefinition &&
                    method.ReturnType == typeof(TaleWorlds.Library.Vec2) &&
                    method.GetParameters().Length == 0)
                .ToArray();
            if (targets.Length != 1)
            {
                throw new MissingMethodException(
                    mapSceneType.FullName,
                    "GetTerrainSize with zero parameters returning Vec2");
            }
            var prefix = typeof(ServerMapTerrainSizePrefix).GetMethod(
                "Override",
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
            {
                throw new MissingMethodException(
                    typeof(ServerMapTerrainSizePrefix).FullName,
                    "Override");
            }

            ServerMapTerrainSizePrefix.Configure(rule.Width, rule.Height);
            new Harmony(bridgeId + ".server-map-terrain-size")
                .Patch(
                    targets[0],
                    prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name)
                    {
                        priority = Priority.First
                    });
            Console.WriteLine(
                "[BCS Coop Bridge] Installed server map terrain size " +
                rule.Width.ToString("R", CultureInfo.InvariantCulture) + "x" +
                rule.Height.ToString("R", CultureInfo.InvariantCulture) + " from " +
                rule.ModuleId + "/" + rule.RelativePath + ".");
        }

        private static Assembly ResolveRequiredRuntimeAssembly(
            string expectedName,
            bool requireAlreadyLoaded,
            string description)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(candidate => string.Equals(
                    candidate.GetName().Name,
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (loaded.Length > 1)
            {
                throw new FileLoadException(
                    description + " has multiple loaded assemblies named " + expectedName + ".");
            }

            Assembly assembly;
            if (loaded.Length == 1)
            {
                assembly = loaded[0];
            }
            else
            {
                if (requireAlreadyLoaded)
                {
                    throw new FileNotFoundException(
                        description + " is not loaded: " + expectedName + ".dll");
                }
                assembly = Assembly.Load(new AssemblyName(expectedName));
            }

            if (!string.Equals(
                    assembly.GetName().Name,
                    expectedName,
                    StringComparison.Ordinal))
            {
                throw new FileLoadException(
                    description + " loaded with an unexpected assembly identity: " +
                    assembly.FullName + ".");
            }

            string location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException exception)
            {
                throw new FileLoadException(
                    description + " has no verifiable file location.",
                    expectedName + ".dll",
                    exception);
            }
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new FileLoadException(
                    description + " has no verifiable file location.",
                    expectedName + ".dll");
            }

            var canonicalLocation = Path.GetFullPath(location);
            if (!File.Exists(canonicalLocation) ||
                (File.GetAttributes(canonicalLocation) & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException(
                    description + " location is missing or linked.",
                    canonicalLocation);
            }
            var fileIdentity = AssemblyName.GetAssemblyName(canonicalLocation).Name;
            if (!string.Equals(fileIdentity, expectedName, StringComparison.Ordinal))
            {
                throw new FileLoadException(
                    description + " file identity mismatch. Expected " + expectedName +
                    ", found " + fileIdentity + ".",
                    canonicalLocation);
            }
            return assembly;
        }

#endif

        private static void ApplyServerXmlOverlays(
            IEnumerable<ServerXmlOverlay> rules,
            string bridgeId)
        {
            var overlays = rules.ToArray();
            if (overlays.Length == 0)
                return;
            if (!IsServerProcess())
            {
                Console.WriteLine("[BCS Coop Bridge] Server XML overlays disabled on client.");
                return;
            }

            foreach (var overlay in overlays)
                ServerXmlOverlayPrefix.Configure(overlay.SourcePath, overlay.OverlayPath);

            var objectManagerType = AccessTools.TypeByName("TaleWorlds.ObjectSystem.MBObjectManager");
            if (objectManagerType == null)
                throw new TypeLoadException("Bannerlord MBObjectManager required by server XML overlays was not found.");
            var loader = objectManagerType.GetMethod(
                "CreateDocumentFromXmlFile",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string), typeof(bool) },
                null);
            if (loader == null)
            {
                throw new MissingMethodException(
                    objectManagerType.FullName,
                    "CreateDocumentFromXmlFile(string,string,bool)");
            }
            var prefix = typeof(ServerXmlOverlayPrefix).GetMethod(
                "Redirect",
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
                throw new MissingMethodException(typeof(ServerXmlOverlayPrefix).FullName, "Redirect");
#if BCS_SERVER
            var xsltLoader = ResolveRequiredServerXsltLoader(objectManagerType);
#endif
            var harmony = new Harmony(bridgeId + ".server-xml-overlays");
            harmony.Patch(
                loader,
                prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name));
#if BCS_SERVER
            harmony.Patch(
                xsltLoader,
                prefix: new HarmonyMethod(prefix.DeclaringType, prefix.Name));
#endif
            var mergedLoader = objectManagerType.GetMethod(
                "GetMergedXmlForManaged",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(string), typeof(bool), typeof(bool), typeof(string) },
                null);
            var mergedAudit = typeof(ServerXmlOverlayPrefix).GetMethod(
                "AuditMerged",
                BindingFlags.Static | BindingFlags.Public);
            if (mergedLoader == null || mergedAudit == null)
            {
                throw new MissingMethodException(
                    objectManagerType.FullName,
                    "GetMergedXmlForManaged(string,bool,bool,string)");
            }
            new Harmony(bridgeId + ".server-xml-overlay-audit")
                .Patch(
                    mergedLoader,
                    postfix: new HarmonyMethod(mergedAudit.DeclaringType, mergedAudit.Name));
            Console.WriteLine(
                "[BCS Coop Bridge] Installed " + overlays.Length + " server XML overlay(s).");
        }

#if BCS_SERVER
        private static MethodInfo ResolveRequiredServerXsltLoader(Type objectManagerType)
        {
            var objectSystem = ResolveRequiredRuntimeAssembly(
                "TaleWorlds.ObjectSystem",
                true,
                "Server XML overlay XSLT loader");
            if (objectManagerType.Assembly != objectSystem)
            {
                throw new InvalidDataException(
                    "Server XML overlay XSLT loader did not match its required ObjectSystem identity.");
            }

            var methods = objectManagerType.GetMethods(
                    BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, "ApplyXslt", StringComparison.Ordinal) &&
                    !method.IsGenericMethodDefinition &&
                    method.ReturnType == typeof(XmlDocument) &&
                    method.GetParameters().Select(parameter => parameter.ParameterType)
                        .SequenceEqual(new[] { typeof(string), typeof(XmlDocument) }))
                .ToArray();
            if (methods.Length != 1 || !methods[0].IsPublic)
            {
                throw new InvalidDataException(
                    "Required server XML overlay ApplyXslt method did not match its unique signature.");
            }

            return methods[0];
        }
#endif

        private static string AppendDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                return path;
            return path + Path.DirectorySeparatorChar;
        }

        private static string ResolveSafeRelativePath(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Bridge path must be relative: " + relativePath);
            var segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment == "." || segment == ".."))
                throw new InvalidDataException("Unsafe bridge path: " + relativePath);
            var canonicalRoot = AppendDirectorySeparator(Path.GetFullPath(root));
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bridge path escaped its module: " + relativePath);
            return path;
        }

        private static void ValidateRequiredRegularFile(
            string path,
            string description)
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException(description + " is missing or linked.", path);
            }
        }

        private static void ValidateRequiredModuleRegularFile(
            string moduleRoot,
            string path,
            string description)
        {
            var canonicalRoot = Path.GetFullPath(moduleRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var canonicalPath = Path.GetFullPath(path);
            var rootedPrefix = AppendDirectorySeparator(canonicalRoot);
            if (!canonicalPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(description + " escaped its module: " + path);

            var reachedRoot = false;
            for (var parent = Directory.GetParent(canonicalPath); parent != null; parent = parent.Parent)
            {
                var parentPath = Path.GetFullPath(parent.FullName).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if ((File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        description + " traverses a linked directory: " + path);
                }
                if (string.Equals(parentPath, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    reachedRoot = true;
                    break;
                }
            }
            if (!reachedRoot)
                throw new InvalidDataException(description + " escaped its module: " + path);

            ValidateRequiredRegularFile(canonicalPath, description);
        }

        private static void ValidateRequiredModuleAssembly(
            string moduleRoot,
            string path,
            string dllName,
            string description)
        {
            ValidateRequiredModuleRegularFile(moduleRoot, path, description);

            string assemblyName;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(path).Name;
            }
            catch (Exception exception) when (
                exception is BadImageFormatException || exception is FileLoadException)
            {
                throw new InvalidDataException(
                    description + " is not a readable managed assembly: " + path,
                    exception);
            }

            var expectedName = Path.GetFileNameWithoutExtension(dllName);
            if (string.IsNullOrWhiteSpace(assemblyName) ||
                !string.Equals(assemblyName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    description + " identity mismatch for " + dllName +
                    ". Found " + (assemblyName ?? "<null>") + ".");
            }
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Invalid base64 value in BCS Coop bridge configuration.", exception);
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        private static string BuildBridgeIdentity(
            byte[] configuration,
            byte[] serverAssembly,
            byte[] clientAssembly)
        {
            using (var payload = new MemoryStream())
            {
                payload.Write(configuration, 0, configuration.Length);
                payload.Write(serverAssembly, 0, serverAssembly.Length);
                payload.Write(clientAssembly, 0, clientAssembly.Length);
                return Hash(payload.ToArray()).Substring(0, 24).ToLowerInvariant();
            }
        }

        private sealed class ModuleIdentity
        {
            internal ModuleIdentity(string id, string version, string rootPath)
            {
                Id = id ?? string.Empty;
                Version = version ?? string.Empty;
                RootPath = rootPath ?? string.Empty;
            }

            internal string Id { get; private set; }
            internal string Version { get; private set; }
            internal string RootPath { get; private set; }
        }

        private sealed class AuthorityRule
        {
            internal AuthorityRule(
                string moduleId,
                string dllName,
                string typeName,
                string methodName,
                int parameterCount,
                AuthorityScope scope)
            {
                ModuleId = moduleId;
                DllName = dllName;
                TypeName = typeName;
                MethodName = methodName;
                ParameterCount = parameterCount;
                Scope = scope;
            }

            internal string ModuleId { get; private set; }
            internal string DllName { get; private set; }
            internal string TypeName { get; private set; }
            internal string MethodName { get; private set; }
            internal int ParameterCount { get; private set; }
            internal AuthorityScope Scope { get; private set; }
        }

        private sealed class ServerFileRedirect
        {
            internal ServerFileRedirect(string moduleId, string relativePath, string sourcePath)
            {
                ModuleId = moduleId;
                RelativePath = relativePath;
                SourcePath = sourcePath;
            }

            internal string ModuleId { get; private set; }
            internal string RelativePath { get; private set; }
            internal string SourcePath { get; private set; }
        }

        private sealed class ServerMapTerrainSizeRule
        {
            internal ServerMapTerrainSizeRule(
                string moduleId,
                string relativePath,
                string sourcePath,
                string sourceSha256,
                float width,
                float height,
                string loaderAssemblyName,
                string loaderAssemblySha256,
                string targetAssemblyName,
                string targetAssemblySha256)
            {
                ModuleId = moduleId;
                RelativePath = relativePath;
                SourcePath = sourcePath;
                SourceSha256 = sourceSha256;
                Width = width;
                Height = height;
                LoaderAssemblyName = loaderAssemblyName;
                LoaderAssemblySha256 = loaderAssemblySha256;
                TargetAssemblyName = targetAssemblyName;
                TargetAssemblySha256 = targetAssemblySha256;
            }

            internal string ModuleId { get; private set; }
            internal string RelativePath { get; private set; }
            internal string SourcePath { get; private set; }
            internal string SourceSha256 { get; private set; }
            internal float Width { get; private set; }
            internal float Height { get; private set; }
            internal string LoaderAssemblyName { get; private set; }
            internal string LoaderAssemblySha256 { get; private set; }
            internal string TargetAssemblyName { get; private set; }
            internal string TargetAssemblySha256 { get; private set; }

            internal bool Matches(ServerMapTerrainSizeRule other)
            {
                return other != null &&
                       string.Equals(ModuleId, other.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(SourceSha256, other.SourceSha256, StringComparison.Ordinal) &&
                       Width.Equals(other.Width) &&
                       Height.Equals(other.Height) &&
                       string.Equals(
                           LoaderAssemblyName,
                           other.LoaderAssemblyName,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           LoaderAssemblySha256,
                           other.LoaderAssemblySha256,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           TargetAssemblyName,
                           other.TargetAssemblyName,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           TargetAssemblySha256,
                           other.TargetAssemblySha256,
                           StringComparison.Ordinal);
            }
        }

        private sealed class ServerXmlOverlay
        {
            internal ServerXmlOverlay(
                string moduleId,
                string relativePath,
                string sourcePath,
                string overlayPath)
            {
                ModuleId = moduleId;
                RelativePath = relativePath;
                SourcePath = sourcePath;
                OverlayPath = overlayPath;
            }

            internal string ModuleId { get; private set; }
            internal string RelativePath { get; private set; }
            internal string SourcePath { get; private set; }
            internal string OverlayPath { get; private set; }
        }

        private sealed class GameVersionCompatibilityRule
        {
            internal GameVersionCompatibilityRule(
                string serverVersion,
                string clientVersion,
                string serverRuntimeVersion,
                string clientRuntimeVersion)
            {
                ServerVersion = serverVersion;
                ClientVersion = clientVersion;
                ServerRuntimeVersion = serverRuntimeVersion;
                ClientRuntimeVersion = clientRuntimeVersion;
            }

            internal string ServerVersion { get; private set; }
            internal string ClientVersion { get; private set; }
            internal string ServerRuntimeVersion { get; private set; }
            internal string ClientRuntimeVersion { get; private set; }
        }

        internal sealed class ClientAssemblyResolveRule
        {
            internal ClientAssemblyResolveRule(string assemblyName, string path)
            {
                AssemblyName = assemblyName;
                Path = path;
            }

            internal string AssemblyName { get; private set; }
            internal string Path { get; private set; }
        }

        private enum AuthorityScope
        {
            ServerOnly,
            ClientOnly,
            ServerSettingsFallback
        }
    }

#if !BCS_SERVER
    internal sealed class ClientCharacterCreationLifecycleGate
    {
        private readonly object sync = new object();
        private object armedState;
        private bool fallbackClaimed;
        private bool introLoadingOverlayReleaseClaimed;

        internal void Arm(object state)
        {
            if (state == null)
                throw new ArgumentNullException("state");
            lock (sync)
            {
                armedState = state;
                fallbackClaimed = false;
                introLoadingOverlayReleaseClaimed = false;
            }
        }

        internal void Cancel(object state)
        {
            if (state == null)
                return;
            lock (sync)
            {
                if (!ReferenceEquals(armedState, state))
                    return;
                armedState = null;
                fallbackClaimed = false;
                introLoadingOverlayReleaseClaimed = false;
            }
        }

        internal object Peek()
        {
            lock (sync)
                return armedState;
        }

        internal bool TryClaim(object state)
        {
            if (state == null)
                return false;
            lock (sync)
            {
                if (!ReferenceEquals(armedState, state) || fallbackClaimed)
                    return false;
                fallbackClaimed = true;
                return true;
            }
        }

        internal bool TryClaimIntroLoadingOverlayRelease(object state)
        {
            if (state == null)
                return false;
            lock (sync)
            {
                if (!ReferenceEquals(armedState, state) ||
                    introLoadingOverlayReleaseClaimed)
                {
                    return false;
                }
                introLoadingOverlayReleaseClaimed = true;
                return true;
            }
        }
    }

    internal static class ClientCharacterCreationLifecycleCompatibility
    {
        private const string HarmonyOwner =
            "BCS.CoopBridge.client-character-creation-lifecycle";
        private const string ValidateModuleStateTypeName =
            "Coop.Core.Client.States.ValidateModuleState";
        private const string CharacterCreationStartedTypeName =
            "GameInterface.Services.GameDebug.Messages.CharacterCreationStarted";
        private const string CharacterCreationStateTypeName =
            "TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState";
        private const string VideoPlaybackStateTypeName =
            "TaleWorlds.MountAndBlade.VideoPlaybackState";
        private const string LoadingInterfaceTypeName =
            "GameInterface.Services.UI.Interfaces.LoadingInterface";
        private const string LoadingWindowPatchesTypeName =
            "GameInterface.Services.UI.Patches.LoadingWindowPatches";
        private static readonly object Sync = new object();
        private static readonly ClientCharacterCreationLifecycleGate Gate =
            new ClientCharacterCreationLifecycleGate();
        private static object broker;
        private static FieldInfo logicField;
        private static MethodInfo stateGetter;
        private static ConstructorInfo characterCreationStartedConstructor;
        private static MethodInfo publishCharacterCreationStarted;
        private static Type videoPlaybackStateType;
        private static object loadingInterface;
        private static MethodInfo hideLoadingScreen;
        private static MethodInfo forceLoadingWindowGetter;
        private static bool installed;

        internal static void Install(
            string coopModuleRoot,
            string bridgeId,
            object messageBroker)
        {
            if (string.IsNullOrWhiteSpace(coopModuleRoot))
                throw new ArgumentException("Coop module root is required.", "coopModuleRoot");
            if (string.IsNullOrWhiteSpace(bridgeId))
                throw new ArgumentException("Bridge ID is required.", "bridgeId");
            if (messageBroker == null)
                throw new ArgumentNullException("messageBroker");

            lock (Sync)
            {
                if (installed)
                {
                    if (!ReferenceEquals(broker, messageBroker))
                    {
                        throw new InvalidOperationException(
                            "Character-creation lifecycle compatibility was reactivated with a different message broker.");
                    }
                    return;
                }

                var commonAssembly = LoadRequiredCoopAssembly(
                    coopModuleRoot,
                    "Common.dll",
                    "Common");
                var coopCoreAssembly = LoadRequiredCoopAssembly(
                    coopModuleRoot,
                    "Coop.Core.dll",
                    "Coop.Core");
                var gameInterfaceAssembly = LoadRequiredCoopAssembly(
                    coopModuleRoot,
                    "GameInterface.dll",
                    "GameInterface");
                var validateModuleState = coopCoreAssembly.GetType(
                    ValidateModuleStateTypeName,
                    true,
                    false);
                var characterCreationStarted = gameInterfaceAssembly.GetType(
                    CharacterCreationStartedTypeName,
                    true,
                    false);
                var characterCreationState = typeof(MapEvent).Assembly.GetType(
                    CharacterCreationStateTypeName,
                    true,
                    false);
                var resolvedVideoPlaybackStateType = typeof(MBGameManager).Assembly.GetType(
                    VideoPlaybackStateTypeName,
                    true,
                    false);
                var resolvedLoadingInterfaceType = gameInterfaceAssembly.GetType(
                    LoadingInterfaceTypeName,
                    true,
                    false);
                var loadingWindowPatchesType = gameInterfaceAssembly.GetType(
                    LoadingWindowPatchesTypeName,
                    true,
                    false);

                var startCharacterCreation = RequireInstanceVoidMethod(
                    validateModuleState,
                    "StartCharacterCreation",
                    Type.EmptyTypes);
                var payloadType = commonAssembly.GetType(
                    "Common.Messaging.MessagePayload`1",
                    true,
                    false).MakeGenericType(characterCreationStarted);
                var handleCharacterCreationStarted = RequireInstanceVoidMethod(
                    validateModuleState,
                    "Handle_CharacterCreationStarted",
                    new[] { payloadType });
                var dispose = RequireInstanceVoidMethod(
                    validateModuleState,
                    "Dispose",
                    Type.EmptyTypes);
                var onActivate = RequireInstanceVoidMethod(
                    characterCreationState,
                    "OnActivate",
                    Type.EmptyTypes);
                var onVideoStarted = RequireInstanceVoidMethod(
                    resolvedVideoPlaybackStateType,
                    "OnVideoStarted",
                    Type.EmptyTypes);
                var resolvedHideLoadingScreen = RequireInstanceVoidMethod(
                    resolvedLoadingInterfaceType,
                    "HideLoadingScreen",
                    Type.EmptyTypes);
                if (!startCharacterCreation.IsPublic || !startCharacterCreation.IsVirtual ||
                    handleCharacterCreationStarted.IsPublic ||
                    dispose.IsStatic || !dispose.IsPublic || !dispose.IsVirtual ||
                    !onActivate.IsFamily || !onActivate.IsVirtual ||
                    !onVideoStarted.IsPublic || onVideoStarted.IsStatic ||
                    onVideoStarted.IsVirtual ||
                    !resolvedHideLoadingScreen.IsPublic ||
                    resolvedHideLoadingScreen.IsStatic ||
                    !resolvedHideLoadingScreen.IsVirtual ||
                    !resolvedHideLoadingScreen.IsFinal)
                {
                    throw new MissingMemberException(
                        "Released Coop, GameInterface, or Bannerlord character-creation ABI changed.");
                }

                var loadingInterfaceConstructor = resolvedLoadingInterfaceType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (loadingInterfaceConstructor == null)
                {
                    throw new MissingMethodException(
                        resolvedLoadingInterfaceType.FullName,
                        ".ctor()");
                }
                var forceLoadingWindowProperty = loadingWindowPatchesType.GetProperty(
                    "ForceLoadingWindow",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
                var resolvedForceLoadingWindowGetter = forceLoadingWindowProperty == null
                    ? null
                    : forceLoadingWindowProperty.GetGetMethod(false);
                if (resolvedForceLoadingWindowGetter == null ||
                    !resolvedForceLoadingWindowGetter.IsStatic ||
                    resolvedForceLoadingWindowGetter.ReturnType != typeof(bool))
                {
                    throw new MissingMemberException(
                        loadingWindowPatchesType.FullName,
                        "ForceLoadingWindow");
                }

                var clientStateBase = validateModuleState.BaseType;
                if (clientStateBase == null ||
                    !string.Equals(
                        clientStateBase.FullName,
                        "Coop.Core.Client.States.ClientStateBase",
                        StringComparison.Ordinal))
                {
                    throw new TypeLoadException(
                        "Released Coop ValidateModuleState has an unexpected base type.");
                }
                var resolvedLogicField = clientStateBase.GetField(
                    "Logic",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (resolvedLogicField == null ||
                    !string.Equals(
                        resolvedLogicField.FieldType.FullName,
                        "Coop.Core.Client.IClientLogic",
                        StringComparison.Ordinal))
                {
                    throw new MissingFieldException(clientStateBase.FullName, "Logic");
                }
                var resolvedStateProperty = resolvedLogicField.FieldType.GetProperty(
                    "State",
                    BindingFlags.Instance | BindingFlags.Public);
                var resolvedStateGetter = resolvedStateProperty == null
                    ? null
                    : resolvedStateProperty.GetGetMethod(false);
                if (resolvedStateGetter == null || resolvedStateGetter.IsStatic ||
                    !string.Equals(
                        resolvedStateGetter.ReturnType.FullName,
                        "Coop.Core.Client.States.IClientState",
                        StringComparison.Ordinal))
                {
                    throw new MissingMemberException(
                        resolvedLogicField.FieldType.FullName,
                        "State");
                }

                var messageConstructor = characterCreationStarted.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (messageConstructor == null)
                {
                    throw new MissingMethodException(
                        characterCreationStarted.FullName,
                        ".ctor()");
                }
                var messageBrokerInterface = commonAssembly.GetType(
                    "Common.Messaging.IMessageBroker",
                    true,
                    false);
                if (!messageBrokerInterface.IsInterface ||
                    !messageBrokerInterface.IsInstanceOfType(messageBroker))
                {
                    throw new TypeLoadException(
                        "Delayed Coop handler supplied an unexpected message-broker implementation.");
                }
                var publishDefinition = messageBrokerInterface.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public)
                    .SingleOrDefault(method =>
                        string.Equals(method.Name, "Publish", StringComparison.Ordinal) &&
                        method.IsGenericMethodDefinition &&
                        method.GetGenericArguments().Length == 1 &&
                        method.GetParameters().Length == 2 &&
                        method.GetParameters()[0].ParameterType == typeof(object) &&
                        method.GetParameters()[1].ParameterType.IsGenericParameter &&
                        method.ReturnType == typeof(void));
                if (publishDefinition == null)
                {
                    throw new MissingMethodException(
                        messageBrokerInterface.FullName,
                        "Publish<T>(object,T)");
                }

                var startPrefix = RequirePatchMethod(nameof(BeforeStartCharacterCreation));
                var startedPrefix = RequirePatchMethod(nameof(BeforeCharacterCreationStarted));
                var disposePrefix = RequirePatchMethod(nameof(BeforeValidateModuleStateDisposed));
                var activatePostfix = RequirePatchMethod(nameof(AfterCharacterCreationActivated));
                var videoStartedPostfix = RequirePatchMethod(nameof(AfterVideoPlaybackStarted));
                EnsureOwnerAbsent(startCharacterCreation);
                EnsureOwnerAbsent(handleCharacterCreationStarted);
                EnsureOwnerAbsent(dispose);
                EnsureOwnerAbsent(onActivate);
                EnsureOwnerAbsent(onVideoStarted);

                broker = messageBroker;
                logicField = resolvedLogicField;
                stateGetter = resolvedStateGetter;
                characterCreationStartedConstructor = messageConstructor;
                publishCharacterCreationStarted = publishDefinition.MakeGenericMethod(
                    characterCreationStarted);
                videoPlaybackStateType = resolvedVideoPlaybackStateType;
                loadingInterface = loadingInterfaceConstructor.Invoke(null);
                hideLoadingScreen = resolvedHideLoadingScreen;
                forceLoadingWindowGetter = resolvedForceLoadingWindowGetter;
                var harmony = new Harmony(HarmonyOwner);
                var patched = new List<MethodInfo>();
                try
                {
                    harmony.Patch(
                        startCharacterCreation,
                        prefix: new HarmonyMethod(startPrefix) { priority = Priority.First });
                    patched.Add(startCharacterCreation);
                    harmony.Patch(
                        handleCharacterCreationStarted,
                        prefix: new HarmonyMethod(startedPrefix) { priority = Priority.First });
                    patched.Add(handleCharacterCreationStarted);
                    harmony.Patch(
                        dispose,
                        prefix: new HarmonyMethod(disposePrefix) { priority = Priority.First });
                    patched.Add(dispose);
                    harmony.Patch(
                        onActivate,
                        postfix: new HarmonyMethod(activatePostfix) { priority = Priority.Last });
                    patched.Add(onActivate);
                    harmony.Patch(
                        onVideoStarted,
                        postfix: new HarmonyMethod(videoStartedPostfix) { priority = Priority.Last });
                    patched.Add(onVideoStarted);
                    foreach (var target in patched)
                        EnsureOwnerInstalledOnce(target);
                    installed = true;
                }
                catch
                {
                    foreach (var target in patched)
                        harmony.Unpatch(target, HarmonyPatchType.All, HarmonyOwner);
                    broker = null;
                    logicField = null;
                    stateGetter = null;
                    characterCreationStartedConstructor = null;
                    publishCharacterCreationStarted = null;
                    videoPlaybackStateType = null;
                    loadingInterface = null;
                    hideLoadingScreen = null;
                    forceLoadingWindowGetter = null;
                    throw;
                }

                Console.WriteLine(
                    "[BCS Coop Bridge] Installed fail-closed client character-creation lifecycle compatibility.");
            }
        }

        private static Assembly LoadRequiredCoopAssembly(
            string coopModuleRoot,
            string fileName,
            string expectedName)
        {
            var path = BridgeRuntime.FindAssemblyForRegistration(coopModuleRoot, fileName);
            if (path == null)
            {
                throw new FileNotFoundException(
                    "Released Coop " + fileName + " is missing for character-creation compatibility.",
                    Path.Combine(coopModuleRoot, "bin", "Win64_Shipping_Client", fileName));
            }
            return ClientCoopHandlerRegistration.LoadExactCoopAssembly(path, expectedName);
        }

        private static MethodInfo RequireInstanceVoidMethod(
            Type declaringType,
            string name,
            Type[] parameterTypes)
        {
            var matches = declaringType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, name, StringComparison.Ordinal) &&
                    method.ReturnType == typeof(void) &&
                    ParametersMatch(method.GetParameters(), parameterTypes))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new MissingMethodException(
                    declaringType.FullName,
                    name + "(" + string.Join(",", parameterTypes.Select(type => type.FullName)) + ")");
            }
            return matches[0];
        }

        private static bool ParametersMatch(ParameterInfo[] actual, Type[] expected)
        {
            if (actual.Length != expected.Length)
                return false;
            for (var index = 0; index < actual.Length; index++)
            {
                if (actual[index].ParameterType != expected[index])
                    return false;
            }
            return true;
        }

        private static MethodInfo RequirePatchMethod(string name)
        {
            var method = typeof(ClientCharacterCreationLifecycleCompatibility).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(
                    typeof(ClientCharacterCreationLifecycleCompatibility).FullName,
                    name);
            }
            return method;
        }

        private static void EnsureOwnerAbsent(MethodInfo target)
        {
            var patches = Harmony.GetPatchInfo(target);
            if (patches != null && patches.Owners.Contains(HarmonyOwner))
            {
                throw new InvalidOperationException(
                    "Character-creation lifecycle Harmony owner is already present on " +
                    target.DeclaringType.FullName + "." + target.Name + ".");
            }
        }

        private static void EnsureOwnerInstalledOnce(MethodInfo target)
        {
            var patches = Harmony.GetPatchInfo(target);
            var count = patches == null
                ? 0
                : patches.Prefixes.Concat(patches.Postfixes)
                    .Count(patch => string.Equals(
                        patch.owner,
                        HarmonyOwner,
                        StringComparison.Ordinal));
            if (count != 1)
            {
                throw new InvalidOperationException(
                    "Character-creation lifecycle Harmony hook was not installed exactly once on " +
                    target.DeclaringType.FullName + "." + target.Name + ".");
            }
        }

        private static void BeforeStartCharacterCreation(object __instance)
        {
            if (!ReferenceEquals(GetCurrentClientState(__instance), __instance))
            {
                throw new InvalidOperationException(
                    "Coop invoked ValidateModuleState.StartCharacterCreation after leaving that state.");
            }
            Gate.Arm(__instance);
        }

        private static void BeforeCharacterCreationStarted(object __instance)
        {
            Gate.Cancel(__instance);
        }

        private static void BeforeValidateModuleStateDisposed(object __instance)
        {
            Gate.Cancel(__instance);
        }

        private static void AfterCharacterCreationActivated(object __instance)
        {
            var armedState = Gate.Peek();
            if (armedState == null)
                return;
            if (!ReferenceEquals(GetCurrentClientState(armedState), armedState))
            {
                Gate.Cancel(armedState);
                return;
            }
            var nativeState = __instance as GameState;
            if (nativeState == null || !nativeState.IsActive ||
                nativeState.GameStateManager == null ||
                !ReferenceEquals(nativeState.GameStateManager.ActiveState, nativeState))
            {
                Gate.Cancel(armedState);
                throw new InvalidOperationException(
                    "CharacterCreationState.OnActivate completed without becoming the active native game state.");
            }
            if (!Gate.TryClaim(armedState))
                return;

            try
            {
                var message = characterCreationStartedConstructor.Invoke(null);
                publishCharacterCreationStarted.Invoke(
                    broker,
                    new[] { __instance, message });
                if (ReferenceEquals(GetCurrentClientState(armedState), armedState))
                {
                    throw new InvalidOperationException(
                        "CharacterCreationStarted was published but Coop remained in ValidateModuleState.");
                }
                Console.WriteLine(
                    "[BCS Coop Bridge] Repaired missed CharacterCreationStarted lifecycle notification.");
            }
            catch (TargetInvocationException exception)
            {
                Gate.Cancel(armedState);
                throw new InvalidOperationException(
                    "CharacterCreationStarted lifecycle repair failed.",
                    exception.InnerException ?? exception);
            }
            catch
            {
                Gate.Cancel(armedState);
                throw;
            }
        }

        private static void AfterVideoPlaybackStarted(object __instance)
        {
            var armedState = Gate.Peek();
            if (armedState == null)
                return;
            if (!ReferenceEquals(GetCurrentClientState(armedState), armedState))
            {
                Gate.Cancel(armedState);
                return;
            }
            if (__instance == null || __instance.GetType() != videoPlaybackStateType)
            {
                throw new InvalidOperationException(
                    "The Coop character-creation intro used an unexpected video playback state.");
            }
            var nativeState = __instance as GameState;
            if (nativeState == null || !nativeState.IsActive ||
                nativeState.GameStateManager == null ||
                !ReferenceEquals(nativeState.GameStateManager.ActiveState, nativeState))
            {
                throw new InvalidOperationException(
                    "The Coop character-creation video started without becoming the active native game state.");
            }
            try
            {
                if (!(bool)forceLoadingWindowGetter.Invoke(null, null))
                    return;
                if (!Gate.TryClaimIntroLoadingOverlayRelease(armedState))
                    return;
                hideLoadingScreen.Invoke(loadingInterface, null);
                if ((bool)forceLoadingWindowGetter.Invoke(null, null))
                {
                    throw new InvalidOperationException(
                        "GameInterface kept the forced loading window active over the character-creation intro.");
                }
                Console.WriteLine(
                    "[BCS Coop Bridge] Released the validated Coop loading overlay for the character-creation intro.");
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "The Coop character-creation intro loading overlay could not be released.",
                    exception.InnerException ?? exception);
            }
        }

        private static object GetCurrentClientState(object validateModuleState)
        {
            var logic = logicField.GetValue(validateModuleState);
            if (logic == null)
                throw new InvalidOperationException("ValidateModuleState has no client logic instance.");
            try
            {
                return stateGetter.Invoke(logic, null);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "Coop client state could not be read.",
                    exception.InnerException ?? exception);
            }
        }
    }

    public static class ClientCoopHandlerRegistration
    {
        private const string HandlerTypeName =
            "GameInterface.BCSCoopBridge.Registration.DiscoveredBridgeHandler";
        private static readonly object Sync = new object();
        private static Type registeredHandlerType;
        private static string registeredCoopModuleRoot;
        private static string registeredBridgeId;

        internal static void Install(string coopModuleRoot, string bridgeId)
        {
            lock (Sync)
            {
                if (registeredHandlerType != null)
                    return;

                var commonPath = BridgeRuntime.FindAssemblyForRegistration(
                    coopModuleRoot,
                    "Common.dll");
                if (commonPath == null)
                    throw new FileNotFoundException(
                        "Released Coop Common.dll is missing for delayed handler registration.",
                        Path.Combine(coopModuleRoot, "bin", "Win64_Shipping_Client", "Common.dll"));

                var commonAssembly = LoadExactCoopAssembly(commonPath, "Common");
                var handlerInterface = commonAssembly.GetType(
                    "Common.Messaging.IHandler",
                    true,
                    false);
                var messageBrokerInterface = commonAssembly.GetType(
                    "Common.Messaging.IMessageBroker",
                    true,
                    false);
                if (!handlerInterface.IsInterface || !messageBrokerInterface.IsInterface)
                    throw new TypeLoadException("Released Coop messaging contracts are not interfaces.");

                var assemblyName = new AssemblyName(
                    "BCS.CoopBridge.ClientRegistration." +
                    bridgeId.Substring(bridgeId.LastIndexOf('.') + 1));
                var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                    assemblyName,
                    AssemblyBuilderAccess.Run);
                var module = assembly.DefineDynamicModule(assemblyName.Name);
                var type = module.DefineType(
                    HandlerTypeName,
                    TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                    typeof(object),
                    new[] { handlerInterface });

                var constructor = type.DefineConstructor(
                    MethodAttributes.Public,
                    CallingConventions.Standard,
                    new[] { messageBrokerInterface });
                var constructorIl = constructor.GetILGenerator();
                constructorIl.Emit(OpCodes.Ldarg_0);
                constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
                constructorIl.Emit(OpCodes.Ldarg_1);
                constructorIl.Emit(
                    OpCodes.Call,
                    typeof(ClientCoopHandlerRegistration).GetMethod(
                        nameof(OnHandlerActivated),
                        BindingFlags.Static | BindingFlags.Public));
                constructorIl.Emit(OpCodes.Ret);

                var dispose = type.DefineMethod(
                    "Dispose",
                    MethodAttributes.Public | MethodAttributes.Virtual |
                    MethodAttributes.Final | MethodAttributes.HideBySig |
                    MethodAttributes.NewSlot,
                    typeof(void),
                    Type.EmptyTypes);
                dispose.GetILGenerator().Emit(OpCodes.Ret);
                var disposeContract = handlerInterface.GetMethod(
                    "Dispose",
                    BindingFlags.Instance | BindingFlags.Public)
                    ?? typeof(IDisposable).GetMethod("Dispose");
                type.DefineMethodOverride(dispose, disposeContract);

                registeredCoopModuleRoot = Path.GetFullPath(coopModuleRoot);
                registeredBridgeId = bridgeId;
                registeredHandlerType = type.CreateType();
                if (!handlerInterface.IsAssignableFrom(registeredHandlerType))
                    throw new TypeLoadException("Delayed Coop handler does not implement IHandler.");
                BridgeRuntime.RecordStartupProgress("Client Coop handler type registered");
                Console.WriteLine(
                    "[BCS Coop Bridge] Delayed Coop handler registered: " +
                    registeredHandlerType.FullName + ".");
            }
        }

        public static void OnHandlerActivated(object messageBroker)
        {
            if (messageBroker == null)
                throw new ArgumentNullException("messageBroker");
            string coopModuleRoot;
            string bridgeId;
            lock (Sync)
            {
                coopModuleRoot = registeredCoopModuleRoot;
                bridgeId = registeredBridgeId;
            }
            if (string.IsNullOrWhiteSpace(coopModuleRoot) ||
                string.IsNullOrWhiteSpace(bridgeId))
            {
                throw new InvalidOperationException(
                    "Delayed Coop compatibility was activated before registration completed.");
            }
            if (BridgeRuntime.IsRuntimeFeatureEnabled(
                    BridgeRuntime.ClientRegistryLifecycleCompatibilityFeature))
                CoopRegistryLifecycleCompatibility.Install(false);
            if (BridgeRuntime.IsRuntimeFeatureEnabled(
                    BridgeRuntime.ClientMapEventPositionAuthorityFeature))
                ClientMapEventPositionAuthority.Install(coopModuleRoot, bridgeId);
            if (BridgeRuntime.IsRuntimeFeatureEnabled(
                    BridgeRuntime.ClientTroopUpgradeLoadRepairFeature))
                ClientTroopUpgradeTrackerLoadRepair.Install(coopModuleRoot, bridgeId);
            if (BridgeRuntime.IsRuntimeFeatureEnabled(
                    BridgeRuntime.ClientSetDisorganizedDiagnosticFeature))
                ClientSetDisorganizedDiagnostic.Install(coopModuleRoot, bridgeId);
            if (BridgeRuntime.IsRuntimeFeatureEnabled(
                    BridgeRuntime.ClientTroopRosterSequenceDiagnosticFeature))
                ClientTroopRosterSequenceDiagnostic.Install(coopModuleRoot, bridgeId);
            if (BridgeRuntime.IsRuntimeFeatureEnabled(
                    BridgeRuntime.ClientCharacterCreationLifecycleCompatibilityFeature))
            {
                ClientCharacterCreationLifecycleCompatibility.Install(
                    coopModuleRoot,
                    bridgeId,
                    messageBroker);
            }
            BridgeRuntime.MarkCoopContainerReady();
        }

        internal static Assembly LoadExactCoopAssembly(string path, string expectedName)
        {
            var fullPath = Path.GetFullPath(path);
            var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                string.Equals(
                    candidate.GetName().Name,
                    expectedName,
                    StringComparison.OrdinalIgnoreCase));
            var assembly = existing ?? Assembly.LoadFrom(fullPath);
            if (!string.Equals(
                    assembly.GetName().Name,
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException(
                    "Released Coop assembly has an unexpected identity.",
                    fullPath);
            }
            if (!string.Equals(
                    Path.GetFullPath(assembly.Location),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException(
                    "A different Common.dll was already loaded before Coop registration.",
                    assembly.Location);
            }
            return assembly;
        }
    }

    internal static class ClientAssemblyResolver
    {
        private static readonly object Sync = new object();
        private static readonly IDictionary<string, string> Paths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool installed;

        internal static void Configure(
            IEnumerable<BridgeRuntime.ClientAssemblyResolveRule> rules)
        {
            lock (Sync)
            {
                foreach (var rule in rules)
                {
                    string existing;
                    if (Paths.TryGetValue(rule.AssemblyName, out existing) &&
                        !string.Equals(existing, rule.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Conflicting client assembly resolver for " + rule.AssemblyName + ".");
                    }
                    Paths[rule.AssemblyName] = rule.Path;
                }
                if (installed)
                    return;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                installed = true;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string requestedName;
            try
            {
                requestedName = new AssemblyName(args.Name).Name;
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(requestedName))
                return null;

            string path;
            lock (Sync)
            {
                if (!Paths.TryGetValue(requestedName, out path))
                    return null;
            }
            var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    requestedName,
                    StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            var loaded = Assembly.LoadFrom(path);
            if (!string.Equals(
                    loaded.GetName().Name,
                    requestedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException(
                    "Required client assembly resolved to an unexpected identity.",
                    path);
            }
            Console.WriteLine(
                "[BCS Coop Bridge] Resolved client assembly " +
                requestedName + " from " + path + ".");
            return loaded;
        }
    }
#endif

#if BCS_SERVER
    internal static class ServerMapTerrainSizePrefix
    {
        private static TaleWorlds.Library.Vec2 terrainSize;
        private static bool configured;

        internal static void Configure(float width, float height)
        {
            if (configured)
                throw new InvalidOperationException("Server map terrain size was already configured.");
            terrainSize = new TaleWorlds.Library.Vec2(width, height);
            configured = true;
        }

        public static bool Override(ref TaleWorlds.Library.Vec2 __result)
        {
            if (!configured)
                throw new InvalidOperationException("Server map terrain size was not configured.");
            __result = terrainSize;
            return false;
        }
    }

#endif

    internal static class AuthorityPrefix
    {
        public static bool ServerOnly()
        {
            return BridgeRuntime.IsServerProcess();
        }

        public static bool ClientOnly()
        {
            return !BridgeRuntime.IsServerProcess();
        }

        public static void ServerSettingsFallback(object __instance)
        {
            if (!BridgeRuntime.IsServerProcess() || __instance == null)
                return;

            var instanceType = __instance.GetType();
            foreach (var field in instanceType.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.GetValue(__instance) != null ||
                    (field.Name.IndexOf("settings", StringComparison.OrdinalIgnoreCase) < 0 &&
                     field.FieldType.Name.IndexOf("settings", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                Type[] assemblyTypes;
                try
                {
                    assemblyTypes = instanceType.Assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    assemblyTypes = exception.Types.Where(type => type != null).ToArray();
                }

                var candidates = assemblyTypes.Where(type =>
                        !type.IsAbstract &&
                        !type.IsInterface &&
                        !type.ContainsGenericParameters &&
                        field.FieldType.IsAssignableFrom(type) &&
                        type.GetConstructor(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null,
                            Type.EmptyTypes,
                            null) != null)
                    .ToArray();
                var exact = candidates.Where(type =>
                        string.Equals(type.Name, "Settings", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var selected = exact.Length == 1
                    ? exact[0]
                    : candidates.Length == 1
                        ? candidates[0]
                        : null;
                if (selected == null)
                {
                    throw new InvalidOperationException(
                        "Could not resolve one server settings fallback for " +
                        instanceType.FullName + "." + field.Name + ".");
                }

                field.SetValue(__instance, Activator.CreateInstance(selected, true));
                Console.WriteLine(
                    "[BCS Coop Bridge] Server settings fallback initialized: " +
                    instanceType.FullName + "." + field.Name + " -> " + selected.FullName);
            }
        }
    }

#if BCS_SERVER
    internal static class ServerCurrentVersionCompatibility
    {
        private static ApplicationVersion configuredVersion;
        private static int repairLogged;

        internal static void Install(string serverRuntimeVersion, string bridgeId)
        {
            Configure(serverRuntimeVersion);

            var getters = typeof(MBSaveLoad)
                .GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, "get_CurrentVersion", StringComparison.Ordinal) &&
                    method.ReturnType == typeof(ApplicationVersion) &&
                    method.GetParameters().Length == 0)
                .ToArray();
            if (getters.Length != 1)
            {
                throw new MissingMethodException(
                    typeof(MBSaveLoad).FullName,
                    "one static ApplicationVersion get_CurrentVersion()");
            }

            var postfix = typeof(ServerCurrentVersionCompatibility).GetMethod(
                "Repair",
                BindingFlags.Static | BindingFlags.Public);
            if (postfix == null)
            {
                throw new MissingMethodException(
                    typeof(ServerCurrentVersionCompatibility).FullName,
                    "Repair");
            }

            new Harmony(bridgeId + ".mbsaveload-current-version")
                .Patch(
                    getters[0],
                    postfix: new HarmonyMethod(postfix));
            Console.WriteLine(
                "[BCS Coop Bridge] Installed empty MBSaveLoad.CurrentVersion repair for " +
                serverRuntimeVersion + ".");
        }

        internal static void Configure(string serverRuntimeVersion)
        {
            if (string.IsNullOrWhiteSpace(serverRuntimeVersion) ||
                serverRuntimeVersion.Length < 2)
            {
                throw new InvalidDataException("Server runtime game version is empty.");
            }

            ApplicationVersionType versionType;
            switch (serverRuntimeVersion[0])
            {
                case 'a':
                    versionType = ApplicationVersionType.Alpha;
                    break;
                case 'b':
                    versionType = ApplicationVersionType.Beta;
                    break;
                case 'e':
                    versionType = ApplicationVersionType.EarlyAccess;
                    break;
                case 'v':
                    versionType = ApplicationVersionType.Release;
                    break;
                case 'd':
                    versionType = ApplicationVersionType.Development;
                    break;
                default:
                    throw new InvalidDataException(
                        "Server runtime game version has an invalid type prefix.");
            }

            var components = serverRuntimeVersion.Substring(1).Split('.');
            if (components.Length != 4)
            {
                throw new InvalidDataException(
                    "Server runtime game version must contain four numeric components.");
            }
            var values = new int[4];
            for (var index = 0; index < components.Length; index++)
            {
                if (!int.TryParse(
                        components[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out values[index]) ||
                    values[index] < 0 ||
                    (index == 0 && values[index] == 0) ||
                    (index == 3 && values[index] == 0))
                {
                    throw new InvalidDataException(
                        "Server runtime game version has an invalid numeric component.");
                }
            }

            configuredVersion = new ApplicationVersion(
                versionType,
                values[0],
                values[1],
                values[2],
                values[3]);
            if (!string.Equals(
                    configuredVersion.ToString(),
                    serverRuntimeVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Server runtime game version did not round-trip through Bannerlord ApplicationVersion.");
            }
        }

        public static void Repair(ref ApplicationVersion __result)
        {
            if (IsUsable(__result))
                return;

            __result = configuredVersion;
            if (System.Threading.Interlocked.Exchange(ref repairLogged, 1) == 0)
            {
                Console.WriteLine(
                    "[BCS Coop Bridge] Repaired empty MBSaveLoad.CurrentVersion to " +
                    configuredVersion + ".");
            }
        }

        private static bool IsUsable(ApplicationVersion version)
        {
            return version.ApplicationVersionType != ApplicationVersionType.Invalid &&
                   version.Major > 0 &&
                   version.Minor >= 0 &&
                   version.Revision >= 0 &&
                   version.ChangeSet > 0;
        }
    }
#endif

    internal static class GameVersionCompatibilityPrefix
    {
        private static string expectedServerVersion;
        private static string expectedClientVersion;

        internal static void Configure(string serverVersion, string clientVersion)
        {
            if (string.IsNullOrWhiteSpace(serverVersion) ||
                string.IsNullOrWhiteSpace(clientVersion))
            {
                throw new InvalidDataException(
                    "Game-version compatibility has an empty version.");
            }
            expectedServerVersion = serverVersion;
            expectedClientVersion = clientVersion;
        }

        public static bool Validate(
            object serverModules,
            object clientModules,
            ref string error,
            ref bool __result)
        {
            var serverVersion = ReadOfficialGameVersion(serverModules);
            var clientVersion = ReadOfficialGameVersion(clientModules);
            if (!MatchesVersion(serverVersion, expectedServerVersion) ||
                !MatchesVersion(clientVersion, expectedClientVersion))
            {
                return true;
            }

            error = null;
            __result = true;
            Console.WriteLine(
                "[BCS Coop Bridge] Accepted supported game-version pair " +
                serverVersion + " -> " + clientVersion + ".");
            return false;
        }

        internal static bool MatchesVersion(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
                return false;
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ||
                   actual.StartsWith(expected + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadOfficialGameVersion(object modules)
        {
            var enumerable = modules as IEnumerable;
            if (enumerable == null)
                return null;
            foreach (var module in enumerable)
            {
                if (module == null)
                    continue;
                var type = module.GetType();
                var officialProperty = type.GetProperty(
                    "IsOfficial",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var versionProperty = type.GetProperty(
                    "Version",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (officialProperty == null || versionProperty == null ||
                    !Convert.ToBoolean(officialProperty.GetValue(module, null)))
                {
                    continue;
                }
                var version = versionProperty.GetValue(module, null);
                return version == null ? null : version.ToString();
            }
            return null;
        }
    }

#if BCS_SERVER
    internal static class SupportedClientOnlyModulePrefix
    {
        private static readonly IDictionary<string, string> AllowedVersions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Bannerlord.Harmony", "v2.4.2.248" }
            };

        public static void Filter(
            IEnumerable<ModuleInfo> serverModules,
            ref IEnumerable<ModuleInfo> clientModules)
        {
            if (serverModules == null || clientModules == null)
                return;

            var serverIds = new HashSet<string>(
                serverModules
                    .Select(module => module.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            clientModules = clientModules.Where(module =>
            {
                string expectedVersion;
                if (string.IsNullOrWhiteSpace(module.Id) ||
                    serverIds.Contains(module.Id) ||
                    !AllowedVersions.TryGetValue(module.Id, out expectedVersion))
                {
                    return true;
                }

                var actualVersion = module.Version.ToString();
                if (!string.Equals(
                        actualVersion,
                        expectedVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                Console.WriteLine(
                    "[BCS Coop Bridge] Accepted supported client-only module " +
                    module.Id + " " + actualVersion + ".");
                return false;
            }).ToArray();
        }
    }
#endif

    internal static class ServerXmlOverlayPrefix
    {
        private static readonly object BannerPaletteSync = new object();
        private static bool bannerPaletteLoaded;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Overlays =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void Configure(string sourcePath, string overlayPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(overlayPath))
                throw new InvalidDataException("Server XML overlay has an empty source or destination.");
            var canonicalSource = Path.GetFullPath(sourcePath);
            var canonicalOverlay = Path.GetFullPath(overlayPath);
            lock (Sync)
            {
                if (Overlays.ContainsKey(canonicalSource))
                    throw new InvalidDataException("Duplicate server XML overlay source: " + canonicalSource);
                Overlays.Add(canonicalSource, canonicalOverlay);
            }
        }

        public static void Redirect(ref string __0)
        {
            if (string.IsNullOrWhiteSpace(__0))
                return;
            string overlay;
            lock (Sync)
            {
                if (!Overlays.TryGetValue(Path.GetFullPath(__0), out overlay))
                    return;
            }
            Console.WriteLine("[BCS Coop Bridge] XML overlay: " + __0 + " -> " + overlay);
            __0 = overlay;
        }

        public static void AuditMerged(string __0, XmlDocument __result)
        {
            if (!string.Equals(__0, "Factions", StringComparison.Ordinal) ||
                __result == null || __result.DocumentElement == null)
            {
                return;
            }
            EnsureBannerPalette();
            var ignoredWhitespace = __result.DocumentElement.ChildNodes
                .Cast<XmlNode>()
                .Where(node =>
                    (node.NodeType == XmlNodeType.Whitespace ||
                     node.NodeType == XmlNodeType.SignificantWhitespace ||
                     node.NodeType == XmlNodeType.Text) &&
                    string.IsNullOrWhiteSpace(node.Value))
                .ToArray();
            foreach (var node in ignoredWhitespace)
                __result.DocumentElement.RemoveChild(node);
            var unsupportedNodes = __result.DocumentElement.ChildNodes
                .Cast<XmlNode>()
                .Where(node =>
                    node.NodeType != XmlNodeType.Element &&
                    node.NodeType != XmlNodeType.Comment)
                .ToArray();
            if (unsupportedNodes.Length > 0)
            {
                throw new InvalidDataException(
                    "Merged Factions XML contains unsupported node type " +
                    unsupportedNodes[0].NodeType + ".");
            }
            var children = __result.DocumentElement.ChildNodes
                .OfType<XmlElement>()
                .ToArray();
            var sample = string.Join(",", children.Take(8)
                .Select(element => element.GetAttribute("id")));
            Console.WriteLine(
                "[BCS Coop Bridge] Merged Factions XML: count=" + children.Length +
                ", removedWhitespace=" + ignoredWhitespace.Length +
                ", sample=" + sample + ".");
        }

        private static void EnsureBannerPalette()
        {
            lock (BannerPaletteSync)
            {
                if (bannerPaletteLoaded)
                    return;
                BannerManager.Initialize();
                BannerManager.Instance.LoadBannerIcons();
                bannerPaletteLoaded = true;
            }
            const string message =
                "Initialized Bannerlord banner palette before merged faction deserialization.";
            Console.WriteLine("[BCS Coop Bridge] " + message);
            CachePathRedirectPrefix.WriteTrace(message);
        }

    }

    internal static class CachePathRedirectPrefix
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Redirects =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal static void Configure(string fileName, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(sourcePath))
                throw new InvalidDataException("Server file redirect has an empty file name or source path.");
            lock (Sync)
            {
                if (Redirects.ContainsKey(fileName))
                    throw new InvalidDataException("Duplicate server file redirect target: " + fileName);
                Redirects.Add(fileName, sourcePath);
            }
        }

        public static void Redirect(object __instance, ref string path)
        {
            WriteTrace("Navigation cache requested: " + path);
            if (string.IsNullOrWhiteSpace(path))
                return;
            string redirect;
            lock (Sync)
            {
                if (!Redirects.TryGetValue(Path.GetFileName(path), out redirect))
                    return;
            }
            if (string.Equals(path, redirect, StringComparison.OrdinalIgnoreCase))
                return;
            Console.WriteLine("[BCS Coop Bridge] Server file redirect: " + path + " -> " + redirect);
            WriteTrace("Navigation cache redirected: " + path + " -> " + redirect);
            NavigationCacheSanitizer.ValidateCompatibleSave(
                redirect,
                __instance,
                WriteTrace);
            path = redirect;
        }

        internal static void WriteTrace(string message)
        {
            var tracePath = Environment.GetEnvironmentVariable("BCSTOOL_BRIDGE_TRACE_LOG");
            if (string.IsNullOrWhiteSpace(tracePath))
                return;
            lock (Sync)
            {
                File.AppendAllText(
                    tracePath,
                    DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
        }
    }
}

#if BCS_SERVER
namespace GameInterface.BCSCoopBridge.Registration
{
    public sealed class DiscoveredBridgeHandler : IHandler
    {
        public DiscoveredBridgeHandler(IMessageBroker messageBroker)
        {
            if (messageBroker == null)
                throw new ArgumentNullException("messageBroker");
            if (BCS.CoopBridge.BridgeRuntime.IsRuntimeFeatureEnabled(
                    BCS.CoopBridge.BridgeRuntime.ServerRegistryLifecycleCompatibilityFeature))
                BCS.CoopBridge.CoopRegistryLifecycleCompatibility.Install(true);
            if (BCS.CoopBridge.BridgeRuntime.IsRuntimeFeatureEnabled(
                    BCS.CoopBridge.BridgeRuntime.ServerPopulationControlFeature))
                BCS.CoopBridge.ServerPopulationControl.Install();
            BCS.CoopBridge.BridgeRuntime.MarkCoopContainerReady();
            if (BCS.CoopBridge.BridgeRuntime.IsRuntimeFeatureEnabled(
                    BCS.CoopBridge.BridgeRuntime.ServerFailedIdCompatibilityFeature))
                BCS.CoopBridge.ServerFailedIdCompatibility.Install();
        }

        public void Dispose()
        {
        }
    }
}
#endif
