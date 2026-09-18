// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Xml.Linq;

namespace ArcForges.Repository;

public static class LicencePolicy
{
    private static readonly HashSet<string> ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".vcxproj", ".esproj"];
    private static readonly HashSet<string> FirstPartyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArcForges.Build.Policy", "ArcForges.Contracts.PublicApi"
    };

    public static string[] Validate(string root, IEnumerable<string> inventory)
    {
        root = Path.GetFullPath(root);
        var files = inventory.Distinct(StringComparer.Ordinal).ToArray();
        string Read(string path)
        {
            var full = Path.GetFullPath(Path.Combine(root, path));
            var relative = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Licence input escapes owner: {path}");
            for (var current = full; current != root; current = Path.GetDirectoryName(current)!)
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Linked licence input: {path}");
            return File.ReadAllText(full);
        }
        static void Fields(JsonElement value, params string[] expected)
        {
            var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (actual.Length != expected.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                throw new InvalidOperationException("Missing, duplicate or unknown licence policy fields.");
        }
        using var policy = JsonDocument.Parse(Read("eng/policy/licence-boundary.json"));
        var data = policy.RootElement;
        Fields(data, "schemaVersion", "repository", "spdxLicense", "licenceBoundary", "projects");
        if (data.GetProperty("schemaVersion").GetInt32() != 1 || data.GetProperty("repository").GetString() != "ArcScope" ||
            data.GetProperty("spdxLicense").GetString() != "AGPL-3.0-only" || data.GetProperty("licenceBoundary").GetString() != "AGPL")
            throw new InvalidOperationException("Incorrect repository licence assignment.");
        var projects = files.Where(p => ProjectExtensions.Contains(Path.GetExtension(p))).ToHashSet(StringComparer.Ordinal);
        if (files.Any(p => Path.GetFileName(p) is "package.json" or "build.gradle.kts" or "build.gradle" or "CMakeLists.txt"))
            throw new InvalidOperationException("New build system requires a reviewed licence declaration and verifier.");
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in data.GetProperty("projects").EnumerateArray())
        {
            Fields(row, "path", "kind");
            if (row.GetProperty("kind").GetString() != "msbuild" || !registered.Add(row.GetProperty("path").GetString()!))
                throw new InvalidOperationException("Incorrect or duplicate project registration.");
        }
        if (projects.Count == 0 || !projects.SetEquals(registered))
            throw new InvalidOperationException("Project licence inventory drift.");
        var managedNames = projects.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in projects)
        {
            var xml = XDocument.Parse(Read(path));
            foreach (var pair in new[] { ("PackageLicenseExpression", "AGPL-3.0-only"), ("LicenceBoundary", "AGPL") })
                if (!xml.Descendants().Where(e => e.Name.LocalName == pair.Item1).Select(e => e.Value).SequenceEqual([pair.Item2]))
                    throw new InvalidOperationException($"Missing, duplicate or inconsistent licence declaration: {path}");
            foreach (var element in xml.Descendants().Where(e => e.Name.LocalName is "AssemblyName" or "PackageId"))
                managedNames.Add(element.Value);
        }
        foreach (var path in files)
        {
            if (ProjectExtensions.Contains(Path.GetExtension(path)) || Path.GetExtension(path) is ".props" or ".targets")
            {
                foreach (var element in XDocument.Parse(Read(path)).Descendants())
                {
                    if (element.Name.LocalName == "LicenceBoundary" && element.Value != "AGPL" ||
                        element.Name.LocalName == "PackageLicenseExpression" && element.Value != "AGPL-3.0-only")
                        throw new InvalidOperationException($"Imported licence property override: {path}");
                    if (element.Name.LocalName == "ProjectReference")
                    {
                        var include = (string?)element.Attribute("Include") ?? "";
                        if (include.Length == 0 || include.IndexOfAny(['$', '@', '*', '?', ';']) >= 0)
                            throw new InvalidOperationException($"Nonliteral project reference requires review: {path}");
                        var target = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(path)!, include.Replace('\\', '/')));
                        var relative = Path.GetRelativePath(root, target).Replace('\\', '/');
                        if (!projects.Contains(relative))
                            throw new InvalidOperationException($"Escaped or unregistered project reference: {path} -> {include}");
                    }
                    if (element.Name.LocalName is "PackageVersion" or "PackageReference")
                        ValidatePackage((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "");
                }
            }
            if (Path.GetFileName(path) == "packages.lock.json")
            {
                using var locked = JsonDocument.Parse(Read(path));
                foreach (var framework in locked.RootElement.GetProperty("dependencies").EnumerateObject())
                    foreach (var package in framework.Value.EnumerateObject())
                        if (package.Value.GetProperty("type").GetString() == "Project")
                        {
                            if (!managedNames.Contains(package.Name))
                                throw new InvalidOperationException($"Unknown locked first-party project: {package.Name}");
                        }
                        else ValidatePackage(package.Name);
            }
        }
        return projects.Order(StringComparer.Ordinal).ToArray();
    }

    private static void ValidatePackage(string name)
    {
        if (name.StartsWith("ArcForges.", StringComparison.OrdinalIgnoreCase) && !FirstPartyPackages.Contains(name))
            throw new InvalidOperationException($"Unknown first-party package requires licence review: {name}");
    }
}
