// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArcForges.Build.Policy.Architecture;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArcForges.ArcScope.ArchitectureTests;

/// <summary>
/// The hosted run of the shared engine over ArcScope's real project graph. It exists only inside the pull-request
/// quality job, after the existing Gitleaks scan of the full history and after the canonical forbidden-term scan, so the
/// RP-01, RP-08 and RP-09 evidence it hands the engine is bound to the exact source, run and attempt.
/// </summary>
internal static class HostedPolicyGate
{
    internal const string HostProject = "tests/ArchitectureTests/ArcForges.ArcScope.ArchitectureTests.csproj";
    internal const string HostedJob = "quality";

    // Every ArcScope project, classified by its real role. The set must equal the solution; an unlisted project fails closed.
    // - Domain is the exact time and channel model: no packages beyond the build-only policy package, no transport, no UI.
    // - Core is the offline-first client library (the gRPC-Web client over the published public contract and view-models).
    //   It holds transport, so it is Infrastructure rather than Application until its ports are separated by a later task.
    // - The executable is the Avalonia Native AOT shell; it composes Core and is the AOT deliverable.
    // DEFERRED: the executable is classified Production: false only because the shared engine fails closed with
    // "Unresolved invocation cannot be audited" on the unmanaged function-pointer calls in NativePackageProof.cs (a local
    // opt-in native proof), so it cannot yet run the banned-API, public-API or layer rules for production code. The deferral is
    // exact and self-expiring: DeferredExecutableScanIsStillBlocked fails once the engine can audit that file, and the
    // ArchitectureFixtureTests function-pointer fixture fails with it. Until then the executable still gets the layering,
    // licence, AOT-fence and contract-consumption rules; only the production-only rules are deferred.
    // Domain is not classified AOT because its project does not yet declare IsAotCompatible; the task that references it
    // from the AOT executable must declare the property and flip the classification (RP-07 then covers it).
    internal static readonly IReadOnlyList<ProjectClassification> Classifications =
    [
        new("src/ArcForges.ArcScope.Domain/ArcForges.ArcScope.Domain.csproj", ProjectRole.Domain, "ArcScope", Production: true, Aot: false),
        new("src/ArcForges.ArcScope.Core/ArcForges.ArcScope.Core.csproj", ProjectRole.Infrastructure, "ArcScope", Production: true, Aot: true),
        new("src/ArcForges.ArcScope/ArcForges.ArcScope.csproj", ProjectRole.UserInterface, "ArcScope", Production: false, Aot: true),
        new("tests/ArcForges.ArcScope.Tests/ArcForges.ArcScope.Tests.csproj", ProjectRole.Test, "ArcScope", Production: false, Aot: false),
        new(HostProject, ProjectRole.Test, "ArcScope", Production: false, Aot: false),
        new("eng/ArcForges.Repository/ArcForges.Repository.csproj", ProjectRole.BuildTool, "ArcScope", Production: false, Aot: false),
    ];

    // The build-only policy package is the one package every project receives. It ships no runtime assembly, so for the
    // layered projects it is classified with the foundation packages that Domain may use.
    internal static readonly IReadOnlyDictionary<string, ProjectRole> DependencyRoles = new Dictionary<string, ProjectRole>(StringComparer.OrdinalIgnoreCase)
    {
        ["ArcForges.Build.Policy"] = ProjectRole.Foundation,
    };

    public static void Run(Action<PolicyGateStage> setStage)
    {
        setStage(PolicyGateStage.LocateRepository);
        string root = RepositoryRoot.Find();

        setStage(PolicyGateStage.ValidateHostedIdentity);
        string sourceCommit = RequireHostedIdentity(root);

        setStage(PolicyGateStage.ValidateRp01Evidence);
        ExternalPolicyEvidence naming = ReadNamingEvidence(root, sourceCommit, "RP-01");

        setStage(PolicyGateStage.ValidateRp08Evidence);
        ExternalPolicyEvidence licence = ReadNamingEvidence(root, sourceCommit, "RP-08");

        setStage(PolicyGateStage.ValidateHostedWorkflow);
        VerifyHostedWorkflow(File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml")));

        setStage(PolicyGateStage.ReadProjectGraph);
        var projects = ReadProjectGraph(root);

        setStage(PolicyGateStage.ReadProjectCompilations);
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
        foreach (var project in projects.Where(project => project.Classification.Role != ProjectRole.BuildTool))
        {
            try
            {
                compilations.Add(project.Classification.Path, ProjectGraph.ReadCompilation(project));
            }
            catch (Exception exception)
            {
                PolicyGateStage diagnosticStage = ClassifyCompilationFailure(exception);
                if (diagnosticStage != PolicyGateStage.ReadProjectCompilations) setStage(diagnosticStage);
                throw;
            }
        }

        setStage(PolicyGateStage.ValidateContractConsumption);
        VerifyContractConsumption(root, projects, compilations);

        VerifyDeferredExecutableScanIsStillBlocked(projects, compilations);

        setStage(PolicyGateStage.ReadDependencyPolicy);
        var dependency = ReadDependencyPolicy(root);

        setStage(PolicyGateStage.ReadPolicyExceptions);
        var exceptions = ReadPolicyExceptions(root);

        setStage(PolicyGateStage.BindContractTests);
        var contractTests = BindPublicApiToArchitectureFact(projects, compilations);

        var evidence = new[] { naming, licence, new ExternalPolicyEvidence("RP-09", sourceCommit, true, []) };
        var repository = new RepositoryFacts(root, "ArcScope", projects, exceptions, contractTests);
        var configuration = new RepositoryPolicyConfiguration(sourceCommit, dependency.Hashes, dependency.Licenses,
            new HashSet<string>(StringComparer.Ordinal), [], [], evidence, DependencyRoles: DependencyRoles);

        setStage(PolicyGateStage.EvaluateSharedPolicy);
        var findings = PolicyEngine.Check(repository, configuration, compilations, DateOnly.FromDateTime(DateTime.UtcNow));
        if (findings.Count != 0)
        {
            setStage(PolicyGateStage.ValidatePolicyResults);
            // Rule, repository-relative path and the engine's own message identify the finding; no input or secret is echoed.
            foreach (var finding in findings)
            {
                Console.Error.WriteLine($"{finding.Rule} {Path.GetRelativePath(root, finding.Path).Replace('\\', '/')}:{finding.Line} {finding.Message}");
            }

            throw new InvalidOperationException("Shared architecture policy reported findings.");
        }
    }

    internal static PolicyGateStage ClassifyCompilationFailure(Exception exception)
    {
        if (exception is not InvalidOperationException invalidOperation) return PolicyGateStage.ReadProjectCompilations;

        // These prefixes are emitted by the exact pinned Build.Policy producer. They are
        // consumed only to select a fixed enum; the message itself is never displayed.
        if (invalidOperation.Message.StartsWith("Completed source/reference inputs are required:", StringComparison.Ordinal))
        {
            return PolicyGateStage.MissingSourceOrReferenceInputs;
        }

        if (invalidOperation.Message.StartsWith("Unsupported managed output kind:", StringComparison.Ordinal))
        {
            return PolicyGateStage.UnsupportedOutputType;
        }

        if (invalidOperation.Message.StartsWith("Invalid owning compilation:", StringComparison.Ordinal))
        {
            return PolicyGateStage.ReconstructedCompilationDiagnostics;
        }

        return PolicyGateStage.ReadProjectCompilations;
    }

    private static string RequireHostedIdentity(string root)
    {
        string? actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        string? job = Environment.GetEnvironmentVariable("GITHUB_JOB");
        string? source = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (actions != "true" || job != HostedJob || source is null || !Regex.IsMatch(source, "^[0-9a-f]{40}$"))
        {
            throw new InvalidOperationException("The architecture gate requires the exact hosted quality job identity.");
        }

        string head = Git(root, "rev-parse", "HEAD");
        if (head != source) throw new InvalidOperationException("The architecture gate source differs from GITHUB_SHA.");
        if (Git(root, "status", "--porcelain", "--untracked-files=no").Length != 0)
        {
            throw new InvalidOperationException("The architecture gate requires a clean tracked source tree.");
        }

        return source;
    }

    /// <summary>The canonical forbidden-term scan (RP-01 and RP-08), bound to the exact source, run and attempt.</summary>
    private static ExternalPolicyEvidence ReadNamingEvidence(string root, string sourceCommit, string rule)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts/evidence/naming.json")));
        var report = document.RootElement;
        Checks.Equal(1, report.GetProperty("schemaVersion").GetInt32(), "Canonical naming report has the wrong schema.");
        Checks.Equal("source-policy-scan", report.GetProperty("evidenceClass").GetString(), "Canonical naming report has the wrong evidence class.");
        Checks.Equal("ArcScope", report.GetProperty("repository").GetString(), "Canonical naming report is for another repository.");
        Checks.Equal(sourceCommit, report.GetProperty("commit").GetString(), "Canonical naming report is not bound to GITHUB_SHA.");
        Checks.Equal(false, report.GetProperty("dirty").GetBoolean(), "Canonical naming report is not from a clean checkout.");
        Checks.Equal("pass", report.GetProperty("status").GetString(), "Canonical naming policy did not pass.");
        Checks.Equal(0, report.GetProperty("findings").GetArrayLength(), "Canonical naming report contains findings.");
        Checks.Equal(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), report.GetProperty("runId").GetString(), "Canonical naming report is from another run.");
        Checks.Equal(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), report.GetProperty("runAttempt").GetString(), "Canonical naming report is from another attempt.");

        // The scanner must be the exact reviewed package, and its fixtures must have exercised every forbidden term.
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests/ArchitectureTests/naming-package.json")));
        var reported = report.GetProperty("namingPackage");
        foreach (string field in new[] { "package", "version", "contentHash", "sourceCommit" })
        {
            Checks.Equal(pin.RootElement.GetProperty(field).GetString(), reported.GetProperty(field).GetString(),
                "Canonical naming report was not produced by the reviewed naming package.");
        }

        var fixtures = report.GetProperty("fixtures");
        Checks.Equal(1, fixtures.GetProperty("positive").GetInt32(), "The positive naming fixture did not run exactly once.");
        Checks.True(fixtures.GetProperty("negative").GetInt32() >= 1, "No forbidden-term negative fixture ran.");
        return new ExternalPolicyEvidence(rule, sourceCommit, true, []);
    }

    /// <summary>
    /// RP-09 consumes the existing full-history Gitleaks scan. The quality job must run it first, build with locked
    /// restore, then scan names and run this gate, none of them allowed to continue past a failure.
    /// </summary>
    internal static void VerifyHostedWorkflow(string workflow)
    {
        Checks.True(HasOrderedHostedSteps(workflow), "The quality job must keep the Gitleaks -> locked build -> naming scan -> architecture gate sequence.");
        Checks.True(workflow.Contains("fetch-depth: 0", StringComparison.Ordinal), "The hosted secret scan must inspect the full history.");
        Checks.True(workflow.Contains("zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f git /repo --redact=100 --no-banner", StringComparison.Ordinal),
            "The existing pinned, redacted Gitleaks command changed or was removed.");
        Checks.True(workflow.Contains("global-json-file: global.json", StringComparison.Ordinal), "The hosted build is not bound to the pinned SDK.");
        int quality = workflow.IndexOf("\n  quality:", StringComparison.Ordinal);
        Checks.True(quality >= 0, "The quality job is missing.");
        int next = workflow.IndexOf("\n  dependency-review:", quality, StringComparison.Ordinal);
        string job = workflow[quality..next];
        Checks.True(!job.Contains("continue-on-error: true", StringComparison.Ordinal) && !job.Contains("if: always()", StringComparison.Ordinal),
            "A quality step can treat a failed Gitleaks scan as successful antecedent evidence.");
        // Negative controls over the same predicate keep the check honest.
        Checks.True(!HasOrderedHostedSteps(workflow.Replace("tests/ArchitectureTests/bin/Release/net10.0/ArcForges.ArcScope.ArchitectureTests.dll --hosted", "", StringComparison.Ordinal)),
            "Removing the architecture gate did not fail its negative fixture.");
        Checks.True(!HasOrderedHostedSteps(workflow.Replace("dotnet restore ArcScope.slnx --locked-mode", "", StringComparison.Ordinal)),
            "Removing the locked restore did not fail its negative fixture.");
    }

    private static bool HasOrderedHostedSteps(string workflow)
    {
        string[] orderedSteps =
        [
            "- name: Scan Git history for secrets",
            "dotnet restore ArcScope.slnx --locked-mode",
            "dotnet build ArcScope.slnx -c Release --no-restore",
            "python3 tests/ArchitectureTests/naming_evidence.py --report artifacts/evidence/naming.json",
            "dotnet tests/ArchitectureTests/bin/Release/net10.0/ArcForges.ArcScope.ArchitectureTests.dll --hosted",
        ];
        int quality = workflow.IndexOf("\n  quality:", StringComparison.Ordinal);
        if (quality < 0) return false;
        int previous = quality;
        foreach (string step in orderedSteps)
        {
            int current = workflow.IndexOf(step, previous, StringComparison.Ordinal);
            if (current < previous) return false;
            previous = current;
        }

        return true;
    }

    private static List<ProjectFacts> ReadProjectGraph(string root)
    {
        var solution = XDocument.Load(Path.Combine(root, "ArcScope.slnx"));
        string[] enrolled = solution.Descendants("Project").Select(project => project.Attribute("Path")?.Value)
            .Where(path => path is not null && path.EndsWith(".csproj", StringComparison.Ordinal))!
            .Select(path => path!).Order(StringComparer.Ordinal).ToArray();
        Checks.SequenceEqual(Classifications.Select(project => project.Path).Order(StringComparer.Ordinal), enrolled,
            "The solution and the reviewed project classifications differ; every project needs a reviewed role.");
        Checks.True(enrolled.Contains(HostProject, StringComparer.Ordinal), "ArchitectureTests is absent from the full solution build graph.");

        var result = new List<ProjectFacts>(Classifications.Count);
        foreach (var classification in Classifications)
        {
            var facts = ProjectGraph.Evaluate(root, classification, configuration: "Release");
            if (classification.Aot)
            {
                Checks.Equal("true", facts.Properties["IsAotCompatible"], "A project classified as AOT does not declare IsAotCompatible.");
            }

            result.Add(facts);
        }

        return result;
    }

    private static void VerifyContractConsumption(string root, IReadOnlyList<ProjectFacts> projects,
        IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        foreach (var project in projects.Where(project => project.Classification.Role is not (ProjectRole.Test or ProjectRole.BuildTool)))
        {
            Checks.Empty(ContractConsumptionChecks.FindAll(compilations[project.Classification.Path]),
                "Production code bypasses the generated-contract consumption rules.");
            if (project.Classification.Aot && project.OutputType is "Exe" or "WinExe")
            {
                Checks.True(ContractConsumptionChecks.DisablesReflectionJson(XDocument.Load(Path.Combine(root, project.Classification.Path))),
                    "An AOT executable leaves reflection-based JSON metadata enabled.");
            }
        }
    }

    internal const string DeferredExecutable = "src/ArcForges.ArcScope/ArcForges.ArcScope.csproj";
    internal const string UnresolvedInvocation = "Unresolved invocation cannot be audited";

    /// <summary>
    /// The production-only rules of the executable are deferred for exactly one reason. Run the real banned-API scan as if the
    /// executable were production: it must still fail closed on NativePackageProof.cs and nowhere else. When it passes or
    /// fails differently, the deferral is obsolete or wrong and the gate fails.
    /// </summary>
    private static void VerifyDeferredExecutableScanIsStillBlocked(IReadOnlyList<ProjectFacts> projects,
        IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        var executable = projects.Single(project => project.Classification.Path == DeferredExecutable);
        Checks.True(!executable.Classification.Production, "The deferral record and the classification disagree.");
        bool blocked = false;
        try
        {
            _ = BannedSymbolScanner.Scan(compilations[DeferredExecutable], executable.Classification with { Production = true });
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith(UnresolvedInvocation, StringComparison.Ordinal))
        {
            blocked = exception.Message.Contains("NativePackageProof.cs", StringComparison.Ordinal);
        }

        Checks.True(blocked, "The engine now audits the executable (or fails elsewhere): remove the production-rule deferral.");
    }

    private static (Dictionary<string, string> Hashes, Dictionary<string, string> Licenses) ReadDependencyPolicy(string root)
    {
        // The reviewed dependency record seals the exact toolchain and lock inputs (SDK pin, central versions, every lock).
        using var review = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/policy/dependency-review.json")));
        var hashes = review.RootElement.GetProperty("inputs").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
        Checks.True(hashes.ContainsKey("global.json") && hashes.ContainsKey("Directory.Packages.props"),
            "The reviewed dependency inputs do not seal the SDK pin and the central package versions.");
        using var policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/policy/dependency-policy.json")));
        var licenses = policy.RootElement.GetProperty("packages").EnumerateArray()
            .GroupBy(package => package.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(package => package.GetProperty("licence").GetString()!)
                .Distinct(StringComparer.Ordinal).Single(), StringComparer.OrdinalIgnoreCase);
        return (hashes, licenses);
    }

    // The repository forbids reflection-based System.Text.Json, so the inventory is read as a document.
    private static PolicyException[] ReadPolicyExceptions(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/policy/exceptions.json")));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Policy exception inventory is not an array.");
        }

        string Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidOperationException("Policy exception row is incomplete.");
        return document.RootElement.EnumerateArray().Select(row => new PolicyException(Text(row, "rule"), Text(row, "path"),
            Text(row, "owner"), Text(row, "reason"), DateOnly.ParseExact(Text(row, "expires"), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture))).ToArray();
    }

    private static List<ContractTestBinding> BindPublicApiToArchitectureFact(
        IReadOnlyList<ProjectFacts> projects, IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        var host = compilations[HostProject];
        var testMethod = host.GetTypeByMetadataName("ArcForges.ArcScope.ArchitectureTests.ArcScopeArchitectureTests")?
            .GetMembers("VerifyEveryPublicApiHasArchitectureTestBinding").OfType<IMethodSymbol>().SingleOrDefault();
        if (testMethod is null || !testMethod.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Xunit.FactAttribute"))
        {
            throw new InvalidOperationException("The architecture contract fact is missing or not registered as a real test.");
        }

        string binding = PolicyEngine.MethodIdentity(testMethod);
        var result = new List<ContractTestBinding>();
        foreach (var project in projects.Where(project => project.Classification.Production))
        {
            var compilation = compilations[project.Classification.Path];
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type) continue;
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(method => method.DeclaredAccessibility == Accessibility.Public
                        && method.MethodKind == MethodKind.Ordinary && !method.IsImplicitlyDeclared))
                    {
                        result.Add(new ContractTestBinding(PolicyEngine.MethodIdentity(method), HostProject, binding));
                    }
                }
            }
        }

        return result;
    }

    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not be started.");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Git source identity could not be read.");
        return output;
    }
}
