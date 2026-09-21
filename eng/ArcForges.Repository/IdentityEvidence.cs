// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ArcForges.ArcScope.Core;

namespace ArcForges.Repository;

public static class IdentityEvidence
{
    public static string Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new InvalidOperationException("Git identity lookup timed out."); }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().Trim();
    }

    public static JsonObject ExpectedBuild(string root, string commit, bool dirty = false)
    {
        if (Git(root, "rev-parse", "HEAD") != commit) throw new InvalidOperationException("Source checkout does not match candidate.");
        var ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        var run = ci ? Environment.GetEnvironmentVariable("GITHUB_RUN_ID") : null;
        var attempt = ci ? int.Parse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")!, CultureInfo.InvariantCulture) : (int?)null;
        if (ci && (Environment.GetEnvironmentVariable("GITHUB_SHA") != commit || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "ArcForges/ArcScope"))
            throw new InvalidOperationException("CI identity differs from source owner.");
        var result = new JsonObject
        {
            ["sourceCommit"] = commit,
            ["dirty"] = dirty,
            ["kind"] = ci ? "ci" : "local",
            ["buildId"] = ci ? run + "." + attempt!.Value.ToString(CultureInfo.InvariantCulture) : "local." + commit,
            ["runId"] = run,
            ["runAttempt"] = attempt,
            ["pipelineRun"] = ci ? "https://github.com/ArcForges/ArcScope/actions/runs/" + run : null,
            ["sourceDateEpoch"] = long.Parse(Git(root, "show", "-s", "--format=%ct", commit), CultureInfo.InvariantCulture)
        };
        BuildIdentity.ValidateBuild(result);
        return result;
    }

    public static JsonObject ExpectedReport(string root, string version, string commit)
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/version-sources.json")))!.AsObject();
        var pins = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var contractsVersion = pins.Descendants("PackageVersion").Single(e => (string?)e.Attribute("Include") == "ArcForges.Contracts.PublicApi").Attribute("Version")!.Value;
        var assets = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/ArcForges.Repository/obj/project.assets.json")))!;
        var contractsPath = assets["packageFolders"]!.AsObject().Select(p => Path.Combine(p.Key, "arcforges.contracts.publicapi", contractsVersion, "source.json")).Single(File.Exists);
        var contracts = File.ReadAllText(contractsPath);
        if (JsonNode.Parse(contracts)!["version"]!.GetValue<string>() != contractsVersion) throw new InvalidOperationException("Restored Contracts identity differs from central pin.");
        string Read(string path) => path switch
        {
            "assembly/release.json" => new JsonObject { ["versions"] = new JsonArray(new JsonObject { ["subject"] = "ArcScope", ["version"] = version }) }.ToJsonString(),
            "packages/contracts/source.json" => contracts,
            _ => File.ReadAllText(Path.Combine(root, path))
        };
        return new JsonObject
        {
            ["schema"] = "arcforges.build-identity.v1",
            ["owner"] = "ArcScope",
            ["artifact"] = new JsonObject { ["id"] = "ArcScope", ["version"] = version },
            ["build"] = ExpectedBuild(root, commit),
            ["axes"] = BuildIdentity.Resolve(catalog, Read)
        };
    }

    public static void Verify(byte[] actual, string root, string version, string commit)
    {
        var report = JsonNode.Parse(actual)!.AsObject();
        BuildIdentity.ValidateBuild(report["build"]!.AsObject());
        if (!JsonNode.DeepEquals(report, ExpectedReport(root, version, commit)))
            throw new InvalidOperationException("Runtime build identity or independent version sources differ from expected candidate.");
    }

    public static void VerifyAssemblies(string root)
    {
        var commit = Git(root, "rev-parse", "HEAD");
        var build = ExpectedBuild(root, commit, Git(root, "status", "--porcelain").Length != 0);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ArcForges.SourceCommit"] = commit,
            ["ArcForges.Dirty"] = build["dirty"]!.GetValue<bool>() ? "true" : "false",
            ["ArcForges.BuildId"] = build["buildId"]!.GetValue<string>(),
            ["ArcForges.BuildKind"] = build["kind"]!.GetValue<string>(),
            ["ArcForges.PipelineRun"] = build["pipelineRun"]?.GetValue<string>() ?? "local",
            ["ArcForges.SourceDateEpoch"] = build["sourceDateEpoch"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture)
        };
        string[] paths = ["src/ArcForges.ArcScope/bin/Release/net10.0/ArcScope.dll", "src/ArcForges.ArcScope.Core/bin/Release/net10.0/ArcForges.ArcScope.Core.dll",
            "eng/ArcForges.Repository/bin/Release/net10.0/ArcForges.Repository.dll", "tests/ArcForges.ArcScope.Tests/bin/Release/net10.0/ArcForges.ArcScope.Tests.dll"];
        foreach (var path in paths)
        {
            using var file = File.OpenRead(Path.Combine(root, path));
            using var pe = new PEReader(file);
            var reader = pe.GetMetadataReader();
            var found = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) continue;
                var type = reader.GetTypeReference((TypeReferenceHandle)parent);
                if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) throw new InvalidOperationException("Malformed assembly attribute.");
                found.Add(blob.ReadSerializedString()!, blob.ReadSerializedString());
            }
            foreach (var entry in expected)
                if (found.GetValueOrDefault(entry.Key) != entry.Value) throw new InvalidOperationException("Compiled assembly identity differs: " + path + " / " + entry.Key);
        }
        Console.WriteLine("Verified actual PE build identity of all four owned assemblies.");
    }
}
