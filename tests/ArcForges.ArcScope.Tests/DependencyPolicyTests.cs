// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Nodes;
using ArcForges.Repository;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class DependencyPolicyTests
{
    [Fact]
    public void ActualReviewedClosurePasses()
    {
        using var fixture = new Fixture();
        fixture.Check();
    }

    [Theory]
    [InlineData("existing-framework-closure; admitted DesktopPlatform Image ABI1.0 win-x64 exact candidate")]
    [InlineData("existing-framework-closure; admitted DesktopPlatform Image ABI1.0 win-x64 exact candidate; admitted Microsoft.Data.Sqlite10.0.12 SQLitePCLRaw.lib.e_sqlcipher 2.1.12 owner-store runtime closure")]
    [InlineData("existing-framework-closure; admitted DesktopPlatform Image ABI1.0 win-x64 exact candidate; admitted Microsoft.Data.Sqlite10.0.12 SQLitePCLRaw.lib.e_sqlite3 2.1.13 owner-store runtime closure")]
    public void MissingOrAdjacentOwnerStoreNativeDeclarationIsRefused(string declaration)
    {
        using var fixture = new Fixture();
        fixture.Edit("eng/policy/dependency-policy.json", data => data["nativeAdmission"] = declaration);
        Assert.Contains("Unreviewed native admission", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("licence", "GPL-3.0-only", "Forbidden dependency licence")]
    [InlineData("version", "1.*", "Floating dependency version")]
    [InlineData("sourceCommit", "main", "Floating source tag")]
    [InlineData("sourceRepository", "https://github.com/untrusted/publisher", "Wrong publisher")]
    [InlineData("id", "ArcForges.Contracts.Internal", "Wrong publisher")]
    public void RejectsUnreviewedAdmission(string field, string value, string expected)
    {
        using var fixture = new Fixture();
        fixture.Edit("eng/policy/dependency-policy.json", data => data["packages"]![field == "id" ? 1 : 0]![field] = value);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAdmissionDoesNotAdmitOtherPackageLicencesOrPublishers()
    {
        using var publicContract = new Fixture();
        publicContract.Edit("eng/policy/dependency-policy.json", data => data["packages"]![1]!["licence"] = "AGPL-3.0-only");
        Assert.Contains("Forbidden dependency licence", Assert.Throws<InvalidOperationException>(publicContract.Check).Message, StringComparison.Ordinal);
        using var unknownNative = new Fixture();
        unknownNative.Edit("eng/policy/dependency-policy.json", data => data["packages"]![0]!["id"] = "ArcForges.Native.Unreviewed");
        Assert.Contains("Forbidden dependency licence", Assert.Throws<InvalidOperationException>(unknownNative.Check).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ArcForges.Capabilities")]
    [InlineData("ArcForges.Persistence.Sqlite")]
    [InlineData("ArcForges.Security")]
    [InlineData("ArcForges.Security.CapabilityEnforcement")]
    [InlineData("ArcForges.Security.Audit")]
    [InlineData("ArcForges.Contracts.LocalRpc.Platform")]
    [InlineData("ArcForges.Sdk.Contracts")]
    public void AdmittedManagedProducerCannotChangePublisher(string id)
    {
        using var fixture = new Fixture();
        fixture.Edit("eng/policy/dependency-policy.json", data =>
        {
            var producer = Assert.Single(data["packages"]!.AsArray(), package => package!["id"]!.GetValue<string>() == id)!;
            producer["sourceRepository"] = "https://github.com/untrusted/publisher";
        });
        Assert.Contains("Wrong publisher", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedSecurityAdmissionDoesNotAdmitAdjacentUnreviewedPackage()
    {
        using var fixture = new Fixture();
        fixture.Edit("eng/policy/dependency-policy.json", data =>
        {
            var producer = Assert.Single(data["packages"]!.AsArray(), package => package!["id"]!.GetValue<string>() == "ArcForges.Security")!;
            producer["id"] = "ArcForges.Security.Unreviewed";
        });
        Assert.Contains("Forbidden dependency licence", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSameVersionWithChangedBytes()
    {
        using var fixture = new Fixture();
        fixture.Edit("eng/policy/dependency-policy.json", data => data["packages"]![0]!["contentHash"] = Convert.ToBase64String(new byte[64]));
        Assert.Contains("Mutable version", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewReviewCannotRewritePreviouslyAdmittedCoordinate()
    {
        const string prior = """{"packages":[{"id":"example","version":"1.0.0","contentHash":"original"}]}""";
        const string updated = """{"packages":[{"id":"example","version":"1.0.0","contentHash":"replacement"}]}""";
        const string removed = """{"packages":[]}""";
        DependencyPolicy.ValidateHistory(prior, [prior]);
        Assert.Contains("Immutable historical coordinate", Assert.Throws<InvalidOperationException>(() => DependencyPolicy.ValidateHistory(updated, [removed, prior])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessorReviewCannotResetFrameworkBaseline()
    {
        const string sdk = """{"sdk":{"version":"10.0.401"}}""";
        const string packages = """<Project><ItemGroup><PackageVersion Include="Avalonia.Desktop" Version="12.1.2" /></ItemGroup></Project>""";
        const string review = """{"baselineFrameworkVersions":{"dotnet":"11.0.100","avalonia":"13.0.0"}}""";
        Assert.Contains("Framework baseline reset", Assert.Throws<InvalidOperationException>(() => DependencyPolicy.ValidateBaseline(review, sdk, packages)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StableCoreCannotConsumeCandidatePackages()
    {
        using var fixture = new Fixture();
        fixture.Edit("eng/policy/dependency-policy.json", data => data["channel"] = "stable");
        Assert.Contains("Preview dependency", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsChangedFeedAndFloatingAction()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "NuGet.Config");
        var original = File.ReadAllText(config);
        File.WriteAllText(config, original.Replace("api.nuget.org", "untrusted.example", StringComparison.Ordinal));
        Assert.Contains("Wrong package publisher feed", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
        File.WriteAllText(config, original);
        File.AppendAllText(Path.Combine(fixture.Root, ".github/workflows/ci.yml"), "\n      - uses: actions/checkout@main\n");
        Assert.Contains("Floating workflow action", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedInputsRequireReviewAndFrameworkMajorNeedsPosture()
    {
        using var fixture = new Fixture();
        fixture.Edit("global.json", data => data["sdk"]!["version"] = "11.0.100");
        Assert.Contains("changed without matching upgrade evidence", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
        fixture.Edit("eng/policy/dependency-review.json", data => data["inputs"]!["global.json"] = DependencyPolicy.HashText(File.ReadAllText(Path.Combine(fixture.Root, "global.json"))));
        Assert.Contains("Framework major upgrade", Assert.Throws<InvalidOperationException>(fixture.Check).Message, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dependency-policy-" + Guid.NewGuid());
        private readonly string[] _files;

        public Fixture()
        {
            var source = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(source, "eng/policy/dependency-policy.json")))
                source = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException("Owner source not found.");
            var start = new ProcessStartInfo("git") { WorkingDirectory = source, RedirectStandardOutput = true };
            start.ArgumentList.Add("ls-files");
            using var process = Process.Start(start)!;
            _files = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimEnd('\r'))
                .Where(DependencyPolicy.IsDependencyInput).Append("eng/policy/dependency-policy.json").ToArray();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            foreach (var path in _files.Append("eng/policy/dependency-review.json"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, path))!);
                File.Copy(Path.Combine(source, path), Path.Combine(Root, path));
            }
        }

        public void Check() => DependencyPolicy.Validate(Root, _files);
        public void Edit(string path, Action<JsonNode> change)
        {
            var data = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, path)))!;
            change(data);
            File.WriteAllText(Path.Combine(Root, path), data.ToJsonString());
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
