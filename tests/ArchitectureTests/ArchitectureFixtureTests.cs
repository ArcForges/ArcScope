// SPDX-License-Identifier: AGPL-3.0-only

using ArcForges.Build.Policy.Architecture;

namespace ArcForges.ArcScope.ArchitectureTests;

/// <summary>
/// Positive and negative fixtures for every rule ArcScope enforces (WP-05.00, WP-05.01, WP-05.04 and the evidence
/// contract of WP-05.02). These run offline from the compiled host with no repository build output and no network.
/// </summary>
internal static class ArchitectureFixtureTests
{
    private static readonly (string Rule, string Allowed, string Banned)[] BannedCases =
    [
        ("BAN-REFLECTION", "class C { object? M() => typeof(string); }", "class C { object? M() => System.Type.GetType(\"Example\"); }"),
        ("BAN-CODEGEN", "class C { object M() => System.Linq.Expressions.Expression.Constant(1); }", "class C { object M() => new System.Reflection.Emit.DynamicMethod(\"example\", typeof(void), System.Type.EmptyTypes); }"),
        ("BAN-BLOCKING", "class C { async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(1); } }", "class C { async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Yield(); System.Threading.Tasks.Task.Delay(1).Wait(); } }"),
        ("BAN-PROVIDER", "class C { int M() => 1; }", "namespace OpenAI { public static class Client { public static int Call() => 1; } } class C { int M() => OpenAI.Client.Call(); }"),
        ("BAN-LOGGING", "class C { void M(string content) { _ = content.Length; } }", "class C { void M(string contentBody) { System.Console.WriteLine(contentBody); } }"),
        ("BAN-MONEY", "class Money { decimal Add(decimal value) => value + 1m; }", "class Money { double Add(double value) => value + 1d; }"),
        ("BAN-POINTER", "class C { internal System.Runtime.InteropServices.SafeHandle? Handle; }", "class C { internal System.IntPtr Handle; }"),
    ];

    public static void Run()
    {
        string[] expected = Enumerable.Range(1, 14).Select(value => $"AT-{value:00}")
            .Concat(Enumerable.Range(1, 10).Select(value => $"RP-{value:00}")).ToArray();
        Checks.SequenceEqual(expected, PolicyEngine.Rules, "Shared AT/RP rule manifest changed.");

        BannedApiFixtures();
        LayeringFixtures();
        LicenceBoundaryFixtures();
        ExternalPolicyEvidenceMustBeExactAndFailClosed();
        ExceptionsMustBeOwnedExactAndExpiring();
        FailureDiagnosticsDoNotRevealExceptionDetails();
        CompilationFailureCodesAreSpecificAndFailClosed();
        ContractConsumptionChecks.Fixtures();
    }

    // WP-05.04: every banned category has an allowed and a banned compiled fixture for each role that ArcScope owns.
    private static void BannedApiFixtures()
    {
        Checks.SequenceEqual(new[] { "BAN-REFLECTION", "BAN-CODEGEN", "BAN-BLOCKING", "BAN-PROVIDER",
            "BAN-LOGGING", "BAN-MONEY", "BAN-POINTER" }, BannedCases.Select(test => test.Rule).Distinct(),
            "A banned-API regression fixture is missing.");
        foreach (var role in new[] { ProjectRole.Domain, ProjectRole.Infrastructure, ProjectRole.UserInterface })
        {
            foreach (var test in BannedCases)
            {
                var classification = new ProjectClassification("fixture.csproj", role, "ArcScope", Aot: true);
                var allowed = FixtureCompiler.Compile("Allowed", new Dictionary<string, string> { ["allowed.cs"] = test.Allowed });
                var banned = FixtureCompiler.Compile("Banned", new Dictionary<string, string> { ["banned.cs"] = test.Banned });
                Checks.Empty(BannedSymbolScanner.Scan(allowed, classification), $"Allowed fixture triggered {test.Rule} in role {role}.");
                bool triggered = BannedSymbolScanner.Scan(banned, classification).Any(finding => finding.Rule == test.Rule);
                if (test.Rule == "BAN-PROVIDER" && role == ProjectRole.Infrastructure)
                {
                    // ArcScope's Infrastructure projects are its adapters, the only place a provider SDK call is permitted.
                    Checks.True(!triggered, "A provider call inside an adapter project was rejected.");
                    continue;
                }

                Checks.True(triggered, $"Banned fixture did not trigger {test.Rule} in role {role}.");
            }
        }

        ExecutableProductionFixtures();

        // A reflection entry point is a finding only on a path classified as AOT; the category is not a blanket ban.
        var reflective = FixtureCompiler.Compile("Reflective", new Dictionary<string, string> { ["reflective.cs"] = BannedCases[0].Banned });
        Checks.Empty(BannedSymbolScanner.Scan(reflective, new ProjectClassification("fixture.csproj", ProjectRole.Infrastructure, "ArcScope", Aot: false)),
            "Reflection was reported on a path that is not classified as AOT.");
    }

    // The Native AOT executable is a production project that makes unmanaged function-pointer calls (NativePackageProof.cs).
    // The shared engine audits such a file: the calls alone are clean, and every banned category added next to or inside
    // them is still reported in the executable's own role.
    private const string PointerProof = """
        using System.Runtime.InteropServices;
        internal static unsafe class Proof
        {
            internal static int Run(nint library)
            {
                var negotiate = (delegate* unmanaged[Cdecl]<uint*, uint*, int>)NativeLibrary.GetExport(library, "abi_version");
                uint minor = 0;
                return negotiate(null, &minor);
            }
        }
        """;

    private static void ExecutableProductionFixtures()
    {
        var executable = new ProjectClassification("exe.csproj", ProjectRole.UserInterface, "ArcScope", Production: true, Aot: true);
        var clean = FixtureCompiler.Compile("PointerProof", new Dictionary<string, string> { ["proof.cs"] = PointerProof });
        Checks.Empty(BannedSymbolScanner.Scan(clean, executable), "The executable's function-pointer call was reported or not audited.");

        // Mutants of the executable: each adds one banned construct to the same file and must be reported once, as its category.
        (string Rule, string Added)[] mutants =
        [
            ("BAN-BLOCKING", "internal static class Extra { internal static async System.Threading.Tasks.Task Run() { await System.Threading.Tasks.Task.Yield(); System.Threading.Tasks.Task.Delay(1).Wait(); } }"),
            ("BAN-REFLECTION", "internal static class Extra { internal static object? Run() => System.Type.GetType(\"Example\"); }"),
            ("BAN-MONEY", "internal static class Money { internal static double Price(double value) => value + 1d; }"),
            ("BAN-CODEGEN", "internal static class Extra { internal static object Run() => new System.Reflection.Emit.DynamicMethod(\"example\", typeof(void), System.Type.EmptyTypes); }"),
            ("BAN-POINTER", "internal static class Extra { internal static System.IntPtr Handle; }"),
        ];
        foreach (var (rule, added) in mutants)
        {
            var mutant = FixtureCompiler.Compile("PointerProofMutant", new Dictionary<string, string> { ["proof.cs"] = PointerProof + "\n" + added });
            Checks.True(BannedSymbolScanner.Scan(mutant, executable).Any(finding => finding.Rule == rule),
                $"The executable mutant was not reported as {rule}.");
        }

        // The same constructs nested in the arguments of the pointer call are reported as well.
        // The System.Text.Json generated reflection providers of the executable are exempt only at their exact anchored path.
        string repo = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arcscope-fixture"));
        string project = Path.Combine(repo, "src", "ArcForges.ArcScope");
        string Generated(params string[] parts) => Path.Combine([project, "obj", "arcforges-policy", "Release", "generated", .. parts]);
        const string Json = "System.Text.Json.SourceGeneration";
        const string JsonGenerator = "System.Text.Json.SourceGeneration.JsonSourceGenerator";
        Checks.True(HostedPolicyGate.IsAnchoredJsonGeneratedTree(Generated(Json, JsonGenerator, "SmokeJson.SmokeReport.g.cs"), project),
            "The executable's own JSON generator output was not recognised.");
        foreach (string spoof in new[]
        {
            Path.Combine(project, "Authored.cs"),
            Path.Combine(project, "Spoof", "obj", "arcforges-policy", "Release", "generated", Json, JsonGenerator, "X.g.cs"),
            Path.Combine(repo, "src", "ArcForges.ArcScope.Core", "obj", "arcforges-policy", "Release", "generated", Json, JsonGenerator, "X.g.cs"),
            Generated("Other.Generator", JsonGenerator, "X.g.cs"),
            Generated(Json + "X", JsonGenerator, "X.g.cs"),
            Generated(Json, "Other", "X.g.cs"),
            Generated(Json, JsonGenerator, "X.cs"),
            Generated(Json, JsonGenerator, "Nested", "X.g.cs"),
            Generated(Json, JsonGenerator, "..", "..", "..", "..", "Authored.g.cs"),
            Path.Combine([project, "obj", "arcforges-policy", "Debug", "generated", Json, JsonGenerator, "X.g.cs"]),
            "relative/SmokeJson.g.cs",
        })
        {
            Checks.True(!HostedPolicyGate.IsAnchoredJsonGeneratedTree(spoof, project), "A non-anchored path was exempted: " + spoof);
        }

        Checks.True(!HostedPolicyGate.IsAnchoredJsonGeneratedTree(Generated(Json, JsonGenerator, "X.g.cs"), Path.Combine(repo, "src", "Other")),
            "Another project's generated directory was exempted.");
        string anchored = Generated(Json, JsonGenerator, "SmokeJson.SmokeReport.g.cs");
        Checks.True(HostedPolicyGate.IsGeneratedJsonReflectionFinding(repo, new PolicyFinding("BAN-REFLECTION", anchored, "m", 1)),
            "The generated reflection finding was not exempted.");
        foreach (string rule in new[] { "BAN-BLOCKING", "BAN-CODEGEN", "BAN-MONEY", "BAN-POINTER", "BAN-PROVIDER", "BAN-LOGGING", "AT-12" })
        {
            Checks.True(!HostedPolicyGate.IsGeneratedJsonReflectionFinding(repo, new PolicyFinding(rule, anchored, "m", 1)),
                "A rule other than BAN-REFLECTION was exempted in the generated tree: " + rule);
        }

        var nested = FixtureCompiler.Compile("PointerNested", new Dictionary<string, string>
        {
            ["nested.cs"] = "internal static unsafe class N { internal static delegate* unmanaged<int, int> F; internal static int Run() => F(System.Type.GetType(\"Example\")!.GetHashCode()); }",
        });
        Checks.True(BannedSymbolScanner.Scan(nested, executable).Any(finding => finding.Rule == "BAN-REFLECTION"),
            "A reflection entry point nested in a function-pointer call was not reported.");
    }

    // WP-05.00: each layering rule has a passing positive case and a failing negative case in the shared engine.
    private static void LayeringFixtures()
    {
        const string foundation = "foundation/foundation.csproj";
        const string domain = "domain/domain.csproj";
        const string application = "application/application.csproj";
        const string infrastructure = "infrastructure/infrastructure.csproj";
        const string ui = "ui/ui.csproj";

        var cases = new (string Rule, string Description, FixtureProject[] Allowed, FixtureProject[] Violation)[]
        {
            ("AT-01", "domain projects reference no infrastructure",
                [new(foundation, ProjectRole.Foundation), new(domain, ProjectRole.Domain, [foundation])],
                [new(infrastructure, ProjectRole.Infrastructure), new(domain, ProjectRole.Domain, [infrastructure])]),
            ("AT-01", "application projects reference no UI",
                [new(domain, ProjectRole.Domain), new(application, ProjectRole.Application, [domain])],
                [new(ui, ProjectRole.UserInterface), new(application, ProjectRole.Application, [ui])]),
            ("AT-14", "the shared foundation holds no product knowledge",
                [new(foundation, ProjectRole.Foundation)],
                [new(domain, ProjectRole.Domain), new(foundation, ProjectRole.Foundation, [domain])]),
            ("AT-14", "building blocks (design system) reference no product layer",
                [new(foundation, ProjectRole.Foundation), new("design/design.csproj", ProjectRole.DesignSystem, [foundation])],
                [new(application, ProjectRole.Application), new("design/design.csproj", ProjectRole.DesignSystem, [application])]),
            ("AT-05", "no product references another product's internals",
                [new(domain, ProjectRole.Domain), new(ui, ProjectRole.UserInterface, [domain])],
                [new(domain, ProjectRole.Domain, Owner: "OtherProduct"), new(ui, ProjectRole.UserInterface, [domain])]),
            ("AT-04", "contract projects reference only contract projects and the foundation",
                [new(foundation, ProjectRole.Foundation), new("contract/contract.csproj", ProjectRole.Contracts, [foundation])],
                [new(infrastructure, ProjectRole.Infrastructure), new("contract/contract.csproj", ProjectRole.Contracts, [infrastructure])]),
            ("AT-02", "a local RPC adapter references no UI project",
                [new(domain, ProjectRole.Domain), new("rpc/rpc.csproj", ProjectRole.LocalRpcAdapter, [domain])],
                [new(ui, ProjectRole.UserInterface), new("rpc/rpc.csproj", ProjectRole.LocalRpcAdapter, [ui])]),
            ("AT-09", "the release graph reaches no non-production project",
                [new(domain, ProjectRole.Domain), new(ui, ProjectRole.UserInterface, [domain])],
                [new("tool/tool.csproj", ProjectRole.BuildTool, Production: false), new(ui, ProjectRole.UserInterface, ["tool/tool.csproj"])]),
            ("RP-07", "no project reachable from an AOT deliverable is fenced",
                [new(domain, ProjectRole.Domain), new(ui, ProjectRole.UserInterface, [domain])],
                [new(domain, ProjectRole.Domain, Aot: false), new(ui, ProjectRole.UserInterface, [domain])]),
        };
        foreach (var test in cases)
        {
            Checks.True(!PolicyFixture.Fires(PolicyFixture.Check(test.Allowed), test.Rule),
                $"The allowed layering fixture was rejected: {test.Description}.");
            Checks.True(PolicyFixture.Fires(PolicyFixture.Check(test.Violation), test.Rule),
                $"The negative layering fixture was accepted: {test.Description}.");
        }

        // Package-level direction: a domain or application project may depend only on reviewed domain, abstraction or
        // foundation packages, and platform or UI packages never reach an RPC adapter.
        var roles = new Dictionary<string, ProjectRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["Reviewed.Abstractions"] = ProjectRole.Abstractions,
            ["Reviewed.Transport"] = ProjectRole.Infrastructure,
        };
        var licences = new Dictionary<string, string> { ["Reviewed.Abstractions"] = "MIT", ["Reviewed.Transport"] = "MIT" };
        Checks.True(!PolicyFixture.Fires(PolicyFixture.Check([new(domain, ProjectRole.Domain, Packages: ["Reviewed.Abstractions"])], licences, roles), "AT-01"),
            "A reviewed abstraction package was rejected for a domain project.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(domain, ProjectRole.Domain, Packages: ["Reviewed.Transport"])], licences, roles), "AT-01"),
            "A transport package reached a domain project.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(domain, ProjectRole.Domain, Packages: ["Reviewed.Abstractions"])], licences), "AT-01"),
            "A domain package without a reviewed layer classification was accepted.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new("rpc/rpc.csproj", ProjectRole.PublicApiAdapter, Packages: ["Avalonia.Desktop"])],
            new Dictionary<string, string> { ["Avalonia.Desktop"] = "MIT" }), "AT-03"), "A UI platform package reached a public API adapter.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(infrastructure, ProjectRole.Infrastructure, Packages: ["Refit"])],
            new Dictionary<string, string> { ["Refit"] = "MIT" }), "AT-10"), "A reflection-based typed HTTP client was accepted.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(ui, ProjectRole.UserInterface,
            Source: "public sealed class Exposed { public System.IntPtr Handle() => default; }")]), "AT-06"),
            "A native pointer crossing a public boundary was accepted.");
        Checks.True(!PolicyFixture.Fires(PolicyFixture.Check([new(ui, ProjectRole.UserInterface,
            Source: "public sealed class Exposed { public System.Runtime.InteropServices.SafeHandle? Handle() => null; }")]), "AT-06"),
            "A safe handle across a public boundary was rejected.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new("rpc/rpc.csproj", ProjectRole.LocalRpcAdapter,
            Source: "public sealed class Service { public void Call() { } }")]), "AT-11"),
            "A local RPC service without its generated interface and port mapping was accepted.");
    }

    // WP-05.01: SPDX and boundary declaration, the Apache set, project edges and dependency licences.
    private static void LicenceBoundaryFixtures()
    {
        const string apacheLib = "apache/lib.csproj";
        const string agplLib = "agpl/lib.csproj";
        var apache = new FixtureProject(apacheLib, ProjectRole.Abstractions, License: "Apache-2.0", Boundary: "Apache");

        Checks.True(!PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain)]), "RP-02"),
            "A consistent AGPL SPDX/boundary pair was rejected.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain, License: "MIT")]), "RP-02"),
            "A project with a mismatched SPDX identifier was accepted.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain, Boundary: "")]), "RP-02"),
            "A project with no boundary was accepted.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain, License: "")]), "RP-02"),
            "A project with no SPDX identifier was accepted.");

        Checks.True(!PolicyFixture.Fires(PolicyFixture.Check([apache, new("apache/other.csproj", ProjectRole.Abstractions, [apacheLib],
            License: "Apache-2.0", Boundary: "Apache")]), "RP-03"), "An Apache-to-Apache edge was rejected.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain),
            new(apacheLib, ProjectRole.Abstractions, [agplLib], License: "Apache-2.0", Boundary: "Apache")]), "RP-03"),
            "An Apache-to-AGPL project edge was accepted.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain),
            new("apache/mid.csproj", ProjectRole.Abstractions, [agplLib], License: "Apache-2.0", Boundary: "Apache"),
            new("apache/top.csproj", ProjectRole.Abstractions, ["apache/mid.csproj"], License: "Apache-2.0", Boundary: "Apache")]), "RP-03"),
            "A transitive Apache-to-AGPL edge was accepted.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(apacheLib, ProjectRole.Abstractions, License: "Apache-2.0", Boundary: "Apache",
            Packages: ["Some.Agpl.Package"])], new Dictionary<string, string> { ["Some.Agpl.Package"] = "AGPL-3.0-only" }), "RP-03"),
            "An Apache project with an AGPL package was accepted.");
        Checks.True(!PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain, Packages: ["Some.Agpl.Package"])],
            new Dictionary<string, string> { ["Some.Agpl.Package"] = "AGPL-3.0-only" }), "RP-03"),
            "An AGPL project with an AGPL package was rejected.");
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([new(agplLib, ProjectRole.Domain, Packages: ["Unclassified.Package"])]), "RP-02"),
            "A package with no classified licence was accepted.");

        // Dependency allowlist for the consuming boundary, evaluated from the repository's own reuse policy.
        using var policy = System.Text.Json.JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(RepositoryRoot.Find(), "eng/policy/reuse-policy.json")));
        var root = policy.RootElement;
        Checks.True(LicenceAllowlist.IsAdmitted(root, "AGPL", "MIT", "https://github.com/dotnet/runtime"), "A permissive licence was rejected for the AGPL boundary.");
        Checks.True(LicenceAllowlist.IsAdmitted(root, "AGPL", "Apache-2.0", "https://github.com/grpc/grpc-dotnet"), "Apache-2.0 was rejected for the AGPL boundary.");
        Checks.True(LicenceAllowlist.IsAdmitted(root, "AGPL", "AGPL-3.0-only", "https://github.com/ArcForges/DesktopPlatform"),
            "An exact ArcForges AGPL package was rejected.");
        Checks.True(!LicenceAllowlist.IsAdmitted(root, "AGPL", "AGPL-3.0-only", "https://github.com/example/other"),
            "An AGPL package from outside ArcForges was admitted.");
        Checks.True(!LicenceAllowlist.IsAdmitted(root, "AGPL", "GPL-3.0-only", "https://github.com/ArcForges/DesktopPlatform"),
            "A GPL-only package was admitted.");
        Checks.True(!LicenceAllowlist.IsAdmitted(root, "AGPL", "Unknown-1.0", "https://github.com/ArcForges/DesktopPlatform"),
            "An unclassified licence was admitted.");
        Checks.True(!LicenceAllowlist.IsAdmitted(root, "Apache", "AGPL-3.0-only", "https://github.com/ArcForges/DesktopPlatform"),
            "An AGPL package was admitted for the Apache boundary.");
        Checks.True(!LicenceAllowlist.IsAdmitted(root, "Elsewhere", "MIT", "https://github.com/dotnet/runtime"), "An unknown consuming boundary admitted a package.");
    }

    // RP-01, RP-08 and RP-09 consume existing gates; missing, stale or failing evidence must never pass.
    private static void ExternalPolicyEvidenceMustBeExactAndFailClosed()
    {
        string root = RepositoryRoot.Find();
        string sourceCommit = new('a', 40);
        var repository = new RepositoryFacts(root, "ArcScope", [], [], []);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["global.json"] = PolicyFixture.HashNormalized(System.IO.Path.Combine(root, "global.json")),
        };
        var absent = PolicyEngine.Check(repository, Configuration([]), new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(), PolicyFixture.Today);
        foreach (string rule in new[] { "RP-01", "RP-08", "RP-09" })
        {
            Checks.True(absent.Any(finding => finding.Rule == rule), $"Missing evidence incorrectly passed {rule}.");
        }

        var evidence = new[]
        {
            new ExternalPolicyEvidence("RP-01", sourceCommit, true, []),
            new ExternalPolicyEvidence("RP-08", sourceCommit, true, []),
            new ExternalPolicyEvidence("RP-09", sourceCommit, true, []),
        };
        var valid = PolicyEngine.Check(repository, Configuration(evidence),
            new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(), PolicyFixture.Today);
        Checks.True(valid.All(finding => finding.Rule is not ("RP-01" or "RP-08" or "RP-09")),
            "Exact, successful evidence was not accepted.");

        foreach (string rule in new[] { "RP-01", "RP-08", "RP-09" })
        {
            var stale = evidence.Select(row => row.Rule == rule ? row with { SourceCommit = new string('b', 40) } : row).ToArray();
            Checks.True(PolicyEngine.Check(repository, Configuration(stale),
                new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(), PolicyFixture.Today).Any(finding => finding.Rule == rule),
                $"Stale source evidence incorrectly passed {rule}.");
            var failed = evidence.Select(row => row.Rule == rule ? row with { Passed = false } : row).ToArray();
            Checks.True(PolicyEngine.Check(repository, Configuration(failed),
                new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(), PolicyFixture.Today).Any(finding => finding.Rule == rule),
                $"Failing evidence incorrectly passed {rule}.");
        }

        RepositoryPolicyConfiguration Configuration(IReadOnlyList<ExternalPolicyEvidence> supplied) =>
            new(sourceCommit, hashes, new Dictionary<string, string>(), new HashSet<string>(), [], [], supplied);
    }

    // The exception inventory is the only way to carry a finding. It is exact, owned and expiring, never a wildcard.
    private static void ExceptionsMustBeOwnedExactAndExpiring()
    {
        var violation = new FixtureProject("tool/domain.csproj", ProjectRole.Domain, License: "MIT");
        var exact = new PolicyException("RP-02", "tool/domain.csproj", "ArcScope", "Reviewed fixture exception.", new DateOnly(2027, 1, 1));
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([violation]), "RP-02"), "The exception fixture has no finding to carry.");
        Checks.True(!PolicyFixture.Fires(PolicyFixture.Check([violation], exceptions: [exact]), "RP-02"),
            "An exact owned exception did not carry its finding.");
        foreach (var rejected in new[]
        {
            exact with { Expires = PolicyFixture.Today },
            exact with { Owner = "Another" },
            exact with { Path = "tool/*.csproj" },
            exact with { Reason = " " },
            exact with { Rule = "RP-99" },
        })
        {
            Checks.True(Throws(() => PolicyFixture.Check([violation], exceptions: [rejected])),
                "An invalid, unowned, wildcard or expired exception was accepted.");
        }

        var other = exact with { Path = "tool/other.csproj" };
        Checks.True(PolicyFixture.Fires(PolicyFixture.Check([violation], exceptions: [other]), "RP-02"),
            "An exception for a different path hid a finding.");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void FailureDiagnosticsDoNotRevealExceptionDetails()
    {
        const string privateDetail = "untrusted exception detail must never be printed";
        using var output = new StringWriter();
        Program.WriteFailure(output, PolicyGateStage.EvaluateSharedPolicy, new Exception(privateDetail));

        string message = output.ToString().Trim();
        Checks.Equal("ArcScope architecture policy failed closed at stage EvaluateSharedPolicy.", message,
            "Failure diagnostics must contain only a fixed stage code.");
        Checks.True(!message.Contains(privateDetail, StringComparison.Ordinal),
            "An exception detail escaped the redacted fail-closed diagnostic.");
    }

    private static void CompilationFailureCodesAreSpecificAndFailClosed()
    {
        const string missingInputsDetail = "Completed source/reference inputs are required: private/path";
        const string outputTypeDetail = "Unsupported managed output kind: SecretOutputType";
        const string compilationDetail = "Invalid owning compilation: private/path\nprivate compiler diagnostic";
        const string unknownDetail = "unrecognized private producer detail";
        var cases = new (Exception Exception, PolicyGateStage Stage, string Secret)[]
        {
            (new InvalidOperationException(missingInputsDetail), PolicyGateStage.MissingSourceOrReferenceInputs, "private/path"),
            (new InvalidOperationException(outputTypeDetail), PolicyGateStage.UnsupportedOutputType, "SecretOutputType"),
            (new InvalidOperationException(compilationDetail), PolicyGateStage.ReconstructedCompilationDiagnostics, "private compiler diagnostic"),
            (new InvalidOperationException(unknownDetail), PolicyGateStage.ReadProjectCompilations, unknownDetail),
            (new IOException("private I/O detail"), PolicyGateStage.ReadProjectCompilations, "private I/O detail"),
        };

        foreach (var test in cases)
        {
            Checks.Equal(test.Stage, HostedPolicyGate.ClassifyCompilationFailure(test.Exception),
                "A compilation failure selected the wrong fixed diagnostic stage.");
            using var output = new StringWriter();
            Program.WriteFailure(output, test.Stage, test.Exception);
            string message = output.ToString().Trim();
            Checks.Equal($"ArcScope architecture policy failed closed at stage {test.Stage}.", message,
                "A compilation failure diagnostic did not contain only its fixed stage code.");
            Checks.True(!message.Contains(test.Secret, StringComparison.Ordinal),
                "A compilation failure detail escaped the redacted diagnostic.");
        }
    }
}
