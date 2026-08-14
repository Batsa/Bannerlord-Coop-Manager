using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata.Ecma335;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml;
using BCSTool;
using BCSTool.Models;
using BCSTool.Services;

if (args is ["--module-manager-change-set", var moduleManagerPath, var moduleManagerRole])
{
    Console.WriteLine(
        CoopCompatibilityPatcher.ReadModuleManagerChangeSet(
            Path.GetFullPath(moduleManagerPath),
            moduleManagerRole));
    return 0;
}

if (args is ["--metadata-memberrefs", var memberAssemblyPath, var memberPattern])
{
    using var memberStream = File.OpenRead(Path.GetFullPath(memberAssemblyPath));
    using var memberPe = new PEReader(memberStream);
    var memberMetadata = memberPe.GetMetadataReader();
    foreach (var handle in memberMetadata.MemberReferences)
    {
        var description = DescribeMemberReference(
            memberMetadata,
            memberMetadata.GetMemberReference(handle));
        if (description.Contains(memberPattern, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(
                "MEMBER=" + description +
                "|TOKEN=0x" + MetadataTokens.GetToken(handle).ToString("X8"));
    }
    for (var row = 1;
         row <= memberMetadata.GetTableRowCount(TableIndex.MethodSpec);
         row++)
    {
        var handle = MetadataTokens.MethodSpecificationHandle(row);
        var specification = memberMetadata.GetMethodSpecification(handle);
        var description = DescribeMetadataToken(
            memberMetadata,
            MetadataTokens.GetToken(specification.Method));
        if (description.Contains(memberPattern, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(
                "METHOD_SPEC=" + description +
                "|TOKEN=0x" + MetadataTokens.GetToken(handle).ToString("X8") +
                "|SIGNATURE=" + Convert.ToHexString(
                    memberMetadata.GetBlobBytes(specification.Signature)));
    }
    return 0;
}

if (args is ["--metadata-methods", var metadataAssemblyPath, var metadataPattern])
{
    using var metadataStream = File.OpenRead(Path.GetFullPath(metadataAssemblyPath));
    using var metadataPe = new PEReader(metadataStream);
    var metadata = metadataPe.GetMetadataReader();
    foreach (var handle in metadata.TypeDefinitions)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var typeName = metadata.GetString(definition.Namespace) + "." +
                       metadata.GetString(definition.Name);
        if (!typeName.Contains(metadataPattern, StringComparison.OrdinalIgnoreCase))
            continue;
        Console.WriteLine(
            "TYPE=" + typeName +
            "|TOKEN=0x" + MetadataTokens.GetToken(handle).ToString("X8") +
            "|MVID=" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
        foreach (var methodHandle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            Console.WriteLine(
                "METHOD=" + metadata.GetString(method.Name) +
                "|PARAMS=" + method.GetParameters().Count +
                "|PARAM_NAMES=" + string.Join(",", method.GetParameters()
                    .Select(parameterHandle => metadata.GetString(
                        metadata.GetParameter(parameterHandle).Name))) +
                "|TOKEN=0x" + MetadataTokens.GetToken(methodHandle).ToString("X8") +
                "|RVA=" + method.RelativeVirtualAddress +
                "|ATTRS=" + method.Attributes +
                "|SIGNATURE=" + Convert.ToHexString(metadata.GetBlobBytes(method.Signature)));
        }
    }
    return 0;
}

if (args is ["--metadata-il", var ilAssemblyPath, var ilTypePattern, var ilMethodName])
{
    using var ilStream = File.OpenRead(Path.GetFullPath(ilAssemblyPath));
    using var ilPe = new PEReader(ilStream);
    var metadata = ilPe.GetMetadataReader();
    var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));
    foreach (var handle in metadata.TypeDefinitions)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var typeName = metadata.GetString(definition.Namespace) + "." +
                       metadata.GetString(definition.Name);
        if (!typeName.Contains(ilTypePattern, StringComparison.OrdinalIgnoreCase))
            continue;
        foreach (var methodHandle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (!metadata.GetString(method.Name).Equals(ilMethodName, StringComparison.Ordinal) ||
                method.RelativeVirtualAddress == 0)
                continue;
            Console.WriteLine("TYPE=" + typeName + " METHOD=" + ilMethodName);
            var body = ilPe.GetMethodBody(method.RelativeVirtualAddress);
            var bytes = body.GetILBytes() ?? throw new InvalidDataException("Method body has no IL bytes.");
            var offset = 0;
            while (offset < bytes.Length)
            {
                var instructionOffset = offset;
                ushort value = bytes[offset++];
                if (value == 0xFE)
                    value = (ushort)(0xFE00 | bytes[offset++]);
                var opCode = opCodes[value];
                var operand = ReadIlOperand(
                    opCode.OperandType,
                    bytes,
                    ref offset,
                    metadata);
                Console.WriteLine(
                    instructionOffset.ToString("X4") + " " + opCode.Name +
                    (operand.Length == 0 ? string.Empty : " " + operand));
            }
        }
    }
    return 0;
}

if (args is ["--metadata-callers", var callerAssemblyPath, var callerTokenText])
{
    var targetToken = Convert.ToInt32(callerTokenText.Replace("0x", string.Empty), 16);
    using var callerStream = File.OpenRead(Path.GetFullPath(callerAssemblyPath));
    using var callerPe = new PEReader(callerStream);
    var metadata = callerPe.GetMetadataReader();
    var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));
    foreach (var typeHandle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(typeHandle);
        var typeName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
                continue;
            var bytes = callerPe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            if (bytes is null)
                continue;
            var offset = 0;
            while (offset < bytes.Length)
            {
                var instructionOffset = offset;
                ushort value = bytes[offset++];
                if (value == 0xFE)
                    value = (ushort)(0xFE00 | bytes[offset++]);
                var opCode = opCodes[value];
                var operandOffset = offset;
                _ = ReadIlOperand(opCode.OperandType, bytes, ref offset, metadata);
                if (opCode.OperandType == OperandType.InlineMethod &&
                    BitConverter.ToInt32(bytes, operandOffset) == targetToken)
                {
                    Console.WriteLine(
                        "CALLER=" + typeName + "::" + metadata.GetString(method.Name) +
                        "|IL=" + instructionOffset.ToString("X4") +
                        "|OP=" + opCode.Name);
                }
            }
        }
    }
    return 0;
}

if (args is ["--server-xml-overlay-abi", var objectSystemAssemblyPath, var bridgeAssemblyPath])
{
    VerifyServerXmlOverlayAbi(
        Path.GetFullPath(objectSystemAssemblyPath),
        File.ReadAllBytes(Path.GetFullPath(bridgeAssemblyPath)));
    Console.WriteLine("PASS: server XML overlay runtime resolves the unique ApplyXslt signature.");
    return 0;
}

if (args is ["--compat-revert", var revertManifestPath])
{
    new CoopCompatibilityPatcher().Revert(Path.GetFullPath(revertManifestPath));
    Console.WriteLine("REVERTED=" + Path.GetFullPath(revertManifestPath));
    return 0;
}

if (args is ["--apply-compatibility", var compatibilityServerRoot, var compatibilityModuleId])
{
    var canonicalServerRoot = Path.GetFullPath(compatibilityServerRoot);
    var modules = new ModuleScanner().Scan(
        Path.Combine(canonicalServerRoot, "engine", "Modules"));
    var selected = modules.Single(module =>
        module.Id.Equals(compatibilityModuleId, StringComparison.OrdinalIgnoreCase));
    var patcher = new CoopCompatibilityPatcher();
    var plan = patcher.CreatePlan(selected, modules, canonicalServerRoot);
    Console.WriteLine(
        $"PLAN={plan.PlanId} RULE={plan.RuleId} CAN_APPLY={plan.CanApply}");
    foreach (var blocker in plan.Blockers)
        Console.WriteLine($"BLOCKER={blocker}");
    foreach (var change in plan.Changes)
    {
        Console.WriteLine(
            $"CHANGE={change.Kind}|{change.TargetPath}|{change.Description}");
    }
    if (!plan.CanApply)
        return 2;

    var result = patcher.Apply(plan);
    Console.WriteLine($"APPLIED={result.PlanId}");
    Console.WriteLine($"BACKUP={result.BackupDirectory}");
    return 0;
}

if (args is ["--server-smoke", var smokeExecutable, var smokeSecondsText, var smokeBootstrap])
{
    if (!int.TryParse(smokeSecondsText, out var smokeSeconds) || smokeSeconds is < 10 or > 300)
        throw new ArgumentOutOfRangeException(nameof(smokeSecondsText), "Smoke duration must be 10-300 seconds.");
    var executable = Path.GetFullPath(smokeExecutable);
    var serverRoot = Path.GetDirectoryName(executable)
                     ?? throw new InvalidDataException("Server executable has no parent directory.");
    var preexistingServerProcesses = ProcessIdsUnderRoot(serverRoot);
    var launchPlan = new DedicatedServerLaunchBuilder(
            new ModuleScanner(),
            Path.GetFullPath(smokeBootstrap))
        .Build(executable, serverRoot);
    var startInfo = new ProcessStartInfo
    {
        FileName = launchPlan.ExecutablePath,
        WorkingDirectory = launchPlan.WorkingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in launchPlan.Arguments)
        startInfo.ArgumentList.Add(argument);
    foreach (var variable in launchPlan.Environment)
        startInfo.Environment[variable.Key] = variable.Value;

    var output = new ConcurrentQueue<string>();
    using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    process.OutputDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
            output.Enqueue(eventArgs.Data);
    };
    process.ErrorDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
            output.Enqueue("STDERR: " + eventArgs.Data);
    };
    if (!process.Start())
        throw new InvalidOperationException("Dedicated-server smoke process did not start.");
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    var exited = await Task.Run(() => process.WaitForExit(smokeSeconds * 1000));
    if (!exited)
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }
    StopNewProcessesUnderRoot(serverRoot, preexistingServerProcesses);
    var captured = output.ToArray();
    var launchArguments = launchPlan.Arguments.ToArray();
    var dedicatedServerArgumentIndex = Array.FindIndex(
        launchArguments,
        argument => argument.Equals("/dedicatedcustomserver", StringComparison.Ordinal));
    var configuredPort = dedicatedServerArgumentIndex >= 0 &&
                         dedicatedServerArgumentIndex + 1 < launchArguments.Length
        ? launchArguments[dedicatedServerArgumentIndex + 1]
        : string.Empty;
    var port4200ArgumentCount = launchArguments.Count(argument =>
        argument.Equals("4200", StringComparison.Ordinal));
    const string terrainInstallMarker =
        "[BCS Coop Bridge] Installed server map terrain size 1696x1696 from " +
        "Europe1700/SceneObj/Main_map/scene.xscene.";
    const string weatherEventStackPattern =
        "DefaultMapWeatherModel.GetWeatherEventInPosition";
    const string weatherTerrainStackPattern =
        "DefaultMapWeatherModel.GetWeatherEffectOnTerrainForPosition";
    var terrainInstallMarkerCount = captured.Count(line =>
        line.Contains(terrainInstallMarker, StringComparison.Ordinal));
    var weatherEventStackPatternCount = captured.Count(line =>
        line.Contains(weatherEventStackPattern, StringComparison.Ordinal));
    var weatherTerrainStackPatternCount = captured.Count(line =>
        line.Contains(weatherTerrainStackPattern, StringComparison.Ordinal));
    var weatherFailureStackPatternCount =
        weatherEventStackPatternCount + weatherTerrainStackPatternCount;
    var indexOutOfRangeExceptionCount = captured.Count(line =>
        line.Contains("IndexOutOfRangeException", StringComparison.Ordinal));
    var servingMarkerCount = captured.Count(line =>
        line.Contains("SERVING", StringComparison.Ordinal));
    var signalTerms = new[]
    {
        "BCS Coop Bridge",
        "BCS.CoopBridge",
        "exception",
        "failed",
        "error",
        "could not load",
        "couldn't load"
    };
    var signals = captured.Where(line => signalTerms.Any(term =>
            line.Contains(term, StringComparison.OrdinalIgnoreCase)))
        .TakeLast(200);
    var displayed = captured.Length <= 500
        ? captured.AsEnumerable()
        : captured.Take(100)
            .Concat(["... output truncated; retained error/bridge signals follow ..."])
            .Concat(signals)
            .Concat(["... final 400 lines follow ..."])
            .Concat(captured.TakeLast(400));
    foreach (var line in displayed)
        Console.WriteLine(line);
    Console.WriteLine($"SERVER_SMOKE_PORT_4200_ARGUMENT_COUNT={port4200ArgumentCount}");
    Console.WriteLine($"SERVER_SMOKE_TERRAIN_INSTALL_MARKER_COUNT={terrainInstallMarkerCount}");
    Console.WriteLine($"SERVER_SMOKE_WEATHER_EVENT_STACK_PATTERN_COUNT={weatherEventStackPatternCount}");
    Console.WriteLine($"SERVER_SMOKE_WEATHER_TERRAIN_STACK_PATTERN_COUNT={weatherTerrainStackPatternCount}");
    Console.WriteLine($"SERVER_SMOKE_WEATHER_FAILURE_STACK_PATTERN_COUNT={weatherFailureStackPatternCount}");
    Console.WriteLine($"SERVER_SMOKE_INDEX_OUT_OF_RANGE_EXCEPTION_COUNT={indexOutOfRangeExceptionCount}");
    Console.WriteLine($"SERVER_SMOKE_SERVING_MARKER_COUNT={servingMarkerCount}");

    var isEurope1700BridgeSmoke = launchPlan.ActiveModuleIds.Contains(
                                      "Europe1700",
                                      StringComparer.OrdinalIgnoreCase) &&
                                  launchPlan.ActiveModuleIds.Any(moduleId =>
                                      moduleId.StartsWith(
                                          CoopBridgePackageBuilder.BridgeIdPrefix,
                                          StringComparison.OrdinalIgnoreCase));
    var eoeBridgeFailures = new List<string>();
    if (isEurope1700BridgeSmoke)
    {
        if (!configuredPort.Equals("4200", StringComparison.Ordinal))
        {
            eoeBridgeFailures.Add(
                "EOE bridge smoke launch port was '" +
                (configuredPort.Length == 0 ? "<missing>" : configuredPort) +
                "', expected 4200.");
        }
        if (terrainInstallMarkerCount != 1)
        {
            eoeBridgeFailures.Add(
                $"EOE bridge terrain install marker count was {terrainInstallMarkerCount}, expected 1.");
        }
        if (weatherFailureStackPatternCount > 0)
        {
            eoeBridgeFailures.Add(
                $"EOE bridge smoke captured {weatherFailureStackPatternCount} weather failure stack pattern(s).");
        }
        if (indexOutOfRangeExceptionCount > 0)
        {
            eoeBridgeFailures.Add(
                $"EOE bridge smoke captured {indexOutOfRangeExceptionCount} IndexOutOfRangeException line(s).");
        }
    }
    if (exited)
    {
        Console.Error.WriteLine($"FAIL: dedicated server exited during smoke test with code {process.ExitCode}.");
        return 3;
    }
    if (eoeBridgeFailures.Count > 0)
    {
        foreach (var failure in eoeBridgeFailures)
            Console.Error.WriteLine("FAIL: " + failure);
        return 4;
    }

    Console.WriteLine(
        $"PASS: dedicated server stayed alive for {smokeSeconds} seconds with modules " +
        string.Join(", ", launchPlan.ActiveModuleIds) + ".");
    return 0;
}

if (args is ["--compat-install-apply", var installServerRoot, var workshopModuleRoot])
{
    var scanner = new ModuleScanner();
    var modulesDirectory = Path.Combine(installServerRoot, "engine", "Modules");
    var importer = new ModuleImporter(modulesDirectory, scanner);
    var candidates = importer.Discover([workshopModuleRoot]);
    var imported = importer.Import(candidates);
    var moduleId = imported.Single().Id;
    var modules = scanner.Scan(modulesDirectory);
    var selected = modules.Single(module =>
        module.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
    var patcher = new CoopCompatibilityPatcher();
    var plan = patcher.CreatePlan(selected, modules, installServerRoot);
    Console.WriteLine(plan.Summary);
    if (!plan.CanApply)
        return 2;
    var result = patcher.Apply(plan);
    Console.WriteLine($"Applied compatibility plan {result.PlanId}.");
    Console.WriteLine($"Revert manifest: {result.ManifestPath}");
    return 0;
}

if (args is ["--compat-apply", var applyServerRoot, var applyModuleId])
{
    var scanner = new ModuleScanner();
    var executable = Path.Combine(applyServerRoot, "BannerlordCoopServer.exe");
    var modules = new ModuleManager(executable, scanner).Load();
    var selected = modules.Single(module =>
        module.Id.Equals(applyModuleId, StringComparison.OrdinalIgnoreCase));
    var patcher = new CoopCompatibilityPatcher();
    var plan = patcher.CreatePlan(selected, modules, applyServerRoot);
    Console.WriteLine(plan.Summary);
    if (!plan.CanApply)
        return 2;
    var result = patcher.Apply(plan);
    Console.WriteLine($"Applied compatibility plan {result.PlanId}.");
    Console.WriteLine($"Revert manifest: {result.ManifestPath}");
    return 0;
}

if (args is ["--compat-plan", var serverRootArgument, var moduleIdArgument])
{
    var scanner = new ModuleScanner();
    var modules = scanner.Scan(Path.Combine(serverRootArgument, "engine", "Modules"));
    var selected = modules.Single(module =>
        module.Id.Equals(moduleIdArgument, StringComparison.OrdinalIgnoreCase));
    var plan = new CoopCompatibilityPatcher().CreatePlan(
        selected,
        modules,
        serverRootArgument);
    Console.WriteLine(plan.Summary);
    return plan.Blockers.Count == 0 ? 0 : 2;
}

if (args is ["--compat-analyze", var analysisServerRoot, var analysisModuleId])
{
    var scanner = new ModuleScanner();
    var modules = scanner.Scan(Path.Combine(analysisServerRoot, "engine", "Modules"));
    var selected = modules.Single(module =>
        module.Id.Equals(analysisModuleId, StringComparison.OrdinalIgnoreCase));
    var report = new CoopCompatibilityAnalyzer().Analyze(
        selected,
        modules,
        analysisServerRoot);
    Console.WriteLine(report.ToPlainText());
    return report.OverallStatus == CoopCompatibilityStatus.Blocked ? 2 : 0;
}

var failures = new List<string>();

Run("missing modOptions loads defaults and materializes safely", TestMissingModOptions);
Run("partial modOptions preserves existing content and adds defaults", TestPartialModOptions);
Run("null modOptions loads defaults and materializes safely", TestNullModOptions);
Run("enabled difficulty selections save as active config values", TestDifficultySelectionsSave);
Run("disabled scheduled restarts skip schedule validation", TestDisabledScheduledRestarts);
Run("released Coop player list parses real target IDs", TestCoopPlayerListParser);
Run("Coop-dependent gameplay modules may load after Coop", TestCoopDependentModuleOrderValidation);
Run("content-only module receives conservative compatible result", TestContentOnlyCompatibilityAnalysis);
Run("missing module dependency blocks compatibility", TestMissingDependencyCompatibilityAnalysis);
Run("custom campaign code requires a bridge", TestCustomCampaignCodeCompatibilityAnalysis);
Run("dependency version mismatch requires testing", TestDependencyVersionCompatibilityAnalysis);
Run("compatibility XML parser rejects DTD content", TestCompatibilityAnalyzerRejectsDtd);
Run("saved module profile becomes the real engine token", TestManagedModuleLaunchPlan);
Run("managed dependency profile requires external official runtime", TestManagedDependencyProfile);
Run("managed resolver prioritizes exact dependency identity", TestManagedResolverPrefersExactIdentity);
Run("managed resolver prioritizes released Coop server dependencies", TestManagedResolverPrefersCoopServerDependencies);
Run("invalid enabled load order blocks server launch", TestInvalidManagedModuleLaunchPlan);
Run("ConPTY quotes Windows arguments safely", TestConPtyArgumentQuoting);
Run("managed engine console logging is lossless and rotated", TestServerConsoleLogWriter);
Run("bridge installation recipe and start preflight are scoped", BridgeInstallationRegression.Run);
Run("bridge population settings are scoped, strict, and identity-neutral", BridgePopulationSettingsRegression.Run);
Run("applied content-only module replans without pending state", TestAppliedContentModuleReplansAsNoOp);
Run("content compatibility prepare and revert are lossless", TestContentCompatibilityPrepareAndRevert);
Run("prepared module DLL unblock preserves assembly bytes", TestPreparedModuleDllUnblock);
Run("prepared module DLL unblock restores markers on failure", TestPreparedModuleDllUnblockRollback);
Run("failed compatibility finalization rolls back without a manifest", TestCompatibilityApplyRollsBackBeforeManifestPublication);
Run("compatibility apply rejects files changed after preview", TestCompatibilityPlanRejectsConcurrentChange);
Run("generic bridge package is deterministic and client-ready", TestGenericBridgePackage);
Run("released Coop bridge records only cross-role assemblies", TestReleasedCoopRoleParity);
Run("bridge builder rejects linked module roots and bins", TestBridgeBuilderRejectsLinkedModulePaths);
Run("bridge builder rejects module DLL identity mismatch", TestBridgeBuilderRejectsAssemblyIdentityMismatch);
Run("bridge builder ignores native support DLLs", TestBridgeBuilderIgnoresNativeSupportDll);
Run("non-Coop bridge records only declared DLLs", TestBridgeBuilderOmitsUndeclaredManagedSidecars);
Run("bridge game-version compatibility is version-scoped", TestBridgeGameVersionCompatibility);
Run("ModuleManager semantic game revisions are observed fail closed", TestModuleManagerSemanticRevisionReader);
Run("bridge authority rules are module-bound", TestBridgeAuthorityRuleConfiguration);
Run("bridge server file redirects require safe existing sources", TestBridgeServerFileRedirectConfiguration);
Run("bridge server XML overlays require safe source and payload", TestBridgeServerXmlOverlayConfiguration);
Run("bridge server map terrain size is version-scoped and fail closed", TestBridgeServerMapTerrainSizeConfiguration);
Run("bridge runtime features are explicit and target scoped", TestBridgeRuntimeFeatureConfiguration);
Run("bridge excludes campaign seeding and save rewriting", TestBridgeExcludesCampaignIntervention);
Run("generic executable preparation projects DLL and creates bridge", TestGenericExecutablePrepareAndRevert);
Run("prepared profile normalizes dependency order", TestPreparedProfileNormalizesDependencyOrder);
Run("prepared profile honors framework-before-Native metadata", TestFrameworkBeforeNativePreparationOrder);
Run("unsafe declared DLL path is rejected", TestUnsafeDeclaredDllRejected);
Run("client-only and wildcard dependencies analyze correctly", TestClientOnlyAndWildcardDependencies);
Run("EOE headless action projection uses native server animations", TestEurope1700HeadlessActionProjection);
Run("EOE headless action types repair the bomb reload ID", TestEurope1700HeadlessActionTypeProjection);
Run("EOE malformed trebuchet prefab receives exact syntax repair", TestEurope1700TrebuchetPrefabRepair);
Run("EOE repeated Weapon schema repair is semantic and fail closed", TestEurope1700ItemsSchemaRepair);
Run("EOE dedicated-server schema repairs are exact and fail closed", TestEurope1700SchemaRepairs);
Run("EOE optional server DLLs follow active manifest declarations", TestEurope1700OptionalServerDllSelection);
Run("compatibility rules accept only verified Coop releases", TestSupportedReleasedCoopVersions);
Run("campaign save discovery hides Coop-owned backup generations", TestCampaignSaveDiscovery);
Run("launcher delegates campaign save backups to Coop", TestLauncherDelegatesCampaignBackupsToCoop);
Run("client campaign imports never overwrite server saves", TestClientSaveImport);
Run("Coop save names stay safe across config and startup", TestCoopSafeServerSaveNames);
Run("Coop port guard distinguishes UDP from TCP", TestCoopUdpPortGuard);
Run("module-row text supports visual ancestor lookup", TestModuleRowTextAncestorLookup);
Run("application branding uses Bannerlord Coop Manager", TestApplicationBranding);

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All Bannerlord Coop Manager regression checks passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}{Environment.NewLine}{exception}");
    }
}

void TestModuleRowTextAncestorLookup()
{
    var findAncestor = typeof(ModManagerWindow)
        .GetMethod("FindAncestor", BindingFlags.NonPublic | BindingFlags.Static)
        ?.MakeGenericMethod(typeof(ListBoxItem))
        ?? throw new InvalidOperationException("Mod manager ancestor helper was not found.");

    var result = findAncestor.Invoke(null, [new Run("Europe1700")]);
    Assert(result is null, "A detached text Run should have no ListBoxItem ancestor.");
}

void TestApplicationBranding()
{
    var applicationAssembly = typeof(BCSTool.ViewModels.MainViewModel).Assembly;
    Assert(
        applicationAssembly.GetName().Name == "Bannerlord Coop Manager",
        "Application assembly still uses the previous product name.");
    Assert(
        BCSTool.Infrastructure.AppVersion.DisplayName.StartsWith(
            "Bannerlord Coop Manager ",
            StringComparison.Ordinal),
        "Application version title still uses the previous product name.");
}

string ReadIlOperand(
    OperandType operandType,
    byte[] bytes,
    ref int offset,
    MetadataReader metadata)
{
    switch (operandType)
    {
        case OperandType.InlineNone:
            return string.Empty;
        case OperandType.ShortInlineI:
            return unchecked((sbyte)bytes[offset++]).ToString();
        case OperandType.InlineI:
            return ReadInt32(bytes, ref offset).ToString();
        case OperandType.InlineI8:
        {
            var value = BitConverter.ToInt64(bytes, offset);
            offset += 8;
            return value.ToString();
        }
        case OperandType.ShortInlineR:
        {
            var value = BitConverter.ToSingle(bytes, offset);
            offset += 4;
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        case OperandType.InlineR:
        {
            var value = BitConverter.ToDouble(bytes, offset);
            offset += 8;
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        case OperandType.ShortInlineBrTarget:
        {
            var delta = unchecked((sbyte)bytes[offset++]);
            return "IL_" + (offset + delta).ToString("X4");
        }
        case OperandType.InlineBrTarget:
        {
            var delta = ReadInt32(bytes, ref offset);
            return "IL_" + (offset + delta).ToString("X4");
        }
        case OperandType.InlineSwitch:
        {
            var count = ReadInt32(bytes, ref offset);
            var baseOffset = offset + count * 4;
            var targets = new string[count];
            for (var index = 0; index < count; index++)
                targets[index] = "IL_" + (baseOffset + ReadInt32(bytes, ref offset)).ToString("X4");
            return string.Join(",", targets);
        }
        case OperandType.ShortInlineVar:
            return bytes[offset++].ToString();
        case OperandType.InlineVar:
        {
            var value = BitConverter.ToUInt16(bytes, offset);
            offset += 2;
            return value.ToString();
        }
        case OperandType.InlineString:
        {
            var token = ReadInt32(bytes, ref offset);
            return "\"" + metadata.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF)) + "\"";
        }
        case OperandType.InlineField:
        case OperandType.InlineMethod:
        case OperandType.InlineType:
        case OperandType.InlineTok:
        case OperandType.InlineSig:
        {
            var token = ReadInt32(bytes, ref offset);
            return DescribeMetadataToken(metadata, token);
        }
        default:
            throw new InvalidDataException("Unsupported IL operand type: " + operandType);
    }
}

int ReadInt32(byte[] bytes, ref int offset)
{
    var value = BitConverter.ToInt32(bytes, offset);
    offset += 4;
    return value;
}

string DescribeMetadataToken(MetadataReader metadata, int token)
{
    var handle = MetadataTokens.Handle(token);
    return handle.Kind switch
    {
        HandleKind.MethodDefinition => metadata.GetString(
            metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
        HandleKind.MemberReference => DescribeMemberReference(
            metadata,
            metadata.GetMemberReference((MemberReferenceHandle)handle)),
        HandleKind.FieldDefinition => metadata.GetString(
            metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
        HandleKind.TypeDefinition => DescribeTypeDefinition(
            metadata,
            metadata.GetTypeDefinition((TypeDefinitionHandle)handle)),
        HandleKind.TypeReference => DescribeTypeReference(
            metadata,
            metadata.GetTypeReference((TypeReferenceHandle)handle)),
        HandleKind.TypeSpecification => "typespec:" + token.ToString("X8"),
        HandleKind.MethodSpecification => DescribeMethodSpecification(
            metadata,
            metadata.GetMethodSpecification((MethodSpecificationHandle)handle)),
        HandleKind.StandaloneSignature => "signature:" + token.ToString("X8"),
        _ => handle.Kind + ":" + token.ToString("X8")
    };
}

string DescribeMethodSpecification(
    MetadataReader metadata,
    MethodSpecification specification)
{
    var signature = metadata.GetBlobBytes(specification.Signature);
    return DescribeMetadataToken(metadata, MetadataTokens.GetToken(specification.Method)) +
           "<" + Convert.ToHexString(signature) + ">";
}

string DescribeMemberReference(MetadataReader metadata, MemberReference reference) =>
    DescribeParent(metadata, reference.Parent) + "::" + metadata.GetString(reference.Name);

string DescribeParent(MetadataReader metadata, EntityHandle handle) => handle.Kind switch
{
    HandleKind.TypeReference => DescribeTypeReference(
        metadata,
        metadata.GetTypeReference((TypeReferenceHandle)handle)),
    HandleKind.TypeDefinition => DescribeTypeDefinition(
        metadata,
        metadata.GetTypeDefinition((TypeDefinitionHandle)handle)),
    HandleKind.TypeSpecification => "typespec",
    HandleKind.MethodDefinition => metadata.GetString(
        metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
    _ => handle.Kind.ToString()
};

string DescribeTypeReference(MetadataReader metadata, TypeReference reference) =>
    metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);

string DescribeTypeDefinition(MetadataReader metadata, TypeDefinition definition) =>
    metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);

void VerifyServerXmlOverlayAbi(string objectSystemPath, byte[] serverBridgeAssembly)
{
    var objectSystemBytes = File.ReadAllBytes(objectSystemPath);
    using (var stream = new MemoryStream(objectSystemBytes, writable: false))
    using (var pe = new PEReader(stream))
    {
        var metadata = pe.GetMetadataReader();
        var typeHandles = metadata.TypeDefinitions.Where(handle =>
            DescribeTypeDefinition(metadata, metadata.GetTypeDefinition(handle)) ==
            "TaleWorlds.ObjectSystem.MBObjectManager").ToArray();
        Assert(typeHandles.Length == 1,
            "Server MBObjectManager type did not resolve exactly once by name.");
        var typeHandle = typeHandles[0];
        var type = metadata.GetTypeDefinition(typeHandle);
        var methods = type.GetMethods().Where(handle =>
        {
            var method = metadata.GetMethodDefinition(handle);
            return metadata.GetString(method.Name) == "ApplyXslt" &&
                   method.Attributes.HasFlag(MethodAttributes.Public) &&
                   method.Attributes.HasFlag(MethodAttributes.Static) &&
                   method.GetParameters().Count == 2 &&
                   Convert.ToHexString(metadata.GetBlobBytes(method.Signature)) ==
                   "00021280850E128085";
        }).ToArray();
        Assert(methods.Length == 1,
            "Server ApplyXslt public static signature did not resolve exactly once.");
    }

    VerifyServerBridgeXsltRedirect(serverBridgeAssembly);
}

void VerifyServerBridgeXsltRedirect(byte[] serverBridgeAssembly)
{
    using var stream = new MemoryStream(serverBridgeAssembly, writable: false);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var bridgeRuntimeHandle = metadata.TypeDefinitions.Single(handle =>
    {
        var definition = metadata.GetTypeDefinition(handle);
        return DescribeTypeDefinition(metadata, definition) == "BCS.CoopBridge.BridgeRuntime";
    });
    var bridgeRuntime = metadata.GetTypeDefinition(bridgeRuntimeHandle);
    var installHandles = bridgeRuntime.GetMethods().Where(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) ==
            "ApplyServerXmlOverlays")
        .ToArray();
    var resolveHandles = bridgeRuntime.GetMethods().Where(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) ==
            "ResolveRequiredServerXsltLoader")
        .ToArray();
    Assert(installHandles.Length == 1 && resolveHandles.Length == 1,
        "Packaged server bridge omitted its signature-based ApplyXslt overlay resolver.");
    var installHandle = installHandles[0];
    var resolveHandle = resolveHandles[0];
    var install = metadata.GetMethodDefinition(installHandle);
    var body = pe.GetMethodBody(install.RelativeVirtualAddress);
    var il = body.GetILBytes() ?? throw new InvalidDataException(
        "Packaged server XML-overlay installer has no IL body.");
    var calls = ReadInlineMethodTokens(il, metadata);
    Assert(calls.Count(token => token == MetadataTokens.GetToken(resolveHandle)) == 1,
        "Packaged server XML-overlay installer does not resolve ApplyXslt by signature exactly once.");
}

void VerifyRuntimeModuleAssemblyValidation(byte[] bridgeAssembly)
{
    using var stream = new MemoryStream(bridgeAssembly, writable: false);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var bridgeRuntimeHandle = metadata.TypeDefinitions.Single(handle =>
        DescribeTypeDefinition(metadata, metadata.GetTypeDefinition(handle)) ==
        "BCS.CoopBridge.BridgeRuntime");
    var bridgeRuntime = metadata.GetTypeDefinition(bridgeRuntimeHandle);
    MethodDefinitionHandle FindMethod(string name) => bridgeRuntime.GetMethods().Single(handle =>
        metadata.GetString(metadata.GetMethodDefinition(handle).Name) == name);

    var validatePackageHandle = FindMethod("ValidateInstalledPackage");
    var validateAssemblyHandle = FindMethod("ValidateRequiredModuleAssembly");
    var validateRegularFileHandle = FindMethod("ValidateRequiredModuleRegularFile");
    var packageBody = pe.GetMethodBody(
        metadata.GetMethodDefinition(validatePackageHandle).RelativeVirtualAddress);
    var packageCalls = ReadInlineMethodTokens(
        packageBody.GetILBytes() ?? throw new InvalidDataException(
            "Packaged bridge validation has no IL body."),
        metadata);
    Assert(packageCalls.Count(token => token == MetadataTokens.GetToken(validateAssemblyHandle)) == 1,
        "Runtime MODULE validation does not require module-contained managed assembly validation.");

    var assemblyBody = pe.GetMethodBody(
        metadata.GetMethodDefinition(validateAssemblyHandle).RelativeVirtualAddress);
    var assemblyCalls = ReadInlineMethodTokens(
        assemblyBody.GetILBytes() ?? throw new InvalidDataException(
            "Packaged module-assembly validation has no IL body."),
        metadata);
    Assert(assemblyCalls.Contains(MetadataTokens.GetToken(validateRegularFileHandle)),
        "Runtime module-assembly validation lost linked-ancestor containment checks.");
    Assert(assemblyCalls.Any(token =>
            DescribeMetadataToken(metadata, token).Contains(
                "System.Reflection.AssemblyName::GetAssemblyName",
                StringComparison.Ordinal)),
        "Runtime module-assembly validation lost managed simple-identity inspection.");
}

IReadOnlyList<int> ReadInlineMethodTokens(byte[] bytes, MetadataReader metadata)
{
    var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));
    var result = new List<int>();
    var offset = 0;
    while (offset < bytes.Length)
    {
        ushort value = bytes[offset++];
        if (value == 0xFE)
            value = (ushort)(0xFE00 | bytes[offset++]);
        var opCode = opCodes[value];
        var operandOffset = offset;
        _ = ReadIlOperand(opCode.OperandType, bytes, ref offset, metadata);
        if (opCode.OperandType == OperandType.InlineMethod)
            result.Add(BitConverter.ToInt32(bytes, operandOffset));
    }
    return result;
}

void TestCoopDependentModuleOrderValidation()
{
    BannerlordModule Module(
        string id,
        bool required,
        IReadOnlyList<string>? mustLoadAfter = null)
    {
        var module = new BannerlordModule
        {
            Name = id,
            Id = id,
            Version = "v1.0.0",
            Path = id,
            IsInstalled = true,
            IsRequired = required,
            IsServerCompatible = true,
            Dependencies = mustLoadAfter ?? Array.Empty<string>(),
            MustLoadAfter = mustLoadAfter ?? Array.Empty<string>(),
            MustLoadBefore = Array.Empty<string>(),
            IncompatibleModules = Array.Empty<string>()
        };
        module.SetInitialEnabled(true);
        return module;
    }

    var native = Module("Native", required: true);
    var host = Module("DedicatedServer.Windows", required: true, ["Native"]);
    var core = Module("SandBoxCore", required: true, ["Native"]);
    var sandbox = Module("Sandbox", required: true, ["SandBoxCore"]);
    var coop = Module("Coop", required: true, ["Sandbox"]);
    var eoe = Module("Europe1700", required: false, ["Coop"]);
    var bridge = Module(
        "BCS.CoopBridge.123456789abc",
        required: false,
        ["Coop", "Europe1700"]);
    var validOrder = new[] { native, host, core, sandbox, coop, eoe, bridge };

    var validMessages = new DependencyValidator().Validate(validOrder);
    Assert(validMessages.Count == 0,
        "Valid Coop -> Europe1700 -> bridge order produced a warning: " +
        string.Join(" | ", validMessages));

    var invalidOrder = new[] { native, host, core, sandbox, eoe, coop, bridge };
    var invalidMessages = new DependencyValidator().Validate(invalidOrder);
    Assert(invalidMessages.Any(message =>
            message.Contains("Europe1700", StringComparison.Ordinal) &&
            message.Contains("Dependency must load first: Coop", StringComparison.Ordinal)),
        "Real Coop dependency inversion was not detected.");

    var bridgeBeforeGameplay = new[] { native, host, core, sandbox, coop, bridge, eoe };
    var bridgeMessages = new DependencyValidator().Validate(bridgeBeforeGameplay);
    Assert(bridgeMessages.Any(message =>
            message.Contains("BCS.CoopBridge.123456789abc", StringComparison.Ordinal) &&
            message.Contains("after all active gameplay modules", StringComparison.Ordinal)),
        "Generated bridge before gameplay modules was not detected.");
}

void TestEurope1700HeadlessActionProjection()
{
    const string source = """
        <?xml version="1.0" encoding="utf-8"?>
        <action_sets>
          <action_set id="as_human_warrior" skeleton="human_skeleton" movement_system="bipedal">
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
          </action_set>
        </action_sets>
        """;

    var transformed = CoopCompatibilityPatcher.TransformEurope1700ActionSetForHeadless(
        System.Text.Encoding.UTF8.GetBytes(source));
    var document = new XmlDocument { XmlResolver = null };
    document.LoadXml(System.Text.Encoding.UTF8.GetString(transformed));
    var actions = document.SelectNodes("/action_sets/action_set/action")!
        .OfType<XmlElement>()
        .ToDictionary(
            action => action.GetAttribute("type"),
            action => action.GetAttribute("animation"),
            StringComparer.Ordinal);

    Assert(actions.Count == 24, "EOE headless action projection changed the action set.");
    Assert(actions["act_ready_musket_cla"] == "ready_crossbow",
        "EOE musket ready action did not use a native headless animation.");
    Assert(actions["act_reload_cannon_horseback_cla"] == "reload_crossbow_horseback",
        "EOE mounted cannon reload did not use a native headless animation.");
    Assert(actions["act_reload_musket_continue_fast_cla"] == "reload_crossbow_fast_continue",
        "EOE fast reload continuation did not use a native headless animation.");
    Assert(actions["cla_act_cla_spear_idle_1"] == "troop_stand_spear_1",
        "EOE spear idle did not use a native headless animation.");
    Assert(actions.Values.All(animation =>
            !animation.Contains("_cla_", StringComparison.Ordinal) &&
            !animation.StartsWith("cla_", StringComparison.Ordinal) &&
            !animation.StartsWith("reznov_", StringComparison.Ordinal)),
        "EOE headless action projection retained a client-only animation.");
}

void TestClientSaveImport()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "bcs-client-save-import-regression-" + Guid.NewGuid().ToString("N"));
    var clientDirectory = Path.Combine(root, "Game Saves", "Native");
    var serverDirectory = Path.Combine(root, "CoopData", "DedicatedServer", "Game Saves");
    Directory.CreateDirectory(clientDirectory);
    Directory.CreateDirectory(serverDirectory);

    try
    {
        File.WriteAllBytes(Path.Combine(clientDirectory, "EOE Seed #1.sav"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(clientDirectory, "default_new_game.sav"), [9]);
        var importer = new ClientSaveImportService(clientDirectory, serverDirectory);

        Assert(importer.GetClientSaveNames().SequenceEqual(["EOE Seed #1"]),
            "Client save discovery did not exclude the default template.");

        var first = importer.Import("EOE Seed #1");
        Assert(!first.AlreadyPresent && first.RenamedForCompatibility &&
               !first.RenamedForCollision &&
               first.SaveName == "EOE_Seed_1",
            "First client save import did not receive a Coop-safe destination name.");
        Assert(File.ReadAllBytes(first.DestinationPath).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "Imported client save bytes changed.");

        var repeated = importer.Import("EOE Seed #1.sav");
        Assert(repeated.AlreadyPresent && repeated.RenamedForCompatibility &&
               !repeated.RenamedForCollision && repeated.SaveName == "EOE_Seed_1",
            "Identical client save import created a duplicate.");

        File.WriteAllBytes(Path.Combine(clientDirectory, "EOE Seed #1.sav"), [5, 6, 7]);
        var collision = importer.Import("EOE Seed #1");
        Assert(!collision.AlreadyPresent && collision.RenamedForCompatibility &&
               collision.RenamedForCollision &&
               collision.SaveName == "EOE_Seed_1_client",
            "Different client save did not receive a collision-free name.");
        Assert(File.ReadAllBytes(Path.Combine(serverDirectory, "EOE_Seed_1.sav"))
                .SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "Existing server save was overwritten during a collision.");
        Assert(File.ReadAllBytes(collision.DestinationPath).SequenceEqual(new byte[] { 5, 6, 7 }),
            "Collision-safe client save copy has incorrect bytes.");

        File.WriteAllBytes(Path.Combine(clientDirectory, "###.sav"), [7, 7]);
        var fallback = importer.Import("###");
        Assert(fallback.SaveName == "Imported_Save" &&
               fallback.RenamedForCompatibility && !fallback.RenamedForCollision,
            "All-special client save name did not receive deterministic fallback name.");

        File.WriteAllText(Path.Combine(serverDirectory, "SidecarOnly.json"), "{}");
        File.WriteAllBytes(Path.Combine(clientDirectory, "SidecarOnly.sav"), [8, 8]);
        var sidecarCollision = importer.Import("SidecarOnly");
        Assert(sidecarCollision.SaveName == "SidecarOnly_client",
            "Orphaned server sidecar was not protected from a client import.");
        Assert(!File.Exists(Path.Combine(serverDirectory, "SidecarOnly.sav")),
            "Import paired a new save with an unrelated server sidecar.");

        var rejectedTraversal = false;
        try
        {
            importer.Import("..\\EOE Seed #1");
        }
        catch (InvalidDataException)
        {
            rejectedTraversal = true;
        }
        Assert(rejectedTraversal,
            "Client save import accepted a path outside the save directory.");
        Assert(!Directory.EnumerateFiles(serverDirectory, ".bcs-client-save-import-*.tmp")
                .Any(),
            "Client save import left a staging file behind.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

void TestCoopSafeServerSaveNames()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "bcs-save-name-policy-regression-" + Guid.NewGuid().ToString("N"));
    var coopDataDirectory = Path.Combine(root, "CoopData");
    var dedicatedServerDirectory = Path.Combine(coopDataDirectory, "DedicatedServer");
    Directory.CreateDirectory(dedicatedServerDirectory);
    var configPath = Path.Combine(dedicatedServerDirectory, "server-config.json");

    void WriteServerConfig(string saveName) => File.WriteAllText(
        configPath,
        $$"""
        {
          "saveName": {{JsonSerializer.Serialize(saveName)}},
          "autosaveMinutes": 5,
          "password": "",
          "logFile": true,
          "steam": true
        }
        """);

    try
    {
        var configService = new CoopConfigService(coopDataDirectory);
        WriteServerConfig("EOE Seed");
        var loaded = configService.LoadServerConfig();
        Assert(loaded.SaveName == "EOE Seed",
            "Unsafe legacy save name could not be loaded for correction.");

        var saveRejected = false;
        try
        {
            configService.SaveServerConfig(loaded);
        }
        catch (InvalidOperationException exception)
        {
            saveRejected = exception.Message.Contains(
                "letters, digits, and underscores",
                StringComparison.OrdinalIgnoreCase);
        }
        Assert(saveRejected,
            "Manual server configuration accepted an unsafe Coop save name.");

        loaded.SaveName = "EOE_Seed_1";
        configService.SaveServerConfig(loaded);
        Assert(configService.LoadServerConfig().SaveName == "EOE_Seed_1",
            "Safe server save name did not persist.");

        Directory.CreateDirectory(configService.ServerSaveDirectory);
        Assert(configService.IsFirstJoinCharacterSetupRequired(),
            "Missing Coop sidecar was not identified as first-join setup.");

        var sidecarPath = Path.Combine(
            configService.ServerSaveDirectory,
            "EOE_Seed_1.json");
        File.WriteAllText(sidecarPath, "{\"Players\":[]}");
        Assert(configService.IsFirstJoinCharacterSetupRequired(),
            "Empty Coop player list was not identified as first-join setup.");

        File.WriteAllText(sidecarPath, "{}");
        var malformedSidecarRejected = false;
        try
        {
            configService.IsFirstJoinCharacterSetupRequired();
        }
        catch (InvalidDataException)
        {
            malformedSidecarRejected = true;
        }
        Assert(malformedSidecarRejected,
            "Malformed Coop player sidecar was silently treated as valid setup state.");

        File.WriteAllText(
            sidecarPath,
            "{\"Players\":[{\"ControllerId\":\"test-player\"}]}");
        Assert(!configService.IsFirstJoinCharacterSetupRequired(),
            "Established Coop player was incorrectly treated as first-join setup.");

        WriteServerConfig("EOE Seed");
        var executablePath = Path.Combine(root, "BannerlordCoopServer.exe");
        File.WriteAllBytes(executablePath, [1]);
        using var processManager = new ServerProcessManager(
            new LogService(),
            new DedicatedServerLaunchBuilder(
                new ModuleScanner(),
                Path.Combine(root, "BCSTool.RuntimeBootstrap.dll")),
            configService,
            new BridgeInstallationService(
                new ModuleScanner(),
                new CoopCompatibilityPatcher()));
        var started = processManager.StartAsync(
                executablePath,
                root,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert(!started && processManager.LastStartError?.Contains(
                "letters, digits, and underscores",
                StringComparison.OrdinalIgnoreCase) == true,
            "Server startup did not reject an unsafe configured save name before launch.");

    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

void TestCoopUdpPortGuard()
{
    using var tcp = new System.Net.Sockets.TcpListener(
        System.Net.IPAddress.Loopback,
        0);
    tcp.Start();
    var tcpPort = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
    var monitor = new PortMonitor();
    Assert(!monitor.IsUdpPortInUse(tcpPort),
        "TCP-only listener was incorrectly treated as an occupied Coop UDP port.");

    using var udp = new System.Net.Sockets.UdpClient(
        new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
    var udpPort = ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
    Assert(monitor.IsUdpPortInUse(udpPort),
        "UDP listener was not detected by the Coop port guard.");
}

void TestCampaignSaveDiscovery()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "bcs-save-discovery-regression-" + Guid.NewGuid().ToString("N"));
    var bannerlordDirectory = Path.Combine(root, "Mount and Blade II Bannerlord");
    var coopDataDirectory = Path.Combine(bannerlordDirectory, "CoopData");
    var clientDirectory = Path.Combine(bannerlordDirectory, "Game Saves");
    var legacyNativeDirectory = Path.Combine(clientDirectory, "Native");
    var serverDirectory = Path.Combine(
        coopDataDirectory,
        "DedicatedServer",
        "Game Saves");
    Directory.CreateDirectory(clientDirectory);
    Directory.CreateDirectory(legacyNativeDirectory);
    Directory.CreateDirectory(serverDirectory);

    try
    {
        File.WriteAllBytes(Path.Combine(clientDirectory, "Test3.sav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(legacyNativeDirectory, "StaleNative.sav"), [4]);
        File.WriteAllBytes(Path.Combine(serverDirectory, "Campaign.sav"), [5]);
        File.WriteAllBytes(Path.Combine(serverDirectory, "Campaign.backup1.sav"), [6]);
        File.WriteAllBytes(Path.Combine(serverDirectory, "Campaign.backup1.json"), [6]);
        File.WriteAllBytes(Path.Combine(serverDirectory, "Campaign.backup2.sav"), [7]);
        File.WriteAllBytes(Path.Combine(serverDirectory, "Campaign.backup2.json"), [7]);
        File.WriteAllBytes(Path.Combine(serverDirectory, "default_new_game.sav"), [8]);

        var configService = new CoopConfigService(coopDataDirectory);
        var importer = new ClientSaveImportService(
            configService.ClientSaveDirectory,
            configService.ServerSaveDirectory);

        Assert(
            Path.GetFullPath(configService.ClientSaveDirectory) ==
            Path.GetFullPath(clientDirectory),
            "Client save discovery still targets the legacy Native subdirectory.");
        Assert(importer.GetClientSaveNames().SequenceEqual(["Test3"]),
            "Client save discovery did not read the live Bannerlord campaign folder.");
        Assert(configService.GetServerSaveNames().SequenceEqual(["Campaign"]),
            "Server save discovery exposed backup or default-template saves as campaigns.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

void TestLauncherDelegatesCampaignBackupsToCoop()
{
    var applicationAssembly = typeof(SettingsService).Assembly;
    Assert(
        applicationAssembly.GetType("BCSTool.Services.SaveBackupService") is null,
        "Launcher-owned campaign save backup service is still present.");
    Assert(
        typeof(ServerSettings).GetProperty("SaveBackupsEnabled") is null &&
        typeof(ServerSettings).GetProperty("SaveBackupCount") is null,
        "Launcher-owned campaign save backup settings are still exposed.");
}

void TestEurope1700HeadlessActionTypeProjection()
{
    const string source = """
        <?xml version="1.0" encoding="utf-8"?>
        <action_types>
          <action name="act_ready_musket_cla" type="actt_ready_ranged" action_stage="as_attack_ready" />
          <action name="cla_reload_bomb" type="actt_reload" action_stage="as_reload_last_phase" />
          <action name="cla_act_cla_spear_idle_1" />
        </action_types>
        """;

    var transformed = CoopCompatibilityPatcher.TransformEurope1700ActionTypesForHeadless(
        System.Text.Encoding.UTF8.GetBytes(source));
    var document = new XmlDocument { XmlResolver = null };
    document.LoadXml(System.Text.Encoding.UTF8.GetString(transformed));
    Assert(document.SelectSingleNode(
               "/action_types/action[@name='cla_act_reload_bomb']") is not null,
        "EOE bomb reload action type did not match its action-set and item-usage ID.");
    Assert(document.SelectSingleNode(
               "/action_types/action[@name='cla_reload_bomb']") is null,
        "EOE mismatched bomb reload action type remained in the server projection.");
}

void TestEurope1700TrebuchetPrefabRepair()
{
    const string malformed = """
        <prefabs>
          <variable name="ProjectileSpeed" value="53.500"
          <variable name="ProjectileSpeed" value="53.500"
          <variable name="ProjectileSpeed" value="53.500"
          <variable name="ProjectileSpeed" value="53.500"
        </prefabs>
        """;

    var transformed = CoopCompatibilityPatcher.TransformEurope1700TrebuchetPrefabForHeadless(
        System.Text.Encoding.UTF8.GetBytes(malformed));
    var text = System.Text.Encoding.UTF8.GetString(transformed);
    Assert(text.Split("value=\"53.500\"/>", StringSplitOptions.None).Length - 1 == 4,
        "EOE trebuchet repair did not close all four malformed ProjectileSpeed variables.");
    var document = new XmlDocument { XmlResolver = null };
    document.LoadXml(text);
    Assert(document.SelectNodes("/prefabs/variable")?.Count == 4,
        "EOE repaired trebuchet prefab did not remain structurally intact.");
    var secondPass = CoopCompatibilityPatcher.TransformEurope1700TrebuchetPrefabForHeadless(
        transformed);
    Assert(secondPass.SequenceEqual(transformed),
        "EOE trebuchet repair was not byte-for-byte idempotent.");

    const string projectilePrefix = "<variable name=\"ProjectileSpeed\" value=\"53.500\"";
    var firstProjectile = malformed.IndexOf(projectilePrefix, StringComparison.Ordinal);
    var mixed = malformed[..firstProjectile] +
                projectilePrefix + "/>" +
                malformed[(firstProjectile + projectilePrefix.Length)..];
    AssertThrowsInvalidData(
        () => CoopCompatibilityPatcher.TransformEurope1700TrebuchetPrefabForHeadless(
            System.Text.Encoding.UTF8.GetBytes(mixed)),
        "EOE trebuchet repair accepted mixed repaired/malformed input.");
}

void TestEurope1700OptionalServerDllSelection()
{
    const string customBattle = "EOE.CustomBattlePatch.dll";
    const string rfBattleAi = "RF_BattleAI.dll";
    var required = new[]
    {
        "XMLMeleePatch.dll",
        "BattleArtilleryReworked.dll",
        "Europe1700.dll",
        "Bannerlord.EOEPatches.dll",
        "ClansResourceAdder.dll",
        "CustomizableClanTier.dll"
    };

    Verify(string.Empty, required, "absent optional declarations");
    Verify(
        $"<!--{SubModule(customBattle)}{SubModule(rfBattleAi)}-->",
        required,
        "commented optional declarations");
    Verify(
        SubModule(rfBattleAi),
        [.. required, rfBattleAi],
        "RF battle AI declaration");
    Verify(
        SubModule(customBattle),
        [.. required, customBattle],
        "custom battle declaration");
    Verify(
        SubModule(customBattle) + SubModule(rfBattleAi),
        [.. required, customBattle, rfBattleAi],
        "both optional declarations");

    return;

    static string SubModule(string dllName) =>
        $$"""
          <SubModule>
            <Name value="{{Path.GetFileNameWithoutExtension(dllName)}}" />
            <DLLName value="{{dllName}}" />
            <SubModuleClassType value="Optional.SubModule" />
            <Tags>
              <Tag key="DedicatedServerType" value="none" />
              <Tag key="IsNoRenderModeElement" value="false" />
            </Tags>
          </SubModule>
        """;

    void Verify(string optionalXml, string[] expected, string scenario)
    {
        var manifest = new XmlDocument { XmlResolver = null };
        manifest.LoadXml($"<Module><SubModules>{optionalXml}</SubModules></Module>");
        var actual = CoopCompatibilityPatcher.SelectEurope1700ServerDlls(manifest);
        Assert(actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
            $"EOE {scenario} selected [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}].");
    }
}

void TestEurope1700ItemsSchemaRepair()
{
    const string schema = """
        <?xml version="1.0" encoding="utf-8"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Items">
            <xs:complexType>
              <xs:choice minOccurs="0" maxOccurs="unbounded">
                <xs:element name="Item">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="ItemComponent" minOccurs="0" maxOccurs="1">
                        <xs:complexType>
                          <xs:choice>
                            <xs:element name="Weapon" minOccurs="0" maxOccurs="1">
                              <xs:complexType>
                                <xs:anyAttribute processContents="skip" />
                              </xs:complexType>
                            </xs:element>
                          </xs:choice>
                        </xs:complexType>
                      </xs:element>
                    </xs:sequence>
                    <xs:attribute name="id" use="required" />
                  </xs:complexType>
                </xs:element>
              </xs:choice>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;
    const string items = """
        <Items>
          <Item id="musket">
            <ItemComponent>
              <Weapon weapon_class="Crossbow" />
              <Weapon weapon_class="TwoHandedMace" />
            </ItemComponent>
          </Item>
        </Items>
        """;
    var schemaBytes = System.Text.Encoding.UTF8.GetBytes(schema);
    var itemsBytes = System.Text.Encoding.UTF8.GetBytes(items);
    var originalSchema = schemaBytes.ToArray();
    var originalItems = itemsBytes.ToArray();

    var transformed = CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
        schemaBytes,
        itemsBytes);
    var expected = System.Text.Encoding.UTF8.GetBytes(schema.Replace(
        "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"1\">",
        "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"unbounded\">",
        StringComparison.Ordinal));
    Assert(transformed.SequenceEqual(expected),
        "EOE Items.xsd repair changed content beyond the Weapon maxOccurs value.");
    Assert(schemaBytes.SequenceEqual(originalSchema) && itemsBytes.SequenceEqual(originalItems),
        "EOE Items.xsd repair mutated an input buffer.");

    var secondPass = CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
        transformed,
        itemsBytes);
    Assert(secondPass.SequenceEqual(transformed),
        "EOE Items.xsd repair was not byte-for-byte idempotent.");

    AssertThrowsInvalidData(
        () => CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
            schemaBytes,
            System.Text.Encoding.UTF8.GetBytes(items.Replace(
                "<Weapon weapon_class=\"TwoHandedMace\" />",
                string.Empty,
                StringComparison.Ordinal))),
        "EOE Items.xsd repair accepted an items file without repeated Weapon modes.");
    AssertThrowsInvalidData(
        () => CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
            schemaBytes,
            System.Text.Encoding.UTF8.GetBytes(items.Replace(
                " id=\"musket\"",
                string.Empty,
                StringComparison.Ordinal))),
        "EOE Items.xsd repair ignored a full-file schema validation error.");
    AssertThrowsInvalidData(
        () => CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
            System.Text.Encoding.UTF8.GetBytes(schema.Replace(
                "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"1\">",
                "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"2\">",
                StringComparison.Ordinal)),
            itemsBytes),
        "EOE Items.xsd repair accepted an unsupported Weapon maxOccurs value.");
    AssertThrowsInvalidData(
        () => CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
            System.Text.Encoding.UTF8.GetBytes(schema.Replace(
                "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"1\">",
                "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"1\">" +
                "<xs:complexType /></xs:element>" +
                "<xs:element name=\"Weapon\" minOccurs=\"0\" maxOccurs=\"1\">",
                StringComparison.Ordinal)),
            itemsBytes),
        "EOE Items.xsd repair accepted ambiguous Weapon declarations.");
    AssertThrowsInvalidData(
        () => CoopCompatibilityPatcher.TransformEurope1700ItemsSchema(
            System.Text.Encoding.UTF8.GetBytes(schema.Replace(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<!DOCTYPE schema [<!ENTITY injected \"Weapon\">]>",
                StringComparison.Ordinal)),
            itemsBytes),
        "EOE Items.xsd repair accepted a DTD-bearing schema.");
}

void TestEurope1700SchemaRepairs()
{
    static XmlDocument ApplyXslt(byte[] stylesheet, string input)
    {
        var transform = new System.Xml.Xsl.XslCompiledTransform();
        using (var stylesheetReader = XmlReader.Create(
                   new MemoryStream(stylesheet, writable: false),
                   new XmlReaderSettings
                   {
                       DtdProcessing = DtdProcessing.Prohibit,
                       XmlResolver = null
                   }))
        {
            transform.Load(stylesheetReader, new System.Xml.Xsl.XsltSettings(), null);
        }

        using var output = new MemoryStream();
        using (var inputReader = XmlReader.Create(
                   new StringReader(input),
                   new XmlReaderSettings
                   {
                       DtdProcessing = DtdProcessing.Prohibit,
                       XmlResolver = null
                   }))
        {
            transform.Transform(inputReader, null, output);
        }
        output.Position = 0;
        var document = new XmlDocument { XmlResolver = null };
        document.Load(output);
        return document;
    }

    var equipmentSource =
        "<EquipmentRosters><EquipmentRoster><EquipmentSet>" +
        string.Concat(Enumerable.Repeat("<equipment slot=\"Item0\" />", 11)) +
        "</EquipmentSet></EquipmentRoster></EquipmentRosters>";
    var equipmentResult = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_italian.xml",
        System.Text.Encoding.UTF8.GetBytes(equipmentSource));
    var equipmentDocument = new XmlDocument { XmlResolver = null };
    equipmentDocument.LoadXml(System.Text.Encoding.UTF8.GetString(equipmentResult));
    Assert(equipmentDocument.SelectNodes("//Equipment")?.Count == 11,
        "EOE server equipment elements were not repaired with exact casing.");
    Assert(equipmentDocument.SelectNodes("//equipment")?.Count == 0,
        "EOE lowercase server equipment elements remained after repair.");

    var traitsSource =
        "<NPCCharacters>" +
        string.Concat(Enumerable.Repeat("<NPCCharacter><traits><Trait /></traits></NPCCharacter>", 24)) +
        "</NPCCharacters>";
    var traitsResult = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/lords_main/lords_ottoman_extra.xml",
        System.Text.Encoding.UTF8.GetBytes(traitsSource));
    var traitsDocument = new XmlDocument { XmlResolver = null };
    traitsDocument.LoadXml(System.Text.Encoding.UTF8.GetString(traitsResult));
    Assert(traitsDocument.SelectNodes("//Traits")?.Count == 24,
        "EOE Ottoman Traits elements were not repaired with exact casing.");

    const string scottishSource =
        "<NPCCharacters><NPCCharacter><Equipments>" +
        "<EquipmentSet id=\"celtic_artisan_civ\" equipmentType=\"Civilian\" />/>" +
        "</Equipments></NPCCharacter></NPCCharacters>";
    var scottishResult = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/npccharacters/spnpccharacters_scottish.xml",
        System.Text.Encoding.UTF8.GetBytes(scottishSource));
    var scottishText = System.Text.Encoding.UTF8.GetString(scottishResult);
    Assert(!scottishText.Contains("/>/>", StringComparison.Ordinal),
        "EOE Scottish NPC repair retained the stray element terminator.");

    const string identityXslt = """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output omit-xml-declaration="yes" />
          <xsl:template match="@*|node()">
            <xsl:copy><xsl:apply-templates select="@*|node()" /></xsl:copy>
          </xsl:template>
        </xsl:stylesheet>
        """;
    var equipmentXslt = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets.xslt",
        System.Text.Encoding.UTF8.GetBytes(identityXslt));
    var mergedEquipment = ApplyXslt(
        equipmentXslt,
        "<EquipmentRosters><EquipmentRoster><EquipmentSet>" +
        string.Concat(Enumerable.Repeat(
            "<Equipment slot=\"Cape\" id=\"Item.bearskin\" />",
            13)) +
        "<Equipment slot=\"Cape\" id=\"Item.other\" />" +
        "</EquipmentSet></EquipmentRoster></EquipmentRosters>");
    Assert(mergedEquipment.SelectNodes("//*[@slot='Cape' and @id='Item.bearskin']")?.Count == 0,
        "EOE merged equipment XSLT retained official Bearskin Cape entries.");
    Assert(mergedEquipment.SelectNodes("//*[@slot='Cape' and @id='Item.other']")?.Count == 1,
        "EOE merged equipment XSLT removed an unrelated Cape entry.");

    var npcXslt = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/trooptrees/spnpccharacters.xslt",
        System.Text.Encoding.UTF8.GetBytes(identityXslt));
    var mergedNpc = ApplyXslt(
        npcXslt,
        "<NPCCharacters><NPCCharacter><Equipments>" +
        string.Concat(Enumerable.Repeat("<EquipmentSet civilian=\"true\" />", 344)) +
        "<EquipmentSet civilian=\"false\" />" +
        "<EquipmentSet equipmentType=\"Civilian\" />" +
        string.Concat(Enumerable.Repeat(
            "<EquipmentSet><equipment slot=\"Cape\" id=\"Item.bearskin\" /></EquipmentSet>",
            4)) +
        "</Equipments></NPCCharacter></NPCCharacters>");
    Assert(mergedNpc.SelectNodes("//EquipmentSet[@civilian='true']")?.Count == 0 &&
           mergedNpc.SelectNodes("//EquipmentSet[@equipmentType='Civilian']")?.Count == 345,
        "EOE merged NPC XSLT did not convert the exact legacy civilian=true semantics.");
    Assert(mergedNpc.SelectNodes("//EquipmentSet[@civilian='false']")?.Count == 1,
        "EOE merged NPC XSLT changed a civilian=false value outside its repair pattern.");
    Assert(mergedNpc.SelectNodes("//*[@slot='Cape' and @id='Item.bearskin']")?.Count == 0,
        "EOE merged NPC XSLT retained Bearskin Cape entries.");

    var supplementalBearskinSource =
        "<EquipmentRosters><EquipmentRoster><EquipmentSet>" +
        string.Concat(Enumerable.Repeat(
            "<Equipment\n slot=\"Cape\"\n id=\"Item.bearskin\" />",
            9)) +
        "<Equipment slot=\"Head\" id=\"Item.bearskin\" />" +
        "</EquipmentSet></EquipmentRoster></EquipmentRosters>";
    var supplementalBearskinBytes = System.Text.Encoding.UTF8.GetBytes(supplementalBearskinSource);
    var supplementalBearskinResult =
        CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_battania.xml",
            supplementalBearskinBytes);
    var repeatedSupplementalBearskinResult =
        CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_battania.xml",
            supplementalBearskinBytes);
    Assert(supplementalBearskinResult.SequenceEqual(repeatedSupplementalBearskinResult),
        "EOE Bearskin Cape repair was not deterministic.");
    var supplementalBearskinDocument = new XmlDocument { XmlResolver = null };
    supplementalBearskinDocument.LoadXml(
        System.Text.Encoding.UTF8.GetString(supplementalBearskinResult));
    Assert(supplementalBearskinDocument
               .SelectNodes("//*[@slot='Cape' and @id='Item.bearskin']")?.Count == 0,
        "EOE supplemental roster retained invalid Bearskin Cape entries.");
    Assert(supplementalBearskinDocument
               .SelectNodes("//*[@slot='Head' and @id='Item.bearskin']")?.Count == 1,
        "EOE Bearskin repair removed the valid HeadArmor usage.");

    var rejectedBearskinCountDrift = false;
    try
    {
        CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
            "ModuleData/sandbox_core_equipment_sets/sandboxcore_equipment_sets_battania.xml",
            System.Text.Encoding.UTF8.GetBytes(supplementalBearskinSource.Replace(
                "<Equipment\n slot=\"Cape\"\n id=\"Item.bearskin\" />",
                string.Empty,
                StringComparison.Ordinal)));
    }
    catch (InvalidDataException)
    {
        rejectedBearskinCountDrift = true;
    }
    Assert(rejectedBearskinCountDrift,
        "EOE Bearskin Cape repair accepted changed occurrence counts.");

    var rejectedXsltDrift = false;
    try
    {
        CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
            "ModuleData/trooptrees/spnpccharacters.xslt",
            System.Text.Encoding.UTF8.GetBytes(
                identityXslt + Environment.NewLine + "</xsl:stylesheet>"));
    }
    catch (InvalidDataException)
    {
        rejectedXsltDrift = true;
    }
    Assert(rejectedXsltDrift,
        "EOE merged NPC XSLT repair accepted changed stylesheet structure.");

    const string workshopXml = """
        <WorkshopTypes>
          <WorkshopType id="artisans">
            <Production conversion_speed="0.1"><Outputs>
              <Output output="ItemCategory.ranged_weapons_3" output_count="1" />
              <Output output="ItemCategory.ranged_weapons_5" output_count="1" />
              <Output
                output="ItemCategory.meat"
                output_count="2" />
              <Output output="ItemCategory.hides" output_count="1" />
            </Outputs></Production>
          </WorkshopType>
          <WorkshopType id="gunsmith">
            <Production conversion_speed="1"><Outputs>
              <Output output="ItemCategory.ranged_weapons" output_count="3" />
              <Output output="ItemCategory.ranged_weapons_2" output_count="1" />
              <Output output="ItemCategory.ranged_weapons_3" output_count="1" />
              <Output output="ItemCategory.ranged_weapons_4" output_count="1" />
              <Output output="ItemCategory.ranged_weapons_5" output_count="1" />
            </Outputs></Production>
          </WorkshopType>
          <WorkshopType id="butcher">
            <Production conversion_speed="2"><Outputs>
              <Output
                output="ItemCategory.meat"
                output_count="6" />
              <Output output="ItemCategory.hides" output_count="2" />
            </Outputs></Production>
            <Production conversion_speed="2"><Outputs>
              <Output
                output="ItemCategory.meat"
                output_count="2" />
              <Output output="ItemCategory.hides" output_count="1" />
            </Outputs></Production>
            <Production conversion_speed="2"><Outputs>
              <Output
                output="ItemCategory.meat"
                output_count="1" />
              <Output output="ItemCategory.hides" output_count="1" />
            </Outputs></Production>
          </WorkshopType>
        </WorkshopTypes>
        """;
    var workshopSource = "\uFEFF" + workshopXml;
    var workshopSourceBytes = System.Text.Encoding.UTF8.GetBytes(workshopSource);
    var workshopResult = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/spworkshops.xml",
        workshopSourceBytes);
    var repeatedWorkshopResult = CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
        "ModuleData/spworkshops.xml",
        workshopSourceBytes);
    Assert(workshopResult.SequenceEqual(repeatedWorkshopResult),
        "EOE workshop category repair was not deterministic.");
    var workshopText = System.Text.Encoding.UTF8.GetString(workshopResult);
    Assert(!workshopText.Contains("output=\"ItemCategory.ranged_weapons\"", StringComparison.Ordinal) &&
           !workshopText.Contains("output=\"ItemCategory.ranged_weapons_2\"", StringComparison.Ordinal) &&
           !workshopText.Contains("output=\"ItemCategory.ranged_weapons_3\"", StringComparison.Ordinal) &&
           !workshopText.Contains("output=\"ItemCategory.ranged_weapons_4\"", StringComparison.Ordinal),
        "EOE empty ranged workshop categories remained after repair.");
    Assert(workshopText.Split(
               "output=\"ItemCategory.ranged_weapons_5\"",
               StringSplitOptions.None).Length - 1 == 7,
        "EOE workshop repair did not map the exact five empty outputs to the populated tier.");
    Assert(workshopText.Split(
               "output=\"ItemCategory.meat\"",
               StringSplitOptions.None).Length - 1 == 4 &&
           workshopText.Contains("output_count=\"6\"", StringComparison.Ordinal),
        "EOE workshop repair removed valid engine-provided meat production.");
    Assert(workshopText.Split(
               "output=\"ItemCategory.hides\"",
               StringSplitOptions.None).Length - 1 == 4,
        "EOE workshop repair removed or changed valid hides co-outputs.");

    var rejectedChangedWorkshopInput = false;
    try
    {
        CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
            "ModuleData/spworkshops.xml",
            System.Text.Encoding.UTF8.GetBytes(
                workshopSource.Replace(
                    "output=\"ItemCategory.ranged_weapons_4\"",
                    "output=\"ItemCategory.ranged_weapons_5\"",
                    StringComparison.Ordinal)));
    }
    catch (InvalidDataException)
    {
        rejectedChangedWorkshopInput = true;
    }
    Assert(rejectedChangedWorkshopInput,
        "EOE workshop category repair accepted changed occurrence counts.");

    var rejectedChangedInput = false;
    try
    {
        CoopCompatibilityPatcher.TransformEurope1700SchemaRepairForHeadless(
            "ModuleData/lord_equipment_sets/sandboxcore_equipment_sets_lords_italian.xml",
            System.Text.Encoding.UTF8.GetBytes("<root><equipment /></root>"));
    }
    catch (InvalidDataException)
    {
        rejectedChangedInput = true;
    }
    Assert(rejectedChangedInput,
        "EOE schema repair accepted an unexpected element count.");
}

void TestSupportedReleasedCoopVersions()
{
    Assert(CoopCompatibilityPatcher.IsSupportedReleasedCoopVersion("v0.1.1"),
        "Previously verified Coop v0.1.1 was rejected.");
    Assert(CoopCompatibilityPatcher.IsSupportedReleasedCoopVersion("V0.1.2"),
        "Current released Coop v0.1.2 was rejected.");
    Assert(!CoopCompatibilityPatcher.IsSupportedReleasedCoopVersion("v0.1.3"),
        "An unverified future Coop release was accepted.");
    Assert(!CoopCompatibilityPatcher.IsSupportedReleasedCoopVersion(null),
        "A missing Coop version was accepted.");
}

void TestManagedResolverPrefersExactIdentity()
{
    var requested = new System.Reflection.AssemblyName(
        "Microsoft.CodeAnalysis.CSharp, Version=4.13.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
    var candidates = new[]
    {
        new System.Reflection.AssemblyName(
            "Microsoft.CodeAnalysis.CSharp, Version=2.8.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"),
        new System.Reflection.AssemblyName(
            "Microsoft.CodeAnalysis.CSharp, Version=4.13.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
    };

    Assert(StartupHook.SelectBestCandidateIndex(requested, candidates) == 1,
        "Managed resolver preferred the engine's older name-only Roslyn match.");
    var unavailable = new System.Reflection.AssemblyName(
        "Microsoft.CodeAnalysis.CSharp, Version=5.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
    Assert(StartupHook.SelectBestCandidateIndex(unavailable, candidates) == 0,
        "Managed resolver did not preserve its name-only legacy fallback.");
}

void TestManagedResolverPrefersCoopServerDependencies()
{
    var root = Path.Combine(Path.GetTempPath(), "bcs-resolver-order");
    var central = Path.Combine(root, "engine", "bin", "Win64_Shipping_Server");
    var framework = Path.Combine(
        root,
        "engine",
        "Modules",
        "Bannerlord.Harmony",
        "bin",
        "Win64_Shipping_Server");
    var coop = Path.Combine(root, "engine", "Modules", "Coop", "bin", "Win64_Shipping_Server");
    var ordered = StartupHook.OrderSearchDirectories([framework, coop, central]);

    Assert(ordered.SequenceEqual([central, coop, framework], StringComparer.OrdinalIgnoreCase),
        "Released Coop server dependencies did not precede community framework collisions.");
}

void TestMissingModOptions()
{
    WithTemporaryConfig(
        BuildConfig(),
        (service, path, original) =>
        {
            var config = service.LoadModConfig();

            AssertDefaults(config);

            config.FastForwardEnabled = false;
            config.WandererLimit = 47;
            config.SmithingStaminaRecoveryOutsideSettlements = false;
            service.SaveModConfig(config);

            Assert(File.ReadAllText(path + ".bak") == original, "Save backup did not preserve the original file.");

            var saved = File.ReadAllText(path);
            Assert(saved.Contains("preserve-this-comment", StringComparison.Ordinal), "Existing comments were removed.");

            using var document = ParseJsonc(saved);
            var options = document.RootElement.GetProperty("modOptions");
            Assert(options.EnumerateObject().Count() == 14, "The complete 14-option schema was not materialized.");
            Assert(!options.GetProperty("fastForwardEnabled").GetBoolean(), "Changed fast-forward value was not saved.");
            Assert(options.GetProperty("wandererLimit").GetInt32() == 47, "Changed wanderer limit was not saved.");
            Assert(!options.GetProperty("smithingStaminaRecoveryOutsideSettlements").GetBoolean(), "Changed smithing location value was not saved.");

            var reloaded = service.LoadModConfig();
            Assert(!reloaded.FastForwardEnabled, "Saved fast-forward value did not reload.");
            Assert(reloaded.WandererLimit == 47, "Saved wanderer limit did not reload.");
        });
}

void TestPartialModOptions()
{
    const string partial = """
      "modOptions": {
        // preserve-partial-comment
        "fastForwardEnabled": false // preserve-inline-comment
      }
    """;

    WithTemporaryConfig(
        BuildConfig(partial),
        (service, path, _) =>
        {
            var config = service.LoadModConfig();
            Assert(!config.FastForwardEnabled, "Existing partial override was not loaded.");
            Assert(config.AutoPauseEnabled, "Missing partial option did not use its default.");

            service.SaveModConfig(config);

            var saved = File.ReadAllText(path);
            Assert(saved.Contains("preserve-partial-comment", StringComparison.Ordinal), "Block comment was removed.");
            Assert(saved.Contains("preserve-inline-comment", StringComparison.Ordinal), "Inline comment was removed.");

            using var document = ParseJsonc(saved);
            Assert(document.RootElement.GetProperty("modOptions").EnumerateObject().Count() == 14, "Missing partial options were not added.");
        });
}

void TestNullModOptions()
{
    WithTemporaryConfig(
        BuildConfig("  \"modOptions\": null"),
        (service, path, _) =>
        {
            AssertDefaults(service.LoadModConfig());
            service.SaveModConfig(service.LoadModConfig());

            using var document = ParseJsonc(File.ReadAllText(path));
            Assert(document.RootElement.GetProperty("modOptions").ValueKind == JsonValueKind.Object, "Null modOptions was not replaced by an object.");
        });
}

void TestDifficultySelectionsSave()
{
    WithTemporaryConfig(
        BuildConfig(),
        (service, path, _) =>
        {
            var config = service.LoadModConfig();
            config.PlayerReceivedDamageOverride = true;
            config.PlayerTroopsReceivedDamageOverride = true;
            config.CombatAIDifficultyOverride = true;
            config.RecruitmentDifficultyOverride = true;
            config.PlayerMapMovementSpeedOverride = true;
            config.StealthAndDisguiseDifficultyOverride = true;
            config.PersuasionSuccessChanceOverride = true;
            config.ClanMemberDeathChanceOverride = true;
            config.BattleDeathOverride = true;
            config.BirthAndDeathOverride = true;
            config.AutoAllocateClanMemberPerksOverride = true;

            config.PlayerReceivedDamage = "VeryEasy";
            config.CombatAIDifficulty = "Realistic";
            config.BirthAndDeath = false;
            config.AutoAllocateClanMemberPerks = true;
            service.SaveModConfig(config);

            using var document = ParseJsonc(File.ReadAllText(path));
            var difficulty = document.RootElement.GetProperty("difficulty");
            Assert(difficulty.EnumerateObject().Count() == 11, "All difficulty selections were not activated.");
            Assert(difficulty.GetProperty("playerReceivedDamage").GetString() == "VeryEasy", "Player damage selection was not saved.");
            Assert(difficulty.GetProperty("combatAIDifficulty").GetString() == "Realistic", "Combat AI selection was not saved.");
            Assert(!difficulty.GetProperty("birthAndDeath").GetBoolean(), "Birth/death selection was not saved.");
            Assert(difficulty.GetProperty("autoAllocateClanMemberPerks").GetBoolean(), "Auto-perks selection was not saved.");
        });
}

void TestDisabledScheduledRestarts()
{
    var settings = new BCSTool.Models.ServerSettings
    {
        ScheduledRestartsEnabled = false,
        RestartEveryHours = 0,
        RestartMinute = -1,
        WarningMinutesBefore = -1
    };

    Assert(settings.Validate().Count == 0, "Disabled scheduled restarts still required schedule values.");
}

void TestCoopPlayerListParser()
{
    const string output = """
    12:00:00 Side: Client  LocalControllerId: peer_Bluey
    12:00:00 Registered players: 2 (expected: one per client, host excluded)
    12:00:00 - ControllerId: peer_Other
    12:00:00     Hero: hero_other resolved, controlled=True
    12:00:00     Party: party_other resolved, controlled=True
    12:00:00     Clan: clan_other resolved, controlled=True
    12:00:00 - ControllerId: peer_Bluey (you)
    12:00:00     Hero: hero_bluey resolved, controlled=True
    12:00:00     Party: party_bluey resolved, controlled=True
    12:00:00     Clan: clan_bluey resolved, controlled=True
    12:00:00 PlayerObjects entries (resolved & controlled): 6
    """;

    var parser = new CoopPlayerListParser();
    Assert(parser.TryParse(output, out var players), "Complete player-list response was not recognized.");
    Assert(players.Count == 2, "Wrong player count was parsed.");
    Assert(players[0].IsLocalPlayer, "The player marked '(you)' was not placed first.");
    Assert(players[0].DisplayName == "Bluey (you)", "Local player display name was incorrect.");
    Assert(players[0].HeroId == "hero_bluey", "Hero ID was not parsed.");
    Assert(players[0].PartyId == "party_bluey", "Party ID was not parsed.");
    Assert(players[0].ClanId == "clan_bluey", "Clan ID was not parsed.");
    Assert(players[1].ControllerId == "peer_Other", "Controller ID was not parsed.");
}

void TestContentOnlyCompatibilityAnalysis()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var selectedPath = CreateModule(
                modulesDirectory,
                "ContentOnly",
                "v1.0.0",
                ["Native", "Coop"],
                contentXml: "<Items><Item id=\"content_only_sword\" /></Items>");

            var before = FingerprintFiles(selectedPath);
            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "ContentOnly");
            var report = new CoopCompatibilityAnalyzer().Analyze(selected, modules, serverRoot);
            var after = FingerprintFiles(selectedPath);

            Assert(
                report.OverallStatus == CoopCompatibilityStatus.LikelyCompatible,
                $"Unexpected content-only result: {report.OverallStatus}.");
            Assert(report.CoopVersion == "v0.1.1", "Installed Coop baseline version was not recorded.");
            Assert(report.GameVersion == "v1.4.7", "Installed Bannerlord version was not recorded.");
            Assert(report.ModuleFingerprint.Length == 64, "Module analysis fingerprint was not SHA-256.");
            Assert(report.CoopFingerprint.Length == 64, "Coop analysis fingerprint was not SHA-256.");
            Assert(
                report.Findings.Any(finding => finding.Area == "Client parity"),
                "Client parity was not explicitly marked UNKNOWN.");
            Assert(before.SequenceEqual(after), "Read-only analysis changed module files.");
        });
}

void TestMissingDependencyCompatibilityAnalysis()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            CreateModule(modulesDirectory, "BrokenMod", "v1.0.0", ["MissingFramework"]);

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "BrokenMod");
            var report = new CoopCompatibilityAnalyzer().Analyze(selected, modules, serverRoot);

            Assert(
                report.OverallStatus == CoopCompatibilityStatus.Blocked,
                "Missing dependency did not block compatibility testing.");
            Assert(
                report.Findings.Any(finding =>
                    finding.Severity == CompatibilityFindingSeverity.Blocker &&
                    finding.Finding.Contains("MissingFramework", StringComparison.Ordinal)),
                "Missing dependency evidence was not reported.");
        });
}

void TestCustomCampaignCodeCompatibilityAnalysis()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var selectedPath = CreateModule(
                modulesDirectory,
                "CampaignCodeMod",
                "v1.0.0",
                ["Native", "Coop"],
                declaredDll: "CampaignCodeMod.dll");
            var bin = Path.Combine(selectedPath, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(bin);
            File.Copy(
                typeof(RegressionCampaignBehavior).Assembly.Location,
                Path.Combine(bin, "CampaignCodeMod.dll"));

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "CampaignCodeMod");
            var report = new CoopCompatibilityAnalyzer().Analyze(selected, modules, serverRoot);

            Assert(
                report.OverallStatus == CoopCompatibilityStatus.BridgeLikelyRequired,
                $"Custom campaign code result was {report.OverallStatus}.");
            Assert(
                report.Findings.Any(finding =>
                    finding.Severity == CompatibilityFindingSeverity.HighRisk &&
                    finding.Area == "Campaign synchronization" &&
                    finding.Evidence.Contains(nameof(RegressionCampaignBehavior), StringComparison.Ordinal)),
                "Custom campaign behavior evidence was not reported.");
        });
}

void TestDependencyVersionCompatibilityAnalysis()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var selectedPath = CreateModule(
                modulesDirectory,
                "OldTargetMod",
                "v1.0.0",
                ["Native", "Coop"]);
            var manifestPath = Path.Combine(selectedPath, "SubModule.xml");
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace(
                    "<DependedModule Id=\"Native\" />",
                    "<DependedModule Id=\"Native\" DependentVersion=\"v1.4.6\" />",
                    StringComparison.Ordinal));

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "OldTargetMod");
            var report = new CoopCompatibilityAnalyzer().Analyze(selected, modules, serverRoot);

            Assert(
                report.OverallStatus == CoopCompatibilityStatus.TestingRequired,
                "Dependency version mismatch did not require testing.");
            Assert(
                report.Findings.Any(finding =>
                    finding.Area == "Dependency versions" &&
                    finding.Evidence.Contains("v1.4.6", StringComparison.Ordinal) &&
                    finding.Evidence.Contains("v1.4.7", StringComparison.Ordinal)),
                "Dependency version mismatch evidence was not reported.");
        });
}

void TestCompatibilityAnalyzerRejectsDtd()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            CreateModule(
                modulesDirectory,
                "UnsafeXmlMod",
                "v1.0.0",
                ["Native", "Coop"],
                contentXml:
                    "<!DOCTYPE Items [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                    "<Items>&xxe;</Items>");

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "UnsafeXmlMod");
            var report = new CoopCompatibilityAnalyzer().Analyze(selected, modules, serverRoot);

            Assert(
                report.Findings.Any(finding =>
                    finding.Area == "XML" &&
                    finding.Finding.Contains("could not be parsed safely", StringComparison.OrdinalIgnoreCase)),
                "DTD-bearing XML was not rejected by the safe parser.");
        });
}

void TestManagedModuleLaunchPlan()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var executable = PrepareFakeDedicatedServer(serverRoot);
            var bootstrap = Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll");
            File.WriteAllBytes(bootstrap, [1]);

            CreateModule(modulesDirectory, "Bannerlord.Harmony", "v2.4.2");
            Directory.CreateDirectory(
                Path.Combine(modulesDirectory, "Bannerlord.Harmony", "bin", "Win64_Shipping_Client"));
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "DedicatedServer.Windows", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "SandBoxCore", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "Sandbox", "v1.4.7", ["SandBoxCore"]);
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Sandbox"]);
            CreateModule(
                modulesDirectory,
                "BCS.CoopBridge.123456789abc",
                "v0.1.0",
                ["Coop"]);

            var manager = new ModuleManager(executable, new ModuleScanner());
            var byId = manager.Load().ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
            byId["Bannerlord.Harmony"].SetInitialEnabled(true);
            byId["BCS.CoopBridge.123456789abc"].SetInitialEnabled(true);
            manager.Save(
                [
                    byId["Bannerlord.Harmony"],
                    byId["Native"],
                    byId["DedicatedServer.Windows"],
                    byId["SandBoxCore"],
                    byId["Sandbox"],
                    byId["BCS.CoopBridge.123456789abc"],
                    byId["Coop"]
                ]);

            var plan = new DedicatedServerLaunchBuilder(new ModuleScanner(), bootstrap)
                .Build(executable, serverRoot);

            Assert(plan.UsesManagedModuleProfile, "Saved profile did not select direct engine launch.");
            Assert(Path.GetFileName(plan.ExecutablePath) == "dotnet.exe", "Bundled dotnet host was not selected.");
            Assert(
                plan.Arguments[1] ==
                "_MODULES_*Bannerlord.Harmony*Native*DedicatedServer.Windows*SandBoxCore*Sandbox*Coop*BCS.CoopBridge.123456789abc*_MODULES_",
                "Engine module token did not move the generated bridge behind all gameplay modules.");
            Assert(
                plan.Arguments[2] == "/dedicatedcustomserver" &&
                DedicatedServerLaunchBuilder.CoopServerPort == 4200 &&
                plan.Arguments[3] == DedicatedServerLaunchBuilder.CoopServerPort.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "Managed dedicated server did not bind Coop's required UDP port 4200.");
            var settingsWithLegacyPort = new ServerSettings
            {
                ServerPort = int.MaxValue
            };
            Assert(
                !settingsWithLegacyPort.Validate().Any(error =>
                    error.Contains("port", StringComparison.OrdinalIgnoreCase)),
                "Retired ServerPort setting still affected runtime settings validation.");
            Assert(
                plan.Environment["DOTNET_STARTUP_HOOKS"] == bootstrap,
                "BCS runtime bootstrap was not isolated to the server child process.");
            Assert(
                plan.Environment["BCSTOOL_DS_MANAGED_SEARCH_DIRECTORIES"]!
                    .Contains("Bannerlord.Harmony", StringComparison.OrdinalIgnoreCase),
                "Enabled module bin directory was not added to managed resolution.");
        });
}

void TestManagedDependencyProfile()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var executable = PrepareFakeDedicatedServer(serverRoot);
            var bootstrap = Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll");
            File.WriteAllBytes(bootstrap, [1]);
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "DedicatedServer.Windows", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "SandBoxCore", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "Sandbox", "v1.4.7", ["SandBoxCore"]);
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Sandbox"]);

            var manager = new ModuleManager(executable, new ModuleScanner());
            var byId = manager.Load().ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
            manager.Save(
                [
                    byId["Native"],
                    byId["DedicatedServer.Windows"],
                    byId["SandBoxCore"],
                    byId["Sandbox"],
                    byId["Coop"]
                ]);

            var externalDirectory = Path.Combine(serverRoot, "official-client-runtime");
            Directory.CreateDirectory(externalDirectory);
            var dependencyPath = Path.Combine(externalDirectory, "StoryMode.dll");
            File.WriteAllBytes(dependencyPath, [4, 2, 4, 2]);
            var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dependencyPath)));
            File.WriteAllText(
                Path.Combine(serverRoot, DedicatedServerLaunchBuilder.ManagedDependencyProfileFileName),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    Directories = new[]
                    {
                        new
                        {
                            Path = externalDirectory,
                            RequiredFiles = new[]
                            {
                                new { Name = "StoryMode.dll", Sha256 = expectedHash }
                            }
                        }
                    }
                }));

            var builder = new DedicatedServerLaunchBuilder(new ModuleScanner(), bootstrap);
            var plan = builder.Build(executable, serverRoot);
            var searchDirectories = plan.Environment["BCSTOOL_DS_MANAGED_SEARCH_DIRECTORIES"]!;
            Assert(
                searchDirectories.Split(Path.PathSeparator)
                    .Contains(externalDirectory, StringComparer.OrdinalIgnoreCase),
                "Required external dependency directory was not added to the child resolver.");

            File.WriteAllBytes(dependencyPath, [9, 9, 9]);
            _ = builder.Build(executable, serverRoot);
            File.Delete(dependencyPath);
            var missingFileRejected = false;
            try
            {
                _ = builder.Build(executable, serverRoot);
            }
            catch (FileNotFoundException)
            {
                missingFileRejected = true;
            }
            Assert(missingFileRejected,
                "Managed dependency profile accepted a missing required file.");
        });
}

void TestInvalidManagedModuleLaunchPlan()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var executable = PrepareFakeDedicatedServer(serverRoot);
            var bootstrap = Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll");
            File.WriteAllBytes(bootstrap, [1]);
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "DedicatedServer.Windows", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "SandBoxCore", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "Sandbox", "v1.4.7", ["SandBoxCore"]);
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Sandbox"]);

            var manager = new ModuleManager(executable, new ModuleScanner());
            var byId = manager.Load().ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
            manager.Save(
                [
                    byId["Native"],
                    byId["DedicatedServer.Windows"],
                    byId["Sandbox"],
                    byId["SandBoxCore"],
                    byId["Coop"]
                ]);

            try
            {
                _ = new DedicatedServerLaunchBuilder(new ModuleScanner(), bootstrap)
                    .Build(executable, serverRoot);
                throw new InvalidOperationException("Invalid load order was accepted.");
            }
            catch (InvalidDataException exception)
            {
                Assert(
                    exception.Message.Contains("SandBoxCore", StringComparison.OrdinalIgnoreCase) &&
                    exception.Message.Contains("Sandbox", StringComparison.OrdinalIgnoreCase),
                    "Invalid load-order error did not identify the broken dependency order.");
            }
        });
}

void TestConPtyArgumentQuoting()
{
    Assert(ConPtySession.QuoteArgument("plain") == "plain", "Plain argument was unnecessarily changed.");
    Assert(
        ConPtySession.QuoteArgument("path with spaces\\") == "\"path with spaces\\\\\"",
        "Trailing backslash was not escaped inside quotes.");
    Assert(
        ConPtySession.QuoteArgument("a\"b") == "\"a\\\"b\"",
        "Embedded quote was not escaped.");
}

void TestServerConsoleLogWriter()
{
    var rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "bcs-server-console-log-regression-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(rootDirectory);

    try
    {
        var legacyDirectory = Path.Combine(rootDirectory, "legacy-retention");
        Directory.CreateDirectory(legacyDirectory);

        var oldLogs = new List<string>();
        for (var index = 0; index < 11; index++)
        {
            var path = Path.Combine(
                legacyDirectory,
                $"coop-server-20260101-0000{index:00}.log");
            File.WriteAllText(path, "old-" + index);
            File.SetLastWriteTimeUtc(
                path,
                new DateTime(2026, 1, 1, 0, index, 0, DateTimeKind.Utc));
            oldLogs.Add(path);
        }

        const string firstChunk = "plain server output\r\n";
        const string secondChunk = "\u001b[31mcolored diagnostic\u001b[0m\r\n";
        string currentLog;

        using (var writer = ServerConsoleLogWriter.Create(
                   legacyDirectory,
                   new DateTime(2026, 8, 13, 13, 5, 7)))
        {
            currentLog = writer.FilePath;
            writer.Append(firstChunk);
            writer.Append(secondChunk);
            writer.Flush();

            using var stream = new FileStream(
                currentLog,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert(
                reader.ReadToEnd() == firstChunk + secondChunk,
                "ConPTY output chunks were not preserved exactly in the server log.");
        }

        var retained = Directory.GetFiles(legacyDirectory, "coop-server-*.log");
        Assert(retained.Length == 10, "Server log rotation did not retain exactly ten logs.");
        Assert(retained.Contains(currentLog), "Server log rotation removed the active log.");
        Assert(!File.Exists(oldLogs[0]) && !File.Exists(oldLogs[1]),
            "Server log rotation did not remove the two oldest logs.");

        var rolloverDirectory = Path.Combine(rootDirectory, "in-session-rollover");
        Directory.CreateDirectory(rolloverDirectory);

        for (var index = 0; index < 8; index++)
        {
            var path = Path.Combine(
                rolloverDirectory,
                $"coop-server-20260102-0000{index:00}.log");
            File.WriteAllText(path, "preexisting-" + index);
            File.SetLastWriteTimeUtc(
                path,
                new DateTime(2026, 1, 2, 0, index, 0, DateTimeKind.Utc));
        }

        var rolloverChunks = new[]
        {
            "first α chunk\r\n",
            "\u001b[31msecond β chunk\u001b[0m\r\n",
            "third γ chunk\r\n",
            "fourth δ chunk\r\n"
        };
        var testSegmentBytes =
            rolloverChunks.Max(chunk => System.Text.Encoding.UTF8.GetByteCount(chunk));
        IReadOnlyList<string> rolloverSegments;

        using (var writer = ServerConsoleLogWriter.CreateForTesting(
                   rolloverDirectory,
                   new DateTime(2026, 8, 13, 13, 6, 7),
                   testSegmentBytes))
        {
            foreach (var chunk in rolloverChunks)
                writer.Append(chunk);

            writer.Flush();
            rolloverSegments = writer.SegmentFilePaths;
        }

        Assert(
            rolloverSegments.Count == rolloverChunks.Length,
            "Server log did not roll at each forced in-session segment boundary.");
        Assert(
            string.Concat(rolloverSegments.Select(File.ReadAllText)) ==
            string.Concat(rolloverChunks),
            "In-session rollover changed the order or content of ConPTY output chunks.");
        Assert(
            rolloverSegments.All(path => new FileInfo(path).Length <= testSegmentBytes),
            "A normal ConPTY chunk crossed the configured server-log segment boundary.");
        Assert(
            rolloverSegments.All(File.Exists),
            "Mixed retention removed a current-session server-log segment before the ten-file limit.");
        Assert(
            Directory.GetFiles(rolloverDirectory, "coop-server-*.log").Length == 10,
            "Mixed preexisting and session log retention did not keep exactly ten files.");

        var namingDirectory = Path.Combine(rootDirectory, "safe-naming");
        Directory.CreateDirectory(namingDirectory);
        var namingTimestamp = new DateTime(2026, 8, 13, 13, 7, 7);
        var namingStem = Path.Combine(namingDirectory, "coop-server-20260813-130707");
        var occupiedBase = namingStem + ".log";
        var occupiedFirstSuffix = namingStem + "-1.log";
        var occupiedFutureSuffix = namingStem + "-3.log";
        File.WriteAllText(occupiedBase, "occupied-base");
        File.WriteAllText(occupiedFirstSuffix, "occupied-one");

        IReadOnlyList<string> safelyNamedSegments;
        using (var writer = ServerConsoleLogWriter.CreateForTesting(
                   namingDirectory,
                   namingTimestamp,
                   maximumSegmentBytes: 4))
        {
            Assert(
                writer.FilePath == namingStem + "-2.log",
                "Server log did not choose the first unused collision-safe session name.");

            writer.Append("1234");
            File.WriteAllText(occupiedFutureSuffix, "occupied-three");
            writer.Append("5678");
            writer.Flush();
            safelyNamedSegments = writer.SegmentFilePaths;
        }

        Assert(
            safelyNamedSegments.SequenceEqual(
                new[] { namingStem + "-2.log", namingStem + "-4.log" }),
            "In-session rollover reused an occupied server-log path.");
        Assert(
            File.ReadAllText(occupiedBase) == "occupied-base" &&
            File.ReadAllText(occupiedFirstSuffix) == "occupied-one" &&
            File.ReadAllText(occupiedFutureSuffix) == "occupied-three",
            "Server log collision handling overwrote an existing file.");
        Assert(
            string.Concat(safelyNamedSegments.Select(File.ReadAllText)) == "12345678",
            "Collision-safe rollover changed the output stream.");

        var longSessionDirectory = Path.Combine(rootDirectory, "long-session-retention");
        Directory.CreateDirectory(longSessionDirectory);
        var longSessionChunks =
            Enumerable.Range(0, 12)
                .Select(index => index.ToString("D2"))
                .ToArray();
        IReadOnlyList<string> longSessionSegments;

        using (var writer = ServerConsoleLogWriter.CreateForTesting(
                   longSessionDirectory,
                   new DateTime(2026, 8, 13, 13, 8, 7),
                   maximumSegmentBytes: 2))
        {
            foreach (var chunk in longSessionChunks)
                writer.Append(chunk);

            writer.Flush();
            longSessionSegments = writer.SegmentFilePaths;
        }

        Assert(
            longSessionSegments.Count == longSessionChunks.Length,
            "Server log segment history omitted a long-session rollover.");
        Assert(
            longSessionSegments.Take(2).All(path => !File.Exists(path)) &&
            longSessionSegments.Skip(2).All(File.Exists),
            "Long-session rotation did not retain exactly the latest ten segments.");
        Assert(
            Directory.GetFiles(longSessionDirectory, "coop-server-*.log").Length == 10,
            "Long-session rotation exceeded the ten-file retention limit.");
        Assert(
            string.Concat(longSessionSegments.Skip(2).Select(File.ReadAllText)) ==
            string.Concat(longSessionChunks.Skip(2)),
            "Long-session rotation changed the retained output order or content.");
    }
    finally
    {
        Directory.Delete(rootDirectory, recursive: true);
    }
}

void TestContentCompatibilityPrepareAndRevert()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "LargeContentPack",
                "v2.0.0",
                ["Native", "StoryMode"],
                contentXml: "<Items><Item id=\"large_pack_sword\" /></Items>");
            var manifestPath = Path.Combine(modulePath, "SubModule.xml");
            var original = File.ReadAllBytes(manifestPath);
            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "LargeContentPack");
            var patcher = new CoopCompatibilityPatcher();

            var plan = patcher.CreatePlan(selected, modules, serverRoot);
            Assert(plan.CanApply, plan.Summary);
            Assert(plan.Changes.Count == 7,
                "Content-only preparation should include manifest, both bridge runtimes, client package, and profile changes.");

            var applied = patcher.Apply(plan);
            var prepared = File.ReadAllText(manifestPath);
            Assert(!prepared.Contains("StoryMode", StringComparison.Ordinal),
                "Client-only StoryMode dependency remained in the server manifest.");
            Assert(prepared.Contains("DependedModuleMetadata", StringComparison.Ordinal),
                "Coop load-order metadata was not added.");
            Assert(prepared.Contains("LoadBeforeThis", StringComparison.Ordinal),
                "Manifest did not require Coop to load before the custom module.");
            Assert(File.Exists(applied.ManifestPath), "Backup manifest was not written.");
            var bridgeId = plan.ModuleIds.Single(id =>
                id.StartsWith(CoopBridgePackageBuilder.BridgeIdPrefix, StringComparison.Ordinal));
            Assert(File.Exists(Path.Combine(
                    modulesDirectory,
                    bridgeId,
                    "bin",
                    "Win64_Shipping_Server",
                    "BCS.CoopBridge.dll")),
                "Generated bridge runtime was not installed on the server.");
            Assert(File.Exists(Path.Combine(
                    modulesDirectory,
                    bridgeId,
                    "bin",
                    "Win64_Shipping_Client",
                    "BCS.CoopBridge.dll")),
                "Generated bridge client runtime was not installed beside the server runtime for identity validation.");
            Assert(File.Exists(Path.Combine(serverRoot, "bcs-client-packages", bridgeId + ".zip")),
                "Matching client bridge package was not created.");
            Assert(File.ReadAllText(Path.Combine(serverRoot, "bcs-server-modules.json"))
                    .Contains(bridgeId, StringComparison.Ordinal),
                "Generated bridge was not enabled in the server module profile.");

            patcher.Revert(applied.ManifestPath);
            Assert(File.ReadAllBytes(manifestPath).SequenceEqual(original),
                "Revert did not restore the exact original manifest bytes.");
            Assert(!Directory.Exists(Path.Combine(modulesDirectory, bridgeId)) ||
                   !Directory.EnumerateFiles(
                       Path.Combine(modulesDirectory, bridgeId),
                       "*",
                       SearchOption.AllDirectories).Any(),
                "Revert left generated bridge files behind.");
        });
}

void TestAppliedContentModuleReplansAsNoOp()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            CreateModule(
                modulesDirectory,
                "ReplanContentPack",
                "v1.0.0",
                ["Native"],
                contentXml: "<Items><Item id=\"replan_item\" /></Items>");
            var scanner = new ModuleScanner();
            var modules = scanner.Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "ReplanContentPack");
            var patcher = new CoopCompatibilityPatcher();
            var initialPlan = patcher.CreatePlan(selected, modules, serverRoot);
            Assert(initialPlan.CanApply, initialPlan.Summary);
            var applied = patcher.Apply(initialPlan);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                modules = scanner.Scan(modulesDirectory);
                selected = modules.Single(module => module.Id == "ReplanContentPack");
                var noOpPlan = patcher.CreatePlan(selected, modules, serverRoot);
                Assert(noOpPlan.Changes.Count == 0,
                    "An unchanged applied content-only module produced another compatibility change.");
                Assert(patcher.PendingPlanCount == 0,
                    "No-op compatibility replan retained private pending state.");
            }

            patcher.Revert(applied.ManifestPath);
        });
}

void TestPreparedModuleDllUnblock()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var modulePath = CreateModule(modulesDirectory, "BlockedModule", "v1.0.0");
            var workshopModulePath = Path.Combine(modulesDirectory, "3231544373");
            Directory.Move(modulePath, workshopModulePath);
            modulePath = workshopModulePath;
            var bin = Path.Combine(modulePath, "bin", "Win64_Shipping_Server");
            Directory.CreateDirectory(bin);
            var assemblyPath = Path.Combine(bin, "BlockedModule.dll");
            var assemblyBytes = new byte[] { 0x4D, 0x5A, 0x01, 0x02, 0x03 };
            File.WriteAllBytes(assemblyPath, assemblyBytes);
            var zoneIdentifier = assemblyPath + ":Zone.Identifier";
            File.WriteAllText(zoneIdentifier, "[ZoneTransfer]\r\nZoneId=3\r\n");

            var unblocked = new CoopCompatibilityPatcher().UnblockPreparedModuleAssemblies(
                serverRoot,
                ["BlockedModule"]);

            Assert(unblocked == 1, "Blocked assembly was not reported as unblocked.");
            Assert(!File.Exists(zoneIdentifier), "Zone.Identifier remained after unblocking.");
            Assert(File.ReadAllBytes(assemblyPath).SequenceEqual(assemblyBytes),
                "Unblocking changed the assembly bytes.");
        });
}

void TestPreparedModuleDllUnblockRollback()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var modulePath = CreateModule(modulesDirectory, "BlockedRollbackModule", "v1.0.0");
            var bin = Path.Combine(modulePath, "bin", "Win64_Shipping_Server");
            Directory.CreateDirectory(bin);
            var firstAssembly = Path.Combine(bin, "01-First.dll");
            var lockedAssembly = Path.Combine(bin, "02-Locked.dll");
            File.WriteAllBytes(firstAssembly, [0x4D, 0x5A, 0x01]);
            File.WriteAllBytes(lockedAssembly, [0x4D, 0x5A, 0x02]);
            var firstZone = firstAssembly + ":Zone.Identifier";
            var lockedZone = lockedAssembly + ":Zone.Identifier";
            var firstZoneBytes = System.Text.Encoding.UTF8.GetBytes(
                "[ZoneTransfer]\r\nZoneId=3\r\nFirst=1\r\n");
            var lockedZoneBytes = System.Text.Encoding.UTF8.GetBytes(
                "[ZoneTransfer]\r\nZoneId=3\r\nLocked=1\r\n");
            File.WriteAllBytes(firstZone, firstZoneBytes);
            File.WriteAllBytes(lockedZone, lockedZoneBytes);

            try
            {
                _ = new CoopCompatibilityPatcher().UnblockPreparedModuleAssemblies(
                    serverRoot,
                    ["BlockedRollbackModule", "MissingEnabledModule"]);
                throw new InvalidOperationException("Missing enabled module was unexpectedly accepted.");
            }
            catch (DirectoryNotFoundException)
            {
                // Expected: every enabled module is validated before the first ADS delete.
            }
            Assert(File.ReadAllBytes(firstZone).SequenceEqual(firstZoneBytes) &&
                   File.ReadAllBytes(lockedZone).SequenceEqual(lockedZoneBytes),
                "Prevalidation failure removed or changed an earlier Zone.Identifier.");

            using var heldMarker = new FileStream(
                lockedZone,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            try
            {
                _ = new CoopCompatibilityPatcher().UnblockPreparedModuleAssemblies(
                    serverRoot,
                    ["BlockedRollbackModule"]);
                throw new InvalidOperationException("Locked blocked-file marker was unexpectedly removed.");
            }
            catch (IOException)
            {
                // Expected: the held ADS allows validation reads but denies deletion.
            }

            Assert(File.Exists(firstZone) &&
                   File.ReadAllBytes(firstZone).SequenceEqual(firstZoneBytes),
                "Earlier Zone.Identifier bytes were not restored after a later unblock failure.");
            Assert(File.Exists(lockedZone) &&
                   File.ReadAllBytes(lockedZone).SequenceEqual(lockedZoneBytes),
                "Locked Zone.Identifier bytes changed during failed unblocking.");
        });
}

void TestGenericBridgePackage()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var contentPath = CreateModule(
                modulesDirectory,
                "ContentPack",
                "v3.2.1",
                ["Native"],
                contentXml: "<Items><Item id=\"content_a\" /></Items>");
            var modules = new ModuleScanner().Scan(modulesDirectory);
            var packageBuilder = new CoopBridgePackageBuilder();
            var inputs = modules.Where(module => module.Id is "Coop" or "ContentPack").ToArray();
            var first = packageBuilder.Build(inputs);
            var second = packageBuilder.Build(inputs.Reverse().ToArray());
            var visualExclusion = new BridgeContentExclusion(
                "ContentPack",
                "ModuleData/content.xml");
            var excludedFirst = packageBuilder.Build(
                inputs,
                contentExclusions: [visualExclusion]);
            var plannedServerSidecar = Path.Combine(
                contentPath,
                "bin",
                "Win64_Shipping_Server",
                "generated-server-config.json");
            var plannedExclusion = new BridgeContentExclusion(
                "ContentPack",
                "bin/Win64_Shipping_Server/generated-server-config.json");
            var rejectedUnplannedExclusion = false;
            try
            {
                packageBuilder.Build(
                    inputs,
                    contentExclusions: [plannedExclusion]);
            }
            catch (InvalidDataException)
            {
                rejectedUnplannedExclusion = true;
            }
            Assert(rejectedUnplannedExclusion,
                "Bridge accepted a missing ignored-content path that was not planned.");
            var plannedPackage = packageBuilder.Build(
                inputs,
                contentExclusions: [plannedExclusion],
                plannedContentPaths: [plannedServerSidecar]);
            Assert(System.Text.Encoding.UTF8.GetString(plannedPackage.Configuration)
                    .Contains("IGNORE_CONTENT|", StringComparison.Ordinal),
                "Bridge omitted an explicitly planned server-only content exclusion.");

            var externalModule = CreateModule(
                modulesDirectory,
                "ExternalRuntime",
                "v1.0.0");
            var externalBin = Path.Combine(externalModule, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(externalBin);
            var externalAssembly = Path.Combine(externalBin, "BCSTool.RegressionTests.dll");
            File.Copy(typeof(RegressionCampaignBehavior).Assembly.Location, externalAssembly);
            var externalHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(externalAssembly)));
            var resolver = new BridgeClientAssemblyResolve(
                "ExternalRuntime",
                "bin/Win64_Shipping_Client/BCSTool.RegressionTests.dll",
                externalHash,
                externalAssembly);
            var resolvedPackage = packageBuilder.Build(
                inputs,
                clientAssemblyResolves: [resolver]);
            Assert(resolvedPackage.ClientAssemblyResolves.SequenceEqual([resolver]),
                "Bridge package omitted its client assembly resolver.");
            Assert(System.Text.Encoding.UTF8.GetString(resolvedPackage.Configuration)
                    .Contains("CLIENT_ASSEMBLY_RESOLVE|", StringComparison.Ordinal),
                "Bridge configuration omitted its client assembly resolver.");
            var resolverWithoutHash = packageBuilder.Build(
                inputs,
                clientAssemblyResolves: [resolver with { Sha256 = string.Empty }]);
            Assert(resolverWithoutHash.ClientAssemblyResolves.Count == 1,
                "Bridge rejected an identity-valid client assembly resolver without a byte pin.");

            Assert(first.ModuleId == second.ModuleId,
                "Bridge ID changed when equivalent inputs were reordered.");
            Assert(first.Configuration.SequenceEqual(second.Configuration),
                "Bridge configuration was not deterministic.");
            Assert(first.ClientPackageZip.SequenceEqual(second.ClientPackageZip),
                "Client bridge package was not deterministic.");
            var legacyConfigOnlyId = CoopBridgePackageBuilder.BridgeIdPrefix +
                                     Convert.ToHexString(SHA256.HashData(first.Configuration))[..24]
                                         .ToLowerInvariant();
            Assert(first.ModuleId != legacyConfigOnlyId,
                "Bridge ID did not bind the runtime assembly bytes.");
            var legacySingleAssemblyPayload = new byte[
                first.Configuration.Length + first.Assembly.Length];
            Buffer.BlockCopy(
                first.Configuration,
                0,
                legacySingleAssemblyPayload,
                0,
                first.Configuration.Length);
            Buffer.BlockCopy(
                first.Assembly,
                0,
                legacySingleAssemblyPayload,
                first.Configuration.Length,
                first.Assembly.Length);
            var legacySingleAssemblyId = CoopBridgePackageBuilder.BridgeIdPrefix +
                                         Convert.ToHexString(
                                                 SHA256.HashData(legacySingleAssemblyPayload))[..24]
                                             .ToLowerInvariant();
            Assert(first.ModuleId != legacySingleAssemblyId,
                "Bridge ID did not bind the client runtime assembly bytes.");
            Assert(!first.Assembly.SequenceEqual(first.ClientAssembly),
                "Server and client bridge runtimes were not split.");
            foreach (var runtime in new[] { first.Assembly, first.ClientAssembly })
            {
                var runtimeText = System.Text.Encoding.Latin1.GetString(runtime);
                Assert(!runtimeText.Contains("C:\\Users\\", StringComparison.OrdinalIgnoreCase) &&
                       !runtimeText.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) &&
                       !runtimeText.Contains(".pdb", StringComparison.OrdinalIgnoreCase),
                    "Packaged bridge runtime leaked a local debug path.");
            }
            using (var serverPe = new PEReader(new MemoryStream(first.Assembly)))
            using (var clientPe = new PEReader(new MemoryStream(first.ClientAssembly)))
            {
                var serverMetadata = serverPe.GetMetadataReader();
                var clientMetadata = clientPe.GetMetadataReader();
                var serverTypes = serverMetadata.TypeDefinitions
                    .Select(handle => serverMetadata.GetTypeDefinition(handle))
                    .Select(type =>
                        serverMetadata.GetString(type.Namespace) + "." +
                        serverMetadata.GetString(type.Name))
                    .ToHashSet(StringComparer.Ordinal);
                var clientTypes = clientMetadata.TypeDefinitions
                    .Select(handle => clientMetadata.GetTypeDefinition(handle))
                    .Select(type =>
                        clientMetadata.GetString(type.Namespace) + "." +
                        clientMetadata.GetString(type.Name))
                    .ToHashSet(StringComparer.Ordinal);
                var serverReferences = serverMetadata.AssemblyReferences
                    .Select(handle => serverMetadata.GetString(
                        serverMetadata.GetAssemblyReference(handle).Name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var clientReferences = clientMetadata.AssemblyReferences
                    .Select(handle => clientMetadata.GetString(
                        clientMetadata.GetAssemblyReference(handle).Name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var clientCompatibilityMethods = clientMetadata.TypeDefinitions
                    .Select(handle => clientMetadata.GetTypeDefinition(handle))
                    .Where(type => clientMetadata.GetString(type.Name)
                        .Equals("ClientMapEventCompatibility", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => clientMetadata.GetString(
                        clientMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var serverFailedIdMethods = serverMetadata.TypeDefinitions
                    .Select(handle => serverMetadata.GetTypeDefinition(handle))
                    .Where(type => serverMetadata.GetString(type.Name)
                        .Equals("ServerFailedIdCompatibility", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => serverMetadata.GetString(
                        serverMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var serverRegistryLifecycleMethods = serverMetadata.TypeDefinitions
                    .Select(handle => serverMetadata.GetTypeDefinition(handle))
                    .Where(type => serverMetadata.GetString(type.Name)
                        .Equals("CoopRegistryLifecycleCompatibility", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => serverMetadata.GetString(
                        serverMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var clientRegistryLifecycleMethods = clientMetadata.TypeDefinitions
                    .Select(handle => clientMetadata.GetTypeDefinition(handle))
                    .Where(type => clientMetadata.GetString(type.Name)
                        .Equals("CoopRegistryLifecycleCompatibility", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => clientMetadata.GetString(
                        clientMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var clientCharacterCreationLifecycleMethods = clientMetadata.TypeDefinitions
                    .Select(handle => clientMetadata.GetTypeDefinition(handle))
                    .Where(type => clientMetadata.GetString(type.Name)
                        .Equals(
                            "ClientCharacterCreationLifecycleCompatibility",
                            StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => clientMetadata.GetString(
                        clientMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var clientCharacterCreationGateMethods = clientMetadata.TypeDefinitions
                    .Select(handle => clientMetadata.GetTypeDefinition(handle))
                    .Where(type => clientMetadata.GetString(type.Name)
                        .Equals(
                            "ClientCharacterCreationLifecycleGate",
                            StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => clientMetadata.GetString(
                        clientMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var serverBridgeRuntimeMethods = serverMetadata.TypeDefinitions
                    .Select(handle => serverMetadata.GetTypeDefinition(handle))
                    .Where(type => serverMetadata.GetString(type.Name)
                        .Equals("BridgeRuntime", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => serverMetadata.GetString(
                        serverMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                var clientBridgeRuntimeMethods = clientMetadata.TypeDefinitions
                    .Select(handle => clientMetadata.GetTypeDefinition(handle))
                    .Where(type => clientMetadata.GetString(type.Name)
                        .Equals("BridgeRuntime", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => clientMetadata.GetString(
                        clientMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                Assert(serverReferences.Contains("Common") &&
                       serverReferences.Contains("GameInterface"),
                    "Server bridge runtime lost required Coop server references.");
                Assert(!clientReferences.Contains("Common") &&
                       !clientReferences.Contains("GameInterface"),
                    "Client bridge runtime regained an early Coop support-assembly reference.");
                Assert(serverTypes.Contains("BCS.CoopBridge.ServerMapTerrainSizePrefix"),
            "Server bridge runtime lost its map terrain size prefix.");
                Assert(!clientTypes.Contains("BCS.CoopBridge.ServerMapTerrainSizePrefix"),
                    "Client bridge runtime contains the server-only map terrain size prefix.");
                Assert(!clientReferences.Contains("SandBox") &&
                       !clientReferences.Contains("TaleWorlds.Library"),
                    "Client bridge runtime gained a direct map-terrain patch dependency.");
                Assert(clientCompatibilityMethods.Contains("BeforeGetLeaderParty") &&
                       clientCompatibilityMethods.Contains("BeforeGetNumberOfInvolvedMen"),
                    "Client bridge runtime lost an EOE invalid-map-event-side guard.");
                Assert(clientTypes.Contains(
                           "BCS.CoopBridge.ClientCharacterCreationLifecycleCompatibility") &&
                       clientTypes.Contains(
                           "BCS.CoopBridge.ClientCharacterCreationLifecycleGate") &&
                       !serverTypes.Contains(
                           "BCS.CoopBridge.ClientCharacterCreationLifecycleCompatibility") &&
                       !serverTypes.Contains(
                           "BCS.CoopBridge.ClientCharacterCreationLifecycleGate") &&
                       clientCharacterCreationLifecycleMethods.Contains(
                           "BeforeStartCharacterCreation") &&
                       clientCharacterCreationLifecycleMethods.Contains(
                           "BeforeCharacterCreationStarted") &&
                       clientCharacterCreationLifecycleMethods.Contains(
                           "BeforeValidateModuleStateDisposed") &&
                       clientCharacterCreationLifecycleMethods.Contains(
                           "AfterCharacterCreationActivated") &&
                       clientCharacterCreationLifecycleMethods.Contains(
                           "AfterVideoPlaybackStarted") &&
                       clientCharacterCreationGateMethods.Contains("Arm") &&
                       clientCharacterCreationGateMethods.Contains("Cancel") &&
                       clientCharacterCreationGateMethods.Contains("TryClaim") &&
                       clientCharacterCreationGateMethods.Contains(
                           "TryClaimIntroLoadingOverlayRelease"),
                    "Client bridge runtime lost the scoped character-creation lifecycle repair.");
                Assert(serverTypes.Contains("BCS.CoopBridge.ServerFailedIdCompatibility") &&
                       serverFailedIdMethods.Contains("BeforePartyVisualDestroyed") &&
                       serverFailedIdMethods.Contains("BeforeWorkshopWarehouseRosterConstruction") &&
                       serverFailedIdMethods.Contains("FinalizeWorkshopWarehouseRosterConstruction") &&
                       serverFailedIdMethods.Contains("InstallItemRosterMissingIdDiagnostic") &&
                       serverFailedIdMethods.Contains("BeforeMissingItemRosterId") &&
                       serverFailedIdMethods.Contains("BeforeWorkshopOutputCategoryLookup") &&
                       serverFailedIdMethods.Contains("AfterAutoSyncPatchAll") &&
                       serverFailedIdMethods.Contains("BeforeHeadlessMapEventVisual"),
                    "Server bridge runtime lost a targeted failed-ID lifecycle fix.");
                Assert(!clientTypes.Contains("BCS.CoopBridge.ServerFailedIdCompatibility"),
                    "Client bridge runtime contains server-only failed-ID lifecycle fixes.");
                Assert(serverTypes.Contains("BCS.CoopBridge.CoopRegistryLifecycleCompatibility") &&
                       clientTypes.Contains("BCS.CoopBridge.CoopRegistryLifecycleCompatibility") &&
                       serverRegistryLifecycleMethods.Contains("BeforeRegisterAllArmies") &&
                       serverRegistryLifecycleMethods.Contains("BeforeArmyAiBehaviorObjectChanged") &&
                       serverRegistryLifecycleMethods.Contains("BeforeNetworkSetArmyAiBehaviorObject") &&
                       serverRegistryLifecycleMethods.Contains("BeforePartyComponentMobilePartyUpdated") &&
                       serverRegistryLifecycleMethods.Contains("TranspileRegisterAllGameObjects") &&
                       serverRegistryLifecycleMethods.Contains("ReplayDeferredPartyComponentUpdates") &&
                       clientRegistryLifecycleMethods.Contains("BeforeRegisterAllArmies") &&
                       clientRegistryLifecycleMethods.Contains("BeforeArmyAiBehaviorObjectChanged") &&
                       clientRegistryLifecycleMethods.Contains("BeforeNetworkSetArmyAiBehaviorObject"),
                    "Packaged bridge runtimes lost deterministic Army or PartyComponent lifecycle compatibility.");
                Assert(serverTypes.Contains("BCS.CoopBridge.ServerCurrentVersionCompatibility") &&
                       !clientTypes.Contains("BCS.CoopBridge.ServerCurrentVersionCompatibility"),
                    "MBSaveLoad current-version repair is missing from the server or leaked into the client runtime.");
                var serverPopulationMethods = serverMetadata.TypeDefinitions
                    .Select(handle => serverMetadata.GetTypeDefinition(handle))
                    .Where(type => serverMetadata.GetString(type.Name)
                        .Equals("ServerPopulationControl", StringComparison.Ordinal))
                    .SelectMany(type => type.GetMethods())
                    .Select(handle => serverMetadata.GetString(
                        serverMetadata.GetMethodDefinition(handle).Name))
                    .ToHashSet(StringComparer.Ordinal);
                Assert(serverTypes.Contains("BCS.CoopBridge.ServerPopulationControl") &&
                       serverPopulationMethods.Contains("BeforeSpawnCaravan") &&
                       serverPopulationMethods.Contains("ResolveCaravanTown") &&
                       serverPopulationMethods.Contains("BeforeCreateVillagerParty") &&
                       serverPopulationMethods.Contains(
                           "AfterGetMaximumBanditPartiesAroundEachHideout"),
                    "Server bridge runtime lost a population-control seam.");
                Assert(!clientTypes.Contains("BCS.CoopBridge.ServerPopulationControl"),
                    "Client bridge runtime contains server-only population controls.");
                Assert(first.Assembly.AsSpan().IndexOf(
                           System.Text.Encoding.Unicode.GetBytes(
                               "AUTOMATIC_NPC_CARAVANS_PER_TOWN")) >= 0 &&
                       first.Assembly.AsSpan().IndexOf(
                           System.Text.Encoding.Unicode.GetBytes(
                               "BCS-BRIDGE-POPULATION|2")) >= 0,
                    "Packaged server bridge lost population schema v2 or its per-town caravan setting.");
            }
            VerifyRuntimeModuleAssemblyValidation(first.Assembly);
            VerifyRuntimeModuleAssemblyValidation(first.ClientAssembly);

            File.WriteAllText(
                Path.Combine(contentPath, "ModuleData", "content.xml"),
                "<Items><Item id=\"content_b\" /></Items>");
            var changed = packageBuilder.Build(inputs);
            Assert(first.ModuleId == changed.ModuleId,
                "Gameplay content bytes changed the version-scoped bridge ID.");
            var excludedChanged = packageBuilder.Build(
                inputs,
                contentExclusions: [visualExclusion]);
            Assert(excludedFirst.ModuleId == excludedChanged.ModuleId,
                "Explicit server-only visual content changed the parity bridge ID.");
            Assert(System.Text.Encoding.UTF8.GetString(excludedFirst.Configuration)
                    .Contains("IGNORE_CONTENT|", StringComparison.Ordinal),
                "Visual content exclusion was not explicit in bridge configuration.");

            using var archive = new ZipArchive(
                new MemoryStream(first.ClientPackageZip),
                ZipArchiveMode.Read);
            Assert(archive.GetEntry($"Modules/{first.ModuleId}/README.txt") is not null,
                "Client package README was not scoped to the generated module directory.");
            Assert(archive.GetEntry($"Modules/{first.ModuleId}/LICENSE") is not null,
                "Client package omitted its GPL license notice.");
            Assert(archive.GetEntry($"Modules/{first.ModuleId}/NOTICE.md") is not null,
                "Client package omitted its attribution notice.");
            Assert(archive.GetEntry("README.txt") is null,
                "Client package would overwrite a game-root README during extraction.");
            Assert(archive.Entries.All(entry =>
                    !entry.FullName.EndsWith(
                        BridgePopulationSettingsService.SettingsFileName,
                        StringComparison.OrdinalIgnoreCase)),
                "Client bridge package included mutable server population settings.");
            var manifestEntry = archive.GetEntry($"Modules/{first.ModuleId}/SubModule.xml");
            Assert(manifestEntry is not null,
                "Client package omitted the bridge manifest.");
            var clientManifest = new XmlDocument { XmlResolver = null };
            using (var entryStream = manifestEntry!.Open())
                clientManifest.Load(entryStream);
            var clientDependencyIds = clientManifest
                .SelectNodes("/Module/DependedModules/DependedModule")!
                .OfType<XmlElement>()
                .Select(element => element.GetAttribute("Id"))
                .ToArray();
            Assert(clientDependencyIds.Length > 0 &&
                   clientDependencyIds[0].Equals("Coop", StringComparison.OrdinalIgnoreCase),
                "Client package did not put Coop first in bridge dependency traversal order.");
            var serverRuntimeEntry = archive.GetEntry(
                $"Modules/{first.ModuleId}/bin/Win64_Shipping_Server/BCS.CoopBridge.dll");
            Assert(serverRuntimeEntry is not null,
                "Client package omitted the server runtime required for full package identity validation.");
            using var serverRuntime = new MemoryStream();
            using (var entryStream = serverRuntimeEntry!.Open())
                entryStream.CopyTo(serverRuntime);
            Assert(serverRuntime.ToArray().SequenceEqual(first.Assembly),
                "Client package did not contain the exact server bridge runtime.");
            var clientRuntimeEntry = archive.GetEntry(
                $"Modules/{first.ModuleId}/bin/Win64_Shipping_Client/BCS.CoopBridge.dll");
            Assert(clientRuntimeEntry is not null,
                "Client package omitted the bridge runtime.");
            using var clientRuntime = new MemoryStream();
            using (var entryStream = clientRuntimeEntry!.Open())
                entryStream.CopyTo(clientRuntime);
            Assert(clientRuntime.ToArray().SequenceEqual(first.ClientAssembly),
                "Client package contained the server bridge runtime.");
        });
}

void TestReleasedCoopRoleParity()
{
    var releaseRoot = Path.Combine(
        Path.GetTempPath(),
        "bcs-coop-role-parity-" + Guid.NewGuid().ToString("N"));
    var modulesDirectory = Path.Combine(
        releaseRoot,
        "DedicatedServer",
        "engine",
        "Modules");
    Directory.CreateDirectory(modulesDirectory);
    try
    {
        var coopPath = CreateModule(modulesDirectory, "Coop", "v0.1.2");
        File.Copy(
            Path.Combine(coopPath, "SubModule.xml"),
            Path.Combine(releaseRoot, "SubModule.xml"));
        var serverBin = Path.Combine(coopPath, "bin", "Win64_Shipping_Server");
        var clientBin = Path.Combine(releaseRoot, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(serverBin);
        Directory.CreateDirectory(clientBin);
        WriteManagedAssembly(Path.Combine(serverBin, "Coop.dll"), "Coop");
        WriteManagedAssembly(Path.Combine(clientBin, "Coop.dll"), "Coop");
        using (var stream = new FileStream(
                   Path.Combine(clientBin, "Coop.dll"),
                   FileMode.Append,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.WriteByte(0x42);
        }
        WriteManagedAssembly(Path.Combine(serverBin, "0Harmony.dll"), "0Harmony");
        WriteManagedAssembly(Path.Combine(clientBin, "0Harmony.dll"), "0Harmony");
        WriteManagedAssembly(Path.Combine(serverBin, "ServerOnly.dll"), "ServerOnly");
        WriteManagedAssembly(Path.Combine(clientBin, "ClientOnly.dll"), "ClientOnly");

        var coop = new ModuleScanner().Scan(modulesDirectory).Single();
        var package = new CoopBridgePackageBuilder().Build([coop]);
        var names = package.ModuleRecords.Select(record => record.DllName).ToArray();
        Assert(names.Contains("Coop.dll", StringComparer.OrdinalIgnoreCase),
            "Bridge rejected byte-different Coop role assemblies with the same managed identity.");
        Assert(!names.Contains("0Harmony.dll", StringComparer.OrdinalIgnoreCase),
            "Bridge included Coop's role-specific Harmony support copy.");
        Assert(!names.Contains("ServerOnly.dll", StringComparer.OrdinalIgnoreCase),
            "Bridge included a Coop server-only support assembly on clients.");
        Assert(!names.Contains("ClientOnly.dll", StringComparer.OrdinalIgnoreCase),
            "Bridge included a Coop client-only support assembly on servers.");
    }
    finally
    {
        Directory.Delete(releaseRoot, recursive: true);
    }
}

void TestBridgeBuilderRejectsLinkedModulePaths()
{
    var root = Path.Combine(Path.GetTempPath(), "bcs-bridge-link-regression-" + Guid.NewGuid().ToString("N"));
    var modulesDirectory = Path.Combine(root, "Modules");
    var targetsDirectory = Path.Combine(root, "targets");
    Directory.CreateDirectory(modulesDirectory);
    Directory.CreateDirectory(targetsDirectory);
    string? linkedRoot = null;
    string? linkedBin = null;
    try
    {
        var rootTarget = CreateModule(targetsDirectory, "LinkedRootMod", "v1.0.0", declaredDll: "LinkedRootMod.dll");
        var rootTargetBin = Path.Combine(rootTarget, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(rootTargetBin);
        WriteManagedAssembly(Path.Combine(rootTargetBin, "LinkedRootMod.dll"), "LinkedRootMod");
        linkedRoot = Path.Combine(modulesDirectory, "LinkedRootMod");
        CreateDirectoryJunction(linkedRoot, rootTarget);
        var linkedRootModule = new ModuleScanner().Scan(modulesDirectory).Single();
        AssertThrowsInvalidData(
            () => new CoopBridgePackageBuilder().Build([linkedRootModule]),
            "Bridge builder accepted a linked module root.");
        Directory.Delete(linkedRoot);
        linkedRoot = null;

        var binLinkedModuleRoot = CreateModule(
            modulesDirectory,
            "LinkedBinMod",
            "v1.0.0",
            declaredDll: "LinkedBinMod.dll");
        var binTarget = Path.Combine(targetsDirectory, "linked-bin");
        var shippingTarget = Path.Combine(binTarget, "Win64_Shipping_Client");
        Directory.CreateDirectory(shippingTarget);
        WriteManagedAssembly(Path.Combine(shippingTarget, "LinkedBinMod.dll"), "LinkedBinMod");
        linkedBin = Path.Combine(binLinkedModuleRoot, "bin");
        CreateDirectoryJunction(linkedBin, binTarget);
        var linkedBinModule = new ModuleScanner().Scan(modulesDirectory).Single();
        AssertThrowsInvalidData(
            () => new CoopBridgePackageBuilder().Build([linkedBinModule]),
            "Bridge builder accepted a linked bin ancestor.");
    }
    finally
    {
        if (linkedBin is not null && Directory.Exists(linkedBin))
            Directory.Delete(linkedBin);
        if (linkedRoot is not null && Directory.Exists(linkedRoot))
            Directory.Delete(linkedRoot);
        Directory.Delete(root, recursive: true);
    }
}

void TestBridgeBuilderRejectsAssemblyIdentityMismatch()
{
    WithTemporaryModules(
        (_, modulesDirectory) =>
        {
            var moduleRoot = CreateModule(
                modulesDirectory,
                "ExpectedModule",
                "v1.0.0",
                declaredDll: "ExpectedModule.dll");
            var bin = Path.Combine(moduleRoot, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(bin);
            WriteManagedAssembly(Path.Combine(bin, "ExpectedModule.dll"), "DifferentIdentity");
            var module = new ModuleScanner().Scan(modulesDirectory).Single();
            AssertThrowsInvalidData(
                () => new CoopBridgePackageBuilder().Build([module]),
                "Bridge builder accepted a module DLL whose managed identity did not match its file name.");
        });
}

void TestBridgeBuilderIgnoresNativeSupportDll()
{
    WithTemporaryModules(
        (_, modulesDirectory) =>
        {
            var moduleRoot = CreateModule(
                modulesDirectory,
                "ManagedModule",
                "v1.0.0",
                declaredDll: "ManagedModule.dll");
            var bin = Path.Combine(moduleRoot, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(bin);
            WriteManagedAssembly(Path.Combine(bin, "ManagedModule.dll"), "ManagedModule");
            File.WriteAllBytes(Path.Combine(bin, "native_support.dll"), [0x4D, 0x5A, 0x01, 0x02]);
            var module = new ModuleScanner().Scan(modulesDirectory).Single();

            var package = new CoopBridgePackageBuilder().Build([module]);
            Assert(package.ModuleRecords.Any(record => record.DllName == "ManagedModule.dll") &&
                   package.ModuleRecords.All(record => record.DllName != "native_support.dll"),
                "Bridge builder rejected or recorded a native support DLL as managed.");
        });
}

void TestBridgeBuilderOmitsUndeclaredManagedSidecars()
{
    WithTemporaryModules(
        (_, modulesDirectory) =>
        {
            var moduleRoot = CreateModule(
                modulesDirectory,
                "DeclaredOnlyModule",
                "v1.0.0",
                declaredDll: "DeclaredOnlyModule.dll");
            var bin = Path.Combine(moduleRoot, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(bin);
            WriteManagedAssembly(
                Path.Combine(bin, "DeclaredOnlyModule.dll"),
                "DeclaredOnlyModule");
            var module = new ModuleScanner().Scan(modulesDirectory).Single();
            var builder = new CoopBridgePackageBuilder();
            var baseline = builder.Build([module]);

            var sidecar = Path.Combine(bin, "LooseSidecar.dll");
            WriteManagedAssembly(sidecar, "LooseSidecar");
            var withSidecar = builder.Build([module]);
            File.AppendAllBytes(sidecar, [0x42]);
            var withChangedSidecar = builder.Build([module]);

            Assert(withSidecar.ModuleRecords.Count == 1 &&
                   withSidecar.ModuleRecords[0].DllName == "DeclaredOnlyModule.dll",
                "Bridge recorded an undeclared managed sidecar as a runtime requirement.");
            Assert(baseline.ModuleId == withSidecar.ModuleId &&
                   withSidecar.ModuleId == withChangedSidecar.ModuleId,
                "Undeclared managed sidecar bytes changed the bridge ID.");
            Assert(baseline.Configuration.SequenceEqual(withSidecar.Configuration) &&
                   withSidecar.Configuration.SequenceEqual(withChangedSidecar.Configuration),
                "Undeclared managed sidecar bytes changed bridge configuration.");
        });
}

void TestBridgeGameVersionCompatibility()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.2");
            var coop = new ModuleScanner().Scan(modulesDirectory).Single();
            var rule = new BridgeGameVersionCompatibility(
                "v1.4.8",
                "v1.4.8",
                "v1.4.8.123456",
                "v1.4.8.123457");
            var packageBuilder = new CoopBridgePackageBuilder();
            var package = packageBuilder.Build(
                [coop],
                gameVersionCompatibility: rule);
            var configuration = System.Text.Encoding.UTF8.GetString(package.Configuration);

            Assert(package.GameVersionCompatibility == rule,
                "Game-version compatibility rule was omitted from the package model.");
            Assert(configuration.Contains("GAME_VERSION_COMPAT|", StringComparison.Ordinal),
                "Game-version compatibility rule was omitted from bridge configuration.");
            var fields = configuration.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("GAME_VERSION_COMPAT|", StringComparison.Ordinal))
                .Split('|');
            Assert(fields.Length == 5 &&
                   System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[3])) ==
                       rule.ServerRuntimeVersion &&
                   System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[4])) ==
                       rule.ClientRuntimeVersion,
                "Game-version compatibility omitted exact semantic runtime versions.");

            var sameBaseWarning =
                CoopCompatibilityPatcher.CreateGameVersionCompatibilityWarning(rule);
            Assert(
                sameBaseWarning.Contains(
                    rule.ServerRuntimeVersion + " -> " + rule.ClientRuntimeVersion,
                    StringComparison.Ordinal) &&
                sameBaseWarning.Contains(
                    "No Coop base-version bypass is installed",
                    StringComparison.Ordinal) &&
                !sameBaseWarning.Contains(
                    "bypasses only Coop's game-version gate",
                    StringComparison.Ordinal),
                "Same-base semantic observation incorrectly reported a Coop version-gate bypass.");

            var differingBaseRule = new BridgeGameVersionCompatibility(
                "v1.4.7",
                "v1.4.8",
                "v1.4.7.118999",
                "v1.4.8.119303");
            var differingBaseWarning =
                CoopCompatibilityPatcher.CreateGameVersionCompatibilityWarning(
                    differingBaseRule);
            Assert(
                differingBaseWarning.Contains(
                    differingBaseRule.ServerRuntimeVersion + " -> " +
                    differingBaseRule.ClientRuntimeVersion,
                    StringComparison.Ordinal) &&
                differingBaseWarning.Contains(
                    "different supported base game versions",
                    StringComparison.Ordinal) &&
                differingBaseWarning.Contains(
                    "bypasses only Coop's game-version gate",
                    StringComparison.Ordinal) &&
                !differingBaseWarning.Contains(
                    "No Coop base-version bypass is installed",
                    StringComparison.Ordinal),
                "Supported differing-base compatibility did not report its scoped Coop version-gate bypass.");

            AssertThrowsInvalidData(
                () => packageBuilder.Build(
                    [coop],
                    gameVersionCompatibility: rule with
                    {
                        ServerRuntimeVersion = "v1.4.7.123456"
                    }),
                "Bridge accepted an exact runtime version outside its declared base version.");
        });
}

void TestModuleManagerSemanticRevisionReader()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "bcs-module-manager-version-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var cases = new[]
        {
            (Value: 8, OpCode: OpCodes.Ldc_I4_8),
            (Value: 42, OpCode: OpCodes.Ldc_I4_S),
            (Value: 123456, OpCode: OpCodes.Ldc_I4)
        };
        foreach (var testCase in cases)
        {
            var path = Path.Combine(root, "valid-" + testCase.Value + ".dll");
            WriteModuleManagerVersionFixture(
                path,
                testCase.Value,
                testCase.OpCode,
                testCase.Value,
                testCase.OpCode);
            Assert(
                CoopCompatibilityPatcher.ReadModuleManagerChangeSet(path, "fixture") ==
                testCase.Value,
                "ModuleManager semantic revision reader rejected " +
                testCase.OpCode.Name + ".");
        }

        var divergent = Path.Combine(root, "divergent.dll");
        WriteModuleManagerVersionFixture(
            divergent,
            123456,
            OpCodes.Ldc_I4,
            123457,
            OpCodes.Ldc_I4);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(divergent, "fixture"),
            "ModuleManager semantic revision reader accepted divergent type revisions.");

        var zero = Path.Combine(root, "zero.dll");
        WriteModuleManagerVersionFixture(
            zero,
            0,
            OpCodes.Ldc_I4_0,
            0,
            OpCodes.Ldc_I4_0);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(zero, "fixture"),
            "ModuleManager semantic revision reader accepted a nonpositive revision.");

        var missing = Path.Combine(root, "missing-type.dll");
        WriteModuleManagerVersionFixture(
            missing,
            123456,
            OpCodes.Ldc_I4,
            123456,
            OpCodes.Ldc_I4,
            includeDependedModule: false);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(missing, "fixture"),
            "ModuleManager semantic revision reader accepted a missing DependedModule type.");

        var ambiguous = Path.Combine(root, "ambiguous.dll");
        WriteModuleManagerVersionFixture(
            ambiguous,
            123456,
            OpCodes.Ldc_I4,
            123456,
            OpCodes.Ldc_I4,
            duplicateConstruction: true);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(ambiguous, "fixture"),
            "ModuleManager semantic revision reader accepted ambiguous constructor sequences.");

        var wrongConstructorParameter = Path.Combine(root, "wrong-constructor-parameter.dll");
        WriteModuleManagerVersionFixture(
            wrongConstructorParameter,
            123456,
            OpCodes.Ldc_I4,
            123456,
            OpCodes.Ldc_I4,
            applicationVersionTypeParameter: false);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(
                wrongConstructorParameter,
                "fixture"),
            "ModuleManager semantic revision reader accepted an ApplicationVersion constructor with the wrong parameter types.");

        var staticUpdateMethod = Path.Combine(root, "static-update-method.dll");
        WriteModuleManagerVersionFixture(
            staticUpdateMethod,
            123456,
            OpCodes.Ldc_I4,
            123456,
            OpCodes.Ldc_I4,
            staticUpdateVersionChangeSet: true);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(staticUpdateMethod, "fixture"),
            "ModuleManager semantic revision reader accepted a static UpdateVersionChangeSet method.");

        var nonVoidUpdateMethod = Path.Combine(root, "non-void-update-method.dll");
        WriteModuleManagerVersionFixture(
            nonVoidUpdateMethod,
            123456,
            OpCodes.Ldc_I4,
            123456,
            OpCodes.Ldc_I4,
            nonVoidUpdateVersionChangeSet: true);
        AssertThrowsInvalidData(
            () => CoopCompatibilityPatcher.ReadModuleManagerChangeSet(nonVoidUpdateMethod, "fixture"),
            "ModuleManager semantic revision reader accepted a non-void UpdateVersionChangeSet method.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

void TestGenericExecutablePrepareAndRevert()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "ExecutableMod",
                "v5.0.0",
                ["Native"],
                declaredDll: "ExecutableMod.dll");
            var clientBin = Path.Combine(modulePath, "bin", "Win64_Shipping_Client");
            var serverBin = Path.Combine(modulePath, "bin", "Win64_Shipping_Server");
            Directory.CreateDirectory(clientBin);
            Directory.CreateDirectory(serverBin);
            var clientDll = Path.Combine(clientBin, "ExecutableMod.dll");
            var serverDll = Path.Combine(serverBin, "ExecutableMod.dll");
            WriteManagedAssembly(clientDll, "ExecutableMod");
            WriteManagedAssembly(serverDll, "ExecutableMod");
            using (var stream = new FileStream(
                       serverDll,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.WriteByte(0x42);
            }
            var originalDll = File.ReadAllBytes(clientDll);
            var staleServerDll = File.ReadAllBytes(serverDll);

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "ExecutableMod");
            var patcher = new CoopCompatibilityPatcher();
            var plan = patcher.CreatePlan(selected, modules, serverRoot);
            Assert(plan.CanApply, plan.Summary);
            Assert(plan.RuleId == "generic-executable-bridge-v1",
                "Executable mod did not select the generic bridge rule.");
            Assert(!plan.Warnings.Any(warning =>
                    warning.Contains("EOE", StringComparison.OrdinalIgnoreCase) ||
                    warning.Contains("Europe1700", StringComparison.OrdinalIgnoreCase)),
                "Generic executable plan leaked EOE-specific user guidance.");

            var result = patcher.Apply(plan);
            Assert(File.Exists(serverDll) && File.ReadAllBytes(serverDll).SequenceEqual(originalDll),
                "Executable mod DLL was not projected to the dedicated-server bin.");
            Assert(plan.ModuleIds.Any(id =>
                    id.StartsWith(CoopBridgePackageBuilder.BridgeIdPrefix, StringComparison.Ordinal)),
                "Executable preparation did not include a generated bridge module.");
            var bridgeId = plan.ModuleIds.Single(id =>
                id.StartsWith(CoopBridgePackageBuilder.BridgeIdPrefix, StringComparison.Ordinal));
            var bridgeConfiguration = File.ReadAllText(Path.Combine(
                modulesDirectory,
                bridgeId,
                "bcs-coop-bridge.config"));
            Assert(bridgeConfiguration.Contains("MODULE|", StringComparison.Ordinal),
                "Bridge configuration omitted its module/version record.");
            Assert(bridgeConfiguration.StartsWith(
                       "BCS-COOP-BRIDGE|2\n",
                       StringComparison.Ordinal) &&
                   !bridgeConfiguration.Contains("RUNTIME_FEATURE|", StringComparison.Ordinal),
                "Generic executable bridge inherited target-specific runtime features.");

            patcher.Revert(result.ManifestPath);
            Assert(File.ReadAllBytes(serverDll).SequenceEqual(staleServerDll),
                "Revert did not restore the original server DLL bytes.");
        });
}

void TestBridgeAuthorityRuleConfiguration()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "AuthorityMod",
                "v1.0.0",
                ["Native"],
                declaredDll: "AuthorityMod.dll");
            var clientBin = Path.Combine(modulePath, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(clientBin);
            WriteManagedAssembly(Path.Combine(clientBin, "AuthorityMod.dll"), "AuthorityMod");

            var modules = new ModuleScanner().Scan(modulesDirectory)
                .Where(module => module.Id is "Coop" or "AuthorityMod")
                .ToArray();
            var serverRule = new BridgeAuthorityRule(
                "AuthorityMod",
                "AuthorityMod.dll",
                "AuthorityMod.DailyBehavior",
                "ApplyMutation",
                0,
                BridgeInvocationScope.ServerOnly);
            var clientRule = new BridgeAuthorityRule(
                "AuthorityMod",
                "AuthorityMod.dll",
                "AuthorityMod.PresentationHook",
                "InitializeMusic",
                0,
                BridgeInvocationScope.ClientOnly);
            var settingsRule = new BridgeAuthorityRule(
                "AuthorityMod",
                "AuthorityMod.dll",
                "AuthorityMod.SubModule",
                "OnGameStart",
                2,
                BridgeInvocationScope.ServerSettingsFallback);
            var package = new CoopBridgePackageBuilder().Build(
                modules,
                null,
                [serverRule, clientRule, settingsRule]);
            var configuration = System.Text.Encoding.UTF8.GetString(package.Configuration);

            Assert(package.AuthorityRules.SequenceEqual([serverRule, clientRule, settingsRule]),
                "Invocation-scope rules were omitted from the generated package model.");
            Assert(configuration.Contains("AUTHORITY|", StringComparison.Ordinal),
                "Authority rule was omitted from bridge configuration.");
            Assert(configuration.Contains("|SERVER_ONLY", StringComparison.Ordinal) &&
                   configuration.Contains("|CLIENT_ONLY", StringComparison.Ordinal) &&
                   configuration.Contains("|SERVER_SETTINGS_FALLBACK", StringComparison.Ordinal),
                "Bridge configuration did not preserve every invocation scope.");
        });
}

void TestBridgeServerFileRedirectConfiguration()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.2", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "MapMod",
                "v1.0.0",
                ["Native"]);
            var cachePath = Path.Combine(
                modulePath,
                "ModuleData",
                "DistanceCaches",
                "settlements_distance_cache_Default.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var cacheBytes = new byte[] { 1, 7, 0, 0, 4, 2 };
            File.WriteAllBytes(cachePath, cacheBytes);
            var cacheHash = Convert.ToHexString(SHA256.HashData(cacheBytes));
            var redirect = new BridgeServerFileRedirect(
                "MapMod",
                "ModuleData/DistanceCaches/settlements_distance_cache_Default.bin",
                cacheHash);
            var modules = new ModuleScanner().Scan(modulesDirectory)
                .Where(module => module.Id is "Coop" or "MapMod")
                .ToArray();
            var packageBuilder = new CoopBridgePackageBuilder();
            var package = packageBuilder.Build(
                modules,
                serverFileRedirects: [redirect]);
            var configuration = System.Text.Encoding.UTF8.GetString(package.Configuration);

            Assert(package.ServerFileRedirects.SequenceEqual([redirect]),
                "Server file redirect was omitted from the generated package model.");
            Assert(configuration.Contains("SERVER_FILE_REDIRECT|", StringComparison.Ordinal),
                "Server file redirect was omitted from bridge configuration.");
            Assert(!configuration.Contains(cacheHash, StringComparison.Ordinal),
                "Server file redirect retained an exact source byte pin.");

            File.WriteAllBytes(cachePath, [9, 9, 9]);
            _ = packageBuilder.Build(modules, serverFileRedirects: [redirect]);
        });
}

void TestBridgeServerXmlOverlayConfiguration()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.2", ["Native"]);
            var modulePath = CreateModule(modulesDirectory, "SchemaMod", "v1.0.0", ["Native"]);
            var sourcePath = Path.Combine(modulePath, "ModuleData", "characters.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var sourceBytes = System.Text.Encoding.UTF8.GetBytes("<NPCCharacters><traits /></NPCCharacters>");
            var overlayBytes = System.Text.Encoding.UTF8.GetBytes("<NPCCharacters><Traits /></NPCCharacters>");
            File.WriteAllBytes(sourcePath, sourceBytes);
            var overlay = new BridgeServerXmlOverlay(
                "SchemaMod",
                "ModuleData/characters.xml",
                Convert.ToHexString(SHA256.HashData(sourceBytes)),
                "bcs-server-overlays/SchemaMod/ModuleData/characters.xml",
                Convert.ToHexString(SHA256.HashData(overlayBytes)),
                overlayBytes);
            var modules = new ModuleScanner().Scan(modulesDirectory)
                .Where(module => module.Id is "Coop" or "SchemaMod")
                .ToArray();
            var builder = new CoopBridgePackageBuilder();
            var package = builder.Build(modules, serverXmlOverlays: [overlay]);
            var configuration = System.Text.Encoding.UTF8.GetString(package.Configuration);
            VerifyServerBridgeXsltRedirect(package.Assembly);

            Assert(package.ServerXmlOverlays.Count == 1 &&
                   package.ServerXmlOverlays[0].Content.SequenceEqual(overlayBytes),
                "Server XML overlay was omitted from the generated package model.");
            Assert(configuration.Contains("SERVER_XML_OVERLAY|", StringComparison.Ordinal) &&
                   !configuration.Contains(overlay.SourceSha256, StringComparison.Ordinal) &&
                   !configuration.Contains(overlay.OverlaySha256, StringComparison.Ordinal),
                "Server XML overlay configuration retained source or payload byte pins.");
            using (var zipStream = new MemoryStream(package.ClientPackageZip, writable: false))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(
                    $"Modules/{package.ModuleId}/{overlay.OverlayRelativePath}");
                Assert(entry is null,
                    "Client package redistributed a server-only XML overlay payload.");
            }

            File.WriteAllText(sourcePath, "<NPCCharacters />");
            _ = builder.Build(modules, serverXmlOverlays: [overlay]);

            File.WriteAllBytes(sourcePath, sourceBytes);
            _ = builder.Build(
                modules,
                serverXmlOverlays:
                [overlay with { Content = System.Text.Encoding.UTF8.GetBytes("<changed />") }]);
        });
}

void TestBridgeServerMapTerrainSizeConfiguration()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.2", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "MapMod",
                "v1.0.0",
                ["Native"]);
            var scenePath = Path.Combine(modulePath, "SceneObj", "Main_map", "scene.xscene");
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath)!);
            var sceneBytes = System.Text.Encoding.UTF8.GetBytes(
                "<scene><terrain node_dimension_x=\"16\" node_dimension_y=\"16\" node_size=\"106.000\" /></scene>");
            File.WriteAllBytes(scenePath, sceneBytes);
            var sceneHash = Convert.ToHexString(SHA256.HashData(sceneBytes));
            var loaderHash = new string('A', 64);
            var targetHash = new string('B', 64);
            var rule = new BridgeServerMapTerrainSize(
                "MapMod",
                "SceneObj/Main_map/scene.xscene",
                sceneHash,
                1696f,
                1696f,
                "DedicatedServer.Core",
                loaderHash,
                "SandBox",
                targetHash);
            var modules = new ModuleScanner().Scan(modulesDirectory)
                .Where(module => module.Id is "Coop" or "MapMod")
                .ToArray();
            var builder = new CoopBridgePackageBuilder();
            var package = builder.Build(modules, serverMapTerrainSizes: [rule]);
            var repeated = builder.Build(modules.Reverse().ToArray(), serverMapTerrainSizes: [rule]);
            var configuration = System.Text.Encoding.UTF8.GetString(package.Configuration);

            Assert(package.ServerMapTerrainSizes.SequenceEqual([rule]),
                "Server map terrain size was omitted from the package model.");
            static string EncodeTerrainField(string value) => Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(value));
            var expectedRecord =
                "SERVER_MAP_TERRAIN_SIZE|" + EncodeTerrainField("MapMod") + "|" +
                EncodeTerrainField("SceneObj/Main_map/scene.xscene") + "|" +
                "|1696|1696|" + EncodeTerrainField("DedicatedServer.Core") + "||" +
                EncodeTerrainField("SandBox") + "|\n";
            Assert(configuration.Contains(expectedRecord, StringComparison.Ordinal),
                "Server map terrain size was omitted from bridge configuration.");
            Assert(package.ModuleId == repeated.ModuleId &&
                   package.Configuration.SequenceEqual(repeated.Configuration),
                "Server map terrain rule made bridge identity depend on input module order.");

            File.WriteAllText(scenePath, "<changed />");
            _ = builder.Build(modules, serverMapTerrainSizes: [rule]);
            File.WriteAllBytes(scenePath, sceneBytes);

            AssertThrowsInvalidData(
                () => builder.Build(
                    modules,
                    serverMapTerrainSizes: [rule with { Width = float.NaN }]),
                "Bridge accepted a non-finite server map terrain dimension.");
            _ = builder.Build(
                modules,
                serverMapTerrainSizes:
                [
                    rule with
                    {
                        Sha256 = sceneHash.ToLowerInvariant(),
                        LoaderAssemblySha256 = loaderHash.ToLowerInvariant(),
                        TargetAssemblySha256 = targetHash.ToLowerInvariant()
                    }
                ]);
            AssertThrowsInvalidData(
                () => builder.Build(
                    modules,
                    serverMapTerrainSizes:
                    [
                        rule with
                        {
                            LoaderAssemblyName = "../DedicatedServer.Core.dll"
                        }
                    ]),
                "Bridge accepted an unsafe terrain loader assembly name.");
            AssertThrowsInvalidData(
                () => builder.Build(
                    modules,
                    serverMapTerrainSizes:
                    [
                        rule with
                        {
                            TargetAssemblyName = "SandBox, Version=1.0.0.0"
                        }
                    ]),
                "Bridge accepted a non-simple terrain target assembly name.");
            var changedTargetPackage = builder.Build(
                modules,
                serverMapTerrainSizes:
                [
                    rule with
                    {
                        TargetAssemblySha256 = new string('C', 64)
                    }
                ]);
            Assert(changedTargetPackage.ModuleId == package.ModuleId &&
                   changedTargetPackage.Configuration.SequenceEqual(package.Configuration),
                "Unused terrain target hash changed bridge identity.");
            AssertThrowsInvalidData(
                () => builder.Build(modules, serverMapTerrainSizes: [rule, rule]),
                "Bridge accepted duplicate server map terrain rules.");
        });
}

void TestBridgeRuntimeFeatureConfiguration()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.2", ["Native"]);
            var modules = new ModuleScanner().Scan(modulesDirectory)
                .Where(module => module.Id == "Coop")
                .ToArray();
            var builder = new CoopBridgePackageBuilder();

            var generic = builder.Build(modules);
            var genericConfiguration = System.Text.Encoding.UTF8.GetString(generic.Configuration);
            Assert(genericConfiguration.StartsWith(
                       "BCS-COOP-BRIDGE|2\n",
                       StringComparison.Ordinal) &&
                   !genericConfiguration.Contains("RUNTIME_FEATURE|", StringComparison.Ordinal) &&
                   generic.RuntimeFeatures.Count == 0,
                "Generic bridge package inherited target-specific runtime features.");

            var eoeFeatures = BridgeRuntimeFeatureSets.Europe1700;
            var eoe = builder.Build(modules, runtimeFeatures: eoeFeatures);
            var featureNames = System.Text.Encoding.UTF8.GetString(eoe.Configuration)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("RUNTIME_FEATURE|", StringComparison.Ordinal))
                .Select(line => line.Split('|'))
                .Select(fields => System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(fields[1])))
                .ToArray();
            var expectedNames = eoeFeatures
                .Select(feature => feature.ToString())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert(featureNames.SequenceEqual(expectedNames, StringComparer.Ordinal) &&
                   eoe.RuntimeFeatures.SequenceEqual(eoeFeatures),
                "EOE runtime feature set did not round-trip through deterministic bridge configuration.");
            Assert(eoe.ModuleId != generic.ModuleId,
                "Runtime feature selection did not contribute to bridge identity.");

            AssertThrowsInvalidData(
                () => builder.Build(
                    modules,
                    runtimeFeatures:
                    [
                        BridgeRuntimeFeature.ServerPopulationControl,
                        BridgeRuntimeFeature.ServerPopulationControl
                    ]),
                "Bridge accepted duplicate runtime features.");
            AssertThrowsInvalidData(
                () => builder.Build(
                    modules,
                    runtimeFeatures: [(BridgeRuntimeFeature)int.MaxValue]),
                "Bridge accepted an unknown runtime feature.");
        });
}

void TestBridgeExcludesCampaignIntervention()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Coop", "v0.1.2", ["Native"]);
            CreateModule(modulesDirectory, "MapMod", "v1.0.0", ["Native"]);
            var modules = new ModuleScanner().Scan(modulesDirectory)
                .Where(module => module.Id is "Coop" or "MapMod")
                .ToArray();
            var package = new CoopBridgePackageBuilder().Build(modules);
            var configuration = System.Text.Encoding.UTF8.GetString(package.Configuration);

            Assert(!configuration.Contains("SERVER_NEW_CAMPAIGN", StringComparison.Ordinal),
                "Bridge configuration still enables campaign seeding.");
            using (var serverStream = new MemoryStream(package.Assembly))
            {
                using var pe = new PEReader(serverStream);
                var metadata = pe.GetMetadataReader();
                var types = metadata.TypeDefinitions
                    .Select(handle => metadata.GetTypeDefinition(handle))
                    .Select(type =>
                        metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name))
                    .ToArray();
                Assert(!types.Contains(
                        "BCS.CoopBridge.ServerCampaignSeeder",
                        StringComparer.Ordinal),
                    "Packaged bridge still contains the campaign seeder.");
                Assert(types.Contains(
                        "GameInterface.BCSCoopBridge.Registration.DiscoveredBridgeHandler",
                        StringComparer.Ordinal),
                    "Packaged server bridge is not discoverable by Coop.");
            }

            using (var clientStream = new MemoryStream(package.ClientAssembly))
            {
                using var pe = new PEReader(clientStream);
                var metadata = pe.GetMetadataReader();
                var types = metadata.TypeDefinitions
                    .Select(handle => metadata.GetTypeDefinition(handle))
                    .Select(type =>
                        metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name))
                    .ToArray();
                var references = metadata.AssemblyReferences
                    .Select(handle => metadata.GetAssemblyReference(handle))
                    .Select(reference => metadata.GetString(reference.Name))
                    .ToArray();
                Assert(!types.Contains(
                        "GameInterface.BCSCoopBridge.Registration.DiscoveredBridgeHandler",
                        StringComparer.Ordinal),
                    "Packaged client bridge still contains the early typed Coop handler.");
                Assert(types.Contains(
                        "BCS.CoopBridge.ClientCoopHandlerRegistration",
                        StringComparer.Ordinal),
                    "Packaged client bridge has no delayed Coop handler registration seam.");
                Assert(!references.Contains("Common", StringComparer.OrdinalIgnoreCase),
                    "Packaged client bridge still hard-references Common.dll.");
                Assert(!references.Contains("GameInterface", StringComparer.OrdinalIgnoreCase),
                    "Packaged client bridge still hard-references GameInterface.dll.");
            }
        });
}

void TestPreparedProfileNormalizesDependencyOrder()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var executable = PrepareFakeDedicatedServer(serverRoot);
            File.WriteAllBytes(Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll"), [1]);
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "DedicatedServer.Windows", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "SandBoxCore", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "Sandbox", "v1.4.7", ["SandBoxCore"]);
            CreateModule(modulesDirectory, "Coop", "v0.1.1");
            var modulePath = CreateModule(
                modulesDirectory,
                "PreparedMod",
                "v1.0.0",
                ["Sandbox"],
                declaredDll: "PreparedMod.dll");
            var manifestPath = Path.Combine(modulePath, "SubModule.xml");
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace(
                    "</Module>",
                    "<DependedModuleMetadatas>" +
                    "<DependedModuleMetadata Id=\"Coop\" Order=\"LoadAfterThis\" Optional=\"true\"/>" +
                    "</DependedModuleMetadatas></Module>",
                    StringComparison.Ordinal));
            var clientBin = Path.Combine(modulePath, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(clientBin);
            WriteManagedAssembly(Path.Combine(clientBin, "PreparedMod.dll"), "PreparedMod");

            var scanned = new ModuleScanner().Scan(modulesDirectory)
                .Reverse()
                .ToArray();
            var selected = scanned.Single(module => module.Id == "PreparedMod");
            var patcher = new CoopCompatibilityPatcher();
            var plan = patcher.CreatePlan(selected, scanned, serverRoot);
            Assert(plan.CanApply, plan.Summary);
            var result = patcher.Apply(plan);
            var launch = new DedicatedServerLaunchBuilder(
                    new ModuleScanner(),
                    Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll"))
                .Build(executable, serverRoot);
            var ids = launch.ActiveModuleIds.ToList();

            Assert(ids.IndexOf("DedicatedServer.Windows") == ids.IndexOf("Native") + 1,
                "Prepared profile did not place DedicatedServer.Windows immediately after Native.");
            Assert(ids.IndexOf("SandBoxCore") < ids.IndexOf("Sandbox"),
                "Prepared profile did not place SandBoxCore before Sandbox.");
            Assert(ids.IndexOf("Sandbox") < ids.IndexOf("Coop"),
                "Prepared profile did not place Coop after Sandbox when released Coop omits dependency metadata.");
            Assert(ids.IndexOf("Sandbox") < ids.IndexOf("PreparedMod"),
                "Prepared profile did not place the prepared mod after Sandbox.");
            Assert(ids.IndexOf("Coop") < ids.IndexOf("PreparedMod"),
                "Prepared profile did not place Coop before the prepared mod.");
            Assert(ids[^1].StartsWith(CoopBridgePackageBuilder.BridgeIdPrefix, StringComparison.Ordinal),
                "Prepared profile did not place the generated bridge last.");

            patcher.Revert(result.ManifestPath);
        });
}

void TestFrameworkBeforeNativePreparationOrder()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var executable = PrepareFakeDedicatedServer(serverRoot);
            File.WriteAllBytes(Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll"), [1]);
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "DedicatedServer.Windows", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "SandBoxCore", "v1.4.7", ["Native"]);
            CreateModule(modulesDirectory, "Sandbox", "v1.4.7", ["SandBoxCore"]);
            CreateModule(modulesDirectory, "Coop", "v0.1.2");
            var frameworkPath = CreateModule(
                modulesDirectory,
                "ThirdParty.Framework",
                "v1.0.0",
                declaredDll: "ThirdParty.Framework.dll");
            var frameworkManifestPath = Path.Combine(frameworkPath, "SubModule.xml");
            File.WriteAllText(
                frameworkManifestPath,
                File.ReadAllText(frameworkManifestPath).Replace(
                    "</Module>",
                    "<DependedModuleMetadatas>" +
                    "<DependedModuleMetadata Id=\"Native\" Order=\"LoadAfterThis\"/>" +
                    "</DependedModuleMetadatas></Module>",
                    StringComparison.Ordinal));
            var frameworkClientBin = Path.Combine(
                frameworkPath,
                "bin",
                "Win64_Shipping_Client");
            Directory.CreateDirectory(frameworkClientBin);
            WriteManagedAssembly(
                Path.Combine(frameworkClientBin, "ThirdParty.Framework.dll"),
                "ThirdParty.Framework");

            var modPath = CreateModule(
                modulesDirectory,
                "FrameworkConsumer",
                "v1.0.0",
                ["Native", "Sandbox", "ThirdParty.Framework"],
                declaredDll: "FrameworkConsumer.dll");
            var modClientBin = Path.Combine(modPath, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(modClientBin);
            WriteManagedAssembly(
                Path.Combine(modClientBin, "FrameworkConsumer.dll"),
                "FrameworkConsumer");

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "FrameworkConsumer");
            var patcher = new CoopCompatibilityPatcher();
            var plan = patcher.CreatePlan(selected, modules, serverRoot);
            Assert(plan.CanApply, plan.Summary);
            var result = patcher.Apply(plan);
            var launch = new DedicatedServerLaunchBuilder(
                    new ModuleScanner(),
                    Path.Combine(serverRoot, "BCSTool.RuntimeBootstrap.dll"))
                .Build(executable, serverRoot);
            var ids = launch.ActiveModuleIds.ToList();

            Assert(ids.IndexOf("ThirdParty.Framework") < ids.IndexOf("Native"),
                "Framework LoadAfterThis metadata did not place the framework before Native.");
            Assert(ids.IndexOf("DedicatedServer.Windows") == ids.IndexOf("Native") + 1,
                "Framework ordering broke the dedicated-server host adjacency rule.");
            Assert(ids.IndexOf("Coop") < ids.IndexOf("FrameworkConsumer"),
                "Prepared consumer did not load after Coop.");
            var preparedFrameworkManifest = File.ReadAllText(frameworkManifestPath);
            Assert(
                preparedFrameworkManifest.Contains(
                    "key=\"DedicatedServerType\" value=\"none\"",
                    StringComparison.Ordinal) &&
                preparedFrameworkManifest.Contains(
                    "key=\"IsNoRenderModeElement\" value=\"false\"",
                    StringComparison.Ordinal),
                "Prepared framework submodule was left executable on the headless server.");

            patcher.Revert(result.ManifestPath);
        });
}

void TestUnsafeDeclaredDllRejected()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            CreateModule(
                modulesDirectory,
                "UnsafeExecutableMod",
                "v1.0.0",
                ["Native"],
                declaredDll: "..\\outside.dll");
            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "UnsafeExecutableMod");

            try
            {
                _ = new CoopCompatibilityPatcher().CreatePlan(selected, modules, serverRoot);
                throw new InvalidOperationException("Unsafe declared DLL path was accepted.");
            }
            catch (InvalidDataException exception)
            {
                Assert(exception.Message.Contains("Unsafe declared DLL", StringComparison.Ordinal),
                    "Unsafe DLL rejection did not explain the failing field.");
            }
        });
}

void TestClientOnlyAndWildcardDependencies()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            CreateModule(modulesDirectory, "Bannerlord.Harmony", "v2.4.2.248");
            var modulePath = CreateModule(
                modulesDirectory,
                "ClientDependencyMod",
                "v1.0.0",
                ["StoryMode", "Bannerlord.Harmony"]);
            var manifestPath = Path.Combine(modulePath, "SubModule.xml");
            var manifest = File.ReadAllText(manifestPath)
                .Replace(
                    "<DependedModule Id=\"Bannerlord.Harmony\" />",
                    "<DependedModule Id=\"Bannerlord.Harmony\" DependentVersion=\"v2.4.*\" />",
                    StringComparison.Ordinal);
            File.WriteAllText(manifestPath, manifest);

            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "ClientDependencyMod");
            var report = new CoopCompatibilityAnalyzer().Analyze(selected, modules, serverRoot);
            Assert(!report.Findings.Any(finding =>
                    finding.Severity == CompatibilityFindingSeverity.Blocker &&
                    finding.Finding.Contains("StoryMode", StringComparison.Ordinal)),
                "Client-only StoryMode dependency incorrectly blocked server analysis.");
            Assert(report.Findings.Any(finding =>
                    finding.Severity == CompatibilityFindingSeverity.Information &&
                    finding.Finding.Contains("StoryMode", StringComparison.Ordinal)),
                "Client-only dependency filtering was not explained.");
            Assert(!report.Findings.Any(finding =>
                    finding.Area == "Dependency versions" &&
                    finding.Finding.Contains("Bannerlord.Harmony", StringComparison.Ordinal)),
                "Compatible wildcard dependency version was reported as mismatched.");
        });
}

void TestCompatibilityPlanRejectsConcurrentChange()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "ConcurrentContentPack",
                "v1.0.0",
                ["Native", "StoryMode"]);
            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "ConcurrentContentPack");
            var patcher = new CoopCompatibilityPatcher();
            var plan = patcher.CreatePlan(selected, modules, serverRoot);
            var manifestPath = Path.Combine(modulePath, "SubModule.xml");
            File.AppendAllText(manifestPath, Environment.NewLine + "<!-- changed after preview -->");

            try
            {
                patcher.Apply(plan);
                throw new InvalidOperationException("Concurrent file change was accepted.");
            }
            catch (IOException exception)
            {
                Assert(exception.Message.Contains("changed after analysis", StringComparison.OrdinalIgnoreCase),
                    "Concurrent-change error was not explicit.");
            }
        });
}

void TestCompatibilityApplyRollsBackBeforeManifestPublication()
{
    WithTemporaryModules(
        (serverRoot, modulesDirectory) =>
        {
            var nativePath = CreateModule(modulesDirectory, "Native", "v1.4.7");
            CreateModule(modulesDirectory, "Coop", "v0.1.1", ["Native"]);
            var modulePath = CreateModule(
                modulesDirectory,
                "RollbackContentPack",
                "v1.0.0",
                ["Native", "StoryMode"],
                contentXml: "<Items><Item id=\"rollback_sword\" /></Items>");
            var moduleManifestPath = Path.Combine(modulePath, "SubModule.xml");
            var originalManifest = File.ReadAllBytes(moduleManifestPath);
            var modules = new ModuleScanner().Scan(modulesDirectory);
            var selected = modules.Single(module => module.Id == "RollbackContentPack");
            var patcher = new CoopCompatibilityPatcher();
            var plan = patcher.CreatePlan(selected, modules, serverRoot);
            Assert(plan.CanApply, plan.Summary);

            // The plan does not modify Native, so deleting it after analysis passes
            // stale-target validation but makes post-file assembly unblocking fail.
            Directory.Delete(nativePath, recursive: true);

            try
            {
                patcher.Apply(plan);
                throw new InvalidOperationException("Post-file compatibility failure was not surfaced.");
            }
            catch (DirectoryNotFoundException exception)
            {
                Assert(exception.Message.Contains("Native", StringComparison.Ordinal),
                    "Finalization failure did not identify the missing enabled module.");
            }

            Assert(File.ReadAllBytes(moduleManifestPath).SequenceEqual(originalManifest),
                "Finalization failure did not restore the changed module manifest.");
            Assert(plan.Changes
                    .Where(change => change.Kind == CoopPreparationChangeKind.CreateFile)
                    .All(change => !File.Exists(change.TargetPath)),
                "Finalization failure left a newly created compatibility file installed.");
            Assert(patcher.FindLatestBackupManifest(serverRoot) is null,
                "Finalization failure left a discoverable completed backup manifest.");
        });
}

string PrepareFakeDedicatedServer(string serverRoot)
{
    var executable = Path.Combine(serverRoot, "BannerlordCoopServer.exe");
    var serverBin = Path.Combine(serverRoot, "engine", "bin", "Win64_Shipping_Server");
    var dotnetDirectory = Path.Combine(serverRoot, "engine", "dotnet");
    Directory.CreateDirectory(serverBin);
    Directory.CreateDirectory(dotnetDirectory);
    File.WriteAllBytes(executable, [1]);
    File.WriteAllBytes(Path.Combine(serverBin, "TaleWorlds.Starter.DotNetCore.dll"), [1]);
    File.WriteAllBytes(Path.Combine(serverBin, "default_new_game.sav"), [1, 4, 7]);
    File.WriteAllBytes(Path.Combine(dotnetDirectory, "dotnet.exe"), [1]);
    return executable;
}

void WithTemporaryModules(Action<string, string> test)
{
    var serverRoot = Path.Combine(
        Path.GetTempPath(),
        "bcs-compatibility-regression-" + Guid.NewGuid().ToString("N"));
    var modulesDirectory = Path.Combine(serverRoot, "engine", "Modules");
    Directory.CreateDirectory(modulesDirectory);
    var serverBin = Path.Combine(serverRoot, "engine", "bin", "Win64_Shipping_Server");
    Directory.CreateDirectory(serverBin);
    File.WriteAllBytes(Path.Combine(serverBin, "default_new_game.sav"), [1, 4, 7]);

    try
    {
        test(serverRoot, modulesDirectory);
    }
    finally
    {
        Directory.Delete(serverRoot, recursive: true);
    }
}

string CreateModule(
    string modulesDirectory,
    string id,
    string version,
    IReadOnlyList<string>? dependencies = null,
    string? declaredDll = null,
    string? contentXml = null)
{
    var modulePath = Path.Combine(modulesDirectory, id);
    Directory.CreateDirectory(modulePath);
    var dependencyXml = string.Join(
        Environment.NewLine,
        (dependencies ?? Array.Empty<string>()).Select(dependency =>
            $"    <DependedModule Id=\"{dependency}\" />"));
    var subModuleXml = declaredDll is null
        ? string.Empty
        : $$"""
            <SubModule>
              <Name value="{{id}}" />
              <DLLName value="{{declaredDll}}" />
              <SubModuleClassType value="{{id}}.SubModule" />
            </SubModule>
          """;
    File.WriteAllText(
        Path.Combine(modulePath, "SubModule.xml"),
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Module>
          <Name value="{{id}}" />
          <Id value="{{id}}" />
          <Version value="{{version}}" />
          <SingleplayerModule value="true" />
          <MultiplayerModule value="false" />
          <DependedModules>
        {{dependencyXml}}
          </DependedModules>
          <SubModules>
        {{subModuleXml}}
          </SubModules>
        </Module>
        """);

    if (contentXml is not null)
    {
        var dataDirectory = Path.Combine(modulePath, "ModuleData");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "content.xml"), contentXml);
    }

    return modulePath;
}

void WriteManagedAssembly(string path, string assemblyName)
{
    var builder = new PersistedAssemblyBuilder(
        new AssemblyName(assemblyName),
        typeof(object).Assembly);
    var module = builder.DefineDynamicModule(assemblyName);
    module.DefineType(
            assemblyName + ".Marker",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed)
        .CreateType();
    builder.Save(path);
}

void WriteModuleManagerVersionFixture(
    string path,
    int moduleInfoChangeSet,
    OpCode moduleInfoOpCode,
    int dependedModuleChangeSet,
    OpCode dependedModuleOpCode,
    bool includeDependedModule = true,
    bool duplicateConstruction = false,
    bool applicationVersionTypeParameter = true,
    bool staticUpdateVersionChangeSet = false,
    bool nonVoidUpdateVersionChangeSet = false)
{
    var fixtureId = Guid.NewGuid().ToString("N");
    var libraryPath = Path.Combine(
        Path.GetDirectoryName(path)!,
        "semantic-library-" + fixtureId + ".dll");
    var libraryBuilder = new PersistedAssemblyBuilder(
        new AssemblyName("SemanticVersionLibrary." + fixtureId),
        typeof(object).Assembly);
    var libraryModule = libraryBuilder.DefineDynamicModule("SemanticVersionLibrary." + fixtureId);
    var applicationVersionType = libraryModule.DefineEnum(
            "TaleWorlds.Library.ApplicationVersionType",
            TypeAttributes.Public,
            typeof(int))
        .CreateTypeInfo()!
        .AsType();
    var applicationVersionBuilder = libraryModule.DefineType(
        "TaleWorlds.Library.ApplicationVersion",
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
    var applicationVersionConstructor = applicationVersionBuilder.DefineConstructor(
        MethodAttributes.Public,
        CallingConventions.Standard,
        [
            applicationVersionTypeParameter ? applicationVersionType : typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int)
        ]);
    var constructorIl = applicationVersionConstructor.GetILGenerator();
    constructorIl.Emit(OpCodes.Ldarg_0);
    constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
    constructorIl.Emit(OpCodes.Ret);
    applicationVersionBuilder.CreateType();
    libraryBuilder.Save(libraryPath);

    var fixtureLibrary = Assembly.Load(File.ReadAllBytes(libraryPath));
    var fixtureApplicationVersion = fixtureLibrary.GetType(
        "TaleWorlds.Library.ApplicationVersion",
        throwOnError: true)!;
    var fixtureApplicationVersionType = fixtureLibrary.GetType(
        "TaleWorlds.Library.ApplicationVersionType",
        throwOnError: true)!;
    var fixtureConstructor = fixtureApplicationVersion.GetConstructor(
        [
            applicationVersionTypeParameter ? fixtureApplicationVersionType : typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int)
        ])!;

    var moduleManagerBuilder = new PersistedAssemblyBuilder(
        new AssemblyName("TaleWorlds.ModuleManager"),
        typeof(object).Assembly);
    var moduleManagerModule = moduleManagerBuilder.DefineDynamicModule(
        "TaleWorlds.ModuleManager");

    DefineVersionType(
        "ModuleInfo",
        moduleInfoChangeSet,
        moduleInfoOpCode,
        duplicateConstruction);
    if (includeDependedModule)
    {
        DefineVersionType(
            "DependedModule",
            dependedModuleChangeSet,
            dependedModuleOpCode,
            duplicateConstruction);
    }
    moduleManagerBuilder.Save(path);

    void DefineVersionType(
        string typeName,
        int changeSet,
        OpCode loadOpCode,
        bool emitTwice)
    {
        var type = moduleManagerModule.DefineType(
            "TaleWorlds.ModuleManager." + typeName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
        var methodAttributes = MethodAttributes.Public;
        if (staticUpdateVersionChangeSet)
            methodAttributes |= MethodAttributes.Static;
        var method = type.DefineMethod(
            "UpdateVersionChangeSet",
            methodAttributes,
            nonVoidUpdateVersionChangeSet ? typeof(int) : typeof(void),
            Type.EmptyTypes);
        var il = method.GetILGenerator();
        EmitConstruction(il, changeSet, loadOpCode);
        if (emitTwice)
            EmitConstruction(il, changeSet, loadOpCode);
        if (nonVoidUpdateVersionChangeSet)
            il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        type.CreateType();
    }

    void EmitConstruction(ILGenerator il, int changeSet, OpCode loadOpCode)
    {
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        if (loadOpCode == OpCodes.Ldc_I4)
            il.Emit(loadOpCode, changeSet);
        else if (loadOpCode == OpCodes.Ldc_I4_S)
            il.Emit(loadOpCode, checked((sbyte)changeSet));
        else
            il.Emit(loadOpCode);
        il.Emit(OpCodes.Newobj, fixtureConstructor);
        il.Emit(OpCodes.Pop);
    }
}

void CreateDirectoryJunction(string path, string target)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(path);
    startInfo.ArgumentList.Add(target);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "Could not start mklink for bridge reparse regression.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            "Could not create bridge regression junction: " + output + error);
    }
}

IReadOnlyList<string> FingerprintFiles(string root) =>
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path =>
            Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

void WithTemporaryConfig(
    string text,
    Action<CoopConfigService, string, string> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "bcs-mod-config-regression-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "mod-config.json");
    File.WriteAllText(path, text);

    try
    {
        test(new CoopConfigService(directory), path, text);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

string BuildConfig(string? additionalRootProperty = null)
{
    var separator = additionalRootProperty is null ? "" : "," + Environment.NewLine + additionalRootProperty;

    return $$"""
    {
      // preserve-this-comment
      "difficulty": {
        // "playerReceivedDamage": "Realistic",
        // "playerTroopsReceivedDamage": "VeryEasy",
        // "combatAIDifficulty": "VeryEasy",
        // "recruitmentDifficulty": "VeryEasy",
        // "playerMapMovementSpeed": "VeryEasy",
        // "stealthAndDisguiseDifficulty": "VeryEasy",
        // "persuasionSuccessChance": "VeryEasy",
        // "clanMemberDeathChance": "VeryEasy",
        // "battleDeath": "VeryEasy",
        // "birthAndDeath": true,
        // "autoAllocateClanMemberPerks": false
      }{{separator}}
    }
    """;
}

JsonDocument ParseJsonc(string text) =>
    JsonDocument.Parse(
        text,
        new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

HashSet<int> ProcessIdsUnderRoot(string root)
{
    var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
    var ids = new HashSet<int>();
    foreach (var candidate in Process.GetProcesses())
    {
        using (candidate)
        {
            try
            {
                var path = candidate.MainModule?.FileName;
                if (path is not null &&
                    Path.GetFullPath(path).StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(candidate.Id);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // An unrelated process can exit or deny inspection during the snapshot.
            }
        }
    }
    return ids;
}

void StopNewProcessesUnderRoot(string root, IReadOnlySet<int> preexisting)
{
    for (var attempt = 0; attempt < 4; attempt++)
    {
        var current = ProcessIdsUnderRoot(root);
        var newIds = current.Where(id => !preexisting.Contains(id)).ToArray();
        if (newIds.Length == 0)
            return;
        foreach (var id in newIds)
        {
            try
            {
                using var candidate = Process.GetProcessById(id);
                candidate.Kill(entireProcessTree: true);
                candidate.WaitForExit(5000);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process already exited while the bounded smoke cleanup ran.
            }
        }
        Thread.Sleep(500);
    }

    var residue = ProcessIdsUnderRoot(root).Where(id => !preexisting.Contains(id)).ToArray();
    if (residue.Length > 0)
        throw new InvalidOperationException(
            "Dedicated-server smoke cleanup left process IDs: " + string.Join(", ", residue));
}

void AssertDefaults(BCSTool.Models.CoopModConfig config)
{
    Assert(config.FastForwardEnabled, "Fast-forward default is incorrect.");
    Assert(config.AutoPauseEnabled, "Auto-pause default is incorrect.");
    Assert(!config.ClientsCanUseCheats, "Client-cheats default is incorrect.");
    Assert(config.GoldFoodInfluenceChangeInSettlements, "Settlement-economy default is incorrect.");
    Assert(config.GoldFoodInfluenceChangeInBattles == "OneDayMax", "Battle-economy default is incorrect.");
    Assert(!config.GoldFoodInfluenceChangeForDisconnectedPlayers, "Disconnected-economy default is incorrect.");
    Assert(config.PlayerBattleAiJoinWindowHours == 24, "AI join-window default is incorrect.");
    Assert(config.SpeedLimitWhilePlayersInBattle, "Battle speed-limit default is incorrect.");
    Assert(config.WandererLimit == 32, "Wanderer default is incorrect.");
    Assert(!config.WandererLimitScalesWithPlayers, "Wanderer-scaling default is incorrect.");
    Assert(config.PlayerKingdomClanTierRequired == 4, "Kingdom tier default is incorrect.");
    Assert(config.SmithingStaminaRecoveryOutsideSettlements, "Smithing location default is incorrect.");
    Assert(Math.Abs(config.SmithingStaminaRecoveryMultiplier - 0.1) < 0.000001, "Smithing multiplier default is incorrect.");
    Assert(Math.Abs(config.MaximumLootersMultiplier - 1.0) < 0.000001, "Looter multiplier default is incorrect.");
}

void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

void AssertThrowsInvalidData(Action action, string message)
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

sealed class RegressionCampaignBehavior
{
}
