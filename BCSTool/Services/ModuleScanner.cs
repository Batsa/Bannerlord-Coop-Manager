using System.IO;
using System.Xml;
using BCSTool.Models;

namespace BCSTool.Services;

/// <summary>
/// Reads Bannerlord SubModule.xml manifests without loading executable code.
/// </summary>
public sealed class ModuleScanner
{
    private const long MaximumManifestCharacters = 4 * 1024 * 1024;

    public IReadOnlyList<BannerlordModule> Scan(string modulesDirectory)
    {
        if (!Directory.Exists(modulesDirectory))
            throw new DirectoryNotFoundException(
                $"Dedicated-server Modules directory was not found: {modulesDirectory}");

        var modules = new List<BannerlordModule>();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(modulesDirectory))
        {
            var manifestPath = System.IO.Path.Combine(directory, "SubModule.xml");
            if (!File.Exists(manifestPath))
                continue;

            var module = ReadManifest(manifestPath, directory);
            if (ids.TryGetValue(module.Id, out var existingPath))
            {
                throw new InvalidDataException(
                    $"Duplicate installed module ID '{module.Id}' in '{existingPath}' and '{directory}'.");
            }

            ids.Add(module.Id, directory);
            modules.Add(module);
        }

        return modules
            .OrderBy(module => module.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BannerlordModule ReadManifest(string manifestPath, string directory)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestCharacters
        };

        var document = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(manifestPath, settings);
            document.Load(reader);
        }
        catch (Exception exception) when (
            exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Could not read module manifest '{manifestPath}': {exception.Message}",
                exception);
        }

        var root = document.DocumentElement;
        if (root is null || !root.LocalName.Equals("Module", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest does not contain a Module root: {manifestPath}");

        var id = ChildValue(root, "Id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException($"Manifest has no module ID: {manifestPath}");

        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mustLoadAfter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mustLoadBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (XmlElement dependency in SelectElements(
                     root,
                     "DependedModules",
                     "DependedModule"))
        {
            if (AttributeIsTrue(dependency, "Optional"))
                continue;

            AddAttributeValue(dependencies, dependency, "Id");
            AddAttributeValue(mustLoadAfter, dependency, "Id");
        }

        foreach (XmlElement metadata in SelectElements(
                     root,
                     "DependedModuleMetadatas",
                     "DependedModuleMetadata"))
        {
            var dependencyId = AttributeValue(metadata, "Id");
            if (string.IsNullOrWhiteSpace(dependencyId))
                continue;

            if (!AttributeIsTrue(metadata, "Optional"))
                dependencies.Add(dependencyId);

            var order = AttributeValue(metadata, "Order");
            if (order.Equals("LoadBeforeThis", StringComparison.OrdinalIgnoreCase))
                mustLoadAfter.Add(dependencyId);
            else if (order.Equals("LoadAfterThis", StringComparison.OrdinalIgnoreCase))
                mustLoadBefore.Add(dependencyId);
        }

        foreach (XmlElement module in SelectElements(
                     root,
                     "ModulesToLoadAfterThis",
                     "Module"))
        {
            AddAttributeValue(mustLoadBefore, module, "Id");
        }

        foreach (XmlElement module in SelectElements(
                     root,
                     "IncompatibleModules",
                     "Module"))
        {
            AddAttributeValue(incompatible, module, "Id");
        }

        var name = RemoveLocalizationPrefix(ChildValue(root, "Name"));
        if (string.IsNullOrWhiteSpace(name))
            name = id;

        return new BannerlordModule
        {
            Name = name,
            Id = id,
            Version = ChildValue(root, "Version"),
            Path = System.IO.Path.GetFullPath(directory),
            IsInstalled = true,
            IsRequired = false,
            IsServerCompatible = true,
            Dependencies = dependencies.ToArray(),
            MustLoadAfter = mustLoadAfter.ToArray(),
            MustLoadBefore = mustLoadBefore.ToArray(),
            IncompatibleModules = incompatible.ToArray()
        };
    }

    private static IEnumerable<XmlElement> SelectElements(
        XmlElement root,
        string containerName,
        string childName)
    {
        return root.ChildNodes
            .OfType<XmlElement>()
            .Where(element => element.LocalName.Equals(containerName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(element => element.ChildNodes.OfType<XmlElement>())
            .Where(element => element.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ChildValue(XmlElement root, string childName)
    {
        var element = root.ChildNodes
            .OfType<XmlElement>()
            .FirstOrDefault(child => child.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase));

        return element is null ? string.Empty : AttributeValue(element, "Value");
    }

    private static string AttributeValue(XmlElement element, string attributeName)
    {
        return element.Attributes
                   .OfType<XmlAttribute>()
                   .FirstOrDefault(attribute =>
                       attribute.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                   ?.Value.Trim()
               ?? string.Empty;
    }

    private static bool AttributeIsTrue(XmlElement element, string attributeName) =>
        bool.TryParse(AttributeValue(element, attributeName), out var value) && value;

    private static void AddAttributeValue(
        ISet<string> values,
        XmlElement element,
        string attributeName)
    {
        var value = AttributeValue(element, attributeName);
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value);
    }

    private static string RemoveLocalizationPrefix(string value)
    {
        if (!value.StartsWith("{=", StringComparison.Ordinal))
            return value;

        var closingBrace = value.IndexOf('}');
        return closingBrace >= 0 ? value[(closingBrace + 1)..].Trim() : value;
    }
}
