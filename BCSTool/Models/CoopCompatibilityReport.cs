using System.Text;

namespace BCSTool.Models;

/// <summary>
/// Conservative result of a read-only static module inspection.
/// Static analysis can identify blockers and risks, but cannot prove that a
/// client can join, save, reconnect, or play a campaign successfully.
/// </summary>
public enum CoopCompatibilityStatus
{
    LikelyCompatible,
    TestingRequired,
    BridgeLikelyRequired,
    Blocked
}

public enum CompatibilityFindingSeverity
{
    Pass,
    Information,
    Unknown,
    Warning,
    HighRisk,
    Blocker
}

public sealed record CoopCompatibilityFinding(
    CompatibilityFindingSeverity Severity,
    string Area,
    string Finding,
    string Evidence,
    string Recommendation)
{
    public string SeverityText => Severity switch
    {
        CompatibilityFindingSeverity.HighRisk => "High risk",
        _ => Severity.ToString()
    };
}

public sealed class CoopCompatibilityReport
{
    public required string ModuleName { get; init; }
    public required string ModuleId { get; init; }
    public required string ModuleVersion { get; init; }
    public required string ModulePath { get; init; }
    public required string ModuleFingerprint { get; init; }
    public required string GameVersion { get; init; }
    public required string CoopVersion { get; init; }
    public required string CoopModulePath { get; init; }
    public required string CoopFingerprint { get; init; }
    public required DateTime GeneratedUtc { get; init; }
    public required CoopCompatibilityStatus OverallStatus { get; init; }
    public required IReadOnlyList<CoopCompatibilityFinding> Findings { get; init; }
    public required int FileCount { get; init; }
    public required long TotalSizeBytes { get; init; }
    public required int ManagedAssemblyCount { get; init; }
    public required int NativeAssemblyCount { get; init; }
    public required int XmlFileCount { get; init; }

    public string StatusText => OverallStatus switch
    {
        CoopCompatibilityStatus.LikelyCompatible => "Likely compatible",
        CoopCompatibilityStatus.TestingRequired => "Testing required",
        CoopCompatibilityStatus.BridgeLikelyRequired => "Bridge likely required",
        CoopCompatibilityStatus.Blocked => "Blocked",
        _ => OverallStatus.ToString()
    };

    public string StatusSummary => OverallStatus switch
    {
        CoopCompatibilityStatus.LikelyCompatible =>
            "No static blocker or executable gameplay code was detected. A real load/join/save test is still required.",
        CoopCompatibilityStatus.TestingRequired =>
            "Static inspection cannot decide compatibility. Test this exact artifact on the released Coop server and clients.",
        CoopCompatibilityStatus.BridgeLikelyRequired =>
            "Custom code touches systems that commonly need Coop authority, registry, serialization, or synchronization work.",
        CoopCompatibilityStatus.Blocked =>
            "At least one concrete installation, dependency, manifest, or profile blocker must be resolved before testing.",
        _ => string.Empty
    };

    public string GeneratedText => $"Generated {GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

    public string InventoryText =>
        $"Files: {FileCount:N0}   Size: {FormatBytes(TotalSizeBytes)}   " +
        $"Managed DLLs: {ManagedAssemblyCount:N0}   Native/unreadable DLLs: {NativeAssemblyCount:N0}   " +
        $"XML/XSLT: {XmlFileCount:N0}";

    public string BaselineText =>
        string.IsNullOrWhiteSpace(CoopVersion)
            ? "Installed Coop baseline: UNKNOWN"
            : $"Installed baseline: Bannerlord {ValueOrUnknown(GameVersion)} / Coop {CoopVersion}";

    public string ToPlainText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Bannerlord Coop Manager - Coop Compatibility Report");
        builder.AppendLine($"Generated (UTC): {GeneratedUtc:O}");
        builder.AppendLine($"Module: {ModuleName} ({ModuleId}) {ModuleVersion}");
        builder.AppendLine($"Path: {ModulePath}");
        builder.AppendLine($"Result: {StatusText}");
        builder.AppendLine(StatusSummary);
        builder.AppendLine(BaselineText);
        builder.AppendLine($"Module analysis fingerprint (manifest/DLL/XML/XSLT): {ModuleFingerprint}");
        builder.AppendLine($"Coop analysis fingerprint (manifest/DLL/XML/XSLT): {CoopFingerprint}");
        builder.AppendLine(InventoryText);
        builder.AppendLine();

        foreach (var finding in Findings)
        {
            builder.AppendLine($"[{finding.SeverityText}] {finding.Area}: {finding.Finding}");
            builder.AppendLine($"Evidence: {finding.Evidence}");
            builder.AppendLine($"Next: {finding.Recommendation}");
            builder.AppendLine();
        }

        builder.AppendLine(
            "Static analysis is not proof of successful server load, client join, save, reconnect, battle, or long-session behavior.");
        return builder.ToString();
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value;

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var scaled = (double)value;
        var suffixIndex = 0;

        while (scaled >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            scaled /= 1024;
            suffixIndex++;
        }

        return $"{scaled:0.##} {suffixes[suffixIndex]}";
    }
}
