#if BCS_SERVER
using Common;
using Common.Messaging;
using GameInterface.Services.Modules;
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
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

[assembly: AssemblyVersion("0.6.60.0")]
[assembly: AssemblyFileVersion("0.6.60.0")]
[assembly: AssemblyInformationalVersion("0.6.60")]

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

    internal static class BridgeRuntime
    {
        private const string BridgeIdPrefix = "BCS.CoopBridge.";
        private const string ConfigurationName = "bcs-coop-bridge.config";
        private const string BridgeAssemblyFileName = "BCS.CoopBridge.dll";
        private static readonly object Sync = new object();
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
                if (lines.Length == 0 || !string.Equals(lines[0], "BCS-COOP-BRIDGE|1", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported BCS Coop bridge configuration schema.");

                var authorityRules = new List<AuthorityRule>();
                var serverFileRedirects = new List<ServerFileRedirect>();
                var serverXmlOverlays = new List<ServerXmlOverlay>();
                var clientAssemblyResolves = new List<ClientAssemblyResolveRule>();
                ServerMapTerrainSizeRule serverMapTerrainSize = null;
                GameVersionCompatibilityRule gameVersionCompatibility = null;
                var ignoredContent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var fields = line.Split('|');
                    if (fields.Length == 5 &&
                        string.Equals(fields[0], "GAME_VERSION_COMPAT", StringComparison.Ordinal))
                    {
                        if (gameVersionCompatibility != null)
                            throw new InvalidDataException("Duplicate game-version compatibility record.");
                        gameVersionCompatibility = new GameVersionCompatibilityRule(
                            Decode(fields[1]),
                            Decode(fields[2]),
                            fields[3],
                            fields[4]);
                        if (string.IsNullOrWhiteSpace(gameVersionCompatibility.ServerVersion) ||
                            string.IsNullOrWhiteSpace(gameVersionCompatibility.ClientVersion) ||
                            string.Equals(
                                gameVersionCompatibility.ServerVersion,
                                gameVersionCompatibility.ClientVersion,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Game-version compatibility requires two different Bannerlord versions.");
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

                ApplyClientAssemblyResolves(clientAssemblyResolves);
                ApplyGameVersionCompatibility(installed, gameVersionCompatibility, actualId);
#if BCS_SERVER
                ApplyServerMapTerrainSize(serverMapTerrainSize, actualId);
#endif
                ApplyServerFileRedirects(serverFileRedirects, actualId);
                ApplyServerXmlOverlays(serverXmlOverlays, actualId);
                ApplyAuthorityRules(installed, authorityRules, actualId);
#if !BCS_SERVER
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
                    "[BCS Coop Bridge] Server game runtime version validated for " +
                    rule.ServerVersion + ".");
            }

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
                rule.ServerVersion,
                rule.ClientVersion);
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
                rule.ServerVersion + " -> " + rule.ClientVersion + ".");
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
                string serverRuntimeSha256,
                string clientRuntimeSha256)
            {
                ServerVersion = serverVersion;
                ClientVersion = clientVersion;
                ServerRuntimeSha256 = serverRuntimeSha256;
                ClientRuntimeSha256 = clientRuntimeSha256;
            }

            internal string ServerVersion { get; private set; }
            internal string ClientVersion { get; private set; }
            internal string ServerRuntimeSha256 { get; private set; }
            internal string ClientRuntimeSha256 { get; private set; }
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
            ClientMapEventPositionAuthority.Install(coopModuleRoot, bridgeId);
            ClientTroopUpgradeTrackerLoadRepair.Install(coopModuleRoot, bridgeId);
            ClientSetDisorganizedDiagnostic.Install(coopModuleRoot, bridgeId);
            ClientTroopRosterSequenceDiagnostic.Install(coopModuleRoot, bridgeId);
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
                "Initialized Bannerlord banner palette before EOE faction deserialization.";
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
            BCS.CoopBridge.BridgeRuntime.MarkCoopContainerReady();
        }

        public void Dispose()
        {
        }
    }
}
#endif
