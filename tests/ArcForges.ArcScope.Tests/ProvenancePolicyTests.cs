// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcForges.Repository;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class ProvenancePolicyTests
{
    [Fact]
    public void CompleteRecordAndInventoryPass()
    {
        using var fixture = new Fixture();
        var result = fixture.Validate();
        Assert.Equal("passed", result.Result);
        Assert.Equal(1, result.ReusedFiles);
        Assert.Equal(["fixture-source-r1"], result.ActiveRecords);
        Assert.StartsWith("ArcForges source provenance notices\n", fixture.Read(ProvenancePolicy.Summary), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sourceRepository")]
    [InlineData("sourceCommit")]
    [InlineData("sourcePaths")]
    [InlineData("licence")]
    [InlineData("attribution")]
    [InlineData("targets")]
    [InlineData("disposition")]
    [InlineData("verification")]
    [InlineData("notice")]
    [InlineData("lifetime")]
    public void EveryRequiredFieldIsChecked(string field)
    {
        using var fixture = new Fixture();
        fixture.Record.Remove(field);
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("sourceRepository", " ")]
    [InlineData("sourceRepository", "http://example.org/reference")]
    [InlineData("sourceRepository", "https://example.org/reference/../other")]
    [InlineData("sourceCommit", "moving-tag")]
    [InlineData("disposition", "Unreviewed")]
    [InlineData("kind", "unknown")]
    public void InvalidIdentityAndDispositionFail(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Record[field] = value;
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("GPL-3.0-only", "gpl-only")]
    [InlineData("EPL-1.0", "incompatible")]
    [InlineData("NOASSERTION", "unclear")]
    [InlineData("MIT", "agpl-compatible")]
    public void ProhibitedOrMismatchedLicenceFails(string expression, string category)
    {
        using var fixture = new Fixture();
        fixture.Record["licence"]!["spdx"] = expression;
        fixture.Record["licence"]!["category"] = category;
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void EditingDecisionDataCannotGrantPermission()
    {
        using var fixture = new Fixture();
        var policy = JsonNode.Parse(fixture.Read(ProvenancePolicy.Policy))!;
        policy["decisions"]!["gpl-only"]!["AGPL"] = "audit";
        fixture.Write(ProvenancePolicy.Policy, policy.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("C:/outside.cs")]
    [InlineData("src/*.cs")]
    [InlineData("src/../reused.cs")]
    public void RecordPathsCannotEscapeOrUsePatterns(string path)
    {
        using var fixture = new Fixture();
        fixture.Record["targets"]![0]!["path"] = path;
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void UnknownFileAndChangedOrMissingTargetFail()
    {
        using var fixture = new Fixture();
        fixture.Write("unclassified.cs", "new file\n");
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
        fixture.Sync();
        fixture.Write("src/reused.cs", "changed reused file\n");
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
        File.Delete(Path.Combine(fixture.Root, "src/reused.cs"));
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void MissingRecordAndChangedNoticeFail()
    {
        using var fixture = new Fixture();
        fixture.Reused["src/reused.cs"] = "missing-record-r1";
        fixture.Sync();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
        fixture.Reused["src/reused.cs"] = "fixture-source-r1";
        fixture.Sync();
        fixture.Write(ProvenancePolicy.Summary, "not the generated summary\n");
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void TemporaryAndGeneratedMaterialRequireConditionalEvidence()
    {
        using var fixture = new Fixture();
        fixture.Record["lifetime"]!["status"] = "temporary";
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
        fixture.Record["lifetime"]!["removalTrigger"] = "Remove when the assigned native owner replaces the fixture.";
        fixture.SaveRecord();
        fixture.Validate(writeNotice: true);
        fixture.Record["kind"] = "generated";
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
        fixture.Record["generation"] = JsonNode.Parse("""{"generators":[],"inputs":[],"command":"fixture generator","outputSpdx":"MIT"}""");
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void LegalTextPermissionCannotBeUsedForSourceCode()
    {
        using var fixture = new Fixture();
        fixture.Record["kind"] = "legal-text";
        fixture.Record["licence"]!["copyingPermission"] = "Permission to copy the legal document.";
        fixture.SaveRecord();
        Assert.Throws<InvalidOperationException>(() => fixture.Validate());
    }

    [Fact]
    public void UsedRecordsCannotBeChangedOrRemoved()
    {
        using var fixture = new Fixture();
        var history = fixture.History();
        fixture.Record["attribution"]![0] = "changed attribution";
        fixture.SaveRecord();
        Assert.Contains("Used record", Assert.Throws<InvalidOperationException>(() => fixture.Validate(history)).Message, StringComparison.Ordinal);
        File.Delete(Path.Combine(fixture.Root, Fixture.RecordPath));
        fixture.Sync();
        Assert.Contains("Used record", Assert.Throws<InvalidOperationException>(() => fixture.Validate(history)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedReuseCannotBecomeFirstParty()
    {
        using var fixture = new Fixture();
        using var previous = JsonDocument.Parse(fixture.Read(ProvenancePolicy.Inventory));
        var history = fixture.History();
        fixture.Reused.Clear();
        var second = (JsonObject)fixture.Record.DeepClone();
        second["id"] = "second-source-r1";
        second["targets"]![0]!["path"] = "second.cs";
        fixture.Write("second.cs", Fixture.Source);
        fixture.Write("eng/provenance/records/second-source-r1.json", second.ToJsonString());
        fixture.Reused.Add("second.cs", "second-source-r1");
        fixture.Sync();
        Assert.Contains("lost provenance", Assert.Throws<InvalidOperationException>(() => fixture.Validate(history, previous.RootElement)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupersedingRevisionPreservesTheOriginalAndItsBinding()
    {
        using var fixture = new Fixture();
        using var previous = JsonDocument.Parse(fixture.Read(ProvenancePolicy.Inventory));
        var history = fixture.History();
        var revised = (JsonObject)fixture.Record.DeepClone();
        revised["id"] = "fixture-source-r2";
        revised["supersedes"] = "fixture-source-r1";
        fixture.Write("eng/provenance/records/fixture-source-r2.json", revised.ToJsonString());
        fixture.Reused["src/reused.cs"] = "fixture-source-r2";
        fixture.Sync();
        Assert.Equal(2, fixture.Validate(history, previous.RootElement, true).Records);
        revised["supersedes"] = "unknown-r1";
        fixture.Write("eng/provenance/records/fixture-source-r2.json", revised.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => fixture.Validate(history, previous.RootElement));
    }

    [Fact]
    public void DuplicateJsonFieldsAreRejected()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.RecordPath, fixture.Read(Fixture.RecordPath).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal));
        Assert.Contains("Duplicate JSON", Assert.Throws<InvalidOperationException>(() => fixture.Validate()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedDirectoryCannotReadOutsideTheOwner()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Root, "linked");
        var target = Path.Combine(fixture.Root, "src");
        if (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        Assert.Contains("Linked", Assert.Throws<InvalidOperationException>(() => ProvenancePolicy.Read(fixture.Root, "linked/reused.cs")).Message, StringComparison.Ordinal);
        Directory.Delete(link);
    }

    [Fact]
    public void EventBaseComesFromTheTrustedEvent()
    {
        using var pr = JsonDocument.Parse("""{"pull_request":{"base":{"sha":"base-identity"},"head":{"sha":"untrusted-head"}}}""");
        Assert.Equal("base-identity", ProvenancePolicy.EventBase("pull_request", pr.RootElement));
        using var push = JsonDocument.Parse("""{"before":"previous-main","after":"new-main"}""");
        Assert.Equal("previous-main", ProvenancePolicy.EventBase("push", push.RootElement));
    }

    [Fact]
    public void PackageNoticePresenceAndFullBytesAreRequired()
    {
        var expected = new Dictionary<string, byte[]> { ["notices/upstream/LICENSE.txt"] = Encoding.UTF8.GetBytes("full permission and disclaimer\n") };
        ProvenancePolicy.VerifyPackageNotices(expected, path => expected[path]);
        Assert.Throws<InvalidOperationException>(() => ProvenancePolicy.VerifyPackageNotices(expected, _ => null));
        Assert.Throws<InvalidOperationException>(() => ProvenancePolicy.VerifyPackageNotices(expected, _ => Encoding.UTF8.GetBytes("abbreviated permission\n")));
    }

    private sealed class Fixture : IDisposable
    {
        public const string Source = "// Synthetic first-party test fixture, not real reused material.\n";
        public const string RecordPath = "eng/provenance/records/fixture-source-r1.json";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "arcscope-provenance-test-" + Guid.NewGuid());
        public Dictionary<string, string> Reused { get; } = new(StringComparer.Ordinal) { ["src/reused.cs"] = "fixture-source-r1" };
        public JsonObject Record { get; }

        public Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repo.FullName, ProvenancePolicy.Policy))) repo = repo.Parent ?? throw new InvalidOperationException("Repository fixture policy was not found.");
            Write(ProvenancePolicy.Policy, File.ReadAllText(Path.Combine(repo.FullName, ProvenancePolicy.Policy)));
            Write("eng/provenance/template.json", File.ReadAllText(Path.Combine(repo.FullName, "eng/provenance/template.json")));
            Record = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1,
                id = "fixture-source-r1",
                kind = "source",
                sourceRepository = "https://example.org/reference",
                sourceCommit = new string('a', 40),
                sourcePaths = new[] { "source.cs" },
                licence = new { spdx = "MIT", category = "permissive", evidence = new[] { new { path = "LICENSE", sha256 = new string('b', 64), finding = "Synthetic file-level evidence for validator tests only." } }, scope = "Synthetic test fixture.", copyingPermission = (string?)null },
                attribution = new[] { "Synthetic fixture attribution." },
                targets = new[] { new { path = "src/reused.cs", sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Source))), normalization = "lf" } },
                artifactTargets = System.Array.Empty<object>(),
                disposition = "Copy",
                verification = new { kind = "byte-match", command = "fixture comparison", expected = "fixed fixture bytes", artifacts = System.Array.Empty<object>() },
                notice = new { required = true, text = "Synthetic fixture notice.", files = new[] { "NOTICE.txt" }, distribution = "source", reason = "Test scope only." },
                lifetime = new { status = "permanent", owner = "Fixture owner", removalTrigger = (string?)null },
                generation = (object?)null,
                review = new { owner = "Licensing and Provenance Owner", reviewer = "Synthetic test reviewer", reviewedOn = "2026-09-19", decision = "approved", rationale = "Test data; no actual reuse approval.", baselineCommit = new string('c', 40), reconciliation = false },
                supersedes = (string?)null
            })!.AsObject();
            Write("src/reused.cs", Source);
            Write("NOTICE.txt", "Synthetic fixture notice.\n");
            Write(ProvenancePolicy.Summary, "");
            Write(ProvenancePolicy.Inventory, "{}");
            SaveRecord();
            Sync();
            Validate(writeNotice: true);
        }

        public string Read(string path) => File.ReadAllText(Path.Combine(Root, path));
        public void Write(string path, string text)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        public void SaveRecord() => Write(RecordPath, Record.ToJsonString());
        private string[] Files() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/')).ToArray();
        public void Sync() => Write(ProvenancePolicy.Inventory, JsonSerializer.Serialize(new { schemaVersion = 1, repository = "ArcScope", firstParty = Files().Where(path => !Reused.ContainsKey(path)).ToArray(), reused = Reused, artifacts = System.Array.Empty<string>() }));
        public Dictionary<string, byte[]> History() => new(StringComparer.Ordinal) { [RecordPath] = File.ReadAllBytes(Path.Combine(Root, RecordPath)) };
        public ProvenanceResult Validate(IReadOnlyDictionary<string, byte[]>? history = null, JsonElement? prior = null, bool writeNotice = false) =>
            ProvenancePolicy.Validate(Root, Files(), history ?? new Dictionary<string, byte[]>(), prior, writeNotice);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(full).StartsWith("arcscope-provenance-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            Directory.Delete(full, true);
        }
    }
}
