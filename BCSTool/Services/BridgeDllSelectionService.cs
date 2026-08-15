using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Reads declared submodule DLLs and restores the selection encoded by the
/// currently enabled generated bridge. This service never changes module files.
/// </summary>
public sealed class BridgeDllSelectionService
{
    private const long MaximumManifestBytes = 4 * 1024 * 1024;
    private const long MaximumManifestCharacters = 4 * 1024 * 1024;
    private const long MaximumConfigurationBytes = 4 * 1024 * 1024;
    private static readonly Version ExplicitSelectionBridgeVersion = new(0, 6, 68);
    private const string ConfigurationFileName = "bcs-coop-bridge.config";
    private const string ServerBridgeRelativePath =
        "bin/Win64_Shipping_Server/BCS.CoopBridge.dll";
    private const string ClientBridgeRelativePath =
        "bin/Win64_Shipping_Client/BCS.CoopBridge.dll";
    private static readonly UTF8Encoding StrictUtf8NoBom = new(false, true);

    /// <summary>
    /// Resolves current selection. With no enabled generated bridge, every DLL
    /// declaration is selected. Current packages use schema-2 disabled records;
    /// legacy packages preserve the server manifest's active/disabled tags.
    /// </summary>
    public BridgeDllSelection Resolve(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> installedModules,
        string serverRoot)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(installedModules);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);

        var canonicalServerRoot = Path.GetFullPath(serverRoot);
        var modulesRoot = Path.GetFullPath(
            Path.Combine(canonicalServerRoot, "engine", "Modules"));
        ValidateRegularDirectory(canonicalServerRoot, "Dedicated-server directory");
        ValidateRegularDirectory(modulesRoot, "Dedicated-server Modules directory");

        var installedById = CreateInstalledModuleIndex(installedModules);
        if (!installedById.TryGetValue(module.Id, out var installedModule) ||
            !installedModule.IsInstalled ||
            !installedModule.Version.Equals(module.Version, StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(installedModule.Path, module.Path))
        {
            throw new InvalidDataException(
                $"Selected module '{module.Id}' does not match the current dedicated-server module scan.");
        }

        var manifest = ReadManifest(module, modulesRoot);
        var generatedBridges = installedModules
            .Where(candidate =>
                candidate.IsInstalled &&
                candidate.Id.StartsWith(
                    CoopBridgePackageBuilder.BridgeIdPrefix,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var activeBridges = generatedBridges
            .Where(candidate => candidate.Enabled)
            .ToArray();
        if (activeBridges.Length == 0)
        {
            if (generatedBridges.Length == 0)
            {
                return new BridgeDllSelection(
                    manifest.Id,
                    manifest.Version,
                    manifest.DeclaredDllNames,
                    manifest.DeclaredDllNames);
            }
        }
        else if (activeBridges.Length != 1)
        {
            throw new InvalidDataException(
                "Exactly one generated BCS Coop bridge may be enabled while resolving DLL options.");
        }

        if (activeBridges.Length == 1)
        {
            return ResolveFromBridge(
                module,
                manifest,
                activeBridges[0],
                installedById,
                modulesRoot);
        }

        var inactiveSelections = generatedBridges
            .Select(bridge => new
            {
                Bridge = bridge,
                Manifest = ReadManifest(bridge, modulesRoot)
            })
            .Where(candidate => candidate.Manifest.Dependencies.Contains(
                module.Id,
                StringComparer.OrdinalIgnoreCase))
            .Select(candidate => ResolveFromBridge(
                module,
                manifest,
                candidate.Bridge,
                installedById,
                modulesRoot,
                candidate.Manifest))
            .ToArray();
        if (inactiveSelections.Length == 0)
        {
            return new BridgeDllSelection(
                manifest.Id,
                manifest.Version,
                manifest.DeclaredDllNames,
                manifest.DeclaredDllNames);
        }

        var matchingServerState = inactiveSelections
            .Where(selection => MatchesCurrentServerPolicy(manifest, selection))
            .ToArray();
        var candidates = matchingServerState.Length > 0
            ? matchingServerState
            : inactiveSelections;
        var first = candidates[0];
        if (candidates.Skip(1).Any(selection => !selection.SelectedDllNames.SequenceEqual(
                first.SelectedDllNames,
                StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Inactive generated bridges disagree on the DLL selection for '{module.Id}'. " +
                "Enable the intended bridge or reopen Bridge DLL Options and apply a new selection.");
        }
        return first;
    }

    private static BridgeDllSelection ResolveFromBridge(
        BannerlordModule module,
        ManifestSnapshot manifest,
        BannerlordModule bridge,
        IReadOnlyDictionary<string, BannerlordModule> installedById,
        string modulesRoot,
        ManifestSnapshot? knownBridgeManifest = null)
    {
        if (!TryGetGeneratedIdentity(bridge.Id, out var declaredIdentity))
        {
            throw new InvalidDataException(
                $"Generated bridge has an unsafe module ID: {bridge.Id}");
        }
        if (!bridge.IsInstalled || string.IsNullOrWhiteSpace(bridge.Path))
            throw new InvalidDataException("Generated BCS Coop bridge is not installed.");

        var bridgeManifest = knownBridgeManifest ?? ReadManifest(bridge, modulesRoot);
        if (!bridgeManifest.Dependencies.Contains(module.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Generated bridge '{bridge.Id}' targets a different overhaul.");
        }

        var bridgeRoot = bridgeManifest.RootPath;
        if (!Path.GetFileName(bridgeRoot).Equals(bridge.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Enabled generated bridge folder does not match its module ID.");
        }

        var configurationPath = Path.Combine(bridgeRoot, ConfigurationFileName);
        var serverAssemblyPath = Path.Combine(
            bridgeRoot,
            ServerBridgeRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var clientAssemblyPath = Path.Combine(
            bridgeRoot,
            ClientBridgeRelativePath.Replace('/', Path.DirectorySeparatorChar));
        ValidateRegularFile(
            configurationPath,
            bridgeRoot,
            MaximumConfigurationBytes,
            "Bridge configuration");

        var configurationBytes = File.ReadAllBytes(configurationPath);
        var usesExplicitDllSelection = UsesExplicitDllSelection(bridgeManifest.Version);
        string? installedIdentity = null;
        Exception? runtimeValidationFailure = null;
        try
        {
            ValidateRegularFile(
                serverAssemblyPath,
                bridgeRoot,
                long.MaxValue,
                "Server bridge assembly");
            ValidateRegularFile(
                clientAssemblyPath,
                bridgeRoot,
                long.MaxValue,
                "Client bridge assembly");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(configurationBytes);
            AppendFile(hash, serverAssemblyPath);
            AppendFile(hash, clientAssemblyPath);
            installedIdentity = Convert.ToHexString(hash.GetHashAndReset())[..24]
                .ToLowerInvariant();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            runtimeValidationFailure = exception;
        }

        if (!string.Equals(installedIdentity, declaredIdentity, StringComparison.Ordinal))
        {
            var recoverableCurrentRuntime =
                usesExplicitDllSelection &&
                bridgeManifest.Version.Equals(
                    CoopBridgePackageBuilder.BridgeVersion,
                    StringComparison.OrdinalIgnoreCase) &&
                CoopBridgePackageBuilder.ComputeBridgeId(configurationBytes).Equals(
                    bridge.Id,
                    StringComparison.Ordinal);
            var recoverableLegacyRuntime = !usesExplicitDllSelection;
            if (!recoverableCurrentRuntime && !recoverableLegacyRuntime)
            {
                throw new InvalidDataException(
                    "Enabled bridge configuration and runtimes do not match its generated module ID.",
                    runtimeValidationFailure);
            }
        }

        var parsed = ParseConfiguration(configurationBytes, installedById);
        if (!parsed.ModuleRecords.TryGetValue(module.Id, out var targetRecords))
        {
            throw new InvalidDataException(
                $"Enabled bridge configuration does not contain module '{module.Id}'.");
        }

        ValidateConfiguredDlls(
            module.Id,
            manifest.DeclaredDllNames,
            targetRecords.DllNames,
            "MODULE");

        parsed.DisabledDlls.TryGetValue(module.Id, out var disabledDlls);
        disabledDlls ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateConfiguredDlls(
            module.Id,
            manifest.DeclaredDllNames,
            disabledDlls,
            "DISABLED_SUBMODULE");

        IReadOnlyList<string> selectedDlls;
        if (usesExplicitDllSelection)
        {
            if (parsed.Schema != 2)
            {
                throw new InvalidDataException(
                    "Current generated bridge requires configuration schema 2 for DLL options.");
            }
            selectedDlls = manifest.DeclaredDllNames
                .Where(name => !disabledDlls.Contains(name))
                .ToArray();
            var selectedSet = selectedDlls.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!targetRecords.DllNames.SetEquals(selectedSet))
            {
                throw new InvalidDataException(
                    $"Enabled bridge MODULE and DISABLED_SUBMODULE records disagree for '{module.Id}'.");
            }
        }
        else
        {
            if (parsed.DisabledDlls.Any(pair => pair.Value.Count > 0))
            {
                throw new InvalidDataException(
                    "Legacy generated bridge contains unsupported DISABLED_SUBMODULE records.");
            }
            selectedDlls = manifest.DeclaredDllNames
                .Where(name =>
                    !manifest.ServerDisabledDllNames.Contains(
                        name,
                        StringComparer.OrdinalIgnoreCase) ||
                    IsKnownClientOnlySelection(module.Id, name))
                .ToArray();
        }

        return new BridgeDllSelection(
            manifest.Id,
            manifest.Version,
            manifest.DeclaredDllNames,
            selectedDlls);
    }

    private static bool MatchesCurrentServerPolicy(
        ManifestSnapshot manifest,
        BridgeDllSelection selection)
    {
        var expectedDisabled = selection.AvailableDllNames
            .Where(name => !selection.IsSelected(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        expectedDisabled.UnionWith(selection.SelectedDllNames.Where(name =>
            IsKnownClientOnlySelection(selection.ModuleId, name)));
        return expectedDisabled.SetEquals(manifest.ServerDisabledDllNames);
    }

    /// <summary>
    /// Creates a selection against the module's current manifest. Used for UI
    /// changes before planning a bridge installation.
    /// </summary>
    public BridgeDllSelection Create(
        BannerlordModule module,
        IEnumerable<string> selectedDllNames)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(selectedDllNames);

        var manifest = ReadManifest(module, expectedModulesRoot: null);
        return new BridgeDllSelection(
            manifest.Id,
            manifest.Version,
            manifest.DeclaredDllNames,
            selectedDllNames);
    }

    /// <summary>
    /// Re-reads the manifest and fails if its identity, version, declarations,
    /// or declaration order changed after selection.
    /// </summary>
    public BridgeDllSelection ValidateCurrentManifest(
        BannerlordModule module,
        BridgeDllSelection selection)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(selection);

        var manifest = ReadManifest(module, expectedModulesRoot: null);
        if (!manifest.Id.Equals(selection.ModuleId, StringComparison.OrdinalIgnoreCase) ||
            !manifest.Version.Equals(
                selection.ModuleVersion,
                StringComparison.OrdinalIgnoreCase) ||
            !manifest.DeclaredDllNames.SequenceEqual(
                selection.AvailableDllNames,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Module '{module.Id}' manifest changed after bridge DLL options were selected. Reload options.");
        }

        return new BridgeDllSelection(
            manifest.Id,
            manifest.Version,
            manifest.DeclaredDllNames,
            selection.SelectedDllNames);
    }

    private static IReadOnlyDictionary<string, BannerlordModule> CreateInstalledModuleIndex(
        IReadOnlyList<BannerlordModule> modules)
    {
        var result = new Dictionary<string, BannerlordModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Id))
                throw new InvalidDataException("Current module scan contains an empty module ID.");
            if (!result.TryAdd(module.Id, module))
                throw new InvalidDataException($"Current module scan repeats module ID: {module.Id}");
        }

        return result;
    }

    private static ManifestSnapshot ReadManifest(
        BannerlordModule module,
        string? expectedModulesRoot)
    {
        if (!module.IsInstalled || string.IsNullOrWhiteSpace(module.Path))
            throw new InvalidDataException($"Bridge DLL module is not installed: {module.Id}");

        var moduleRoot = Path.GetFullPath(module.Path);
        ValidateRegularDirectory(moduleRoot, $"Module '{module.Id}' directory");
        if (expectedModulesRoot is not null)
        {
            var parent = Directory.GetParent(moduleRoot);
            if (parent is null || !PathsEqual(parent.FullName, expectedModulesRoot))
            {
                throw new InvalidDataException(
                    $"Module '{module.Id}' is not a direct child of the dedicated-server Modules directory.");
            }
            ValidateDirectoryChain(moduleRoot, expectedModulesRoot);
        }

        var manifestPath = Path.Combine(moduleRoot, "SubModule.xml");
        ValidateRegularFile(
            manifestPath,
            moduleRoot,
            MaximumManifestBytes,
            $"Module '{module.Id}' manifest");

        var document = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestCharacters
            });
            document.Load(reader);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new InvalidDataException(
                $"Could not read module manifest '{manifestPath}': {exception.Message}",
                exception);
        }

        var root = document.DocumentElement;
        if (root is null || !root.LocalName.Equals("Module", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest does not contain a Module root: {manifestPath}");

        var id = ReadSingleChildValue(root, "Id", manifestPath, required: true);
        var version = ReadSingleChildValue(root, "Version", manifestPath, required: false);
        ValidateSafeModuleId(id, "manifest module ID");
        ValidateSafeText(version, "manifest module version", allowEmpty: true);
        if (!id.Equals(module.Id, StringComparison.OrdinalIgnoreCase) ||
            !version.Equals(module.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Module scan identity does not match manifest '{manifestPath}'.");
        }

        var subModuleContainers = DirectChildren(root, "SubModules").ToArray();
        if (subModuleContainers.Length > 1)
            throw new InvalidDataException($"Manifest repeats SubModules: {manifestPath}");

        var declaredDlls = new List<string>();
        var declaredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serverDisabledDlls = new List<string>();
        if (subModuleContainers.Length == 1)
        {
            foreach (var subModule in DirectChildren(subModuleContainers[0], "SubModule"))
            {
                foreach (var dllElement in DirectChildren(subModule, "DLLName"))
                {
                    var dllName = ReadSingleAttributeValue(
                        dllElement,
                        "value",
                        manifestPath,
                        required: true);
                    BridgeDllSelection.ValidateDllName(dllName, "declared");
                    if (!declaredSet.Add(dllName))
                    {
                        throw new InvalidDataException(
                            $"Manifest repeats DLLName '{dllName}' ignoring case: {manifestPath}");
                    }
                    declaredDlls.Add(dllName);
                    if (IsServerDisabledSubModule(subModule, manifestPath))
                        serverDisabledDlls.Add(dllName);
                }
            }
        }

        var dependencies = new List<string>();
        var dependencySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in DirectChildren(root, "DependedModules"))
        {
            foreach (var dependency in DirectChildren(container, "DependedModule"))
            {
                var dependencyId = ReadSingleAttributeValue(
                    dependency,
                    "Id",
                    manifestPath,
                    required: true);
                ValidateSafeModuleId(dependencyId, "manifest dependency module ID");
                if (!dependencySet.Add(dependencyId))
                {
                    throw new InvalidDataException(
                        $"Manifest repeats dependency '{dependencyId}': {manifestPath}");
                }
                dependencies.Add(dependencyId);
            }
        }

        return new ManifestSnapshot(
            id,
            version,
            moduleRoot,
            declaredDlls.ToArray(),
            serverDisabledDlls.ToArray(),
            dependencies.ToArray());
    }

    private static ParsedBridgeConfiguration ParseConfiguration(
        byte[] bytes,
        IReadOnlyDictionary<string, BannerlordModule> installedById)
    {
        string text;
        try
        {
            text = StrictUtf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Enabled bridge configuration is not valid UTF-8.",
                exception);
        }

        var lines = SplitConfigurationLines(text);
        var schema = lines[0] switch
        {
            "BCS-COOP-BRIDGE|1" => 1,
            "BCS-COOP-BRIDGE|2" => 2,
            _ => throw new InvalidDataException(
                "Unsupported BCS Coop bridge configuration schema.")
        };

        var moduleRecords = new Dictionary<string, ConfiguredModule>(
            StringComparer.OrdinalIgnoreCase);
        var disabledDlls = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        var seenLines = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines.Skip(1))
        {
            if (!seenLines.Add(line))
                throw new InvalidDataException("Enabled bridge configuration repeats a record.");

            var fields = line.Split('|');
            if (fields[0].Equals("MODULE", StringComparison.Ordinal))
            {
                ParseModuleRecord(fields, schema, installedById, moduleRecords);
                continue;
            }
            if (fields[0].Equals("DISABLED_SUBMODULE", StringComparison.Ordinal))
            {
                if (schema != 2)
                {
                    throw new InvalidDataException(
                        "DISABLED_SUBMODULE records require bridge configuration schema 2.");
                }
                ParseDisabledRecord(fields, installedById, disabledDlls);
                continue;
            }

            ValidateOtherRecord(fields, schema);
        }

        foreach (var pair in disabledDlls)
        {
            if (!moduleRecords.TryGetValue(pair.Key, out var configuredModule))
            {
                throw new InvalidDataException(
                    $"DISABLED_SUBMODULE targets module missing from MODULE records: {pair.Key}");
            }
            if (pair.Value.Overlaps(configuredModule.DllNames))
            {
                throw new InvalidDataException(
                    $"Bridge configuration both enables and disables a DLL for module '{pair.Key}'.");
            }
        }

        return new ParsedBridgeConfiguration(schema, moduleRecords, disabledDlls);
    }

    private static void ParseModuleRecord(
        string[] fields,
        int schema,
        IReadOnlyDictionary<string, BannerlordModule> installedById,
        IDictionary<string, ConfiguredModule> moduleRecords)
    {
        if (fields.Length != 5)
            throw new InvalidDataException("Malformed MODULE bridge record.");

        var moduleId = Decode(fields[1], "MODULE module ID", allowEmpty: false);
        var version = Decode(fields[2], "MODULE version", allowEmpty: true);
        var dllName = Decode(fields[3], "MODULE DLL name", allowEmpty: true);
        ValidateSafeModuleId(moduleId, "MODULE module ID");
        ValidateSafeText(version, "MODULE version", allowEmpty: true);
        ValidateOpaqueValue(fields[4], "MODULE hash");
        var clientOnly = schema == 2 && fields[4].Equals(
            CoopBridgePackageBuilder.ClientOnlyModuleMarker,
            StringComparison.Ordinal);
        if (schema == 2 && fields[4].Length > 0 && !clientOnly)
        {
            throw new InvalidDataException(
                $"Unsupported MODULE runtime-role marker: {fields[4]}");
        }
        if (dllName.Length > 0)
            BridgeDllSelection.ValidateDllName(dllName, "configured");
        else if (clientOnly)
            throw new InvalidDataException("Empty MODULE record cannot be client-only.");

        if (!installedById.TryGetValue(moduleId, out var installed) || !installed.IsInstalled)
        {
            throw new InvalidDataException(
                $"Enabled bridge MODULE record targets missing module: {moduleId}");
        }
        if (!installed.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Enabled bridge MODULE version mismatch for '{moduleId}'.");
        }

        if (!moduleRecords.TryGetValue(moduleId, out var configured))
        {
            configured = new ConfiguredModule(version);
            moduleRecords.Add(moduleId, configured);
        }
        else if (!configured.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Enabled bridge MODULE records disagree on version for '{moduleId}'.");
        }

        if (dllName.Length == 0)
        {
            if (configured.HasEmptyRecord || configured.DllNames.Count > 0)
                throw new InvalidDataException($"Duplicate or mixed empty MODULE record for '{moduleId}'.");
            configured.HasEmptyRecord = true;
        }
        else
        {
            if (configured.HasEmptyRecord || !configured.DllNames.Add(dllName))
                throw new InvalidDataException($"Duplicate or mixed MODULE DLL record: {moduleId}/{dllName}");
        }
    }

    private static void ParseDisabledRecord(
        string[] fields,
        IReadOnlyDictionary<string, BannerlordModule> installedById,
        IDictionary<string, HashSet<string>> disabledDlls)
    {
        if (fields.Length != 3)
            throw new InvalidDataException("Malformed DISABLED_SUBMODULE bridge record.");

        var moduleId = Decode(fields[1], "DISABLED_SUBMODULE module ID", allowEmpty: false);
        var dllName = Decode(fields[2], "DISABLED_SUBMODULE DLL name", allowEmpty: false);
        ValidateSafeModuleId(moduleId, "DISABLED_SUBMODULE module ID");
        BridgeDllSelection.ValidateDllName(dllName, "disabled configured");
        if (!installedById.TryGetValue(moduleId, out var installed) || !installed.IsInstalled)
        {
            throw new InvalidDataException(
                $"DISABLED_SUBMODULE targets missing module: {moduleId}");
        }

        if (!disabledDlls.TryGetValue(moduleId, out var names))
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            disabledDlls.Add(moduleId, names);
        }
        if (!names.Add(dllName))
            throw new InvalidDataException($"Duplicate DISABLED_SUBMODULE record: {moduleId}/{dllName}");
    }

    private static void ValidateOtherRecord(string[] fields, int schema)
    {
        switch (fields[0])
        {
            case "RUNTIME_FEATURE" when fields.Length == 2 && schema == 2:
                Decode(fields[1], "runtime feature", allowEmpty: false);
                return;
            case "GAME_VERSION_COMPAT" when fields.Length == 5:
                for (var index = 1; index < fields.Length; index++)
                    Decode(fields[index], "game version", allowEmpty: false);
                return;
            case "CLIENT_ASSEMBLY_RESOLVE" when fields.Length == 4:
                DecodeModuleId(fields[1], "client resolver module ID");
                ValidateRelativePath(
                    Decode(fields[2], "client resolver path", allowEmpty: false),
                    "client resolver path");
                ValidateOpaqueValue(fields[3], "client resolver hash");
                return;
            case "IGNORE_CONTENT" when fields.Length == 3:
                DecodeModuleId(fields[1], "ignored-content module ID");
                ValidateRelativePath(
                    Decode(fields[2], "ignored-content path", allowEmpty: false),
                    "ignored-content path");
                return;
            case "CONTENT" when fields.Length == 3:
                DecodeModuleId(fields[1], "content module ID");
                ValidateOpaqueValue(fields[2], "content hash");
                return;
            case "SERVER_FILE_REDIRECT" when fields.Length == 4:
                DecodeModuleId(fields[1], "server redirect module ID");
                ValidateRelativePath(
                    Decode(fields[2], "server redirect path", allowEmpty: false),
                    "server redirect path");
                ValidateOpaqueValue(fields[3], "server redirect hash");
                return;
            case "SERVER_XML_OVERLAY" when fields.Length == 6:
                DecodeModuleId(fields[1], "server overlay module ID");
                ValidateRelativePath(
                    Decode(fields[2], "server overlay source path", allowEmpty: false),
                    "server overlay source path");
                ValidateOpaqueValue(fields[3], "server overlay source hash");
                ValidateRelativePath(
                    Decode(fields[4], "server overlay target path", allowEmpty: false),
                    "server overlay target path");
                ValidateOpaqueValue(fields[5], "server overlay target hash");
                return;
            case "SERVER_MAP_TERRAIN_SIZE" when fields.Length == 10:
                DecodeModuleId(fields[1], "terrain-size module ID");
                ValidateRelativePath(
                    Decode(fields[2], "terrain-size path", allowEmpty: false),
                    "terrain-size path");
                ValidateOpaqueValue(fields[3], "terrain-size source hash");
                ValidatePositiveFiniteFloat(fields[4], "terrain width");
                ValidatePositiveFiniteFloat(fields[5], "terrain height");
                ValidateSafeText(
                    Decode(fields[6], "terrain loader assembly", allowEmpty: false),
                    "terrain loader assembly");
                ValidateOpaqueValue(fields[7], "terrain loader hash");
                ValidateSafeText(
                    Decode(fields[8], "terrain target assembly", allowEmpty: false),
                    "terrain target assembly");
                ValidateOpaqueValue(fields[9], "terrain target hash");
                return;
            case "AUTHORITY" when fields.Length is 6 or 7:
                DecodeModuleId(fields[1], "authority module ID");
                BridgeDllSelection.ValidateDllName(
                    Decode(fields[2], "authority DLL", allowEmpty: false),
                    "authority");
                ValidateSafeText(
                    Decode(fields[3], "authority type", allowEmpty: false),
                    "authority type");
                ValidateSafeText(
                    Decode(fields[4], "authority method", allowEmpty: false),
                    "authority method");
                if (!int.TryParse(
                        fields[5],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parameterCount) ||
                    parameterCount < 0)
                {
                    throw new InvalidDataException("Invalid authority parameter count.");
                }
                if (fields.Length == 7 && fields[6] is not (
                        "SERVER_ONLY" or "CLIENT_ONLY" or "SERVER_SETTINGS_FALLBACK"))
                {
                    throw new InvalidDataException("Invalid authority scope.");
                }
                return;
            default:
                throw new InvalidDataException("Malformed BCS Coop bridge record.");
        }
    }

    private static string DecodeModuleId(string encoded, string description)
    {
        var value = Decode(encoded, description, allowEmpty: false);
        ValidateSafeModuleId(value, description);
        return value;
    }

    private static string Decode(string value, string description, bool allowEmpty)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Invalid base64 value for {description} in bridge configuration.",
                exception);
        }

        if (!Convert.ToBase64String(bytes).Equals(value, StringComparison.Ordinal))
            throw new InvalidDataException($"Non-canonical base64 value for {description}.");

        string decoded;
        try
        {
            decoded = StrictUtf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Invalid UTF-8 value for {description} in bridge configuration.",
                exception);
        }

        ValidateSafeText(decoded, description, allowEmpty);
        return decoded;
    }

    private static void ValidateSafeModuleId(string value, string description)
    {
        ValidateSafeText(value, description, allowEmpty: false);
        if (!Path.GetFileName(value).Equals(value, StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"Unsafe {description}: '{value}'.");
        }
    }

    private static void ValidateSafeText(
        string value,
        string description,
        bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            (!string.IsNullOrEmpty(value) && !value.Equals(value.Trim(), StringComparison.Ordinal)) ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Empty or unsafe {description}.");
        }
    }

    private static void ValidateRelativePath(string value, string description)
    {
        ValidateSafeText(value, description, allowEmpty: false);
        var normalized = value.Replace('\\', '/');
        if (Path.IsPathRooted(value) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"Unsafe {description}: '{value}'.");
        }
    }

    private static void ValidateOpaqueValue(string value, string description)
    {
        if (value.Any(char.IsControl) || value.Contains('|'))
            throw new InvalidDataException($"Unsafe {description} in bridge configuration.");
    }

    private static void ValidatePositiveFiniteFloat(string value, string description)
    {
        if (!float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !float.IsFinite(parsed) ||
            parsed <= 0f ||
            parsed > 1_000_000f)
        {
            throw new InvalidDataException($"Invalid {description} in bridge configuration.");
        }
    }

    private static string[] SplitConfigurationLines(string text)
    {
        if (text.Length == 0)
            throw new InvalidDataException("Enabled bridge configuration is empty.");

        var rawLines = text.Split('\n');
        var count = rawLines.Length;
        if (count > 0 && rawLines[^1].Length == 0)
            count--;
        if (count == 0)
            throw new InvalidDataException("Enabled bridge configuration is empty.");

        var lines = new string[count];
        for (var index = 0; index < count; index++)
        {
            var line = rawLines[index];
            if (line.EndsWith('\r'))
                line = line[..^1];
            if (line.Length == 0 || line.Contains('\r') || line.Any(character =>
                    char.IsControl(character) && character is not '\t'))
            {
                throw new InvalidDataException("Enabled bridge configuration contains an invalid line.");
            }
            lines[index] = line;
        }

        return lines;
    }

    private static void ValidateConfiguredDlls(
        string moduleId,
        IReadOnlyList<string> declaredDllNames,
        IReadOnlySet<string> configuredDllNames,
        string recordType)
    {
        var declared = declaredDllNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = configuredDllNames.FirstOrDefault(name => !declared.Contains(name));
        if (unknown is not null)
        {
            throw new InvalidDataException(
                $"Enabled bridge {recordType} record is not declared by '{moduleId}': {unknown}");
        }
    }

    private static bool UsesExplicitDllSelection(string bridgeVersion)
    {
        if (bridgeVersion.Length < 2 ||
            bridgeVersion[0] is not ('v' or 'V') ||
            !Version.TryParse(bridgeVersion[1..], out var parsedVersion))
        {
            throw new InvalidDataException(
                $"Generated bridge has an unsupported version: '{bridgeVersion}'.");
        }

        return parsedVersion >= ExplicitSelectionBridgeVersion;
    }

    private static bool IsKnownClientOnlySelection(string moduleId, string dllName) =>
        moduleId.Equals("Europe1700", StringComparison.OrdinalIgnoreCase) &&
        CoopCompatibilityPatcher.Europe1700ClientOnlyDlls.Contains(
            dllName,
            StringComparer.OrdinalIgnoreCase);

    private static bool IsServerDisabledSubModule(
        XmlElement subModule,
        string manifestPath)
    {
        var tagsContainers = DirectChildren(subModule, "Tags").ToArray();
        if (tagsContainers.Length > 1)
            throw new InvalidDataException($"Manifest submodule repeats Tags: {manifestPath}");
        if (tagsContainers.Length == 0)
            return false;

        string? dedicatedServerType = null;
        string? isNoRenderModeElement = null;
        foreach (var tag in DirectChildren(tagsContainers[0], "Tag"))
        {
            var key = ReadSingleAttributeValue(
                tag,
                "key",
                manifestPath,
                required: false);
            if (key.Equals("DedicatedServerType", StringComparison.OrdinalIgnoreCase))
            {
                if (dedicatedServerType is not null)
                {
                    throw new InvalidDataException(
                        $"Manifest submodule repeats DedicatedServerType tag: {manifestPath}");
                }
                dedicatedServerType = ReadSingleAttributeValue(
                    tag,
                    "value",
                    manifestPath,
                    required: true);
            }
            else if (key.Equals("IsNoRenderModeElement", StringComparison.OrdinalIgnoreCase))
            {
                if (isNoRenderModeElement is not null)
                {
                    throw new InvalidDataException(
                        $"Manifest submodule repeats IsNoRenderModeElement tag: {manifestPath}");
                }
                isNoRenderModeElement = ReadSingleAttributeValue(
                    tag,
                    "value",
                    manifestPath,
                    required: true);
            }
        }

        return dedicatedServerType?.Equals("none", StringComparison.OrdinalIgnoreCase) == true &&
               isNoRenderModeElement?.Equals("false", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ReadSingleChildValue(
        XmlElement parent,
        string childName,
        string manifestPath,
        bool required)
    {
        var children = DirectChildren(parent, childName).ToArray();
        if (children.Length > 1)
            throw new InvalidDataException($"Manifest repeats {childName}: {manifestPath}");
        if (children.Length == 0)
        {
            if (required)
                throw new InvalidDataException($"Manifest has no {childName}: {manifestPath}");
            return string.Empty;
        }

        return ReadSingleAttributeValue(
            children[0],
            "value",
            manifestPath,
            required);
    }

    private static string ReadSingleAttributeValue(
        XmlElement element,
        string attributeName,
        string manifestPath,
        bool required)
    {
        var attributes = element.Attributes
            .OfType<XmlAttribute>()
            .Where(attribute => attribute.LocalName.Equals(
                attributeName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (attributes.Length > 1)
        {
            throw new InvalidDataException(
                $"Manifest repeats attribute '{attributeName}': {manifestPath}");
        }
        if (attributes.Length == 0)
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"Manifest value '{element.LocalName}' has no '{attributeName}' attribute: {manifestPath}");
            }
            return string.Empty;
        }

        var value = attributes[0].Value;
        if (!value.Equals(value.Trim(), StringComparison.Ordinal) ||
            (required && string.IsNullOrWhiteSpace(value)))
        {
            throw new InvalidDataException(
                $"Manifest value '{element.LocalName}' is empty or padded: {manifestPath}");
        }
        return value;
    }

    private static IEnumerable<XmlElement> DirectChildren(
        XmlElement parent,
        string localName) =>
        parent.ChildNodes
            .OfType<XmlElement>()
            .Where(child => child.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetGeneratedIdentity(string moduleId, out string identity)
    {
        identity = string.Empty;
        if (!moduleId.StartsWith(
                CoopBridgePackageBuilder.BridgeIdPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = moduleId[CoopBridgePackageBuilder.BridgeIdPrefix.Length..];
        if (suffix.Length != 24 || suffix.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        identity = suffix;
        return true;
    }

    private static void ValidateRegularDirectory(string directory, string description)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"{description} was not found: {directory}");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} cannot be linked: {directory}");
    }

    private static void ValidateDirectoryChain(string directory, string expectedRoot)
    {
        var root = Path.GetFullPath(expectedRoot);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Linked or missing module directories are not supported: {current.FullName}");
            }
            if (PathsEqual(current.FullName, root))
                return;
        }

        throw new InvalidDataException("Module directory escaped the dedicated-server Modules directory.");
    }

    private static void ValidateRegularFile(
        string path,
        string expectedRoot,
        long maximumBytes,
        string description)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(expectedRoot);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{description} is outside its expected directory: {fullPath}");
        }
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{description} is missing.", fullPath);

        var info = new FileInfo(fullPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length <= 0 ||
            info.Length > maximumBytes)
        {
            throw new InvalidDataException($"{description} is empty, oversized, or linked: {fullPath}");
        }

        for (var current = info.Directory; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{description} uses a linked directory: {current.FullName}");
            if (PathsEqual(current.FullName, root))
                return;
        }

        throw new InvalidDataException($"{description} escaped its expected directory.");
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private sealed record ManifestSnapshot(
        string Id,
        string Version,
        string RootPath,
        IReadOnlyList<string> DeclaredDllNames,
        IReadOnlyList<string> ServerDisabledDllNames,
        IReadOnlyList<string> Dependencies);

    private sealed class ConfiguredModule
    {
        internal ConfiguredModule(string version)
        {
            Version = version;
        }

        internal string Version { get; }
        internal HashSet<string> DllNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal bool HasEmptyRecord { get; set; }
    }

    private sealed record ParsedBridgeConfiguration(
        int Schema,
        IReadOnlyDictionary<string, ConfiguredModule> ModuleRecords,
        IReadOnlyDictionary<string, HashSet<string>> DisabledDlls);
}
