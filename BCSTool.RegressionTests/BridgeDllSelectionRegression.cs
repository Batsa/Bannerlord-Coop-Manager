using System.IO;
using System.Security.Cryptography;
using System.Text;
using BCSTool.Models;
using BCSTool.Services;

internal static class BridgeDllSelectionRegression
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    internal static void Run()
    {
        var serverRoot = Path.Combine(
            Path.GetTempPath(),
            "bcs-dll-selection-" + Guid.NewGuid().ToString("N"));
        var modulesRoot = Path.Combine(serverRoot, "engine", "Modules");
        Directory.CreateDirectory(modulesRoot);
        try
        {
            var targetRoot = Path.Combine(modulesRoot, "TargetMod");
            Directory.CreateDirectory(targetRoot);
            WriteTargetManifest(targetRoot, suppressSecond: true);
            var target = new ModuleScanner().Scan(modulesRoot).Single();
            target.SetInitialEnabled(true);
            var service = new BridgeDllSelectionService();

            var fresh = service.Resolve(target, [target], serverRoot);
            Assert(fresh.SelectedDllNames.SequenceEqual(["First.dll", "Second.dll"]),
                "Fresh bridge DLL selection did not enable every declared DLL by default.");
            var none = fresh.WithSelectedDllNames([]);
            Assert(none.SelectedDllNames.Count == 0,
                "Bridge DLL selection rejected an explicit empty selection.");
            AssertThrowsInvalidData(
                () => fresh.WithSelectedDllNames(["Unknown.dll"]),
                "Bridge DLL selection accepted an undeclared DLL.");

            var legacyBridge = CreateBridge(
                modulesRoot,
                "v0.6.67",
                BuildConfiguration(["First.dll", "Second.dll"], []));
            legacyBridge.SetInitialEnabled(true);
            var migrated = service.Resolve(target, [target, legacyBridge], serverRoot);
            Assert(migrated.SelectedDllNames.SequenceEqual(["First.dll"]),
                "Legacy bridge migration did not preserve the server manifest suppression policy.");

            legacyBridge.SetInitialEnabled(false);
            var currentBridge = CreateBridge(
                modulesRoot,
                CoopBridgePackageBuilder.BridgeVersion,
                BuildConfiguration(["First.dll"], ["Second.dll"]));
            currentBridge.SetInitialEnabled(true);
            var current = service.Resolve(target, [target, legacyBridge, currentBridge], serverRoot);
            Assert(current.SelectedDllNames.SequenceEqual(["First.dll"]),
                "Current bridge did not restore its explicit disabled DLL policy.");

            currentBridge.SetInitialEnabled(false);
            var unsupportedMarkerConfiguration = Utf8NoBom.GetBytes(
                Utf8NoBom.GetString(BuildConfiguration(["First.dll"], ["Second.dll"]))
                    .Replace("|\n", "|UNSUPPORTED_ROLE\n", StringComparison.Ordinal));
            var unsupportedMarkerBridge = CreateBridge(
                modulesRoot,
                CoopBridgePackageBuilder.BridgeVersion,
                unsupportedMarkerConfiguration);
            unsupportedMarkerBridge.SetInitialEnabled(true);
            AssertThrowsInvalidData(
                () => service.Resolve(
                    target,
                    [target, legacyBridge, currentBridge, unsupportedMarkerBridge],
                    serverRoot),
                "Current bridge accepted an unsupported MODULE runtime-role marker.");
            unsupportedMarkerBridge.SetInitialEnabled(false);
            currentBridge.SetInitialEnabled(true);

            var currentConfigPath = Path.Combine(currentBridge.Path, "bcs-coop-bridge.config");
            var currentServerAssemblyPath = Path.Combine(
                currentBridge.Path,
                "bin",
                "Win64_Shipping_Server",
                "BCS.CoopBridge.dll");
            var currentClientAssemblyPath = Path.Combine(
                currentBridge.Path,
                "bin",
                "Win64_Shipping_Client",
                "BCS.CoopBridge.dll");
            File.WriteAllBytes(currentServerAssemblyPath, [9, 9, 9]);
            File.Delete(currentClientAssemblyPath);
            var recovered = service.Resolve(target, [target, legacyBridge, currentBridge], serverRoot);
            Assert(recovered.SelectedDllNames.SequenceEqual(["First.dll"]),
                "Current bridge policy could not be recovered for runtime repair.");
            var originalConfiguration = File.ReadAllBytes(currentConfigPath);
            File.WriteAllBytes(currentConfigPath, [.. originalConfiguration, (byte)'\n']);
            AssertThrowsInvalidData(
                () => service.Resolve(target, [target, legacyBridge, currentBridge], serverRoot),
                "Runtime repair accepted a bridge configuration that no longer matched its identity.");
            File.WriteAllBytes(currentConfigPath, originalConfiguration);

            currentBridge.SetInitialEnabled(false);
            var disabledBridgeRecovery = service.Resolve(
                target,
                [target, legacyBridge, currentBridge],
                serverRoot);
            Assert(disabledBridgeRecovery.SelectedDllNames.SequenceEqual(["First.dll"]),
                "Disabling the generated bridge row discarded its DLL exclusion policy.");
            var allCurrentBridge = CreateBridge(
                modulesRoot,
                CoopBridgePackageBuilder.BridgeVersion,
                BuildConfiguration(["First.dll", "Second.dll"], []));
            allCurrentBridge.SetInitialEnabled(true);
            var allCurrent = service.Resolve(
                target,
                [target, currentBridge, allCurrentBridge],
                serverRoot);
            Assert(allCurrent.SelectedDllNames.SequenceEqual(["First.dll", "Second.dll"]),
                "Current all-enabled policy incorrectly inherited legacy server suppression tags.");

            allCurrentBridge.SetInitialEnabled(false);
            var eoeRoot = Path.Combine(modulesRoot, "Europe1700");
            Directory.CreateDirectory(eoeRoot);
            WriteEurope1700LegacyManifest(eoeRoot);
            var eoe = new ModuleScanner().Scan(modulesRoot)
                .Single(module => module.Id == "Europe1700");
            eoe.SetInitialEnabled(true);
            var legacyEoeBridge = CreateBridge(
                modulesRoot,
                "v0.6.67",
                BuildConfiguration(
                    ["First.dll", "BannerColorPersistence.dll"],
                    [],
                    "Europe1700",
                    "v1.4.7.1"),
                "Europe1700");
            legacyEoeBridge.SetInitialEnabled(true);
            var migratedEoe = service.Resolve(
                eoe,
                [target, eoe, currentBridge, allCurrentBridge, legacyEoeBridge],
                serverRoot);
            Assert(migratedEoe.SelectedDllNames.SequenceEqual(
                    [
                        "First.dll",
                        "BannerColorPersistence.dll",
                        "EOE.CustomBattlePatch.dll",
                        "RF_BattleAI.dll"
                    ]),
                "Legacy EOE migration mistook its selected client-only DLL for a global exclusion.");
            var excludedBattleDlls = migratedEoe.WithSelectedDllNames(
                ["First.dll", "BannerColorPersistence.dll"]);
            var patcher = new CoopCompatibilityPatcher();
            var excludedPlan = patcher.CreatePlan(
                eoe,
                [target, eoe],
                serverRoot,
                excludedBattleDlls);
            Assert(!HasMissingAssemblyBlocker(excludedPlan, "EOE.CustomBattlePatch.dll") &&
                   !HasMissingAssemblyBlocker(excludedPlan, "RF_BattleAI.dll"),
                "EOE CreatePlan ignored the explicit DLL exclusions.");
            var defaultPlan = patcher.CreatePlan(eoe, [target, eoe], serverRoot);
            Assert(HasMissingAssemblyBlocker(defaultPlan, "EOE.CustomBattlePatch.dll") &&
                   HasMissingAssemblyBlocker(defaultPlan, "RF_BattleAI.dll"),
                "EOE CreatePlan did not include every declared DLL by default.");
            legacyEoeBridge.SetInitialEnabled(false);
            var globallyDisabledBannerBridge = CreateBridge(
                modulesRoot,
                CoopBridgePackageBuilder.BridgeVersion,
                BuildConfiguration(
                    ["First.dll"],
                    ["BannerColorPersistence.dll"],
                    "Europe1700",
                    "v1.4.7.1"),
                "Europe1700");
            globallyDisabledBannerBridge.SetInitialEnabled(false);
            AssertThrowsInvalidData(
                () => service.Resolve(
                    eoe,
                    [
                        target,
                        eoe,
                        currentBridge,
                        allCurrentBridge,
                        legacyEoeBridge,
                        globallyDisabledBannerBridge
                    ],
                    serverRoot),
                "Inactive bridges with different client policy were resolved from ambiguous server tags.");
        }
        finally
        {
            Directory.Delete(serverRoot, recursive: true);
        }
    }

    private static BannerlordModule CreateBridge(
        string modulesRoot,
        string version,
        byte[] configuration,
        string targetModuleId = "TargetMod")
    {
        var currentVersion = version.Equals(
            CoopBridgePackageBuilder.BridgeVersion,
            StringComparison.OrdinalIgnoreCase);
        var serverAssembly = currentVersion
            ? ReadEmbeddedBridgeAssembly("BCSTool.Assets.CoopBridge.BCS.CoopBridge.Server.dll")
            : [1, 3, 5, 7];
        var clientAssembly = currentVersion
            ? ReadEmbeddedBridgeAssembly("BCSTool.Assets.CoopBridge.BCS.CoopBridge.Client.dll")
            : [2, 4, 6, 8];
        var payload = new byte[configuration.Length + serverAssembly.Length + clientAssembly.Length];
        Buffer.BlockCopy(configuration, 0, payload, 0, configuration.Length);
        Buffer.BlockCopy(serverAssembly, 0, payload, configuration.Length, serverAssembly.Length);
        Buffer.BlockCopy(
            clientAssembly,
            0,
            payload,
            configuration.Length + serverAssembly.Length,
            clientAssembly.Length);
        var id = CoopBridgePackageBuilder.BridgeIdPrefix +
                 Convert.ToHexString(SHA256.HashData(payload))[..24].ToLowerInvariant();
        var root = Path.Combine(modulesRoot, id);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "SubModule.xml"),
            $$"""
            <Module>
              <Name value="{{id}}" />
              <Id value="{{id}}" />
              <Version value="{{version}}" />
              <DependedModules>
                <DependedModule Id="{{targetModuleId}}" />
              </DependedModules>
              <SubModules />
            </Module>
            """,
            Utf8NoBom);
        File.WriteAllBytes(Path.Combine(root, "bcs-coop-bridge.config"), configuration);
        var serverBin = Path.Combine(root, "bin", "Win64_Shipping_Server");
        var clientBin = Path.Combine(root, "bin", "Win64_Shipping_Client");
        Directory.CreateDirectory(serverBin);
        Directory.CreateDirectory(clientBin);
        File.WriteAllBytes(Path.Combine(serverBin, "BCS.CoopBridge.dll"), serverAssembly);
        File.WriteAllBytes(Path.Combine(clientBin, "BCS.CoopBridge.dll"), clientAssembly);

        return new ModuleScanner().Scan(modulesRoot).Single(module => module.Id == id);
    }

    private static byte[] BuildConfiguration(
        IReadOnlyList<string> enabledDlls,
        IReadOnlyList<string> disabledDlls,
        string moduleId = "TargetMod",
        string moduleVersion = "v1.0.0")
    {
        var builder = new StringBuilder("BCS-COOP-BRIDGE|2\n");
        if (enabledDlls.Count == 0)
        {
            builder.Append("MODULE|")
                .Append(Encode(moduleId)).Append('|')
                .Append(Encode(moduleVersion)).Append("||\n");
        }
        else
        {
            foreach (var dllName in enabledDlls)
            {
                builder.Append("MODULE|")
                    .Append(Encode(moduleId)).Append('|')
                    .Append(Encode(moduleVersion)).Append('|')
                    .Append(Encode(dllName)).Append("|\n");
            }
        }
        foreach (var dllName in disabledDlls)
        {
            builder.Append("DISABLED_SUBMODULE|")
                .Append(Encode(moduleId)).Append('|')
                .Append(Encode(dllName)).Append('\n');
        }
        return Utf8NoBom.GetBytes(builder.ToString());
    }

    private static void WriteTargetManifest(string root, bool suppressSecond)
    {
        var tags = suppressSecond
            ? """
              <Tags>
                <Tag key="DedicatedServerType" value="none" />
                <Tag key="IsNoRenderModeElement" value="false" />
              </Tags>
              """
            : string.Empty;
        File.WriteAllText(
            Path.Combine(root, "SubModule.xml"),
            $$"""
            <Module>
              <Name value="TargetMod" />
              <Id value="TargetMod" />
              <Version value="v1.0.0" />
              <DependedModules />
              <SubModules>
                <SubModule>
                  <Name value="First" />
                  <DLLName value="First.dll" />
                  <SubModuleClassType value="First.SubModule" />
                </SubModule>
                <SubModule>
                  <Name value="Second" />
                  <DLLName value="Second.dll" />
                  <SubModuleClassType value="Second.SubModule" />
                  {{tags}}
                </SubModule>
              </SubModules>
            </Module>
            """,
            Utf8NoBom);
    }

    private static void WriteEurope1700LegacyManifest(string root)
    {
        File.WriteAllText(
            Path.Combine(root, "SubModule.xml"),
            """
            <Module>
              <Name value="Europe1700" />
              <Id value="Europe1700" />
              <Version value="v1.4.7.1" />
              <DependedModules />
              <SubModules>
                <SubModule>
                  <Name value="First" />
                  <DLLName value="First.dll" />
                  <SubModuleClassType value="First.SubModule" />
                </SubModule>
                <SubModule>
                  <Name value="BannerColorPersistence" />
                  <DLLName value="BannerColorPersistence.dll" />
                  <SubModuleClassType value="BannerColorPersistence.SubModule" />
                  <Tags>
                    <Tag key="DedicatedServerType" value="none" />
                    <Tag key="IsNoRenderModeElement" value="false" />
                  </Tags>
                </SubModule>
                <SubModule>
                  <Name value="CustomBattlePatch" />
                  <DLLName value="EOE.CustomBattlePatch.dll" />
                  <SubModuleClassType value="EOE.CustomBattlePatch.SubModule" />
                </SubModule>
                <SubModule>
                  <Name value="RF Battle AI" />
                  <DLLName value="RF_BattleAI.dll" />
                  <SubModuleClassType value="RF_BattleAI.SubModule" />
                </SubModule>
              </SubModules>
            </Module>
            """,
            Utf8NoBom);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Utf8NoBom.GetBytes(value));

    private static bool HasMissingAssemblyBlocker(CoopPreparationPlan plan, string dllName) =>
        plan.Blockers.Contains(
            $"Empires of Europe 1700 assembly is missing: {dllName}.",
            StringComparer.Ordinal);

    private static byte[] ReadEmbeddedBridgeAssembly(string resourceName)
    {
        using var stream = typeof(CoopBridgePackageBuilder).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing regression resource: {resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

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
