using System.IO;
using System.Text.Json;
using BCSTool.Models;
using Microsoft.VisualBasic.FileIO;

namespace BCSTool.Services;

/// <summary>
/// Removes one non-core module through a reversible quarantine/profile update,
/// then sends the exact module folder to the Windows Recycle Bin.
/// </summary>
public sealed class ModuleRemovalService
{
    private readonly ModuleManager _moduleManager;
    private readonly IModuleDirectoryRecycler _recycler;
    private readonly BridgeInstallationService _bridgeInstallationService;

    public ModuleRemovalService(
        ModuleManager moduleManager,
        IModuleDirectoryRecycler recycler,
        BridgeInstallationService bridgeInstallationService)
    {
        _moduleManager = moduleManager;
        _recycler = recycler;
        _bridgeInstallationService = bridgeInstallationService;
    }

    public void Remove(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> currentModules)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(currentModules);

        var removalBlocker = GetRemovalBlocker(module, currentModules);
        if (removalBlocker is not null)
            throw new InvalidOperationException(removalBlocker);
        if (IsGeneratedBridge(module))
            ValidateActiveGeneratedBridgeReplacement(module, currentModules);

        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(module.Path));
        var modulesDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_moduleManager.ModulesDirectory));
        var sourceParent = Directory.GetParent(source)?.FullName;
        if (sourceParent is null ||
            !Path.TrimEndingDirectorySeparator(sourceParent).Equals(
                modulesDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Module folder is not a direct child of the dedicated-server Modules directory.");
        }
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Module folder was not found: {source}");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked module folders cannot be deleted by Bannerlord Coop Manager.");

        var stagingRoot = Path.Combine(
            _moduleManager.ServerRoot,
            $".bcs-remove-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var quarantined = Path.Combine(stagingRoot, Path.GetFileName(source));
        Directory.CreateDirectory(stagingRoot);
        var profileUpdated = false;
        var generatedBridge = IsGeneratedBridge(module);

        try
        {
            Directory.Move(source, quarantined);
            if (generatedBridge)
            {
                // Keep the disabled profile entry byte-for-byte so the current
                // bridge installation backup remains revertible.
                _moduleManager.EnsureProfileStillCurrent();
            }
            else
            {
                _moduleManager.Save(
                    currentModules.Where(item => !ReferenceEquals(item, module)).ToArray());
                profileUpdated = true;
            }
            _recycler.Recycle(quarantined);
        }
        catch (Exception removalException)
        {
            var rollbackErrors = new List<Exception>();
            if (profileUpdated)
            {
                try
                {
                    _moduleManager.Save(currentModules);
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(exception);
                }
            }

            try
            {
                if (Directory.Exists(quarantined) && !Directory.Exists(source))
                    Directory.Move(quarantined, source);
            }
            catch (Exception exception)
            {
                rollbackErrors.Add(exception);
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Module removal failed and could not be completely rolled back.",
                    new[] { removalException }.Concat(rollbackErrors));
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingRoot) &&
                !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            {
                Directory.Delete(stagingRoot);
            }
        }
    }

    internal string? GetRemovalBlocker(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> currentModules)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(currentModules);

        if (module.IsRequired)
            return "Required server modules cannot be deleted.";
        if (!module.IsInstalled || string.IsNullOrWhiteSpace(module.Path))
            return "Only an installed module folder can be deleted.";
        if (!IsGeneratedBridge(module))
            return null;
        if (module.Enabled)
        {
            return "The active generated bridge cannot be deleted. " +
                   "Prepare and activate its replacement first.";
        }

        var activeGeneratedBridges = currentModules
            .Where(candidate =>
                !ReferenceEquals(candidate, module) &&
                candidate.IsInstalled &&
                candidate.Enabled &&
                IsGeneratedBridge(candidate))
            .ToArray();
        if (activeGeneratedBridges.Length != 1)
        {
            return "An inactive generated bridge can be deleted only when exactly one " +
                   "other generated bridge is active.";
        }

        var enabledDependents = currentModules
            .Where(candidate =>
                !ReferenceEquals(candidate, module) &&
                candidate.Enabled &&
                candidate.Dependencies.Contains(
                    module.Id,
                    StringComparer.OrdinalIgnoreCase))
            .Select(candidate => candidate.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (enabledDependents.Length > 0)
        {
            return "The inactive generated bridge is still required by enabled module(s): " +
                   string.Join(", ", enabledDependents) + ".";
        }

        try
        {
            var rollbackBridge =
                _bridgeInstallationService.FindRollbackRequiredGeneratedBridge(
                    _moduleManager.ServerRoot);
            if (rollbackBridge?.Equals(
                    module.Id,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return "This historical bridge is required by the latest Revert Bridge Install " +
                       "backup and cannot be deleted yet.";
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidDataException or
                JsonException or UnauthorizedAccessException)
        {
            return "Historical bridge deletion is blocked because the latest bridge rollback " +
                   "backup could not be validated: " + exception.Message;
        }

        return null;
    }

    private void ValidateActiveGeneratedBridgeReplacement(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> currentModules)
    {
        var replacements = currentModules
            .Where(candidate =>
                !ReferenceEquals(candidate, module) &&
                candidate.IsInstalled &&
                candidate.Enabled &&
                IsGeneratedBridge(candidate))
            .ToArray();
        if (replacements.Length != 1 || string.IsNullOrWhiteSpace(replacements[0].Path))
        {
            throw new InvalidOperationException(
                "Historical bridge deletion requires exactly one installed active replacement.");
        }

        var replacementPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(replacements[0].Path));
        var modulesDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_moduleManager.ModulesDirectory));
        var replacementParent = Directory.GetParent(replacementPath)?.FullName;
        if (replacementParent is null ||
            !Path.TrimEndingDirectorySeparator(replacementParent).Equals(
                modulesDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(replacementPath) ||
            (File.GetAttributes(replacementPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The active replacement bridge folder is missing, linked, or outside the " +
                "dedicated-server Modules directory. Rescan before deleting historical bridges.");
        }

        _bridgeInstallationService.ValidateGeneratedBridgeForActivation(
            _moduleManager.ServerRoot,
            replacements[0].Id);
    }

    private static bool IsGeneratedBridge(BannerlordModule module) =>
        module.Id.StartsWith(
            CoopBridgePackageBuilder.BridgeIdPrefix,
            StringComparison.OrdinalIgnoreCase);
}

public interface IModuleDirectoryRecycler
{
    void Recycle(string directoryPath);
}

public sealed class WindowsModuleDirectoryRecycler : IModuleDirectoryRecycler
{
    public void Recycle(string directoryPath)
    {
        FileSystem.DeleteDirectory(
            directoryPath,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
    }
}
