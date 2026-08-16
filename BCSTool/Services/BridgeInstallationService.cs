using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Owns the user-facing bridge installation lifecycle. Compatibility planning
/// remains an internal implementation detail: a known module recipe is
/// installed or repaired as one backed-up operation.
/// </summary>
public sealed class BridgeInstallationService
{
    private const string BackupManifestFileName = "bcs-compatibility-backup.json";
    private const long MaximumBackupManifestBytes = 4 * 1024 * 1024;
    private const long MaximumBridgeAssemblyBytes = 64 * 1024 * 1024;

    private readonly ModuleScanner _moduleScanner;
    private readonly CoopCompatibilityPatcher _compatibilityPatcher;
    private readonly BridgeDllSelectionService _bridgeDllSelectionService;
    private readonly Func<
        BannerlordModule,
        IReadOnlyList<BannerlordModule>,
        string,
        BridgeInstallationResult> _installOrUpdate;

    public BridgeInstallationService(
        ModuleScanner moduleScanner,
        CoopCompatibilityPatcher compatibilityPatcher)
        : this(moduleScanner, compatibilityPatcher, installOrUpdate: null)
    {
    }

    internal BridgeInstallationService(
        ModuleScanner moduleScanner,
        CoopCompatibilityPatcher compatibilityPatcher,
        Func<
            BannerlordModule,
            IReadOnlyList<BannerlordModule>,
            string,
            BridgeInstallationResult>? installOrUpdate,
        BridgeDllSelectionService? bridgeDllSelectionService = null)
    {
        _moduleScanner = moduleScanner;
        _compatibilityPatcher = compatibilityPatcher;
        _bridgeDllSelectionService = bridgeDllSelectionService ?? new BridgeDllSelectionService();
        _installOrUpdate = installOrUpdate ?? InstallOrUpdate;
    }

    public bool IsKnownRecipe(BannerlordModule? module) =>
        module is { IsInstalled: true } &&
        CompatibilityRecipeRegistry.FindByRootModule(module.Id) is not null;

    public string? GetRecipeDisplayName(BannerlordModule? module) =>
        module is null
            ? null
            : CompatibilityRecipeRegistry.FindByRootModule(module.Id)?.DisplayName;

    public BridgeInstallationResult InstallOrUpdate(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> installedModules,
        string serverRoot)
    {
        var selection = ResolveDllSelection(module, installedModules, serverRoot);
        return InstallOrUpdate(module, installedModules, serverRoot, selection);
    }

    public BridgeInstallationResult InstallOrUpdate(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> installedModules,
        string serverRoot,
        BridgeDllSelection bridgeDllSelection)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(installedModules);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        ArgumentNullException.ThrowIfNull(bridgeDllSelection);

        if (!IsKnownRecipe(module))
        {
            throw new InvalidOperationException(
                $"No generated bridge recipe is available for module '{module.Id}'.");
        }

        var validatedSelection = _bridgeDllSelectionService.ValidateCurrentManifest(
            module,
            bridgeDllSelection);
        var plan = _compatibilityPatcher.CreatePlan(
            module,
            installedModules,
            serverRoot,
            validatedSelection);
        if (plan.Blockers.Count > 0)
        {
            throw new InvalidDataException(
                "Bridge installation is blocked:" + Environment.NewLine +
                string.Join(Environment.NewLine, plan.Blockers.Select(blocker => "- " + blocker)));
        }

        var clientPackage = FindClientPackage(plan, serverRoot);
        if (plan.Changes.Count == 0)
        {
            return new BridgeInstallationResult(
                RecipeDetected: true,
                ChangesApplied: false,
                ModuleIds: plan.ModuleIds,
                ClientPackagePath: clientPackage,
                BackupDirectory: null);
        }

        var applied = _compatibilityPatcher.Apply(plan);
        return new BridgeInstallationResult(
            RecipeDetected: true,
            ChangesApplied: true,
            ModuleIds: applied.ModuleIds,
            ClientPackagePath: clientPackage,
            BackupDirectory: applied.BackupDirectory);
    }

    public BridgeDllSelection ResolveDllSelection(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> installedModules,
        string serverRoot) =>
        _bridgeDllSelectionService.Resolve(module, installedModules, serverRoot);

    public BridgeDllSelection ValidateCurrentDllSelection(
        BannerlordModule module,
        BridgeDllSelection selection) =>
        _bridgeDllSelectionService.ValidateCurrentManifest(module, selection);

    /// <summary>
    /// Repairs bridge-owned files before process creation when a supported
    /// overhaul recipe is active. Disabled or absent recipes are ignored.
    /// </summary>
    public BridgeInstallationResult RepairForStart(string serverExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExecutablePath);

        var canonicalExecutable = Path.GetFullPath(serverExecutablePath);
        var serverRoot = Path.GetDirectoryName(canonicalExecutable)
                         ?? throw new InvalidDataException(
                             "Dedicated-server executable has no parent directory.");
        var moduleManager = new ModuleManager(canonicalExecutable, _moduleScanner);
        var modules = moduleManager.Load();
        var activeRecipes = modules
            .Where(module => module.Enabled && IsKnownRecipe(module))
            .ToArray();
        if (activeRecipes.Length == 0)
            return BridgeInstallationResult.NotRequired;
        if (activeRecipes.Length > 1)
        {
            throw new InvalidDataException(
                "Only one bridge-managed overhaul can be active at a time: " +
                string.Join(", ", activeRecipes.Select(module => module.Id)));
        }

        return _installOrUpdate(activeRecipes[0], modules, serverRoot);
    }

    public string? FindLatestInstallationBackup(string serverRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);

        var canonicalServerRoot = Path.GetFullPath(serverRoot);
        var backupsRoot = Path.Combine(
            canonicalServerRoot,
            "bcs-compatibility-backups");
        if (!Directory.Exists(backupsRoot))
            return null;

        string? latest = null;
        var latestWrite = DateTime.MinValue;
        foreach (var planDirectory in Directory.EnumerateDirectories(
                     backupsRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(planDirectory) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(Path.Combine(planDirectory, "REVERTED.txt")))
            {
                continue;
            }

            var manifestPath = Path.Combine(planDirectory, BackupManifestFileName);
            if (!File.Exists(manifestPath) ||
                !ReadBackupScope(manifestPath, canonicalServerRoot).IsKnownBridge)
            {
                continue;
            }

            var write = File.GetLastWriteTimeUtc(manifestPath);
            if (latest is null || write > latestWrite)
            {
                latest = manifestPath;
                latestWrite = write;
            }
        }

        return latest;
    }

    internal string? FindRollbackRequiredGeneratedBridge(string serverRoot)
    {
        var manifestPath = FindLatestInstallationBackup(serverRoot);
        if (manifestPath is null)
            return null;

        return ReadRollbackRequiredGeneratedBridge(manifestPath, serverRoot);
    }

    private static string? ReadRollbackRequiredGeneratedBridge(
        string manifestPath,
        string serverRoot)
    {
        var profileDeclaration = ReadRollbackProfileDeclaration(manifestPath);
        if (profileDeclaration is null)
            return null;

        var currentProfilePath = Path.Combine(
            Path.GetFullPath(serverRoot),
            "bcs-server-modules.json");
        ValidateRegularProfileFile(currentProfilePath, "current module profile");
        if (!FileSha256(currentProfilePath).Equals(
                profileDeclaration.AppliedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The current module profile no longer matches the latest bridge rollback backup.");
        }

        if (!profileDeclaration.OriginalExisted)
            return null;

        var backupDirectory = Path.GetDirectoryName(manifestPath)
                              ?? throw new InvalidDataException(
                                  "Bridge installation backup has no parent directory.");
        var filesDirectory = Path.Combine(backupDirectory, "files");
        var profilePath = Path.Combine(
            filesDirectory,
            "bcs-server-modules.json");
        if (!Directory.Exists(filesDirectory) ||
            (File.GetAttributes(filesDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Bridge rollback files directory is missing or linked: " + filesDirectory);
        }

        ValidateRegularProfileFile(profilePath, "original module profile");
        if (!FileSha256(profilePath).Equals(
                profileDeclaration.OriginalSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Bridge rollback original module profile hash does not match its manifest.");
        }

        using var stream = new FileStream(
            profilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Bridge rollback module profile must be a JSON object.");
        EnsureNoDuplicateProperties(root);
        if (RequiredInt32(root, "SchemaVersion") != 1)
            throw new InvalidDataException("Unsupported bridge rollback module profile schema.");

        var modules = RequiredProperty(root, "Modules");
        if (modules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Bridge rollback module profile Modules must be an array.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledGeneratedBridges = new List<string>();
        foreach (var entry in modules.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Bridge rollback module profile contains a non-object entry.");
            }

            EnsureNoDuplicateProperties(entry);
            var id = RequiredString(entry, "Id");
            if (!ids.Add(id))
            {
                throw new InvalidDataException(
                    $"Bridge rollback module profile repeats module ID: {id}");
            }

            if (RequiredBoolean(entry, "Enabled") &&
                id.StartsWith(
                    CoopBridgePackageBuilder.BridgeIdPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                enabledGeneratedBridges.Add(id);
            }
        }

        if (enabledGeneratedBridges.Count > 1)
        {
            throw new InvalidDataException(
                "Bridge rollback module profile enables multiple generated bridges.");
        }

        return enabledGeneratedBridges.SingleOrDefault();
    }

    private static RollbackProfileDeclaration? ReadRollbackProfileDeclaration(
        string manifestPath)
    {
        using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Bridge installation backup manifest must be a JSON object.");
        EnsureNoDuplicateProperties(root);

        var files = RequiredProperty(root, "Files");
        if (files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Bridge installation backup Files must be an array.");

        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RollbackProfileDeclaration? profile = null;
        foreach (var entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Bridge installation backup contains a non-object file entry.");

            EnsureNoDuplicateProperties(entry);
            var relativePath = RequiredString(entry, "RelativePath");
            if (!relativePaths.Add(relativePath))
                throw new InvalidDataException(
                    "Bridge installation backup repeats file path: " + relativePath);

            var originalExisted = RequiredBoolean(entry, "OriginalExisted");
            var originalSha256 = RequiredHash(entry, "OriginalSha256", allowEmpty: !originalExisted);
            var appliedSha256 = RequiredHash(entry, "AppliedSha256", allowEmpty: false);
            if (!relativePath.Equals(
                    "bcs-server-modules.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            profile = new RollbackProfileDeclaration(
                originalExisted,
                originalSha256,
                appliedSha256);
        }

        return profile;
    }

    private static void ValidateRegularProfileFile(string path, string description)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Bridge rollback {description} is missing: {path}");

        var info = new FileInfo(path);
        if (info.Length <= 0 ||
            info.Length > MaximumBackupManifestBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Bridge rollback {description} is empty, too large, or linked: {path}");
        }
    }

    private static string RequiredHash(
        JsonElement root,
        string name,
        bool allowEmpty)
    {
        var value = RequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Bridge installation backup '{name}' must be a string.");

        var hash = value.GetString()!;
        if (allowEmpty && hash.Length == 0)
            return hash;
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"Bridge installation backup '{name}' must be a SHA-256 hash.");
        }

        return hash;
    }

    private static string FileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public CoopPreparationResult RevertInstallation(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var canonicalManifest = Path.GetFullPath(manifestPath);
        var scope = ReadBackupScope(canonicalManifest, expectedServerRoot: null);
        if (!scope.IsKnownBridge)
        {
            throw new InvalidDataException(
                "The selected backup is not a recognized bridge installation.");
        }

        var rollbackBridge = ReadRollbackRequiredGeneratedBridge(
            canonicalManifest,
            scope.ServerRoot);
        if (rollbackBridge is not null)
            ValidateGeneratedBridgeForActivation(scope.ServerRoot, rollbackBridge);

        return _compatibilityPatcher.Revert(canonicalManifest);
    }

    internal void ValidateGeneratedBridgeForActivation(
        string serverRoot,
        string bridgeId)
    {
        var modulesDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(serverRoot, "engine", "Modules")));
        var bridgeDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(modulesDirectory, bridgeId)));
        var bridgeParent = Directory.GetParent(bridgeDirectory)?.FullName;
        if (bridgeParent is null ||
            !Path.TrimEndingDirectorySeparator(bridgeParent).Equals(
                modulesDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(bridgeDirectory).Equals(
                bridgeId,
                StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(bridgeDirectory) ||
            (File.GetAttributes(bridgeDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Cannot revert because the restored active generated bridge folder is " +
                "missing, linked, or outside the dedicated-server Modules directory: " +
                bridgeId);
        }

        var manifestPath = Path.Combine(bridgeDirectory, "SubModule.xml");
        var configurationPath = Path.Combine(
            bridgeDirectory,
            "bcs-coop-bridge.config");
        var binDirectory = Path.Combine(bridgeDirectory, "bin");
        var serverRoleDirectory = Path.Combine(
            binDirectory,
            "Win64_Shipping_Server");
        var serverAssemblyPath = Path.Combine(
            serverRoleDirectory,
            "BCS.CoopBridge.dll");
        var clientRoleDirectory = Path.Combine(
            binDirectory,
            "Win64_Shipping_Client");
        var clientAssemblyPath = Path.Combine(
            clientRoleDirectory,
            "BCS.CoopBridge.dll");
        ValidateRegularBridgeDirectory(binDirectory, "bridge bin directory");
        ValidateRegularBridgeDirectory(serverRoleDirectory, "bridge server role directory");
        ValidateRegularBridgeDirectory(clientRoleDirectory, "bridge client role directory");
        ValidateRegularBridgeFile(
            manifestPath,
            MaximumBackupManifestBytes,
            "bridge module manifest");
        ValidateRegularBridgeFile(
            configurationPath,
            MaximumBackupManifestBytes,
            "bridge configuration");
        ValidateRegularBridgeFile(
            serverAssemblyPath,
            MaximumBridgeAssemblyBytes,
            "bridge server assembly");
        ValidateRegularBridgeFile(
            clientAssemblyPath,
            MaximumBridgeAssemblyBytes,
            "bridge client assembly");
        var serverRuntimeVersion = ValidateManagedBridgeAssembly(serverAssemblyPath, "server");
        var clientRuntimeVersion = ValidateManagedBridgeAssembly(clientAssemblyPath, "client");
        if (serverRuntimeVersion != clientRuntimeVersion)
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge server and client runtime versions " +
                "do not match.");
        }
        ValidateGeneratedBridgeManifest(
            manifestPath,
            bridgeId,
            serverRuntimeVersion,
            configuredModuleVersions: null);

        var installedById = _moduleScanner.Scan(modulesDirectory)
            .ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        if (!installedById.TryGetValue(bridgeId, out var scannedBridge) ||
            !Path.GetFullPath(scannedBridge.Path).Equals(
                bridgeDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge manifest identity does not match " +
                "its exact module directory.");
        }

        var configurationBytes = File.ReadAllBytes(configurationPath);
        var installedConfiguration =
            BridgeDllSelectionService.ValidateInstalledConfiguration(
                configurationBytes,
                installedById);
        ValidateGeneratedBridgeManifest(
            manifestPath,
            bridgeId,
            serverRuntimeVersion,
            installedConfiguration.ModuleVersions);
        var catalogContract = installedConfiguration.BattleSceneCatalogContract;
        byte[]? catalogBytes = null;
        if (catalogContract is { } contract)
        {
            var catalogPath = Path.Combine(
                bridgeDirectory,
                contract.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            ValidateRegularBridgeFileWithin(
                catalogPath,
                bridgeDirectory,
                MaximumBackupManifestBytes,
                "bridge battle-scene catalog contract");
            catalogBytes = File.ReadAllBytes(catalogPath);
            var catalogHash = Convert.ToHexString(SHA256.HashData(catalogBytes));
            if (!catalogHash.Equals(contract.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Cannot revert because the restored bridge battle-scene catalog contract " +
                    "does not match its configured hash.");
            }
        }

        using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identityHash.AppendData(configurationBytes);
        if (catalogBytes is not null)
            identityHash.AppendData(catalogBytes);
        identityHash.AppendData(File.ReadAllBytes(serverAssemblyPath));
        identityHash.AppendData(File.ReadAllBytes(clientAssemblyPath));
        var expectedBridgeId =
            CoopBridgePackageBuilder.BridgeIdPrefix +
            Convert.ToHexString(identityHash.GetHashAndReset())[..24].ToLowerInvariant();
        if (!expectedBridgeId.Equals(bridgeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge configuration and runtimes do not " +
                "match its generated module ID.");
        }
    }

    private static void ValidateRegularBridgeDirectory(string path, string description)
    {
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Cannot revert because the restored {description} is missing or linked: {path}");
        }
    }

    private static void ValidateRegularBridgeFile(
        string path,
        long maximumBytes,
        string description)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Cannot revert because the restored {description} is missing: {path}");

        var info = new FileInfo(path);
        if (info.Length <= 0 ||
            info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Cannot revert because the restored {description} is empty, too large, or linked: {path}");
        }
    }

    private static void ValidateRegularBridgeFileWithin(
        string path,
        string bridgeDirectory,
        long maximumBytes,
        string description)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bridgeDirectory));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cannot revert because the restored {description} is outside its bridge folder: {fullPath}");
        }

        ValidateRegularBridgeFile(fullPath, maximumBytes, description);
        for (var current = Directory.GetParent(fullPath);
             current is not null;
             current = current.Parent)
        {
            var currentPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(current.FullName));
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Cannot revert because the restored {description} uses a linked directory: " +
                    currentPath);
            }

            if (currentPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidDataException(
            $"Cannot revert because the restored {description} escaped its bridge folder: {fullPath}");
    }

    private static void ValidateGeneratedBridgeManifest(
        string manifestPath,
        string bridgeId,
        Version runtimeVersion,
        IReadOnlyDictionary<string, string>? configuredModuleVersions)
    {
        var document = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(
                manifestPath,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumBackupManifestBytes
                });
            document.Load(reader);
        }
        catch (Exception exception) when (
            exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge manifest could not be read: " +
                exception.Message,
                exception);
        }

        var root = document.DocumentElement;
        var idElements = DirectChildren(root, "Id").ToArray();
        var versionElements = DirectChildren(root, "Version").ToArray();
        var singleplayerElements = DirectChildren(root, "SingleplayerModule").ToArray();
        var multiplayerElements = DirectChildren(root, "MultiplayerModule").ToArray();
        var moduleTypeElements = DirectChildren(root, "ModuleType").ToArray();
        var dependencyContainers = DirectChildren(root, "DependedModules").ToArray();
        var dependencies = dependencyContainers.Length == 1
            ? DirectChildren(dependencyContainers[0], "DependedModule").ToArray()
            : [];
        var subModuleContainers = DirectChildren(root, "SubModules").ToArray();
        var subModules = subModuleContainers.Length == 1
            ? DirectChildren(subModuleContainers[0], "SubModule").ToArray()
            : [];
        var dllNames = subModules.Length == 1
            ? DirectChildren(subModules[0], "DLLName").ToArray()
            : [];
        var classTypes = subModules.Length == 1
            ? DirectChildren(subModules[0], "SubModuleClassType").ToArray()
            : [];
        if (root is null ||
            !root.LocalName.Equals("Module", StringComparison.OrdinalIgnoreCase) ||
            idElements.Length != 1 ||
            !ElementValue(idElements[0]).Equals(bridgeId, StringComparison.OrdinalIgnoreCase) ||
            versionElements.Length != 1 ||
            singleplayerElements.Length != 1 ||
            !ElementValue(singleplayerElements[0]).Equals("true", StringComparison.OrdinalIgnoreCase) ||
            multiplayerElements.Length != 1 ||
            !ElementValue(multiplayerElements[0]).Equals("false", StringComparison.OrdinalIgnoreCase) ||
            moduleTypeElements.Length != 1 ||
            !ElementValue(moduleTypeElements[0]).Equals("Community", StringComparison.OrdinalIgnoreCase) ||
            subModuleContainers.Length != 1 ||
            subModules.Length != 1 ||
            dllNames.Length != 1 ||
            !ElementValue(dllNames[0]).Equals(
                "BCS.CoopBridge.dll",
                StringComparison.OrdinalIgnoreCase) ||
            classTypes.Length != 1 ||
            !ElementValue(classTypes[0]).Equals(
                "BCS.CoopBridge.BridgeSubModule",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge manifest does not declare the exact " +
                $"generated bridge identity, load-role flags, and runtime: {bridgeId}");
        }

        var expectedVersion = "v" + runtimeVersion.Major + "." + runtimeVersion.Minor + "." +
                              runtimeVersion.Build;
        if (!ElementValue(versionElements[0]).Equals(
                expectedVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge manifest version does not match its " +
                $"managed runtimes: expected {expectedVersion}.");
        }

        if (configuredModuleVersions is null)
            return;

        if (dependencyContainers.Length != 1)
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge manifest does not contain exactly one " +
                "dependency declaration.");
        }

        var manifestDependencies = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            var dependencyId = ExactAttributeValue(dependency, "Id");
            var dependentVersion = ExactAttributeValue(
                dependency,
                "DependentVersion",
                allowMissing: true);
            var optional = ExactAttributeValue(dependency, "Optional");
            if (string.IsNullOrWhiteSpace(dependencyId) ||
                !optional.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                !manifestDependencies.TryAdd(dependencyId, dependentVersion))
            {
                throw new InvalidDataException(
                    "Cannot revert because the restored bridge manifest contains an invalid, " +
                    "optional, or duplicate dependency.");
            }
        }

        if (manifestDependencies.Count != configuredModuleVersions.Count ||
            configuredModuleVersions.Any(pair =>
                !manifestDependencies.TryGetValue(pair.Key, out var manifestVersion) ||
                !manifestVersion.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Cannot revert because the restored bridge manifest dependencies do not exactly " +
                "match its installed configuration module IDs and versions.");
        }
    }

    private static Version ValidateManagedBridgeAssembly(
        string assemblyPath,
        string role)
    {
        AssemblyName assemblyName;
        try
        {
            assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or FileLoadException or IOException or
                UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Cannot revert because the restored bridge {role} runtime is not a valid " +
                "managed BCS.CoopBridge assembly.",
                exception);
        }

        if (!string.Equals(
                assemblyName.Name,
                "BCS.CoopBridge",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cannot revert because the restored bridge {role} runtime has assembly " +
                $"identity '{assemblyName.Name}', not 'BCS.CoopBridge'.");
        }

        return assemblyName.Version ?? throw new InvalidDataException(
            $"Cannot revert because the restored bridge {role} runtime has no assembly version.");
    }

    private static IEnumerable<XmlElement> DirectChildren(
        XmlElement? parent,
        string localName) =>
        parent?.ChildNodes
            .OfType<XmlElement>()
            .Where(element => element.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase)) ?? [];

    private static string ElementValue(XmlElement element)
    {
        var values = element.Attributes
            .OfType<XmlAttribute>()
            .Where(attribute => attribute.LocalName.Equals(
                "value",
                StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value.Trim())
            .Take(2)
            .ToArray();
        return values.Length == 1 ? values[0] : string.Empty;
    }

    private static string ExactAttributeValue(
        XmlElement element,
        string localName,
        bool allowMissing = false)
    {
        var values = element.Attributes
            .OfType<XmlAttribute>()
            .Where(attribute => attribute.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value.Trim())
            .Take(2)
            .ToArray();
        if (values.Length == 0 && allowMissing)
            return string.Empty;
        if (values.Length != 1)
        {
            throw new InvalidDataException(
                $"Cannot revert because a restored bridge dependency has an invalid '{localName}' attribute.");
        }

        return values[0];
    }

    private static BackupScope ReadBackupScope(
        string manifestPath,
        string? expectedServerRoot)
    {
        var canonicalManifest = Path.GetFullPath(manifestPath);
        if (!Path.GetFileName(canonicalManifest).Equals(
                BackupManifestFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(canonicalManifest))
        {
            throw new FileNotFoundException(
                "Bridge installation backup manifest was not found.",
                canonicalManifest);
        }

        var manifestInfo = new FileInfo(canonicalManifest);
        if (manifestInfo.Length <= 0 || manifestInfo.Length > MaximumBackupManifestBytes ||
            (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Bridge installation backup manifest is empty, too large, or linked: " +
                canonicalManifest);
        }

        using var stream = new FileStream(
            canonicalManifest,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Bridge installation backup manifest must be a JSON object.");
        EnsureNoDuplicateProperties(root);

        var schema = RequiredInt32(root, "SchemaVersion");
        var ruleId = RequiredString(root, "RuleId");
        var declaredServerRoot = Path.GetFullPath(RequiredString(root, "ServerRoot"));
        var moduleIdsElement = RequiredProperty(root, "ModuleIds");
        if (moduleIdsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Bridge installation backup ModuleIds must be an array.");
        var moduleIds = moduleIdsElement.EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : throw new InvalidDataException(
                    "Bridge installation backup ModuleIds contains a non-string value."))
            .Select(value => !string.IsNullOrWhiteSpace(value)
                ? value!
                : throw new InvalidDataException(
                    "Bridge installation backup ModuleIds contains an empty value."))
            .ToArray();

        if (expectedServerRoot is not null &&
            !declaredServerRoot.Equals(
                Path.GetFullPath(expectedServerRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Bridge installation backup declares a different dedicated-server root.");
        }

        var backupsRoot = Path.Combine(declaredServerRoot, "bcs-compatibility-backups");
        var planDirectory = Directory.GetParent(canonicalManifest);
        if (planDirectory?.Parent is null ||
            !planDirectory.Parent.FullName.Equals(
                backupsRoot,
                StringComparison.OrdinalIgnoreCase) ||
            (planDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Bridge installation backup is not in a regular plan directory.");
        }

        var relative = Path.GetRelativePath(backupsRoot, canonicalManifest);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Bridge installation backup is outside its declared server backup directory.");
        }

        var distinctModuleIds = moduleIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchingRecipe = schema == 1
            ? CompatibilityRecipeRegistry.FindByRuleId(ruleId)
            : null;
        var isKnownBridge =
            matchingRecipe is not null &&
            moduleIds.Length == distinctModuleIds.Count &&
            distinctModuleIds.Count == matchingRecipe.PreparedModuleIds.Count + 1 &&
            matchingRecipe.PreparedModuleIds.All(distinctModuleIds.Contains) &&
            distinctModuleIds.Count(id => id.StartsWith(
                CoopBridgePackageBuilder.BridgeIdPrefix,
                StringComparison.OrdinalIgnoreCase)) == 1;
        return new BackupScope(isKnownBridge, declaredServerRoot);
    }

    private static void EnsureNoDuplicateProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException(
                    $"Bridge installation backup repeats JSON property '{property.Name}'.");
        }
    }

    private static JsonElement RequiredProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException(
                $"Bridge installation backup is missing '{name}'.");

    private static string RequiredString(JsonElement root, string name)
    {
        var value = RequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Bridge installation backup '{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static int RequiredInt32(JsonElement root, string name)
    {
        var value = RequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException(
                $"Bridge installation backup '{name}' must be an integer.");
        }

        return result;
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        var value = RequiredProperty(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"Bridge installation backup '{name}' must be a boolean.")
        };
    }

    private static string? FindClientPackage(
        CoopPreparationPlan plan,
        string serverRoot)
    {
        var planned = plan.Changes
            .Select(change => change.TargetPath)
            .FirstOrDefault(path =>
                path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("bcs-client-packages", StringComparison.OrdinalIgnoreCase));
        if (planned is not null)
            return planned;

        var bridgeId = plan.ModuleIds.LastOrDefault(id =>
            id.StartsWith(CoopBridgePackageBuilder.BridgeIdPrefix, StringComparison.OrdinalIgnoreCase));
        if (bridgeId is null)
            return null;

        var existing = Path.Combine(serverRoot, "bcs-client-packages", bridgeId + ".zip");
        return File.Exists(existing) ? existing : null;
    }

    private sealed record BackupScope(bool IsKnownBridge, string ServerRoot);

    private sealed record RollbackProfileDeclaration(
        bool OriginalExisted,
        string OriginalSha256,
        string AppliedSha256);
}

public sealed record BridgeInstallationResult(
    bool RecipeDetected,
    bool ChangesApplied,
    IReadOnlyList<string> ModuleIds,
    string? ClientPackagePath,
    string? BackupDirectory)
{
    public static BridgeInstallationResult NotRequired { get; } = new(
        RecipeDetected: false,
        ChangesApplied: false,
        ModuleIds: Array.Empty<string>(),
        ClientPackagePath: null,
        BackupDirectory: null);
}
