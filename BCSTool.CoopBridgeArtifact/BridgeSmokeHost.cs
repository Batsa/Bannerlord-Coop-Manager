using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class BridgeSmokeHost
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: BridgeSmokeHost <bridge.dll> <dependency-dir> [...]");
            return 2;
        }

        var dependencyDirectories = new string[args.Length - 1];
        Array.Copy(args, 1, dependencyDirectories, 0, dependencyDirectories.Length);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            var fileName = new AssemblyName(eventArgs.Name).Name + ".dll";
            foreach (var directory in dependencyDirectories)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        try
        {
            var preloadAssemblyPath = Environment.GetEnvironmentVariable(
                "BCS_BRIDGE_SMOKE_PRELOAD_ASSEMBLY");
            if (!string.IsNullOrWhiteSpace(preloadAssemblyPath))
            {
                foreach (var path in preloadAssemblyPath.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        Assembly.LoadFrom(Path.GetFullPath(path));
                }
            }
            var workingDirectory = Environment.GetEnvironmentVariable(
                "BCS_BRIDGE_SMOKE_WORKING_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                Directory.SetCurrentDirectory(Path.GetFullPath(workingDirectory));
            InitializeActiveModuleFixtureIfRequested();

            var bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var runtime = bridge.GetType("BCS.CoopBridge.BridgeRuntime", true);
            var validate = runtime.GetMethod(
                "ValidateInstalledPackage",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (validate == null)
                throw new MissingMethodException(runtime.FullName, "ValidateInstalledPackage");
            validate.Invoke(null, null);
            VerifyGameVersionCompatibilityIfRequested(args[0]);
            VerifyAuthorityRuleIfPresent(args[0]);
            Console.WriteLine("PASS: bridge runtime accepted the exact generated package.");
            return 0;
        }
        catch (TargetInvocationException exception)
        {
            Console.Error.WriteLine(exception.InnerException ?? exception);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyGameVersionCompatibilityIfRequested(string bridgePath)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("BCS_BRIDGE_SMOKE_VALIDATE_GAME_VERSION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var versionRecord = ReadGameVersionRecord(bridgePath);
        var serverBaseVersion = versionRecord[0];
        var clientBaseVersion = versionRecord[1];
        var serverRuntimeVersion = versionRecord[2];
        var clientRuntimeVersion = versionRecord[3];
        if (!serverRuntimeVersion.StartsWith(
                serverBaseVersion + ".",
                StringComparison.OrdinalIgnoreCase) ||
            !clientRuntimeVersion.StartsWith(
                clientBaseVersion + ".",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Bridge smoke runtime versions do not match their declared base versions.");
        }

        var gameInterface = AppDomain.CurrentDomain.GetAssemblies().First(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "GameInterface",
                StringComparison.OrdinalIgnoreCase));
        var moduleInfo = gameInterface.GetType(
            "GameInterface.Services.Modules.ModuleInfo",
            true);
        var validator = gameInterface.GetType(
            "GameInterface.Services.Modules.Validators.ModuleValidator",
            true);
        var applicationVersion = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(
                "TaleWorlds.Library.ApplicationVersion",
                false))
            .First(type => type != null);
        var fromString = applicationVersion.GetMethod(
            "FromString",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(string), typeof(int) },
            null);
        var moduleConstructor = moduleInfo.GetConstructor(new[]
        {
            typeof(string),
            typeof(bool),
            typeof(bool),
            applicationVersion
        });
        var validate = validator.GetMethod(
            "ValidateGameVersion",
            BindingFlags.Static | BindingFlags.NonPublic);
        var validateModules = validator.GetMethod(
            "Validate",
            BindingFlags.Instance | BindingFlags.Public);
        if (fromString == null || moduleConstructor == null ||
            validate == null || validateModules == null)
            throw new MissingMemberException("Released Coop game-version validation fixture is incomplete.");

        var server = CreateModuleInfoArray(
            moduleInfo,
            moduleConstructor,
            fromString,
            serverRuntimeVersion);
        var client = CreateModuleInfoArray(
            moduleInfo,
            moduleConstructor,
            fromString,
            clientRuntimeVersion);
        var arguments = new object[] { server, client, null };
        var accepted = (bool)validate.Invoke(null, arguments);
        if (!accepted || arguments[2] != null)
            throw new InvalidOperationException("Supported released game-version pair was not accepted.");

        var unsupportedServer = CreateModuleInfoArray(
            moduleInfo,
            moduleConstructor,
            fromString,
            CreateUnsupportedRuntimeVersion(serverRuntimeVersion));
        arguments = new object[] { unsupportedServer, client, null };
        accepted = (bool)validate.Invoke(null, arguments);
        if (accepted || string.IsNullOrWhiteSpace(arguments[2] as string))
            throw new InvalidOperationException("Unsupported game-version pair bypassed Coop validation.");

        VerifyCurrentVersionRepair(
            bridgePath,
            applicationVersion,
            fromString,
            serverRuntimeVersion);

        if (bridgePath.IndexOf(
                "Win64_Shipping_Server",
                StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.WriteLine(
                "PASS: game-version adapter accepted only the supported release pair.");
            return;
        }

        var serverNative = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Native",
            true,
            serverRuntimeVersion);
        var clientNative = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Native",
            true,
            clientRuntimeVersion);
        var harmony = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Bannerlord.Harmony",
            false,
            "v2.4.2.248");
        var moduleServer = CreateModuleInfoArray(moduleInfo, serverNative);
        var moduleClient = CreateModuleInfoArray(moduleInfo, clientNative, harmony);
        arguments = new object[] { moduleServer, moduleClient, null };
        accepted = (bool)validateModules.Invoke(
            Activator.CreateInstance(validator),
            arguments);
        if (!accepted || arguments[2] != null)
        {
            throw new InvalidOperationException(
                "Supported client-only Harmony module was not accepted.");
        }

        var unsupportedHarmony = CreateModuleInfo(
            moduleConstructor,
            fromString,
            "Bannerlord.Harmony",
            false,
            "v2.4.3.0");
        moduleClient = CreateModuleInfoArray(
            moduleInfo,
            clientNative,
            unsupportedHarmony);
        arguments = new object[] { moduleServer, moduleClient, null };
        accepted = (bool)validateModules.Invoke(
            Activator.CreateInstance(validator),
            arguments);
        if (accepted || string.IsNullOrWhiteSpace(arguments[2] as string))
        {
            throw new InvalidOperationException(
                "Unsupported client-only Harmony version bypassed Coop validation.");
        }
        Console.WriteLine(
            "PASS: game-version and client-only-module adapters accepted only supported releases.");
    }

    private static string[] ReadGameVersionRecord(string bridgePath)
    {
        var directory = Directory.GetParent(Path.GetFullPath(bridgePath));
        string configurationPath = null;
        for (var depth = 0; depth < 4 && directory != null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "bcs-coop-bridge.config");
            if (File.Exists(candidate))
            {
                configurationPath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        if (configurationPath == null)
            throw new FileNotFoundException("Bridge smoke configuration was not found.");

        var records = File.ReadAllLines(configurationPath)
            .Where(line => line.StartsWith("GAME_VERSION_COMPAT|", StringComparison.Ordinal))
            .ToArray();
        if (records.Length != 1)
            throw new InvalidDataException("Bridge smoke requires one game-version compatibility record.");
        var fields = records[0].Split('|');
        if (fields.Length != 5)
            throw new InvalidDataException("Bridge game-version compatibility record is malformed.");
        return new[]
        {
            Decode(fields[1]),
            Decode(fields[2]),
            Decode(fields[3]),
            Decode(fields[4])
        };
    }

    private static string Decode(string value)
    {
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private static string CreateUnsupportedRuntimeVersion(string value)
    {
        var components = value.Substring(1).Split('.');
        int major;
        if (components.Length != 4 ||
            !int.TryParse(components[0], out major) ||
            major == int.MaxValue)
        {
            throw new InvalidDataException("Could not derive an unsupported game version for smoke testing.");
        }
        components[0] = (major + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value[0] + string.Join(".", components);
    }

    private static void VerifyCurrentVersionRepair(
        string bridgePath,
        Type applicationVersion,
        MethodInfo fromString,
        string serverRuntimeVersion)
    {
        var bridge = Assembly.LoadFrom(Path.GetFullPath(bridgePath));
        var repairType = bridge.GetType(
            "BCS.CoopBridge.ServerCurrentVersionCompatibility",
            false);
        var isServer = bridgePath.IndexOf(
            "Win64_Shipping_Server",
            StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isServer)
        {
            if (repairType != null)
                throw new InvalidOperationException("Client bridge contains the server CurrentVersion repair.");
            return;
        }
        if (repairType == null)
            throw new TypeLoadException("Server bridge CurrentVersion repair type was not found.");

        var repair = repairType.GetMethod(
            "Repair",
            BindingFlags.Static | BindingFlags.Public);
        var empty = applicationVersion.GetField(
            "Empty",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (repair == null || empty == null)
            throw new MissingMemberException("Server CurrentVersion repair smoke fixture is incomplete.");

        var valid = fromString.Invoke(null, new object[] { serverRuntimeVersion, 0 });
        var validArguments = new[] { valid };
        repair.Invoke(null, validArguments);
        if (!string.Equals(
                validArguments[0].ToString(),
                serverRuntimeVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CurrentVersion repair changed a valid version.");
        }

        var emptyArguments = new[] { empty.GetValue(null) };
        repair.Invoke(null, emptyArguments);
        if (!string.Equals(
                emptyArguments[0].ToString(),
                serverRuntimeVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CurrentVersion repair did not replace ApplicationVersion.Empty.");
        }
        Console.WriteLine("PASS: server CurrentVersion repair is empty-only and semantic-version scoped.");
    }

    private static Array CreateModuleInfoArray(
        Type moduleInfo,
        ConstructorInfo constructor,
        MethodInfo fromString,
        string versionText)
    {
        return CreateModuleInfoArray(
            moduleInfo,
            CreateModuleInfo(
                constructor,
                fromString,
                "Native",
                true,
                versionText));
    }

    private static object CreateModuleInfo(
        ConstructorInfo constructor,
        MethodInfo fromString,
        string id,
        bool isOfficial,
        string versionText)
    {
        var version = fromString.Invoke(null, new object[] { versionText, 0 });
        return constructor.Invoke(new[]
        {
            (object)id,
            isOfficial,
            false,
            version
        });
    }

    private static Array CreateModuleInfoArray(Type moduleInfo, params object[] modules)
    {
        var array = Array.CreateInstance(moduleInfo, modules.Length);
        for (var index = 0; index < modules.Length; index++)
            array.SetValue(modules[index], index);
        return array;
    }

    private static void InitializeActiveModuleFixtureIfRequested()
    {
        var idsText = Environment.GetEnvironmentVariable("BCS_BRIDGE_SMOKE_ACTIVE_IDS");
        var pathsText = Environment.GetEnvironmentVariable("BCS_BRIDGE_SMOKE_ACTIVE_PATHS");
        if (string.IsNullOrWhiteSpace(idsText) || string.IsNullOrWhiteSpace(pathsText))
            return;

        var moduleManager = AppDomain.CurrentDomain.GetAssemblies().First(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "TaleWorlds.ModuleManager",
                StringComparison.OrdinalIgnoreCase));
        var helper = moduleManager.GetType("TaleWorlds.ModuleManager.ModuleHelper", true);
        var initialize = helper.GetMethod(
            "InitializeModules",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(string[]), typeof(string[]) },
            null);
        if (initialize == null)
            throw new MissingMethodException(helper.FullName, "InitializeModules");
        var ids = idsText.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var paths = pathsText.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        initialize.Invoke(null, new object[] { ids, paths });

        var overrideRoot = Environment.GetEnvironmentVariable(
            "BCS_BRIDGE_SMOKE_OVERRIDE_ACTIVE_ROOT");
        if (string.IsNullOrWhiteSpace(overrideRoot))
            return;
        var getActive = helper.GetMethod(
            "GetActiveModules",
            BindingFlags.Static | BindingFlags.Public,
            null,
            Type.EmptyTypes,
            null);
        var active = (IEnumerable)getActive.Invoke(null, null);
        foreach (var module in active)
        {
            var id = module.GetType().GetProperty("Id").GetValue(module, null) as string;
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;
            var folder = module.GetType().GetProperty("FolderPath");
            folder.GetSetMethod(true).Invoke(module, new object[] { overrideRoot });
        }
    }

    private static void VerifyAuthorityRuleIfPresent(string bridgePath)
    {
        var fixture = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("AuthoritySmokeFixture.Target", false))
            .FirstOrDefault(type => type != null);
        if (fixture == null)
            return;

        var reset = fixture.GetMethod("Reset", BindingFlags.Static | BindingFlags.Public);
        var invoke = fixture.GetMethod("Invoke", BindingFlags.Static | BindingFlags.Public);
        var invokeClientOnly = fixture.GetMethod(
            "InvokeClientOnly",
            BindingFlags.Static | BindingFlags.Public);
        var count = fixture.GetProperty("Count", BindingFlags.Static | BindingFlags.Public);
        var clientOnlyCount = fixture.GetProperty(
            "ClientOnlyCount",
            BindingFlags.Static | BindingFlags.Public);
        if (reset == null || invoke == null || invokeClientOnly == null || count == null ||
            clientOnlyCount == null)
            throw new MissingMemberException("Authority smoke fixture surface is incomplete.");

        var expectedServer = string.Equals(
            new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(bridgePath))).Name,
            "Win64_Shipping_Server",
            StringComparison.OrdinalIgnoreCase);
        reset.Invoke(null, null);
        invoke.Invoke(null, null);
        invokeClientOnly.Invoke(null, null);
        var expectedServerCount = expectedServer ? 1 : 0;
        var expectedClientCount = expectedServer ? 0 : 1;
        if ((int)count.GetValue(null, null) != expectedServerCount)
            throw new InvalidOperationException("Server-only authority scope did not match runtime role.");
        if ((int)clientOnlyCount.GetValue(null, null) != expectedClientCount)
            throw new InvalidOperationException("Client-only authority scope did not match runtime role.");
        Console.WriteLine(
            "PASS: authority adapter enforced " + (expectedServer ? "server" : "client") +
            " runtime scope.");
    }
}
