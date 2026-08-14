using System.IO;
using System.Text.Json;
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

    private readonly ModuleScanner _moduleScanner;
    private readonly CoopCompatibilityPatcher _compatibilityPatcher;
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
            BridgeInstallationResult>? installOrUpdate)
    {
        _moduleScanner = moduleScanner;
        _compatibilityPatcher = compatibilityPatcher;
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
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(installedModules);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);

        if (!IsKnownRecipe(module))
        {
            throw new InvalidOperationException(
                $"No generated bridge recipe is available for module '{module.Id}'.");
        }

        var plan = _compatibilityPatcher.CreatePlan(
            module,
            installedModules,
            serverRoot);
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

        return _compatibilityPatcher.Revert(canonicalManifest);
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
        return new BackupScope(isKnownBridge);
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

    private sealed record BackupScope(bool IsKnownBridge);
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
