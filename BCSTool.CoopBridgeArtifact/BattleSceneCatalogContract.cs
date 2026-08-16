using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BCS.CoopBridge
{
    internal sealed class BattleSceneCatalogContractRule
    {
        internal BattleSceneCatalogContractRule(
            string targetModuleId,
            string targetVersion,
            string baseModuleId,
            string baseVersion,
            string relativePath,
            string contractPath,
            string sha256)
        {
            TargetModuleId = targetModuleId;
            TargetVersion = targetVersion;
            BaseModuleId = baseModuleId;
            BaseVersion = baseVersion;
            RelativePath = relativePath;
            ContractPath = contractPath;
            Sha256 = sha256;
        }

        internal string TargetModuleId { get; private set; }
        internal string TargetVersion { get; private set; }
        internal string BaseModuleId { get; private set; }
        internal string BaseVersion { get; private set; }
        internal string RelativePath { get; private set; }
        internal string ContractPath { get; private set; }
        internal string Sha256 { get; private set; }
    }

    internal sealed class BattleSceneCatalogCandidate
    {
        private readonly HashSet<int> mapIndices;

        internal BattleSceneCatalogCandidate(
            int ordinal,
            string sceneId,
            string terrain,
            string forestDensity,
            string mapIndicesText,
            IEnumerable<int> parsedMapIndices)
        {
            Ordinal = ordinal;
            SceneId = sceneId;
            Terrain = terrain;
            ForestDensity = forestDensity;
            MapIndicesText = mapIndicesText;
            mapIndices = new HashSet<int>(parsedMapIndices);
        }

        internal int Ordinal { get; private set; }
        internal string SceneId { get; private set; }
        internal string Terrain { get; private set; }
        internal string ForestDensity { get; private set; }
        internal string MapIndicesText { get; private set; }

        internal bool IncludesMapIndex(int mapIndex)
        {
            return mapIndices.Contains(mapIndex);
        }
    }

    internal sealed class BattleSceneCatalogValidationResult
    {
        private readonly BattleSceneCatalogCandidate[] candidates;

        internal BattleSceneCatalogValidationResult(
            BattleSceneCatalogContractRule rule,
            string actualContractSha256,
            IEnumerable<BattleSceneCatalogCandidate> candidates,
            int contentMismatchCount,
            string firstContentMismatch)
        {
            Rule = rule;
            ContractSha256 = actualContractSha256;
            this.candidates = candidates.ToArray();
            ContentMismatchCount = contentMismatchCount;
            FirstContentMismatch = firstContentMismatch ?? string.Empty;
        }

        internal BattleSceneCatalogContractRule Rule { get; private set; }
        internal string ContractSha256 { get; private set; }
        internal int ContentMismatchCount { get; private set; }
        internal string FirstContentMismatch { get; private set; }
        internal bool HasContentDrift { get { return ContentMismatchCount != 0; } }

        internal BattleSceneCatalogCandidate[] GetContractCandidates(int mapIndex)
        {
            return candidates.Where(candidate => candidate.IncludesMapIndex(mapIndex)).ToArray();
        }
    }

    internal static class BattleSceneCatalogContractLoader
    {
        private const string Header = "BCS-BATTLE-SCENE-CATALOG|1";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static BattleSceneCatalogValidationResult LoadAndValidate(
            BattleSceneCatalogContractRule rule,
            IReadOnlyDictionary<string, string> moduleRoots,
            bool validateAssets,
            Action<string> progress,
            Action<string> contentWarning)
        {
            if (rule == null)
                throw new ArgumentNullException("rule");
            if (moduleRoots == null)
                throw new ArgumentNullException("moduleRoots");

            ValidateContractRegularFile(rule.ContractPath);
            var contractBytes = File.ReadAllBytes(rule.ContractPath);
            var actualContractHash = Hash(contractBytes);
            if (!string.Equals(actualContractHash, rule.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Battle-scene catalog contract hash mismatch. Expected " + rule.Sha256 +
                    ", found " + actualContractHash + ".");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(contractBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "Battle-scene catalog contract is not canonical UTF-8.",
                    exception);
            }
            if (text.IndexOf('\r') >= 0 || !text.EndsWith("\n", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Battle-scene catalog contract must use LF endings and end with one LF.");
            }

            var lines = text.Substring(0, text.Length - 1).Split('\n');
            if (lines.Length < 4 || !string.Equals(lines[0], Header, StringComparison.Ordinal) ||
                lines.Any(string.IsNullOrEmpty))
            {
                throw new InvalidDataException("Malformed battle-scene catalog contract header or records.");
            }

            var targetFields = lines[1].Split('|');
            if (targetFields.Length != 5 ||
                !string.Equals(targetFields[0], "TARGET", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Battle-scene catalog contract has no canonical TARGET record.");
            }
            var contractTargetId = Decode(targetFields[1], "TARGET module ID");
            var contractTargetVersion = Decode(targetFields[2], "TARGET version");
            var contractBaseId = Decode(targetFields[3], "TARGET base module ID");
            var contractBaseVersion = Decode(targetFields[4], "TARGET base version");
            if (!string.Equals(contractTargetId, rule.TargetModuleId, StringComparison.Ordinal) ||
                !string.Equals(contractTargetVersion, rule.TargetVersion, StringComparison.Ordinal) ||
                !string.Equals(contractBaseId, rule.BaseModuleId, StringComparison.Ordinal) ||
                !string.Equals(contractBaseVersion, rule.BaseVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Battle-scene catalog TARGET record does not match its schema-3 configuration record.");
            }

            var catalogFields = lines[2].Split('|');
            if (catalogFields.Length != 4 ||
                !string.Equals(catalogFields[0], "CATALOG", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Battle-scene catalog contract has no canonical CATALOG record.");
            }
            var catalogModuleId = Decode(catalogFields[1], "CATALOG module ID");
            var catalogRelativePath = DecodeRelativePath(catalogFields[2], "CATALOG path");
            var catalogHash = RequireSha256(catalogFields[3], "CATALOG hash");
            if (!string.Equals(catalogModuleId, rule.TargetModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Battle-scene CATALOG record must belong to the configured target module.");
            }

            var candidates = new List<BattleSceneCatalogCandidate>();
            var assets = new List<AssetRecord>();
            var assetKeys = new HashSet<string>(StringComparer.Ordinal);
            var assetSectionStarted = false;
            string previousAssetModule = null;
            string previousAssetPath = null;
            for (var lineIndex = 3; lineIndex < lines.Length; lineIndex++)
            {
                var fields = lines[lineIndex].Split('|');
                if (fields.Length == 6 &&
                    string.Equals(fields[0], "CANDIDATE", StringComparison.Ordinal))
                {
                    if (assetSectionStarted)
                    {
                        throw new InvalidDataException(
                            "Battle-scene CANDIDATE record appears after the ASSET section.");
                    }
                    int ordinal;
                    if (!int.TryParse(
                            fields[1],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ordinal) ||
                        ordinal != candidates.Count ||
                        !string.Equals(
                            fields[1],
                            ordinal.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Battle-scene CANDIDATE ordinals must be canonical and contiguous.");
                    }
                    var sceneId = DecodeRequiredValue(fields[2], "CANDIDATE scene ID");
                    var terrain = DecodeRequiredValue(fields[3], "CANDIDATE terrain");
                    var forestDensity = DecodeRequiredValue(fields[4], "CANDIDATE forest density");
                    var mapIndicesText = DecodeRequiredValue(fields[5], "CANDIDATE map indices");
                    candidates.Add(new BattleSceneCatalogCandidate(
                        ordinal,
                        sceneId,
                        terrain,
                        forestDensity,
                        mapIndicesText,
                        ParseMapIndices(mapIndicesText)));
                    continue;
                }

                if (fields.Length == 5 &&
                    string.Equals(fields[0], "ASSET", StringComparison.Ordinal))
                {
                    assetSectionStarted = true;
                    var moduleId = DecodeRequiredValue(fields[1], "ASSET module ID");
                    if (!string.Equals(moduleId, rule.TargetModuleId, StringComparison.Ordinal) &&
                        !string.Equals(moduleId, rule.BaseModuleId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Battle-scene ASSET belongs to an unpinned module: " + moduleId + ".");
                    }
                    var relativePath = DecodeRelativePath(fields[2], "ASSET path");
                    long length;
                    if (!long.TryParse(
                            fields[3],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out length) ||
                        length < 0 ||
                        !string.Equals(
                            fields[3],
                            length.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Battle-scene ASSET length is not canonical.");
                    }
                    var sha256 = RequireSha256(fields[4], "ASSET hash");
                    var key = moduleId + "\0" + relativePath;
                    if (!assetKeys.Add(key))
                        throw new InvalidDataException("Duplicate battle-scene ASSET record: " + relativePath);
                    if (previousAssetModule != null &&
                        (string.CompareOrdinal(previousAssetModule, moduleId) > 0 ||
                         (string.Equals(previousAssetModule, moduleId, StringComparison.Ordinal) &&
                          string.CompareOrdinal(previousAssetPath, relativePath) >= 0)))
                    {
                        throw new InvalidDataException(
                            "Battle-scene ASSET records are not in canonical ordinal order.");
                    }
                    previousAssetModule = moduleId;
                    previousAssetPath = relativePath;
                    assets.Add(new AssetRecord(moduleId, relativePath, length, sha256));
                    continue;
                }

                throw new InvalidDataException(
                    "Malformed battle-scene catalog contract record at line " + (lineIndex + 1) + ".");
            }
            if (candidates.Count == 0 || assets.Count == 0)
                throw new InvalidDataException("Battle-scene catalog contract is incomplete.");

            var mismatchCount = 0;
            string firstMismatch = null;
            Action<string> mismatch = message =>
            {
                mismatchCount++;
                if (firstMismatch == null)
                    firstMismatch = message;
                if (contentWarning != null)
                    contentWarning(message);
            };

            string catalogRoot;
            if (!moduleRoots.TryGetValue(catalogModuleId, out catalogRoot))
            {
                throw new InvalidDataException(
                    "Configured battle-scene catalog module is not installed: " + catalogModuleId + ".");
            }
            ValidateContentFile(
                catalogRoot,
                catalogRelativePath,
                null,
                catalogHash,
                "catalog",
                mismatch);

            if (validateAssets)
            {
                for (var index = 0; index < assets.Count; index++)
                {
                    var asset = assets[index];
                    string moduleRoot;
                    if (!moduleRoots.TryGetValue(asset.ModuleId, out moduleRoot))
                    {
                        throw new InvalidDataException(
                            "Configured battle-scene asset module is not installed: " +
                            asset.ModuleId + ".");
                    }
                    ValidateContentFile(
                        moduleRoot,
                        asset.RelativePath,
                        asset.Length,
                        asset.Sha256,
                        "asset",
                        mismatch);
                    if (progress != null && ((index + 1) % 50 == 0 || index + 1 == assets.Count))
                    {
                        progress(
                            "Battle-scene asset validation " + (index + 1) + "/" + assets.Count);
                    }
                }
            }

            return new BattleSceneCatalogValidationResult(
                rule,
                actualContractHash,
                candidates,
                mismatchCount,
                firstMismatch);
        }

        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        private static void ValidateContentFile(
            string moduleRoot,
            string relativePath,
            long? expectedLength,
            string expectedHash,
            string kind,
            Action<string> mismatch)
        {
            string path;
            try
            {
                path = ResolveSafeRelativePath(moduleRoot, relativePath);
                if (!IsUnlinkedRegularFile(moduleRoot, path))
                {
                    mismatch(
                        "Battle-scene " + kind + " is missing or linked: " + relativePath + ".");
                    return;
                }
                var info = new FileInfo(path);
                if (expectedLength.HasValue && info.Length != expectedLength.Value)
                {
                    mismatch(
                        "Battle-scene " + kind + " length mismatch: " + relativePath +
                        " expected=" + expectedLength.Value.ToString(CultureInfo.InvariantCulture) +
                        " actual=" + info.Length.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                }
                var actualHash = HashFile(path);
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                {
                    mismatch(
                        "Battle-scene " + kind + " hash mismatch: " + relativePath +
                        " expected=" + expectedHash + " actual=" + actualHash + ".");
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidDataException)
            {
                mismatch(
                    "Battle-scene " + kind + " could not be validated: " + relativePath +
                    " (" + exception.GetType().Name + ": " + exception.Message + ").");
            }
        }

        private static bool IsUnlinkedRegularFile(string root, string path)
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            var canonicalRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            for (var parent = Directory.GetParent(path); parent != null; parent = parent.Parent)
            {
                var parentPath = Path.GetFullPath(parent.FullName).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if ((File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (string.Equals(parentPath, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void ValidateContractRegularFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileNotFoundException(
                    "Battle-scene catalog contract is missing or linked.",
                    path);
            }
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static string ResolveSafeRelativePath(string root, string relativePath)
        {
            var canonicalRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Battle-scene path escaped its module: " + relativePath);
            return path;
        }

        private static string DecodeRelativePath(string value, string description)
        {
            var decoded = DecodeRequiredValue(value, description).Replace('\\', '/');
            if (Path.IsPathRooted(decoded) || decoded.StartsWith("/", StringComparison.Ordinal) ||
                decoded.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(description + " is not a safe relative path.");
            }
            var segments = decoded.Split('/');
            if (segments.Length == 0 || segments.Any(segment =>
                    string.IsNullOrEmpty(segment) || segment == "." || segment == ".."))
            {
                throw new InvalidDataException(description + " is not a safe relative path.");
            }
            return decoded;
        }

        private static IEnumerable<int> ParseMapIndices(string text)
        {
            var result = new List<int>();
            foreach (var field in text.Split(','))
            {
                int value;
                if (!int.TryParse(
                        field,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out value) ||
                    value < 0 ||
                    !string.Equals(
                        field,
                        value.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal) ||
                    result.Contains(value))
                {
                    throw new InvalidDataException(
                        "Battle-scene CANDIDATE map indices are not canonical.");
                }
                result.Add(value);
            }
            if (result.Count == 0)
                throw new InvalidDataException("Battle-scene CANDIDATE has no map index.");
            return result;
        }

        private static string DecodeRequiredValue(string value, string description)
        {
            var decoded = Decode(value, description);
            if (string.IsNullOrWhiteSpace(decoded) || decoded.IndexOf('|') >= 0 ||
                decoded.IndexOf('\r') >= 0 || decoded.IndexOf('\n') >= 0)
            {
                throw new InvalidDataException(description + " is empty or contains a delimiter.");
            }
            return decoded;
        }

        private static string Decode(string value, string description)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(value);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(description + " is not base64.", exception);
            }
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                throw new InvalidDataException(description + " is not canonical base64.");
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(description + " is not UTF-8.", exception);
            }
        }

        private static string RequireSha256(string value, string description)
        {
            if (value == null || value.Length != 64 || value.Any(character =>
                    !(character >= '0' && character <= '9') &&
                    !(character >= 'A' && character <= 'F')))
            {
                throw new InvalidDataException(description + " is not an uppercase SHA-256 value.");
            }
            return value;
        }

        private sealed class AssetRecord
        {
            internal AssetRecord(string moduleId, string relativePath, long length, string sha256)
            {
                ModuleId = moduleId;
                RelativePath = relativePath;
                Length = length;
                Sha256 = sha256;
            }

            internal string ModuleId { get; private set; }
            internal string RelativePath { get; private set; }
            internal long Length { get; private set; }
            internal string Sha256 { get; private set; }
        }
    }
}
