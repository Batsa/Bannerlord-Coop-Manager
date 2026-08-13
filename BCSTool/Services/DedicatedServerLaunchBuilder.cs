using System.IO;
using System.Text.Json;

namespace BCSTool.Services;

/// <summary>
/// Converts BCS Tool's saved module profile into Bannerlord's real engine
/// module token. BannerlordCoopServer.exe currently starts a fixed vanilla
/// module set, so enabled community modules must be passed to the engine
/// directly.
/// </summary>
public sealed class DedicatedServerLaunchBuilder
{
    private const string CoopServerPort = "4200";
    private const string RuntimeBootstrapFileName = "BCSTool.RuntimeBootstrap.dll";
    internal const string ManagedDependencyProfileFileName =
        "bcs-managed-dependencies.json";

    private static readonly HashSet<string> RequiredModules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Native",
            "SandBoxCore",
            "Sandbox",
            "Coop",
            "DedicatedServer.Windows"
        };

    private static readonly HashSet<string> NonServerOfficialModules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CustomBattle",
            "StoryMode",
            "BirthAndDeath"
        };

    private readonly ModuleScanner _scanner;
    private readonly string _runtimeBootstrapPath;

    public DedicatedServerLaunchBuilder(
        ModuleScanner scanner,
        string? runtimeBootstrapPath = null)
    {
        _scanner = scanner;
        _runtimeBootstrapPath = Path.GetFullPath(
            runtimeBootstrapPath ??
            Path.Combine(AppContext.BaseDirectory, RuntimeBootstrapFileName));
    }

    public ServerLaunchPlan Build(
        string serverExecutablePath,
        string fallbackWorkingDirectory)
    {
        var executable = Path.GetFullPath(serverExecutablePath);
        var serverRoot = Path.GetDirectoryName(executable)
                         ?? throw new InvalidDataException(
                             "Server executable has no parent directory.");
        var profilePath = Path.Combine(serverRoot, "bcs-server-modules.json");

        if (!File.Exists(profilePath))
        {
            return new ServerLaunchPlan(
                executable,
                Path.GetFullPath(fallbackWorkingDirectory),
                Array.Empty<string>(),
                new Dictionary<string, string?>(),
                Array.Empty<string>(),
                UsesManagedModuleProfile: false);
        }

        var manager = new ModuleManager(executable, _scanner);
        var modules = manager.Load();
        var active = modules
            .Where(module => module.Enabled && module.IsInstalled && module.IsServerCompatible)
            .ToList();

        NormalizeDedicatedServerHost(active);
        NormalizeBridgesLast(active);
        ValidateActiveModules(active);

        var engineRoot = Path.Combine(serverRoot, "engine");
        var serverBin = Path.Combine(engineRoot, "bin", "Win64_Shipping_Server");
        var dotnetPath = Path.Combine(engineRoot, "dotnet", "dotnet.exe");
        var starterPath = Path.Combine(serverBin, "TaleWorlds.Starter.DotNetCore.dll");

        RequireFile(dotnetPath, "bundled .NET host");
        RequireFile(starterPath, "Bannerlord server entry point");
        RequireFile(_runtimeBootstrapPath, "BCS Tool runtime bootstrap");

        var ids = active.Select(module => module.Id).ToArray();
        var token = "_MODULES_" + string.Concat(ids.Select(id => "*" + id)) + "*_MODULES_";
        var searchDirectories = BuildManagedSearchDirectories(serverBin, active).ToList();
        AddManagedDependencyDirectories(serverRoot, searchDirectories);
        var userDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "CoopData",
            "DedicatedServer");
        var coopDataDirectory = Directory.GetParent(userDirectory)?.FullName
                                ?? throw new InvalidDataException(
                                    "Could not resolve the CoopData directory.");

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = Path.Combine(engineRoot, "dotnet"),
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_STARTUP_HOOKS"] = _runtimeBootstrapPath,
            ["BANNERLORD_USER_DIR"] = userDirectory,
            ["COOP_DATA_DIR"] = coopDataDirectory,
            ["BCSTOOL_DS_MANAGED_SEARCH_DIRECTORIES"] =
                string.Join(Path.PathSeparator, searchDirectories)
        };

        return new ServerLaunchPlan(
            dotnetPath,
            serverBin,
            [
                Path.GetFileName(starterPath),
                token,
                "/dedicatedcustomserver",
                CoopServerPort,
                "EU",
                "0"
            ],
            environment,
            ids,
            UsesManagedModuleProfile: true);
    }

    private static void NormalizeDedicatedServerHost(List<Models.BannerlordModule> active)
    {
        var hostIndex = active.FindIndex(module =>
            module.Id.Equals("DedicatedServer.Windows", StringComparison.OrdinalIgnoreCase));
        var nativeIndex = active.FindIndex(module =>
            module.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));

        if (hostIndex < 0 || nativeIndex < 0)
            return;

        var host = active[hostIndex];
        active.RemoveAt(hostIndex);
        nativeIndex = active.FindIndex(module =>
            module.Id.Equals("Native", StringComparison.OrdinalIgnoreCase));
        active.Insert(nativeIndex + 1, host);
    }

    private static void NormalizeBridgesLast(List<Models.BannerlordModule> active)
    {
        var bridges = active
            .Where(module => module.Id.StartsWith(
                CoopBridgePackageBuilder.BridgeIdPrefix,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        active.RemoveAll(module => bridges.Contains(module));
        active.AddRange(bridges);
    }

    private static void ValidateActiveModules(IReadOnlyList<Models.BannerlordModule> active)
    {
        var activeById = active.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var indexById = active
            .Select((module, index) => (module.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        foreach (var required in RequiredModules)
        {
            if (!activeById.ContainsKey(required))
                throw new InvalidDataException(
                    $"Required dedicated-server module is missing or disabled: {required}");
        }

        var firstBridgeIndex = active.ToList().FindIndex(module =>
            module.Id.StartsWith(
                CoopBridgePackageBuilder.BridgeIdPrefix,
                StringComparison.OrdinalIgnoreCase));
        if (firstBridgeIndex >= 0 &&
            active.Skip(firstBridgeIndex).Any(module =>
                !module.Id.StartsWith(
                    CoopBridgePackageBuilder.BridgeIdPrefix,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Generated BCS bridge modules must be the final active modules.");
        }

        foreach (var module in active)
        {
            ValidateModuleId(module.Id);

            foreach (var dependency in module.Dependencies)
            {
                if (NonServerOfficialModules.Contains(dependency))
                    continue;
                if (!activeById.ContainsKey(dependency))
                    throw new InvalidDataException(
                        $"Module '{module.Id}' requires missing or disabled module '{dependency}'.");
            }

            foreach (var dependency in module.MustLoadAfter)
            {
                if (indexById.TryGetValue(dependency, out var dependencyIndex) &&
                    dependencyIndex > indexById[module.Id])
                {
                    throw new InvalidDataException(
                        $"Module '{dependency}' must load before '{module.Id}'.");
                }
            }

            foreach (var laterModule in module.MustLoadBefore)
            {
                if (indexById.TryGetValue(laterModule, out var laterIndex) &&
                    laterIndex < indexById[module.Id])
                {
                    throw new InvalidDataException(
                        $"Module '{module.Id}' must load before '{laterModule}'.");
                }
            }

            foreach (var incompatible in module.IncompatibleModules)
            {
                if (activeById.ContainsKey(incompatible))
                    throw new InvalidDataException(
                        $"Module '{module.Id}' is incompatible with active module '{incompatible}'.");
            }
        }
    }

    private static IReadOnlyList<string> BuildManagedSearchDirectories(
        string serverBin,
        IEnumerable<Models.BannerlordModule> active)
    {
        var directories = new List<string> { Path.GetFullPath(serverBin) };
        foreach (var module in active)
        {
            foreach (var relativeBin in new[]
                     {
                         Path.Combine("bin", "Win64_Shipping_Server"),
                         Path.Combine("bin", "Win64_Shipping_Client"),
                         Path.Combine("bin", "Gaming.Desktop.x64_Shipping_Client")
                     })
            {
                var candidate = Path.Combine(module.Path, relativeBin);
                if (Directory.Exists(candidate) &&
                    !directories.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    directories.Add(Path.GetFullPath(candidate));
                }
            }
        }

        return directories;
    }

    private static void AddManagedDependencyDirectories(
        string serverRoot,
        ICollection<string> directories)
    {
        var profilePath = Path.Combine(serverRoot, ManagedDependencyProfileFileName);
        if (!File.Exists(profilePath))
            return;

        ManagedDependencyProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<ManagedDependencyProfile>(
                          File.ReadAllText(profilePath),
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidDataException(
                          $"Managed dependency profile is empty: {profilePath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Managed dependency profile is invalid: {profilePath}",
                exception);
        }

        if (profile.SchemaVersion != 1 || profile.Directories.Count == 0)
        {
            throw new InvalidDataException(
                $"Managed dependency profile has an unsupported schema or no directories: {profilePath}");
        }

        foreach (var entry in profile.Directories)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) ||
                !Path.IsPathFullyQualified(entry.Path))
            {
                throw new InvalidDataException(
                    "Managed dependency directory must be an absolute path.");
            }

            var directory = Path.GetFullPath(entry.Path);
            if (!Directory.Exists(directory) || entry.RequiredFiles.Count == 0)
            {
                throw new InvalidDataException(
                    $"Managed dependency directory is missing or has no required files: {directory}");
            }

            foreach (var required in entry.RequiredFiles)
            {
                if (string.IsNullOrWhiteSpace(required.Name) ||
                    Path.IsPathRooted(required.Name) ||
                    required.Name.Contains(Path.DirectorySeparatorChar) ||
                    required.Name.Contains(Path.AltDirectorySeparatorChar))
                {
                    throw new InvalidDataException(
                        $"Managed dependency file entry is unsafe: {required.Name}");
                }

                var file = Path.Combine(directory, required.Name);
                if (!File.Exists(file))
                {
                    throw new FileNotFoundException(
                        $"Required managed dependency is missing: {file}",
                        file);
                }
            }

            if (!directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                directories.Add(directory);
        }
    }

    private static void ValidateModuleId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id.Contains('*') ||
            id.Contains("_MODULES_", StringComparison.OrdinalIgnoreCase) ||
            id.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Module ID cannot be represented safely in the engine token: '{id}'");
        }
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Missing {description}: {path}", path);
    }

    private sealed class ManagedDependencyProfile
    {
        public int SchemaVersion { get; set; }
        public List<ManagedDependencyDirectory> Directories { get; set; } = [];
    }

    private sealed class ManagedDependencyDirectory
    {
        public string Path { get; set; } = string.Empty;
        public List<ManagedDependencyFile> RequiredFiles { get; set; } = [];
    }

    private sealed class ManagedDependencyFile
    {
        public string Name { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }
}
