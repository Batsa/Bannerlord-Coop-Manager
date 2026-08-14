using System.IO;
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

    public ModuleRemovalService(
        ModuleManager moduleManager,
        IModuleDirectoryRecycler recycler)
    {
        _moduleManager = moduleManager;
        _recycler = recycler;
    }

    public void Remove(
        BannerlordModule module,
        IReadOnlyList<BannerlordModule> currentModules)
    {
        if (module.IsRequired)
            throw new InvalidOperationException("Required server modules cannot be deleted.");
        if (!module.IsInstalled || string.IsNullOrWhiteSpace(module.Path))
            throw new InvalidOperationException("Only an installed module folder can be deleted.");

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

        try
        {
            Directory.Move(source, quarantined);
            _moduleManager.Save(currentModules.Where(item => !ReferenceEquals(item, module)).ToArray());
            profileUpdated = true;
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
