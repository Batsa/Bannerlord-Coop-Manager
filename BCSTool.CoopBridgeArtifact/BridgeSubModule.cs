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

[assembly: AssemblyVersion("0.6.58.0")]
[assembly: AssemblyFileVersion("0.6.58.0")]
[assembly: AssemblyInformationalVersion("0.6.58")]

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
        internal const string ExpectedGameInterfaceHash =
            "253EC70813715EFB5618F72F5D05A4B10F57F46F235977B79A2114A064488E0E";
        private const string ExpectedCommonHash =
            "9DCBCF74E5D86FCBBA98D3BD66A4E33C01E4E688E0ADAA2367054452121E9018";
        internal const string ExpectedCampaignSystemHash =
            "1F8E33E2ED73E6EC653D7629180AFB70649DDC6E5BD1657A802A264EFDA1C3AE";
        private const string ExpectedHarmonyHash =
            "643C9FB053F7A7465F6A6F484614EF8459C376BEAD23E8BAD1E3F4C66B150520";
        private const int MapEventSideDestructionPatchesToken = 0x020003E9;
        private const int MapEventSideDestructionPrefixToken = 0x060014B7;
        private const int CallOriginalPolicyToken = 0x02000ABB;
        private const int IsOriginalAllowedToken = 0x06003570;
        private const int AllowedThreadToken = 0x02000015;
        private const int AllowedThreadConstructorToken = 0x0600007F;
        private const int AllowedThreadDisposeToken = 0x06000080;
        private const int RemoveInvolvedPartyInternalToken = 0x06002E5E;
        internal static readonly Guid ExpectedGameInterfaceMvid =
            new Guid("f1c54ac0-7b6d-4b2e-af77-2e8e9c7e38ed");
        private static readonly Guid ExpectedCommonMvid =
            new Guid("9a027d25-be9b-4676-8a3c-a25f39d33cd7");
        internal static readonly Guid ExpectedCampaignSystemMvid =
            new Guid("886629fe-6e60-40d7-9a57-8d46017179d9");
        private static readonly Guid ExpectedHarmonyMvid =
            new Guid("024a0e6e-c8c2-437e-ad04-7b6279389c23");
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

                var gameInterface = LoadPinnedCoopAssembly(
                    coopModuleRoot,
                    "GameInterface.dll",
                    "GameInterface",
                    ExpectedGameInterfaceHash,
                    ExpectedGameInterfaceMvid);
                var common = LoadPinnedCoopAssembly(
                    coopModuleRoot,
                    "Common.dll",
                    "Common",
                    ExpectedCommonHash,
                    ExpectedCommonMvid);
                ValidatePinnedAssembly(
                    typeof(Harmony).Assembly,
                    Path.Combine(
                        coopModuleRoot,
                        "bin",
                        "Win64_Shipping_Client",
                        "0Harmony.dll"),
                    "0Harmony",
                    ExpectedHarmonyHash,
                    ExpectedHarmonyMvid);
                var campaignSystem = ResolvePinnedLoadedAssembly(
                    "TaleWorlds.CampaignSystem",
                    ExpectedCampaignSystemHash,
                    ExpectedCampaignSystemMvid);

                var patchType = gameInterface.ManifestModule.ResolveType(
                    MapEventSideDestructionPatchesToken);
                if (patchType == null ||
                    !string.Equals(
                        patchType.FullName,
                        "GameInterface.Services.MapEvents.Patches.MapEventSideDestructionPatches",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Released Coop map-event destruction patch type did not match its pinned ABI.");
                }
                var prefix = gameInterface.ManifestModule.ResolveMethod(
                    MapEventSideDestructionPrefixToken) as MethodInfo;
                RequireMethod(
                    prefix,
                    patchType,
                    "Prefix",
                    typeof(bool),
                    true,
                    "TaleWorlds.CampaignSystem.MapEvents.MapEventSide",
                    "TaleWorlds.CampaignSystem.Party.PartyBase");

                var policyType = gameInterface.ManifestModule.ResolveType(CallOriginalPolicyToken);
                if (policyType == null ||
                    !string.Equals(
                        policyType.FullName,
                        "GameInterface.Policies.CallOriginalPolicy",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Released Coop call-original policy type did not match its pinned ABI.");
                }
                RequireMethod(
                    gameInterface.ManifestModule.ResolveMethod(IsOriginalAllowedToken) as MethodInfo,
                    policyType,
                    "IsOriginalAllowed",
                    typeof(bool),
                    true);

                var allowedThreadType = common.ManifestModule.ResolveType(AllowedThreadToken);
                if (allowedThreadType == null ||
                    !string.Equals(
                        allowedThreadType.FullName,
                        "Common.Util.AllowedThread",
                        StringComparison.Ordinal) ||
                    !typeof(IDisposable).IsAssignableFrom(allowedThreadType))
                {
                    throw new InvalidDataException(
                        "Released Coop AllowedThread type did not match its pinned ABI.");
                }
                var allowedThreadConstructor = common.ManifestModule.ResolveMethod(
                    AllowedThreadConstructorToken) as ConstructorInfo;
                if (allowedThreadConstructor == null ||
                    allowedThreadConstructor.DeclaringType != allowedThreadType ||
                    !allowedThreadConstructor.IsPublic ||
                    allowedThreadConstructor.GetParameters().Length != 0)
                {
                    throw new InvalidDataException(
                        "Released Coop AllowedThread constructor did not match its pinned ABI.");
                }
                RequireMethod(
                    common.ManifestModule.ResolveMethod(AllowedThreadDisposeToken) as MethodInfo,
                    allowedThreadType,
                    "Dispose",
                    typeof(void),
                    false);

                var removeMethod = campaignSystem.ManifestModule.ResolveMethod(
                    RemoveInvolvedPartyInternalToken) as MethodInfo;
                var mapEventType = campaignSystem.GetType(
                    "TaleWorlds.CampaignSystem.MapEvents.MapEvent",
                    true,
                    false);
                RequireMethod(
                    removeMethod,
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
                    "[BCS Coop Bridge] Installed pinned client map-event position-authority scope.");
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
                    method.Module != removeInvolvedPartyInternalMethod.Module ||
                    method.MetadataToken != removeInvolvedPartyInternalMethod.MetadataToken)
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
                    " pinned RemoveInvolvedPartyInternal calls; expected exactly one.");
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

        internal static Assembly LoadPinnedCoopAssembly(
            string coopModuleRoot,
            string fileName,
            string assemblyName,
            string expectedHash,
            Guid expectedMvid)
        {
            var path = Path.Combine(
                coopModuleRoot,
                "bin",
                "Win64_Shipping_Client",
                fileName);
            var assembly = ClientCoopHandlerRegistration.LoadExactCoopAssembly(
                path,
                assemblyName);
            ValidatePinnedAssembly(
                assembly,
                path,
                assemblyName,
                expectedHash,
                expectedMvid);
            return assembly;
        }

        internal static Assembly ResolvePinnedLoadedAssembly(
            string assemblyName,
            string expectedHash,
            Guid expectedMvid)
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
            ValidatePinnedAssembly(
                matches[0],
                matches[0].Location,
                assemblyName,
                expectedHash,
                expectedMvid);
            return matches[0];
        }

        private static void ValidatePinnedAssembly(
            Assembly assembly,
            string expectedPath,
            string expectedName,
            string expectedHash,
            Guid expectedMvid)
        {
            var canonicalPath = Path.GetFullPath(expectedPath);
            if (!File.Exists(canonicalPath))
                throw new FileNotFoundException("Pinned assembly is missing.", canonicalPath);
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
                    "Loaded " + expectedName + " assembly did not match its pinned location.",
                    assembly.Location);
            }
            var actualHash = BridgeRuntime.HashFileForCompatibility(canonicalPath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal) ||
                assembly.ManifestModule.ModuleVersionId != expectedMvid)
            {
                throw new InvalidDataException(
                    "Loaded " + expectedName + " assembly did not match its pinned binary identity.");
            }
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
                    "Released Coop method did not match its pinned ABI: " +
                    declaringType.FullName + "::" + name + ".");
            }
            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypeNames.Length)
            {
                throw new InvalidDataException(
                    "Released Coop method parameter count did not match its pinned ABI: " +
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
                        "Released Coop method parameter did not match its pinned ABI: " +
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
        private const int RobustnessPatchTypeToken = 0x020003D4;
        private const int RestoringTrackerFieldToken = 0x040009C7;
        private const int TrackerPostfixToken = 0x06001461;
        private const int MapEventTypeToken = 0x0200030E;
        private const int TroopUpgradeTrackerTypeToken = 0x020000AD;
        private const int OnAfterLoadToken = 0x06002E4F;
        private const int TrackerGetterToken = 0x06002E1A;
        private const string ExpectedTrackerPostfixIlHash =
            "90D17D47B193590A2F4C0A818AA0DBB3BC9E8BB3B9F4765101D3E5C944449EB5";
        private const string ExpectedOnAfterLoadIlHash =
            "691A338FBCF2A9C5863866F9B1161D23E13578810ADDB538E1A56D28D1A572C0";
        private const string ExpectedTrackerGetterIlHash =
            "1539AD0199AA3BDFC34E229CB895C687FD99FDC73A478C6523F082D99ACE145F";
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

                var gameInterface = ClientMapEventPositionAuthority.LoadPinnedCoopAssembly(
                    coopModuleRoot,
                    "GameInterface.dll",
                    "GameInterface",
                    ClientMapEventPositionAuthority.ExpectedGameInterfaceHash,
                    ClientMapEventPositionAuthority.ExpectedGameInterfaceMvid);
                var campaignSystem = ClientMapEventPositionAuthority.ResolvePinnedLoadedAssembly(
                    "TaleWorlds.CampaignSystem",
                    ClientMapEventPositionAuthority.ExpectedCampaignSystemHash,
                    ClientMapEventPositionAuthority.ExpectedCampaignSystemMvid);

                var robustnessPatchType = gameInterface.ManifestModule.ResolveType(
                    RobustnessPatchTypeToken);
                if (robustnessPatchType == null ||
                    !string.Equals(
                        robustnessPatchType.FullName,
                        "GameInterface.Services.MapEvents.Patches.MapEventRobustnessPatches",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Released Coop tracker robustness patch type did not match its pinned ABI.");
                }

                var restoringField = gameInterface.ManifestModule.ResolveField(
                    RestoringTrackerFieldToken);
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
                        "Released Coop tracker restoration guard did not match its pinned ABI.");
                }

                var mapEventType = campaignSystem.ManifestModule.ResolveType(MapEventTypeToken);
                if (mapEventType == null ||
                    mapEventType != typeof(MapEvent) ||
                    !string.Equals(
                        mapEventType.FullName,
                        "TaleWorlds.CampaignSystem.MapEvents.MapEvent",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds MapEvent type did not match its pinned ABI.");
                }
                var trackerType = campaignSystem.ManifestModule.ResolveType(
                    TroopUpgradeTrackerTypeToken);
                if (trackerType == null ||
                    !string.Equals(
                        trackerType.FullName,
                        "TaleWorlds.CampaignSystem.TroopUpgradeTracker",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds TroopUpgradeTracker type did not match its pinned ABI.");
                }

                var trackerPostfix = gameInterface.ManifestModule.ResolveMethod(
                    TrackerPostfixToken) as MethodInfo;
                ClientMapEventPositionAuthority.RequireMethod(
                    trackerPostfix,
                    robustnessPatchType,
                    "PostfixTroopUpgradeTracker",
                    typeof(void),
                    true,
                    mapEventType.FullName,
                    trackerType.MakeByRefType().FullName);
                if (!trackerPostfix.IsPrivate)
                {
                    throw new InvalidDataException(
                        "Released Coop tracker robustness postfix visibility did not match its pinned ABI.");
                }

                var onAfterLoad = campaignSystem.ManifestModule.ResolveMethod(
                    OnAfterLoadToken) as MethodInfo;
                ClientMapEventPositionAuthority.RequireMethod(
                    onAfterLoad,
                    mapEventType,
                    "OnAfterLoad",
                    typeof(void),
                    false);
                if (!onAfterLoad.IsAssembly)
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds MapEvent.OnAfterLoad visibility did not match its pinned ABI.");
                }

                var trackerGetter = campaignSystem.ManifestModule.ResolveMethod(
                    TrackerGetterToken) as MethodInfo;
                ClientMapEventPositionAuthority.RequireMethod(
                    trackerGetter,
                    mapEventType,
                    "get_TroopUpgradeTracker",
                    trackerType,
                    false);
                if (!trackerGetter.IsPublic || !trackerGetter.IsSpecialName)
                {
                    throw new InvalidDataException(
                        "Released TaleWorlds tracker getter did not match its pinned ABI.");
                }

                RequireMethodIlHash(
                    trackerPostfix,
                    ExpectedTrackerPostfixIlHash,
                    "Released Coop tracker robustness postfix");
                RequireMethodIlHash(
                    onAfterLoad,
                    ExpectedOnAfterLoadIlHash,
                    "Released TaleWorlds MapEvent.OnAfterLoad");
                RequireMethodIlHash(
                    trackerGetter,
                    ExpectedTrackerGetterIlHash,
                    "Released TaleWorlds tracker getter");

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
                        "[BCS Coop Bridge] Installed pinned client troop-upgrade tracker load repair.");
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

        private static void RequireMethodIlHash(
            MethodInfo method,
            string expectedHash,
            string description)
        {
            var body = method.GetMethodBody();
            var bytes = body == null ? null : body.GetILAsByteArray();
            if (bytes == null)
                throw new InvalidDataException(description + " has no managed IL body.");
            string actualHash;
            using (var sha256 = SHA256.Create())
            {
                actualHash = BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty);
            }
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    description + " did not match its pinned IL fingerprint.");
            }
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
        internal const string ExpectedCommonHash =
            "9DCBCF74E5D86FCBBA98D3BD66A4E33C01E4E688E0ADAA2367054452121E9018";
        internal static readonly Guid ExpectedCommonMvid =
            new Guid("9a027d25-be9b-4676-8a3c-a25f39d33cd7");
        private const int GameThreadTypeToken = 0x02000009;
        private const int GameThreadInstanceGetterToken = 0x0600001E;
        private const int IsGameThreadGetterToken = 0x0600001C;
        private const int MaxFailureKeys = 16;
        private static readonly object FailureSync = new object();
        private static readonly ISet<string> FailureKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static bool failureSuppressionReported;

        internal static GameThreadProbe CreateGameThreadProbe(Assembly common)
        {
            var gameThreadType = common.ManifestModule.ResolveType(GameThreadTypeToken);
            if (gameThreadType == null ||
                !string.Equals(gameThreadType.FullName, "Common.GameThread", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Released Coop GameThread type did not match its pinned ABI.");
            }

            var instanceGetter = common.ManifestModule.ResolveMethod(
                GameThreadInstanceGetterToken) as MethodInfo;
            ClientMapEventPositionAuthority.RequireMethod(
                instanceGetter,
                gameThreadType,
                "get_Instance",
                gameThreadType,
                true);
            var isGameThreadGetter = common.ManifestModule.ResolveMethod(
                IsGameThreadGetterToken) as MethodInfo;
            ClientMapEventPositionAuthority.RequireMethod(
                isGameThreadGetter,
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
                    "Released Coop GameThread accessors did not match their pinned ABI.");
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
                        method.Module == target.Module &&
                        method.MetadataToken == target.MetadataToken)
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
        private const int MobilePartyTypeToken = 0x020002F2;
        private const int SetDisorganizedToken = 0x06002B0B;
        private const int IsDisorganizedGetterToken = 0x06002A6A;
        private const int IsMainPartyGetterToken = 0x06002B35;
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
                    var common = ClientMapEventPositionAuthority.LoadPinnedCoopAssembly(
                        coopModuleRoot,
                        "Common.dll",
                        "Common",
                        ClientDiagnosticSupport.ExpectedCommonHash,
                        ClientDiagnosticSupport.ExpectedCommonMvid);
                    var campaignSystem =
                        ClientMapEventPositionAuthority.ResolvePinnedLoadedAssembly(
                            "TaleWorlds.CampaignSystem",
                            ClientMapEventPositionAuthority.ExpectedCampaignSystemHash,
                            ClientMapEventPositionAuthority.ExpectedCampaignSystemMvid);

                    var mobilePartyType = campaignSystem.ManifestModule.ResolveType(
                        MobilePartyTypeToken);
                    if (mobilePartyType == null ||
                        !string.Equals(
                            mobilePartyType.FullName,
                            "TaleWorlds.CampaignSystem.Party.MobileParty",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released TaleWorlds MobileParty type did not match its pinned ABI.");
                    }

                    resolvedTarget = campaignSystem.ManifestModule.ResolveMethod(
                        SetDisorganizedToken) as MethodInfo;
                    ClientMapEventPositionAuthority.RequireMethod(
                        resolvedTarget,
                        mobilePartyType,
                        "SetDisorganized",
                        typeof(void),
                        false,
                        typeof(bool).FullName);
                    if (!resolvedTarget.IsPublic)
                    {
                        throw new InvalidDataException(
                            "Released TaleWorlds MobileParty.SetDisorganized visibility did not match its pinned ABI.");
                    }

                    var resolvedIsDisorganizedGetter =
                        campaignSystem.ManifestModule.ResolveMethod(
                            IsDisorganizedGetterToken) as MethodInfo;
                    ClientMapEventPositionAuthority.RequireMethod(
                        resolvedIsDisorganizedGetter,
                        mobilePartyType,
                        "get_IsDisorganized",
                        typeof(bool),
                        false);
                    var resolvedIsMainPartyGetter = campaignSystem.ManifestModule.ResolveMethod(
                        IsMainPartyGetterToken) as MethodInfo;
                    ClientMapEventPositionAuthority.RequireMethod(
                        resolvedIsMainPartyGetter,
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
                            "Released TaleWorlds MobileParty diagnostic getters did not match their pinned ABI.");
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
                            "Released TaleWorlds MobileParty.StringId getter did not match its pinned ABI.");
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
                        "[BCS Coop Bridge] Installed pinned client SetDisorganized diagnostic.");
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
        private const int AutoRegistryHandlerTypeToken = 0x02000AB5;
        private const int CreateClientInstanceToken = 0x06003565;
        private const int AutoRegistryObjectManagerFieldToken = 0x04001708;
        private const int NetworkCreateInstanceIdFieldToken = 0x0400170D;
        private const int TroopRosterDeltaHandlerTypeToken = 0x0200019B;
        private const int DeltaObjectManagerFieldToken = 0x04000432;
        private const int HandleSetNumberToken = 0x06000852;
        private const int HandleSetWoundedToken = 0x06000853;
        private const int HandleElementBatchToken = 0x06000854;
        private const int HandleRemoveZeroCountsToken = 0x06000855;
        private const int SetNumberRosterIdFieldToken = 0x04000414;
        private const int SetWoundedRosterIdFieldToken = 0x04000417;
        private const int ElementBatchRosterIdFieldToken = 0x04000410;
        private const int RemoveZeroCountsRosterIdFieldToken = 0x04000413;
        private const int ApplyClosureTypeToken = 0x02000BCA;
        private const int ApplyClosureMethodToken = 0x060038C7;
        private const int ApplyClosureOuterFieldToken = 0x040019C7;
        private const int ApplyClosureRosterIdFieldToken = 0x040019C8;
        private const int ApplyClosureDelegateFieldToken = 0x040019CA;
        private const int RemoveClosureTypeToken = 0x02000BC9;
        private const int RemoveClosureMethodToken = 0x060038C5;
        private const int RemoveClosureOuterFieldToken = 0x040019C5;
        private const int RemoveClosureRosterIdFieldToken = 0x040019C6;
        private const int TroopRosterTypeToken = 0x020002E2;
        private const int ObjectManagerTypeToken = 0x0200032B;
        private const int ObjectManagerContainsIdToken = 0x06001023;
        private const int MaxRecords = 512;
        private const int MaxRosterIds = 128;
        private static readonly long DedupeTicks =
            Math.Max(1L, Stopwatch.Frequency / 4L);
        private static readonly object Sync = new object();
        private static readonly ISet<string> RosterIds =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly IDictionary<string, long> Recent =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static IDictionary<int, PayloadIdAccessor> payloadAccessors;
        private static IDictionary<int, string> deltaTypes;
        private static FieldInfo autoRegistryObjectManagerField;
        private static FieldInfo deltaObjectManagerField;
        private static MethodInfo objectManagerContainsId;
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
                        ClientMapEventPositionAuthority.LoadPinnedCoopAssembly(
                            coopModuleRoot,
                            "GameInterface.dll",
                            "GameInterface",
                            ClientMapEventPositionAuthority.ExpectedGameInterfaceHash,
                            ClientMapEventPositionAuthority.ExpectedGameInterfaceMvid);
                    var common = ClientMapEventPositionAuthority.LoadPinnedCoopAssembly(
                        coopModuleRoot,
                        "Common.dll",
                        "Common",
                        ClientDiagnosticSupport.ExpectedCommonHash,
                        ClientDiagnosticSupport.ExpectedCommonMvid);
                    var campaignSystem =
                        ClientMapEventPositionAuthority.ResolvePinnedLoadedAssembly(
                            "TaleWorlds.CampaignSystem",
                            ClientMapEventPositionAuthority.ExpectedCampaignSystemHash,
                            ClientMapEventPositionAuthority.ExpectedCampaignSystemMvid);

                    var troopRosterType = campaignSystem.ManifestModule.ResolveType(
                        TroopRosterTypeToken);
                    if (troopRosterType == null ||
                        !string.Equals(
                            troopRosterType.FullName,
                            "TaleWorlds.CampaignSystem.Roster.TroopRoster",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released TaleWorlds TroopRoster type did not match its pinned ABI.");
                    }

                    var openAutoRegistryHandler = gameInterface.ManifestModule.ResolveType(
                        AutoRegistryHandlerTypeToken);
                    if (openAutoRegistryHandler == null ||
                        !openAutoRegistryHandler.IsGenericTypeDefinition ||
                        openAutoRegistryHandler.GetGenericArguments().Length != 1 ||
                        !string.Equals(
                            openAutoRegistryHandler.FullName,
                            "GameInterface.Registry.Auto.AutoRegistryHandler`1",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released Coop AutoRegistryHandler type did not match its pinned ABI.");
                    }
                    var openCreateMethod = gameInterface.ManifestModule.ResolveMethod(
                        CreateClientInstanceToken) as MethodInfo;
                    RequireAutoRegistryCreateMethod(
                        openCreateMethod,
                        openAutoRegistryHandler);
                    var closedAutoRegistryHandler = openAutoRegistryHandler.MakeGenericType(
                        troopRosterType);
                    var createClientInstance = FindClosedMethod(
                        closedAutoRegistryHandler,
                        CreateClientInstanceToken);
                    RequireClosedAutoRegistryCreateMethod(
                        createClientInstance,
                        closedAutoRegistryHandler);
                    targets.Add(createClientInstance);

                    var resolvedAutoRegistryObjectManagerField = FindClosedField(
                        closedAutoRegistryHandler,
                        AutoRegistryObjectManagerFieldToken);
                    RequireObjectManagerField(
                        resolvedAutoRegistryObjectManagerField,
                        closedAutoRegistryHandler,
                        "<ObjectManager>k__BackingField");

                    var deltaHandlerType = gameInterface.ManifestModule.ResolveType(
                        TroopRosterDeltaHandlerTypeToken);
                    if (deltaHandlerType == null ||
                        !string.Equals(
                            deltaHandlerType.FullName,
                            "GameInterface.Services.TroopRosters.Handlers.TroopRosterDeltaHandler",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released Coop TroopRosterDeltaHandler type did not match its pinned ABI.");
                    }
                    var resolvedDeltaObjectManagerField =
                        gameInterface.ManifestModule.ResolveField(
                            DeltaObjectManagerFieldToken);
                    RequireObjectManagerField(
                        resolvedDeltaObjectManagerField,
                        deltaHandlerType,
                        "objectManager");

                    var setNumber = RequireDeltaMethod(
                        gameInterface,
                        deltaHandlerType,
                        HandleSetNumberToken,
                        "Handle_NetworkSetNumber");
                    var setWounded = RequireDeltaMethod(
                        gameInterface,
                        deltaHandlerType,
                        HandleSetWoundedToken,
                        "Handle_NetworkSetWoundedNumber");
                    var elementBatch = RequireDeltaMethod(
                        gameInterface,
                        deltaHandlerType,
                        HandleElementBatchToken,
                        "Handle_NetworkElementBatch");
                    var removeZeroCounts = RequireDeltaMethod(
                        gameInterface,
                        deltaHandlerType,
                        HandleRemoveZeroCountsToken,
                        "Handle_NetworkRemoveZeroCounts");
                    targets.Add(setNumber);
                    targets.Add(setWounded);
                    targets.Add(elementBatch);
                    targets.Add(removeZeroCounts);

                    var resolvedPayloadAccessors =
                        new Dictionary<int, PayloadIdAccessor>();
                    resolvedPayloadAccessors.Add(
                        CreateClientInstanceToken,
                        BuildPayloadIdAccessor(
                            createClientInstance,
                            "GameInterface.Registry.Auto.NetworkCreateInstance`1",
                            troopRosterType,
                            NetworkCreateInstanceIdFieldToken,
                            "InstanceId"));
                    resolvedPayloadAccessors.Add(
                        HandleSetNumberToken,
                        BuildPayloadIdAccessor(
                            setNumber,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterSetNumber",
                            null,
                            SetNumberRosterIdFieldToken,
                            "RosterId"));
                    resolvedPayloadAccessors.Add(
                        HandleSetWoundedToken,
                        BuildPayloadIdAccessor(
                            setWounded,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterSetWoundedNumber",
                            null,
                            SetWoundedRosterIdFieldToken,
                            "RosterId"));
                    resolvedPayloadAccessors.Add(
                        HandleElementBatchToken,
                        BuildPayloadIdAccessor(
                            elementBatch,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterElementBatch",
                            null,
                            ElementBatchRosterIdFieldToken,
                            "RosterId"));
                    resolvedPayloadAccessors.Add(
                        HandleRemoveZeroCountsToken,
                        BuildPayloadIdAccessor(
                            removeZeroCounts,
                            "GameInterface.Services.TroopRosters.Messages.NetworkTroopRosterRemoveZeroCounts",
                            null,
                            RemoveZeroCountsRosterIdFieldToken,
                            "RosterId"));

                    var resolvedDeltaTypes = new Dictionary<int, string>
                    {
                        { HandleSetNumberToken, "NetworkTroopRosterSetNumber" },
                        { HandleSetWoundedToken, "NetworkTroopRosterSetWoundedNumber" },
                        { HandleElementBatchToken, "NetworkTroopRosterElementBatch" },
                        { HandleRemoveZeroCountsToken, "NetworkTroopRosterRemoveZeroCounts" }
                    };

                    var resolvedApplyClosure = BuildClosureAccessor(
                        gameInterface,
                        deltaHandlerType,
                        ApplyClosureTypeToken,
                        ApplyClosureMethodToken,
                        "GameInterface.Services.TroopRosters.Handlers.TroopRosterDeltaHandler+<>c__DisplayClass22_0",
                        "<Apply>b__0",
                        ApplyClosureOuterFieldToken,
                        ApplyClosureRosterIdFieldToken,
                        ApplyClosureDelegateFieldToken,
                        "Apply");
                    var resolvedRemoveClosure = BuildClosureAccessor(
                        gameInterface,
                        deltaHandlerType,
                        RemoveClosureTypeToken,
                        RemoveClosureMethodToken,
                        "GameInterface.Services.TroopRosters.Handlers.TroopRosterDeltaHandler+<>c__DisplayClass21_0",
                        "<Handle_NetworkRemoveZeroCounts>b__0",
                        RemoveClosureOuterFieldToken,
                        RemoveClosureRosterIdFieldToken,
                        0,
                        "NetworkTroopRosterRemoveZeroCounts");
                    targets.Add(resolvedApplyClosure.Method);
                    targets.Add(resolvedRemoveClosure.Method);

                    var objectManagerType = gameInterface.ManifestModule.ResolveType(
                        ObjectManagerTypeToken);
                    if (objectManagerType == null ||
                        !objectManagerType.IsInterface ||
                        !string.Equals(
                            objectManagerType.FullName,
                            "GameInterface.Services.ObjectManager.IObjectManager",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Released Coop IObjectManager type did not match its pinned ABI.");
                    }
                    var resolvedContainsId = gameInterface.ManifestModule.ResolveMethod(
                        ObjectManagerContainsIdToken) as MethodInfo;
                    ClientMapEventPositionAuthority.RequireMethod(
                        resolvedContainsId,
                        objectManagerType,
                        "Contains",
                        typeof(bool),
                        false,
                        typeof(string).FullName);
                    if (!resolvedContainsId.IsPublic || !resolvedContainsId.IsAbstract)
                    {
                        throw new InvalidDataException(
                            "Released Coop IObjectManager.Contains(string) did not match its pinned ABI.");
                    }

                    payloadAccessors = resolvedPayloadAccessors;
                    deltaTypes = resolvedDeltaTypes;
                    autoRegistryObjectManagerField =
                        resolvedAutoRegistryObjectManagerField;
                    deltaObjectManagerField = resolvedDeltaObjectManagerField;
                    objectManagerContainsId = resolvedContainsId;
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
                        "[BCS Coop Bridge] Installed pinned client TroopRoster sequence diagnostic.");
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
                var instanceId = ReadPayloadId(CreateClientInstanceToken, __0);
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
                var token = __originalMethod.MetadataToken;
                string deltaType;
                if (!deltaTypes.TryGetValue(token, out deltaType))
                    throw new InvalidDataException("Unexpected TroopRoster delta hook token.");
                var rosterId = ReadPayloadId(token, __0);
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
                var accessor = __originalMethod.MetadataToken == ApplyClosureMethodToken
                    ? applyClosureAccessor
                    : __originalMethod.MetadataToken == RemoveClosureMethodToken
                        ? removeClosureAccessor
                        : null;
                if (accessor == null)
                    throw new InvalidDataException("Unexpected TroopRoster closure hook token.");
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
                    "Released Coop AutoRegistryHandler.CreateClientInstance did not match its pinned ABI.");
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
                    "Released Coop AutoRegistryHandler.CreateClientInstance payload did not match its pinned ABI.");
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
                    "Released Coop NetworkCreateInstance payload did not match its pinned ABI.");
            }
        }

        private static void RequireClosedAutoRegistryCreateMethod(
            MethodInfo method,
            Type closedDeclaringType)
        {
            if (method == null ||
                method.DeclaringType != closedDeclaringType ||
                method.MetadataToken != CreateClientInstanceToken ||
                method.ContainsGenericParameters)
            {
                throw new InvalidDataException(
                    "Released Coop closed TroopRoster registry method did not match its pinned ABI.");
            }
            RequireAutoRegistryCreateMethod(method, closedDeclaringType);
        }

        private static MethodInfo FindClosedMethod(Type closedType, int token)
        {
            return closedType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .SingleOrDefault(method => method.MetadataToken == token);
        }

        private static FieldInfo FindClosedField(Type closedType, int token)
        {
            return closedType.GetFields(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .SingleOrDefault(field => field.MetadataToken == token);
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
                    "Released Coop object-manager field did not match its pinned ABI: " +
                    declaringType.FullName + "::" + name + ".");
            }
        }

        private static MethodInfo RequireDeltaMethod(
            Assembly gameInterface,
            Type declaringType,
            int token,
            string name)
        {
            var method = gameInterface.ManifestModule.ResolveMethod(token) as MethodInfo;
            if (method == null ||
                method.DeclaringType != declaringType ||
                !string.Equals(method.Name, name, StringComparison.Ordinal) ||
                method.ReturnType != typeof(void) ||
                method.IsStatic ||
                !method.IsPrivate ||
                method.GetParameters().Length != 1)
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster delta method did not match its pinned ABI: " + name + ".");
            }
            return method;
        }

        private static PayloadIdAccessor BuildPayloadIdAccessor(
            MethodInfo method,
            string expectedMessageTypeName,
            Type expectedGenericArgument,
            int idFieldToken,
            string idFieldName)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                throw new InvalidDataException("Pinned Coop payload parameter count changed.");
            var payloadType = parameters[0].ParameterType;
            if (!payloadType.IsGenericType ||
                !string.Equals(
                    payloadType.GetGenericTypeDefinition().FullName,
                    "Common.Messaging.MessagePayload`1",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Pinned Coop payload type changed.");
            }
            var messageType = payloadType.GetGenericArguments()[0];
            if (expectedGenericArgument == null)
            {
                if (!string.Equals(
                        messageType.FullName,
                        expectedMessageTypeName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Pinned Coop message type changed.");
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
                throw new InvalidDataException("Pinned Coop generic message type changed.");
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
                throw new InvalidDataException("Pinned Coop MessagePayload.What getter changed.");
            }

            var idField = messageType.GetFields(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .SingleOrDefault(field => field.MetadataToken == idFieldToken);
            if (idField == null ||
                !string.Equals(idField.Name, idFieldName, StringComparison.Ordinal) ||
                idField.FieldType != typeof(string) ||
                idField.IsStatic ||
                !idField.IsPublic ||
                !idField.IsInitOnly)
            {
                throw new InvalidDataException("Pinned Coop payload identifier field changed.");
            }
            return new PayloadIdAccessor(whatGetter, idField);
        }

        private static ClosureAccessor BuildClosureAccessor(
            Assembly gameInterface,
            Type deltaHandlerType,
            int typeToken,
            int methodToken,
            string typeName,
            string methodName,
            int outerFieldToken,
            int rosterIdFieldToken,
            int delegateFieldToken,
            string fallbackDeltaType)
        {
            var closureType = gameInterface.ManifestModule.ResolveType(typeToken);
            if (closureType == null ||
                !string.Equals(closureType.FullName, typeName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster closure type did not match its pinned ABI.");
            }
            var method = gameInterface.ManifestModule.ResolveMethod(methodToken) as MethodInfo;
            ClientMapEventPositionAuthority.RequireMethod(
                method,
                closureType,
                methodName,
                typeof(void),
                false);
            if (!method.IsAssembly)
            {
                throw new InvalidDataException(
                    "Released Coop TroopRoster closure method visibility changed.");
            }

            var outerField = gameInterface.ManifestModule.ResolveField(outerFieldToken);
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
            var rosterIdField = gameInterface.ManifestModule.ResolveField(
                rosterIdFieldToken);
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
            if (delegateFieldToken != 0)
            {
                delegateField = gameInterface.ManifestModule.ResolveField(
                    delegateFieldToken);
                if (delegateField == null ||
                    delegateField.DeclaringType != closureType ||
                    !string.Equals(delegateField.Name, "apply", StringComparison.Ordinal) ||
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

        private static string ReadPayloadId(int token, object payload)
        {
            if (payload == null)
                return null;
            PayloadIdAccessor accessor;
            if (payloadAccessors == null ||
                !payloadAccessors.TryGetValue(token, out accessor))
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
#if BCS_SERVER
        private const string ServerXmlOverlayObjectSystemHash =
            "E080BAFA4DA67B76385B20E7898D0B1B197A28B92A731F7B54A8898665BC2C3B";
        private const string ServerXmlOverlayApplyXsltIlHash =
            "3A397E9A796458021A93B5A6393A1220D9DCA6D75B9860CFDAA5C267F01961D9";
        private const int ServerXmlOverlayApplyXsltToken = 0x0600005E;
        private static readonly Guid ServerXmlOverlayObjectSystemMvid =
            new Guid("825bfb0e-b3c8-4816-a193-0f5ded0dd5d5");
#endif
        private static readonly string[] GameRuntimeFingerprintFiles =
        {
            "TaleWorlds.CampaignSystem.dll",
            "TaleWorlds.Core.dll",
            "TaleWorlds.Library.dll",
            "TaleWorlds.Localization.dll",
            "TaleWorlds.ModuleManager.dll",
            "TaleWorlds.MountAndBlade.dll",
            "TaleWorlds.ObjectSystem.dll"
        };
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
                        if (!IsSha256(fields[3]) || !IsSha256(fields[4]))
                        {
                            throw new InvalidDataException(
                                "Game-version compatibility runtime fingerprints must be SHA-256 hashes.");
                        }
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
                        if (!IsSha256(fields[3]) ||
                            !string.Equals(
                                fields[3],
                                fields[3].ToUpperInvariant(),
                                StringComparison.Ordinal))
                            throw new InvalidDataException("Client assembly resolver requires a SHA-256 fingerprint.");
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
                        ValidatePinnedRegularFile(
                            resolverPath,
                            fields[3],
                            "Pinned client assembly resolver");
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
#if BCS_SERVER
                        if (!File.Exists(ignoredPath))
                        {
                            throw new InvalidDataException(
                                "Ignored server-only content file is missing: " +
                                ignoredModuleId + "/" + ignoredRelativePath);
                        }
#endif
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
                        var redirectHash = HashFile(redirectPath);
                        if (!string.Equals(redirectHash, fields[3], StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "Server file redirect fingerprint mismatch for " +
                                redirectModuleId + "/" + redirectRelativePath +
                                ". Expected " + fields[3] + ", found " + redirectHash + ".");
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
                        if (!IsUppercaseSha256(fields[3]) ||
                            !IsUppercaseSha256(fields[7]) ||
                            !IsUppercaseSha256(fields[9]))
                        {
                            throw new InvalidDataException(
                                "Server map terrain size source, loader, and target require canonical SHA-256 fingerprints.");
                        }
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
                        ValidatePinnedModuleRegularFile(
                            terrainModule.RootPath,
                            terrainSourcePath,
                            fields[3],
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
                        if (!IsSha256(fields[3]) || !IsSha256(fields[5]))
                            throw new InvalidDataException(
                                "Server XML overlay fingerprints must be SHA-256 hashes.");
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
                        ValidatePinnedRegularFile(
                            sourcePath,
                            fields[3],
                            "Server XML overlay source");
#if BCS_SERVER
                        ValidatePinnedRegularFile(
                            overlayPath,
                            fields[5],
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
                        var actualContentHash = BuildContentFingerprint(
                            contentModuleId,
                            contentModule.RootPath,
                            moduleIgnored ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        if (!string.Equals(actualContentHash, fields[2], StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "Coop bridge content fingerprint mismatch for " + contentModuleId +
                                ". Expected " + fields[2] + ", found " + actualContentHash + ".");
                        }
                        continue;
                    }

                    if (fields.Length != 5 || !string.Equals(fields[0], "MODULE", StringComparison.Ordinal))
                        throw new InvalidDataException("Malformed BCS Coop bridge record.");

                    var moduleId = Decode(fields[1]);
                    var version = Decode(fields[2]);
                    var dllName = Decode(fields[3]);
                    var expectedHash = fields[4];
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
                    var actualHash = HashFile(dllPath);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Coop bridge assembly fingerprint mismatch for " + moduleId + "/" + dllName +
                            ". Expected " + expectedHash + ", found " + actualHash + ".");
                    }
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

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
                return false;
            foreach (var character in value)
            {
                if (!Uri.IsHexDigit(character))
                    return false;
            }
            return true;
        }

        private static bool IsUppercaseSha256(string value)
        {
            if (value == null || value.Length != 64)
                return false;
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
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

        internal static string HashFileForCompatibility(string path)
        {
            return HashFile(path);
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
                " pinned client assembly resolver(s).");
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
                    "Pinned Bannerlord version mismatch. Expected " + expectedVersion +
                    ", found " + native.Version + ".");
            }

            var runtimeDirectory = Path.GetDirectoryName(typeof(MBSubModuleBase).Assembly.Location);
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
                throw new InvalidOperationException("Bannerlord runtime directory could not be resolved.");
            var expectedRuntimeHash = IsServerProcess()
                ? rule.ServerRuntimeSha256
                : rule.ClientRuntimeSha256;
            var actualRuntimeHash = BuildGameRuntimeFingerprint(runtimeDirectory);
            if (!string.Equals(actualRuntimeHash, expectedRuntimeHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Pinned Bannerlord runtime fingerprint mismatch. Expected " +
                    expectedRuntimeHash + ", found " + actualRuntimeHash + ".");
            }
            RecordStartupProgress("Pinned game runtime fingerprint validated");

            if (IsServerProcess())
            {
                Console.WriteLine(
                    "[BCS Coop Bridge] Pinned server game runtime validated for " +
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
                InstallPinnedClientOnlyModuleCompatibility(validatorType, bridgeId);
#endif
            RecordStartupProgress("Harmony game-version patch completed");
            Console.WriteLine(
                "[BCS Coop Bridge] Installed pinned Coop game-version compatibility: " +
                rule.ServerVersion + " -> " + rule.ClientVersion + ".");
        }

#if BCS_SERVER
        private static void InstallPinnedClientOnlyModuleCompatibility(
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

            var prefix = typeof(PinnedClientOnlyModulePrefix).GetMethod(
                "Filter",
                BindingFlags.Static | BindingFlags.Public);
            if (prefix == null)
            {
                throw new MissingMethodException(
                    typeof(PinnedClientOnlyModulePrefix).FullName,
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

        private static string BuildGameRuntimeFingerprint(string binDirectory)
        {
            using (var payload = new MemoryStream())
            {
                foreach (var fileName in GameRuntimeFingerprintFiles.OrderBy(value => value, StringComparer.Ordinal))
                {
                    var path = Path.Combine(binDirectory, fileName);
                    if (!File.Exists(path) ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new FileNotFoundException(
                            "Pinned Bannerlord runtime file is missing or linked.",
                            path);
                    }
                    var nameBytes = Encoding.UTF8.GetBytes(fileName);
                    payload.Write(nameBytes, 0, nameBytes.Length);
                    payload.WriteByte(0);
                    using (var sha256 = SHA256.Create())
                    using (var input = File.OpenRead(path))
                    {
                        var fileHash = sha256.ComputeHash(input);
                        payload.Write(fileHash, 0, fileHash.Length);
                    }
                }
                return Hash(payload.ToArray());
            }
        }

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
                "[BCS Coop Bridge] Installed " + redirects.Length + " pinned server file redirect(s).");
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
                    "Server map terrain size requires the pinned DedicatedServer.Core loader and SandBox target.");
            }

            ResolvePinnedRuntimeAssembly(
                rule.LoaderAssemblyName,
                rule.LoaderAssemblySha256,
                true,
                "Server map terrain size loader");
            var targetAssembly = ResolvePinnedRuntimeAssembly(
                rule.TargetAssemblyName,
                rule.TargetAssemblySha256,
                false,
                "Server map terrain size target");
            var mapSceneType = targetAssembly.GetType("SandBox.MapScene", false, false);
            if (mapSceneType == null)
            {
                throw new TypeLoadException(
                    "Pinned Bannerlord SandBox assembly does not contain SandBox.MapScene.");
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
                "[BCS Coop Bridge] Installed pinned server map terrain size " +
                rule.Width.ToString("R", CultureInfo.InvariantCulture) + "x" +
                rule.Height.ToString("R", CultureInfo.InvariantCulture) + " from " +
                rule.ModuleId + "/" + rule.RelativePath + ".");
        }

        private static Assembly ResolvePinnedRuntimeAssembly(
            string expectedName,
            string expectedHash,
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
            var actualHash = HashFile(canonicalLocation);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    description + " fingerprint mismatch at " + canonicalLocation +
                    ". Expected " + expectedHash + ", found " + actualHash + ".");
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
            var xsltLoader = ResolvePinnedServerXsltLoader(objectManagerType);
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
                "[BCS Coop Bridge] Installed " + overlays.Length + " pinned server XML overlay(s).");
        }

#if BCS_SERVER
        private static MethodInfo ResolvePinnedServerXsltLoader(Type objectManagerType)
        {
            var objectSystem = ResolvePinnedRuntimeAssembly(
                "TaleWorlds.ObjectSystem",
                ServerXmlOverlayObjectSystemHash,
                true,
                "Server XML overlay XSLT loader");
            if (objectManagerType.Assembly != objectSystem ||
                objectSystem.ManifestModule.ModuleVersionId != ServerXmlOverlayObjectSystemMvid)
            {
                throw new InvalidDataException(
                    "Server XML overlay XSLT loader did not match its pinned ObjectSystem identity.");
            }

            MethodInfo method;
            try
            {
                method = objectSystem.ManifestModule.ResolveMethod(
                    ServerXmlOverlayApplyXsltToken) as MethodInfo;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Pinned server XML overlay ApplyXslt token could not be resolved.",
                    exception);
            }
            var parameters = method == null ? new ParameterInfo[0] : method.GetParameters();
            if (method == null ||
                method.DeclaringType != objectManagerType ||
                !string.Equals(method.Name, "ApplyXslt", StringComparison.Ordinal) ||
                !method.IsPublic ||
                !method.IsStatic ||
                method.IsGenericMethodDefinition ||
                method.ReturnType != typeof(XmlDocument) ||
                parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(string) ||
                parameters[1].ParameterType != typeof(XmlDocument))
            {
                throw new InvalidDataException(
                    "Pinned server XML overlay ApplyXslt method did not match its exact ABI.");
            }

            var body = method.GetMethodBody();
            var il = body == null ? null : body.GetILAsByteArray();
            if (il == null ||
                !string.Equals(Hash(il), ServerXmlOverlayApplyXsltIlHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Pinned server XML overlay ApplyXslt implementation changed.");
            }
            return method;
        }
#endif

        private static string BuildContentFingerprint(
            string moduleId,
            string moduleRoot,
            ISet<string> ignoredRelativePaths)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(moduleRoot);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var relative = MakeRelativePath(moduleRoot, entry).Replace('\\', '/');
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked module content is not safe to fingerprint: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (IsCoopRoleSpecificDirectory(moduleId, relative))
                            continue;
                        pending.Push(entry);
                        continue;
                    }

                    if (string.Equals(relative, "SubModule.xml", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ignoredRelativePaths.Contains(relative))
                        continue;
                    var extension = Path.GetExtension(entry);
                    if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".xslt", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".xsl", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(entry);
                    }
                }
            }

            files.Sort(delegate(string left, string right)
            {
                return string.CompareOrdinal(
                    MakeRelativePath(moduleRoot, left).Replace('\\', '/'),
                    MakeRelativePath(moduleRoot, right).Replace('\\', '/'));
            });
            using (var payload = new MemoryStream())
            {
                foreach (var file in files)
                {
                    var relative = MakeRelativePath(moduleRoot, file).Replace('\\', '/');
                    var relativeBytes = Encoding.UTF8.GetBytes(relative);
                    payload.Write(relativeBytes, 0, relativeBytes.Length);
                    payload.WriteByte(0);
                    byte[] fileHash;
                    using (var sha = SHA256.Create())
                    using (var input = File.OpenRead(file))
                        fileHash = sha.ComputeHash(input);
                    payload.Write(fileHash, 0, fileHash.Length);
                }
                return Hash(payload.ToArray());
            }
        }

        private static bool IsCoopRoleSpecificDirectory(string moduleId, string relativePath)
        {
            if (!string.Equals(moduleId, "Coop", StringComparison.OrdinalIgnoreCase) ||
                relativePath.IndexOf('/') >= 0)
            {
                return false;
            }
            return string.Equals(relativePath, "DedicatedServer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(relativePath, "GUI", StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

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

        private static void ValidatePinnedRegularFile(
            string path,
            string expectedHash,
            string description)
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException(description + " is missing or linked.", path);
            }
            var actualHash = HashFile(path);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    description + " fingerprint mismatch. Expected " +
                    expectedHash + ", found " + actualHash + ".");
            }
        }

        private static void ValidatePinnedModuleRegularFile(
            string moduleRoot,
            string path,
            string expectedHash,
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

            ValidatePinnedRegularFile(canonicalPath, expectedHash, description);
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

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
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
                    "Pinned client assembly resolved to an unexpected identity.",
                    path);
            }
            Console.WriteLine(
                "[BCS Coop Bridge] Resolved pinned client assembly " +
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
                    "Pinned game-version compatibility has an empty version.");
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
                "[BCS Coop Bridge] Accepted pinned game-version pair " +
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
    internal static class PinnedClientOnlyModulePrefix
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
                    "[BCS Coop Bridge] Accepted pinned client-only module " +
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
