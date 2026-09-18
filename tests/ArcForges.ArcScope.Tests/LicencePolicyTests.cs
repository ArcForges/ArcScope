// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using ArcForges.Repository;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class LicencePolicyTests
{
    [Fact]
    public void CompleteInventoryPasses()
    {
        using var fixture = new Fixture();
        Assert.Single(fixture.Validate());
    }

    [Theory]
    [InlineData("new.csproj", "<Project />")]
    [InlineData("tools/new.esproj", "<Project />")]
    [InlineData("tools/package.json", "{}")]
    public void NewlyIntroducedBuildScopeCannotEscapeInventory(string path, string text)
    {
        using var fixture = new Fixture();
        fixture.Write(path, text);
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("", "AGPL-3.0-only")]
    [InlineData("Apache", "AGPL-3.0-only")]
    [InlineData("AGPL", "Apache-2.0")]
    public void MissingOrInconsistentBoundaryFails(string boundary, string spdx)
    {
        using var fixture = new Fixture();
        fixture.Write("app.csproj", Fixture.Project(boundary, spdx));
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void ImportedOverrideFails()
    {
        using var fixture = new Fixture();
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><LicenceBoundary>Apache</LicenceBoundary></PropertyGroup></Project>");
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("../sibling.csproj")]
    [InlineData("missing.csproj")]
    public void EscapedOrUnregisteredProjectReferenceFails(string target)
    {
        using var fixture = new Fixture();
        fixture.Write("app.csproj", Fixture.Project().Replace("</Project>", $"<ItemGroup><ProjectReference Include=\"{target}\" /></ItemGroup></Project>", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("Transitive", "ArcForges.Unknown")]
    [InlineData("Project", "UnknownProject")]
    public void UnknownLockedFirstPartyDependencyFails(string type, string name)
    {
        using var fixture = new Fixture();
        fixture.Write("packages.lock.json", JsonSerializer.Serialize(new { dependencies = new { net10 = new Dictionary<string, object> { [name] = new { type } } } }));
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "licence-policy-" + Guid.NewGuid());

        public Fixture()
        {
            Write("app.csproj", Project());
            Write("eng/policy/licence-boundary.json", JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                repository = "ArcScope",
                spdxLicense = "AGPL-3.0-only",
                licenceBoundary = "AGPL",
                projects = new[] { new { path = "app.csproj", kind = "msbuild" } }
            }));
        }

        public static string Project(string boundary = "AGPL", string spdx = "AGPL-3.0-only") =>
            $"<Project><PropertyGroup><PackageLicenseExpression>{spdx}</PackageLicenseExpression><LicenceBoundary>{boundary}</LicenceBoundary></PropertyGroup></Project>";

        public void Write(string path, string text)
        {
            var target = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
        }

        public string[] Validate() => LicencePolicy.Validate(_root,
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/')));

        public void Dispose() => Directory.Delete(_root, true);
    }
}
