using System;
using System.IO;
using System.Security.Cryptography;

namespace BCSTool.Services;

/// <summary>
/// Trusted, reviewed battle-scene contracts. These artifacts are release
/// inputs; they are never generated from an operator's installed modules.
/// </summary>
internal static class BattleSceneCatalogContractRegistry
{
    private const string Europe1700Resource =
        "BCSTool.Assets.BattleSceneCatalog.europe-1700-1.4.7.1-sandboxcore-1.4.8.bcs";
    private const string Europe1700Hash =
        "73A8FE6A386CAC331CA25182AE3268E7A52E7DE8BA21A954F1FC0B5A3FB61D54";

    internal static BridgeBattleSceneCatalogContract CreateEurope1700()
    {
        using var stream = typeof(BattleSceneCatalogContractRegistry).Assembly
            .GetManifestResourceStream(Europe1700Resource)
            ?? throw new InvalidOperationException(
                $"Embedded battle-scene catalog contract is missing: {Europe1700Resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var content = buffer.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(content));
        if (!actualHash.Equals(Europe1700Hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Embedded EOE battle-scene catalog contract failed integrity validation. " +
                $"Expected {Europe1700Hash}, found {actualHash}.");
        }

        return new BridgeBattleSceneCatalogContract(
            "Europe1700",
            "v1.4.7.1",
            "SandBoxCore",
            "v1.4.8",
            "BattleSceneCatalog/europe-1700-1.4.7.1-sandboxcore-1.4.8.bcs",
            Europe1700Hash,
            content);
    }
}
