using System;
using System.Text;

namespace BCSTool.Services;

internal static class CoopSaveNamePolicy
{
    private const string ReservedTemplateName = "default_new_game";
    private const string ImportedSaveFallback = "Imported_Save";

    internal static void EnsureValid(string? saveName)
    {
        if (string.IsNullOrWhiteSpace(saveName))
            throw new InvalidOperationException("Save name cannot be empty.");

        if (saveName.Equals(ReservedTemplateName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Save name '{ReservedTemplateName}' is reserved by Coop.");
        }

        if (saveName.Any(character => !IsAllowed(character)))
        {
            throw new InvalidOperationException(
                "Save name may contain only ASCII letters, digits, and underscores.");
        }
    }

    internal static string SanitizeForServer(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var result = new StringBuilder(sourceName.Length);
        var pendingSeparator = false;
        foreach (var character in sourceName)
        {
            if (!IsAllowed(character))
            {
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && result.Length > 0 && result[^1] != '_')
                result.Append('_');

            pendingSeparator = false;
            result.Append(character);
        }

        var sanitized = result.Length == 0
            ? ImportedSaveFallback
            : result.ToString();
        if (sanitized.Equals(ReservedTemplateName, StringComparison.OrdinalIgnoreCase))
            sanitized = ImportedSaveFallback;

        EnsureValid(sanitized);
        return sanitized;
    }

    private static bool IsAllowed(char character) =>
        character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '_';
}
