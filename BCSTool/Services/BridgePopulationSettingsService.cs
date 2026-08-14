using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Validates an installed generated bridge and persists its server-only
/// population settings without changing the bridge/client package identity.
/// </summary>
public sealed class BridgePopulationSettingsService
{
    public const string SettingsFileName = "bcs-coop-bridge-population.config";
    private const string HeaderV1 = "BCS-BRIDGE-POPULATION|1";
    private const string HeaderV2 = "BCS-BRIDGE-POPULATION|2";
    private const string NativeValue = "NATIVE";
    private const long MaximumSettingsBytes = 64 * 1024;
    private const long MaximumBridgeConfigurationBytes = 4 * 1024 * 1024;
    private const long MaximumSettlementDataBytes = 16 * 1024 * 1024;
    private const long MaximumSettlementCharacters = 16 * 1024 * 1024;
    private const int MaximumSettlementCount = 100_000;
    private const int MaximumPartyLimit = 1_000_000;
    private const int MaximumAutomaticNpcCaravansPerTown = 10;
    private const double MaximumBanditMultiplier = 10.0;
    private static readonly UTF8Encoding StrictUtf8NoBom = new(false, true);
    private readonly string _serverRoot;

    public BridgePopulationSettingsService(string serverRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        _serverRoot = Path.GetFullPath(serverRoot);
    }

    public string SettingsPath => Path.Combine(_serverRoot, SettingsFileName);

    public BridgePopulationSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new BridgePopulationSettings();

        ValidateRegularFile(SettingsPath, _serverRoot, MaximumSettingsBytes, "Bridge population settings");
        var bytes = File.ReadAllBytes(SettingsPath);
        string text;
        try
        {
            text = StrictUtf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Bridge population settings are not valid UTF-8.",
                exception);
        }

        var lines = SplitConfigurationLines(text);
        if (lines.Length == 4 && lines[0].Equals(HeaderV1, StringComparison.Ordinal))
        {
            return new BridgePopulationSettings
            {
                MaximumAutomaticCaravans = ParsePartyLimit(
                    lines[1],
                    "MAXIMUM_AUTOMATIC_CARAVANS"),
                AutomaticNpcCaravansPerTown =
                    BridgePopulationSettings.DefaultAutomaticNpcCaravansPerTown,
                MaximumActiveVillagerParties = ParsePartyLimit(
                    lines[2],
                    "MAXIMUM_ACTIVE_VILLAGER_PARTIES"),
                BanditPartiesAroundHideoutMultiplier = ParseMultiplier(
                    lines[3],
                    "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER")
            };
        }

        if (lines.Length == 5 && lines[0].Equals(HeaderV2, StringComparison.Ordinal))
        {
            return new BridgePopulationSettings
            {
                MaximumAutomaticCaravans = ParsePartyLimit(
                    lines[1],
                    "MAXIMUM_AUTOMATIC_CARAVANS"),
                AutomaticNpcCaravansPerTown = ParseAutomaticNpcCaravansPerTown(
                    lines[2],
                    "AUTOMATIC_NPC_CARAVANS_PER_TOWN"),
                MaximumActiveVillagerParties = ParsePartyLimit(
                    lines[3],
                    "MAXIMUM_ACTIVE_VILLAGER_PARTIES"),
                BanditPartiesAroundHideoutMultiplier = ParseMultiplier(
                    lines[4],
                    "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER")
            };
        }

        throw new InvalidDataException("Unsupported bridge population settings format.");
    }

    public void Save(BridgePopulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        if (!Directory.Exists(_serverRoot))
            throw new DirectoryNotFoundException(
                $"Dedicated-server directory was not found: {_serverRoot}");
        if ((File.GetAttributes(_serverRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Linked dedicated-server directories cannot contain bridge settings: {_serverRoot}");
        if (File.Exists(SettingsPath) &&
            (File.GetAttributes(SettingsPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Linked bridge population settings cannot be replaced: {SettingsPath}");
        }

        var bytes = StrictUtf8NoBom.GetBytes(Serialize(settings));
        if (bytes.Length > MaximumSettingsBytes)
            throw new InvalidDataException("Bridge population settings exceed the 64 KiB limit.");

        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, SettingsPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal string CreateSnapshot(BridgePopulationSettings settings)
    {
        Validate(settings);
        return Serialize(settings);
    }

    public BridgePopulationSettingsTarget ValidateSelectedBridge(
        BannerlordModule selected,
        IReadOnlyList<BannerlordModule> modules)
    {
        return ValidateSelectedBridge(selected, modules, includePopulationGuide: true);
    }

    private BridgePopulationSettingsTarget ValidateSelectedBridge(
        BannerlordModule selected,
        IReadOnlyList<BannerlordModule> modules,
        bool includePopulationGuide)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(modules);

        if (!selected.IsInstalled || !selected.Enabled || !selected.IsServerCompatible)
            throw new InvalidDataException("Select the enabled generated BCS Coop bridge.");
        if (!TryGetGeneratedIdentity(selected.Id, out var declaredIdentity))
            throw new InvalidDataException("The selected module is not a generated BCS Coop bridge.");
        if (!selected.Version.Equals(
                CoopBridgePackageBuilder.BridgeVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The selected bridge is not current. Run Prepare / Install Bridge first.");
        }

        var enabledGenerated = modules
            .Where(module => module.Enabled && TryGetGeneratedIdentity(module.Id, out _))
            .ToArray();
        if (enabledGenerated.Length != 1 ||
            !enabledGenerated[0].Id.Equals(selected.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Exactly one generated BCS Coop bridge must be enabled.");
        }

        if (!selected.Dependencies.Contains("Coop", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected bridge does not depend on Coop.");
        var overhaulDependencies = selected.Dependencies
            .Where(id => !id.Equals("Coop", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var overhauls = modules
            .Where(module =>
                module.Enabled &&
                overhaulDependencies.Contains(module.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (overhaulDependencies.Length != 1 || overhauls.Length != 1)
        {
            throw new InvalidDataException(
                "The selected bridge must have exactly one enabled overhaul dependency.");
        }
        var overhaul = overhauls[0];

        var modulesRoot = Path.GetFullPath(Path.Combine(_serverRoot, "engine", "Modules"));
        var bridgeRoot = Path.GetFullPath(selected.Path);
        if (!Directory.Exists(bridgeRoot) ||
            Directory.GetParent(bridgeRoot)?.FullName.Equals(
                modulesRoot,
                StringComparison.OrdinalIgnoreCase) != true ||
            !Path.GetFileName(bridgeRoot).Equals(selected.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected bridge is not a direct child of the dedicated-server Modules directory.");
        }
        ValidateDirectoryChain(bridgeRoot, modulesRoot);

        var configurationPath = Path.Combine(bridgeRoot, "bcs-coop-bridge.config");
        var serverAssemblyPath = Path.Combine(
            bridgeRoot,
            "bin",
            "Win64_Shipping_Server",
            "BCS.CoopBridge.dll");
        var clientAssemblyPath = Path.Combine(
            bridgeRoot,
            "bin",
            "Win64_Shipping_Client",
            "BCS.CoopBridge.dll");
        ValidateRegularFile(
            configurationPath,
            bridgeRoot,
            MaximumBridgeConfigurationBytes,
            "Bridge configuration");
        ValidateRegularFile(serverAssemblyPath, bridgeRoot, long.MaxValue, "Server bridge assembly");
        ValidateRegularFile(clientAssemblyPath, bridgeRoot, long.MaxValue, "Client bridge assembly");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, configurationPath);
        AppendFile(hash, serverAssemblyPath);
        AppendFile(hash, clientAssemblyPath);
        var actualIdentity = Convert.ToHexString(hash.GetHashAndReset())[..24].ToLowerInvariant();
        if (!actualIdentity.Equals(declaredIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected bridge configuration and assemblies do not match its generated module ID.");
        }

        BridgePopulationGuide? guide = null;
        var guideMessage = string.Empty;
        if (includePopulationGuide)
            (guide, guideMessage) = TryCalculatePopulationGuide(overhaul, modulesRoot);

        return new BridgePopulationSettingsTarget(
            selected.Id,
            selected.Name,
            overhaul.Id,
            overhaul.Name,
            guide,
            guideMessage);
    }

    internal bool TryValidateSelectedBridge(
        BannerlordModule? selected,
        IReadOnlyList<BannerlordModule> modules,
        out BridgePopulationSettingsTarget? target)
    {
        target = null;
        if (selected is null)
            return false;

        try
        {
            target = ValidateSelectedBridge(
                selected,
                modules,
                includePopulationGuide: false);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Validate(BridgePopulationSettings settings)
    {
        ValidatePartyLimit(settings.MaximumAutomaticCaravans, "Maximum automatic caravans");
        if (settings.AutomaticNpcCaravansPerTown is < 0 or > MaximumAutomaticNpcCaravansPerTown)
        {
            throw new InvalidDataException(
                "Automatic NPC caravans per town must be between 0 and 10.");
        }
        ValidatePartyLimit(
            settings.MaximumActiveVillagerParties,
            "Maximum active villager parties");
        if (!double.IsFinite(settings.BanditPartiesAroundHideoutMultiplier) ||
            settings.BanditPartiesAroundHideoutMultiplier < 0.0 ||
            settings.BanditPartiesAroundHideoutMultiplier > MaximumBanditMultiplier)
        {
            throw new InvalidDataException(
                "Bandit parties around hideouts multiplier must be between 0 and 10.");
        }
    }

    private static (BridgePopulationGuide? Guide, string Message) TryCalculatePopulationGuide(
        BannerlordModule overhaul,
        string modulesRoot)
    {
        var recipe = CompatibilityRecipeRegistry.FindByRootModule(overhaul.Id);
        if (recipe?.SupportsPopulationGuide != true)
        {
            return (
                null,
                $"No calculated population guide is supplied for {overhaul.Name}.");
        }

        try
        {
            var moduleRoot = Path.GetFullPath(overhaul.Path);
            if (!Directory.Exists(moduleRoot) ||
                Directory.GetParent(moduleRoot)?.FullName.Equals(
                    modulesRoot,
                    StringComparison.OrdinalIgnoreCase) != true)
            {
                throw new InvalidDataException(
                    "The overhaul is not a direct child of the dedicated-server Modules directory.");
            }
            ValidateDirectoryChain(moduleRoot, modulesRoot);

            var settlementPath = Path.Combine(moduleRoot, "ModuleData", "settlements.xml");
            ValidateRegularFile(
                settlementPath,
                moduleRoot,
                MaximumSettlementDataBytes,
                "Overhaul settlement data");

            var guide = ReadPopulationGuide(settlementPath);
            return (
                guide,
                $"Calculated from {overhaul.Name} ModuleData{Path.DirectorySeparatorChar}settlements.xml.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
        {
            return (
                null,
                $"The population guide could not be calculated from {overhaul.Name}: {exception.Message}");
        }
    }

    private static BridgePopulationGuide ReadPopulationGuide(string settlementPath)
    {
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(settlementPath, new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Prohibit,
                   XmlResolver = null,
                   MaxCharactersInDocument = MaximumSettlementCharacters
               }))
        {
            document.Load(reader);
        }

        var root = document.DocumentElement;
        if (root is null ||
            !root.LocalName.Equals("Settlements", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Settlement data does not contain a Settlements root.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalSettlements = 0;
        var towns = 0;
        var castles = 0;
        var villages = 0;
        foreach (var settlement in root.ChildNodes
                     .OfType<XmlElement>()
                     .Where(element => element.LocalName.Equals(
                         "Settlement",
                         StringComparison.OrdinalIgnoreCase)))
        {
            totalSettlements++;
            if (totalSettlements > MaximumSettlementCount)
                throw new InvalidDataException("Settlement data exceeds the 100000-entry limit.");

            var id = settlement.GetAttribute("id").Trim();
            if (id.Length == 0 || !ids.Add(id))
                throw new InvalidDataException("Settlement data contains a missing or duplicate ID.");

            var componentContainers = settlement.ChildNodes
                .OfType<XmlElement>()
                .Where(element => element.LocalName.Equals(
                    "Components",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (componentContainers.Length > 1)
            {
                throw new InvalidDataException(
                    $"Settlement '{id}' has multiple Components containers.");
            }
            if (componentContainers.Length == 0)
                continue;
            var components = componentContainers[0];

            var townComponents = components.ChildNodes
                .OfType<XmlElement>()
                .Where(element => element.LocalName.Equals("Town", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var villageComponents = components.ChildNodes
                .OfType<XmlElement>()
                .Where(element => element.LocalName.Equals("Village", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (townComponents.Length > 1 ||
                villageComponents.Length > 1 ||
                (townComponents.Length == 1 && villageComponents.Length == 1))
            {
                throw new InvalidDataException(
                    $"Settlement '{id}' has an ambiguous town/village component layout.");
            }

            if (townComponents.Length == 1)
            {
                var rawCastle = townComponents[0].GetAttribute("is_castle").Trim();
                if (rawCastle.Length > 0 && !bool.TryParse(rawCastle, out _))
                {
                    throw new InvalidDataException(
                        $"Settlement '{id}' has an invalid is_castle value.");
                }

                if (bool.TryParse(rawCastle, out var isCastle) && isCastle)
                    castles++;
                else
                    towns++;
            }
            else if (villageComponents.Length == 1)
            {
                villages++;
            }
        }

        if (totalSettlements == 0 || towns == 0 || villages == 0)
        {
            throw new InvalidDataException(
                "Settlement data contains no usable towns or villages.");
        }

        return new BridgePopulationGuide(
            TotalSettlements: totalSettlements,
            TownCount: towns,
            CastleCount: castles,
            VillageCount: villages,
            CalculatedAutomaticCaravanBaseline: checked(towns * 2),
            CalculatedActiveVillagerPartyCeiling: villages);
    }

    private static string Serialize(BridgePopulationSettings settings) =>
        HeaderV2 + "\n" +
        "MAXIMUM_AUTOMATIC_CARAVANS|" + FormatPartyLimit(settings.MaximumAutomaticCaravans) + "\n" +
        "AUTOMATIC_NPC_CARAVANS_PER_TOWN|" +
        settings.AutomaticNpcCaravansPerTown.ToString(CultureInfo.InvariantCulture) + "\n" +
        "MAXIMUM_ACTIVE_VILLAGER_PARTIES|" + FormatPartyLimit(settings.MaximumActiveVillagerParties) + "\n" +
        "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|" +
        settings.BanditPartiesAroundHideoutMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n";

    private static string[] SplitConfigurationLines(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        return lines.Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
    }

    private static int? ParsePartyLimit(string line, string key)
    {
        var value = ReadValue(line, key);
        if (value.Equals(NativeValue, StringComparison.Ordinal))
            return null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 0 or > MaximumPartyLimit)
        {
            throw new InvalidDataException($"{key} must be NATIVE or an integer from 0 to 1000000.");
        }
        return parsed;
    }

    private static int ParseAutomaticNpcCaravansPerTown(string line, string key)
    {
        var value = ReadValue(line, key);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 0 or > MaximumAutomaticNpcCaravansPerTown)
        {
            throw new InvalidDataException($"{key} must be an integer from 0 to 10.");
        }
        return parsed;
    }

    private static double ParseMultiplier(string line, string key)
    {
        var value = ReadValue(line, key);
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < 0.0 ||
            parsed > MaximumBanditMultiplier)
        {
            throw new InvalidDataException($"{key} must be a finite number from 0 to 10.");
        }
        return parsed;
    }

    private static string ReadValue(string line, string key)
    {
        var prefix = key + "|";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length == prefix.Length)
            throw new InvalidDataException($"Bridge population settings are missing {key}.");
        return line[prefix.Length..];
    }

    private static string FormatPartyLimit(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? NativeValue;

    private static void ValidatePartyLimit(int? value, string name)
    {
        if (value is < 0 or > MaximumPartyLimit)
            throw new InvalidDataException($"{name} must be between 0 and 1000000.");
    }

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

    private static void ValidateDirectoryChain(string directory, string expectedParent)
    {
        var current = new DirectoryInfo(directory);
        var parent = Path.GetFullPath(expectedParent);
        while (true)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked bridge directories are not supported: {current.FullName}");
            if (current.Parent?.FullName.Equals(parent, StringComparison.OrdinalIgnoreCase) == true)
                return;
            current = current.Parent ?? throw new InvalidDataException(
                "Bridge directory escaped the dedicated-server Modules directory.");
        }
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

        var current = info.Directory;
        while (current is not null &&
               !current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{description} uses a linked directory: {current.FullName}");
            current = current.Parent;
        }
        if (current is null)
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
}

public sealed record BridgePopulationSettingsTarget(
    string BridgeModuleId,
    string BridgeDisplayName,
    string OverhaulModuleId,
    string OverhaulDisplayName,
    BridgePopulationGuide? PopulationGuide,
    string PopulationGuideMessage);

public sealed record BridgePopulationGuide(
    int TotalSettlements,
    int TownCount,
    int CastleCount,
    int VillageCount,
    int CalculatedAutomaticCaravanBaseline,
    int CalculatedActiveVillagerPartyCeiling);
