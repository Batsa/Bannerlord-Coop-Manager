using BCSTool.Models;
using BCSTool.Services;
using BCSTool.ViewModels;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        VerifyUnlinkedCompatibilityWriteTargetIsRejected();
        VerifyNoInstalledRecipeIsNoOp(service);
        VerifyDroppedRecipeNeedsNoManualEnableOrSave(scanner, service);
        VerifyInactiveGeneratedBridgeRemovalIsGuarded(scanner, service);
        VerifyEnabledRecipeDispatchesRepair(scanner);
        VerifyDisabledRecipeIsNoOp(scanner);
        VerifyBackupsAreScopedToEurope1700(service);
    }

    private static void VerifyUnlinkedCompatibilityWriteTargetIsRejected()
    {
        var root = TemporaryRoot();
        var externalRoot = TemporaryRoot();
        var catalogDirectory = Path.Combine(
            root,
            "engine",
            "Modules",
            "BCS.CoopBridge.test",
            "BattleSceneCatalog");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(catalogDirectory)!);
            Directory.CreateDirectory(externalRoot);
            var sentinelPath = Path.Combine(externalRoot, "sentinel.txt");
            File.WriteAllText(sentinelPath, "outside");
            CreateDirectoryJunction(catalogDirectory, externalRoot);

            var rejected = false;
            try
            {
                CoopCompatibilityPatcher.ValidateUnlinkedWriteTarget(
                    Path.Combine(catalogDirectory, "contract.bcs"),
                    root);
            }
            catch (InvalidDataException exception)
            {
                rejected = exception.Message.Contains(
                    "linked parent",
                    StringComparison.OrdinalIgnoreCase);
            }

            Assert(rejected &&
                   File.ReadAllText(sentinelPath).Equals("outside", StringComparison.Ordinal) &&
                   !File.Exists(Path.Combine(externalRoot, "contract.bcs")),
                "Compatibility apply accepted a catalog target through a junction.");

            var contractBytes = Encoding.UTF8.GetBytes("external catalog");
            var contractPath = Path.Combine(externalRoot, "contract.bcs");
            File.WriteAllBytes(contractPath, contractBytes);
            const string planId = "linked-revert";
            var planDirectory = Path.Combine(root, "bcs-compatibility-backups", planId);
            Directory.CreateDirectory(planDirectory);
            var manifestPath = Path.Combine(
                planDirectory,
                "bcs-compatibility-backup.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    PlanId = planId,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    ServerRoot = root,
                    RuleId = "linked-revert-regression",
                    ModuleIds = Array.Empty<string>(),
                    Files = new[]
                    {
                        new
                        {
                            RelativePath = Path.GetRelativePath(
                                root,
                                Path.Combine(catalogDirectory, "contract.bcs")),
                            OriginalExisted = false,
                            OriginalSha256 = string.Empty,
                            AppliedSha256 = Convert.ToHexString(SHA256.HashData(contractBytes))
                        }
                    }
                }));

            var revertRejected = false;
            try
            {
                _ = new CoopCompatibilityPatcher().Revert(manifestPath);
            }
            catch (InvalidDataException exception)
            {
                revertRejected = exception.Message.Contains(
                    "linked parent",
                    StringComparison.OrdinalIgnoreCase);
            }

            Assert(revertRejected &&
                   File.ReadAllBytes(contractPath).SequenceEqual(contractBytes) &&
                   File.ReadAllText(sentinelPath).Equals("outside", StringComparison.Ordinal) &&
                   !File.Exists(Path.Combine(planDirectory, "REVERTED.txt")),
                "Compatibility revert followed a catalog junction or published a revert marker.");
        }
        finally
        {
            if (Directory.Exists(catalogDirectory) &&
                (File.GetAttributes(catalogDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(catalogDirectory);
            }
            DeleteTemporaryRoot(root);
            DeleteTemporaryRoot(externalRoot);
        }
    }

    private static void VerifyInactiveGeneratedBridgeRemovalIsGuarded(
        ModuleScanner scanner,
        BridgeInstallationService service)
    {
        var root = TemporaryRoot();
        try
        {
            var executable = Path.Combine(root, "BannerlordCoopServer.exe");
            var modulesDirectory = Path.Combine(root, "engine", "Modules");
            Directory.CreateDirectory(modulesDirectory);
            File.WriteAllBytes(executable, [1]);

            var standardDependencies = new (string Id, string Version)[]
            {
                ("Coop", "v0.1.2"),
                ("Europe1700", "v1.4.7.1")
            };
            var catalogDependencies = standardDependencies
                .Append((Id: "SandBoxCore", Version: "v1.4.8"))
                .ToArray();
            foreach (var dependency in catalogDependencies)
            {
                WriteDependencyModule(
                    Path.Combine(modulesDirectory, dependency.Id),
                    dependency.Id,
                    dependency.Version);
            }

            var activeConfiguration = BridgeConfiguration("active", standardDependencies);
            var inactiveConfiguration = BridgeConfiguration("inactive", standardDependencies);
            var activeId = CoopBridgePackageBuilder.ComputeBridgeId(activeConfiguration);
            var inactiveId = CoopBridgePackageBuilder.ComputeBridgeId(inactiveConfiguration);
            var activePath = Path.Combine(modulesDirectory, activeId);
            var inactivePath = Path.Combine(modulesDirectory, inactiveId);
            WriteBridgeModule(
                activePath,
                activeId,
                activeConfiguration,
                standardDependencies);
            WriteBridgeModule(
                inactivePath,
                inactiveId,
                inactiveConfiguration,
                standardDependencies);

            var active = Module(activeId, activePath, enabled: true);
            var inactive = Module(inactiveId, inactivePath);
            var recycler = new DeletingRecycler();
            var manager = new ModuleManager(executable, scanner);
            manager.Save([active, inactive]);
            var profileBeforeRemoval = File.ReadAllBytes(manager.ProfilePath);
            var removalService = new ModuleRemovalService(manager, recycler, service);
            var viewModel = new ModManagerViewModel(
                manager,
                new ModuleImporter(modulesDirectory, scanner),
                removalService,
                new DependencyValidator(),
                new CoopCompatibilityAnalyzer(),
                service);
            viewModel.Modules.Add(active);
            viewModel.Modules.Add(inactive);

            var battleSceneCatalog = BattleSceneCatalogContractRegistry.CreateEurope1700();
            var rollbackConfiguration = BridgeConfiguration(
                "rollback-source",
                catalogDependencies,
                battleSceneCatalog);
            var rollbackBridgeId = CoopBridgePackageBuilder.ComputeBridgeId(
                rollbackConfiguration,
                battleSceneCatalog.Content);
            var rollbackBridge = Module(
                rollbackBridgeId,
                Path.Combine(modulesDirectory, rollbackBridgeId));
            WriteBridgeModule(
                rollbackBridge.Path,
                rollbackBridgeId,
                rollbackConfiguration,
                catalogDependencies,
                battleSceneCatalog);
            var rollbackProfileBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    SchemaVersion = 1,
                    Modules = new[]
                    {
                        new { Id = rollbackBridgeId, Enabled = true }
                    }
                },
                new JsonSerializerOptions { WriteIndented = true });
            var rollbackManifest = CreateBackupManifest(
                root,
                "rollback-owned-bridge",
                "europe-1700-1.4.7.1-server-v58",
                ["Europe1700", activeId],
                rollbackProfileBytes,
                profileBeforeRemoval);
            var rollbackProfile = Path.Combine(
                Path.GetDirectoryName(rollbackManifest)!,
                "files",
                "bcs-server-modules.json");
            viewModel.Modules.Add(rollbackBridge);

            viewModel.SelectedModule = inactive;
            Assert(viewModel.DeleteCommand.CanExecute(null) &&
                   viewModel.DeleteModuleHelpText.Contains(
                       "inactive historical bridge",
                       StringComparison.Ordinal),
                "An inactive historical bridge with one active replacement was not deletable.");

            viewModel.SelectedModule = active;
            Assert(!viewModel.DeleteCommand.CanExecute(null) &&
                   viewModel.DeleteModuleHelpText.Contains(
                       "active generated bridge",
                       StringComparison.Ordinal),
                "The active generated bridge was exposed to deletion.");
            var directActiveRemovalRejected = false;
            try
            {
                removalService.Remove(active, [active, inactive]);
            }
            catch (InvalidOperationException exception)
            {
                directActiveRemovalRejected = exception.Message.Contains(
                    "active generated bridge",
                    StringComparison.Ordinal);
            }
            Assert(directActiveRemovalRejected,
                "The removal service allowed direct deletion of the active generated bridge.");

            viewModel.SelectedModule = rollbackBridge;
            Assert(!viewModel.DeleteCommand.CanExecute(null) &&
                   viewModel.DeleteModuleHelpText.Contains(
                       "latest Revert Bridge Install backup",
                       StringComparison.Ordinal),
                "The bridge required by the latest rollback was exposed to deletion.");

            var manifestBytes = File.ReadAllBytes(rollbackManifest);
            File.WriteAllText(rollbackManifest, "{");
            viewModel.SelectedModule = inactive;
            Assert(!viewModel.DeleteCommand.CanExecute(null) &&
                   viewModel.DeleteModuleHelpText.Contains(
                       "could not be validated",
                       StringComparison.Ordinal),
                "Malformed rollback JSON escaped WPF command evaluation or failed open.");
            File.WriteAllBytes(rollbackManifest, manifestBytes);

            var displacedRollbackProfile = rollbackProfile + ".missing";
            File.Move(rollbackProfile, displacedRollbackProfile);
            try
            {
                Assert(!viewModel.DeleteCommand.CanExecute(null) &&
                       viewModel.DeleteModuleHelpText.Contains(
                           "could not be validated",
                           StringComparison.Ordinal),
                    "A manifest-declared missing rollback profile failed open.");
            }
            finally
            {
                File.Move(displacedRollbackProfile, rollbackProfile);
            }

            var displacedRollbackBridge = rollbackBridge.Path + ".missing";
            Directory.Move(rollbackBridge.Path, displacedRollbackBridge);
            var missingRestoredBridgeRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                missingRestoredBridgeRejected = exception.Message.Contains(
                    "restored active generated bridge folder",
                    StringComparison.Ordinal);
            }
            finally
            {
                Directory.Move(displacedRollbackBridge, rollbackBridge.Path);
            }
            Assert(missingRestoredBridgeRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert activated a missing prior bridge or touched current state before rejecting it.");

            var rollbackServerAssembly = Path.Combine(
                rollbackBridge.Path,
                "bin",
                "Win64_Shipping_Server",
                "BCS.CoopBridge.dll");
            var displacedServerAssembly = rollbackServerAssembly + ".missing";
            File.Move(rollbackServerAssembly, displacedServerAssembly);
            var missingRestoredRuntimeRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                missingRestoredRuntimeRejected = exception.Message.Contains(
                    "bridge server assembly",
                    StringComparison.Ordinal);
            }
            finally
            {
                File.Move(displacedServerAssembly, rollbackServerAssembly);
            }
            Assert(missingRestoredRuntimeRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert activated a bridge without its server runtime or mutated current state.");

            var rollbackServerAssemblyBytes = File.ReadAllBytes(rollbackServerAssembly);
            File.WriteAllBytes(rollbackServerAssembly, [1]);
            var corruptRestoredRuntimeRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                corruptRestoredRuntimeRejected = exception.Message.Contains(
                    "not a valid managed BCS.CoopBridge assembly",
                    StringComparison.Ordinal);
            }
            finally
            {
                File.WriteAllBytes(rollbackServerAssembly, rollbackServerAssemblyBytes);
            }
            Assert(corruptRestoredRuntimeRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert accepted a corrupt prior bridge runtime or mutated current state.");

            var rollbackSubModule = Path.Combine(rollbackBridge.Path, "SubModule.xml");
            var rollbackSubModuleBytes = File.ReadAllBytes(rollbackSubModule);
            File.WriteAllText(
                rollbackSubModule,
                File.ReadAllText(rollbackSubModule).Replace(
                    rollbackBridgeId,
                    "BCS.CoopBridge.wrong-identity",
                    StringComparison.Ordinal));
            var mismatchedRestoredManifestRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                mismatchedRestoredManifestRejected = exception.Message.Contains(
                    "exact generated bridge identity",
                    StringComparison.Ordinal);
            }
            finally
            {
                File.WriteAllBytes(rollbackSubModule, rollbackSubModuleBytes);
            }
            Assert(mismatchedRestoredManifestRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert accepted a mismatched generated bridge manifest or mutated current state.");

            File.WriteAllText(
                rollbackSubModule,
                File.ReadAllText(rollbackSubModule).Replace(
                    CoopBridgePackageBuilder.BridgeVersion,
                    "v0.0.1",
                    StringComparison.Ordinal));
            var mismatchedManifestVersionRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                mismatchedManifestVersionRejected = exception.Message.Contains(
                    "manifest version",
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.WriteAllBytes(rollbackSubModule, rollbackSubModuleBytes);
            }
            Assert(mismatchedManifestVersionRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert accepted a bridge manifest version that disagreed with its runtimes.");

            File.WriteAllText(
                rollbackSubModule,
                File.ReadAllText(rollbackSubModule).Replace(
                    "DependentVersion=\"v0.1.2\"",
                    "DependentVersion=\"v9.9.9\"",
                    StringComparison.Ordinal));
            var mismatchedManifestDependencyRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                mismatchedManifestDependencyRejected = exception.Message.Contains(
                    "manifest dependencies",
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.WriteAllBytes(rollbackSubModule, rollbackSubModuleBytes);
            }
            Assert(mismatchedManifestDependencyRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert accepted bridge manifest dependencies that disagreed with configuration.");

            File.WriteAllText(
                rollbackSubModule,
                File.ReadAllText(rollbackSubModule)
                    .Replace(
                        "<SingleplayerModule value=\"true\" />",
                        "<SingleplayerModule value=\"false\" />",
                        StringComparison.Ordinal)
                    .Replace(
                        "<MultiplayerModule value=\"false\" />",
                        "<MultiplayerModule value=\"true\" />",
                        StringComparison.Ordinal));
            var mismatchedManifestRoleRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                mismatchedManifestRoleRejected = exception.Message.Contains(
                    "load-role flags",
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.WriteAllBytes(rollbackSubModule, rollbackSubModuleBytes);
            }
            Assert(mismatchedManifestRoleRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert accepted a bridge manifest with client/server load roles reversed.");

            var catalogDirectory = Path.GetDirectoryName(Path.Combine(
                rollbackBridge.Path,
                battleSceneCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar)))!;
            var originalCatalogDirectory = catalogDirectory + ".original";
            var externalCatalogDirectory = Path.Combine(root, "external-battle-scene-catalog");
            Directory.Move(catalogDirectory, originalCatalogDirectory);
            Directory.CreateDirectory(externalCatalogDirectory);
            File.WriteAllBytes(
                Path.Combine(externalCatalogDirectory, Path.GetFileName(battleSceneCatalog.RelativePath)),
                battleSceneCatalog.Content);
            CreateDirectoryJunction(catalogDirectory, externalCatalogDirectory);
            var linkedCatalogRejected = false;
            try
            {
                _ = service.RevertInstallation(rollbackManifest);
            }
            catch (InvalidDataException exception)
            {
                linkedCatalogRejected = exception.Message.Contains(
                    "linked directory",
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (Directory.Exists(catalogDirectory))
                    Directory.Delete(catalogDirectory);
                Directory.Move(originalCatalogDirectory, catalogDirectory);
            }
            Assert(linkedCatalogRejected &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)) &&
                   !File.Exists(Path.Combine(
                       Path.GetDirectoryName(rollbackManifest)!,
                       "REVERTED.txt")),
                "Revert accepted a catalog contract reached through a junction.");

            var lastInactive = Module(
                "BCS.CoopBridge.last",
                Path.Combine(modulesDirectory, "BCS.CoopBridge.last"));
            viewModel.Modules.Clear();
            viewModel.Modules.Add(lastInactive);
            viewModel.SelectedModule = lastInactive;
            Assert(!viewModel.DeleteCommand.CanExecute(null) &&
                   viewModel.DeleteModuleHelpText.Contains(
                       "exactly one other generated bridge is active",
                       StringComparison.Ordinal),
                "The last inactive generated bridge lost its policy-recovery guard.");

            var dependent = Module(
                "EnabledDependent",
                Path.Combine(modulesDirectory, "EnabledDependent"),
                enabled: true,
                dependencies: [inactiveId]);
            viewModel.Modules.Clear();
            viewModel.Modules.Add(active);
            viewModel.Modules.Add(inactive);
            viewModel.Modules.Add(dependent);
            viewModel.SelectedModule = inactive;
            Assert(!viewModel.DeleteCommand.CanExecute(null) &&
                   viewModel.DeleteModuleHelpText.Contains(
                       dependent.Id,
                       StringComparison.Ordinal),
                "An enabled dependent did not block historical bridge deletion.");

            var secondActive = Module(
                "BCS.CoopBridge.second-active",
                Path.Combine(modulesDirectory, "BCS.CoopBridge.second-active"),
                enabled: true);
            viewModel.Modules.Remove(dependent);
            viewModel.Modules.Add(secondActive);
            viewModel.SelectedModule = inactive;
            Assert(!viewModel.DeleteCommand.CanExecute(null),
                "Ambiguous multiple active bridges allowed historical bridge deletion.");

            var generic = Module(
                "GenericMod",
                Path.Combine(modulesDirectory, "GenericMod"));
            viewModel.Modules.Add(generic);
            viewModel.SelectedModule = generic;
            Assert(viewModel.DeleteCommand.CanExecute(null),
                "Generated-bridge safety rules blocked ordinary non-core module deletion.");

            var displacedActivePath = activePath + ".missing";
            Directory.Move(activePath, displacedActivePath);
            var staleReplacementRejected = false;
            try
            {
                removalService.Remove(inactive, [active, inactive]);
            }
            catch (InvalidOperationException exception)
            {
                staleReplacementRejected = exception.Message.Contains(
                    "active replacement bridge folder",
                    StringComparison.Ordinal);
            }
            finally
            {
                Directory.Move(displacedActivePath, activePath);
            }
            Assert(staleReplacementRejected &&
                   Directory.Exists(inactivePath) &&
                   recycler.Count == 0,
                "A stale active replacement folder did not fail closed before removal.");

            removalService.Remove(inactive, [active, inactive]);
            Assert(recycler.Count == 1 &&
                   !Directory.Exists(inactivePath) &&
                   Directory.Exists(activePath),
                "Historical bridge removal did not recycle only the inactive folder.");
            var profileAfterRemoval = File.ReadAllBytes(manager.ProfilePath);
            Assert(profileBeforeRemoval.SequenceEqual(profileAfterRemoval),
                "Historical bridge removal changed the rollback-owned module profile bytes.");
            using var profileDocument = JsonDocument.Parse(profileAfterRemoval);
            var profileEntries = profileDocument.RootElement
                .GetProperty("Modules")
                .EnumerateArray()
                .Select(entry => (
                    Id: entry.GetProperty("Id").GetString(),
                    Enabled: entry.GetProperty("Enabled").GetBoolean()))
                .ToArray();
            Assert(profileEntries.Length == 2 &&
                   profileEntries[0] == (activeId, true) &&
                   profileEntries[1] == (inactiveId, false),
                "Historical bridge removal did not preserve exact active/inactive profile state.");
            var reloaded = manager.Load();
            Assert(reloaded.Any(module =>
                       module.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase) &&
                       module.IsInstalled &&
                       module.Enabled) &&
                   reloaded.All(module =>
                       !module.Id.Equals(inactiveId, StringComparison.OrdinalIgnoreCase)) &&
                   profileBeforeRemoval.SequenceEqual(File.ReadAllBytes(manager.ProfilePath)),
                "A deleted historical bridge returned as a missing row or changed profile bytes after rescan.");
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
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
                new ModuleRemovalService(manager, new RejectingRecycler(), service),
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
                new ModuleRemovalService(manager, new RejectingRecycler(), service),
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
                "europe-1700-1.4.7.1-server-v58",
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
        string[] moduleIds,
        byte[]? originalProfileBytes = null,
        byte[]? appliedProfileBytes = null)
    {
        if ((originalProfileBytes is null) != (appliedProfileBytes is null))
            throw new ArgumentException("Both rollback profile byte arrays are required together.");

        var planDirectory = Path.Combine(
            serverRoot,
            "bcs-compatibility-backups",
            planId);
        Directory.CreateDirectory(planDirectory);
        object[] files = [];
        if (originalProfileBytes is not null && appliedProfileBytes is not null)
        {
            var originalProfilePath = Path.Combine(
                planDirectory,
                "files",
                "bcs-server-modules.json");
            Directory.CreateDirectory(Path.GetDirectoryName(originalProfilePath)!);
            File.WriteAllBytes(originalProfilePath, originalProfileBytes);
            files =
            [
                new
                {
                    RelativePath = "bcs-server-modules.json",
                    OriginalExisted = true,
                    OriginalSha256 = Convert.ToHexString(SHA256.HashData(originalProfileBytes)),
                    AppliedSha256 = Convert.ToHexString(SHA256.HashData(appliedProfileBytes))
                }
            ];
        }

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
                Files = files
            }));
        return manifest;
    }

    private static byte[] BridgeConfiguration(
        string identitySeed,
        IReadOnlyList<(string Id, string Version)> dependencies,
        BridgeBattleSceneCatalogContract? battleSceneCatalog = null)
    {
        var builder = new StringBuilder();
        builder.Append(battleSceneCatalog is null
            ? "BCS-COOP-BRIDGE|2\n"
            : "BCS-COOP-BRIDGE|3\n");
        builder.Append("RUNTIME_FEATURE|").Append(Encode(identitySeed)).Append('\n');
        if (battleSceneCatalog is not null)
        {
            builder.Append("BATTLE_SCENE_CATALOG_CONTRACT|")
                .Append(Encode(battleSceneCatalog.TargetModuleId)).Append('|')
                .Append(Encode(battleSceneCatalog.TargetVersion)).Append('|')
                .Append(Encode(battleSceneCatalog.BaseModuleId)).Append('|')
                .Append(Encode(battleSceneCatalog.BaseVersion)).Append('|')
                .Append(Encode(battleSceneCatalog.RelativePath)).Append('|')
                .Append(battleSceneCatalog.Sha256).Append("|WARN_ONLY\n");
        }
        foreach (var dependency in dependencies)
        {
            builder.Append("MODULE|")
                .Append(Encode(dependency.Id)).Append('|')
                .Append(Encode(dependency.Version)).Append("||\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteBridgeModule(
        string moduleRoot,
        string id,
        byte[] configuration,
        IReadOnlyList<(string Id, string Version)> dependencies,
        BridgeBattleSceneCatalogContract? battleSceneCatalog = null)
    {
        var dependencyXml = string.Concat(dependencies.Select(dependency =>
            $"<DependedModule Id=\"{dependency.Id}\" " +
            $"DependentVersion=\"{dependency.Version}\" Optional=\"false\" />"));
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            "<Module>" +
            $"<Name value=\"{id}\" />" +
            $"<Id value=\"{id}\" />" +
            $"<Version value=\"{CoopBridgePackageBuilder.BridgeVersion}\" />" +
            "<SingleplayerModule value=\"true\" />" +
            "<MultiplayerModule value=\"false\" />" +
            "<DependedModules>" + dependencyXml + "</DependedModules>" +
            "<ModuleType value=\"Community\" />" +
            "<SubModules><SubModule>" +
            "<Name value=\"BCS Coop Bridge\" />" +
            "<DLLName value=\"BCS.CoopBridge.dll\" />" +
            "<SubModuleClassType value=\"BCS.CoopBridge.BridgeSubModule\" />" +
            "</SubModule></SubModules>" +
            "</Module>");
        File.WriteAllBytes(
            Path.Combine(moduleRoot, "bcs-coop-bridge.config"),
            configuration);
        if (battleSceneCatalog is not null)
        {
            var catalogPath = Path.Combine(
                moduleRoot,
                battleSceneCatalog.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            File.WriteAllBytes(catalogPath, battleSceneCatalog.Content);
        }
        var serverBin = Path.Combine(
            moduleRoot,
            "bin",
            "Win64_Shipping_Server");
        var clientBin = Path.Combine(
            moduleRoot,
            "bin",
            "Win64_Shipping_Client");
        Directory.CreateDirectory(serverBin);
        Directory.CreateDirectory(clientBin);
        WriteEmbeddedBridgeAssembly(
            Path.Combine(serverBin, "BCS.CoopBridge.dll"),
            "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Server.dll");
        WriteEmbeddedBridgeAssembly(
            Path.Combine(clientBin, "BCS.CoopBridge.dll"),
            "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Client.dll");
    }

    private static void WriteEmbeddedBridgeAssembly(string path, string resourceName)
    {
        using var source = typeof(CoopBridgePackageBuilder).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "Embedded bridge regression runtime is missing: " + resourceName);
        using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        source.CopyTo(destination);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static void WriteDependencyModule(
        string moduleRoot,
        string id,
        string version)
    {
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(
            Path.Combine(moduleRoot, "SubModule.xml"),
            "<Module>" +
            $"<Name value=\"{id}\" />" +
            $"<Id value=\"{id}\" />" +
            $"<Version value=\"{version}\" />" +
            "<DependedModules />" +
            "<SubModules />" +
            "</Module>");
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

    private static void CreateDirectoryJunction(string path, string target)
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
            "Could not start mklink for bridge installation regression.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not create bridge installation regression junction: " + output + error);
        }
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static BannerlordModule Module(
        string id,
        string? path = null,
        bool enabled = false,
        IReadOnlyList<string>? dependencies = null)
    {
        var module = new BannerlordModule
        {
            Name = id,
            Id = id,
            Version = "v1.0.0",
            Path = path ?? Path.Combine(Path.GetTempPath(), id),
            IsInstalled = true,
            IsRequired = false,
            IsServerCompatible = true,
            Dependencies = dependencies ?? Array.Empty<string>(),
            MustLoadAfter = Array.Empty<string>(),
            MustLoadBefore = Array.Empty<string>(),
            IncompatibleModules = Array.Empty<string>()
        };
        module.SetInitialEnabled(enabled);
        return module;
    }

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

    private sealed class DeletingRecycler : IModuleDirectoryRecycler
    {
        public int Count { get; private set; }

        public void Recycle(string directoryPath)
        {
            Count++;
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
