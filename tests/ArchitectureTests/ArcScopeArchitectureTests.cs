// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;

namespace ArcForges.ArcScope.ArchitectureTests;

internal static class RepositoryRoot
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ArcScope.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Owning ArcScope repository was not found.");
    }
}

/// <summary>Allowlist of dependency licences for a consuming boundary, read from the repository's own reuse policy.</summary>
internal static class LicenceAllowlist
{
    private const string ReviewedPublisher = "https://github.com/ArcForges/";

    /// <summary>
    /// A permissive licence is admitted for either boundary after audit. An AGPL-compatible licence is admitted only for the
    /// AGPL boundary and only for an exact package published by ArcForges. Anything unclassified, GPL-only or incompatible is not.
    /// </summary>
    public static bool IsAdmitted(JsonElement reusePolicy, string boundary, string licence, string sourceRepository)
    {
        if (!reusePolicy.GetProperty("licences").TryGetProperty(licence, out var category)
            || !reusePolicy.GetProperty("decisions").TryGetProperty(category.GetString() ?? "", out var decisions)
            || !decisions.TryGetProperty(boundary, out var decision))
        {
            return false;
        }

        return decision.GetString() switch
        {
            "audit" => true,
            "exact-review" => sourceRepository.StartsWith(ReviewedPublisher, StringComparison.Ordinal),
            _ => false,
        };
    }
}

internal static class ArcScopeArchitectureTests
{
    public static void RunLocal() => VerifyEveryPublicApiHasArchitectureTestBinding();

    /// <summary>
    /// The one contract-test fact the hosted gate binds every public production API to (RP-10). It runs the checks of
    /// ArcScope's own repository that need no build output: contract consumption, the dependency licence allowlist and the
    /// absence of any Apache-boundary project. The engine fixtures (layering, licence boundary, banned API, evidence) run
    /// before it from <see cref="ArchitectureFixtureTests.Run"/>.
    /// </summary>
    [Xunit.Fact]
    public static void VerifyEveryPublicApiHasArchitectureTestBinding()
    {
        string root = RepositoryRoot.Find();
        ContractConsumptionChecks.VerifyRepository(root);
        VerifyDependencyLicenceAllowlist(root);
        VerifyLicenceBoundaryInventory(root);
        HostedPolicyGate.VerifyHostedWorkflow(File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml")));
    }

    private static void VerifyDependencyLicenceAllowlist(string root)
    {
        using var reuse = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/policy/reuse-policy.json")));
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/policy/dependency-policy.json")));
        int checkedPackages = 0;
        foreach (var package in dependencies.RootElement.GetProperty("packages").EnumerateArray())
        {
            string id = package.GetProperty("id").GetString()!;
            string licence = package.GetProperty("licence").GetString()!;
            string source = package.GetProperty("sourceRepository").GetString()!;
            Checks.True(LicenceAllowlist.IsAdmitted(reuse.RootElement, "AGPL", licence, source),
                $"Dependency {id} has a licence that is not on the AGPL-boundary allowlist.");
            checkedPackages++;
        }

        Checks.True(checkedPackages > 0, "The admitted dependency inventory is empty.");
    }

    // The Apache set is the enumerated list in the licence inventory; ArcScope enumerates none, so no project may declare it.
    private static void VerifyLicenceBoundaryInventory(string root)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/policy/licence-boundary.json")));
        Checks.Equal("AGPL", inventory.RootElement.GetProperty("licenceBoundary").GetString(), "ArcScope is not an AGPL-boundary repository.");
        Checks.Equal("AGPL-3.0-only", inventory.RootElement.GetProperty("spdxLicense").GetString(), "ArcScope is not licensed AGPL-3.0-only.");
        string[] enumerated = inventory.RootElement.GetProperty("projects").EnumerateArray()
            .Select(project => project.GetProperty("path").GetString()!).Order(StringComparer.Ordinal).ToArray();
        string[] actual = System.Xml.Linq.XDocument.Load(Path.Combine(root, "ArcScope.slnx")).Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value).Order(StringComparer.Ordinal).ToArray();
        Checks.SequenceEqual(actual, enumerated, "The licence inventory does not match the solution's projects.");
        foreach (string path in actual)
        {
            var xml = System.Xml.Linq.XDocument.Load(Path.Combine(root, path));
            Checks.SequenceEqual(["AGPL"], xml.Descendants().Where(element => element.Name.LocalName == "LicenceBoundary").Select(element => element.Value),
                $"Project {path} does not declare exactly the AGPL boundary.");
            Checks.SequenceEqual(["AGPL-3.0-only"], xml.Descendants().Where(element => element.Name.LocalName == "PackageLicenseExpression").Select(element => element.Value),
                $"Project {path} does not declare exactly the AGPL-3.0-only SPDX identifier.");
        }
    }
}
