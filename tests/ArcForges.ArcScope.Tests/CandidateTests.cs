// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
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
        Assert.Equal("win-x64", Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit));
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, new string('b', 40)));
        File.AppendAllText(fixture.Archive, "changed after validation");
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit));
    }

    [Theory]
    [InlineData("../outside.zip", true)]
    [InlineData(null, false)]
    public void ReleaseRejectsPathEscapeOrFailedLiveEvidence(string? unsafeArchive, bool success)
    {
        using var fixture = new CandidateFixture(unsafeArchive, success);
        Assert.Throws<InvalidOperationException>(() => Program.VerifyCandidate(fixture.Manifest, fixture.Version, fixture.Commit));
    }

    private sealed class CandidateFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "arcscope-test-" + Guid.NewGuid());
        public string Version => "0.1.0-ci.23.1";
        public string Commit => new('a', 40);
        public string Manifest => Path.Combine(_folder, "manifest.json");
        public string Archive => Path.Combine(_folder, $"arcscope-{Version}-win-x64.zip");

        public CandidateFixture(string? unsafeArchive = null, bool success = true)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Archive, "synthetic archive bytes for hash validation");
            var smoke = Path.Combine(_folder, "smoke.json");
            File.WriteAllText(smoke, JsonSerializer.Serialize(new
            {
                success,
                nativeAot = true,
                rid = "win-x64",
                sourceRevision = Commit,
                version = Version,
                uiGreeting = "Hello, ArcScope!",
                cloud = new { nativeAot = true },
                checks = new[] { "native-ui-live-action", "unicode-whitespace-boundary", "InvalidArgument", "ResourceExhausted" }
            }));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Archive)));
            File.WriteAllText(Archive + ".sha256", $"{hash}  {Path.GetFileName(Archive)}\n");
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new Candidate("win-x64", Version, Commit,
                unsafeArchive ?? Path.GetFileName(Archive), hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(smoke))))));
        }

        public void Dispose() => Directory.Delete(_folder, true);
    }
}
