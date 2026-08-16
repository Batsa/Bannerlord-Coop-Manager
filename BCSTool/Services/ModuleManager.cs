using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Scans dedicated-server modules and persists Bannerlord Coop Manager's own reversible
/// enabled-state and load-order profile. No external mod manager is required.
/// </summary>
public sealed class ModuleManager
{
    private static readonly UTF8Encoding StrictUtf8NoBom = new(false, true);

    private static readonly HashSet<string> AlwaysRequiredModules =
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
    private readonly string _serverRoot;
    private readonly string _modulesDirectory;
    private readonly string _profilePath;

    private string? _profileHash;

    public ModuleManager(string serverExecutablePath, ModuleScanner scanner)
    {
        if (string.IsNullOrWhiteSpace(serverExecutablePath))
            throw new ArgumentException("Server executable path is required.", nameof(serverExecutablePath));

        _scanner = scanner;
        _serverRoot = Path.GetDirectoryName(Path.GetFullPath(serverExecutablePath))
                      ?? throw new ArgumentException(
                          "Server executable has no parent directory.",
                          nameof(serverExecutablePath));

        _modulesDirectory = Path.Combine(_serverRoot, "engine", "Modules");
        _profilePath = Path.Combine(_serverRoot, "bcs-server-modules.json");
    }

    public string ServerRoot => _serverRoot;
    public string ModulesDirectory => _modulesDirectory;
    public string ProfilePath => _profilePath;

    public IReadOnlyList<BannerlordModule> Load()
    {
        var scanned = _scanner.Scan(_modulesDirectory);
        var installedById = scanned.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var savedEntries = ReadProfileIfPresent();
        var orderedIds = savedEntries.Select(entry => entry.Id).ToList();

        foreach (var installed in scanned)
        {
            if (!orderedIds.Contains(installed.Id, StringComparer.OrdinalIgnoreCase))
                orderedIds.Add(installed.Id);
        }

        var savedById = savedEntries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var modules = new List<BannerlordModule>();

        foreach (var id in orderedIds)
        {
            if (!installedById.ContainsKey(id) &&
                savedById.TryGetValue(id, out var missingSaved) &&
                !missingSaved.Enabled &&
                id.StartsWith(
                    CoopBridgePackageBuilder.BridgeIdPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Deleted historical bridges remain as disabled profile entries
                // while the current installation backup owns those exact bytes.
                // Keep that rollback record without resurrecting a missing UI row.
                continue;
            }

            var required = AlwaysRequiredModules.Contains(id);
            var serverCompatible = !NonServerOfficialModules.Contains(id);

            BannerlordModule module;
            if (installedById.TryGetValue(id, out var installed))
            {
                module = CopyWithPolicy(installed, required, serverCompatible);
            }
            else
            {
                module = new BannerlordModule
                {
                    Name = id,
                    Id = id,
                    Version = string.Empty,
                    Path = string.Empty,
                    IsInstalled = false,
                    IsRequired = required,
                    IsServerCompatible = serverCompatible,
                    Dependencies = Array.Empty<string>(),
                    MustLoadAfter = Array.Empty<string>(),
                    MustLoadBefore = Array.Empty<string>(),
                    IncompatibleModules = Array.Empty<string>()
                };
            }

            var enabled = required ||
                          (serverCompatible &&
                           savedById.TryGetValue(id, out var saved) &&
                           saved.Enabled);
            module.SetInitialEnabled(enabled);
            modules.Add(module);
        }

        return modules;
    }

    public void Save(IReadOnlyList<BannerlordModule> modules)
    {
        EnsureProfileUnchanged();

        var ids = modules.Select(module => module.Id).ToArray();
        var duplicate = ids
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Cannot save duplicate module ID: {duplicate.Key}");

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('\r') || id.Contains('\n'))
                throw new InvalidDataException($"Unsafe module ID: '{id}'");
        }

        var profile = new ModuleProfile
        {
            SchemaVersion = 1,
            Modules = modules
                .Select(module => new ModuleEntry
                {
                    Id = module.Id,
                    Enabled = module.Enabled
                })
                .ToList()
        };

        var bytes = StrictUtf8NoBom.GetBytes(
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);

        ReplaceSafely(bytes);
        _profileHash = Hash(File.ReadAllBytes(_profilePath));
    }

    internal void EnsureProfileStillCurrent() => EnsureProfileUnchanged();

    private IReadOnlyList<ModuleEntry> ReadProfileIfPresent()
    {
        if (!File.Exists(_profilePath))
        {
            _profileHash = null;
            return Array.Empty<ModuleEntry>();
        }

        var bytes = File.ReadAllBytes(_profilePath);
        _profileHash = Hash(bytes);

        var profile = JsonSerializer.Deserialize<ModuleProfile>(
                          StrictUtf8NoBom.GetString(bytes),
                          new JsonSerializerOptions
                          {
                              PropertyNameCaseInsensitive = false,
                              AllowTrailingCommas = false,
                              ReadCommentHandling = JsonCommentHandling.Disallow,
                              MaxDepth = 16
                          })
                      ?? throw new InvalidDataException("BCS module profile is empty.");

        if (profile.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Unsupported BCS module profile schema: {profile.SchemaVersion}");

        var entries = profile.Modules ?? throw new InvalidDataException(
            "BCS module profile must contain a modules array.");
        var duplicate = entries
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Duplicate module ID in BCS profile: {duplicate.Key}");
        if (entries.Any(entry => string.IsNullOrWhiteSpace(entry.Id)))
            throw new InvalidDataException("BCS module profile contains an empty module ID.");

        return entries;
    }

    private void EnsureProfileUnchanged()
    {
        if (_profileHash is null)
        {
            if (File.Exists(_profilePath))
                throw new IOException("BCS module profile was created after the last scan. Rescan before saving.");
            return;
        }

        if (!File.Exists(_profilePath) ||
            !Hash(File.ReadAllBytes(_profilePath)).Equals(_profileHash, StringComparison.Ordinal))
        {
            throw new IOException("BCS module profile changed after the last scan. Rescan before saving.");
        }
    }

    private void ReplaceSafely(byte[] bytes)
    {
        var temporary = Path.Combine(
            _serverRoot,
            $".{Path.GetFileName(_profilePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var backup = _profilePath + ".bak";
        File.WriteAllBytes(temporary, bytes);

        try
        {
            if (File.Exists(_profilePath))
                File.Replace(temporary, _profilePath, backup, true);
            else
                File.Move(temporary, _profilePath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Preserve the original save failure; the uniquely named temp file is harmless.
            }
        }
    }

    private static BannerlordModule CopyWithPolicy(
        BannerlordModule source,
        bool required,
        bool serverCompatible) =>
        new()
        {
            Name = source.Name,
            Id = source.Id,
            Version = source.Version,
            Path = source.Path,
            IsInstalled = true,
            IsRequired = required,
            IsServerCompatible = serverCompatible,
            Dependencies = source.Dependencies,
            MustLoadAfter = source.MustLoadAfter,
            MustLoadBefore = source.MustLoadBefore,
            IncompatibleModules = source.IncompatibleModules
        };

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class ModuleProfile
    {
        public int SchemaVersion { get; set; }
        public List<ModuleEntry>? Modules { get; set; }
    }

    private sealed class ModuleEntry
    {
        public string Id { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }
}
