using System.IO;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Safely copies dropped module folders into the configured dedicated server.
/// Sources are never changed and existing destination folders are never overwritten.
/// </summary>
public sealed class ModuleImporter
{
    private readonly string _modulesDirectory;
    private readonly ModuleScanner _scanner;

    public ModuleImporter(string modulesDirectory, ModuleScanner scanner)
    {
        _modulesDirectory = Path.GetFullPath(modulesDirectory);
        _scanner = scanner;
    }

    public IReadOnlyList<ModuleImportCandidate> Discover(IEnumerable<string> droppedPaths)
    {
        var candidates = new Dictionary<string, ModuleImportCandidate>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var droppedPath in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(droppedPath) || !Directory.Exists(droppedPath))
                throw new InvalidDataException(
                    $"Only folders can be imported: {droppedPath}");

            var fullPath = Path.GetFullPath(droppedPath);
            RejectReparsePoint(fullPath);

            if (File.Exists(Path.Combine(fullPath, "SubModule.xml")))
            {
                AddCandidate(candidates, fullPath);
                continue;
            }

            var childModules = Directory.EnumerateDirectories(fullPath)
                .Where(path => File.Exists(Path.Combine(path, "SubModule.xml")))
                .ToArray();
            if (childModules.Length == 0)
            {
                throw new InvalidDataException(
                    $"Folder contains no module with SubModule.xml: {fullPath}");
            }

            foreach (var childModule in childModules)
            {
                RejectReparsePoint(childModule);
                AddCandidate(candidates, childModule);
            }
        }

        if (candidates.Count == 0)
            throw new InvalidDataException("No module folders were dropped.");

        foreach (var candidate in candidates.Values)
        {
            var destination = Path.Combine(_modulesDirectory, candidate.FolderName);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException(
                    $"Module folder already exists; nothing was overwritten: {destination}");
            }
        }

        return candidates.Values
            .OrderBy(candidate => candidate.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<BannerlordModule> Import(
        IReadOnlyList<ModuleImportCandidate> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one module folder is required.", nameof(candidates));
        if (!Directory.Exists(_modulesDirectory))
            throw new DirectoryNotFoundException(
                $"Dedicated-server Modules directory was not found: {_modulesDirectory}");

        var refreshed = Discover(candidates.Select(candidate => candidate.SourcePath));
        if (refreshed.Count != candidates.Count ||
            !refreshed.Select(candidate => candidate.SourcePath).SequenceEqual(
                candidates.Select(candidate => candidate.SourcePath),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException("Dropped module folders changed before import. Drop them again.");
        }

        var existingIds = _scanner.Scan(_modulesDirectory)
            .Select(module => module.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stagingDirectory = Path.Combine(
            _modulesDirectory,
            $".bcs-import-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var movedDestinations = new List<string>();

        try
        {
            foreach (var candidate in candidates)
            {
                CopyDirectory(
                    candidate.SourcePath,
                    Path.Combine(stagingDirectory, candidate.FolderName));
            }

            var imported = _scanner.Scan(stagingDirectory);
            if (imported.Count != candidates.Count)
                throw new InvalidDataException("Not every staged folder contained a valid SubModule.xml.");

            var duplicateId = imported.FirstOrDefault(module => existingIds.Contains(module.Id));
            if (duplicateId is not null)
            {
                throw new InvalidDataException(
                    $"Module ID is already installed: {duplicateId.Id}");
            }

            foreach (var candidate in candidates)
            {
                var staged = Path.Combine(stagingDirectory, candidate.FolderName);
                var destination = Path.Combine(_modulesDirectory, candidate.FolderName);
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new IOException($"Module folder appeared during import: {destination}");

                Directory.Move(staged, destination);
                movedDestinations.Add(destination);
            }

            return imported;
        }
        catch
        {
            for (var index = movedDestinations.Count - 1; index >= 0; index--)
            {
                var destination = movedDestinations[index];
                var staged = Path.Combine(stagingDirectory, Path.GetFileName(destination));
                if (Directory.Exists(destination) && !Directory.Exists(staged))
                    Directory.Move(destination, staged);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static void AddCandidate(
        IDictionary<string, ModuleImportCandidate> candidates,
        string sourcePath)
    {
        var folderName = new DirectoryInfo(sourcePath).Name;
        if (string.IsNullOrWhiteSpace(folderName))
            throw new InvalidDataException($"Module folder has no usable name: {sourcePath}");
        if (candidates.Values.Any(candidate =>
                candidate.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase) &&
                !candidate.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Multiple dropped modules use folder name '{folderName}'.");
        }

        candidates[sourcePath] = new ModuleImportCandidate(sourcePath, folderName);
    }

    private static void CopyDirectory(string source, string destination)
    {
        RejectReparsePoint(source);
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            RejectReparsePoint(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            RejectReparsePoint(directory);
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Linked folders and files cannot be imported: {path}");
    }
}

public sealed record ModuleImportCandidate(string SourcePath, string FolderName);
