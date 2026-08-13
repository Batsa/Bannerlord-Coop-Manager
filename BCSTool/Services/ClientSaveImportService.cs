using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace BCSTool.Services;

/// <summary>
/// Imports an existing single-player Bannerlord campaign into Coop's
/// dedicated-server save directory without overwriting any server state.
/// Coop creates its own sidecar JSON after the imported campaign is hosted.
/// </summary>
public sealed class ClientSaveImportService
{
    private const string SaveExtension = ".sav";

    private readonly string _clientSaveDirectory;
    private readonly string _serverSaveDirectory;

    public ClientSaveImportService(CoopConfigService configService)
        : this(
            configService.ClientSaveDirectory,
            configService.ServerSaveDirectory)
    {
    }

    internal ClientSaveImportService(
        string clientSaveDirectory,
        string serverSaveDirectory)
    {
        if (string.IsNullOrWhiteSpace(clientSaveDirectory))
            throw new ArgumentException("Client save directory cannot be empty.", nameof(clientSaveDirectory));
        if (string.IsNullOrWhiteSpace(serverSaveDirectory))
            throw new ArgumentException("Server save directory cannot be empty.", nameof(serverSaveDirectory));

        _clientSaveDirectory = Path.GetFullPath(clientSaveDirectory);
        _serverSaveDirectory = Path.GetFullPath(serverSaveDirectory);
    }

    public string ClientSaveDirectory => _clientSaveDirectory;

    public string ServerSaveDirectory => _serverSaveDirectory;

    public IReadOnlyList<string> GetClientSaveNames()
    {
        if (!Directory.Exists(_clientSaveDirectory))
            return Array.Empty<string>();

        EnsureUnlinkedDirectory(_clientSaveDirectory, "Client save directory");

        return Directory
            .EnumerateFiles(_clientSaveDirectory, "*.sav", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file =>
                file.Length > 0 &&
                !file.Name.Equals("default_new_game.sav", StringComparison.OrdinalIgnoreCase) &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(file => Path.GetFileNameWithoutExtension(file.Name))
            .ToArray();
    }

    public ClientSaveImportResult Import(string saveName)
    {
        var sourceName = NormalizeSaveName(saveName);
        var destinationBaseName = CoopSaveNamePolicy.SanitizeForServer(sourceName);
        var sourcePath = ResolveDirectChild(
            _clientSaveDirectory,
            sourceName + SaveExtension,
            "Client save");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected client campaign save no longer exists.", sourcePath);

        var source = new FileInfo(sourcePath);
        if (source.Length == 0)
            throw new InvalidDataException("The selected client campaign save is empty.");
        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked client campaign saves cannot be imported.");

        Directory.CreateDirectory(_serverSaveDirectory);
        EnsureUnlinkedDirectory(_clientSaveDirectory, "Client save directory");
        EnsureUnlinkedDirectory(_serverSaveDirectory, "Server save directory");
        var sourceHash = HashFile(sourcePath);
        var destination = FindDestination(sourceName, destinationBaseName, sourceHash);
        if (destination.AlreadyPresent)
        {
            return new ClientSaveImportResult(
                destination.SaveName,
                sourcePath,
                destination.Path,
                AlreadyPresent: true,
                RenamedForCompatibility: !destinationBaseName.Equals(
                    sourceName,
                    StringComparison.OrdinalIgnoreCase),
                RenamedForCollision: !destination.SaveName.Equals(
                    destinationBaseName,
                    StringComparison.OrdinalIgnoreCase));
        }

        var stagingPath = ResolveDirectChild(
            _serverSaveDirectory,
            ".bcs-client-save-import-" + Guid.NewGuid().ToString("N") + ".tmp",
            "Import staging file");
        try
        {
            using (var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!HashFile(stagingPath).Equals(sourceHash, StringComparison.Ordinal) ||
                !HashFile(sourcePath).Equals(sourceHash, StringComparison.Ordinal))
            {
                throw new IOException(
                    "The client campaign save changed while it was being imported. " +
                    "Exit Bannerlord completely and try again.");
            }

            File.SetLastWriteTimeUtc(stagingPath, source.LastWriteTimeUtc);
            File.Move(stagingPath, destination.Path);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }

        return new ClientSaveImportResult(
            destination.SaveName,
            sourcePath,
            destination.Path,
            AlreadyPresent: false,
            RenamedForCompatibility: !destinationBaseName.Equals(
                sourceName,
                StringComparison.OrdinalIgnoreCase),
            RenamedForCollision: !destination.SaveName.Equals(
                destinationBaseName,
                StringComparison.OrdinalIgnoreCase));
    }

    private SaveDestination FindDestination(
        string sourceName,
        string destinationBaseName,
        string sourceHash)
    {
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            var candidateName = suffix switch
            {
                0 => destinationBaseName,
                1 => destinationBaseName + "_client",
                _ => destinationBaseName + "_client_" + suffix
            };
            var candidatePath = ResolveDirectChild(
                _serverSaveDirectory,
                candidateName + SaveExtension,
                "Server save");
            var sidecarPath = ResolveDirectChild(
                _serverSaveDirectory,
                candidateName + ".json",
                "Server save sidecar");

            if (File.Exists(candidatePath))
            {
                var existing = new FileInfo(candidatePath);
                if ((existing.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (existing.Length == new FileInfo(
                        ResolveDirectChild(
                            _clientSaveDirectory,
                            sourceName + SaveExtension,
                            "Client save")).Length &&
                    HashFile(candidatePath).Equals(sourceHash, StringComparison.Ordinal))
                {
                    return new SaveDestination(candidateName, candidatePath, AlreadyPresent: true);
                }
                continue;
            }

            if (File.Exists(sidecarPath))
                continue;

            return new SaveDestination(candidateName, candidatePath, AlreadyPresent: false);
        }

        throw new IOException("Could not allocate a collision-free server save name.");
    }

    private static string NormalizeSaveName(string saveName)
    {
        if (string.IsNullOrWhiteSpace(saveName))
            throw new ArgumentException("A client save must be selected.", nameof(saveName));

        var normalized = saveName.Trim();
        if (normalized.EndsWith(SaveExtension, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^SaveExtension.Length];

        if (normalized.Length == 0 ||
            !Path.GetFileName(normalized).Equals(normalized, StringComparison.Ordinal) ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Equals("default_new_game", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected client campaign has an unsafe save name.");
        }

        return normalized;
    }

    private static string ResolveDirectChild(string directory, string fileName, string description)
    {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).Equals(fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(description + " escaped its expected directory.");
        }

        return path;
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureUnlinkedDirectory(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(description + " cannot be a linked directory.");
    }

    private sealed record SaveDestination(
        string SaveName,
        string Path,
        bool AlreadyPresent);
}

public sealed record ClientSaveImportResult(
    string SaveName,
    string SourcePath,
    string DestinationPath,
    bool AlreadyPresent,
    bool RenamedForCompatibility,
    bool RenamedForCollision);
