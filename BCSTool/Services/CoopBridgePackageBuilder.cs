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
    public const string BridgeVersion = "v0.6.60";
    private const string ProjectUrl =
        "https://github.com/Batsa/Bannerlord-Coop-Manager";

    private const string ServerBridgeAssemblyResource =
        "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Server.dll";
    private const string ServerBridgeAssemblyHash =
        "6156313216D8E342E2EA63AEB8B47706922B23D375146AD01F303C5E62091591";
    private const string ClientBridgeAssemblyResource =
        "BCSTool.Assets.CoopBridge.BCS.CoopBridge.Client.dll";
    private const string LicenseResource = "BCSTool.LICENSE";
    private const string NoticeResource = "BCSTool.NOTICE.md";
    private const string ClientBridgeAssemblyHash =
        "1AB981E4D4EB13E6C16E298A9F4CAEDDF474ED411220147078F8F889487D21FF";
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
        IReadOnlyList<BridgeServerMapTerrainSize>? serverMapTerrainSizes = null)
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
        var records = new List<BridgeModuleRecord>();
        var contentRecords = new List<BridgeContentRecord>();
        foreach (var module in modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            if (!module.IsInstalled || string.IsNullOrWhiteSpace(module.Path))
                throw new InvalidDataException($"Bridge module is not installed: {module.Id}");
            if (module.Id.StartsWith(BridgeIdPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A generated BCS bridge cannot depend on another generated bridge.");

            ValidateModuleDirectoryChain(module.Path, module.Path, "module root");

            var declaredDlls = ReadDeclaredDlls(Path.Combine(module.Path, "SubModule.xml"));
            var preferClient = projectedModuleIds?.Contains(
                module.Id,
                StringComparer.OrdinalIgnoreCase) == true;
            var assemblies = module.Id.Equals("Coop", StringComparison.OrdinalIgnoreCase)
                ? EnumerateReleasedCoopAssemblies(module.Path, preferClient)
                : ResolveDeclaredAssemblies(module.Path, preferClient, declaredDlls);
            foreach (var declaredDll in declaredDlls)
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
                    string.Empty));
            }
            contentRecords.Add(new BridgeContentRecord(
                module.Id,
                string.Empty));
        }

        authorityRules ??= Array.Empty<BridgeAuthorityRule>();
        ValidateAuthorityRules(authorityRules, modules, records);
        serverFileRedirects ??= Array.Empty<BridgeServerFileRedirect>();
        ValidateServerFileRedirects(serverFileRedirects, modules);
        serverXmlOverlays ??= Array.Empty<BridgeServerXmlOverlay>();
        ValidateServerXmlOverlays(serverXmlOverlays, modules);
        ValidateGameVersionCompatibility(gameVersionCompatibility);
        clientAssemblyResolves ??= Array.Empty<BridgeClientAssemblyResolve>();
        ValidateClientAssemblyResolves(clientAssemblyResolves);
        serverMapTerrainSizes ??= Array.Empty<BridgeServerMapTerrainSize>();
        ValidateServerMapTerrainSizes(serverMapTerrainSizes, modules);
        var configuration = BuildConfiguration(
            records,
            contentRecords,
            authorityRules,
            contentExclusions,
            serverFileRedirects,
            serverXmlOverlays,
            gameVersionCompatibility,
            clientAssemblyResolves,
            serverMapTerrainSizes);
        var serverAssembly = ReadBridgeAssembly(
            ServerBridgeAssemblyResource,
            ServerBridgeAssemblyHash);
        var clientAssembly = ReadBridgeAssembly(
            ClientBridgeAssemblyResource,
            ClientBridgeAssemblyHash);
        var identityPayload = new byte[
            configuration.Length + serverAssembly.Length + clientAssembly.Length];
        Buffer.BlockCopy(configuration, 0, identityPayload, 0, configuration.Length);
        Buffer.BlockCopy(
            serverAssembly,
            0,
            identityPayload,
            configuration.Length,
            serverAssembly.Length);
        Buffer.BlockCopy(
            clientAssembly,
            0,
            identityPayload,
            configuration.Length + serverAssembly.Length,
            clientAssembly.Length);
        var bridgeId = BridgeIdPrefix + Hash(identityPayload)[..24].ToLowerInvariant();
        var manifest = BuildManifest(bridgeId, modules);
        var readme = Utf8NoBom.GetBytes(
            "BCS Tool generated Coop bridge package\r\n" +
            $"Module: {bridgeId} {BridgeVersion}\r\n" +
            "\r\n" +
            "Copy the Modules folder into the Bannerlord client installation.\r\n" +
            "Enable this bridge build and the same module IDs and versions on every client.\r\n" +
            "The package validates module versions, required files, safe paths, and required method signatures.\r\n" +
            $"Server-authority rules: {authorityRules.Count}.\r\n" +
            $"Allowed server-only visual files: {contentExclusions.Count}.\r\n" +
            $"Server file redirects: {serverFileRedirects.Count}.\r\n" +
            $"Server XML overlays: {serverXmlOverlays.Count}.\r\n" +
            "Campaign intervention: none.\r\n" +
            $"Bannerlord game-version compatibility: {gameVersionCompatibility is not null}.\r\n" +
            $"Client assembly resolvers: {clientAssemblyResolves.Count}.\r\n" +
            $"Server map terrain sizes: {serverMapTerrainSizes.Count}.\r\n" +
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
            notice);

        return new CoopBridgePackage(
            bridgeId,
            BridgeVersion,
            manifest,
            configuration,
            serverAssembly,
            clientAssembly,
            clientZip,
            records,
            contentRecords,
            authorityRules,
            contentExclusions,
            serverFileRedirects,
            serverXmlOverlays,
            gameVersionCompatibility,
            clientAssemblyResolves,
            serverMapTerrainSizes);
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
        IReadOnlyList<BridgeServerMapTerrainSize> serverMapTerrainSizes)
    {
        var builder = new StringBuilder("BCS-COOP-BRIDGE|1\n");
        if (gameVersionCompatibility is not null)
        {
            builder.Append("GAME_VERSION_COMPAT|")
                .Append(Encode(gameVersionCompatibility.ServerVersion)).Append('|')
                .Append(Encode(gameVersionCompatibility.ClientVersion)).Append('|')
                .Append(string.Empty).Append('|')
                .Append(string.Empty)
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
                .Append(string.Empty)
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

    private static void ValidateClientAssemblyResolves(
        IReadOnlyList<BridgeClientAssemblyResolve> resolvers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        IReadOnlyList<BridgeModuleRecord> records)
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
            rule.ServerVersion.Equals(rule.ClientVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Game-version compatibility requires two different non-empty Bannerlord versions.");
        }
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
        byte[] notice)
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
        var names = document.SelectNodes("/Module/SubModules/SubModule/DLLName")!
            .OfType<XmlElement>()
            .Select(element => element.GetAttribute("value"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in names)
        {
            if (!Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
                !Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Unsafe declared DLL name in {manifestPath}: {name}");
            }
        }
        return names;
    }

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
    string ServerRuntimeSha256,
    string ClientRuntimeSha256);

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

public sealed record CoopBridgePackage(
    string ModuleId,
    string Version,
    byte[] Manifest,
    byte[] Configuration,
    byte[] Assembly,
    byte[] ClientAssembly,
    byte[] ClientPackageZip,
    IReadOnlyList<BridgeModuleRecord> ModuleRecords,
    IReadOnlyList<BridgeContentRecord> ContentRecords,
    IReadOnlyList<BridgeAuthorityRule> AuthorityRules,
    IReadOnlyList<BridgeContentExclusion> ContentExclusions,
    IReadOnlyList<BridgeServerFileRedirect> ServerFileRedirects,
    IReadOnlyList<BridgeServerXmlOverlay> ServerXmlOverlays,
    BridgeGameVersionCompatibility? GameVersionCompatibility,
    IReadOnlyList<BridgeClientAssemblyResolve> ClientAssemblyResolves,
    IReadOnlyList<BridgeServerMapTerrainSize> ServerMapTerrainSizes);
