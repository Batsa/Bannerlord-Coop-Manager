using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

/// <summary>
/// Runs before Bannerlord's managed entry point. The dedicated server keeps
/// community assemblies in per-module bin folders, which are outside the
/// default .NET probing path. BCS Tool supplies the allowed folders through a
/// process-private environment variable.
/// </summary>
public static class StartupHook
{
    private const string SearchDirectoriesVariable =
        "BCSTOOL_DS_MANAGED_SEARCH_DIRECTORIES";
    private const string CrashLogVariable = "BCSTOOL_DS_CRASH_LOG";
    private const string ConsoleLogVariable = "BCSTOOL_DS_CONSOLE_LOG";
    private const string FirstChanceLogVariable = "BCSTOOL_DS_FIRST_CHANCE_LOG";

    private static TextWriter? ConsoleLogWriter;

    private static readonly string[] SearchDirectories = ReadSearchDirectories();

    public static void Initialize()
    {
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
        InstallConsoleLogging();
        InstallCrashLogging();
        InstallFirstChanceLogging();
        Console.WriteLine(
            $"[BCS Tool] Module assembly resolver active ({SearchDirectories.Length} search folders)." );
    }

    private static void InstallConsoleLogging()
    {
        var consoleLog = Environment.GetEnvironmentVariable(ConsoleLogVariable);
        if (string.IsNullOrWhiteSpace(consoleLog))
            return;

        var fullPath = Path.GetFullPath(consoleLog);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var writer = new StreamWriter(fullPath, append: false)
        {
            AutoFlush = true
        };
        ConsoleLogWriter = TextWriter.Synchronized(writer);
        Console.SetOut(ConsoleLogWriter);
        Console.SetError(ConsoleLogWriter);
    }

    private static void InstallCrashLogging()
    {
        var crashLog = Environment.GetEnvironmentVariable(CrashLogVariable);
        if (string.IsNullOrWhiteSpace(crashLog))
            return;

        var fullPath = Path.GetFullPath(crashLog);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(
                    fullPath,
                    $"[{DateTimeOffset.Now:O}] Terminating={eventArgs.IsTerminating}{Environment.NewLine}" +
                    $"{eventArgs.ExceptionObject}{Environment.NewLine}");
            }
            catch
            {
                // Crash diagnostics must never replace the original exception.
            }
        };
    }

    private static void InstallFirstChanceLogging()
    {
        var firstChanceLog = Environment.GetEnvironmentVariable(FirstChanceLogVariable);
        if (string.IsNullOrWhiteSpace(firstChanceLog))
            return;

        var fullPath = Path.GetFullPath(firstChanceLog);
        var sync = new object();
        var eventCount = 0;
        AppDomain.CurrentDomain.FirstChanceException += (_, eventArgs) =>
        {
            try
            {
                lock (sync)
                {
                    if (eventCount++ >= 2_000)
                        return;
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    File.AppendAllText(
                        fullPath,
                        $"[{DateTimeOffset.Now:O}] {eventArgs.Exception}{Environment.NewLine}");
                }
            }
            catch
            {
                // Opt-in diagnostics must never alter server exception handling.
            }
        };
    }

    private static Assembly? ResolveAssembly(
        AssemblyLoadContext context,
        AssemblyName requested)
    {
        if (string.IsNullOrWhiteSpace(requested.Name))
            return null;

        var candidates = new List<(string Path, AssemblyName Identity)>();
        foreach (var directory in SearchDirectories)
        {
            var candidate = Path.Combine(directory, requested.Name + ".dll");
            if (!File.Exists(candidate) || !TryReadCompatibleIdentity(candidate, requested, out var identity))
                continue;

            candidates.Add((candidate, identity));
        }

        var bestIndex = SelectBestCandidateIndex(
            requested,
            candidates.Select(candidate => candidate.Identity).ToArray());
        var orderedIndices = Enumerable.Range(0, candidates.Count)
            .OrderBy(index => index == bestIndex ? 0 : 1);
        foreach (var index in orderedIndices)
        {
            var candidate = candidates[index].Path;

            try
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            catch (FileLoadException)
            {
                var loaded = context.Assemblies.FirstOrDefault(
                    assembly => AssemblyName.ReferenceMatchesDefinition(
                        assembly.GetName(),
                        requested));
                if (loaded is not null)
                    return loaded;
            }
            catch (BadImageFormatException)
            {
                // Native DLLs can share a managed dependency's file name.
            }
        }

        return null;
    }

    internal static int SelectBestCandidateIndex(
        AssemblyName requested,
        IReadOnlyList<AssemblyName> candidates)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (IsExactIdentityMatch(candidates[index], requested))
                return index;
        }
        return candidates.Count == 0 ? -1 : 0;
    }

    private static bool IsExactIdentityMatch(AssemblyName available, AssemblyName requested)
    {
        if (available.Name?.Equals(requested.Name, StringComparison.OrdinalIgnoreCase) != true)
            return false;
        if (requested.Version is not null && available.Version != requested.Version)
            return false;
        var requestedToken = requested.GetPublicKeyToken() ?? Array.Empty<byte>();
        var availableToken = available.GetPublicKeyToken() ?? Array.Empty<byte>();
        if (requestedToken.Length > 0 && !requestedToken.SequenceEqual(availableToken))
            return false;
        if (!string.IsNullOrWhiteSpace(requested.CultureName) &&
            !requested.CultureName.Equals("neutral", StringComparison.OrdinalIgnoreCase) &&
            !requested.CultureName.Equals(available.CultureName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static bool TryReadCompatibleIdentity(
        string candidate,
        AssemblyName requested,
        out AssemblyName identity)
    {
        try
        {
            identity = AssemblyName.GetAssemblyName(candidate);
            return identity.Name?.Equals(
                       requested.Name,
                       StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            identity = new AssemblyName();
            return false;
        }
    }

    private static string[] ReadSearchDirectories()
    {
        var configured = Environment.GetEnvironmentVariable(SearchDirectoriesVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return Array.Empty<string>();

        return OrderSearchDirectories(configured
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static string[] OrderSearchDirectories(IEnumerable<string> directories)
    {
        return directories
            .Select((path, index) => new
            {
                Path = Path.GetFullPath(path),
                Index = index
            })
            .OrderBy(item => SearchDirectoryPriority(item.Path))
            .ThenBy(item => item.Index)
            .Select(item => item.Path)
            .ToArray();
    }

    private static int SearchDirectoryPriority(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (normalized.EndsWith(
                Path.Combine("engine", "bin", "Win64_Shipping_Server"),
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (normalized.EndsWith(
                Path.Combine("Modules", "Coop", "bin", "Win64_Shipping_Server"),
                StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return 2;
    }
}
