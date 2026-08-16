using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Validates an installed generated bridge and persists strict server-only
/// population and economy settings without changing package identity.
/// </summary>
public sealed class BridgePopulationSettingsService
{
    public const string SettingsFileName = "bcs-coop-bridge-population.config";
    public const string EconomySettingsFileName = "bcs-coop-bridge-economy.config";
    private const string HeaderV1 = "BCS-BRIDGE-POPULATION|1";
    private const string HeaderV2 = "BCS-BRIDGE-POPULATION|2";
    private const string HeaderV3 = "BCS-BRIDGE-POPULATION|3";
    private const string EconomyHeaderV1 = "BCS-BRIDGE-ECONOMY|1";
    private const string NativeValue = "NATIVE";
    private const string TrueValue = "TRUE";
    private const string FalseValue = "FALSE";
    private const string TransactionHeader = "BCS-BRIDGE-SETTINGS-TRANSACTION|1";
    private const long MaximumSettingsBytes = 64 * 1024;
    private const long MaximumTransactionBytes = 256 * 1024;
    private const long MaximumBridgeConfigurationBytes = 4 * 1024 * 1024;
    private const long MaximumBridgeAssemblyBytes = 64 * 1024 * 1024;
    private const long MaximumSettlementDataBytes = 16 * 1024 * 1024;
    private const long MaximumSettlementCharacters = 16 * 1024 * 1024;
    private const int MaximumSettlementCount = 100_000;
    private const int MaximumPartyLimit = 1_000_000;
    private const int MaximumAutomaticNpcCaravansPerTown = 10;
    private const double MinimumEconomyMultiplier = 0.1;
    private const double MaximumEconomyMultiplier = 10.0;
    private const double MaximumDestinationAgeBonus = 4.0;
    private const int MaximumCampaignDays = 3650;
    private const double MaximumBanditMultiplier = 10.0;
    private const double MinimumRegionalSpawnRadiusBanditTravelDays = 0.1;
    private const double MaximumRegionalSpawnRadiusBanditTravelDays = 30.0;
    private const int SettingsLockAttempts = 100;
    private const int SettingsLockDelayMilliseconds = 50;
    private static readonly UTF8Encoding StrictUtf8NoBom = new(false, true);
    private readonly string _serverRoot;
    private readonly Action? _beforeCommittedCleanup;
    private readonly Action? _beforeFirstCommit;

    public BridgePopulationSettingsService(string serverRoot)
        : this(serverRoot, beforeCommittedCleanup: null)
    {
    }

    internal BridgePopulationSettingsService(
        string serverRoot,
        Action? beforeCommittedCleanup,
        Action? beforeFirstCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        _serverRoot = Path.GetFullPath(serverRoot);
        _beforeCommittedCleanup = beforeCommittedCleanup;
        _beforeFirstCommit = beforeFirstCommit;
    }

    public string SettingsPath => Path.Combine(_serverRoot, SettingsFileName);
    public string EconomySettingsPath => Path.Combine(_serverRoot, EconomySettingsFileName);
    private string SettingsLockPath => Path.Combine(
        _serverRoot,
        ".bcs-bridge-settings.lock");
    private string TransactionPath => Path.Combine(
        _serverRoot,
        ".bcs-bridge-settings.transaction");
    private string TransactionPreparePath => TransactionPath + ".new";
    private string PopulationStagedPath => SettingsPath + ".bcs-txn-new";
    private string EconomyStagedPath => EconomySettingsPath + ".bcs-txn-new";

    public BridgePopulationSettings Load()
    {
        if (!Directory.Exists(_serverRoot))
            return LoadUnlocked();

        ValidateSettingsRoot();
        using var settingsLock = AcquireSettingsLock();
        RecoverSettingsTransaction();
        return LoadUnlocked();
    }

    private BridgePopulationSettings LoadUnlocked()
    {
        var settings = LoadPopulationSettings();
        LoadEconomySettings(settings);
        Validate(settings);
        return settings;
    }

    private BridgePopulationSettings LoadPopulationSettings()
    {
        if (!File.Exists(SettingsPath))
            return new BridgePopulationSettings();

        var lines = SplitConfigurationLines(ReadSettingsText(
            SettingsPath,
            "Bridge population settings"));
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

        if (lines.Length == 10 && lines[0].Equals(HeaderV3, StringComparison.Ordinal))
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
                    "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER"),
                PlayerActiveSpawnRadiusBanditTravelDays = ParseRegionalSpawnRadius(
                    lines[5],
                    "PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS"),
                RegionalAmbientOutlawSpawnsEnabled = ParseBoolean(
                    lines[6],
                    "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED"),
                RegionalVillagerTradeEnabled = ParseBoolean(
                    lines[7],
                    "REGIONAL_VILLAGER_TRADE_ENABLED"),
                RegionalSettlementPatrolSpawnsEnabled = ParseBoolean(
                    lines[8],
                    "REGIONAL_SETTLEMENT_PATROL_SPAWNS_ENABLED"),
                RegionalBattleDeserterSpawnsEnabled = ParseBoolean(
                    lines[9],
                    "REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED")
            };
        }

        throw new InvalidDataException("Unsupported bridge population settings format.");
    }

    private void LoadEconomySettings(BridgePopulationSettings settings)
    {
        if (!File.Exists(EconomySettingsPath))
            return;

        var lines = SplitConfigurationLines(ReadSettingsText(
            EconomySettingsPath,
            "Bridge economy settings"));
        if (lines.Length != 10 ||
            !lines[0].Equals(EconomyHeaderV1, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported bridge economy settings format.");
        }

        settings.CaravanCapacityMultiplier = ParseEconomyMultiplier(
            lines[1],
            "CARAVAN_CAPACITY_MULTIPLIER");
        settings.CaravanTradeBudgetMultiplier = ParseEconomyMultiplier(
            lines[2],
            "CARAVAN_TRADE_BUDGET_MULTIPLIER");
        settings.CaravanDestinationAgeMaxBonus = ParseDestinationAgeBonus(
            lines[3],
            "CARAVAN_DESTINATION_AGE_MAX_BONUS");
        settings.CaravanDestinationAgeHorizonDays = ParseCampaignDays(
            lines[4],
            "CARAVAN_DESTINATION_AGE_HORIZON_DAYS");
        settings.VillagerPartyCapacityMultiplier = ParseEconomyMultiplier(
            lines[5],
            "VILLAGER_PARTY_CAPACITY_MULTIPLIER");
        settings.VirtualVillagerShipmentsEnabled = ParseBoolean(
            lines[6],
            "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED");
        settings.VirtualVillagerCargoMultiplier = ParseEconomyMultiplier(
            lines[7],
            "VIRTUAL_VILLAGER_CARGO_MULTIPLIER");
        settings.VirtualVillagerCooldownDays = ParseCampaignDays(
            lines[8],
            "VIRTUAL_VILLAGER_COOLDOWN_DAYS");
        settings.VirtualVillagerTravelTimeMultiplier = ParseEconomyMultiplier(
            lines[9],
            "VIRTUAL_VILLAGER_TRAVEL_TIME_MULTIPLIER");
    }

    private string ReadSettingsText(string path, string description)
    {
        ValidateRegularFile(path, _serverRoot, MaximumSettingsBytes, description);
        var bytes = File.ReadAllBytes(path);
        try
        {
            return StrictUtf8NoBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"{description} are not valid UTF-8.",
                exception);
        }
    }

    public BridgePopulationSettingsSaveResult Save(BridgePopulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        if (!Directory.Exists(_serverRoot))
            throw new DirectoryNotFoundException(
                $"Dedicated-server directory was not found: {_serverRoot}");
        ValidateSettingsRoot();

        var populationBytes = StrictUtf8NoBom.GetBytes(SerializePopulation(settings));
        if (populationBytes.Length > MaximumSettingsBytes)
            throw new InvalidDataException("Bridge population settings exceed the 64 KiB limit.");
        var economyBytes = StrictUtf8NoBom.GetBytes(SerializeEconomy(settings));
        if (economyBytes.Length > MaximumSettingsBytes)
            throw new InvalidDataException("Bridge economy settings exceed the 64 KiB limit.");

        using var settingsLock = AcquireSettingsLock();
        RecoverSettingsTransaction();
        ValidateReplaceableSettingsPath(SettingsPath, "population");
        ValidateReplaceableSettingsPath(EconomySettingsPath, "economy");
        return WriteSettingsTransaction(populationBytes, economyBytes);
    }

    private void ValidateSettingsRoot()
    {
        if ((File.GetAttributes(_serverRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Linked dedicated-server directories cannot contain bridge settings: {_serverRoot}");
        }
    }

    private static void ValidateReplaceableSettingsPath(string path, string name)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                $"Bridge {name} settings path is a directory: {path}");
        }
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Linked bridge {name} settings cannot be replaced: {path}");
        }
    }

    private FileStream AcquireSettingsLock()
    {
        ValidateReplaceableSettingsPath(SettingsLockPath, "transaction lock");
        IOException? lastException = null;
        for (var attempt = 0; attempt < SettingsLockAttempts; attempt++)
        {
            try
            {
                var stream = new FileStream(
                    SettingsLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                if ((File.GetAttributes(SettingsLockPath) & FileAttributes.ReparsePoint) != 0)
                {
                    stream.Dispose();
                    throw new InvalidDataException(
                        $"Linked bridge settings transaction locks are not allowed: {SettingsLockPath}");
                }
                return stream;
            }
            catch (IOException exception)
            {
                lastException = exception;
                if (attempt + 1 < SettingsLockAttempts)
                    Thread.Sleep(SettingsLockDelayMilliseconds);
            }
        }

        throw new IOException(
            "Timed out waiting for another bridge settings transaction to finish.",
            lastException);
    }

    private BridgePopulationSettingsSaveResult WriteSettingsTransaction(
        byte[] populationBytes,
        byte[] economyBytes)
    {
        var population = CreateTransactionEntry(
            "POPULATION",
            SettingsPath,
            PopulationStagedPath,
            populationBytes);
        var economy = CreateTransactionEntry(
            "ECONOMY",
            EconomySettingsPath,
            EconomyStagedPath,
            economyBytes);
        var journalWritten = false;
        try
        {
            WriteDurableFile(population.StagedPath, populationBytes);
            WriteDurableFile(economy.StagedPath, economyBytes);
            WriteDurableFile(
                TransactionPreparePath,
                StrictUtf8NoBom.GetBytes(SerializeTransaction(population, economy)));
            File.Move(TransactionPreparePath, TransactionPath);
            journalWritten = true;

            _beforeFirstCommit?.Invoke();
            VerifyOriginalGeneration(population);
            VerifyOriginalGeneration(economy);
            CommitStagedFile(population);
            VerifyOriginalGeneration(economy);
            CommitStagedFile(economy);
            VerifyNewGeneration(population);
            VerifyNewGeneration(economy);
        }
        catch (Exception transactionException)
        {
            try
            {
                if (journalWritten || File.Exists(TransactionPath))
                    RecoverSettingsTransaction();
                else
                    CleanupTransactionArtifacts();
            }
            catch (Exception recoveryException)
            {
                throw new AggregateException(
                    "Bridge settings transaction failed and could not be fully recovered.",
                    transactionException,
                    recoveryException);
            }
            throw;
        }

        try
        {
            _beforeCommittedCleanup?.Invoke();
            CleanupTransactionArtifacts();
            return BridgePopulationSettingsSaveResult.Complete;
        }
        catch (Exception cleanupException)
        {
            return BridgePopulationSettingsSaveResult.WithCleanupPending(cleanupException);
        }
    }

    private SettingsTransactionEntry CreateTransactionEntry(
        string name,
        string destinationPath,
        string stagedPath,
        byte[] newBytes)
    {
        ValidateReplaceableSettingsPath(destinationPath, name.ToLowerInvariant());
        ValidateReplaceableSettingsPath(stagedPath, name.ToLowerInvariant() + " staged");
        var originalExists = File.Exists(destinationPath);
        var originalBytes = originalExists
            ? File.ReadAllBytes(destinationPath)
            : Array.Empty<byte>();
        if (originalBytes.Length > MaximumSettingsBytes)
        {
            throw new InvalidDataException(
                $"Existing bridge {name.ToLowerInvariant()} settings exceed the 64 KiB limit.");
        }
        return new SettingsTransactionEntry(
            name,
            destinationPath,
            stagedPath,
            originalExists,
            originalBytes,
            HashBytes(originalBytes),
            HashBytes(newBytes));
    }

    private void RecoverSettingsTransaction()
    {
        ValidateReplaceableSettingsPath(TransactionPath, "transaction journal");
        if (!File.Exists(TransactionPath))
        {
            CleanupOrphanStagedFiles();
            return;
        }

        var journalBytes = File.ReadAllBytes(TransactionPath);
        if (journalBytes.Length <= 0 || journalBytes.Length > MaximumTransactionBytes)
            throw new InvalidDataException("Bridge settings transaction journal has an invalid size.");
        string journalText;
        try
        {
            journalText = StrictUtf8NoBom.GetString(journalBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Bridge settings transaction journal is not valid UTF-8.",
                exception);
        }

        var entries = ParseTransaction(journalText);
        var population = entries[0];
        var economy = entries[1];
        if (IsNewGeneration(population) && IsNewGeneration(economy))
        {
            CleanupTransactionArtifacts();
            return;
        }

        ValidateRecoverableGeneration(population);
        ValidateRecoverableGeneration(economy);
        RestoreOriginalGeneration(population);
        RestoreOriginalGeneration(economy);
        VerifyOriginalGeneration(population);
        VerifyOriginalGeneration(economy);
        CleanupTransactionArtifacts();
    }

    private SettingsTransactionEntry[] ParseTransaction(string text)
    {
        if (text.IndexOf('\r') >= 0 || !text.EndsWith("\n", StringComparison.Ordinal))
            throw new InvalidDataException("Bridge settings transaction journal is not canonical.");
        var lines = text.Substring(0, text.Length - 1).Split('\n');
        if (lines.Length != 3 ||
            !lines[0].Equals(TransactionHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Bridge settings transaction journal has an invalid schema.");
        }

        return new[]
        {
            ParseTransactionEntry(
                lines[1],
                "POPULATION",
                SettingsPath,
                PopulationStagedPath),
            ParseTransactionEntry(
                lines[2],
                "ECONOMY",
                EconomySettingsPath,
                EconomyStagedPath)
        };
    }

    private static SettingsTransactionEntry ParseTransactionEntry(
        string line,
        string expectedName,
        string destinationPath,
        string stagedPath)
    {
        var fields = line.Split('|');
        if (fields.Length != 5 ||
            !fields[0].Equals(expectedName, StringComparison.Ordinal) ||
            (fields[1] != TrueValue && fields[1] != FalseValue) ||
            !IsSha256(fields[2]) ||
            !IsSha256(fields[4]))
        {
            throw new InvalidDataException(
                $"Bridge settings transaction {expectedName} record is invalid.");
        }

        byte[] originalBytes;
        try
        {
            originalBytes = fields[3].Length == 0
                ? Array.Empty<byte>()
                : Convert.FromBase64String(fields[3]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Bridge settings transaction {expectedName} snapshot is invalid.",
                exception);
        }

        var originalExists = fields[1] == TrueValue;
        if (originalBytes.Length > MaximumSettingsBytes ||
            (!originalExists && originalBytes.Length != 0) ||
            !HashBytes(originalBytes).Equals(fields[2], StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Bridge settings transaction {expectedName} snapshot failed integrity validation.");
        }

        return new SettingsTransactionEntry(
            expectedName,
            destinationPath,
            stagedPath,
            originalExists,
            originalBytes,
            fields[2],
            fields[4]);
    }

    private static string SerializeTransaction(
        SettingsTransactionEntry population,
        SettingsTransactionEntry economy) =>
        TransactionHeader + "\n" +
        SerializeTransactionEntry(population) + "\n" +
        SerializeTransactionEntry(economy) + "\n";

    private static string SerializeTransactionEntry(SettingsTransactionEntry entry) =>
        entry.Name + "|" +
        (entry.OriginalExists ? TrueValue : FalseValue) + "|" +
        entry.OriginalSha256 + "|" +
        Convert.ToBase64String(entry.OriginalBytes) + "|" +
        entry.NewSha256;

    private static void WriteDurableFile(string path, byte[] bytes)
    {
        ValidateReplaceableSettingsPath(path, "transaction artifact");
        if (File.Exists(path))
            throw new InvalidDataException($"Stale bridge settings transaction artifact exists: {path}");
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CommitStagedFile(SettingsTransactionEntry entry)
    {
        if (File.Exists(entry.DestinationPath))
            File.Replace(entry.StagedPath, entry.DestinationPath, destinationBackupFileName: null);
        else
            File.Move(entry.StagedPath, entry.DestinationPath);
    }

    private static void RestoreOriginalGeneration(SettingsTransactionEntry entry)
    {
        ValidateReplaceableSettingsPath(entry.DestinationPath, entry.Name.ToLowerInvariant());
        if (!entry.OriginalExists)
        {
            if (File.Exists(entry.DestinationPath))
            {
                if (!HashFile(entry.DestinationPath).Equals(
                        entry.NewSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Bridge settings transaction will not delete externally changed {entry.Name.ToLowerInvariant()} bytes.");
                }
                File.Delete(entry.DestinationPath);
            }
            return;
        }
        if (!File.Exists(entry.DestinationPath))
        {
            throw new InvalidDataException(
                $"Bridge settings transaction will not recreate unexpectedly missing {entry.Name.ToLowerInvariant()} bytes.");
        }

        var currentSha256 = HashFile(entry.DestinationPath);
        if (currentSha256.Equals(entry.OriginalSha256, StringComparison.Ordinal))
        {
            return;
        }
        if (!currentSha256.Equals(entry.NewSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Bridge settings transaction will not overwrite externally changed {entry.Name.ToLowerInvariant()} bytes.");
        }

        var recoveryPath = entry.StagedPath + ".recovery";
        ValidateReplaceableSettingsPath(recoveryPath, entry.Name.ToLowerInvariant() + " recovery");
        if (File.Exists(recoveryPath))
            File.Delete(recoveryPath);
        WriteDurableFile(recoveryPath, entry.OriginalBytes);
        try
        {
            if (File.Exists(entry.DestinationPath))
                File.Replace(recoveryPath, entry.DestinationPath, destinationBackupFileName: null);
            else
                File.Move(recoveryPath, entry.DestinationPath);
        }
        finally
        {
            if (File.Exists(recoveryPath))
                File.Delete(recoveryPath);
        }
    }

    private static void ValidateRecoverableGeneration(SettingsTransactionEntry entry)
    {
        ValidateReplaceableSettingsPath(entry.DestinationPath, entry.Name.ToLowerInvariant());
        if (!File.Exists(entry.DestinationPath))
        {
            if (!entry.OriginalExists)
                return;
            throw new InvalidDataException(
                $"Bridge settings transaction found missing {entry.Name.ToLowerInvariant()} bytes outside its expected states.");
        }

        var currentSha256 = HashFile(entry.DestinationPath);
        var isOriginal = entry.OriginalExists &&
                         currentSha256.Equals(entry.OriginalSha256, StringComparison.Ordinal);
        var isNew = currentSha256.Equals(entry.NewSha256, StringComparison.Ordinal);
        if (!isOriginal && !isNew)
        {
            throw new InvalidDataException(
                $"Bridge settings transaction found externally changed {entry.Name.ToLowerInvariant()} bytes; recovery stopped without overwriting them.");
        }
    }

    private static bool IsNewGeneration(SettingsTransactionEntry entry) =>
        File.Exists(entry.DestinationPath) &&
        HashFile(entry.DestinationPath).Equals(entry.NewSha256, StringComparison.Ordinal);

    private static void VerifyNewGeneration(SettingsTransactionEntry entry)
    {
        if (!IsNewGeneration(entry))
        {
            throw new IOException(
                $"Bridge settings transaction did not persist {entry.Name.ToLowerInvariant()} exactly.");
        }
    }

    private static void VerifyOriginalGeneration(SettingsTransactionEntry entry)
    {
        var restored = entry.OriginalExists
            ? File.Exists(entry.DestinationPath) &&
              HashFile(entry.DestinationPath).Equals(
                  entry.OriginalSha256,
                  StringComparison.Ordinal)
            : !File.Exists(entry.DestinationPath);
        if (!restored)
        {
            throw new IOException(
                $"Bridge settings transaction could not restore {entry.Name.ToLowerInvariant()}.");
        }
    }

    private void CleanupTransactionArtifacts()
    {
        CleanupOrphanStagedFiles();
        if (File.Exists(TransactionPath))
            File.Delete(TransactionPath);
    }

    private void CleanupOrphanStagedFiles()
    {
        DeleteTransactionArtifact(TransactionPreparePath);
        DeleteTransactionArtifact(PopulationStagedPath);
        DeleteTransactionArtifact(EconomyStagedPath);
        DeleteTransactionArtifact(PopulationStagedPath + ".recovery");
        DeleteTransactionArtifact(EconomyStagedPath + ".recovery");
    }

    private static void DeleteTransactionArtifact(string path)
    {
        ValidateReplaceableSettingsPath(path, "transaction artifact");
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string HashFile(string path)
    {
        ValidateReplaceableSettingsPath(path, "transaction destination");
        return HashBytes(File.ReadAllBytes(path));
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'A' && character <= 'F'));

    private sealed record SettingsTransactionEntry(
        string Name,
        string DestinationPath,
        string StagedPath,
        bool OriginalExists,
        byte[] OriginalBytes,
        string OriginalSha256,
        string NewSha256);

    internal string CreateSnapshot(BridgePopulationSettings settings)
    {
        Validate(settings);
        return SerializePopulation(settings) + SerializeEconomy(settings);
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

        var selectedIsBridge = TryGetGeneratedIdentity(selected.Id, out _);
        var bridge = ResolveSelectedBridge(selected, modules, selectedIsBridge);
        if (!bridge.IsInstalled || !bridge.Enabled || !bridge.IsServerCompatible)
            throw new InvalidDataException("The generated BCS Coop bridge is not enabled and current.");
        if (!TryGetGeneratedIdentity(bridge.Id, out _))
            throw new InvalidDataException("The resolved module is not a generated BCS Coop bridge.");
        if (!bridge.Version.Equals(
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
            !enabledGenerated[0].Id.Equals(bridge.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Exactly one generated BCS Coop bridge must be enabled.");
        }

        if (!bridge.Dependencies.Contains("Coop", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected bridge does not depend on Coop.");
        var overhaulDependencies = bridge.Dependencies
            .Where(id => !id.Equals("Coop", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var enabledDependencies = modules
            .Where(module =>
                module.Enabled &&
                overhaulDependencies.Contains(module.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        BannerlordModule? guideModule;
        if (!selectedIsBridge)
        {
            if (!overhaulDependencies.Contains(
                    selected.Id,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The enabled generated bridge targets a different overhaul.");
            }

            guideModule = selected;
        }
        else
        {
            var recipeRoots = enabledDependencies
                .Where(module =>
                    CompatibilityRecipeRegistry.FindByRootModule(module.Id) is not null)
                .ToArray();
            guideModule = recipeRoots.Length == 1
                ? recipeRoots[0]
                : overhaulDependencies.Length == 1 && enabledDependencies.Length == 1
                    ? enabledDependencies[0]
                    : null;
        }

        var modulesRoot = Path.GetFullPath(Path.Combine(_serverRoot, "engine", "Modules"));
        var bridgeRoot = Path.GetFullPath(bridge.Path);
        if (!Directory.Exists(bridgeRoot) ||
            Directory.GetParent(bridgeRoot)?.FullName.Equals(
                modulesRoot,
                StringComparison.OrdinalIgnoreCase) != true ||
            !Path.GetFileName(bridgeRoot).Equals(bridge.Id, StringComparison.Ordinal))
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
        ValidateRegularFile(
            serverAssemblyPath,
            bridgeRoot,
            MaximumBridgeAssemblyBytes,
            "Server bridge assembly");
        ValidateRegularFile(
            clientAssemblyPath,
            bridgeRoot,
            MaximumBridgeAssemblyBytes,
            "Client bridge assembly");

        var installedById = CreateInstalledModuleIndex(modules);
        var configurationBytes = File.ReadAllBytes(configurationPath);
        var installedConfiguration = BridgeDllSelectionService.ValidateInstalledConfiguration(
            configurationBytes,
            installedById);
        var catalogContract = installedConfiguration.BattleSceneCatalogContract;
        byte[]? catalogBytes = null;
        if (catalogContract is { } contract)
        {
            var catalogPath = Path.Combine(
                bridgeRoot,
                contract.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            ValidateRegularFile(
                catalogPath,
                bridgeRoot,
                MaximumBridgeConfigurationBytes,
                "Battle-scene catalog contract");
            catalogBytes = File.ReadAllBytes(catalogPath);
            var actualCatalogHash = Convert.ToHexString(SHA256.HashData(catalogBytes));
            if (!actualCatalogHash.Equals(contract.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Battle-scene catalog contract does not match its configured hash.");
            }
        }

        var actualBridgeId = CoopBridgePackageBuilder.ComputeBridgeId(
            configurationBytes,
            catalogBytes,
            File.ReadAllBytes(serverAssemblyPath),
            File.ReadAllBytes(clientAssemblyPath));
        if (!actualBridgeId.Equals(bridge.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected bridge configuration and assemblies do not match its generated module ID.");
        }

        BridgePopulationGuide? guide = null;
        var guideMessage = guideModule is null
            ? "Population guidance is unavailable because this server-wide bridge has " +
              "multiple campaign-module dependencies."
            : string.Empty;
        if (includePopulationGuide && guideModule is not null)
            (guide, guideMessage) = TryCalculatePopulationGuide(guideModule, modulesRoot);

        return new BridgePopulationSettingsTarget(
            bridge.Id,
            bridge.Name,
            guideModule?.Id ?? string.Empty,
            guideModule?.Name ?? "this dedicated server",
            guide,
            guideMessage);
    }

    private static BannerlordModule ResolveSelectedBridge(
        BannerlordModule selected,
        IReadOnlyList<BannerlordModule> modules,
        bool selectedIsBridge)
    {
        if (selectedIsBridge)
            return selected;

        if (!selected.IsInstalled || !selected.Enabled || !selected.IsServerCompatible ||
            CompatibilityRecipeRegistry.FindByRootModule(selected.Id) is null)
        {
            throw new InvalidDataException(
                "Select an enabled bridge-managed overhaul to edit Population & Trade Settings.");
        }

        var matches = modules
            .Where(module =>
                module.Enabled &&
                TryGetGeneratedIdentity(module.Id, out _) &&
                module.Dependencies.Contains(selected.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                "The selected overhaul must have exactly one enabled generated BCS Coop bridge.");
        }

        return matches[0];
    }

    private static IReadOnlyDictionary<string, BannerlordModule> CreateInstalledModuleIndex(
        IReadOnlyList<BannerlordModule> modules)
    {
        var installedById = new Dictionary<string, BannerlordModule>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Id))
                throw new InvalidDataException("Current module scan contains an empty module ID.");
            if (!installedById.TryAdd(module.Id, module))
                throw new InvalidDataException($"Current module scan repeats module ID: {module.Id}");
        }

        return installedById;
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
        ValidateEconomyMultiplier(
            settings.CaravanCapacityMultiplier,
            "Caravan capacity multiplier");
        ValidateEconomyMultiplier(
            settings.CaravanTradeBudgetMultiplier,
            "Caravan trade budget multiplier");
        if (!double.IsFinite(settings.CaravanDestinationAgeMaxBonus) ||
            settings.CaravanDestinationAgeMaxBonus < 0.0 ||
            settings.CaravanDestinationAgeMaxBonus > MaximumDestinationAgeBonus)
        {
            throw new InvalidDataException(
                "Caravan destination age maximum bonus must be between 0 and 4.");
        }
        ValidateCampaignDays(
            settings.CaravanDestinationAgeHorizonDays,
            "Caravan destination age horizon days");
        ValidatePartyLimit(
            settings.MaximumActiveVillagerParties,
            "Maximum active villager parties");
        ValidateEconomyMultiplier(
            settings.VillagerPartyCapacityMultiplier,
            "Villager party capacity multiplier");
        ValidateEconomyMultiplier(
            settings.VirtualVillagerCargoMultiplier,
            "Virtual villager cargo multiplier");
        ValidateCampaignDays(
            settings.VirtualVillagerCooldownDays,
            "Virtual villager cooldown days");
        ValidateEconomyMultiplier(
            settings.VirtualVillagerTravelTimeMultiplier,
            "Virtual villager travel time multiplier");
        if (!double.IsFinite(settings.BanditPartiesAroundHideoutMultiplier) ||
            settings.BanditPartiesAroundHideoutMultiplier < 0.0 ||
            settings.BanditPartiesAroundHideoutMultiplier > MaximumBanditMultiplier)
        {
            throw new InvalidDataException(
                "Bandit parties around hideouts multiplier must be between 0 and 10.");
        }
        ValidateRegionalSpawnRadius(settings.PlayerActiveSpawnRadiusBanditTravelDays);
        if (settings.RegionalVillagerTradeEnabled &&
            !settings.VirtualVillagerShipmentsEnabled)
        {
            throw new InvalidDataException(
                "Regional villager trade requires virtual villager shipments to be enabled.");
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

    private static string SerializePopulation(BridgePopulationSettings settings) =>
        HeaderV3 + "\n" +
        "MAXIMUM_AUTOMATIC_CARAVANS|" + FormatPartyLimit(settings.MaximumAutomaticCaravans) + "\n" +
        "AUTOMATIC_NPC_CARAVANS_PER_TOWN|" +
        settings.AutomaticNpcCaravansPerTown.ToString(CultureInfo.InvariantCulture) + "\n" +
        "MAXIMUM_ACTIVE_VILLAGER_PARTIES|" + FormatPartyLimit(settings.MaximumActiveVillagerParties) + "\n" +
        "BANDIT_PARTIES_AROUND_HIDEOUT_MULTIPLIER|" +
        settings.BanditPartiesAroundHideoutMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "PLAYER_ACTIVE_SPAWN_RADIUS_BANDIT_TRAVEL_DAYS|" +
        settings.PlayerActiveSpawnRadiusBanditTravelDays.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "REGIONAL_AMBIENT_OUTLAW_SPAWNS_ENABLED|" +
        (settings.RegionalAmbientOutlawSpawnsEnabled ? TrueValue : FalseValue) + "\n" +
        "REGIONAL_VILLAGER_TRADE_ENABLED|" +
        (settings.RegionalVillagerTradeEnabled ? TrueValue : FalseValue) + "\n" +
        "REGIONAL_SETTLEMENT_PATROL_SPAWNS_ENABLED|" +
        (settings.RegionalSettlementPatrolSpawnsEnabled ? TrueValue : FalseValue) + "\n" +
        "REGIONAL_BATTLE_DESERTER_SPAWNS_ENABLED|" +
        (settings.RegionalBattleDeserterSpawnsEnabled ? TrueValue : FalseValue) + "\n";

    private static string SerializeEconomy(BridgePopulationSettings settings) =>
        EconomyHeaderV1 + "\n" +
        "CARAVAN_CAPACITY_MULTIPLIER|" +
        settings.CaravanCapacityMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "CARAVAN_TRADE_BUDGET_MULTIPLIER|" +
        settings.CaravanTradeBudgetMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "CARAVAN_DESTINATION_AGE_MAX_BONUS|" +
        settings.CaravanDestinationAgeMaxBonus.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "CARAVAN_DESTINATION_AGE_HORIZON_DAYS|" +
        settings.CaravanDestinationAgeHorizonDays.ToString(CultureInfo.InvariantCulture) + "\n" +
        "VILLAGER_PARTY_CAPACITY_MULTIPLIER|" +
        settings.VillagerPartyCapacityMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "VIRTUAL_VILLAGER_SHIPMENTS_ENABLED|" +
        (settings.VirtualVillagerShipmentsEnabled ? TrueValue : FalseValue) + "\n" +
        "VIRTUAL_VILLAGER_CARGO_MULTIPLIER|" +
        settings.VirtualVillagerCargoMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n" +
        "VIRTUAL_VILLAGER_COOLDOWN_DAYS|" +
        settings.VirtualVillagerCooldownDays.ToString(CultureInfo.InvariantCulture) + "\n" +
        "VIRTUAL_VILLAGER_TRAVEL_TIME_MULTIPLIER|" +
        settings.VirtualVillagerTravelTimeMultiplier.ToString("R", CultureInfo.InvariantCulture) + "\n";

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

    private static double ParseEconomyMultiplier(string line, string key)
    {
        var value = ReadValue(line, key);
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < MinimumEconomyMultiplier ||
            parsed > MaximumEconomyMultiplier)
        {
            throw new InvalidDataException(
                $"{key} must be a finite number from 0.1 to 10.");
        }
        return parsed;
    }

    private static double ParseDestinationAgeBonus(string line, string key)
    {
        var value = ReadValue(line, key);
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < 0.0 ||
            parsed > MaximumDestinationAgeBonus)
        {
            throw new InvalidDataException(
                $"{key} must be a finite number from 0 to 4.");
        }
        return parsed;
    }

    private static int ParseCampaignDays(string line, string key)
    {
        var value = ReadValue(line, key);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 1 or > MaximumCampaignDays)
        {
            throw new InvalidDataException($"{key} must be an integer from 1 to 3650.");
        }
        return parsed;
    }

    private static bool ParseBoolean(string line, string key)
    {
        var value = ReadValue(line, key);
        if (value.Equals(TrueValue, StringComparison.Ordinal))
            return true;
        if (value.Equals(FalseValue, StringComparison.Ordinal))
            return false;
        throw new InvalidDataException($"{key} must be TRUE or FALSE.");
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

    private static double ParseRegionalSpawnRadius(string line, string key)
    {
        var value = ReadValue(line, key);
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < MinimumRegionalSpawnRadiusBanditTravelDays ||
            parsed > MaximumRegionalSpawnRadiusBanditTravelDays)
        {
            throw new InvalidDataException(
                $"{key} must be a finite number from 0.1 to 30.");
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

    private static void ValidateEconomyMultiplier(double value, string name)
    {
        if (!double.IsFinite(value) ||
            value < MinimumEconomyMultiplier ||
            value > MaximumEconomyMultiplier)
        {
            throw new InvalidDataException($"{name} must be between 0.1 and 10.");
        }
    }

    private static void ValidateCampaignDays(int value, string name)
    {
        if (value is < 1 or > MaximumCampaignDays)
            throw new InvalidDataException($"{name} must be between 1 and 3650.");
    }

    private static void ValidateRegionalSpawnRadius(double value)
    {
        if (!double.IsFinite(value) ||
            value < MinimumRegionalSpawnRadiusBanditTravelDays ||
            value > MaximumRegionalSpawnRadiusBanditTravelDays)
        {
            throw new InvalidDataException(
                "Player-active spawn radius must be between 0.1 and 30 average bandit travel-days.");
        }
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

}

public sealed record BridgePopulationSettingsSaveResult(
    bool CleanupPending,
    string? WarningMessage)
{
    public static BridgePopulationSettingsSaveResult Complete { get; } =
        new(false, null);

    internal static BridgePopulationSettingsSaveResult WithCleanupPending(
        Exception exception) =>
        new(
            true,
            "Settings were saved, but transaction cleanup is pending and will be retried " +
            "the next time settings are loaded or saved. " + exception.Message);
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
