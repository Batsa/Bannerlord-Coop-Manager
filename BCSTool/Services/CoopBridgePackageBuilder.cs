using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Builds a deterministic, mod-agnostic bridge module. The bridge does not
/// claim to invent synchronization semantics for arbitrary mod code; it records
/// module identities and exposes one public Coop handler-discovery seam.
/// </summary>
public sealed class CoopBridgePackageBuilder
{
    public const string BridgeIdPrefix = "BCS.CoopBridge.";
    public const string BridgeVersion = "v0.6.73";
    internal const string ClientOnlyModuleMarker = "CLIENT_ONLY";
    private const string ProjectUrl =
        "https://github.com/Batsa/Bannerlord-Coop-Manager";

    private const string ServerBridgeAssemblyResource =
        "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Server.dll";
    private const string ServerBridgeAssemblyHash =
        "EBF01400B85F9E13D9D86D37628035CD2B5A802C4DB174AA3ADF730B2B7CBCB2";
    private const string ClientBridgeAssemblyResource =
        "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Client.dll";
    private const string LicenseResource = "BCSTool.LICENSE";
    private const string NoticeResource = "BCSTool.NOTICE.md";
    private const string ClientBridgeAssemblyHash =
        "97DF251987239E2BFB2280F925AD64602C6D5080A080AE9F02E71202EEFE2D59";
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    public CoopBridgePackage Build(
        IReadOnlyList<BannerlordModule> modules,
        IReadOnlyCollection<string>? projectedModuleIds = null,
        IReadOnlyList<BridgeAuthorityRule>? authorityRules = null,
        IReadOnlyList<BridgeContentExclusion>? contentExclusions = null,
        IReadOnlyCollection<string>? plannedContentPaths = null,
        IReadOnlyList<BridgeServerFileRedirect>? serverFileRedirects = null,
        IReadOnlyList<BridgeServerXmlOverlay>? serverXmlOverlays = null,
        BridgeGameVersionCompatibility? gameVersionCompatibility = null,
        IReadOnlyList<BridgeClientAssemblyResolve>? clientAssemblyResolves = null,
        IReadOnlyList<BridgeServerMapTerrainSize>? serverMapTerrainSizes = null,
        IReadOnlyList<BridgeRuntimeFeature>? runtimeFeatures = null,
        IReadOnlyList<BridgeDisabledSubModule>? disabledSubModules = null,
        IReadOnlyList<BridgeClientOnlySubModule>? clientOnlySubModules = null,
        BridgeBattleSceneCatalogContract? battleSceneCatalogContract = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count == 0)
            throw new ArgumentException("At least one module is required.", nameof(modules));

        var duplicate = modules
            .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Duplicate bridge module ID: {duplicate.Key}");

        contentExclusions ??= Array.Empty<BridgeContentExclusion>();
        ValidateContentExclusions(contentExclusions, modules, plannedContentPaths);
        var exclusionsByModule = contentExclusions
            .GroupBy(exclusion => exclusion.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(exclusion => exclusion.RelativePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var declaredDllsByModule = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            if (!module.IsInstalled || string.IsNullOrWhiteSpace(module.Path))
                throw new InvalidDataException($"Bridge module is not installed: {module.Id}");
            if (module.Id.StartsWith(BridgeIdPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A generated BCS bridge cannot depend on another generated bridge.");

            ValidateModuleDirectoryChain(module.Path, module.Path, "module root");

            declaredDllsByModule.Add(
                module.Id,
                ReadDeclaredDlls(Path.Combine(module.Path, "SubModule.xml")));
        }

        disabledSubModules = NormalizeDisabledSubModules(
            disabledSubModules ?? Array.Empty<BridgeDisabledSubModule>(),
            modules,
            declaredDllsByModule);
        clientOnlySubModules = NormalizeClientOnlySubModules(
            clientOnlySubModules ?? Array.Empty<BridgeClientOnlySubModule>(),
            modules,
            declaredDllsByModule);
        var clientOnlyKeys = clientOnlySubModules
            .Select(value => value.ModuleId + "\0" + value.DllName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlappingPolicy = disabledSubModules.FirstOrDefault(value =>
            clientOnlyKeys.Contains(value.ModuleId + "\0" + value.DllName));
        if (overlappingPolicy is not null)
        {
            throw new InvalidDataException(
                $"A submodule cannot be both disabled and client-only: " +
                $"{overlappingPolicy.ModuleId}/{overlappingPolicy.DllName}");
        }
        var disabledDllsByModule = disabledSubModules
            .GroupBy(disabled => disabled.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(disabled => disabled.DllName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var records = new List<BridgeModuleRecord>();
        var contentRecords = new List<BridgeContentRecord>();
        foreach (var module in modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            var declaredDlls = declaredDllsByModule[module.Id];
            disabledDllsByModule.TryGetValue(module.Id, out var disabledDlls);
            var moduleClientOnlyDlls = clientOnlySubModules
                .Where(value => value.ModuleId.Equals(
                    module.Id,
                    StringComparison.OrdinalIgnoreCase))
                .Select(value => value.DllName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var enabledDeclaredDlls = declaredDlls
                .Where(dll => disabledDlls?.Contains(dll) != true)
                .ToArray();
            var preferClient = projectedModuleIds?.Contains(
                module.Id,
                StringComparer.OrdinalIgnoreCase) == true;
            IReadOnlyDictionary<string, string> assemblies = module.Id.Equals(
                    "Coop",
                    StringComparison.OrdinalIgnoreCase)
                ? EnumerateReleasedCoopAssemblies(module.Path, preferClient)
                : ResolveDeclaredAssemblies(module.Path, preferClient, enabledDeclaredDlls);
            if (disabledDlls is { Count: > 0 })
            {
                assemblies = assemblies
                    .Where(pair => !disabledDlls.Contains(pair.Key))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
            }
            foreach (var declaredDll in enabledDeclaredDlls)
            {
                if (!assemblies.ContainsKey(declaredDll))
                    throw new FileNotFoundException(
                        $"Declared assembly was not found for bridge packaging: {module.Id}/{declaredDll}");
            }

            if (assemblies.Count == 0)
                records.Add(new BridgeModuleRecord(module.Id, module.Version, string.Empty, string.Empty));
            foreach (var assemblyEntry in assemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                records.Add(new BridgeModuleRecord(
                    module.Id,
                    module.Version,
                    assemblyEntry.Key,
                    moduleClientOnlyDlls.Contains(assemblyEntry.Key)
                        ? ClientOnlyModuleMarker
                        : string.Empty));
            }
            contentRecords.Add(new BridgeContentRecord(
                module.Id,
                string.Empty));
        }

        authorityRules ??= Array.Empty<BridgeAuthorityRule>();
        ValidateAuthorityRules(authorityRules, modules, records, clientOnlySubModules);
        serverFileRedirects ??= Array.Empty<BridgeServerFileRedirect>();
        ValidateServerFileRedirects(serverFileRedirects, modules);
        serverXmlOverlays ??= Array.Empty<BridgeServerXmlOverlay>();
        ValidateServerXmlOverlays(serverXmlOverlays, modules);
        ValidateGameVersionCompatibility(gameVersionCompatibility);
        clientAssemblyResolves ??= Array.Empty<BridgeClientAssemblyResolve>();
        ValidateClientAssemblyResolves(
            clientAssemblyResolves,
            disabledSubModules);
        serverMapTerrainSizes ??= Array.Empty<BridgeServerMapTerrainSize>();
        ValidateServerMapTerrainSizes(serverMapTerrainSizes, modules);
        runtimeFeatures ??= Array.Empty<BridgeRuntimeFeature>();
        ValidateRuntimeFeatures(runtimeFeatures);
        ValidateBattleSceneCatalogContract(
            battleSceneCatalogContract,
            modules,
            runtimeFeatures);
        var configuration = BuildConfiguration(
            records,
            contentRecords,
            authorityRules,
            contentExclusions,
            serverFileRedirects,
            serverXmlOverlays,
            gameVersionCompatibility,
            clientAssemblyResolves,
            serverMapTerrainSizes,
            runtimeFeatures,
            disabledSubModules,
            battleSceneCatalogContract);
        var serverAssembly = ReadBridgeAssembly(
            ServerBridgeAssemblyResource,
            ServerBridgeAssemblyHash);
        var clientAssembly = ReadBridgeAssembly(
            ClientBridgeAssemblyResource,
            ClientBridgeAssemblyHash);
        var bridgeId = ComputeBridgeId(
            configuration,
            battleSceneCatalogContract?.Content,
            serverAssembly,
            clientAssembly);
        var manifest = BuildManifest(bridgeId, modules);
        var readme = Utf8NoBom.GetBytes(
            "Bannerlord Coop Manager generated Coop bridge package\r\n" +
            $"Module: {bridgeId} {BridgeVersion}\r\n" +
            "\r\n" +
            "Copy the Modules folder into the Bannerlord client installation.\r\n" +
            "Enable this bridge build and the same module IDs and versions on every client.\r\n" +
            "The package validates module versions, required files, safe paths, and required method signatures.\r\n" +
            $"Server-authority rules: {authorityRules.Count}.\r\n" +
            $"Allowed server-only visual files: {contentExclusions.Count}.\r\n" +
            $"Server file redirects: {serverFileRedirects.Count}.\r\n" +
            $"Server XML overlays: {serverXmlOverlays.Count}.\r\n" +
            "Campaign seeding/save rewriting: none.\r\n" +
            "Optional server population controls use bcs-coop-bridge-population.config and preserve native behavior by default.\r\n" +
            $"Bannerlord game-version compatibility: {gameVersionCompatibility is not null}.\r\n" +
            $"Client assembly resolvers: {clientAssemblyResolves.Count}.\r\n" +
            $"Server map terrain sizes: {serverMapTerrainSizes.Count}.\r\n" +
            $"Explicit runtime features: {runtimeFeatures.Count}.\r\n" +
            $"Disabled target submodule DLLs: {disabledSubModules.Count}.\r\n" +
            $"Selected client-only submodule DLLs: {clientOnlySubModules.Count}.\r\n" +
            "Every disabled DLL declaration must also be removed or commented in the client mod manifest before launch; the bridge rejects mismatches.\r\n" +
            "It is a compatibility/authority extension point, not proof that arbitrary custom gameplay state is synchronized.\r\n" +
            "\r\n" +
            "License: GNU GPL version 3 only (GPL-3.0-only).\r\n" +
            "Based on BCS Tool by AppleDeath (AppleDeath318).\r\n" +
            "Compatibility research was informed by HexTool V0.2.3 - Server Update.\r\n" +
            $"Complete corresponding source and notices: {ProjectUrl}\r\n");
        var license = ReadEmbeddedResource(LicenseResource);
        var notice = ReadEmbeddedResource(NoticeResource);
        var clientZip = BuildClientZip(
            bridgeId,
            manifest,
            configuration,
            serverAssembly,
            clientAssembly,
            readme,
            license,
            notice,
            battleSceneCatalogContract);

        return new CoopBridgePackage(
            bridgeId,
            BridgeVersion,
            manifest,
            configuration,
            serverAssembly,
            clientAssembly,
            clientZip,
            disabledSubModules,
            clientOnlySubModules,
            records,
            contentRecords,
            authorityRules,
            contentExclusions,
            serverFileRedirects,
            serverXmlOverlays,
            gameVersionCompatibility,
            clientAssemblyResolves,
            serverMapTerrainSizes,
            runtimeFeatures,
            battleSceneCatalogContract);
    }

    private static byte[] BuildConfiguration(
        IReadOnlyList<BridgeModuleRecord> records,
        IReadOnlyList<BridgeContentRecord> contentRecords,
        IReadOnlyList<BridgeAuthorityRule> authorityRules,
        IReadOnlyList<BridgeContentExclusion> contentExclusions,
        IReadOnlyList<BridgeServerFileRedirect> serverFileRedirects,
        IReadOnlyList<BridgeServerXmlOverlay> serverXmlOverlays,
        BridgeGameVersionCompatibility? gameVersionCompatibility,
        IReadOnlyList<BridgeClientAssemblyResolve> clientAssemblyResolves,
        IReadOnlyList<BridgeServerMapTerrainSize> serverMapTerrainSizes,
        IReadOnlyList<BridgeRuntimeFeature> runtimeFeatures,
        IReadOnlyList<BridgeDisabledSubModule> disabledSubModules,
        BridgeBattleSceneCatalogContract? battleSceneCatalogContract)
    {
        var builder = new StringBuilder(battleSceneCatalogContract is null
            ? "BCS-COOP-BRIDGE|2\n"
            : "BCS-COOP-BRIDGE|3\n");
        foreach (var feature in runtimeFeatures.OrderBy(value => value.ToString(), StringComparer.Ordinal))
        {
            builder.Append("RUNTIME_FEATURE|")
                .Append(Encode(feature.ToString()))
                .Append('\n');
        }
        if (battleSceneCatalogContract is not null)
        {
            builder.Append("BATTLE_SCENE_CATALOG_CONTRACT|")
                .Append(Encode(battleSceneCatalogContract.TargetModuleId)).Append('|')
                .Append(Encode(battleSceneCatalogContract.TargetVersion)).Append('|')
                .Append(Encode(battleSceneCatalogContract.BaseModuleId)).Append('|')
                .Append(Encode(battleSceneCatalogContract.BaseVersion)).Append('|')
                .Append(Encode(battleSceneCatalogContract.RelativePath)).Append('|')
                .Append(battleSceneCatalogContract.Sha256).Append('|')
                .Append("WARN_ONLY")
                .Append('\n');
        }
        if (gameVersionCompatibility is not null)
        {
            builder.Append("GAME_VERSION_COMPAT|")
                .Append(Encode(gameVersionCompatibility.ServerVersion)).Append('|')
                .Append(Encode(gameVersionCompatibility.ClientVersion)).Append('|')
                .Append(Encode(gameVersionCompatibility.ServerRuntimeVersion)).Append('|')
                .Append(Encode(gameVersionCompatibility.ClientRuntimeVersion))
                .Append('\n');
        }
        foreach (var resolver in clientAssemblyResolves
                     .OrderBy(rule => rule.ModuleId, StringComparer.Ordinal)
                     .ThenBy(rule => rule.RelativePath, StringComparer.Ordinal))
        {
            builder.Append("CLIENT_ASSEMBLY_RESOLVE|")
                .Append(Encode(resolver.ModuleId)).Append('|')
                .Append(Encode(resolver.RelativePath)).Append('|')
                .Append(string.Empty)
                .Append('\n');
        }
        foreach (var record in records)
        {
            builder.Append("MODULE|")
                .Append(Encode(record.ModuleId)).Append('|')
                .Append(Encode(record.Version)).Append('|')
                .Append(Encode(record.DllName)).Append('|')
                .Append(record.Sha256)
                .Append('\n');
        }
        foreach (var disabled in disabledSubModules
                     .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
                     .ThenBy(value => value.DllName, StringComparer.Ordinal))
        {
            builder.Append("DISABLED_SUBMODULE|")
                .Append(Encode(disabled.ModuleId)).Append('|')
                .Append(Encode(disabled.DllName))
                .Append('\n');
        }
        foreach (var exclusion in contentExclusions
                     .OrderBy(exclusion => exclusion.ModuleId, StringComparer.Ordinal)
                     .ThenBy(exclusion => exclusion.RelativePath, StringComparer.Ordinal))
        {
            builder.Append("IGNORE_CONTENT|")
                .Append(Encode(exclusion.ModuleId)).Append('|')
                .Append(Encode(exclusion.RelativePath))
                .Append('\n');
        }
        foreach (var record in contentRecords.OrderBy(record => record.ModuleId, StringComparer.Ordinal))
        {
            builder.Append("CONTENT|")
                .Append(Encode(record.ModuleId)).Append('|')
                .Append(string.Empty)
                .Append('\n');
        }
        foreach (var redirect in serverFileRedirects
                     .OrderBy(redirect => redirect.ModuleId, StringComparer.Ordinal)
                     .ThenBy(redirect => redirect.RelativePath, StringComparer.Ordinal))
        {
            builder.Append("SERVER_FILE_REDIRECT|")
                .Append(Encode(redirect.ModuleId)).Append('|')
                .Append(Encode(redirect.RelativePath)).Append('|')
                .Append(string.Empty)
                .Append('\n');
        }
        foreach (var overlay in serverXmlOverlays
                     .OrderBy(overlay => overlay.ModuleId, StringComparer.Ordinal)
                     .ThenBy(overlay => overlay.RelativePath, StringComparer.Ordinal))
        {
            builder.Append("SERVER_XML_OVERLAY|")
                .Append(Encode(overlay.ModuleId)).Append('|')
                .Append(Encode(overlay.RelativePath)).Append('|')
                .Append(string.Empty).Append('|')
                .Append(Encode(overlay.OverlayRelativePath)).Append('|')
                .Append(string.Empty)
                .Append('\n');
        }
        foreach (var terrainSize in serverMapTerrainSizes
                     .OrderBy(rule => rule.ModuleId, StringComparer.Ordinal)
                     .ThenBy(rule => rule.RelativePath, StringComparer.Ordinal))
        {
            builder.Append("SERVER_MAP_TERRAIN_SIZE|")
                .Append(Encode(terrainSize.ModuleId)).Append('|')
                .Append(Encode(terrainSize.RelativePath)).Append('|')
                .Append(string.Empty).Append('|')
                .Append(terrainSize.Width.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(terrainSize.Height.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(Encode(terrainSize.LoaderAssemblyName)).Append('|')
                .Append(string.Empty).Append('|')
                .Append(Encode(terrainSize.TargetAssemblyName)).Append('|')
                .Append(string.Empty)
                .Append('\n');
        }
        foreach (var rule in authorityRules
                     .OrderBy(rule => rule.ModuleId, StringComparer.Ordinal)
                     .ThenBy(rule => rule.DllName, StringComparer.Ordinal)
                     .ThenBy(rule => rule.TypeName, StringComparer.Ordinal)
                     .ThenBy(rule => rule.MethodName, StringComparer.Ordinal)
                     .ThenBy(rule => rule.ParameterCount)
                     .ThenBy(rule => rule.Scope))
        {
            builder.Append("AUTHORITY|")
                .Append(Encode(rule.ModuleId)).Append('|')
                .Append(Encode(rule.DllName)).Append('|')
                .Append(Encode(rule.TypeName)).Append('|')
                .Append(Encode(rule.MethodName)).Append('|')
                .Append(rule.ParameterCount).Append('|')
                .Append(rule.Scope switch
                {
                    BridgeInvocationScope.ServerOnly => "SERVER_ONLY",
                    BridgeInvocationScope.ClientOnly => "CLIENT_ONLY",
                    BridgeInvocationScope.ServerSettingsFallback => "SERVER_SETTINGS_FALLBACK",
                    _ => throw new InvalidDataException("Unsupported authority-rule scope.")
                })
                .Append('\n');
        }
        return Utf8NoBom.GetBytes(builder.ToString());
    }

    private static IReadOnlyList<BridgeDisabledSubModule> NormalizeDisabledSubModules(
        IReadOnlyList<BridgeDisabledSubModule> disabledSubModules,
        IReadOnlyList<BannerlordModule> modules,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredDllsByModule)
    {
        var modulesById = modules.ToDictionary(
            module => module.Id,
            StringComparer.OrdinalIgnoreCase);
        var normalized = new List<BridgeDisabledSubModule>(disabledSubModules.Count);
        foreach (var disabled in disabledSubModules)
        {
            if (disabled is null)
                throw new InvalidDataException("Disabled submodule records cannot be null.");
            if (string.IsNullOrWhiteSpace(disabled.ModuleId))
                throw new InvalidDataException("Disabled submodule module IDs cannot be empty.");
            if (!modulesById.TryGetValue(disabled.ModuleId, out var module))
            {
                throw new InvalidDataException(
                    $"Disabled submodule references an unknown bridge module: {disabled.ModuleId}");
            }
            if (module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Coop submodules cannot be disabled by a generated compatibility bridge.");
            }
            if (!IsSafeDllName(disabled.DllName))
            {
                throw new InvalidDataException(
                    $"Unsafe disabled submodule DLL name: {disabled.DllName}");
            }

            var declaredDll = declaredDllsByModule[module.Id]
                .SingleOrDefault(name => name.Equals(
                    disabled.DllName,
                    StringComparison.OrdinalIgnoreCase));
            if (declaredDll is null)
            {
                throw new InvalidDataException(
                    $"Disabled submodule is not declared by {module.Id}: {disabled.DllName}");
            }

            normalized.Add(new BridgeDisabledSubModule(module.Id, declaredDll));
        }

        var duplicate = normalized
            .GroupBy(value => value.ModuleId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.GroupBy(
                value => value.DllName,
                StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Duplicate disabled submodule DLL: {duplicate.First().ModuleId}/{duplicate.Key}");
        }

        return normalized
            .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
            .ThenBy(value => value.DllName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<BridgeClientOnlySubModule> NormalizeClientOnlySubModules(
        IReadOnlyList<BridgeClientOnlySubModule> clientOnlySubModules,
        IReadOnlyList<BannerlordModule> modules,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredDllsByModule)
    {
        var modulesById = modules.ToDictionary(
            module => module.Id,
            StringComparer.OrdinalIgnoreCase);
        var normalized = new List<BridgeClientOnlySubModule>(clientOnlySubModules.Count);
        foreach (var clientOnly in clientOnlySubModules)
        {
            if (clientOnly is null)
                throw new InvalidDataException("Client-only submodule records cannot be null.");
            if (string.IsNullOrWhiteSpace(clientOnly.ModuleId))
                throw new InvalidDataException("Client-only submodule module IDs cannot be empty.");
            if (!modulesById.TryGetValue(clientOnly.ModuleId, out var module))
            {
                throw new InvalidDataException(
                    $"Client-only submodule references an unknown bridge module: {clientOnly.ModuleId}");
            }
            if (module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Coop submodules cannot be marked client-only by a generated compatibility bridge.");
            }
            if (!IsSafeDllName(clientOnly.DllName))
            {
                throw new InvalidDataException(
                    $"Unsafe client-only submodule DLL name: {clientOnly.DllName}");
            }

            var declaredDll = declaredDllsByModule[module.Id]
                .SingleOrDefault(name => name.Equals(
                    clientOnly.DllName,
                    StringComparison.OrdinalIgnoreCase));
            if (declaredDll is null)
            {
                throw new InvalidDataException(
                    $"Client-only submodule is not declared by {module.Id}: {clientOnly.DllName}");
            }

            normalized.Add(new BridgeClientOnlySubModule(module.Id, declaredDll));
        }

        var duplicate = normalized
            .GroupBy(value => value.ModuleId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.GroupBy(
                value => value.DllName,
                StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Duplicate client-only submodule DLL: {duplicate.First().ModuleId}/{duplicate.Key}");
        }

        return normalized
            .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
            .ThenBy(value => value.DllName, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateRuntimeFeatures(IReadOnlyList<BridgeRuntimeFeature> runtimeFeatures)
    {
        var seen = new HashSet<BridgeRuntimeFeature>();
        foreach (var feature in runtimeFeatures)
        {
            if (!Enum.IsDefined(feature))
                throw new InvalidDataException($"Unsupported bridge runtime feature: {(int)feature}.");
            if (!seen.Add(feature))
                throw new InvalidDataException($"Duplicate bridge runtime feature: {feature}.");
        }
    }

    private static void ValidateBattleSceneCatalogContract(
        BridgeBattleSceneCatalogContract? contract,
        IReadOnlyList<BannerlordModule> modules,
        IReadOnlyList<BridgeRuntimeFeature> runtimeFeatures)
    {
        var featureEnabled = runtimeFeatures.Contains(
            BridgeRuntimeFeature.ClientDeterministicBattleSceneProjection);
        if (contract is null)
        {
            if (featureEnabled)
            {
                throw new InvalidDataException(
                    "Deterministic battle-scene projection requires a pinned catalog contract.");
            }
            return;
        }
        if (!featureEnabled)
        {
            throw new InvalidDataException(
                "A battle-scene catalog contract requires deterministic battle-scene projection.");
        }

        var target = modules.SingleOrDefault(module => module.Id.Equals(
            contract.TargetModuleId,
            StringComparison.OrdinalIgnoreCase));
        if (target is null || !target.Version.Equals(
                contract.TargetVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Battle-scene catalog contract target identity does not match the bridge modules.");
        }
        if (string.IsNullOrWhiteSpace(contract.BaseModuleId) ||
            string.IsNullOrWhiteSpace(contract.BaseVersion) ||
            contract.BaseModuleId.Equals(contract.TargetModuleId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Battle-scene catalog contract has an invalid base-module identity.");
        }
        var relativePath = ValidateSafeRelativePath(
            contract.RelativePath,
            "battle-scene catalog contract path");
        if (!relativePath.Equals(contract.RelativePath, StringComparison.Ordinal) ||
            relativePath.Split('/').Any(segment =>
                segment.Length == 0 ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            !relativePath.StartsWith("BattleSceneCatalog/", StringComparison.Ordinal) ||
            !Path.GetExtension(relativePath).Equals(".bcs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Battle-scene catalog contract must stay under BattleSceneCatalog and use .bcs.");
        }
        if (contract.Content is null || contract.Content.Length is 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("Battle-scene catalog contract payload is invalid.");
        if (contract.Sha256.Length != 64 ||
            contract.Sha256.Any(character => character is not (
                >= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new InvalidDataException(
                "Battle-scene catalog contract hash must be uppercase SHA-256.");
        }
        var actualHash = Hash(contract.Content);
        if (!actualHash.Equals(contract.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Battle-scene catalog contract hash mismatch. Expected {contract.Sha256}, found {actualHash}.");
        }
    }

    private static void ValidateClientAssemblyResolves(
        IReadOnlyList<BridgeClientAssemblyResolve> resolvers,
        IReadOnlyList<BridgeDisabledSubModule> disabledSubModules)
    {
        var disabledKeys = disabledSubModules
            .Select(disabled => disabled.ModuleId + "\0" + disabled.DllName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolver in resolvers)
        {
            var relative = resolver.RelativePath.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(resolver.ModuleId) ||
                string.IsNullOrWhiteSpace(relative) ||
                Path.IsPathRooted(relative) ||
                relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or "..") ||
                !Path.GetExtension(relative).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsafe client assembly resolver path.");
            }
            if (!seen.Add(resolver.ModuleId + "|" + relative))
                throw new InvalidDataException("Duplicate client assembly resolver.");
            if (disabledKeys.Contains(
                    resolver.ModuleId + "\0" + Path.GetFileName(relative)))
            {
                throw new InvalidDataException(
                    "Client assembly resolver targets a disabled submodule: " +
                    resolver.ModuleId + "/" + Path.GetFileName(relative));
            }
            if (!File.Exists(resolver.SourcePath) ||
                (File.GetAttributes(resolver.SourcePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException(
                    "Client assembly resolver source is missing or linked.",
                    resolver.SourcePath);
            }
            if (!Path.GetFileName(resolver.SourcePath).Equals(
                    Path.GetFileName(relative),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Client assembly resolver source name does not match its module-relative path.");
            }
            var assemblyName = AssemblyName.GetAssemblyName(resolver.SourcePath).Name;
            if (!string.Equals(
                    assemblyName,
                    Path.GetFileNameWithoutExtension(relative),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Client assembly resolver file name does not match its assembly identity.");
            }
            if (!seenAssemblyNames.Add(assemblyName!))
            {
                throw new InvalidDataException(
                    "Duplicate client assembly resolver simple name: " + assemblyName + ".");
            }
        }
    }

    private static void ValidateContentExclusions(
        IReadOnlyList<BridgeContentExclusion> contentExclusions,
        IReadOnlyList<BannerlordModule> modules,
        IReadOnlyCollection<string>? plannedContentPaths)
    {
        var moduleIds = modules.Select(module => module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedTargets = (plannedContentPaths ?? Array.Empty<string>())
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var exclusion in contentExclusions)
        {
            if (!moduleIds.Contains(exclusion.ModuleId))
                throw new InvalidDataException(
                    $"Content exclusion targets an unknown module: {exclusion.ModuleId}");
            var relative = exclusion.RelativePath.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) ||
                Path.IsPathRooted(relative) ||
                relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException($"Unsafe bridge content exclusion: {exclusion.RelativePath}");
            }
            var module = modules.Single(candidate =>
                candidate.Id.Equals(exclusion.ModuleId, StringComparison.OrdinalIgnoreCase));
            var target = Path.GetFullPath(Path.Combine(module.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(module.Path).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                (!File.Exists(target) && !plannedTargets.Contains(target)) ||
                (File.Exists(target) &&
                 (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException(
                    $"Bridge content exclusion is missing, linked, or outside its module: {exclusion.ModuleId}/{relative}");
            }
            if (!seen.Add(exclusion.ModuleId + "|" + relative))
                throw new InvalidDataException("Duplicate bridge content exclusion.");
        }
    }

    private static void ValidateAuthorityRules(
        IReadOnlyList<BridgeAuthorityRule> authorityRules,
        IReadOnlyList<BannerlordModule> modules,
        IReadOnlyList<BridgeModuleRecord> records,
        IReadOnlyList<BridgeClientOnlySubModule> clientOnlySubModules)
    {
        var moduleIds = modules.Select(module => module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in authorityRules)
        {
            if (!moduleIds.Contains(rule.ModuleId))
                throw new InvalidDataException($"Authority rule targets an unknown module: {rule.ModuleId}");
            if (string.IsNullOrWhiteSpace(rule.DllName) ||
                !Path.GetFileName(rule.DllName).Equals(rule.DllName, StringComparison.Ordinal) ||
                !Path.GetExtension(rule.DllName).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe authority-rule DLL name: {rule.DllName}");
            }
            if (!records.Any(record =>
                    record.ModuleId.Equals(rule.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                    record.DllName.Equals(rule.DllName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Authority rule targets an undeclared assembly: {rule.ModuleId}/{rule.DllName}");
            }
            if (rule.Scope != BridgeInvocationScope.ClientOnly &&
                clientOnlySubModules.Any(value =>
                    value.ModuleId.Equals(rule.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                    value.DllName.Equals(rule.DllName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Non-client authority rule targets a client-only submodule: " +
                    $"{rule.ModuleId}/{rule.DllName}");
            }
            if (string.IsNullOrWhiteSpace(rule.TypeName) ||
                string.IsNullOrWhiteSpace(rule.MethodName) ||
                rule.ParameterCount < 0 ||
                !Enum.IsDefined(rule.Scope))
            {
                throw new InvalidDataException("Authority rule has an invalid type, method, or parameter count.");
            }
        }

        var duplicate = authorityRules
            .GroupBy(rule => new
            {
                Module = rule.ModuleId.ToUpperInvariant(),
                Dll = rule.DllName.ToUpperInvariant(),
                rule.TypeName,
                rule.MethodName,
                rule.ParameterCount,
                rule.Scope
            })
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException("Duplicate Coop bridge authority rule.");
    }

    private static void ValidateServerFileRedirects(
        IReadOnlyList<BridgeServerFileRedirect> redirects,
        IReadOnlyList<BannerlordModule> modules)
    {
        var byId = modules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var redirect in redirects)
        {
            if (!byId.TryGetValue(redirect.ModuleId, out var module))
            {
                throw new InvalidDataException(
                    $"Server file redirect targets an unknown module: {redirect.ModuleId}");
            }
            var relative = redirect.RelativePath.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) ||
                Path.IsPathRooted(relative) ||
                relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException(
                    $"Unsafe server file redirect: {redirect.RelativePath}");
            }
            var target = Path.GetFullPath(
                Path.Combine(module.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(module.Path).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target) ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Server file redirect is missing, linked, or outside its module: " +
                    $"{redirect.ModuleId}/{relative}");
            }
            if (!fileNames.Add(Path.GetFileName(target)))
                throw new InvalidDataException("Duplicate server file redirect target name.");
        }
    }

    private static void ValidateServerXmlOverlays(
        IReadOnlyList<BridgeServerXmlOverlay> overlays,
        IReadOnlyList<BannerlordModule> modules)
    {
        var byId = modules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in overlays)
        {
            if (!byId.TryGetValue(overlay.ModuleId, out var module))
                throw new InvalidDataException(
                    $"Server XML overlay targets an unknown module: {overlay.ModuleId}");
            var sourceRelative = ValidateSafeRelativePath(
                overlay.RelativePath,
                "server XML overlay source");
            var overlayRelative = ValidateSafeRelativePath(
                overlay.OverlayRelativePath,
                "server XML overlay destination");
            if (!overlayRelative.StartsWith("bcs-server-overlays/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Server XML overlay must stay under bcs-server-overlays: {overlay.OverlayRelativePath}");
            var sourcePath = Path.GetFullPath(Path.Combine(
                module.Path,
                sourceRelative.Replace('/', Path.DirectorySeparatorChar)));
            var moduleRoot = Path.GetFullPath(module.Path).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
            if (!sourcePath.StartsWith(moduleRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(sourcePath) ||
                (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Server XML overlay source is missing, linked, or outside its module: " +
                    $"{overlay.ModuleId}/{sourceRelative}");
            }
            if (overlay.Content is null || overlay.Content.Length == 0)
            {
                throw new InvalidDataException(
                    $"Server XML overlay payload is empty: {overlay.OverlayRelativePath}");
            }
            if (!sources.Add(overlay.ModuleId + "|" + sourceRelative) ||
                !destinations.Add(overlayRelative))
                throw new InvalidDataException("Duplicate server XML overlay source or destination.");
        }
    }

    private static void ValidateServerMapTerrainSizes(
        IReadOnlyList<BridgeServerMapTerrainSize> terrainSizes,
        IReadOnlyList<BannerlordModule> modules)
    {
        const float MaximumTerrainDimension = 1_000_000f;
        if (terrainSizes.Count > 1)
        {
            throw new InvalidDataException(
                "Conflicting server map terrain size rules are not supported.");
        }
        var byId = modules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var terrainSize in terrainSizes)
        {
            if (!byId.TryGetValue(terrainSize.ModuleId, out var module))
            {
                throw new InvalidDataException(
                    $"Server map terrain size targets an unknown module: {terrainSize.ModuleId}");
            }

            var relative = ValidateSafeRelativePath(
                terrainSize.RelativePath,
                "server map terrain size source");
            var moduleRoot = Path.GetFullPath(module.Path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var sourcePath = Path.GetFullPath(Path.Combine(
                moduleRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            var modulePrefix = moduleRoot + Path.DirectorySeparatorChar;
            if (!sourcePath.StartsWith(modulePrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(sourcePath))
            {
                throw new InvalidDataException(
                    $"Server map terrain size source is missing or outside its module: " +
                    $"{terrainSize.ModuleId}/{relative}");
            }

            var attributes = File.GetAttributes(sourcePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    $"Server map terrain size source is not a regular unlinked file: " +
                    $"{terrainSize.ModuleId}/{relative}");
            }

            for (var parent = Directory.GetParent(sourcePath); parent is not null; parent = parent.Parent)
            {
                var parentPath = Path.GetFullPath(parent.FullName).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if ((File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Server map terrain size source traverses a linked directory: " +
                        $"{terrainSize.ModuleId}/{relative}");
                }
                if (parentPath.Equals(moduleRoot, StringComparison.OrdinalIgnoreCase))
                    break;
            }

            if (!IsSafeAssemblySimpleName(terrainSize.LoaderAssemblyName) ||
                !IsSafeAssemblySimpleName(terrainSize.TargetAssemblyName))
            {
                throw new InvalidDataException(
                    "Server map terrain size loader and target assembly names must be safe simple names.");
            }
            if (!float.IsFinite(terrainSize.Width) ||
                !float.IsFinite(terrainSize.Height) ||
                terrainSize.Width <= 0f ||
                terrainSize.Height <= 0f ||
                terrainSize.Width > MaximumTerrainDimension ||
                terrainSize.Height > MaximumTerrainDimension)
            {
                throw new InvalidDataException(
                    "Server map terrain dimensions must be finite, positive, and no greater than " +
                    $"{MaximumTerrainDimension.ToString("R", CultureInfo.InvariantCulture)}.");
            }
            if (!sources.Add(terrainSize.ModuleId + "|" + relative))
                throw new InvalidDataException("Duplicate server map terrain size source.");
        }
    }

    private static string ValidateSafeRelativePath(string value, string description)
    {
        var relative = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative) ||
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe {description}: {value}");
        }
        return relative;
    }

    private static bool IsSafeAssemblySimpleName(string value) =>
        value is not null &&
        value.Length is > 0 and <= 128 &&
        value[0] != '.' &&
        value[^1] != '.' &&
        !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '.' or '_' or '-');

    private static void ValidateGameVersionCompatibility(
        BridgeGameVersionCompatibility? rule)
    {
        if (rule is null)
            return;
        if (string.IsNullOrWhiteSpace(rule.ServerVersion) ||
            string.IsNullOrWhiteSpace(rule.ClientVersion) ||
            string.IsNullOrWhiteSpace(rule.ServerRuntimeVersion) ||
            string.IsNullOrWhiteSpace(rule.ClientRuntimeVersion) ||
            !IsSemanticGameVersion(rule.ServerVersion, 3) ||
            !IsSemanticGameVersion(rule.ClientVersion, 3) ||
            !IsSemanticGameVersion(rule.ServerRuntimeVersion, 4) ||
            !IsSemanticGameVersion(rule.ClientRuntimeVersion, 4) ||
            !rule.ServerRuntimeVersion.StartsWith(
                rule.ServerVersion + ".",
                StringComparison.OrdinalIgnoreCase) ||
            !rule.ClientRuntimeVersion.StartsWith(
                rule.ClientVersion + ".",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Game-version compatibility requires valid base versions and matching exact runtime versions.");
        }
    }

    private static bool IsSemanticGameVersion(string value, int componentCount)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 ||
            value[0] is not ('a' or 'b' or 'd' or 'e' or 'v'))
        {
            return false;
        }

        var components = value[1..].Split('.');
        if (components.Length != componentCount)
            return false;
        for (var index = 0; index < components.Length; index++)
        {
            if (!int.TryParse(
                    components[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var component) ||
                component < 0 ||
                (index == 0 && component == 0) ||
                (index == components.Length - 1 && componentCount == 4 && component == 0))
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] BuildManifest(
        string bridgeId,
        IReadOnlyList<BannerlordModule> modules)
    {
        var document = new XmlDocument { XmlResolver = null };
        var root = document.CreateElement("Module");
        document.AppendChild(root);
        AddValue(document, root, "Name", $"BCS Coop Bridge ({bridgeId[^24..]})");
        AddValue(document, root, "Id", bridgeId);
        AddValue(document, root, "Version", BridgeVersion);
        AddValue(document, root, "SingleplayerModule", "true");
        AddValue(document, root, "MultiplayerModule", "false");

        var dependencies = document.CreateElement("DependedModules");
        root.AppendChild(dependencies);
        foreach (var module in modules
                     .OrderBy(module => module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(module => module.Id, StringComparer.Ordinal))
        {
            var dependency = document.CreateElement("DependedModule");
            dependency.SetAttribute("Id", module.Id);
            if (!string.IsNullOrWhiteSpace(module.Version))
                dependency.SetAttribute("DependentVersion", module.Version);
            dependency.SetAttribute("Optional", "false");
            dependencies.AppendChild(dependency);
        }

        AddValue(document, root, "ModuleType", "Community");

        var submodules = document.CreateElement("SubModules");
        root.AppendChild(submodules);
        var submodule = document.CreateElement("SubModule");
        submodules.AppendChild(submodule);
        AddValue(document, submodule, "Name", "BCS Coop Bridge");
        AddValue(document, submodule, "DLLName", "BCS.CoopBridge.dll");
        AddValue(document, submodule, "SubModuleClassType", "BCS.CoopBridge.BridgeSubModule");
        root.AppendChild(document.CreateElement("Xmls"));

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return stream.ToArray();
    }

    private static void AddValue(
        XmlDocument document,
        XmlElement parent,
        string name,
        string value)
    {
        var element = document.CreateElement(name);
        element.SetAttribute("value", value);
        parent.AppendChild(element);
    }

    private static byte[] ReadBridgeAssembly(string resourceName, string expectedHash)
    {
        var bytes = ReadEmbeddedResource(resourceName);
        var actualHash = Hash(bytes);
        if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Embedded Coop bridge artifact failed integrity validation. Expected {expectedHash}, found {actualHash}.");
        }
        return bytes;
    }

    private static byte[] ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(CoopBridgePackageBuilder).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource is missing: {resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] BuildClientZip(
        string bridgeId,
        byte[] manifest,
        byte[] configuration,
        byte[] serverAssembly,
        byte[] clientAssembly,
        byte[] readme,
        byte[] license,
        byte[] notice,
        BridgeBattleSceneCatalogContract? battleSceneCatalogContract)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = $"Modules/{bridgeId}/";
            AddZipEntry(archive, root + "README.txt", readme);
            AddZipEntry(archive, root + "LICENSE", license);
            AddZipEntry(archive, root + "NOTICE.md", notice);
            AddZipEntry(archive, root + "SubModule.xml", manifest);
            AddZipEntry(archive, root + "bcs-coop-bridge.config", configuration);
            if (battleSceneCatalogContract is not null)
            {
                AddZipEntry(
                    archive,
                    root + battleSceneCatalogContract.RelativePath,
                    battleSceneCatalogContract.Content);
            }
            AddZipEntry(
                archive,
                root + "bin/Win64_Shipping_Server/BCS.CoopBridge.dll",
                serverAssembly);
            AddZipEntry(
                archive,
                root + "bin/Win64_Shipping_Client/BCS.CoopBridge.dll",
                clientAssembly);
        }
        return stream.ToArray();
    }

    private static void AddZipEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var target = entry.Open();
        target.Write(bytes, 0, bytes.Length);
    }

    private static IReadOnlyList<string> ReadDeclaredDlls(string manifestPath)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        document.Load(reader);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document
                     .SelectNodes("/Module/SubModules/SubModule/DLLName")!
                     .OfType<XmlElement>())
        {
            var name = element.GetAttribute("value");
            if (!IsSafeDllName(name))
            {
                throw new InvalidDataException(
                    $"Unsafe declared DLL name in {manifestPath}: {name}");
            }
            if (!seen.Add(name))
            {
                throw new InvalidDataException(
                    $"Duplicate declared DLL name in {manifestPath}: {name}");
            }
            names.Add(name);
        }
        return names;
    }

    internal static string ComputeBridgeId(byte[] configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ComputeBridgeId(
            configuration,
            null,
            ReadBridgeAssembly(ServerBridgeAssemblyResource, ServerBridgeAssemblyHash),
            ReadBridgeAssembly(ClientBridgeAssemblyResource, ClientBridgeAssemblyHash));
    }

    internal static string ComputeBridgeId(
        byte[] configuration,
        byte[] battleSceneCatalogContract)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(battleSceneCatalogContract);
        return ComputeBridgeId(
            configuration,
            battleSceneCatalogContract,
            ReadBridgeAssembly(ServerBridgeAssemblyResource, ServerBridgeAssemblyHash),
            ReadBridgeAssembly(ClientBridgeAssemblyResource, ClientBridgeAssemblyHash));
    }

    internal static string ComputeBridgeId(
        byte[] configuration,
        byte[]? battleSceneCatalogContract,
        byte[] serverAssembly,
        byte[] clientAssembly)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serverAssembly);
        ArgumentNullException.ThrowIfNull(clientAssembly);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(configuration);
        if (battleSceneCatalogContract is not null)
            hash.AppendData(battleSceneCatalogContract);
        hash.AppendData(serverAssembly);
        hash.AppendData(clientAssembly);
        return BridgeIdPrefix + Convert.ToHexString(hash.GetHashAndReset())[..24]
            .ToLowerInvariant();
    }

    private static bool IsSafeDllName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Equals(name.Trim(), StringComparison.Ordinal) &&
        Path.GetFileName(name).Equals(name, StringComparison.Ordinal) &&
        Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static IReadOnlyDictionary<string, string> EnumerateAssemblies(
        string moduleRoot,
        bool preferClient)
    {
        var bins = preferClient
            ? new[]
            {
                "Win64_Shipping_Client",
                "Gaming.Desktop.x64_Shipping_Client",
                "Win64_Shipping_Server"
            }
            : new[]
            {
                "Win64_Shipping_Server",
                "Win64_Shipping_Client",
                "Gaming.Desktop.x64_Shipping_Client"
            };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bin in bins)
        {
            var directory = Path.Combine(moduleRoot, "bin", bin);
            if (!Directory.Exists(directory))
                continue;
            ValidateModuleDirectoryChain(moduleRoot, directory, "module bin");
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (!TryValidateManagedAssemblyFile(file))
                    continue;
                result.TryAdd(Path.GetFileName(file), file);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ResolveDeclaredAssemblies(
        string moduleRoot,
        bool preferClient,
        IReadOnlyList<string> declaredDlls)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (declaredDlls.Count == 0)
            return result;

        var bins = preferClient
            ? new[]
            {
                "Win64_Shipping_Client",
                "Gaming.Desktop.x64_Shipping_Client",
                "Win64_Shipping_Server"
            }
            : new[]
            {
                "Win64_Shipping_Server",
                "Win64_Shipping_Client",
                "Gaming.Desktop.x64_Shipping_Client"
            };
        foreach (var bin in bins)
        {
            var directory = Path.Combine(moduleRoot, "bin", bin);
            if (!Directory.Exists(directory))
                continue;
            ValidateModuleDirectoryChain(moduleRoot, directory, "module bin");
            foreach (var declaredDll in declaredDlls)
            {
                if (result.ContainsKey(declaredDll))
                    continue;
                var file = Path.Combine(directory, declaredDll);
                if (!File.Exists(file))
                    continue;
                if (!TryValidateManagedAssemblyFile(file))
                {
                    throw new InvalidDataException(
                        $"Declared module DLL is not a readable managed assembly: {file}");
                }
                result.Add(declaredDll, file);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> EnumerateReleasedCoopAssemblies(
        string moduleRoot,
        bool preferClient)
    {
        var dedicatedServer = Directory.GetParent(moduleRoot)?.Parent?.Parent;
        if (dedicatedServer is null ||
            !dedicatedServer.Name.Equals("DedicatedServer", StringComparison.OrdinalIgnoreCase))
        {
            return EnumerateAssemblies(moduleRoot, preferClient);
        }

        var releaseRoot = dedicatedServer.Parent?.FullName;
        if (releaseRoot is null ||
            !File.Exists(Path.Combine(releaseRoot, "SubModule.xml")))
        {
            return EnumerateAssemblies(moduleRoot, preferClient);
        }

        var server = EnumerateAssemblyDirectory(
            moduleRoot,
            Path.Combine(moduleRoot, "bin", "Win64_Shipping_Server"));
        var client = EnumerateAssemblyDirectory(
            releaseRoot,
            Path.Combine(releaseRoot, "bin", "Win64_Shipping_Client"));
        if (server.Count == 0 || client.Count == 0)
            return EnumerateAssemblies(moduleRoot, preferClient);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serverAssembly in server.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!client.TryGetValue(serverAssembly.Key, out var clientPath))
                continue;
            if (serverAssembly.Key.Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(serverAssembly.Key, serverAssembly.Value);
        }

        if (!result.ContainsKey("Coop.dll"))
            throw new InvalidDataException("Released Coop package has no cross-role Coop.dll.");
        return result;
    }

    private static IReadOnlyDictionary<string, string> EnumerateAssemblyDirectory(
        string moduleRoot,
        string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
            return result;
        ValidateModuleDirectoryChain(moduleRoot, directory, "module bin");
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!TryValidateManagedAssemblyFile(file))
                continue;
            result.Add(Path.GetFileName(file), file);
        }
        return result;
    }

    private static void ValidateModuleDirectoryChain(
        string moduleRoot,
        string directory,
        string description)
    {
        var canonicalRoot = Path.GetFullPath(moduleRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var canonicalDirectory = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(canonicalDirectory, canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonicalDirectory.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Bridge {description} escaped its module: {directory}");
        }

        for (var current = new DirectoryInfo(canonicalDirectory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked bridge {description} is not safe to package: {current.FullName}");
            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidDataException($"Bridge {description} escaped its module: {directory}");
    }

    private static bool TryValidateManagedAssemblyFile(string file)
    {
        if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Linked or missing assembly is not safe to package: {file}");

        string? assemblyName;
        try
        {
            assemblyName = AssemblyName.GetAssemblyName(file).Name;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileLoadException)
        {
            return false;
        }

        var expectedName = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(assemblyName) ||
            !assemblyName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Module DLL identity does not match its file name: {file}. Found {assemblyName ?? "<null>"}.");
        }
        return true;
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Utf8NoBom.GetBytes(value ?? string.Empty));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

}

public sealed record BridgeModuleRecord(
    string ModuleId,
    string Version,
    string DllName,
    string Sha256);

public sealed record BridgeDisabledSubModule(
    string ModuleId,
    string DllName);

public sealed record BridgeClientOnlySubModule(
    string ModuleId,
    string DllName);

public sealed record BridgeContentRecord(
    string ModuleId,
    string Sha256);

public sealed record BridgeAuthorityRule(
    string ModuleId,
    string DllName,
    string TypeName,
    string MethodName,
    int ParameterCount,
    BridgeInvocationScope Scope = BridgeInvocationScope.ServerOnly);

public enum BridgeInvocationScope
{
    ServerOnly,
    ClientOnly,
    ServerSettingsFallback
}

public enum BridgeRuntimeFeature
{
    ClientMapEventCompatibility,
    ClientRegistryLifecycleCompatibility,
    ClientMapEventPositionAuthority,
    ClientTroopUpgradeLoadRepair,
    ClientSetDisorganizedDiagnostic,
    ClientTroopRosterSequenceDiagnostic,
    ClientCharacterCreationLifecycleCompatibility,
    ServerRegistryLifecycleCompatibility,
    ServerPopulationControl,
    ServerFailedIdCompatibility,
    ClientDeterministicBattleSceneProjection,
    ServerEurope1700ShieldProductionSuppression
}

public sealed record BridgeContentExclusion(
    string ModuleId,
    string RelativePath);

public sealed record BridgeServerFileRedirect(
    string ModuleId,
    string RelativePath,
    string Sha256);

public sealed record BridgeServerXmlOverlay(
    string ModuleId,
    string RelativePath,
    string SourceSha256,
    string OverlayRelativePath,
    string OverlaySha256,
    byte[] Content);

public sealed record BridgeGameVersionCompatibility(
    string ServerVersion,
    string ClientVersion,
    string ServerRuntimeVersion,
    string ClientRuntimeVersion);

public sealed record BridgeClientAssemblyResolve(
    string ModuleId,
    string RelativePath,
    string Sha256,
    string SourcePath);

public sealed record BridgeServerMapTerrainSize(
    string ModuleId,
    string RelativePath,
    string Sha256,
    float Width,
    float Height,
    string LoaderAssemblyName,
    string LoaderAssemblySha256,
    string TargetAssemblyName,
    string TargetAssemblySha256);

public sealed record BridgeBattleSceneCatalogContract(
    string TargetModuleId,
    string TargetVersion,
    string BaseModuleId,
    string BaseVersion,
    string RelativePath,
    string Sha256,
    byte[] Content);

public sealed record CoopBridgePackage(
    string ModuleId,
    string Version,
    byte[] Manifest,
    byte[] Configuration,
    byte[] Assembly,
    byte[] ClientAssembly,
    byte[] ClientPackageZip,
    IReadOnlyList<BridgeDisabledSubModule> DisabledSubModules,
    IReadOnlyList<BridgeClientOnlySubModule> ClientOnlySubModules,
    IReadOnlyList<BridgeModuleRecord> ModuleRecords,
    IReadOnlyList<BridgeContentRecord> ContentRecords,
    IReadOnlyList<BridgeAuthorityRule> AuthorityRules,
    IReadOnlyList<BridgeContentExclusion> ContentExclusions,
    IReadOnlyList<BridgeServerFileRedirect> ServerFileRedirects,
    IReadOnlyList<BridgeServerXmlOverlay> ServerXmlOverlays,
    BridgeGameVersionCompatibility? GameVersionCompatibility,
    IReadOnlyList<BridgeClientAssemblyResolve> ClientAssemblyResolves,
    IReadOnlyList<BridgeServerMapTerrainSize> ServerMapTerrainSizes,
    IReadOnlyList<BridgeRuntimeFeature> RuntimeFeatures,
    BridgeBattleSceneCatalogContract? BattleSceneCatalogContract);
