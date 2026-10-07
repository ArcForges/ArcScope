// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ArcForges.ArcScope.Core;
using ArcForges.Repository;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class BuildIdentityTests
{
    private static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(current.FullName, "eng/version-sources.json"))) current = current.Parent!;
        return current.FullName;
    }

    [Fact]
    public void NineRealSourceKindsAreIndependentAndDeterministic()
    {
        // Mechanism fixture: these declarations do not claim production format/storage support.
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "eng/version-sources.json")))!.AsObject();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in BuildIdentity.AxisNames)
        {
            var kind = catalog["axes"]![name]!["kind"]!.GetValue<string>();
            catalog["axes"]![name] = new JsonObject { ["kind"] = kind, ["sources"] = new JsonArray(name + ".json") };
            sources[name + ".json"] = kind switch
            {
                "contracts" => "{\"schema\":\"fixture.rpc.v1\",\"descriptorSha256\":\"" + new string('a', 64) + "\",\"dirty\":false}",
                "packages" => "{\"dependencies\":{\"net10.0\":{\"fixture.package\":{\"type\":\"Direct\",\"resolved\":\"1.0\"}}}}",
                "native-abi" => "#define ARC_ABI_MAJOR 1\n#define ARC_ABI_MINOR 0\n",
                "migrations" => "{\"migrations\":[{\"subject\":\"fixture.store\",\"version\":\"0.9\"},{\"subject\":\"fixture.store\",\"version\":\"1.0\"}]}",
                _ => "{\"versions\":[{\"subject\":\"fixture.owned\",\"version\":\"1.0\"}]}"
            };
        }
        var first = BuildIdentity.Resolve(catalog, path => sources[path]);
        Assert.Equal(9, first.Count);
        foreach (var name in BuildIdentity.AxisNames)
        {
            Assert.Equal(name == "ContractSet" ? "1" : "1.0", first[name]!["values"]![0]!["version"]!.GetValue<string>());
            var changed = new Dictionary<string, string>(sources, StringComparer.Ordinal);
            changed[name + ".json"] = name switch
            {
                "ContractSet" => sources[name + ".json"].Replace("rpc.v1", "rpc.v2", StringComparison.Ordinal),
                "NativeAbiVersion" => sources[name + ".json"].Replace("MAJOR 1", "MAJOR 2", StringComparison.Ordinal),
                _ => sources[name + ".json"].Replace("1.0", "2.0", StringComparison.Ordinal)
            };
            var next = BuildIdentity.Resolve(catalog, path => changed[path]);
            foreach (var axis in BuildIdentity.AxisNames) Assert.Equal(axis != name, JsonNode.DeepEquals(first[axis], next[axis]));
        }
        Assert.Equal(first.ToJsonString(), BuildIdentity.Resolve(catalog, path => sources[path]).ToJsonString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("alias")]
    [InlineData("producer")]
    [InlineData("kind")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("path")]
    public void InvalidSourcesCannotProduceAReport(string mode)
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "eng/version-sources.json")))!.AsObject();
        switch (mode)
        {
            case "missing": catalog["axes"]!.AsObject().Remove("AppVersion"); break;
            case "unknown": catalog["axes"]!["OtherVersion"] = new JsonObject(); break;
            case "alias": catalog["axes"]!["AppVersion"]!["alias"] = "PackageVersion"; break;
            case "producer": catalog["axes"]!["CapabilityVersion"]!.AsObject().Remove("producer"); break;
            case "kind": catalog["axes"]!["AppVersion"]!["kind"] = "packages"; break;
            case "path": catalog["axes"]!["AppVersion"]!["sources"]![0] = "../outside.json"; break;
        }
        string Read(string path) => path switch
        {
            "assembly/release.json" when mode == "duplicate" => "{\"versions\":[{\"subject\":\"app\",\"version\":\"1\"},{\"subject\":\"app\",\"version\":\"2\"}]}",
            "assembly/release.json" when mode == "version" => "{\"versions\":[{\"subject\":\"app\",\"version\":\"AppVersion\"}]}",
            "assembly/release.json" => "{\"versions\":[{\"subject\":\"app\",\"version\":\"1\"}]}",
            "packages/contracts/source.json" => "{\"schema\":\"fixture.rpc.v1\",\"descriptorSha256\":\"" + new string('a', 64) + "\",\"dirty\":false}",
            "src/ArcForges.ArcScope/packages.lock.json" => "{\"dependencies\":{}}",
            _ => "{\"versions\":[{\"subject\":\"app\",\"version\":\"1\"}]}"
        };
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.Resolve(catalog, Read));
    }

    [Theory]
    [InlineData("sourceCommit")]
    [InlineData("sourceDateEpoch")]
    [InlineData("dirty")]
    [InlineData("buildId")]
    [InlineData("kind")]
    [InlineData("axis")]
    public void RehashedSupportReportsCannotChangeSourceOrRun(string field)
    {
        var root = Root();
        var commit = IdentityEvidence.Git(root, "rev-parse", "HEAD");
        var report = IdentityEvidence.ExpectedReport(root, "0.1.0-ci.7.1", commit);
        IdentityEvidence.Verify(Encoding.UTF8.GetBytes(report.ToJsonString()), root, "0.1.0-ci.7.1", commit);
        switch (field)
        {
            case "sourceCommit": report["build"]![field] = new string('b', 40); break;
            case "sourceDateEpoch": report["build"]![field] = 1; break;
            case "dirty": report["build"]![field] = true; break;
            case "buildId": report["build"]![field] = "another-run"; break;
            case "kind": report["build"]![field] = "unknown"; break;
            case "axis": report["axes"]!["ContractSet"]!["values"]![0]!["version"] = "36"; break;
        }
        Assert.Throws<InvalidOperationException>(() => IdentityEvidence.Verify(Encoding.UTF8.GetBytes(report.ToJsonString()), root, "0.1.0-ci.7.1", commit));
    }

    [Fact]
    public void DirtyOrIncompleteCiIdentityIsRejected()
    {
        var local = new JsonObject
        {
            ["sourceCommit"] = new string('a', 40),
            ["sourceDateEpoch"] = 1L,
            ["dirty"] = true,
            ["kind"] = "local",
            ["buildId"] = "local." + new string('a', 40),
            ["runId"] = null,
            ["runAttempt"] = null,
            ["pipelineRun"] = null
        };
        BuildIdentity.ValidateBuild(local);
        local["kind"] = "ci";
        local["runId"] = "123";
        local["runAttempt"] = 1;
        local["buildId"] = "123.1";
        local["pipelineRun"] = "https://github.com/ArcForges/ArcScope/actions/runs/123";
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.ValidateBuild(local));
        local["dirty"] = false;
        BuildIdentity.ValidateBuild(local);
        local["runAttempt"] = 0;
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.ValidateBuild(local));
    }

    [Fact]
    public void ActualRestored324SchemasAndDescriptorProduceCompleteIndependentContractSet()
    {
        var (catalog, sources) = ActualContracts();
        var axes = BuildIdentity.Resolve(catalog, path => sources[path]);
        var values = axes["ContractSet"]!["values"]!.AsArray();
        Assert.Equal(10, values.Count);
        Assert.All(values, value => Assert.Equal("1", value!["version"]!.GetValue<string>()));
        Assert.Contains(values, value => value!["subject"]!.GetValue<string>() == "json:BrowserSession");
        var publicApi = Assert.Single(values, value => value!["subject"]!.GetValue<string>() == "arcforges.publicapi")!;
        Assert.Null(publicApi["source"]);
        Assert.Equal(13, publicApi["sources"]!.AsArray().Count);
        Assert.Equal(22, values.Sum(value => value!["sources"]?.AsArray().Count ?? 1));
        Assert.Contains(axes["PackageVersion"]!["values"]!.AsArray(), value =>
            value!["subject"]!.GetValue<string>().StartsWith("pkg:nuget/ArcForges.Contracts.PublicApi?", StringComparison.Ordinal)
            && value["version"]!.GetValue<string>() == "1.0.0-ci.324.1");
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("foreign")]
    [InlineData("artifact")]
    [InlineData("version")]
    [InlineData("commit")]
    [InlineData("descriptor")]
    [InlineData("schema-bytes")]
    [InlineData("semantic-version")]
    [InlineData("subject")]
    [InlineData("omitted")]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    [InlineData("extra-source")]
    [InlineData("private-source")]
    [InlineData("unknown-profile")]
    [InlineData("missing-report")]
    public void ActualProducerTamperingCannotBecomeContractIdentity(string change)
    {
        var (catalog, sources) = ActualContracts();
        const string provenanceKey = "packages/contracts/source.json";
        const string reportKey = "packages/contracts/build-identity.json";
        var provenance = JsonNode.Parse(sources[provenanceKey])!.AsObject();
        var report = JsonNode.Parse(sources[reportKey])!.AsObject();
        var values = report["axes"]!["ContractSet"]!["values"]!.AsArray();
        string schemaPath = provenance["schemaSources"]!.AsObject().First().Key;
        switch (change)
        {
            case "dirty": report["build"]!["dirty"] = true; break;
            case "foreign": report["owner"] = "Foreign"; break;
            case "artifact": report["artifact"]!["id"] = "ArcForges.Contracts.CloudInternal"; break;
            case "version": report["artifact"]!["version"] = "1.0.0-ci.999.1"; break;
            case "commit": report["build"]!["sourceCommit"] = new string('a', 40); break;
            case "descriptor": sources["packages/contracts/descriptor.base64"] = Convert.ToBase64String([1, 2, 3]); break;
            case "schema-bytes": sources["packages/contracts/schema/" + schemaPath] += " "; break;
            case "semantic-version": values[0]!["version"] = "324"; break;
            case "subject": values[0]!["subject"] = "invented.namespace"; break;
            case "omitted": values.RemoveAt(0); break;
            case "duplicate": values.Add(values[0]!.DeepClone()); break;
            case "reordered": var first = values[0]!.DeepClone(); values.RemoveAt(0); values.Add(first); break;
            case "extra-source": provenance["schemaSources"]!["public/proto/foreign/v1/foreign.proto"] = new string('a', 64); break;
            case "private-source": provenance["schemaSources"]!["../private.proto"] = new string('a', 64); break;
            case "unknown-profile": provenance["invented"] = true; break;
            case "missing-report": catalog["axes"]!["ContractSet"]!.AsObject().Remove("producerReport"); break;
        }
        sources[provenanceKey] = provenance.ToJsonString();
        sources[reportKey] = report.ToJsonString();
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.Resolve(catalog, path =>
            sources.TryGetValue(path, out var source) ? source : throw new InvalidOperationException("Required producer source missing.")));
    }

    [Fact]
    public void ActualCompiledPublicProducerResourcesMatchIndependentRestoredPackageReader()
    {
        var root = Root();
        string executable = Path.Combine(root, "src/ArcForges.ArcScope/bin/Release/net10.0/ArcScope.dll");
        var actual = BuildIdentity.FromAssembly(Assembly.LoadFrom(executable));
        var expected = IdentityEvidence.ExpectedReport(root, actual["artifact"]!["version"]!.GetValue<string>(),
            IdentityEvidence.Git(root, "rev-parse", "HEAD"));
        Assert.True(JsonNode.DeepEquals(expected["axes"], actual["axes"]));
    }

    private static (JsonObject Catalog, Dictionary<string, string> Sources) ActualContracts()
    {
        var root = Root();
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/version-sources.json")))!.AsObject();
        var assets = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/ArcForges.Repository/obj/project.assets.json")))!;
        string package = assets["packageFolders"]!.AsObject().Select(folder => Path.Combine(folder.Key,
            "arcforges.contracts.publicapi", "1.0.0-ci.324.1")).Single(path => File.Exists(Path.Combine(path, "source.json")));
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["assembly/release.json"] = """{"versions":[{"subject":"ArcScope","version":"0.1.0"}]}""",
            ["packages/contracts/source.json"] = File.ReadAllText(Path.Combine(package, "source.json")),
            ["packages/contracts/build-identity.json"] = File.ReadAllText(Path.Combine(package, "build-identity.json")),
            ["packages/contracts/descriptor.base64"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(package, "contracts.binpb"))),
            ["src/ArcForges.ArcScope/packages.lock.json"] = File.ReadAllText(Path.Combine(root, "src/ArcForges.ArcScope/packages.lock.json"))
        };
        foreach (var source in JsonNode.Parse(sources["packages/contracts/source.json"])!["schemaSources"]!.AsObject())
            sources.Add("packages/contracts/schema/" + source.Key, File.ReadAllText(Path.Combine(package, "schemas", source.Key)));
        return (catalog, sources);
    }
}
