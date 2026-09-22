// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArcForges.Repository;

/// <summary>Offline admission of the owner's complete locked dependency closure.</summary>
public static partial class DependencyPolicy
{
    private const string Feed = "https://api.nuget.org/v3/index.json";
    private static readonly Dictionary<string, string> Publishers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ArcForges.Build.Policy"] = "https://github.com/ArcForges/DesktopPlatform",
        ["ArcForges.Contracts.PublicApi"] = "https://github.com/ArcForges/Contracts"
    };

    // Historical Git snapshots retain every admitted coordinate, including removed dependencies.
    // A new review may admit a new version but cannot rewrite bytes under a previous version.
    public static void ValidateHistory(string currentPolicy, IEnumerable<string> priorPolicies)
    {
        using var current = JsonDocument.Parse(currentPolicy);
        var hashes = current.RootElement.GetProperty("packages").EnumerateArray()
            .ToDictionary(p => Text(p, "id") + "/" + Text(p, "version"), p => Text(p, "contentHash"), StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in priorPolicies)
        {
            using var prior = JsonDocument.Parse(snapshot);
            foreach (var package in prior.RootElement.GetProperty("packages").EnumerateArray())
            {
                var coordinate = Text(package, "id") + "/" + Text(package, "version");
                if (hashes.TryGetValue(coordinate, out var hash))
                    Require(hash == Text(package, "contentHash"), "Immutable historical coordinate changed: " + coordinate);
                else hashes.Add(coordinate, Text(package, "contentHash"));
            }
        }
    }

    public static void ValidateBaseline(string reviewJson, string baselineGlobalJson, string baselinePackagesXml)
    {
        using var review = JsonDocument.Parse(reviewJson);
        using var sdk = JsonDocument.Parse(baselineGlobalJson);
        var declared = review.RootElement.GetProperty("baselineFrameworkVersions");
        var avalonia = XDocument.Parse(baselinePackagesXml).Descendants("PackageVersion")
            .Single(p => (string?)p.Attribute("Include") == "Avalonia.Desktop").Attribute("Version")!.Value;
        Require(Text(declared, "dotnet") == Text(sdk.RootElement.GetProperty("sdk"), "version") && Text(declared, "avalonia") == avalonia,
            "Framework baseline reset does not match accepted Git source.");
    }

    public static void Validate(string root, IEnumerable<string> inventory)
    {
        var files = inventory.Distinct(StringComparer.Ordinal).ToArray();
        string Read(string path)
        {
            var full = Path.GetFullPath(Path.Combine(root, path));
            var relative = Path.GetRelativePath(root, full);
            Require(!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Dependency input escapes owner.");
            return File.ReadAllText(full);
        }
        using var document = JsonDocument.Parse(Read("eng/policy/dependency-policy.json"));
        var policy = document.RootElement;
        Require(policy.GetProperty("schemaVersion").GetInt32() == 1 && Text(policy, "repository") == "ArcScope" && Text(policy, "licenceBoundary") == "AGPL", "Invalid dependency owner.");
        Require(Text(policy, "channel") is "foundation-candidate" or "stable", "Invalid dependency channel.");
        Require(Text(policy, "feed") == Feed, "Wrong dependency feed.");
        Require(Text(policy, "nativeAdmission") == "existing-framework-closure-only; new native slots require DesktopPlatform admission", "Unreviewed native admission.");
        var admitted = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in policy.GetProperty("packages").EnumerateArray())
        {
            var id = Text(entry, "id");
            var version = Text(entry, "version");
            Require(ExactVersion().IsMatch(version), "Floating dependency version.");
            Require(admitted.TryAdd(id + "/" + version, entry), "Duplicate dependency admission.");
            var licence = Text(entry, "licence");
            Require(licence is "MIT" or "Apache-2.0" or "BSD-3-Clause" || licence == "AGPL-3.0-only" && id == "ArcForges.Build.Policy", "Forbidden dependency licence.");
            Require(Convert.FromBase64String(Text(entry, "contentHash")).Length == 64, "Invalid lock integrity.");
            Require(Sha40().IsMatch(Text(entry, "sourceCommit")) && Sha256().IsMatch(Text(entry, "nuspecSha256")), "Floating source tag or missing exact source evidence.");
            Require(Uri.TryCreate(Text(entry, "sourceRepository"), UriKind.Absolute, out var source) && source.Scheme == "https", "Untrusted source location.");
            Require(Text(entry, "maintenanceAssessment").Length >= 30 && Text(entry, "licenceEvidence").Length >= 20, "Missing dependency review.");
            Require(Text(entry, "dependencyClass") is "framework" or "transport" or "tooling" or "library", "Missing dependency class.");
            if (id.StartsWith("ArcForges.", StringComparison.OrdinalIgnoreCase))
                Require(Publishers.TryGetValue(id, out var publisher) && publisher == Text(entry, "sourceRepository"), "Wrong publisher or internal contract import.");
            if (version.Contains('-', StringComparison.Ordinal))
                Require(Text(policy, "channel") == "foundation-candidate" && Publishers.ContainsKey(id) && Text(entry, "previewAdmission") == "exact-foundation-candidate", "Preview dependency on stable core path.");
        }

        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Where(p => Path.GetFileName(p) == "packages.lock.json"))
        {
            using var locked = JsonDocument.Parse(Read(path));
            foreach (var framework in locked.RootElement.GetProperty("dependencies").EnumerateObject())
                foreach (var package in framework.Value.EnumerateObject())
                {
                    if (Text(package.Value, "type") == "Project") continue;
                    var identity = package.Name + "/" + Text(package.Value, "resolved");
                    Require(admitted.TryGetValue(identity, out var admission), "Unadmitted dependency: " + identity);
                    Require(Text(package.Value, "contentHash") == Text(admission, "contentHash"), "Mutable version: locked package bytes changed.");
                    observed.Add(identity);
                    if (package.Value.TryGetProperty("dependencies", out var edges))
                        foreach (var edge in edges.EnumerateObject())
                            Require(framework.Value.EnumerateObject().Any(p => p.Name.Equals(edge.Name, StringComparison.OrdinalIgnoreCase)) ||
                                framework.Name.Contains('/', StringComparison.Ordinal) &&
                                locked.RootElement.GetProperty("dependencies").GetProperty(framework.Name.Split('/')[0]).EnumerateObject()
                                    .Any(p => p.Name.Equals(edge.Name, StringComparison.OrdinalIgnoreCase)), "Incomplete transitive closure.");
                }
        }
        Require(observed.Count > 0 && observed.SetEquals(admitted.Keys), "Dependency closure drift.");
        foreach (var path in files.Where(p => Path.GetExtension(p) is ".csproj" or ".props" or ".targets"))
            foreach (var element in XDocument.Parse(Read(path)).Descendants())
            {
                if (element.Name.LocalName == "PackageVersion")
                {
                    var id = (string?)element.Attribute("Include") ?? "";
                    var version = (string?)element.Attribute("Version") ?? "";
                    Require(ExactVersion().IsMatch(version) && admitted.ContainsKey(id + "/" + version), "Floating or unadmitted central dependency.");
                }
                if (element.Name.LocalName == "PackageReference")
                    Require(element.Attribute("Version") is null && element.Attribute("VersionOverride") is null && !element.Elements().Any(e => e.Name.LocalName is "Version" or "VersionOverride"), "Noncentral dependency override.");
                Require(element.Name.LocalName is not "RestoreSources" and not "RestoreAdditionalProjectSources", "Untrusted restore source override.");
            }
        var config = XDocument.Parse(Read("NuGet.Config"));
        var sources = config.Descendants("packageSources").Elements("add").ToArray();
        Require(sources.Length == 1 && (string?)sources[0].Attribute("key") == "nuget.org" && (string?)sources[0].Attribute("value") == Feed, "Wrong package publisher feed.");
        Require(!files.Any(p => Path.GetFileName(p).Equals("nuget.config", StringComparison.OrdinalIgnoreCase) && p != "NuGet.Config"), "Unreviewed nested feed configuration.");
        foreach (var path in files.Where(p => p.StartsWith(".github/workflows/", StringComparison.Ordinal) && Path.GetExtension(p) is ".yml" or ".yaml"))
            foreach (Match action in ActionReference().Matches(Read(path)))
                Require(action.Groups[1].Value.StartsWith("./", StringComparison.Ordinal) || ImmutableAction().IsMatch(action.Groups[1].Value), "Floating workflow action tag.");

        using var reviewDocument = JsonDocument.Parse(Read(Text(policy, "reviewRecord")));
        var review = reviewDocument.RootElement;
        Require(Text(review, "owner") == "ArcScope" && Text(review, "decision") == "approved" && Sha40().IsMatch(Text(review, "baselineCommit")), "Missing upgrade review.");
        Require(DateOnly.TryParseExact(Text(review, "reviewedOn"), "yyyy-MM-dd", out _), "Invalid review date.");
        var requiredInputs = files.Where(IsDependencyInput).Append("eng/policy/dependency-policy.json").Order(StringComparer.Ordinal).ToArray();
        var reviewedInputs = review.GetProperty("inputs").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Require(requiredInputs.SequenceEqual(reviewedInputs), "Missing dependency input review.");
        foreach (var path in requiredInputs)
            Require(HashText(Read(path)) == Text(review.GetProperty("inputs"), path), "Dependency inputs changed without matching upgrade evidence: " + path);
        foreach (var check in new[] { "compilation", "aot", "compatibility", "licence", "security", "sbom", "localRuntime", "performance", "migration" })
            Require(Text(review.GetProperty("checks"), check).Length >= 20, "Missing upgrade evidence: " + check);
        var frameworkVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        using var sdk = JsonDocument.Parse(Read("global.json"));
        frameworkVersions.Add("dotnet", Text(sdk.RootElement.GetProperty("sdk"), "version"));
        frameworkVersions.Add("avalonia", admitted.Values.First(p => Text(p, "id") == "Avalonia").GetProperty("version").GetString()!);
        foreach (var framework in frameworkVersions)
        {
            var baseline = Text(review.GetProperty("baselineFrameworkVersions"), framework.Key);
            Require(ExactVersion().IsMatch(baseline), "Missing framework baseline.");
            if (baseline.Split('.')[0] != framework.Value.Split('.')[0])
                Require(Text(review, "runtimePostureAssessment").Length >= 40 && review.GetProperty("frameworkMajorUpgrade").GetBoolean(), "Framework major upgrade requires runtime posture evidence.");
        }
    }

    public static bool IsDependencyInput(string path) => Path.GetFileName(path) is "packages.lock.json" or "global.json" or "NuGet.Config" ||
        Path.GetExtension(path) is ".csproj" or ".props" or ".targets" || path is "third-party/sources.json" or "eng/policy/reuse-policy.json" or "eng/policy/licence-boundary.json" ||
        path.StartsWith(".github/workflows/", StringComparison.Ordinal) && Path.GetExtension(path) is ".yml" or ".yaml";

    public static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString() ?? "";
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:\.\d+)*(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$")]
    private static partial Regex ExactVersion();
    [GeneratedRegex("^[a-f0-9]{40}$")]
    private static partial Regex Sha40();
    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256();
    [GeneratedRegex(@"(?m)^\s*(?:-\s*)?uses:\s*([^\s#]+)")]
    private static partial Regex ActionReference();
    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[a-f0-9]{40}$")]
    private static partial Regex ImmutableAction();
}
