using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;
using System.Text.Json;

internal static class BridgeInstallationRegression
{
    public static void Run()
    {
        var scanner = new ModuleScanner();
        var service = new BridgeInstallationService(
            scanner,
            new CoopCompatibilityPatcher());
        var eoe = Module("Europe1700");
        var unrelated = Module("UnrelatedMod");

        Assert(service.IsKnownRecipe(eoe),
            "Europe1700 did not activate the known bridge recipe.");
        Assert(!service.IsKnownRecipe(unrelated),
            "An unrelated module activated a known bridge recipe.");
        Assert(service.GetRecipeDisplayName(eoe) == "Empires of Europe 1700",
            "Known recipe did not expose its user-facing name.");
        VerifyRecipeRegistryIsExplicitAndUnique();

        VerifyNoInstalledRecipeIsNoOp(service);
        VerifyDroppedRecipeNeedsNoManualEnableOrSave(scanner, service);
        VerifyEnabledRecipeDispatchesRepair(scanner);
        VerifyDisabledRecipeIsNoOp(scanner);
        VerifyBackupsAreScopedToEurope1700(service);
    }

    private static void VerifyRecipeRegistryIsExplicitAndUnique()
    {
        var recipes = CompatibilityRecipeRegistry.All;
        Assert(recipes.Count > 0,
            "Compatibility recipe registry is empty.");
        Assert(recipes.Select(recipe => recipe.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
               recipes.Count,
            "Compatibility recipe registry repeats a recipe ID.");
        Assert(recipes.Select(recipe => recipe.RootModuleId)
                   .Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipes.Count,
            "Compatibility recipe registry repeats a root module ID.");
        Assert(recipes.All(recipe =>
                recipe.RecognizedRuleIds.Contains(recipe.CurrentRuleId, StringComparer.Ordinal)),
            "A compatibility recipe does not recognize its current rule ID.");

        var eoe = CompatibilityRecipeRegistry.FindByRootModule("Europe1700");
        Assert(eoe is not null &&
               eoe.SupportsPopulationGuide &&
               eoe.CampaignSaveDescription == "EOE" &&
               eoe.RuntimeFeatures.Count == Enum.GetValues<BridgeRuntimeFeature>().Length &&
               Enum.GetValues<BridgeRuntimeFeature>().All(eoe.RuntimeFeatures.Contains),
            "Europe1700 recipe did not preserve its complete runtime compatibility feature set.");
    }

    private static void VerifyDroppedRecipeNeedsNoManualEnableOrSave(
        ModuleScanner scanner,
        BridgeInstallationService service)
    {
        var root = TemporaryRoot();
        var sourceRoot = TemporaryRoot();
        try
        {
            var executable = Path.Combine(root, "BannerlordCoopServer.exe");
            var modulesDirectory = Path.Combine(root, "engine", "Modules");
            Directory.CreateDirectory(modulesDirectory);
            File.WriteAllBytes(executable, [1]);
            var source = Path.Combine(sourceRoot, "Europe1700");
            WriteModule(source, "v1.4.7.1");

            var manager = new ModuleManager(executable, scanner);
            var importer = new ModuleImporter(modulesDirectory, scanner);
            var viewModel = new ModManagerViewModel(
                manager,
                importer,
                new ModuleRemovalService(manager, new RejectingRecycler()),
                new DependencyValidator(),
                new CoopCompatibilityAnalyzer(),
                service);

            viewModel.InitializeAsync().GetAwaiter().GetResult();
            viewModel.ImportFoldersAsync([source]).GetAwaiter().GetResult();

            var selected = viewModel.SelectedModule;
            Assert(selected?.Id == "Europe1700",
                "Dropped bridge recipe was not selected automatically.");
            if (selected is null)
                throw new InvalidOperationException("Dropped bridge recipe selection was null.");
            Assert(selected.IsBridgeManaged && !selected.CanToggle &&
                   selected.StateText == "NEEDS BRIDGE",
                "Dropped bridge recipe still exposed a misleading manual OFF toggle.");
            Assert(!viewModel.IsDirty && viewModel.CanPrepareSelectedBridge,
                "Dropped bridge recipe required Analyze, enable, reorder, or Save before preparation.");

            var pendingSelection = new BridgeDllSelection(
                selected.Id,
                selected.Version,
                ["Test.dll"],
                []);
            viewModel.ApplyBridgeDllSelection(pendingSelection);
            viewModel.SelectedModule = null;
            viewModel.SelectedModule = selected;
            BridgeDllSelection? reopenedSelection = null;
            viewModel.BridgeDllSelectionRequested += value => reopenedSelection = value;
            viewModel.OpenBridgeDllOptionsCommand.Execute(null);
            Assert(reopenedSelection is { SelectedDllNames.Count: 0 } &&
                   !reopenedSelection.IsSelected("Test.dll"),
                "Changing rows discarded the pending bridge DLL selection.");
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            reopenedSelection = null;
            viewModel.OpenBridgeDllOptionsCommand.Execute(null);
            Assert(reopenedSelection is { SelectedDllNames.Count: 1 } &&
                   reopenedSelection.IsSelected("Test.dll"),
                "Rescanning retained a stale pending bridge DLL selection.");

            var refusedOverwrite = false;
            try
            {
                importer.Discover([source]);
            }
            catch (IOException)
            {
                refusedOverwrite = true;
            }
            Assert(refusedOverwrite,
                "Re-dropping a module silently exposed the installed server copy to overwrite.");

            var reopened = new ModManagerViewModel(
                manager,
                importer,
                new ModuleRemovalService(manager, new RejectingRecycler()),
                new DependencyValidator(),
                new CoopCompatibilityAnalyzer(),
                service);
            reopened.InitializeAsync().GetAwaiter().GetResult();
            Assert(reopened.SelectedModule?.Id == "Europe1700" &&
                   reopened.CanPrepareSelectedBridge,
                "Reopening the mod list required manually selecting the installed bridge recipe.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
            DeleteTemporaryRoot(sourceRoot);
        }
    }

    private static void VerifyNoInstalledRecipeIsNoOp(
        BridgeInstallationService service)
    {
        var root = TemporaryRoot();
        try
        {
            var executable = Path.Combine(root, "BannerlordCoopServer.exe");
            Directory.CreateDirectory(Path.Combine(root, "engine", "Modules"));
            File.WriteAllBytes(executable, [1]);

            var result = service.RepairForStart(executable);
            Assert(!result.RecipeDetected && !result.ChangesApplied,
                "Start preflight changed a server with no enabled EOE recipe.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static void VerifyEnabledRecipeDispatchesRepair(ModuleScanner scanner)
    {
        var root = TemporaryRoot();
        try
        {
            var executable = CreateServerWithEurope1700(root, enabled: true);
            var dispatchCount = 0;
            var service = new BridgeInstallationService(
                scanner,
                new CoopCompatibilityPatcher(),
                (module, modules, serverRoot) =>
                {
                    dispatchCount++;
                    Assert(module.Id.Equals("Europe1700", StringComparison.OrdinalIgnoreCase),
                        "Start repair dispatched the wrong module recipe.");
                    Assert(modules.Any(candidate =>
                            candidate.Id.Equals("Europe1700", StringComparison.OrdinalIgnoreCase)),
                        "Start repair omitted Europe1700 from its module snapshot.");
                    Assert(serverRoot.Equals(root, StringComparison.OrdinalIgnoreCase),
                        "Start repair used the wrong dedicated-server root.");
                    return new BridgeInstallationResult(
                        RecipeDetected: true,
                        ChangesApplied: true,
                        ModuleIds: ["Europe1700", "BCS.CoopBridge.test"],
                        ClientPackagePath: Path.Combine(root, "bridge.zip"),
                        BackupDirectory: Path.Combine(root, "backup"));
                });

            var result = service.RepairForStart(executable);
            Assert(dispatchCount == 1,
                "Enabled Europe1700 did not dispatch exactly one start repair.");
            Assert(result.RecipeDetected && result.ChangesApplied,
                "Enabled Europe1700 did not return the bridge repair result.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static void VerifyDisabledRecipeIsNoOp(ModuleScanner scanner)
    {
        var root = TemporaryRoot();
        try
        {
            var executable = CreateServerWithEurope1700(root, enabled: false);
            var dispatchCount = 0;
            var service = new BridgeInstallationService(
                scanner,
                new CoopCompatibilityPatcher(),
                (_, _, _) =>
                {
                    dispatchCount++;
                    throw new InvalidOperationException(
                        "Disabled Europe1700 dispatched bridge installation.");
                });

            var result = service.RepairForStart(executable);
            Assert(dispatchCount == 0,
                "Disabled Europe1700 dispatched a start repair.");
            Assert(!result.RecipeDetected && !result.ChangesApplied,
                "Disabled Europe1700 did not remain a no-op.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static void VerifyBackupsAreScopedToEurope1700(
        BridgeInstallationService service)
    {
        var root = TemporaryRoot();
        try
        {
            Directory.CreateDirectory(root);
            var eoeManifest = CreateBackupManifest(
                root,
                "eoe-plan",
                "europe-1700-1.4.7.1-server-v57",
                ["Europe1700", "BCS.CoopBridge.eoe"]);
            var legacyEoeManifest = CreateBackupManifest(
                root,
                "legacy-eoe-plan",
                "europe-1700-1.4.7.1-server-v55",
                ["Europe1700", "BCS.CoopBridge.legacy"]);
            var unrelatedManifest = CreateBackupManifest(
                root,
                "newer-generic-plan",
                "generic-executable-bridge-v1",
                ["UnrelatedMod", "BCS.CoopBridge.generic"]);
            File.SetLastWriteTimeUtc(eoeManifest, new DateTime(2026, 8, 13, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                legacyEoeManifest,
                new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                unrelatedManifest,
                new DateTime(2026, 8, 13, 2, 0, 0, DateTimeKind.Utc));

            Assert(service.FindLatestInstallationBackup(root) == eoeManifest,
                "A newer non-EOE backup hid the latest EOE bridge installation backup.");

            File.Delete(eoeManifest);
            Assert(service.FindLatestInstallationBackup(root) == legacyEoeManifest,
                "The bridge lifecycle stopped recognizing a real v55 EOE backup.");

            var rejected = false;
            try
            {
                _ = service.RevertInstallation(unrelatedManifest);
            }
            catch (InvalidDataException exception)
            {
                rejected = exception.Message.Contains(
                    "not a recognized bridge installation",
                    StringComparison.Ordinal);
            }

            Assert(rejected,
                "Bridge lifecycle accepted direct reversion of a non-EOE backup.");
            Assert(!File.Exists(Path.Combine(
                    Path.GetDirectoryName(unrelatedManifest)!,
                    "REVERTED.txt")),
                "Rejected non-EOE backup was modified by the bridge lifecycle.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static string CreateBackupManifest(
        string serverRoot,
        string planId,
        string ruleId,
        string[] moduleIds)
    {
        var planDirectory = Path.Combine(
            serverRoot,
            "bcs-compatibility-backups",
            planId);
        Directory.CreateDirectory(planDirectory);
        var manifest = Path.Combine(planDirectory, "bcs-compatibility-backup.json");
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                PlanId = planId,
                CreatedUtc = DateTimeOffset.UtcNow,
                ServerRoot = serverRoot,
                RuleId = ruleId,
                ModuleIds = moduleIds,
                Files = Array.Empty<object>()
            }));
        return manifest;
    }

    private static string CreateServerWithEurope1700(string root, bool enabled)
    {
        var executable = Path.Combine(root, "BannerlordCoopServer.exe");
        var moduleRoot = Path.Combine(root, "engine", "Modules", "Europe1700");
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllBytes(executable, [1]);
        File.WriteAllText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            "<Module>" +
            "<Name value=\"Empires of Europe 1700\" />" +
            "<Id value=\"Europe1700\" />" +
            "<Version value=\"v1.4.7.1\" />" +
            "<DependedModules />" +
            "<SubModules />" +
            "</Module>");
        File.WriteAllText(
            Path.Combine(root, "bcs-server-modules.json"),
            "{\"SchemaVersion\":1,\"Modules\":[{\"Id\":\"Europe1700\",\"Enabled\":" +
            (enabled ? "true" : "false") + "}]}");
        return executable;
    }

    private static void WriteModule(string moduleRoot, string version)
    {
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            "<Module>" +
            "<Name value=\"Empires of Europe 1700\" />" +
            "<Id value=\"Europe1700\" />" +
            $"<Version value=\"{version}\" />" +
            "<DependedModules />" +
            "<SubModules><SubModule>" +
            "<Name value=\"Test\" />" +
            "<DLLName value=\"Test.dll\" />" +
            "<SubModuleClassType value=\"Test.SubModule\" />" +
            "</SubModule></SubModules>" +
            "</Module>");
    }

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        "bcs-bridge-lifecycle-regression-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static BannerlordModule Module(string id) => new()
    {
        Name = id,
        Id = id,
        Version = "v1.0.0",
        Path = Path.Combine(Path.GetTempPath(), id),
        IsInstalled = true,
        IsRequired = false,
        IsServerCompatible = true,
        Dependencies = Array.Empty<string>(),
        MustLoadAfter = Array.Empty<string>(),
        MustLoadBefore = Array.Empty<string>(),
        IncompatibleModules = Array.Empty<string>()
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RejectingRecycler : IModuleDirectoryRecycler
    {
        public void Recycle(string directoryPath) =>
            throw new InvalidOperationException("Recycler must not run in bridge-flow regression.");
    }
}
