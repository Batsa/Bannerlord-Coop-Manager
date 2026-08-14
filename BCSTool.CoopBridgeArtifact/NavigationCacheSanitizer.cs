using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BCS.CoopBridge
{
    internal static class NavigationCacheSanitizer
    {
        private const int MaximumRecordCount = 10000000;

        internal static void ValidateCompatibleSave(
            string sourcePath,
            object cacheInstance,
            Action<string> trace)
        {
            if (sourcePath == null)
                throw new ArgumentNullException("sourcePath");
            if (cacheInstance == null)
                throw new ArgumentNullException("cacheInstance");

            var navigationType = ReadNavigationType(cacheInstance);
            var hasLandRatio = navigationType == 3;
            var ids = CollectIds(sourcePath, hasLandRatio);
            var missingIds = ResolveMissingSettlementIds(ids);
            if (missingIds.Count == 0)
            {
                trace(
                    "Navigation cache validated against " + ids.Count +
                    " settlement ID(s) in the loaded campaign save.");
                return;
            }

            var examples = string.Join(
                ", ",
                missingIds.OrderBy(id => id, StringComparer.Ordinal).Take(8));
            trace(
                "Navigation cache validation failed: " + missingIds.Count +
                " of " + ids.Count + " settlement ID(s) are absent from the loaded save. " +
                "Examples: " + examples);
            throw new InvalidDataException(
                "The selected campaign save is not compatible with the active total-conversion " +
                "module. Its navigation cache references " + missingIds.Count + " settlement " +
                "ID(s) that are absent from the save (for example: " + examples + "). " +
                "Create and save a new campaign with the same module enabled, then select that " +
                "save in Bannerlord Coop Manager. No navigation data was removed or rewritten.");
        }

        private static int ReadNavigationType(object cacheInstance)
        {
            for (var type = cacheInstance.GetType(); type != null; type = type.BaseType)
            {
                var getter = type.GetMethod(
                    "get__navigationType",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                if (getter != null)
                    return Convert.ToInt32(getter.Invoke(cacheInstance, null));
            }
            throw new MissingMethodException(
                cacheInstance.GetType().FullName,
                "get__navigationType");
        }

        private static HashSet<string> ResolveMissingSettlementIds(IEnumerable<string> ids)
        {
            var settlementType = AccessTools.TypeByName(
                "TaleWorlds.CampaignSystem.Settlements.Settlement");
            if (settlementType == null)
                throw new TypeLoadException("Bannerlord Settlement type was not found.");
            var find = settlementType.GetMethod(
                "Find",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (find == null)
                throw new MissingMethodException(settlementType.FullName, "Find(string)");

            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (find.Invoke(null, new object[] { id }) == null)
                    missing.Add(id);
            }
            return missing;
        }

        private static HashSet<string> CollectIds(string path, bool hasLandRatio)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                reader.ReadUInt32();
                reader.ReadUInt32();
                var sourceCount = ReadCount(reader, "distance source");
                for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
                {
                    ids.Add(reader.ReadString());
                    reader.ReadBoolean();
                    var targetCount = ReadCount(reader, "distance target");
                    for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
                    {
                        ids.Add(reader.ReadString());
                        reader.ReadBoolean();
                        reader.ReadSingle();
                        if (hasLandRatio)
                            reader.ReadSingle();
                    }
                }

                var neighborCount = ReadCount(reader, "fortification neighbor");
                for (var index = 0; index < neighborCount; index++)
                {
                    ids.Add(reader.ReadString());
                    ids.Add(reader.ReadString());
                }

                var faceCount = ReadCount(reader, "closest-settlement face");
                for (var index = 0; index < faceCount; index++)
                {
                    reader.ReadInt32();
                    ids.Add(reader.ReadString());
                    reader.ReadBoolean();
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Navigation cache contains trailing data.");
            }
            return ids;
        }

        private static int ReadCount(BinaryReader reader, string recordType)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumRecordCount)
                throw new InvalidDataException("Invalid " + recordType + " count: " + count);
            return count;
        }

    }
}
