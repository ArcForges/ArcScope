// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArcForges.ArcScope.Core;

/// <summary>Offline support information from compiled metadata and embedded, source-bound inputs.</summary>
public static partial class BuildIdentity
{
    public static readonly string[] AxisNames = ["AppVersion", "ContractSet", "CapabilityVersion", "NativeFormatVersion",
        "StorageSchemaVersion", "NativeAbiVersion", "PolicySchemaVersion", "ExtensionProtocolVersion", "PackageVersion"];
    private static readonly string[] Kinds = ["release", "contracts", "declarations", "declarations", "migrations",
        "native-abi", "declarations", "declarations", "packages"];

    public static JsonObject FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        string Field(string key) => metadata["ArcForges." + key] ?? throw new InvalidOperationException("Missing compiled build identity.");
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var kind = Field("BuildKind");
        var id = Field("BuildId");
        var build = new JsonObject
        {
            ["sourceCommit"] = Field("SourceCommit"),
            ["dirty"] = bool.Parse(Field("Dirty")),
            ["kind"] = kind,
            ["buildId"] = id,
            ["runId"] = kind == "ci" ? id.Split('.')[0] : null,
            ["runAttempt"] = kind == "ci" ? int.Parse(id.Split('.')[1], CultureInfo.InvariantCulture) : (int?)null,
            ["pipelineRun"] = kind == "ci" ? Field("PipelineRun") : null,
            ["sourceDateEpoch"] = long.Parse(Field("SourceDateEpoch"), CultureInfo.InvariantCulture)
        };
        byte[] ResourceBytes(string name)
        {
            using var stream = assembly.GetManifestResourceStream("ArcScope." + name) ?? throw new InvalidOperationException("Missing embedded producer evidence.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        string Resource(string name)
        {
            using var stream = assembly.GetManifestResourceStream("ArcScope." + name) ?? throw new InvalidOperationException("Missing embedded source: " + name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["assembly/release.json"] = new JsonObject { ["versions"] = new JsonArray(new JsonObject { ["subject"] = "ArcScope", ["version"] = version }) }.ToJsonString(),
            ["packages/contracts/source.json"] = Resource("ContractsSource"),
            ["packages/contracts/build-identity.json"] = Resource("ContractsIdentity"),
            ["packages/contracts/descriptor.base64"] = Convert.ToBase64String(ResourceBytes("ContractsDescriptor")),
            ["src/ArcForges.ArcScope/packages.lock.json"] = Resource("PackageLock")
        };
        ValidateBuild(build);
        return new JsonObject
        {
            ["schema"] = "arcforges.build-identity.v1",
            ["owner"] = "ArcScope",
            ["artifact"] = new JsonObject { ["id"] = "ArcScope", ["version"] = version },
            ["build"] = build,
            ["axes"] = Resolve(JsonNode.Parse(Resource("VersionSources"))!.AsObject(), path => path.StartsWith("packages/contracts/schema/", StringComparison.Ordinal)
                ? Resource("ContractsSchema." + path["packages/contracts/schema/".Length..]) : inputs[path])
        };
    }

    public static JsonObject Resolve(JsonObject catalog, Func<string, string> read)
    {
        Require(catalog["schemaVersion"]!.GetValue<int>() == 1 && catalog["owner"]!.GetValue<string>() == "ArcScope", "Wrong source catalog.");
        var definitions = catalog["axes"]!.AsObject();
        Require(definitions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(AxisNames), "Exactly nine independent axes are required.");
        var result = new JsonObject();
        for (var index = 0; index < AxisNames.Length; index++)
        {
            var name = AxisNames[index];
            var definition = definitions[name]!.AsObject();
            var kind = definition["kind"]!.GetValue<string>();
            Require(kind == Kinds[index], "Wrong source kind for " + name);
            if (definition["absence"] is { } absence)
            {
                Require(definition.All(p => p.Key is "kind" or "absence" or "reason" or "producer"), "Unknown absent-axis field.");
                var status = absence.GetValue<string>();
                Require(status is "not-applicable" or "not-produced" && !string.IsNullOrWhiteSpace(definition["reason"]?.GetValue<string>()), "Invalid absence.");
                Require(status != "not-produced" || !string.IsNullOrWhiteSpace(definition["producer"]?.GetValue<string>()), "Missing future producer.");
                var absent = new JsonObject { ["status"] = status, ["reason"] = definition["reason"]!.DeepClone() };
                if (definition["producer"] is { } producer) absent["producer"] = producer.DeepClone();
                result[name] = absent;
                continue;
            }
            Require(definition.All(p => p.Key is "kind" or "sources" || p.Key == "producerReport" && name == "ContractSet" && kind == "contracts"), "Aliases and unknown source properties are forbidden.");
            var values = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in definition["sources"]!.AsArray())
            {
                var path = item!.GetValue<string>();
                Require(!Path.IsPathRooted(path) && path.IndexOfAny(['\\', ':']) < 0 && !path.Split('/').Any(p => p is "" or "." or ".."), "Unsafe source path.");
                var content = read(path).Replace("\r\n", "\n", StringComparison.Ordinal);
                var source = new JsonObject { ["path"] = path, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))) };
                void Add(string subject, string version, JsonNode? descriptor = null)
                {
                    Require(!string.IsNullOrWhiteSpace(subject) && VersionPattern().IsMatch(version), "Malformed subject or version.");
                    var value = new JsonObject { ["subject"] = subject, ["version"] = version, ["source"] = source.DeepClone() };
                    if (descriptor is not null) value["descriptorSha256"] = descriptor.DeepClone();
                    Require(values.TryAdd(subject, value), "Duplicate version subject.");
                }
                if (kind == "native-abi")
                {
                    var major = AbiMajor().Match(content);
                    var minor = AbiMinor().Match(content);
                    Require(major.Success && minor.Success, "Missing native ABI constants.");
                    Add(path, major.Groups[1].Value + "." + minor.Groups[1].Value);
                    continue;
                }
                var document = JsonNode.Parse(content)!.AsObject();
                if (kind == "contracts")
                {
                    if (document["schema"] is { } legacySchema)
                    {
                        var schema = ContractPattern().Match(legacySchema.GetValue<string>());
                        Require(schema.Success && HashPattern().IsMatch(document["descriptorSha256"]!.GetValue<string>()) && !document["dirty"]!.GetValue<bool>(), "Invalid restored contract provenance.");
                        Add(schema.Groups[1].Value, schema.Groups[2].Value, document["descriptorSha256"]);
                    }
                    else
                    {
                        string reportPath = definition["producerReport"]?.GetValue<string>()
                            ?? throw new InvalidOperationException("A complete Contracts producer report is required.");
                        JsonArray contracts;
                        try { contracts = VerifiedContractValues(document, reportPath, read); }
                        catch (JsonException) { throw new InvalidOperationException("Malformed Contracts producer evidence."); }
                        catch (FormatException) { throw new InvalidOperationException("Malformed Contracts producer evidence."); }
                        foreach (var contract in contracts)
                        {
                            var value = contract!.AsObject();
                            Require(values.TryAdd(value["subject"]!.GetValue<string>(), (JsonObject)value.DeepClone()), "Duplicate version subject.");
                        }
                    }
                }
                else if (kind == "packages")
                {
                    foreach (var framework in document["dependencies"]!.AsObject())
                        foreach (var dependency in framework.Value!.AsObject())
                        {
                            if (dependency.Value!["type"]!.GetValue<string>() == "Project") continue;
                            Add("pkg:nuget/" + dependency.Key + "?target=" + framework.Key, dependency.Value["resolved"]!.GetValue<string>());
                        }
                }
                else
                {
                    var declarations = document[kind == "migrations" ? "migrations" : "versions"]!.AsArray();
                    var selected = kind == "migrations" ? declarations.TakeLast(1) : declarations;
                    foreach (var value in selected) Add(value!["subject"]!.GetValue<string>(), value["version"]!.GetValue<string>());
                }
            }
            Require(kind == "packages" || values.Count > 0, "No implemented source for " + name);
            result[name] = new JsonObject { ["status"] = "present", ["values"] = new JsonArray(values.Values.Select(v => (JsonNode)v).ToArray()) };
        }
        return result;
    }

    private static JsonArray VerifiedContractValues(JsonObject provenance, string reportPath, Func<string, string> read)
    {
        const int maximumBody = 1024 * 1024;
        const int maximumTotal = 4 * maximumBody;
        Require(reportPath == "packages/contracts/build-identity.json", "Unknown Contracts producer-report source.");
        Require(provenance.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(
            ["repository", "commit", "dirty", "version", "contractAccess", "schemaSources", "descriptorSha256", "dependencyLocks"]),
            "Unknown Contracts provenance profile.");
        Require(provenance["repository"]?.GetValue<string>() == "https://github.com/ArcForges/Contracts"
            && provenance["contractAccess"]?.GetValue<string>() == "public"
            && provenance["dirty"]?.GetValue<bool>() == false
            && CommitPattern().IsMatch(provenance["commit"]?.GetValue<string>() ?? "")
            && VersionPattern().IsMatch(provenance["version"]?.GetValue<string>() ?? "")
            && HashPattern().IsMatch(provenance["descriptorSha256"]?.GetValue<string>() ?? ""), "Invalid Contracts producer provenance.");
        string reportBody = read(reportPath);
        Require(Encoding.UTF8.GetByteCount(reportBody) <= 65536, "Oversized Contracts producer report.");
        var report = JsonNode.Parse(reportBody)?.AsObject() ?? throw new InvalidOperationException("Missing Contracts producer report.");
        Require(report.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["schema", "owner", "artifact", "build", "axes"])
            && report["schema"]?.GetValue<string>() == "arcforges.build-identity.v1" && report["owner"]?.GetValue<string>() == "Contracts",
            "Invalid Contracts producer report owner/profile.");
        Require(report["artifact"] is JsonObject, "Missing Contracts producer artifact.");
        var artifact = report["artifact"]!.AsObject();
        Require(artifact.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["id", "version"])
            && artifact["id"]?.GetValue<string>() == "ArcForges.Contracts.PublicApi"
            && artifact["version"]?.GetValue<string>() == provenance["version"]!.GetValue<string>(), "Wrong Contracts producer artifact.");
        Require(report["build"] is JsonObject, "Missing Contracts producer build identity.");
        var producerBuild = report["build"]!.AsObject();
        Require(producerBuild.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(
            ["sourceCommit", "dirty", "kind", "buildId", "runId", "runAttempt", "pipelineRun", "sourceDateEpoch"]), "Unknown Contracts producer build profile.");
        Require(producerBuild["sourceCommit"]?.GetValue<string>() == provenance["commit"]!.GetValue<string>()
            && producerBuild["dirty"]?.GetValue<bool>() == false, "Contracts producer source identity differs.");
        Require(report["axes"] is JsonObject, "Missing Contracts producer axes.");
        Require(report["axes"]!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(AxisNames), "Incomplete Contracts producer axes.");
        Require(report["axes"]!["ContractSet"] is JsonObject, "Missing Contracts producer schema axis.");
        var declared = report["axes"]!["ContractSet"]!.AsObject();
        Require(declared.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["status", "values"])
            && declared["status"]?.GetValue<string>() == "present", "Missing Contracts schema identities.");
        Require(provenance["schemaSources"] is JsonObject, "Missing Contracts source inventory.");
        var schemaSources = provenance["schemaSources"]!.AsObject();
        Require(schemaSources.Count is > 0 and <= 200, "Invalid Contracts source count.");
        string descriptorText = read("packages/contracts/descriptor.base64");
        Require(descriptorText.Length <= ((maximumTotal + 2) / 3) * 4, "Oversized Contracts descriptor.");
        byte[] descriptor = Convert.FromBase64String(descriptorText);
        Require(descriptor.Length is > 0 and <= maximumTotal && Convert.ToBase64String(descriptor) == descriptorText
            && Convert.ToHexStringLower(SHA256.HashData(descriptor)) == provenance["descriptorSha256"]!.GetValue<string>(), "Contracts descriptor differs from provenance.");
        var values = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var sourcesBySubject = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        int total = 0;
        foreach (var pair in schemaSources.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var path = pair.Key;
            Require(path.StartsWith("public/", StringComparison.Ordinal) && !Path.IsPathRooted(path)
                && path.IndexOfAny(['\\', ':']) < 0 && !path.Split('/').Any(p => p is "" or "." or "..")
                && HashPattern().IsMatch(pair.Value?.GetValue<string>() ?? ""), "Unsafe or incomplete Contracts schema source.");
            string text = read("packages/contracts/schema/" + path);
            int length = Encoding.UTF8.GetByteCount(text);
            Require(length <= maximumBody && total <= maximumTotal - length, "Oversized Contracts schema closure.");
            total += length;
            Require(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) == pair.Value!.GetValue<string>(), "Contracts schema bytes differ from provenance.");
            var evidence = new JsonObject { ["path"] = path, ["sha256"] = pair.Value.DeepClone() };
            string subject;
            string version;
            bool proto = path.EndsWith(".proto", StringComparison.Ordinal);
            if (proto)
            {
                var declarations = ProtoPackagePattern().Matches(text);
                Require(declarations.Count == 1, "Missing or ambiguous authored protobuf identity.");
                subject = declarations[0].Groups[1].Value;
                version = declarations[0].Groups[2].Value;
            }
            else
            {
                Require(path.StartsWith("public/http/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal), "Unknown Contracts schema kind.");
                var schema = JsonNode.Parse(text)!.AsObject();
                string title = schema["title"]?.GetValue<string>() ?? "";
                version = schema["x-arcforges-schema-version"]?.GetValue<string>() ?? "";
                Require(title.Length is > 0 and <= 128 && !title.Any(char.IsControl) && RunPattern().IsMatch(version), "Missing authored JSON schema identity.");
                subject = "json:" + title;
            }

            if (values.TryGetValue(subject, out var prior))
            {
                Require(proto && prior["version"]!.GetValue<string>() == version && prior["descriptorSha256"] is not null,
                    "Conflicting authored Contracts identity.");
                sourcesBySubject[subject].Add(evidence);
            }
            else
            {
                var value = new JsonObject { ["subject"] = subject, ["version"] = version };
                if (proto) value["descriptorSha256"] = provenance["descriptorSha256"]!.DeepClone();
                values.Add(subject, value);
                sourcesBySubject.Add(subject, [evidence]);
            }
        }

        foreach (var pair in values)
        {
            var sources = sourcesBySubject[pair.Key];
            if (sources.Count == 1) pair.Value["source"] = sources[0];
            else pair.Value["sources"] = new JsonArray(sources.Select(p => (JsonNode)p).ToArray());
        }
        var recomputed = new JsonArray(values.Values.Select(p => (JsonNode)p).ToArray());
        Require(JsonNode.DeepEquals(recomputed, declared["values"]), "Contracts producer semantic identities differ from actual authored sources.");
        return recomputed;
    }

    [GeneratedRegex(@"^package\s+([a-zA-Z0-9_.]+)\.v([1-9][0-9]*);\s*$", RegexOptions.Multiline)]
    private static partial Regex ProtoPackagePattern();

    public static void ValidateBuild(JsonObject build)
    {
        Require(build.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["sourceCommit", "dirty", "kind", "buildId", "runId", "runAttempt", "pipelineRun", "sourceDateEpoch"]), "Unknown or missing build fields.");
        var commit = build["sourceCommit"]!.GetValue<string>();
        Require(CommitPattern().IsMatch(commit) && build["sourceDateEpoch"]!.GetValue<long>() > 0, "Invalid source identity.");
        var dirty = build["dirty"]!.GetValue<bool>();
        var id = build["buildId"]!.GetValue<string>();
        var kind = build["kind"]!.GetValue<string>();
        if (kind == "ci")
        {
            var run = build["runId"]!.GetValue<string>();
            var attempt = build["runAttempt"]!.GetValue<int>();
            Require(!dirty && RunPattern().IsMatch(run) && attempt > 0 && id == run + "." + attempt.ToString(CultureInfo.InvariantCulture)
                && build["pipelineRun"]!.GetValue<string>() == "https://github.com/ArcForges/ArcScope/actions/runs/" + run, "Invalid CI identity.");
        }
        else Require(kind == "local" && id == "local." + commit && build["runId"] is null && build["runAttempt"] is null && build["pipelineRun"] is null, "Invalid local identity.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)*(?:[-+][A-Za-z0-9.-]+)?$")]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"^([a-zA-Z0-9_.]+)\.v([1-9][0-9]*)$")]
    private static partial Regex ContractPattern();
    [GeneratedRegex(@"^[a-f0-9]{64}$")]
    private static partial Regex HashPattern();
    [GeneratedRegex(@"^[a-f0-9]{40}$")]
    private static partial Regex CommitPattern();
    [GeneratedRegex(@"^[1-9][0-9]*$")]
    private static partial Regex RunPattern();
    [GeneratedRegex(@"#define\s+ARC_ABI_MAJOR\s+(\d+)")]
    private static partial Regex AbiMajor();
    [GeneratedRegex(@"#define\s+ARC_ABI_MINOR\s+(\d+)")]
    private static partial Regex AbiMinor();
}
