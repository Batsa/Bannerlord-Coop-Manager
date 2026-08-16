using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using BCSTool.Services;

internal static class BattleSceneProjectionRuntimeRegression
{
    private const string FeatureName = "ClientDeterministicBattleSceneProjection";
    private const string ProjectionTypeName =
        "BCS.CoopBridge.ClientDeterministicBattleSceneProjection";

    internal static void Run()
    {
        Assert(
            Enum.TryParse<BridgeRuntimeFeature>(FeatureName, out var feature) &&
            Enum.IsDefined(feature),
            "Bridge runtime feature enum does not define " + FeatureName + ".");

        var eoe = CompatibilityRecipeRegistry.FindByRootModule("Europe1700");
        Assert(eoe is not null && eoe.RuntimeFeatures.Contains(feature),
            "Europe1700 recipe does not explicitly require deterministic battle-scene projection.");

        var clientRuntime = ReadEmbeddedRuntime(
            "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Client.dll");
        using var stream = new MemoryStream(clientRuntime, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var projection = FindRequiredType(metadata, ProjectionTypeName);

        var methods = projection.GetMethods()
            .Select(handle => metadata.GetMethodDefinition(handle))
            .ToDictionary(method => metadata.GetString(method.Name), StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "Install",
                     "BeforeCreate",
                     "AfterCreate",
                     "FinalizeCreate",
                     "BeginRandomScopeForSmoke"
                 })
        {
            Assert(methods.ContainsKey(required),
                ProjectionTypeName + " is missing required scope method " + required + ".");
        }
        foreach (var harmonySeam in new[] { "BeforeCreate", "AfterCreate", "FinalizeCreate" })
        {
            var attributes = methods[harmonySeam].Attributes;
            Assert((attributes & MethodAttributes.Public) != 0 &&
                   (attributes & MethodAttributes.Static) != 0,
                ProjectionTypeName + "." + harmonySeam +
                " must remain a public static Harmony seam.");
        }

        var selectionScope = FindRequiredType(metadata, ProjectionTypeName + "+SelectionScope");
        Assert(selectionScope.GetMethods()
                .Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name))
                .Contains("Dispose", StringComparer.Ordinal),
            "Battle-scene random scope is not disposable for guaranteed restoration.");

        var runtime = FindRequiredType(metadata, "BCS.CoopBridge.BridgeRuntime");
        var runtimeFields = runtime.GetFields()
            .Select(handle => metadata.GetString(metadata.GetFieldDefinition(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
        Assert(runtimeFields.Contains("SupportedRuntimeFeatures") &&
               runtimeFields.Contains("HistoricalRuntimeFeatures"),
            "Bridge runtime does not freeze schema-1 features separately from the supported set.");

        AssertPersistentBattleSceneLogging(
            pe,
            metadata,
            projection,
            runtime);

        Assert(ContainsText(clientRuntime,
                   "GameInterface.Services.MapEvents.FieldBattleMissionInitializer") &&
               ContainsText(clientRuntime, "Create") &&
               ContainsText(clientRuntime, "client-deterministic-battle-scene-projection"),
            "Packaged client runtime does not bind the exact Coop " +
            "FieldBattleMissionInitializer.Create seam.");
        Assert(ContainsText(clientRuntime, "contractCandidateCount") &&
               ContainsText(clientRuntime, "contractCandidates") &&
               !ContainsText(clientRuntime, "actualEligibleCandidateCount"),
            "Battle-scene resolution diagnostics overclaim native candidate eligibility.");

        var serverRuntime = ReadEmbeddedRuntime(
            "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Server.dll");
        using var serverStream = new MemoryStream(serverRuntime, writable: false);
        using var serverPe = new PEReader(serverStream);
        var serverMetadata = serverPe.GetMetadataReader();
        Assert(!TryFindType(serverMetadata, ProjectionTypeName, out _),
            "Server bridge runtime contains the client-only battle-scene projection hook.");
        var serverBridgeRuntime = FindRequiredType(
            serverMetadata,
            "BCS.CoopBridge.BridgeRuntime");
        AssertCrossProcessPersistentSinkSerialization(
            pe,
            metadata,
            runtime,
            serverPe,
            serverMetadata,
            serverBridgeRuntime);
    }

    private static void AssertCrossProcessPersistentSinkSerialization(
        PEReader clientPe,
        MetadataReader clientMetadata,
        TypeDefinition clientRuntime,
        PEReader serverPe,
        MetadataReader serverMetadata,
        TypeDefinition serverRuntime)
    {
        const string mutexName = "Local\\BCS.CoopBridge.StartupProgress.v1";
        var failures = new List<string>();
        ValidatePersistentSinkMutex(
            "client",
            clientPe,
            clientMetadata,
            clientRuntime,
            mutexName,
            failures);
        ValidatePersistentSinkMutex(
            "server",
            serverPe,
            serverMetadata,
            serverRuntime,
            mutexName,
            failures);
        Assert(failures.Count == 0,
            "Packaged bridge runtimes do not serialize the shared persistent sink " +
            "through the same named cross-process mutex: " +
            string.Join("; ", failures) + ".");
    }

    private static void ValidatePersistentSinkMutex(
        string role,
        PEReader pe,
        MetadataReader metadata,
        TypeDefinition runtime,
        string mutexName,
        ICollection<string> failures)
    {
        var sink = FindRequiredMethod(metadata, runtime, "RecordStartupProgress");
        var method = metadata.GetMethodDefinition(sink);
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        var instructions = ReadInstructions(pe, sink);
        var mutexConstructors = FindMethodIndexes(
            metadata,
            instructions,
            "System.Threading.Mutex",
            ".ctor",
            OpCodes.Newobj);
        var waitCalls = FindMethodIndexes(
            metadata,
            instructions,
            null,
            "WaitOne",
            OpCodes.Call,
            OpCodes.Callvirt);
        var appendCalls = FindMethodIndexes(
            metadata,
            instructions,
            "System.IO.File",
            "AppendAllText",
            OpCodes.Call);
        var releaseCalls = FindMethodIndexes(
            metadata,
            instructions,
            "System.Threading.Mutex",
            "ReleaseMutex",
            OpCodes.Call,
            OpCodes.Callvirt);

        var mutexNameIndex = mutexConstructors.Count == 1
            ? PreviousMeaningfulInstruction(instructions, mutexConstructors[0] - 1)
            : -1;
        if (mutexConstructors.Count != 1 ||
            mutexNameIndex < 0 ||
            !LoadsExactString(
                metadata,
                new[] { instructions[mutexNameIndex] },
                mutexName))
        {
            failures.Add(role + " sink does not construct the exact named Mutex " + mutexName);
            return;
        }
        if (waitCalls.Count != 1 || appendCalls.Count != 2 || releaseCalls.Count != 1)
        {
            failures.Add(
                role + " sink does not contain one timed WaitOne, distinct central/fallback " +
                "AppendAllText calls, and one ReleaseMutex");
            return;
        }

        var constructorIndex = mutexConstructors[0];
        var waitIndex = waitCalls[0];
        var releaseIndex = releaseCalls[0];
        var constructorOffset = instructions[constructorIndex].Offset;
        var releaseOffset = instructions[releaseIndex].Offset;
        var constructorStoreIndex = NextMeaningfulInstruction(
            instructions,
            constructorIndex + 1);
        var waitTimeoutIndex = PreviousMeaningfulInstruction(instructions, waitIndex - 1);
        var waitLoadIndex = PreviousMeaningfulInstruction(instructions, waitTimeoutIndex - 1);
        var waitResultStoreIndex = NextMeaningfulInstruction(instructions, waitIndex + 1);
        var releaseLoadIndex = PreviousMeaningfulInstruction(instructions, releaseIndex - 1);
        var mutexLocal = constructorStoreIndex >= 0
            ? GetStoredLocal(instructions[constructorStoreIndex])
            : null;
        var acquiredLocal = waitResultStoreIndex >= 0
            ? GetStoredLocal(instructions[waitResultStoreIndex])
            : null;

        if (mutexLocal is null ||
            waitTimeoutIndex < 0 ||
            !TryGetLoadedInt32(instructions[waitTimeoutIndex], out var waitTimeout) ||
            waitTimeout != 100 ||
            waitLoadIndex < 0 || GetLoadedLocal(instructions[waitLoadIndex]) != mutexLocal ||
            acquiredLocal is null)
        {
            failures.Add(
                role + " sink does not use WaitOne(100) and retain its Boolean ownership result");
            return;
        }

        var ownershipBranchIndex = FindBooleanBranchOnLocal(
            instructions,
            waitResultStoreIndex + 1,
            acquiredLocal.Value);
        if (ownershipBranchIndex < 0 ||
            !TryGetBooleanBranchSuccessors(
                instructions,
                ownershipBranchIndex,
                out var ownedPathStart,
                out var timeoutPathStart))
        {
            failures.Add(
                role + " sink does not branch explicitly on a false WaitOne ownership result");
            return;
        }

        var appendTargets = appendCalls.ToHashSet();
        var ownedAppends = FindFirstReachableTargets(
            instructions,
            ownedPathStart,
            appendTargets);
        var timeoutAppends = FindFirstReachableTargets(
            instructions,
            timeoutPathStart,
            appendTargets);
        if (ownedAppends.Count != 1 || timeoutAppends.Count != 1 ||
            ownedAppends.SetEquals(timeoutAppends))
        {
            failures.Add(
                role + " sink false WaitOne branch does not bypass the central append " +
                "for a distinct fallback append");
            return;
        }

        var appendIndex = ownedAppends.Single();
        var fallbackAppendIndex = timeoutAppends.Single();
        var appendOffset = instructions[appendIndex].Offset;
        var fallbackAppendOffset = instructions[fallbackAppendIndex].Offset;
        var hasProcessId =
            (FindMethodIndexes(
                 metadata,
                 instructions,
                 "System.Diagnostics.Process",
                 "GetCurrentProcess",
                 OpCodes.Call).Count == 1 &&
             FindMethodIndexes(
                 metadata,
                 instructions,
                 "System.Diagnostics.Process",
                 "get_Id",
                 OpCodes.Call,
                 OpCodes.Callvirt).Count == 1) ||
            FindMethodIndexes(
                metadata,
                instructions,
                "System.Environment",
                "get_ProcessId",
                OpCodes.Call).Count == 1;
        if (!LoadsStringContaining(
                metadata,
                instructions,
                "bcs-coop-bridge-startup-progress-") ||
            !LoadsStringContaining(metadata, instructions, ".fallback.log") ||
            !hasProcessId)
        {
            failures.Add(
                role + " sink timeout path does not append to the PID-scoped fallback log");
        }

        if (releaseLoadIndex < 0 ||
            GetLoadedLocal(instructions[releaseLoadIndex]) != mutexLocal ||
            !(constructorIndex < waitIndex && appendOffset < releaseOffset))
        {
            failures.Add(
                role + " sink does not append under and release the same named Mutex");
        }

        var releaseIsFinallyProtected = body.ExceptionRegions.Any(region =>
            region.Kind == ExceptionRegionKind.Finally &&
            appendOffset >= region.TryOffset &&
            appendOffset < region.TryOffset + region.TryLength &&
            releaseOffset >= region.HandlerOffset &&
            releaseOffset < region.HandlerOffset + region.HandlerLength);
        if (!releaseIsFinallyProtected)
            failures.Add(role + " sink does not release its named Mutex from a finally handler");

        if (!IsProtectedByNonThrowingExceptionCatch(
                metadata,
                body,
                instructions,
                constructorOffset) ||
            !IsProtectedByNonThrowingExceptionCatch(
                metadata,
                body,
                instructions,
                appendOffset) ||
            !IsProtectedByNonThrowingExceptionCatch(
                metadata,
                body,
                instructions,
                fallbackAppendOffset))
        {
            failures.Add(role + " sink no longer keeps Mutex or file failures non-fatal");
        }
    }

    private static void AssertPersistentBattleSceneLogging(
        PEReader pe,
        MetadataReader metadata,
        TypeDefinition projection,
        TypeDefinition runtime)
    {
        const string activationPrefix =
            "BATTLE_SCENE_PROJECTION_ACTIVATED ";
        var resolutionPrefixes = new[]
        {
            "BATTLE_SCENE_RESOLUTION ",
            "[BCS Coop Bridge] BATTLE_SCENE_RESOLUTION "
        };

        var install = FindRequiredMethod(metadata, projection, "Install");
        var writeResolutionRecord = FindRequiredMethod(
            metadata,
            projection,
            "WriteResolutionRecord");
        var persistentSink = FindRequiredMethod(
            metadata,
            runtime,
            "RecordStartupProgress");
        var persistentSinkToken = MetadataTokens.GetToken(persistentSink);

        var installInstructions = ReadInstructions(pe, install);
        var resolutionInstructions = ReadInstructions(pe, writeResolutionRecord);
        var installCalls = FindCallIndexes(installInstructions, persistentSinkToken);
        var resolutionCalls = FindCallIndexes(resolutionInstructions, persistentSinkToken);
        var failures = new List<string>();
        var activationJsonFragments = new[]
        {
            "{\"schema\":1",
            ",\"bridgeId\":",
            ",\"target\":",
            ",\"catalogContractSha256\":",
            ",\"parity\":",
            ",\"contentMismatchCount\":"
        };

        if (installCalls.Count != 1 ||
            !ExactConstructedLineFlowsToCall(
                metadata,
                installInstructions,
                installCalls.Count == 1 ? installCalls[0] : -1,
                new[] { activationPrefix }) ||
            activationJsonFragments.Any(fragment =>
                !LoadsStringContaining(metadata, installInstructions, fragment)))
        {
            failures.Add(
                "exact BATTLE_SCENE_PROJECTION_ACTIVATED JSON is not written " +
                "exactly once through BridgeRuntime.RecordStartupProgress");
        }

        if (resolutionCalls.Count != 1)
        {
            failures.Add(
                "BATTLE_SCENE_RESOLUTION is not written exactly once through " +
                "BridgeRuntime.RecordStartupProgress");
        }
        else
        {
            var callIndex = resolutionCalls[0];
            if (!ExactConstructedLineFlowsToCall(
                    metadata,
                    resolutionInstructions,
                    callIndex,
                    resolutionPrefixes))
            {
                failures.Add(
                    "persistent BATTLE_SCENE_RESOLUTION logging does not receive the " +
                    "already-built exact JSON line");
            }

            var body = pe.GetMethodBody(
                metadata.GetMethodDefinition(writeResolutionRecord).RelativeVirtualAddress);
            var callOffset = resolutionInstructions[callIndex].Offset;
            if (!IsProtectedByNonThrowingExceptionCatch(
                    metadata,
                    body,
                    resolutionInstructions,
                    callOffset))
            {
                failures.Add(
                    "persistent BATTLE_SCENE_RESOLUTION logging can escape into mission startup");
            }
        }

        var sinkBody = pe.GetMethodBody(
            metadata.GetMethodDefinition(persistentSink).RelativeVirtualAddress);
        var sinkInstructions = ReadInstructions(pe, persistentSink);
        if (!HasNonThrowingExceptionCatch(metadata, sinkBody, sinkInstructions))
        {
            failures.Add(
                "BridgeRuntime.RecordStartupProgress does not keep persistence failures non-fatal");
        }

        Assert(failures.Count == 0,
            "Packaged client runtime lacks durable deterministic battle-scene diagnostics: " +
            string.Join("; ", failures) + ".");
    }

    private static MethodDefinitionHandle FindRequiredMethod(
        MetadataReader metadata,
        TypeDefinition type,
        string name)
    {
        var matches = type.GetMethods()
            .Where(handle => string.Equals(
                metadata.GetString(metadata.GetMethodDefinition(handle).Name),
                name,
                StringComparison.Ordinal))
            .ToArray();
        Assert(matches.Length == 1,
            "Packaged client runtime must contain exactly one " + name + " method.");
        return matches[0];
    }

    private static List<int> FindCallIndexes(
        IReadOnlyList<IlInstruction> instructions,
        int targetToken)
    {
        var result = new List<int>();
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if ((instruction.OpCode == OpCodes.Call ||
                 instruction.OpCode == OpCodes.Callvirt) &&
                instruction.Operand == targetToken)
            {
                result.Add(index);
            }
        }
        return result;
    }

    private static List<int> FindMethodIndexes(
        MetadataReader metadata,
        IReadOnlyList<IlInstruction> instructions,
        string? declaringType,
        string methodName,
        params OpCode[] permittedOpCodes)
    {
        var result = new List<int>();
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (!permittedOpCodes.Contains(instruction.OpCode) ||
                instruction.Operand is not int token ||
                !TryDescribeMethod(
                    metadata,
                    token,
                    out var actualDeclaringType,
                    out var actualMethodName))
            {
                continue;
            }
            if (string.Equals(actualMethodName, methodName, StringComparison.Ordinal) &&
                (declaringType is null || string.Equals(
                    actualDeclaringType,
                    declaringType,
                    StringComparison.Ordinal)))
            {
                result.Add(index);
            }
        }
        return result;
    }

    private static bool TryDescribeMethod(
        MetadataReader metadata,
        int token,
        out string declaringType,
        out string methodName)
    {
        var handle = MetadataTokens.Handle(token);
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            declaringType = GetFullTypeName(
                metadata,
                metadata.GetTypeDefinition(method.GetDeclaringType()));
            methodName = metadata.GetString(method.Name);
            return true;
        }
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            declaringType = GetTypeFullName(metadata, member.Parent);
            methodName = metadata.GetString(member.Name);
            return declaringType.Length != 0;
        }
        declaringType = string.Empty;
        methodName = string.Empty;
        return false;
    }

    private static string GetTypeFullName(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            var typeNamespace = metadata.GetString(type.Namespace);
            var name = metadata.GetString(type.Name);
            return typeNamespace.Length == 0 ? name : typeNamespace + "." + name;
        }
        if (handle.Kind == HandleKind.TypeDefinition)
            return GetFullTypeName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)handle));
        return string.Empty;
    }

    private static bool LoadsExactString(
        MetadataReader metadata,
        IReadOnlyList<IlInstruction> instructions,
        string expected)
    {
        return instructions.Any(instruction =>
            instruction.OpCode == OpCodes.Ldstr &&
            instruction.Operand is int token &&
            string.Equals(
                metadata.GetUserString(
                    MetadataTokens.UserStringHandle(token & 0x00FFFFFF)),
                expected,
                StringComparison.Ordinal));
    }

    private static bool LoadsStringContaining(
        MetadataReader metadata,
        IReadOnlyList<IlInstruction> instructions,
        string expectedFragment)
    {
        return instructions.Any(instruction =>
            instruction.OpCode == OpCodes.Ldstr &&
            instruction.Operand is int token &&
            metadata.GetUserString(
                    MetadataTokens.UserStringHandle(token & 0x00FFFFFF))
                .Contains(expectedFragment, StringComparison.Ordinal));
    }

    private static bool ExactConstructedLineFlowsToCall(
        MetadataReader metadata,
        IReadOnlyList<IlInstruction> instructions,
        int callIndex,
        IReadOnlyList<string> exactPrefixes)
    {
        var argumentIndex = PreviousMeaningfulInstruction(instructions, callIndex - 1);
        if (argumentIndex < 0)
            return false;

        if (IsExactPrefixConcat(
                metadata,
                instructions,
                argumentIndex,
                exactPrefixes))
        {
            return true;
        }

        if (GetLoadedLocal(instructions[argumentIndex]) is not int lineLocal)
        {
            return false;
        }

        for (var index = argumentIndex - 1; index >= 0; index--)
        {
            if (GetStoredLocal(instructions[index]) != lineLocal)
                continue;

            var concatIndex = PreviousMeaningfulInstruction(instructions, index - 1);
            return concatIndex >= 0 &&
                   IsExactPrefixConcat(
                       metadata,
                       instructions,
                       concatIndex,
                       exactPrefixes);
        }
        return false;
    }

    private static bool IsExactPrefixConcat(
        MetadataReader metadata,
        IReadOnlyList<IlInstruction> instructions,
        int concatIndex,
        IReadOnlyList<string> exactPrefixes)
    {
        if (!IsNamedMethodCall(metadata, instructions[concatIndex], "Concat"))
            return false;
        var recordIndex = PreviousMeaningfulInstruction(instructions, concatIndex - 1);
        var prefixIndex = PreviousMeaningfulInstruction(instructions, recordIndex - 1);
        return recordIndex >= 0 &&
               GetLoadedLocal(instructions[recordIndex]) is not null &&
               prefixIndex >= 0 &&
               exactPrefixes.Any(prefix => LoadsExactString(
                   metadata,
                   new[] { instructions[prefixIndex] },
                   prefix));
    }

    private static int PreviousMeaningfulInstruction(
        IReadOnlyList<IlInstruction> instructions,
        int index)
    {
        while (index >= 0 && instructions[index].OpCode == OpCodes.Nop)
            index--;
        return index;
    }

    private static int NextMeaningfulInstruction(
        IReadOnlyList<IlInstruction> instructions,
        int index)
    {
        while (index < instructions.Count && instructions[index].OpCode == OpCodes.Nop)
            index++;
        return index < instructions.Count ? index : -1;
    }

    private static bool TryGetLoadedInt32(IlInstruction instruction, out int value)
    {
        if (instruction.OpCode == OpCodes.Ldc_I4_M1) value = -1;
        else if (instruction.OpCode == OpCodes.Ldc_I4_0) value = 0;
        else if (instruction.OpCode == OpCodes.Ldc_I4_1) value = 1;
        else if (instruction.OpCode == OpCodes.Ldc_I4_2) value = 2;
        else if (instruction.OpCode == OpCodes.Ldc_I4_3) value = 3;
        else if (instruction.OpCode == OpCodes.Ldc_I4_4) value = 4;
        else if (instruction.OpCode == OpCodes.Ldc_I4_5) value = 5;
        else if (instruction.OpCode == OpCodes.Ldc_I4_6) value = 6;
        else if (instruction.OpCode == OpCodes.Ldc_I4_7) value = 7;
        else if (instruction.OpCode == OpCodes.Ldc_I4_8) value = 8;
        else if (instruction.OpCode == OpCodes.Ldc_I4 && instruction.Operand is int inline)
            value = inline;
        else if (instruction.OpCode == OpCodes.Ldc_I4_S && instruction.Operand is int shortInline)
            value = unchecked((sbyte)shortInline);
        else
        {
            value = default;
            return false;
        }
        return true;
    }

    private static int FindBooleanBranchOnLocal(
        IReadOnlyList<IlInstruction> instructions,
        int startIndex,
        int local)
    {
        for (var index = startIndex; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCodes.Brtrue &&
                instruction.OpCode != OpCodes.Brtrue_S &&
                instruction.OpCode != OpCodes.Brfalse &&
                instruction.OpCode != OpCodes.Brfalse_S)
            {
                continue;
            }
            var loadIndex = PreviousMeaningfulInstruction(instructions, index - 1);
            if (loadIndex >= 0 && GetLoadedLocal(instructions[loadIndex]) == local)
                return index;
        }
        return -1;
    }

    private static bool TryGetBooleanBranchSuccessors(
        IReadOnlyList<IlInstruction> instructions,
        int branchIndex,
        out int trueSuccessor,
        out int falseSuccessor)
    {
        var branch = instructions[branchIndex];
        if (branch.Operand is not int targetOffset)
        {
            trueSuccessor = -1;
            falseSuccessor = -1;
            return false;
        }
        var target = FindInstructionIndexByOffset(instructions, targetOffset);
        var fallThrough = NextMeaningfulInstruction(instructions, branchIndex + 1);
        if (target < 0 || fallThrough < 0)
        {
            trueSuccessor = -1;
            falseSuccessor = -1;
            return false;
        }
        if (branch.OpCode == OpCodes.Brtrue || branch.OpCode == OpCodes.Brtrue_S)
        {
            trueSuccessor = target;
            falseSuccessor = fallThrough;
            return true;
        }
        if (branch.OpCode == OpCodes.Brfalse || branch.OpCode == OpCodes.Brfalse_S)
        {
            trueSuccessor = fallThrough;
            falseSuccessor = target;
            return true;
        }
        trueSuccessor = -1;
        falseSuccessor = -1;
        return false;
    }

    private static HashSet<int> FindFirstReachableTargets(
        IReadOnlyList<IlInstruction> instructions,
        int startIndex,
        ISet<int> targets)
    {
        var result = new HashSet<int>();
        var pending = new Queue<int>();
        var visited = new HashSet<int>();
        pending.Enqueue(startIndex);
        while (pending.Count != 0)
        {
            var index = pending.Dequeue();
            if (index < 0 || index >= instructions.Count || !visited.Add(index))
                continue;
            if (targets.Contains(index))
            {
                result.Add(index);
                continue;
            }

            var instruction = instructions[index];
            if (instruction.OpCode.FlowControl == FlowControl.Return ||
                instruction.OpCode.FlowControl == FlowControl.Throw ||
                instruction.OpCode == OpCodes.Endfinally)
            {
                continue;
            }
            if (instruction.OpCode.FlowControl == FlowControl.Branch)
            {
                if (instruction.Operand is int branchOffset)
                    pending.Enqueue(FindInstructionIndexByOffset(instructions, branchOffset));
                continue;
            }
            if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
            {
                if (instruction.Operand is int branchOffset)
                    pending.Enqueue(FindInstructionIndexByOffset(instructions, branchOffset));
                pending.Enqueue(index + 1);
                continue;
            }
            pending.Enqueue(index + 1);
        }
        return result;
    }

    private static int FindInstructionIndexByOffset(
        IReadOnlyList<IlInstruction> instructions,
        int offset)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Offset == offset)
                return index;
        }
        return -1;
    }

    private static int? GetLoadedLocal(IlInstruction instruction)
    {
        if (instruction.OpCode == OpCodes.Ldloc_0) return 0;
        if (instruction.OpCode == OpCodes.Ldloc_1) return 1;
        if (instruction.OpCode == OpCodes.Ldloc_2) return 2;
        if (instruction.OpCode == OpCodes.Ldloc_3) return 3;
        if (instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S)
            return instruction.Operand;
        return null;
    }

    private static int? GetStoredLocal(IlInstruction instruction)
    {
        if (instruction.OpCode == OpCodes.Stloc_0) return 0;
        if (instruction.OpCode == OpCodes.Stloc_1) return 1;
        if (instruction.OpCode == OpCodes.Stloc_2) return 2;
        if (instruction.OpCode == OpCodes.Stloc_3) return 3;
        if (instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S)
            return instruction.Operand;
        return null;
    }

    private static bool IsNamedMethodCall(
        MetadataReader metadata,
        IlInstruction instruction,
        string name)
    {
        if ((instruction.OpCode != OpCodes.Call &&
             instruction.OpCode != OpCodes.Callvirt) ||
            instruction.Operand is not int token)
        {
            return false;
        }
        var handle = MetadataTokens.Handle(token);
        var actualName = handle.Kind switch
        {
            HandleKind.MethodDefinition => metadata.GetString(
                metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
            HandleKind.MemberReference => metadata.GetString(
                metadata.GetMemberReference((MemberReferenceHandle)handle).Name),
            _ => string.Empty
        };
        return string.Equals(actualName, name, StringComparison.Ordinal);
    }

    private static bool IsProtectedByNonThrowingExceptionCatch(
        MetadataReader metadata,
        MethodBodyBlock body,
        IReadOnlyList<IlInstruction> instructions,
        int protectedOffset)
    {
        return body.ExceptionRegions.Any(region =>
            region.Kind == ExceptionRegionKind.Catch &&
            IsNonFatalCatchType(metadata, region.CatchType) &&
            protectedOffset >= region.TryOffset &&
            protectedOffset < region.TryOffset + region.TryLength &&
            HandlerDoesNotThrow(instructions, region));
    }

    private static bool HasNonThrowingExceptionCatch(
        MetadataReader metadata,
        MethodBodyBlock body,
        IReadOnlyList<IlInstruction> instructions)
    {
        return body.ExceptionRegions.Any(region =>
            region.Kind == ExceptionRegionKind.Catch &&
            IsNonFatalCatchType(metadata, region.CatchType) &&
            HandlerDoesNotThrow(instructions, region));
    }

    private static bool HandlerDoesNotThrow(
        IReadOnlyList<IlInstruction> instructions,
        ExceptionRegion region)
    {
        return !instructions.Any(instruction =>
            instruction.Offset >= region.HandlerOffset &&
            instruction.Offset < region.HandlerOffset + region.HandlerLength &&
            (instruction.OpCode == OpCodes.Throw || instruction.OpCode == OpCodes.Rethrow));
    }

    private static bool IsNonFatalCatchType(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            var name = metadata.GetString(type.Name);
            return string.Equals(metadata.GetString(type.Namespace), "System", StringComparison.Ordinal) &&
                   (string.Equals(name, "Exception", StringComparison.Ordinal) ||
                    string.Equals(name, "Object", StringComparison.Ordinal));
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            var name = metadata.GetString(type.Name);
            return string.Equals(metadata.GetString(type.Namespace), "System", StringComparison.Ordinal) &&
                   (string.Equals(name, "Exception", StringComparison.Ordinal) ||
                    string.Equals(name, "Object", StringComparison.Ordinal));
        }
        return false;
    }

    private static IReadOnlyList<IlInstruction> ReadInstructions(
        PEReader pe,
        MethodDefinitionHandle methodHandle)
    {
        var method = pe.GetMetadataReader().GetMethodDefinition(methodHandle);
        Assert(method.RelativeVirtualAddress != 0,
            "Packaged runtime method has no IL body.");
        var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ??
                    throw new InvalidDataException("Packaged runtime method has no IL bytes.");
        var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));
        var result = new List<IlInstruction>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var instructionOffset = offset;
            ushort value = bytes[offset++];
            if (value == 0xFE)
                value = (ushort)(0xFE00 | bytes[offset++]);
            var opCode = opCodes[value];
            int? operand = null;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    operand = bytes[offset++];
                    break;
                case OperandType.ShortInlineBrTarget:
                    var shortDelta = unchecked((sbyte)bytes[offset++]);
                    operand = offset + shortDelta;
                    break;
                case OperandType.InlineVar:
                    operand = BitConverter.ToUInt16(bytes, offset);
                    offset += 2;
                    break;
                case OperandType.InlineI:
                case OperandType.ShortInlineR:
                case OperandType.InlineString:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    operand = BitConverter.ToInt32(bytes, offset);
                    offset += 4;
                    break;
                case OperandType.InlineBrTarget:
                    var delta = BitConverter.ToInt32(bytes, offset);
                    offset += 4;
                    operand = offset + delta;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(bytes, offset);
                    offset += 4 + count * 4;
                    break;
                default:
                    throw new InvalidDataException(
                        "Unsupported IL operand type: " + opCode.OperandType + ".");
            }
            result.Add(new IlInstruction(instructionOffset, opCode, operand));
        }
        return result;
    }

    private readonly record struct IlInstruction(
        int Offset,
        OpCode OpCode,
        int? Operand);

    private static byte[] ReadEmbeddedRuntime(string resourceName)
    {
        using var resource = typeof(CoopBridgePackageBuilder).Assembly
            .GetManifestResourceStream(resourceName);
        Assert(resource is not null,
            "Manager assembly is missing embedded client bridge runtime.");
        using var buffer = new MemoryStream();
        resource!.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static TypeDefinition FindRequiredType(MetadataReader metadata, string fullName)
    {
        Assert(TryFindType(metadata, fullName, out var type),
            "Packaged client runtime does not contain " + fullName + ".");
        return type;
    }

    private static bool TryFindType(
        MetadataReader metadata,
        string fullName,
        out TypeDefinition result)
    {
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var candidate = GetFullTypeName(metadata, type);
            if (candidate.Equals(fullName, StringComparison.Ordinal))
            {
                result = type;
                return true;
            }
        }
        result = default;
        return false;
    }

    private static string GetFullTypeName(MetadataReader metadata, TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return GetFullTypeName(metadata, metadata.GetTypeDefinition(declaring)) + "+" + name;
        var typeNamespace = metadata.GetString(type.Namespace);
        return typeNamespace.Length == 0 ? name : typeNamespace + "." + name;
    }

    private static bool ContainsText(byte[] bytes, string value) =>
        bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0 ||
        bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)) >= 0;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
