// SPDX-License-Identifier: AGPL-3.0-only
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcForges.Repository;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class CandidateTests
{
    [Fact]
    public void CiAttemptsHaveDifferentImmutableVersions()
    {
        Assert.Equal("0.1.0-ci.23.1", Program.Version("23", "1"));
        Assert.NotEqual(Program.Version("23", "1"), Program.Version("23", "2"));
        Assert.Throws<ArgumentException>(() => Program.Version("23", "0"));
    }

    [Fact]
    public void ReleaseRejectsTamperingOrWrongSource()
    {
        using var fixture = new CandidateFixture();
        Assert.Equal("win-x64", Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit, fixture.SourceRoot));
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, new string('b', 40), fixture.SourceRoot));
        File.AppendAllText(fixture.Archive, "changed after validation");
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit, fixture.SourceRoot));
    }

    [Theory]
    [InlineData("../outside.zip", true)]
    [InlineData(null, false)]
    public void ReleaseRejectsPathEscapeOrFailedLiveEvidence(string? unsafeArchive, bool success)
    {
        using var fixture = new CandidateFixture(unsafeArchive, success);
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit, fixture.SourceRoot));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public void ZipAndTarNoticesAreActuallyRead(string rid)
    {
        using var fixture = new CandidateFixture(rid: rid);
        Assert.Equal(rid, Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit, fixture.SourceRoot));
    }

    [Theory]
    [InlineData("win-x64", "missing")]
    [InlineData("win-x64", "changed")]
    [InlineData("win-x64", "duplicate")]
    [InlineData("win-x64", "wrong-source")]
    [InlineData("win-x64", "dirty")]
    [InlineData("win-x64", "case-collision")]
    [InlineData("win-x64", "escape")]
    [InlineData("linux-x64", "missing")]
    [InlineData("linux-x64", "changed")]
    [InlineData("linux-x64", "duplicate")]
    [InlineData("linux-x64", "wrong-source")]
    [InlineData("linux-x64", "dirty")]
    [InlineData("linux-x64", "case-collision")]
    [InlineData("linux-x64", "escape")]
    public void MatchingOuterHashesCannotHideMissingLegalTextOrWrongSource(string rid, string mode)
    {
        using var fixture = new CandidateFixture(rid: rid, mode: mode);
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit, fixture.SourceRoot));
    }

    private sealed class CandidateFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "arcscope-test-" + Guid.NewGuid());
        public string Version => "0.1.0-ci.23.1";
        public string Commit => new('a', 40);
        public string SourceRoot { get; }
        public string Manifest => Path.Combine(_folder, "manifest.json");
        public string Archive { get; }

        public CandidateFixture(string? unsafeArchive = null, bool success = true, string rid = "win-x64", string mode = "valid")
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repo.FullName, ProvenancePolicy.Policy))) repo = repo.Parent ?? throw new InvalidOperationException("Repository policy not found.");
            SourceRoot = repo.FullName;
            Archive = Path.Combine(_folder, $"arcscope-{Version}-{rid}" + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz"));
            Directory.CreateDirectory(_folder);
            var entries = ProvenancePolicy.PackageNotices(SourceRoot).Select(n => (n.Key, n.Value)).ToList();
            entries.Add(("notices/provenance-source.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { result = "passed", sourceCommit = mode == "wrong-source" ? new string('b', 40) : Commit, dirty = mode == "dirty" }))));
            if (mode == "missing") entries.RemoveAll(e => e.Key == "LICENSE");
            if (mode == "changed") entries[entries.FindIndex(e => e.Key == "LICENSE")] = ("LICENSE", Encoding.UTF8.GetBytes("abbreviated licence\n"));
            if (mode == "duplicate") entries.Add(entries.Single(e => e.Key == "LICENSE"));
            if (mode == "case-collision") entries.Add(("license", Encoding.UTF8.GetBytes("shadowed legal text")));
            if (mode == "escape") entries.Add(("../outside", Encoding.UTF8.GetBytes("outside member")));
            if (Archive.EndsWith(".zip", StringComparison.Ordinal))
            {
                using var zip = ZipFile.Open(Archive, ZipArchiveMode.Create);
                foreach (var notice in entries)
                {
                    using var output = zip.CreateEntry(notice.Key).Open();
                    output.Write(notice.Value);
                }
            }
            else
            {
                using var output = File.Create(Archive);
                using var gzip = new GZipStream(output, CompressionLevel.Optimal);
                using var tar = new TarWriter(gzip);
                foreach (var notice in entries)
                {
                    using var data = new MemoryStream(notice.Value);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, notice.Key) { DataStream = data });
                }
            }
            var smoke = Path.Combine(_folder, "smoke.json");
            File.WriteAllText(smoke, JsonSerializer.Serialize(new
            {
                success,
                nativeAot = true,
                rid,
                sourceRevision = Commit,
                version = Version,
                uiGreeting = "Hello, ArcScope!",
                cloud = new { nativeAot = true },
                checks = new[] { "native-ui-live-action", "unicode-whitespace-boundary", "InvalidArgument", "ResourceExhausted" }
            }));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Archive)));
            File.WriteAllText(Archive + ".sha256", $"{hash}  {Path.GetFileName(Archive)}\n");
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new Candidate(rid, Version, Commit,
                unsafeArchive ?? Path.GetFileName(Archive), hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(smoke))))));
        }

        public void Dispose() => Directory.Delete(_folder, true);
    }
}
