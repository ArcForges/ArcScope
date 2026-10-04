// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using ArcForges.Build.Policy.Architecture;
using Microsoft.CodeAnalysis.CSharp;

namespace ArcForges.ArcScope.ArchitectureTests;

internal static class Checks
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message);
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message);
    }

    public static void Empty<T>(IEnumerable<T> values, string message)
    {
        if (values.Any()) throw new InvalidOperationException(message);
    }
}

/// <summary>One synthetic project of a fixture graph. Fixtures are compiled and analysed, never executed.</summary>
internal sealed record FixtureProject(
    string Path,
    ProjectRole Role,
    string[]? References = null,
    string License = "AGPL-3.0-only",
    string Boundary = "AGPL",
    bool Production = true,
    bool Aot = true,
    string Owner = "ArcScope",
    string Source = "internal sealed class Empty { }",
    string[]? Packages = null);

/// <summary>
/// A real temporary repository root for the shared engine. Every fixture is the smallest graph that can show one rule
/// passing and failing, so a rule is exercised by the same engine that evaluates the real ArcScope graph.
/// </summary>
internal static class PolicyFixture
{
    internal static readonly DateOnly Today = new(2026, 10, 4);

    public static IReadOnlyList<PolicyFinding> Check(IReadOnlyList<FixtureProject> projects,
        IReadOnlyDictionary<string, string>? dependencyLicences = null,
        IReadOnlyDictionary<string, ProjectRole>? dependencyRoles = null,
        IReadOnlyList<PolicyException>? exceptions = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "arcforges-gov07-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "global.json"), "{}\n");
            var facts = new List<ProjectFacts>();
            var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                string full = Path.Combine(root, project.Path);
                string directory = Path.GetDirectoryName(full)!;
                _ = Directory.CreateDirectory(directory);
                File.WriteAllText(full, "<Project />\n");
                var packages = (project.Packages ?? []).ToDictionary(package => package, _ => "1.0.0", StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(Path.Combine(directory, "packages.lock.json"), LockText(packages.Keys));
                string sourcePath = Path.Combine(directory, "Empty.cs");
                File.WriteAllText(sourcePath, project.Source + Environment.NewLine);
                var classification = new ProjectClassification(project.Path, project.Role, project.Owner,
                    Production: project.Production, Aot: project.Aot);
                var properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ManagePackageVersionsCentrally"] = "true",
                    ["RestorePackagesWithLockFile"] = "true",
                    ["SuppressTrimAnalysisWarnings"] = "false",
                    ["EnableTrimAnalyzer"] = "true",
                    ["EnableAotAnalyzer"] = "true",
                };
                facts.Add(new ProjectFacts(classification, "net10.0", "Library", project.License, project.Boundary,
                    project.References ?? [], [sourcePath], [], properties, packages));
                compilations.Add(project.Path, FixtureCompiler.Compile("Fixture" + facts.Count,
                    new Dictionary<string, string> { ["Empty.cs"] = project.Source }));
            }

            string commit = new('a', 40);
            var evidence = new[]
            {
                new ExternalPolicyEvidence("RP-01", commit, true, []),
                new ExternalPolicyEvidence("RP-08", commit, true, []),
                new ExternalPolicyEvidence("RP-09", commit, true, []),
            };
            var licences = new Dictionary<string, string>(dependencyLicences ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            var configuration = new RepositoryPolicyConfiguration(commit,
                new Dictionary<string, string> { ["global.json"] = HashNormalized(Path.Combine(root, "global.json")) },
                licences, new HashSet<string>(), [], [], evidence, DependencyRoles: dependencyRoles);
            var repository = new RepositoryFacts(root, "ArcScope", facts, exceptions ?? [], []);
            return PolicyEngine.Check(repository, configuration, compilations, Today);
        }
        finally
        {
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string target = System.IO.Path.GetFullPath(root);
            if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(target).StartsWith("arcforges-gov07-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unexpected architecture-fixture directory.");
            }

            Directory.Delete(target, recursive: true);
        }
    }

    public static bool Fires(IReadOnlyList<PolicyFinding> findings, string rule) => findings.Any(finding => finding.Rule == rule);

    internal static string HashNormalized(string path) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static string LockText(IEnumerable<string> packages)
    {
        var entries = packages.Select(package =>
            $"\"{package}\":{{\"type\":\"Direct\",\"requested\":\"[1.0.0, )\",\"resolved\":\"1.0.0\",\"contentHash\":\"AAAA\"}}");
        return "{\"version\":2,\"dependencies\":{\"net10.0\":{" + string.Join(",", entries) + "}}}\n";
    }
}
