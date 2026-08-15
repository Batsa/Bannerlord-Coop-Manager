using System.Collections.ObjectModel;
using System.IO;

namespace BCSTool.Models;

/// <summary>
/// Immutable selection of declared submodule DLLs for one bridge-managed module.
/// Available DLLs retain manifest declaration order; selected DLLs are normalized
/// to that same order.
/// </summary>
public sealed class BridgeDllSelection
{
    private readonly HashSet<string> _selectedDllNames;

    public BridgeDllSelection(
        string moduleId,
        string moduleVersion,
        IEnumerable<string> availableDllNames,
        IEnumerable<string> selectedDllNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(moduleVersion);
        ArgumentNullException.ThrowIfNull(availableDllNames);
        ArgumentNullException.ThrowIfNull(selectedDllNames);

        ValidateModuleValue(moduleId, "Module ID");
        ValidateModuleValue(moduleVersion, "Module version", allowEmpty: true);
        if (!Path.GetFileName(moduleId).Equals(moduleId, StringComparison.Ordinal) ||
            moduleId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"Unsafe bridge module ID: '{moduleId}'.");
        }

        var available = MaterializeDllNames(availableDllNames, "available");
        var selectedInput = MaterializeDllNames(selectedDllNames, "selected");
        var availableSet = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var selectedName in selectedInput)
        {
            if (!availableSet.Contains(selectedName))
            {
                throw new InvalidDataException(
                    $"Selected bridge DLL is not declared by module '{moduleId}': {selectedName}");
            }
        }

        var selectedSet = selectedInput.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedSelected = available
            .Where(selectedSet.Contains)
            .ToArray();

        ModuleId = moduleId;
        ModuleVersion = moduleVersion;
        AvailableDllNames = new ReadOnlyCollection<string>(available);
        SelectedDllNames = new ReadOnlyCollection<string>(normalizedSelected);
        _selectedDllNames = normalizedSelected.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string ModuleId { get; }

    public string ModuleVersion { get; }

    public IReadOnlyList<string> AvailableDllNames { get; }

    public IReadOnlyList<string> SelectedDllNames { get; }

    public bool IsSelected(string dllName) =>
        !string.IsNullOrWhiteSpace(dllName) && _selectedDllNames.Contains(dllName);

    public BridgeDllSelection WithSelectedDllNames(IEnumerable<string> selectedDllNames) =>
        new(ModuleId, ModuleVersion, AvailableDllNames, selectedDllNames);

    internal static void ValidateDllName(string dllName, string description)
    {
        if (string.IsNullOrWhiteSpace(dllName) ||
            !dllName.Equals(dllName.Trim(), StringComparison.Ordinal) ||
            !Path.GetFileName(dllName).Equals(dllName, StringComparison.Ordinal) ||
            !Path.GetExtension(dllName).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            dllName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            dllName.Any(char.IsControl))
        {
            throw new InvalidDataException($"Unsafe {description} bridge DLL name: '{dllName}'.");
        }
    }

    private static string[] MaterializeDllNames(
        IEnumerable<string> names,
        string description)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            ValidateDllName(name, description);
            if (!seen.Add(name))
                throw new InvalidDataException($"Duplicate {description} bridge DLL name: {name}");
            result.Add(name);
        }

        return result.ToArray();
    }

    private static void ValidateModuleValue(
        string value,
        string description,
        bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            (!string.IsNullOrEmpty(value) && !value.Equals(value.Trim(), StringComparison.Ordinal)) ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{description} is empty or unsafe.");
        }
    }
}
