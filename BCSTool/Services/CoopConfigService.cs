using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Loads and saves Bannerlord Coop's server and mod configuration files.
///
/// The source files are JSON-with-comments (JSONC). Active values are parsed
/// with System.Text.Json while comments are skipped and trailing commas are
/// allowed. Known settings are updated line-by-line so surrounding comments
/// and property ordering are retained.
///
/// Before each save, the current configuration is copied to a sibling .bak
/// file and the edited JSONC is written directly to the original file.
/// </summary>
public sealed class CoopConfigService
{
    private readonly string? _coopDataDirectoryOverride;

    private static readonly JsonDocumentOptions JsonOptions =
        new()
        {
            CommentHandling =
                JsonCommentHandling.Skip,

            AllowTrailingCommas =
                true
        };

    public CoopConfigService()
    {
    }

    internal CoopConfigService(
        string coopDataDirectoryOverride)
    {
        if (string.IsNullOrWhiteSpace(coopDataDirectoryOverride))
        {
            throw new ArgumentException(
                "Coop data directory cannot be empty.",
                nameof(coopDataDirectoryOverride));
        }

        _coopDataDirectoryOverride =
            Path.GetFullPath(coopDataDirectoryOverride);
    }

    public string CoopDataDirectory =>
        _coopDataDirectoryOverride ??
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "CoopData");

    public string ModConfigPath =>
        Path.Combine(
            CoopDataDirectory,
            "mod-config.json");

    public string ServerConfigPath =>
        Path.Combine(
            CoopDataDirectory,
            "DedicatedServer",
            "server-config.json");

    public string BannerlordDocumentsDirectory =>
        Path.GetDirectoryName(CoopDataDirectory) ??
        throw new InvalidOperationException(
            "Could not determine Bannerlord's Documents directory.");

    public string ClientSaveDirectory =>
        Path.Combine(
            BannerlordDocumentsDirectory,
            "Game Saves");

    public string ServerSaveDirectory =>
        Path.Combine(
            CoopDataDirectory,
            "DedicatedServer",
            "Game Saves");

    public string ServerLogDirectory =>
        Path.Combine(
            CoopDataDirectory,
            "DedicatedServer",
            "logs");

    public IReadOnlyList<string> GetServerSaveNames()
    {
        if (!Directory.Exists(ServerSaveDirectory))
            return Array.Empty<string>();

        return Directory
            .EnumerateFiles(ServerSaveDirectory, "*.sav", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file =>
                !file.Name.Equals(
                    "default_new_game.sav",
                    StringComparison.OrdinalIgnoreCase) &&
                !IsNumberedSaveBackup(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(file => Path.GetFileNameWithoutExtension(file.Name))
            .ToArray();
    }

    /// <summary>
    /// Returns true when the active server campaign has not yet recorded a
    /// Coop player. In that state the first joining client must finish the
    /// campaign intro and character-creation flow before save transfer begins.
    /// </summary>
    public bool IsFirstJoinCharacterSetupRequired()
    {
        var saveName =
            LoadServerConfig().SaveName;

        CoopSaveNamePolicy.EnsureValid(saveName);

        var sidecarPath =
            Path.Combine(
                ServerSaveDirectory,
                saveName + ".json");

        if (!File.Exists(sidecarPath))
            return true;

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(sidecarPath),
                JsonOptions);

        if (
            !document.RootElement.TryGetProperty(
                "Players",
                out var players) ||
            players.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Coop save sidecar does not contain a Players array: {sidecarPath}");
        }

        return players.GetArrayLength() == 0;
    }

    private static bool IsNumberedSaveBackup(string fileName)
    {
        var saveName =
            Path.GetFileNameWithoutExtension(fileName);
        var markerIndex =
            saveName.LastIndexOf(
                ".backup",
                StringComparison.OrdinalIgnoreCase);

        if (markerIndex < 0)
            return false;

        var backupNumber =
            saveName[(markerIndex + ".backup".Length)..];

        return backupNumber.Length > 0 &&
               backupNumber.All(char.IsDigit);
    }


    // ========================================================
    // SERVER CONFIG
    // ========================================================

    public DedicatedServerConfig LoadServerConfig()
    {
        var text =
            ReadRequiredFile(
                ServerConfigPath);

        using var document =
            JsonDocument.Parse(
                text,
                JsonOptions);

        var root =
            document.RootElement;

        return
            new DedicatedServerConfig
            {
                SaveName =
                    GetString(
                        root,
                        "saveName",
                        "saveauto1"),

                AutosaveMinutes =
                    GetInt(
                        root,
                        "autosaveMinutes",
                        5),

                Password =
                    GetString(
                        root,
                        "password",
                        ""),

                LogFile =
                    GetBool(
                        root,
                        "logFile",
                        true),

                Steam =
                    GetBool(
                        root,
                        "steam",
                        true),

                TraceTick =
                    GetBool(
                        root,
                        "traceTick",
                        false),

                TracePublish =
                    GetBool(
                        root,
                        "tracePublish",
                        false),

                TraceBandits =
                    GetBool(
                        root,
                        "traceBandits",
                        false)
            };
    }


    public void SaveServerConfig(
        DedicatedServerConfig config)
    {
        CoopSaveNamePolicy.EnsureValid(config.SaveName);

        if (config.AutosaveMinutes < 0)
        {
            throw new InvalidOperationException(
                "Autosave minutes cannot be negative.");
        }

        if (config.Password.Length > 128)
        {
            throw new InvalidOperationException(
                "Server password cannot exceed 128 characters.");
        }

        var path =
            ServerConfigPath;

        var text =
            ReadRequiredFile(path);

        text =
            SetRequiredKey(
                text,
                "saveName",
                JsonSerializer.Serialize(
                    config.SaveName));

        text =
            SetRequiredKey(
                text,
                "autosaveMinutes",
                config.AutosaveMinutes.ToString(
                    CultureInfo.InvariantCulture));

        text =
            SetRequiredKey(
                text,
                "password",
                JsonSerializer.Serialize(
                    config.Password));

        text =
            SetRequiredKey(
                text,
                "logFile",
                ToJsonBool(
                    config.LogFile));

        text =
            SetRequiredKey(
                text,
                "steam",
                ToJsonBool(
                    config.Steam));

        text =
            SetOptionalRootBoolean(
                text,
                "traceTick",
                config.TraceTick);

        text =
            SetOptionalRootBoolean(
                text,
                "tracePublish",
                config.TracePublish);

        text =
            SetOptionalRootBoolean(
                text,
                "traceBandits",
                config.TraceBandits);

        SaveWithBackup(
            path,
            text);
    }


    // ========================================================
    // MOD CONFIG
    // ========================================================

    public CoopModConfig LoadModConfig()
    {
        var text =
            ReadRequiredFile(
                ModConfigPath);

        using var document =
            JsonDocument.Parse(
                text,
                JsonOptions);

        var root =
            document.RootElement;

        var difficulty =
            root.TryGetProperty(
                "difficulty",
                out var difficultyElement)
                ? difficultyElement
                : default;

        var hasModOptions =
            root.TryGetProperty(
                "modOptions",
                out var modOptions);

        if (
            hasModOptions &&
            modOptions.ValueKind is not
                (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new InvalidDataException(
                "mod-config.json modOptions must be an object or null.");
        }

        var config =
            new CoopModConfig();

        LoadOptionalDifficultyString(
            text,
            difficulty,
            "playerReceivedDamage",
            "Realistic",
            out var playerReceivedDamageOverride,
            out var playerReceivedDamage);

        config.PlayerReceivedDamageOverride =
            playerReceivedDamageOverride;

        config.PlayerReceivedDamage =
            playerReceivedDamage;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "playerTroopsReceivedDamage",
            "VeryEasy",
            out var playerTroopsReceivedDamageOverride,
            out var playerTroopsReceivedDamage);

        config.PlayerTroopsReceivedDamageOverride =
            playerTroopsReceivedDamageOverride;

        config.PlayerTroopsReceivedDamage =
            playerTroopsReceivedDamage;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "combatAIDifficulty",
            "VeryEasy",
            out var combatAIDifficultyOverride,
            out var combatAIDifficulty);

        config.CombatAIDifficultyOverride =
            combatAIDifficultyOverride;

        config.CombatAIDifficulty =
            combatAIDifficulty;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "recruitmentDifficulty",
            "VeryEasy",
            out var recruitmentDifficultyOverride,
            out var recruitmentDifficulty);

        config.RecruitmentDifficultyOverride =
            recruitmentDifficultyOverride;

        config.RecruitmentDifficulty =
            recruitmentDifficulty;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "playerMapMovementSpeed",
            "VeryEasy",
            out var playerMapMovementSpeedOverride,
            out var playerMapMovementSpeed);

        config.PlayerMapMovementSpeedOverride =
            playerMapMovementSpeedOverride;

        config.PlayerMapMovementSpeed =
            playerMapMovementSpeed;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "stealthAndDisguiseDifficulty",
            "VeryEasy",
            out var stealthAndDisguiseDifficultyOverride,
            out var stealthAndDisguiseDifficulty);

        config.StealthAndDisguiseDifficultyOverride =
            stealthAndDisguiseDifficultyOverride;

        config.StealthAndDisguiseDifficulty =
            stealthAndDisguiseDifficulty;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "persuasionSuccessChance",
            "VeryEasy",
            out var persuasionSuccessChanceOverride,
            out var persuasionSuccessChance);

        config.PersuasionSuccessChanceOverride =
            persuasionSuccessChanceOverride;

        config.PersuasionSuccessChance =
            persuasionSuccessChance;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "clanMemberDeathChance",
            "VeryEasy",
            out var clanMemberDeathChanceOverride,
            out var clanMemberDeathChance);

        config.ClanMemberDeathChanceOverride =
            clanMemberDeathChanceOverride;

        config.ClanMemberDeathChance =
            clanMemberDeathChance;


        LoadOptionalDifficultyString(
            text,
            difficulty,
            "battleDeath",
            "VeryEasy",
            out var battleDeathOverride,
            out var battleDeath);

        config.BattleDeathOverride =
            battleDeathOverride;

        config.BattleDeath =
            battleDeath;


        LoadOptionalDifficultyBool(
            text,
            difficulty,
            "birthAndDeath",
            true,
            out var birthAndDeathOverride,
            out var birthAndDeath);

        config.BirthAndDeathOverride =
            birthAndDeathOverride;

        config.BirthAndDeath =
            birthAndDeath;


        LoadOptionalDifficultyBool(
            text,
            difficulty,
            "autoAllocateClanMemberPerks",
            false,
            out var autoAllocateClanMemberPerksOverride,
            out var autoAllocateClanMemberPerks);

        config.AutoAllocateClanMemberPerksOverride =
            autoAllocateClanMemberPerksOverride;

        config.AutoAllocateClanMemberPerks =
            autoAllocateClanMemberPerks;


        // Bannerlord Coop intentionally treats an absent or explicit null
        // modOptions block as an all-default ModOptionsData instance. Keep the
        // editor aligned with that production behavior instead of rejecting a
        // valid minimal config.
        if (
            !hasModOptions ||
            modOptions.ValueKind == JsonValueKind.Null)
        {
            return config;
        }


        config.FastForwardEnabled =
            GetBool(
                modOptions,
                "fastForwardEnabled",
                true);

        config.AutoPauseEnabled =
            GetBool(
                modOptions,
                "autoPauseEnabled",
                true);

        config.ClientsCanUseCheats =
            GetBool(
                modOptions,
                "clientsCanUseCheats",
                false);

        config.GoldFoodInfluenceChangeInSettlements =
            GetBool(
                modOptions,
                "goldFoodInfluenceChangeInSettlements",
                true);

        config.GoldFoodInfluenceChangeInBattles =
            GetString(
                modOptions,
                "goldFoodInfluenceChangeInBattles",
                "OneDayMax");

        config.GoldFoodInfluenceChangeForDisconnectedPlayers =
            GetBool(
                modOptions,
                "goldFoodInfluenceChangeForDisconnectedPlayers",
                false);

        config.PlayerBattleAiJoinWindowHours =
            GetInt(
                modOptions,
                "playerBattleAiJoinWindowHours",
                24);

        config.SpeedLimitWhilePlayersInBattle =
            GetBool(
                modOptions,
                "speedLimitWhilePlayersInBattle",
                true);

        config.WandererLimit =
            GetInt(
                modOptions,
                "wandererLimit",
                32);

        config.WandererLimitScalesWithPlayers =
            GetBool(
                modOptions,
                "wandererLimitScalesWithPlayers",
                false);

        config.PlayerKingdomClanTierRequired =
            GetInt(
                modOptions,
                "playerKingdomClanTierRequired",
                4);

        config.SmithingStaminaRecoveryOutsideSettlements =
            GetBool(
                modOptions,
                "smithingStaminaRecoveryOutsideSettlements",
                true);

        config.SmithingStaminaRecoveryMultiplier =
            GetDouble(
                modOptions,
                "smithingStaminaRecoveryMultiplier",
                0.1);

        config.MaximumLootersMultiplier =
            GetDouble(
                modOptions,
                "maximumLootersMultiplier",
                1.0);

        return config;
    }


    public void SaveModConfig(
        CoopModConfig config)
    {
        ValidateDifficultyValue(
            config.PlayerReceivedDamage,
            nameof(config.PlayerReceivedDamage));

        ValidateDifficultyValue(
            config.PlayerTroopsReceivedDamage,
            nameof(config.PlayerTroopsReceivedDamage));

        ValidateDifficultyValue(
            config.CombatAIDifficulty,
            nameof(config.CombatAIDifficulty));

        ValidateDifficultyValue(
            config.RecruitmentDifficulty,
            nameof(config.RecruitmentDifficulty));

        ValidateDifficultyValue(
            config.PlayerMapMovementSpeed,
            nameof(config.PlayerMapMovementSpeed));

        ValidateDifficultyValue(
            config.StealthAndDisguiseDifficulty,
            nameof(config.StealthAndDisguiseDifficulty));

        ValidateDifficultyValue(
            config.PersuasionSuccessChance,
            nameof(config.PersuasionSuccessChance));

        ValidateDifficultyValue(
            config.ClanMemberDeathChance,
            nameof(config.ClanMemberDeathChance));

        ValidateDifficultyValue(
            config.BattleDeath,
            nameof(config.BattleDeath));

        if (
            config.GoldFoodInfluenceChangeInBattles is not
                ("Disabled" or "OneDayMax" or "Enabled"))
        {
            throw new InvalidOperationException(
                "Battle gold/food/influence mode must be Disabled, OneDayMax, or Enabled.");
        }

        if (config.PlayerBattleAiJoinWindowHours < 0)
        {
            throw new InvalidOperationException(
                "AI battle join window hours cannot be negative.");
        }

        if (config.WandererLimit < 0)
        {
            throw new InvalidOperationException(
                "Wanderer limit cannot be negative.");
        }

        if (config.PlayerKingdomClanTierRequired < 0)
        {
            throw new InvalidOperationException(
                "Kingdom clan tier requirement cannot be negative.");
        }

        if (config.SmithingStaminaRecoveryMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Smithing stamina recovery multiplier cannot be negative.");
        }

        if (config.MaximumLootersMultiplier < 0)
        {
            throw new InvalidOperationException(
                "Maximum looters multiplier cannot be negative.");
        }


        var path =
            ModConfigPath;

        var text =
            ReadRequiredFile(path);


        // Difficulty overrides.
        text =
            SetOptionalCommentedKey(
                text,
                "playerReceivedDamage",
                JsonSerializer.Serialize(
                    config.PlayerReceivedDamage),
                config.PlayerReceivedDamageOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "playerTroopsReceivedDamage",
                JsonSerializer.Serialize(
                    config.PlayerTroopsReceivedDamage),
                config.PlayerTroopsReceivedDamageOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "combatAIDifficulty",
                JsonSerializer.Serialize(
                    config.CombatAIDifficulty),
                config.CombatAIDifficultyOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "recruitmentDifficulty",
                JsonSerializer.Serialize(
                    config.RecruitmentDifficulty),
                config.RecruitmentDifficultyOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "playerMapMovementSpeed",
                JsonSerializer.Serialize(
                    config.PlayerMapMovementSpeed),
                config.PlayerMapMovementSpeedOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "stealthAndDisguiseDifficulty",
                JsonSerializer.Serialize(
                    config.StealthAndDisguiseDifficulty),
                config.StealthAndDisguiseDifficultyOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "persuasionSuccessChance",
                JsonSerializer.Serialize(
                    config.PersuasionSuccessChance),
                config.PersuasionSuccessChanceOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "clanMemberDeathChance",
                JsonSerializer.Serialize(
                    config.ClanMemberDeathChance),
                config.ClanMemberDeathChanceOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "battleDeath",
                JsonSerializer.Serialize(
                    config.BattleDeath),
                config.BattleDeathOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "birthAndDeath",
                ToJsonBool(
                    config.BirthAndDeath),
                config.BirthAndDeathOverride);

        text =
            SetOptionalCommentedKey(
                text,
                "autoAllocateClanMemberPerks",
                ToJsonBool(
                    config.AutoAllocateClanMemberPerks),
                config.AutoAllocateClanMemberPerksOverride);


        // Mod options are optional in Bannerlord Coop. Materialize a missing,
        // null, or partial block before applying the user's values so a valid
        // minimal config remains fully editable.
        var modOptionValues =
            GetModOptionValues(config);

        text =
            EnsureModOptionsKeys(
                text,
                modOptionValues);

        foreach (var option in modOptionValues)
        {
            text =
                SetRequiredKey(
                    text,
                    option.Key,
                    option.JsonValue);
        }


        SaveWithBackup(
            path,
            text);
    }


    // ========================================================
    // JSONC HELPERS — v1.8.1 SIMPLE WRITER (NO REGEX)
    // ========================================================

    private static string ReadRequiredFile(
        string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file was not found:\n{path}",
                path);
        }

        return
            File.ReadAllText(
                path,
                Encoding.UTF8);
    }


    private static string SetRequiredKey(
        string text,
        string key,
        string jsonValue)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var index =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: false);

        if (index < 0)
        {
            throw new InvalidDataException(
                $"Could not find setting '{key}' in the configuration file.");
        }

        lines[index] =
            BuildSettingLine(
                lines[index],
                key,
                jsonValue,
                commented: false);

        return
            string.Join(
                newline,
                lines);
    }


    private static IReadOnlyList<(string Key, string JsonValue)> GetModOptionValues(
        CoopModConfig config) =>
        new (string Key, string JsonValue)[]
        {
            ("fastForwardEnabled", ToJsonBool(config.FastForwardEnabled)),
            ("autoPauseEnabled", ToJsonBool(config.AutoPauseEnabled)),
            ("clientsCanUseCheats", ToJsonBool(config.ClientsCanUseCheats)),
            (
                "goldFoodInfluenceChangeInSettlements",
                ToJsonBool(config.GoldFoodInfluenceChangeInSettlements)),
            (
                "goldFoodInfluenceChangeInBattles",
                JsonSerializer.Serialize(config.GoldFoodInfluenceChangeInBattles)),
            (
                "goldFoodInfluenceChangeForDisconnectedPlayers",
                ToJsonBool(config.GoldFoodInfluenceChangeForDisconnectedPlayers)),
            (
                "playerBattleAiJoinWindowHours",
                config.PlayerBattleAiJoinWindowHours.ToString(CultureInfo.InvariantCulture)),
            (
                "speedLimitWhilePlayersInBattle",
                ToJsonBool(config.SpeedLimitWhilePlayersInBattle)),
            ("wandererLimit", config.WandererLimit.ToString(CultureInfo.InvariantCulture)),
            (
                "wandererLimitScalesWithPlayers",
                ToJsonBool(config.WandererLimitScalesWithPlayers)),
            (
                "playerKingdomClanTierRequired",
                config.PlayerKingdomClanTierRequired.ToString(CultureInfo.InvariantCulture)),
            (
                "smithingStaminaRecoveryOutsideSettlements",
                ToJsonBool(config.SmithingStaminaRecoveryOutsideSettlements)),
            (
                "smithingStaminaRecoveryMultiplier",
                config.SmithingStaminaRecoveryMultiplier.ToString(CultureInfo.InvariantCulture)),
            (
                "maximumLootersMultiplier",
                config.MaximumLootersMultiplier.ToString(CultureInfo.InvariantCulture))
        };


    private static string EnsureModOptionsKeys(
        string text,
        IReadOnlyList<(string Key, string JsonValue)> options)
    {
        using var document =
            JsonDocument.Parse(
                text,
                JsonOptions);

        var root =
            document.RootElement;

        var hasModOptions =
            root.TryGetProperty(
                "modOptions",
                out var modOptions);

        if (
            hasModOptions &&
            modOptions.ValueKind is not
                (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new InvalidDataException(
                "mod-config.json modOptions must be an object or null.");
        }

        var lines =
            new List<string>(
                SplitLines(
                    text,
                    out var newline));

        if (
            !hasModOptions ||
            modOptions.ValueKind == JsonValueKind.Null)
        {
            InsertCompleteModOptionsObject(
                lines,
                options,
                hasModOptions);

            return string.Join(newline, lines);
        }

        var missing =
            options
                .Where(option =>
                    !modOptions.TryGetProperty(
                        option.Key,
                        out _))
                .ToArray();

        if (missing.Length == 0)
            return text;

        var openingIndex =
            FindSettingLineIndex(
                lines.ToArray(),
                "modOptions",
                includeCommented: false);

        if (openingIndex < 0)
        {
            throw new InvalidDataException(
                "Could not locate the active modOptions object in mod-config.json.");
        }

        var closingIndex =
            FindObjectClosingBraceLineIndex(
                lines,
                openingIndex);

        if (closingIndex < 0 || closingIndex == openingIndex)
        {
            throw new InvalidDataException(
                "The modOptions object must use the normal multi-line JSONC layout before missing settings can be added safely.");
        }

        EnsureCommaBeforeInsertion(
            lines,
            closingIndex);

        var indentation =
            GetLeadingWhitespace(lines[closingIndex]) +
            "  ";

        foreach (var option in missing)
        {
            lines.Insert(
                closingIndex++,
                $"{indentation}\"{option.Key}\": {option.JsonValue},");
        }

        return string.Join(newline, lines);
    }


    private static void InsertCompleteModOptionsObject(
        List<string> lines,
        IReadOnlyList<(string Key, string JsonValue)> options,
        bool replaceNullProperty)
    {
        var insertionIndex =
            replaceNullProperty
                ? FindSettingLineIndex(
                    lines.ToArray(),
                    "modOptions",
                    includeCommented: false)
                : FindRootClosingBraceIndex(
                    lines.ToArray());

        if (insertionIndex < 0)
        {
            throw new InvalidDataException(
                "Could not locate where to create modOptions in mod-config.json.");
        }

        if (!replaceNullProperty)
        {
            EnsureCommaBeforeInsertion(
                lines,
                insertionIndex);
        }

        var indentation =
            replaceNullProperty
                ? GetLeadingWhitespace(lines[insertionIndex])
                : GetLeadingWhitespace(lines[insertionIndex]) + "  ";

        var block =
            new List<string>
            {
                $"{indentation}\"modOptions\": {{"
            };

        block.AddRange(
            options.Select(option =>
                $"{indentation}  \"{option.Key}\": {option.JsonValue},"));

        block.Add(
            $"{indentation}}},");

        if (replaceNullProperty)
        {
            lines.RemoveAt(insertionIndex);
        }

        lines.InsertRange(
            insertionIndex,
            block);
    }


    private static int FindObjectClosingBraceLineIndex(
        IReadOnlyList<string> lines,
        int openingLineIndex)
    {
        var depth = 0;
        var objectStarted = false;
        var inString = false;
        var escaped = false;

        for (var lineIndex = openingLineIndex; lineIndex < lines.Count; lineIndex++)
        {
            var line =
                lines[lineIndex];

            for (var characterIndex = 0; characterIndex < line.Length; characterIndex++)
            {
                var character =
                    line[characterIndex];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (inString && character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (
                    !inString &&
                    character == '/' &&
                    characterIndex + 1 < line.Length &&
                    line[characterIndex + 1] == '/')
                {
                    break;
                }

                if (inString)
                    continue;

                if (character == '{')
                {
                    objectStarted = true;
                    depth++;
                }
                else if (
                    character == '}' &&
                    objectStarted &&
                    --depth == 0)
                {
                    return lineIndex;
                }
            }
        }

        return -1;
    }


    private static void EnsureCommaBeforeInsertion(
        List<string> lines,
        int insertionIndex)
    {
        for (var index = insertionIndex - 1; index >= 0; index--)
        {
            var trimmed =
                lines[index].Trim();

            if (
                trimmed.Length == 0 ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (
                trimmed.EndsWith(",", StringComparison.Ordinal) ||
                trimmed.EndsWith("{", StringComparison.Ordinal))
            {
                return;
            }

            var commentIndex =
                lines[index].IndexOf(
                    "//",
                    StringComparison.Ordinal);

            if (commentIndex < 0)
            {
                lines[index] += ",";
            }
            else
            {
                lines[index] =
                    lines[index][..commentIndex].TrimEnd() +
                    ", " +
                    lines[index][commentIndex..];
            }

            return;
        }

        throw new InvalidDataException(
            "Could not safely add a property to mod-config.json.");
    }


    /// <summary>
    /// Enables/disables one optional difficulty setting by toggling only the
    /// JSONC // marker on that setting line. Indentation and any explanatory
    /// inline comment after the comma are preserved.
    /// </summary>
    private static string SetOptionalCommentedKey(
        string text,
        string key,
        string jsonValue,
        bool enabled)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var index =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: true);

        if (index < 0)
        {
            throw new InvalidDataException(
                $"Could not find optional difficulty setting '{key}' in mod-config.json.");
        }

        lines[index] =
            BuildSettingLine(
                lines[index],
                key,
                jsonValue,
                commented: !enabled);

        return
            string.Join(
                newline,
                lines);
    }


    /// <summary>
    /// Diagnostic booleans may be absent. Existing active keys are updated.
    /// Missing false keys remain absent. Missing true keys are inserted before
    /// the root closing brace.
    /// </summary>
    private static string SetOptionalRootBoolean(
        string text,
        string key,
        bool value)
    {
        var lines =
            SplitLines(
                text,
                out var newline);

        var index =
            FindSettingLineIndex(
                lines,
                key,
                includeCommented: false);

        if (index >= 0)
        {
            lines[index] =
                BuildSettingLine(
                    lines[index],
                    key,
                    ToJsonBool(value),
                    commented: false);

            return
                string.Join(
                    newline,
                    lines);
        }

        if (!value)
        {
            return
                string.Join(
                    newline,
                    lines);
        }

        var closingIndex =
            FindRootClosingBraceIndex(
                lines);

        if (closingIndex < 0)
        {
            throw new InvalidDataException(
                "server-config.json does not contain a root closing brace.");
        }

        var list =
            new List<string>(
                lines);

        list.Insert(
            closingIndex,
            $"  \"{key}\": true,");

        return
            string.Join(
                newline,
                list);
    }


    private static string BuildSettingLine(
        string originalLine,
        string key,
        string jsonValue,
        bool commented)
    {
        var indentation =
            GetLeadingWhitespace(
                originalLine);

        var trailingComment =
            ExtractTrailingCommentFromOptionalLine(
                originalLine);

        var result =
            indentation +
            (commented ? "// " : "") +
            "\"" + key + "\": " + jsonValue + ",";

        if (trailingComment.Length > 0)
        {
            result +=
                " " +
                trailingComment;
        }

        return result;
    }


    private static string ExtractTrailingCommentFromOptionalLine(
        string line)
    {
        var commaIndex =
            line.IndexOf(',');

        if (commaIndex < 0)
            return "";

        var commentIndex =
            line.IndexOf(
                "//",
                commaIndex + 1,
                StringComparison.Ordinal);

        if (commentIndex < 0)
            return "";

        return
            line[commentIndex..]
                .Trim();
    }


    private static string GetLeadingWhitespace(
        string line)
    {
        var count = 0;

        while (
            count < line.Length &&
            char.IsWhiteSpace(
                line[count]))
        {
            count++;
        }

        return
            line[..count];
    }


    private static int FindSettingLineIndex(
        string[] lines,
        string key,
        bool includeCommented)
    {
        for (
            var i = 0;
            i < lines.Length;
            i++)
        {
            if (
                IsSettingLineForKey(
                    lines[i],
                    key,
                    includeCommented))
            {
                return i;
            }
        }

        return -1;
    }


    private static bool IsSettingLineForKey(
        string line,
        string key,
        bool includeCommented)
    {
        var trimmed =
            line.TrimStart();

        if (
            trimmed.StartsWith(
                "//",
                StringComparison.Ordinal))
        {
            if (!includeCommented)
                return false;

            trimmed =
                trimmed[2..]
                    .TrimStart();
        }

        var quotedKey =
            "\"" +
            key +
            "\"";

        if (
            !trimmed.StartsWith(
                quotedKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        var remainder =
            trimmed[quotedKey.Length..]
                .TrimStart();

        return
            remainder.StartsWith(
                ":",
                StringComparison.Ordinal);
    }


    private static int FindRootClosingBraceIndex(
        string[] lines)
    {
        for (
            var i = lines.Length - 1;
            i >= 0;
            i--)
        {
            if (
                lines[i]
                    .Trim()
                    .Equals(
                        "}",
                        StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }


    private static void SaveWithBackup(
        string path,
        string text)
    {
        File.Copy(
            path,
            path + ".bak",
            overwrite: true);

        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
    }


    private static void LoadOptionalDifficultyString(
        string rawText,
        JsonElement difficulty,
        string key,
        string fallback,
        out bool enabled,
        out string value)
    {
        if (
            difficulty.ValueKind == JsonValueKind.Object &&
            difficulty.TryGetProperty(
                key,
                out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            enabled = true;

            value =
                element.GetString() ??
                fallback;

            return;
        }

        enabled = false;

        value =
            ReadCommentedStringValue(
                rawText,
                key) ??
            fallback;
    }


    private static void LoadOptionalDifficultyBool(
        string rawText,
        JsonElement difficulty,
        string key,
        bool fallback,
        out bool enabled,
        out bool value)
    {
        if (
            difficulty.ValueKind == JsonValueKind.Object &&
            difficulty.TryGetProperty(
                key,
                out var element) &&
            (
                element.ValueKind == JsonValueKind.True ||
                element.ValueKind == JsonValueKind.False
            ))
        {
            enabled = true;
            value = element.GetBoolean();
            return;
        }

        enabled = false;

        value =
            ReadCommentedBoolValue(
                rawText,
                key) ??
            fallback;
    }


    private static string? ReadCommentedStringValue(
        string text,
        string key)
    {
        var rawValue =
            ReadCommentedRawValue(
                text,
                key);

        if (rawValue is null)
            return null;

        try
        {
            return
                JsonSerializer.Deserialize<string>(
                    rawValue);
        }
        catch
        {
            return null;
        }
    }


    private static bool? ReadCommentedBoolValue(
        string text,
        string key)
    {
        var rawValue =
            ReadCommentedRawValue(
                text,
                key);

        if (
            rawValue is null ||
            !bool.TryParse(
                rawValue,
                out var value))
        {
            return null;
        }

        return value;
    }


    private static string? ReadCommentedRawValue(
        string text,
        string key)
    {
        var lines =
            SplitLines(
                text,
                out _);

        foreach (var line in lines)
        {
            var trimmed =
                line.TrimStart();

            if (
                !trimmed.StartsWith(
                    "//",
                    StringComparison.Ordinal))
            {
                continue;
            }

            trimmed =
                trimmed[2..]
                    .TrimStart();

            var quotedKey =
                "\"" +
                key +
                "\"";

            if (
                !trimmed.StartsWith(
                    quotedKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var remainder =
                trimmed[quotedKey.Length..]
                    .TrimStart();

            if (
                !remainder.StartsWith(
                    ":",
                    StringComparison.Ordinal))
            {
                continue;
            }

            remainder =
                remainder[1..]
                    .TrimStart();

            var commaIndex =
                FindValueTerminatingComma(
                    remainder);

            var rawValue =
                commaIndex >= 0
                    ? remainder[..commaIndex]
                    : remainder;

            return
                rawValue.Trim();
        }

        return null;
    }


    private static int FindValueTerminatingComma(
        string text)
    {
        var inString = false;
        var escaped = false;

        for (
            var i = 0;
            i < text.Length;
            i++)
        {
            var ch =
                text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (
                inString &&
                ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString =
                    !inString;

                continue;
            }

            if (
                !inString &&
                ch == ',')
            {
                return i;
            }
        }

        return -1;
    }


    private static string[] SplitLines(
        string text,
        out string newline)
    {
        newline =
            text.Contains(
                "\r\n",
                StringComparison.Ordinal)
                ? "\r\n"
                : "\n";

        return
            text.Replace(
                    "\r\n",
                    "\n",
                    StringComparison.Ordinal)
                .Split('\n');
    }


    private static string GetString(
        JsonElement element,
        string key,
        string fallback)
    {
        return
            element.TryGetProperty(
                key,
                out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
    }


    private static int GetInt(
        JsonElement element,
        string key,
        int fallback)
    {
        return
            element.TryGetProperty(
                key,
                out var value) &&
            value.TryGetInt32(
                out var result)
                ? result
                : fallback;
    }


    private static double GetDouble(
        JsonElement element,
        string key,
        double fallback)
    {
        return
            element.TryGetProperty(
                key,
                out var value) &&
            value.TryGetDouble(
                out var result)
                ? result
                : fallback;
    }


    private static bool GetBool(
        JsonElement element,
        string key,
        bool fallback)
    {
        if (
            !element.TryGetProperty(
                key,
                out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }


    private static string ToJsonBool(bool value) =>
        value
            ? "true"
            : "false";


    private static void ValidateDifficultyValue(
        string value,
        string propertyName)
    {
        if (
            value is not
                ("VeryEasy" or "Easy" or "Realistic"))
        {
            throw new InvalidOperationException(
                $"{propertyName} must be VeryEasy, Easy, or Realistic.");
        }
    }
}
