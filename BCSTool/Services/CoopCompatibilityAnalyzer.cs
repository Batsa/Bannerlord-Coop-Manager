using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Performs a bounded, read-only inspection of one installed Bannerlord
/// module. It never loads mod assemblies, executes mod code, edits manifests,
/// or claims that static inspection is runtime compatibility proof.
/// </summary>
public sealed class CoopCompatibilityAnalyzer
{
    private const int MaximumFiles = 50_000;
    private const int MaximumContentIds = 250_000;
    private const int MaximumExamplesPerFinding = 6;
    private const long MaximumXmlFileBytes = 16L * 1024 * 1024;
    private const long MaximumTotalXmlBytes = 256L * 1024 * 1024;
    private const long MaximumXmlCharacters = 32L * 1024 * 1024;

    private static readonly HashSet<string> DlcNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "NavalDLC",
            "BirthAndDeath"
        };

    private static readonly HashSet<string> NonServerOfficialDependencies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CustomBattle",
            "StoryMode",
            "BirthAndDeath"
        };

    private static readonly string[] CampaignBehaviorMarkers =
        ["CampaignBehavior", "CampaignBehaviour"];

    private static readonly string[] MissionBehaviorMarkers =
        ["MissionBehavior", "MissionBehaviour"];

    public CoopCompatibilityReport Analyze(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> currentModules,
        string serverRoot)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(currentModules);

        if (!module.IsInstalled || string.IsNullOrWhiteSpace(module.Path))
            throw new InvalidOperationException("Select an installed module to analyze.");

        var modulePath = Path.GetFullPath(module.Path);
        if (!Directory.Exists(modulePath))
            throw new DirectoryNotFoundException($"Module folder was not found: {modulePath}");

        var expectedModulesDirectory = Path.GetFullPath(
            Path.Combine(serverRoot, "engine", "Modules"));
        var moduleParent = Directory.GetParent(modulePath)?.FullName;
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(moduleParent ?? string.Empty),
                Path.TrimEndingDirectorySeparator(expectedModulesDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Compatibility analysis is limited to direct children of the configured " +
                $"server Modules directory: {expectedModulesDirectory}");
        }

        var findings = new List<CoopCompatibilityFinding>();
        var inventory = BuildInventory(modulePath, findings);
        AnalyzeManifest(module, currentModules, inventory, findings);
        AnalyzeAssemblies(inventory, findings);
        AnalyzeXmlContent(inventory, findings);

        var coopModule = currentModules.FirstOrDefault(candidate =>
            candidate.IsInstalled &&
            candidate.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase));
        var nativeModule = currentModules.FirstOrDefault(candidate =>
            candidate.IsInstalled &&
            candidate.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));

        var coopFingerprint = "UNKNOWN";
        if (coopModule is null || string.IsNullOrWhiteSpace(coopModule.Path))
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Unknown,
                "Baseline",
                "Installed Coop release could not be fingerprinted.",
                "No installed module with ID 'Coop' was available in the current server scan.",
                "Install/rescan the released Coop server before relying on this report.");
        }
        else
        {
            coopFingerprint = BuildAnalysisFingerprint(coopModule.Path, findings, "Coop baseline");
        }

        Add(
            findings,
            CompatibilityFindingSeverity.Unknown,
            "Client parity",
            "Client module parity was not tested.",
            "This milestone analyzes the dedicated-server copy only; no client artifact was supplied.",
            "Install the same non-official module IDs, versions, and bridge build on every client before a join test.");

        var moduleFingerprint =
            BuildAnalysisFingerprint(modulePath, findings, "Selected module");
        var distinctFindings = findings.Distinct().ToArray();
        var status = DetermineStatus(distinctFindings, inventory);
        return new CoopCompatibilityReport
        {
            ModuleName = module.Name,
            ModuleId = module.Id,
            ModuleVersion = module.Version,
            ModulePath = modulePath,
            ModuleFingerprint = moduleFingerprint,
            GameVersion = nativeModule?.Version ?? string.Empty,
            CoopVersion = coopModule?.Version ?? string.Empty,
            CoopModulePath = coopModule?.Path ?? string.Empty,
            CoopFingerprint = coopFingerprint,
            GeneratedUtc = DateTime.UtcNow,
            OverallStatus = status,
            Findings = distinctFindings,
            FileCount = inventory.Files.Count,
            TotalSizeBytes = inventory.TotalSizeBytes,
            ManagedAssemblyCount = inventory.ManagedAssemblyCount,
            NativeAssemblyCount = inventory.NativeAssemblyCount,
            XmlFileCount = inventory.XmlFiles.Count
        };
    }

    private static ModuleInventory BuildInventory(
        string modulePath,
        ICollection<CoopCompatibilityFinding> findings)
    {
        var inventory = new ModuleInventory(modulePath);
        var pending = new Stack<string>();
        pending.Push(modulePath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Unknown,
                    "Files",
                    "Part of the module could not be inspected.",
                    $"{Relative(modulePath, directory)}: {exception.Message}",
                    "Grant read access and run the analysis again.");
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    Add(
                        findings,
                        CompatibilityFindingSeverity.Unknown,
                        "Files",
                        "A file-system entry could not be inspected.",
                        $"{Relative(modulePath, entry)}: {exception.Message}",
                        "Grant read access and run the analysis again.");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Add(
                        findings,
                        CompatibilityFindingSeverity.Warning,
                        "Files",
                        "Linked content was skipped.",
                        Relative(modulePath, entry),
                        "Replace linked content with ordinary files before packaging or compatibility testing.");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (inventory.Files.Count >= MaximumFiles)
                {
                    Add(
                        findings,
                        CompatibilityFindingSeverity.Unknown,
                        "Limits",
                        "File scan stopped at its safety limit.",
                        $"The module contains more than {MaximumFiles:N0} files.",
                        "Treat unscanned content as UNKNOWN or split the module into a smaller analysis workspace.");
                    return inventory;
                }

                long length;
                try
                {
                    length = new FileInfo(entry).Length;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    length = 0;
                    Add(
                        findings,
                        CompatibilityFindingSeverity.Unknown,
                        "Files",
                        "A file size could not be read.",
                        $"{Relative(modulePath, entry)}: {exception.Message}",
                        "Grant read access and run the analysis again.");
                }

                var file = new ModuleFile(entry, Relative(modulePath, entry), length);
                inventory.Files.Add(file);
                inventory.TotalSizeBytes += Math.Max(0, length);

                var extension = Path.GetExtension(entry);
                if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    inventory.Assemblies.Add(file);
                else if (
                    extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".xslt", StringComparison.OrdinalIgnoreCase))
                {
                    inventory.XmlFiles.Add(file);
                }
            }
        }

        return inventory;
    }

    private static void AnalyzeManifest(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> currentModules,
        ModuleInventory inventory,
        ICollection<CoopCompatibilityFinding> findings)
    {
        var manifest = inventory.Files.FirstOrDefault(file =>
            file.RelativePath.Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Blocker,
                "Manifest",
                "SubModule.xml is missing.",
                module.Path,
                "Restore a valid module manifest before enabling this module.");
            return;
        }

        if (!module.IsServerCompatible)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Blocker,
                "Server policy",
                "This module is excluded from the dedicated-server profile.",
                $"Module ID: {module.Id}",
                "Do not enable this client/official module on the server.");
        }

        var byId = currentModules.ToDictionary(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var dependencyId in module.Dependencies)
        {
            if (NonServerOfficialDependencies.Contains(dependencyId))
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Information,
                    "Dependencies",
                    $"Client-only dependency '{dependencyId}' will not be enabled on the dedicated server.",
                    "BCS preparation removes this dependency from the server-side manifest only.",
                    "Keep the dependency installed on clients; review the preparation preview before applying.");
                continue;
            }

            if (!byId.TryGetValue(dependencyId, out var dependency) || !dependency.IsInstalled)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Blocker,
                    "Dependencies",
                    $"Required dependency '{dependencyId}' is missing.",
                    "SubModule.xml declares a non-optional dependency that is absent from the server scan.",
                    "Install the exact dependency build, rescan, then analyze again.");
                continue;
            }

            if (!dependency.IsServerCompatible)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.HighRisk,
                    "Dependencies",
                    $"Dependency '{dependencyId}' is excluded from the server profile.",
                    $"Installed version: {dependency.Version}",
                    "Verify that the installed Coop release intentionally filters this dependency; otherwise a bridge or manifest split is required.");
            }
            else if (module.Enabled && !dependency.Enabled)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Blocker,
                    "Dependencies",
                    $"Dependency '{dependencyId}' is currently disabled.",
                    "The selected module is ON in the current BCS profile.",
                    "Enable and correctly order the dependency before saving the profile.");
            }
        }

        foreach (var message in module.ValidationMessages)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Blocker,
                "Current profile",
                message,
                $"Current BCS load order for {module.Id}",
                "Correct the dependency/load-order problem before runtime testing.");
        }

        XmlDocument document;
        try
        {
            document = LoadXml(manifest.FullPath, Math.Min(MaximumXmlCharacters, manifest.Length * 4 + 4096));
        }
        catch (Exception exception) when (
            exception is XmlException or IOException or UnauthorizedAccessException)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Blocker,
                "Manifest",
                "SubModule.xml could not be parsed safely.",
                exception.Message,
                "Repair the manifest before enabling the module.");
            return;
        }

        var declaredDlls = document.SelectNodes("//*[local-name()='SubModule']/*[local-name()='DLLName']")
            ?.OfType<XmlElement>()
            .Select(ValueAttribute)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        var availableDlls = inventory.Assemblies
            .Select(file => Path.GetFileName(file.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dllName in declaredDlls.Where(dll => !availableDlls.Contains(Path.GetFileName(dll))))
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Blocker,
                "Manifest",
                $"Declared assembly '{dllName}' is missing.",
                "SubModule.xml references a DLL that was not found anywhere in the module folder.",
                "Restore the matching DLL or use a complete build of the mod.");
        }

        var tags = document.SelectNodes("//*[local-name()='Tag']")
            ?.OfType<XmlElement>()
            .Select(tag =>
                $"{Attribute(tag, "key", "Key")}={Attribute(tag, "value", "Value")}")
            .Where(tag => tag != "=")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray()
            ?? Array.Empty<string>();
        if (tags.Length > 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Information,
                "Manifest tags",
                "Submodule execution tags were preserved for review.",
                string.Join(", ", tags),
                "Do not force client-only tags open; verify each submodule against the released dedicated server.");
        }

        foreach (var dependencyElement in document
                     .SelectNodes(
                         "//*[local-name()='DependedModule' or local-name()='DependedModuleMetadata']")
                     ?.OfType<XmlElement>()
                     ?? Enumerable.Empty<XmlElement>())
        {
            var dependencyId = Attribute(dependencyElement, "Id", "id");
            var declaredVersion = Attribute(
                dependencyElement,
                "DependentVersion",
                "Version");
            if (
                string.IsNullOrWhiteSpace(dependencyId) ||
                string.IsNullOrWhiteSpace(declaredVersion) ||
                !byId.TryGetValue(dependencyId, out var installedDependency) ||
                !installedDependency.IsInstalled ||
                VersionConstraintMatches(declaredVersion, installedDependency.Version))
            {
                continue;
            }

            Add(
                findings,
                CompatibilityFindingSeverity.Warning,
                "Dependency versions",
                $"Declared and installed versions differ for '{dependencyId}'.",
                $"Declared: {declaredVersion}; installed: {installedDependency.Version}",
                "Verify Bannerlord's version-range semantics and test this exact dependency build before enabling the module.");
        }

        Add(
            findings,
            CompatibilityFindingSeverity.Pass,
            "Manifest",
            "Manifest parsed without executing module code.",
            $"{module.Id} {module.Version}; declared DLLs: {declaredDlls.Length}",
            "Continue with exact-version runtime testing after resolving all blockers and high-risk findings.");
    }

    private static bool VersionConstraintMatches(string declared, string installed)
    {
        if (string.Equals(declared, installed, StringComparison.OrdinalIgnoreCase))
            return true;

        var declaredValue = declared.Trim().TrimStart('v', 'V');
        var installedValue = installed.Trim().TrimStart('v', 'V');
        if (!declaredValue.EndsWith(".*", StringComparison.Ordinal))
            return false;
        var prefix = declaredValue[..^1];
        return installedValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void AnalyzeAssemblies(
        ModuleInventory inventory,
        ICollection<CoopCompatibilityFinding> findings)
    {
        var assemblyReferences = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var campaignBehaviors = new List<string>();
        var missionBehaviors = new List<string>();
        var saveTypes = new List<string>();
        var modelTypes = new List<string>();
        var tickMethods = new List<string>();

        foreach (var assembly in inventory.Assemblies)
        {
            try
            {
                using var stream = new FileStream(
                    assembly.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!peReader.HasMetadata)
                {
                    inventory.NativeAssemblyCount++;
                    continue;
                }

                var reader = peReader.GetMetadataReader();
                inventory.ManagedAssemblyCount++;

                foreach (var referenceHandle in reader.AssemblyReferences)
                {
                    var reference = reader.GetAssemblyReference(referenceHandle);
                    var name = reader.GetString(reference.Name);
                    if (!assemblyReferences.TryGetValue(name, out var files))
                    {
                        files = new List<string>();
                        assemblyReferences.Add(name, files);
                    }

                    files.Add(assembly.RelativePath);
                }

                foreach (var typeHandle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(typeHandle);
                    var name = FullTypeName(reader, type);
                    if (name.EndsWith(".<Module>", StringComparison.Ordinal) || name == "<Module>")
                        continue;

                    var baseType = EntityTypeName(reader, type.BaseType);
                    var evidence = $"{assembly.RelativePath}: {name}";
                    if (ContainsAny(name, baseType, CampaignBehaviorMarkers))
                        AddExample(campaignBehaviors, evidence);
                    if (ContainsAny(name, baseType, MissionBehaviorMarkers))
                        AddExample(missionBehaviors, evidence);
                    if (ContainsAny(name, baseType, ["SaveableTypeDefiner", "SaveDefiner"]))
                        AddExample(saveTypes, evidence);
                    if (ContainsAny(name, baseType, ["GameModel", "ModelBase"]))
                        AddExample(modelTypes, evidence);

                    foreach (var methodHandle in type.GetMethods())
                    {
                        var method = reader.GetMethodDefinition(methodHandle);
                        var methodName = reader.GetString(method.Name);
                        if (IsTickMethod(methodName))
                            AddExample(tickMethods, $"{evidence}.{methodName}");
                    }
                }
            }
            catch (Exception exception) when (
                exception is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                inventory.NativeAssemblyCount++;
                Add(
                    findings,
                    CompatibilityFindingSeverity.Unknown,
                    "Assemblies",
                    "A DLL could not be inspected as managed .NET metadata.",
                    $"{assembly.RelativePath}: {exception.Message}",
                    "Treat native, protected, mixed-mode, or damaged code as UNKNOWN and test it separately.");
            }
        }

        var harmonyReferences = assemblyReferences
            .Where(pair => pair.Key.Contains("Harmony", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(file => $"{file} -> {pair.Key}"))
            .Take(MaximumExamplesPerFinding)
            .ToArray();
        if (harmonyReferences.Length > 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.HighRisk,
                "Harmony",
                "Runtime patching dependency detected.",
                string.Join(Environment.NewLine, harmonyReferences),
                "Review every gameplay patch for server authority, deterministic execution, and equivalent client code.");
        }

        var dlcReferences = assemblyReferences
            .Where(pair => DlcNames.Contains(pair.Key) || pair.Key.EndsWith("DLC", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(file => $"{file} -> {pair.Key}"))
            .Take(MaximumExamplesPerFinding)
            .ToArray();
        if (dlcReferences.Length > 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.HighRisk,
                "DLC",
                "A managed assembly directly references a DLC assembly.",
                string.Join(Environment.NewLine, dlcReferences),
                "Treat this as a go/no-go test against the exact released Coop build before writing a bridge.");
        }

        AddRiskGroup(
            findings,
            campaignBehaviors,
            "Campaign synchronization",
            "Custom campaign behavior types detected.",
            "Audit state mutations, authority, late-join snapshots, reconnects, and save synchronization.");
        AddRiskGroup(
            findings,
            missionBehaviors,
            "Mission synchronization",
            "Custom mission behavior types detected.",
            "Audit battle/mission creation, agent state, results, and client/server lifecycle synchronization.");
        AddRiskGroup(
            findings,
            saveTypes,
            "Save compatibility",
            "Custom save type definitions detected.",
            "Verify stable type IDs and identical bridge/mod builds on server and clients before loading an important save.");
        AddRiskGroup(
            findings,
            modelTypes,
            "Game models",
            "Custom game-model types detected.",
            "Compare model results on server and client and move authoritative mutations behind Coop-owned flows.");

        if (tickMethods.Count > 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Warning,
                "Optimization",
                "Potential per-tick or periodic hot paths detected.",
                string.Join(Environment.NewLine, tickMethods),
                "Capture CPU/tick timing in a controlled runtime profile; method names alone are not performance proof.");
        }

        if (inventory.ManagedAssemblyCount > 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Unknown,
                "Runtime proof",
                "Executable managed code requires real Coop testing.",
                $"Managed assemblies inspected: {inventory.ManagedAssemblyCount}",
                "Test server load, client join, new game, save, reconnect, battle, and restart with exact artifact hashes.");
        }
        else if (inventory.NativeAssemblyCount == 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Information,
                "Code",
                "No executable DLL was found in this module.",
                "The module appears content-only from its file inventory.",
                "Content-only does not guarantee compatibility; complete load/join/save testing remains required.");
        }
    }

    private static void AnalyzeXmlContent(
        ModuleInventory inventory,
        ICollection<CoopCompatibilityFinding> findings)
    {
        var roots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicateExamples = new List<string>();
        var duplicateCount = 0;
        var totalBytes = 0L;
        var idLimitReached = false;

        foreach (var file in inventory.XmlFiles)
        {
            if (file.Length > MaximumXmlFileBytes)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Unknown,
                    "XML limits",
                    "A large XML file was skipped.",
                    $"{file.RelativePath}: {file.Length:N0} bytes",
                    "Inspect this file manually or split the analysis artifact.");
                continue;
            }

            if (totalBytes + file.Length > MaximumTotalXmlBytes)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Unknown,
                    "XML limits",
                    "XML inspection stopped at its total-size safety limit.",
                    $"Limit: {MaximumTotalXmlBytes:N0} bytes",
                    "Treat remaining XML content as UNKNOWN.");
                break;
            }

            totalBytes += Math.Max(0, file.Length);
            try
            {
                var settings = SafeXmlSettings(Math.Min(MaximumXmlCharacters, file.Length * 4 + 4096));
                using var reader = XmlReader.Create(file.FullPath, settings);
                var rootRecorded = false;
                var inspectIds = Path.GetExtension(file.FullPath)
                    .Equals(".xml", StringComparison.OrdinalIgnoreCase);

                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;

                    if (!rootRecorded)
                    {
                        roots[reader.LocalName] = roots.GetValueOrDefault(reader.LocalName) + 1;
                        rootRecorded = true;
                    }

                    if (!inspectIds || idLimitReached)
                        continue;

                    var id = Attribute(reader, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    if (firstIds.Count >= MaximumContentIds)
                    {
                        idLimitReached = true;
                        continue;
                    }

                    var key = reader.LocalName + "\0" + id;
                    var evidence = $"{reader.LocalName} id='{id}' in {file.RelativePath}";
                    if (firstIds.TryGetValue(key, out var first))
                    {
                        duplicateCount++;
                        AddExample(duplicateExamples, $"{first} / {evidence}");
                    }
                    else
                    {
                        firstIds.Add(key, evidence);
                    }
                }
            }
            catch (Exception exception) when (
                exception is XmlException or IOException or UnauthorizedAccessException)
            {
                Add(
                    findings,
                    CompatibilityFindingSeverity.Warning,
                    "XML",
                    "An XML/XSLT file could not be parsed safely.",
                    $"{file.RelativePath}: {exception.Message}",
                    "Confirm Bannerlord can load this exact file and repair malformed or externally dependent XML.");
            }
        }

        if (idLimitReached)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Unknown,
                "XML limits",
                "Content-ID comparison reached its safety limit.",
                $"Unique IDs inspected: {MaximumContentIds:N0}",
                "Treat remaining duplicate-ID coverage as UNKNOWN.");
        }

        if (duplicateCount > 0)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Warning,
                "XML IDs",
                $"Potential duplicate content IDs detected ({duplicateCount:N0}).",
                string.Join(Environment.NewLine, duplicateExamples),
                "Review duplicates in context; Bannerlord merge/patch XML can intentionally repeat IDs.");
        }

        if (roots.Count > 0)
        {
            var summary = string.Join(
                ", ",
                roots.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(pair => $"{pair.Key} ({pair.Value})"));
            Add(
                findings,
                CompatibilityFindingSeverity.Information,
                "Content inventory",
                "XML/XSLT roots were inventoried.",
                summary,
                "Use this inventory to plan feature-specific load/join/save tests; it does not prove synchronization.");
        }
    }

    private static CoopCompatibilityStatus DetermineStatus(
        IEnumerable<CoopCompatibilityFinding> findings,
        ModuleInventory inventory)
    {
        var severities = findings.Select(finding => finding.Severity).ToHashSet();
        if (severities.Contains(CompatibilityFindingSeverity.Blocker))
            return CoopCompatibilityStatus.Blocked;
        if (severities.Contains(CompatibilityFindingSeverity.HighRisk))
            return CoopCompatibilityStatus.BridgeLikelyRequired;
        if (
            inventory.ManagedAssemblyCount > 0 ||
            inventory.NativeAssemblyCount > 0 ||
            severities.Contains(CompatibilityFindingSeverity.Warning) ||
            findings.Any(finding =>
                finding.Severity == CompatibilityFindingSeverity.Unknown &&
                !finding.Area.Equals("Client parity", StringComparison.OrdinalIgnoreCase)))
        {
            return CoopCompatibilityStatus.TestingRequired;
        }

        return CoopCompatibilityStatus.LikelyCompatible;
    }

    private static string BuildAnalysisFingerprint(
        string modulePath,
        ICollection<CoopCompatibilityFinding> findings,
        string label)
    {
        try
        {
            var files = EnumerateFingerprintFiles(modulePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];

            foreach (var file in files)
            {
                var relative = Relative(modulePath, file).Replace('\\', '/');
                hash.AppendData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
                hash.AppendData([0]);

                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }

            return files.Length == 0
                ? "EMPTY"
                : Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Add(
                findings,
                CompatibilityFindingSeverity.Unknown,
                "Fingerprint",
                $"{label} fingerprint could not be completed.",
                exception.Message,
                "Close programs modifying these files, confirm read access, and analyze again.");
            return "UNKNOWN";
        }
    }

    private static IEnumerable<string> EnumerateFingerprintFiles(string modulePath)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(modulePath));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                var extension = Path.GetExtension(entry);
                if (
                    Path.GetFileName(entry).Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".xslt", StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    private static XmlDocument LoadXml(string path, long maxCharacters)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(path, SafeXmlSettings(maxCharacters));
        document.Load(reader);
        return document;
    }

    private static XmlReaderSettings SafeXmlSettings(long maxCharacters) =>
        new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = Math.Max(4096, maxCharacters),
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

    private static string ValueAttribute(XmlElement element) =>
        Attribute(element, "value", "Value");

    private static string Attribute(XmlElement element, params string[] names)
    {
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (names.Any(name => attribute.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return attribute.Value.Trim();
        }

        return string.Empty;
    }

    private static string Attribute(XmlReader reader, string name)
    {
        if (!reader.HasAttributes)
            return string.Empty;

        while (reader.MoveToNextAttribute())
        {
            if (reader.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = reader.Value.Trim();
                reader.MoveToElement();
                return value;
            }
        }

        reader.MoveToElement();
        return string.Empty;
    }

    private static string FullTypeName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string EntityTypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil)
            return string.Empty;

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => FullTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
            _ => handle.Kind.ToString()
        };
    }

    private static string FullTypeName(MetadataReader reader, TypeReference type)
    {
        var name = reader.GetString(type.Name);
        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static bool ContainsAny(
        string typeName,
        string baseTypeName,
        IEnumerable<string> markers) =>
        markers.Any(marker =>
            typeName.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            baseTypeName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsTickMethod(string name) =>
        name.Equals("Tick", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OnTick", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OnApplicationTick", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OnMissionTick", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HourlyTick", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DailyTick", StringComparison.OrdinalIgnoreCase);

    private static void AddRiskGroup(
        ICollection<CoopCompatibilityFinding> findings,
        IReadOnlyCollection<string> examples,
        string area,
        string finding,
        string recommendation)
    {
        if (examples.Count == 0)
            return;

        Add(
            findings,
            CompatibilityFindingSeverity.HighRisk,
            area,
            finding,
            string.Join(Environment.NewLine, examples),
            recommendation);
    }

    private static void AddExample(ICollection<string> examples, string evidence)
    {
        if (examples.Count < MaximumExamplesPerFinding)
            examples.Add(evidence);
    }

    private static void Add(
        ICollection<CoopCompatibilityFinding> findings,
        CompatibilityFindingSeverity severity,
        string area,
        string finding,
        string evidence,
        string recommendation)
    {
        findings.Add(new CoopCompatibilityFinding(
            severity,
            area,
            finding,
            evidence,
            recommendation));
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? Path.GetFileName(root) : relative;
    }

    private sealed class ModuleInventory(string rootPath)
    {
        public string RootPath { get; } = rootPath;
        public List<ModuleFile> Files { get; } = new();
        public List<ModuleFile> Assemblies { get; } = new();
        public List<ModuleFile> XmlFiles { get; } = new();
        public long TotalSizeBytes { get; set; }
        public int ManagedAssemblyCount { get; set; }
        public int NativeAssemblyCount { get; set; }
    }

    private sealed record ModuleFile(string FullPath, string RelativePath, long Length);
}
