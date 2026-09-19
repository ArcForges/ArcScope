// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from ArcForges Contracts eng/check_provenance.py (Apache-2.0).
// C# rewrite and integration changes: ArcForges. See eng/third-party/LICENSE.Contracts.txt.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcForges.Repository;

public static class ProvenancePolicy
{
    public const string Owner = "ArcScope";
    public const string Inventory = "eng/provenance/files.json";
    public const string Summary = "eng/provenance/NOTICE.txt";
    public const string Policy = "eng/policy/reuse-policy.json";
    private const string Store = "eng/provenance/records/";
    private const string RecordFields = "schemaVersion id kind sourceRepository sourceCommit sourcePaths licence attribution targets artifactTargets disposition verification notice lifetime generation review supersedes";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Dictionary<string, string> Licences = new(StringComparer.Ordinal)
    {
        ["Apache-2.0"] = "permissive",
        ["MIT"] = "permissive",
        ["BSD-2-Clause"] = "permissive",
        ["BSD-3-Clause"] = "permissive",
        ["ISC"] = "permissive",
        ["Apache-2.0 WITH LLVM-exception"] = "permissive",
        ["Apache-2.0 AND BSD-3-Clause"] = "permissive",
        ["Apache-2.0 AND MIT"] = "permissive",
        ["AGPL-3.0-only"] = "agpl-compatible",
        ["GPL-2.0-only"] = "gpl-only",
        ["GPL-3.0-only"] = "gpl-only",
        ["NOASSERTION"] = "unclear",
        ["EPL-1.0"] = "incompatible"
    };

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static string Text(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()), "Blank or non-string provenance field.");
        return value.GetString()!;
    }

    private static void Fields(JsonElement value, string fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected provenance object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        Require(names.Length == fields.Split(' ').Length && names.ToHashSet(StringComparer.Ordinal).SetEquals(fields.Split(' ')), "Missing, duplicate or unknown provenance fields: " + fields);
    }

    private static JsonElement Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(Utf8.GetString(bytes));
        static void Unique(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in item.EnumerateObject())
                {
                    Require(names.Add(field.Name), "Duplicate JSON field: " + field.Name);
                    Unique(field.Value);
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
                foreach (var entry in item.EnumerateArray()) Unique(entry);
        }
        Unique(document.RootElement);
        return document.RootElement.Clone();
    }

    private static string Relative(string value)
    {
        Require(!string.IsNullOrWhiteSpace(value) && !value.StartsWith('/') && value.IndexOfAny(['\\', ':', '*', '?', '\0']) < 0 &&
            value.Split('/').All(p => p is not ("" or "." or "..")), "Nonliteral or escaping provenance path: " + value);
        return value;
    }

    public static byte[] Read(string root, string relative)
    {
        root = Path.GetFullPath(root);
        var target = Path.Combine(root, Relative(relative));
        for (var current = target; current != root; current = Path.GetDirectoryName(current)!)
        {
            Require(File.Exists(current) || Directory.Exists(current), "Missing provenance input: " + relative);
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked provenance input: " + relative);
        }
        Require(File.Exists(target), "Provenance input is not a file: " + relative);
        return File.ReadAllBytes(target);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Lf(byte[] bytes) => Utf8.GetBytes(Utf8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
    private static void Digest(string value, bool commit = false) => Require((value.Length == 64 || commit && value.Length == 40) && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Expected full immutable hash.");
    private static void Id(string value) => Require(Regex.IsMatch(value, @"\A[a-z0-9]+(?:-[a-z0-9]+)*-r[1-9][0-9]*\z", RegexOptions.CultureInvariant), "Invalid provenance record ID.");
    private static void Repository(string value) => Require(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.Trim('/').Length > 0 && !value.EndsWith('/') && value.Split('/').All(p => p is not ("." or "..")), "Expected canonical HTTPS source repository.");
    private static JsonElement[] Array(JsonElement value, bool empty = false)
    {
        Require(value.ValueKind == JsonValueKind.Array, "Expected provenance array.");
        var result = value.EnumerateArray().ToArray();
        Require(empty || result.Length > 0, "Empty required provenance array.");
        return result;
    }
    private static string[] Strings(JsonElement value, Action<string>? validate = null, bool empty = false)
    {
        var result = Array(value, empty).Select(Text).ToArray();
        foreach (var item in result) validate?.Invoke(item);
        Require(result.Distinct(StringComparer.Ordinal).Count() == result.Length, "Duplicate provenance array entry.");
        return result;
    }
    private static void Paths(JsonElement value, bool empty = false) => Strings(value, p => Relative(p), empty);
    private static void Evidence(JsonElement value)
    {
        foreach (var item in Array(value))
        {
            Fields(item, "path sha256 finding");
            Relative(Text(item.GetProperty("path")));
            Digest(Text(item.GetProperty("sha256")));
            Text(item.GetProperty("finding"));
        }
    }
    private static string Category(string expression)
    {
        Require(Licences.ContainsKey(expression), "Unknown provenance SPDX expression.");
        return Licences[expression];
    }
    private static void Compatible(string expression) => Require(Category(expression) is "permissive" or "agpl-compatible", "Prohibited implementation reuse.");
    private static void Source(JsonElement item)
    {
        Fields(item, "repository commit paths spdx evidence");
        Repository(Text(item.GetProperty("repository")));
        Digest(Text(item.GetProperty("commit")), true);
        Paths(item.GetProperty("paths"));
        Compatible(Text(item.GetProperty("spdx")));
        Evidence(item.GetProperty("evidence"));
    }
    private static void CheckPolicy(JsonElement value)
    {
        Fields(value, "schemaVersion repository licenceBoundary authority decisions licences");
        Require(value.GetProperty("schemaVersion").GetRawText() == "1" && Text(value.GetProperty("repository")) == Owner && Text(value.GetProperty("licenceBoundary")) == "AGPL", "Incorrect provenance owner.");
        var authority = value.GetProperty("authority");
        Fields(authority, "repository commit path");
        Require(Text(authority.GetProperty("repository")) == "https://github.com/ArcForges/ArcForges-Design" && Text(authority.GetProperty("path")) == "docs/assurance/reference-coverage-and-provenance.md", "Incorrect provenance authority.");
        Digest(Text(authority.GetProperty("commit")), true);
        var decisions = value.GetProperty("decisions");
        Fields(decisions, "permissive agpl-compatible gpl-only unclear incompatible");
        foreach (var row in decisions.EnumerateObject())
        {
            Fields(row.Value, "AGPL Apache");
            Require(Text(row.Value.GetProperty("AGPL")) == (row.Name == "permissive" ? "audit" : row.Name == "agpl-compatible" ? "exact-review" : "prohibited") &&
                Text(row.Value.GetProperty("Apache")) == (row.Name == "permissive" ? "audit" : "prohibited"), "Changed closed licence decision table.");
        }
        var licences = value.GetProperty("licences");
        Require(licences.ValueKind == JsonValueKind.Object && licences.EnumerateObject().Count() == Licences.Count, "Changed closed licence expressions.");
        foreach (var pair in licences.EnumerateObject()) Require(Category(pair.Name) == Text(pair.Value), "Changed licence category.");
    }
    private static void Record(JsonElement item)
    {
        Fields(item, RecordFields);
        Require(item.GetProperty("schemaVersion").GetRawText() == "1", "Incorrect record schema.");
        var id = Text(item.GetProperty("id"));
        Id(id);
        var kind = Text(item.GetProperty("kind"));
        Require(kind is "source" or "patch" or "generated" or "legal-text", "Unknown material kind.");
        Repository(Text(item.GetProperty("sourceRepository")));
        Digest(Text(item.GetProperty("sourceCommit")), true);
        Paths(item.GetProperty("sourcePaths"));
        var licence = item.GetProperty("licence");
        Fields(licence, "spdx category evidence scope copyingPermission");
        var expression = Text(licence.GetProperty("spdx"));
        Require(Category(expression) == Text(licence.GetProperty("category")), "Mismatched licence category.");
        Evidence(licence.GetProperty("evidence"));
        Text(licence.GetProperty("scope"));
        if (kind == "legal-text")
        {
            Text(licence.GetProperty("copyingPermission"));
            Require(expression != "NOASSERTION", "Unknown-origin legal text.");
        }
        else
        {
            Require(licence.GetProperty("copyingPermission").ValueKind == JsonValueKind.Null, "Legal-document permission used for implementation.");
            Compatible(expression);
        }
        Strings(item.GetProperty("attribution"));
        var disposition = Text(item.GetProperty("disposition"));
        Require(disposition is "Copy" or "Rewrite" or "Improve" or "Replace" or "Reference Only" or "Drop", "Unknown disposition.");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in Array(item.GetProperty("targets"), true))
        {
            Fields(target, "path sha256 normalization");
            var path = Relative(Text(target.GetProperty("path")));
            Require(targets.Add(path) && !path.StartsWith("eng/provenance/", StringComparison.Ordinal) && path != Policy, "Duplicate or self-attesting target.");
            Digest(Text(target.GetProperty("sha256")));
            Require(Text(target.GetProperty("normalization")) is "lf" or "raw", "Unknown target normalization.");
            if (kind == "legal-text")
            {
                var name = Path.GetFileName(path).ToLowerInvariant();
                Require(new[] { "license", "licence", "copying", "notice" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) || path.StartsWith("third-party/", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal), "Legal text targets implementation.");
            }
        }
        var artifactKeys = new HashSet<(string, string, string)>();
        foreach (var target in Array(item.GetProperty("artifactTargets"), true))
        {
            Fields(target, "project package kind profile sha256");
            Require(artifactKeys.Add((Text(target.GetProperty("project")), Text(target.GetProperty("package")), Text(target.GetProperty("kind")))), "Duplicate artifact target.");
            Require(Relative(Text(target.GetProperty("profile"))).StartsWith("eng/provenance/artifact-profiles/", StringComparison.Ordinal), "Artifact profile outside provenance store.");
            Digest(Text(target.GetProperty("sha256")));
        }
        Require(artifactKeys.Count == 0 || kind == "generated", "Artifact requires generator and input positions.");
        Require(disposition is "Reference Only" or "Drop" ? targets.Count + artifactKeys.Count == 0 : targets.Count + artifactKeys.Count > 0, "Disposition does not match targets.");
        var oracle = item.GetProperty("verification");
        Fields(oracle, "kind command expected artifacts");
        Require(Text(oracle.GetProperty("kind")) is "byte-match" or "regeneration" or "transformation", "Unknown verification oracle.");
        Text(oracle.GetProperty("command"));
        Text(oracle.GetProperty("expected"));
        foreach (var artifact in Array(oracle.GetProperty("artifacts"), true))
        {
            Fields(artifact, "url sha256 members");
            Repository(Text(artifact.GetProperty("url")));
            Digest(Text(artifact.GetProperty("sha256")));
            Paths(artifact.GetProperty("members"));
        }
        var notice = item.GetProperty("notice");
        Fields(notice, "required text files distribution reason");
        Require(notice.GetProperty("required").ValueKind is JsonValueKind.True or JsonValueKind.False, "NOTICE requirement must be boolean.");
        Text(notice.GetProperty("text"));
        Paths(notice.GetProperty("files"), !notice.GetProperty("required").GetBoolean());
        Require(Text(notice.GetProperty("distribution")) is "source" or "packages" or "documentation", "Unknown notice distribution.");
        Text(notice.GetProperty("reason"));
        var lifetime = item.GetProperty("lifetime");
        Fields(lifetime, "status owner removalTrigger");
        Text(lifetime.GetProperty("owner"));
        Require(Text(lifetime.GetProperty("status")) is "temporary" or "permanent", "Unknown lifetime.");
        if (Text(lifetime.GetProperty("status")) == "temporary") Text(lifetime.GetProperty("removalTrigger"));
        else Require(lifetime.GetProperty("removalTrigger").ValueKind == JsonValueKind.Null, "Permanent record has temporary removal trigger.");
        var generation = item.GetProperty("generation");
        if (kind == "generated")
        {
            Fields(generation, "generators inputs command outputSpdx");
            foreach (var role in new[] { "generators", "inputs" })
                foreach (var input in Array(generation.GetProperty(role))) Source(input);
            Text(generation.GetProperty("command"));
            Compatible(Text(generation.GetProperty("outputSpdx")));
        }
        else Require(generation.ValueKind == JsonValueKind.Null, "Non-generated record carries generation metadata.");
        var review = item.GetProperty("review");
        Fields(review, "owner reviewer reviewedOn decision rationale baselineCommit reconciliation");
        Require(Text(review.GetProperty("owner")) == "Licensing and Provenance Owner" && Text(review.GetProperty("decision")) == "approved", "Unapproved disposition.");
        Text(review.GetProperty("reviewer"));
        Text(review.GetProperty("rationale"));
        Require(DateOnly.TryParseExact(Text(review.GetProperty("reviewedOn")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "Invalid review date.");
        Digest(Text(review.GetProperty("baselineCommit")), true);
        Require(review.GetProperty("reconciliation").ValueKind is JsonValueKind.True or JsonValueKind.False, "Missing reconciliation classification.");
        if (item.GetProperty("supersedes").ValueKind != JsonValueKind.Null)
        {
            Id(Text(item.GetProperty("supersedes")));
            Require(Text(item.GetProperty("supersedes")) != id, "Self-superseding record.");
        }
    }

    private static string Notice(IReadOnlyDictionary<string, JsonElement> records, IEnumerable<string> active, bool packages = false)
    {
        var lines = new List<string> { "ArcForges source provenance notices", "", "Generated from reviewed active records. Original licence files and dependency notices remain authoritative.", "" };
        foreach (var id in active.Order(StringComparer.Ordinal))
        {
            var item = records[id];
            var notice = item.GetProperty("notice");
            if (packages && Text(notice.GetProperty("distribution")) != "packages") continue;
            lines.AddRange([id, $"Source: {Text(item.GetProperty("sourceRepository"))} @ {Text(item.GetProperty("sourceCommit"))}",
                "Material licence: " + Text(item.GetProperty("licence").GetProperty("spdx")), "Notice scope: " + Text(notice.GetProperty("distribution"))]);
            lines.AddRange(Strings(item.GetProperty("attribution")));
            lines.Add(Text(notice.GetProperty("text")));
            lines.Add("");
        }
        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    public static ProvenanceResult Validate(string root, IEnumerable<string> inventory, IReadOnlyDictionary<string, byte[]> history, JsonElement? oldInventory = null, bool writeNotice = false)
    {
        var files = inventory.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Require(files.Count == files.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Case-colliding inventory.");
        foreach (var file in files) Read(root, file);
        CheckPolicy(Parse(Read(root, Policy)));
        var template = Parse(Read(root, "eng/provenance/template.json"));
        Fields(template, "schemaVersion instructions example");
        Require(template.GetProperty("schemaVersion").GetRawText() == "1", "Invalid template schema.");
        Text(template.GetProperty("instructions"));
        Fields(template.GetProperty("example"), RecordFields);
        var inv = Parse(Read(root, Inventory));
        Fields(inv, "schemaVersion repository firstParty reused artifacts");
        Require(inv.GetProperty("schemaVersion").GetRawText() == "1" && Text(inv.GetProperty("repository")) == Owner, "Invalid inventory owner.");
        var authored = Strings(inv.GetProperty("firstParty"), p => Relative(p)).ToHashSet(StringComparer.Ordinal);
        var reused = inv.GetProperty("reused").EnumerateObject().ToDictionary(p => Relative(p.Name), p => Text(p.Value), StringComparer.Ordinal);
        foreach (var id in reused.Values) Id(id);
        var artifacts = Strings(inv.GetProperty("artifacts"), Id, true).ToHashSet(StringComparer.Ordinal);
        Require(!authored.Overlaps(reused.Keys) && files.SetEquals(authored.Concat(reused.Keys)), "Unclassified, conflicting or stale inventory files.");
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.StartsWith("eng/provenance/conflicts/", StringComparison.Ordinal))
            {
                var conflict = Parse(Read(root, file));
                Fields(conflict, "id material evidence boundary owner requiredDecision status resolution");
                foreach (var property in conflict.EnumerateObject()) Text(property.Value);
                Require(Text(conflict.GetProperty("status")) == "resolved", "Unresolved provenance conflict.");
            }
            if (!file.StartsWith(Store, StringComparison.Ordinal)) continue;
            var item = Parse(Read(root, file));
            Record(item);
            var id = Text(item.GetProperty("id"));
            Require(file == Store + id + ".json" && records.TryAdd(id, item), "Record path/ID mismatch or duplicate record.");
        }
        foreach (var original in history)
            Require(files.Contains(original.Key) && Lf(Read(root, original.Key)).SequenceEqual(Lf(original.Value)), "Used record changed or removed: " + original.Key);
        string? Parent(string id) => records[id].GetProperty("supersedes").ValueKind == JsonValueKind.Null ? null : Text(records[id].GetProperty("supersedes"));
        foreach (var id in records.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            for (var parent = Parent(id); parent is not null; parent = Parent(parent))
                Require(records.ContainsKey(parent) && seen.Add(parent), "Missing or cyclic superseded record.");
        }
        var active = reused.Values.Concat(artifacts).ToHashSet(StringComparer.Ordinal);
        Require(active.Count > 0, "At least one real record must be in use.");
        foreach (var id in active)
        {
            Require(records.ContainsKey(id), "Missing provenance record: " + id);
            var item = records[id];
            var targets = Array(item.GetProperty("targets"), true);
            Require(targets.Select(t => Text(t.GetProperty("path"))).ToHashSet(StringComparer.Ordinal).SetEquals(reused.Where(p => p.Value == id).Select(p => p.Key)), "Active record targets do not match complete inventory bindings.");
            var artifactTargets = Array(item.GetProperty("artifactTargets"), true);
            Require((artifactTargets.Length > 0) == artifacts.Contains(id), "Artifact record is not explicitly registered.");
            foreach (var target in artifactTargets)
            {
                var path = Text(target.GetProperty("profile"));
                Require(files.Contains(path) && Hash(Lf(Read(root, path))) == Text(target.GetProperty("sha256")), "Untracked or changed artifact profile.");
            }
            foreach (var target in targets)
            {
                var bytes = Read(root, Text(target.GetProperty("path")));
                if (Text(target.GetProperty("normalization")) == "lf") bytes = Lf(bytes);
                Require(Hash(bytes) == Text(target.GetProperty("sha256")), "Recorded target bytes changed: " + Text(target.GetProperty("path")));
            }
            foreach (var path in Strings(item.GetProperty("notice").GetProperty("files"), empty: true))
            {
                Require(files.Contains(path), "Untracked required notice: " + path);
                Read(root, path);
            }
        }
        bool Descends(string current, string old)
        {
            for (string? id = current; id is not null; id = Parent(id))
                if (id == old) return true;
            return false;
        }
        if (oldInventory is { } prior)
        {
            foreach (var old in prior.GetProperty("reused").EnumerateObject())
                if (files.Contains(old.Name)) Require(reused.TryGetValue(old.Name, out var current) && Descends(current, Text(old.Value)), "Retained external file lost provenance: " + old.Name);
            foreach (var oldId in Strings(prior.GetProperty("artifacts"), empty: true))
                foreach (var target in Array(Parse(history[Store + oldId + ".json"]).GetProperty("artifactTargets")))
                {
                    var replacements = artifacts.Where(id => Array(records[id].GetProperty("artifactTargets")).Any(row => new[] { "project", "package", "kind" }.All(key => Text(row.GetProperty(key)) == Text(target.GetProperty(key))))).ToArray();
                    Require(replacements.Length == 1 && Descends(replacements[0], oldId), "Active artifact silently removed or reclassified.");
                }
        }
        var expected = Utf8.GetBytes(Notice(records, active));
        if (writeNotice) File.WriteAllBytes(Path.Combine(root, Summary), expected);
        Require(Lf(Read(root, Summary)).SequenceEqual(expected), "Stale generated provenance NOTICE.");
        return new ProvenanceResult("passed", Owner, files.Count, reused.Count, records.Count, active.Order(StringComparer.Ordinal).ToArray(), Hash(expected));
    }

    private static async Task<string> Git(string root, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException("Provenance Git lookup timed out."); }
        var text = await output;
        var diagnostic = await error;
        Require(process.ExitCode == 0, "Provenance history unavailable: " + diagnostic);
        return text;
    }

    public static string? EventBase(string? eventName, JsonElement? data) => eventName switch
    {
        "pull_request" => Text(data!.Value.GetProperty("pull_request").GetProperty("base").GetProperty("sha")),
        "push" => Text(data!.Value.GetProperty("before")),
        "merge_group" => Text(data!.Value.GetProperty("merge_group").GetProperty("base_sha")),
        _ => null
    };

    public static async Task Check(string root, bool writeNotice = false)
    {
        var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
        var comparison = EventBase(eventName, eventPath is null ? null : Parse(File.ReadAllBytes(eventPath)));
        comparison ??= (await Git(root, "rev-parse", "--abbrev-ref", "HEAD")).Trim() == "main" ? "HEAD" : "origin/main";
        comparison = (await Git(root, "rev-parse", "--verify", comparison + "^{commit}")).Trim();
        Digest(comparison, true);
        var files = (await Git(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var history = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in (await Git(root, "ls-tree", "-r", "--name-only", comparison, "--", Store)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            history.Add(path, Utf8.GetBytes(await Git(root, "show", comparison + ":" + path)));
        var priorExists = (await Git(root, "ls-tree", "-r", "--name-only", comparison, "--", Inventory)).Trim().Length > 0;
        JsonElement? prior = priorExists ? Parse(Utf8.GetBytes(await Git(root, "show", comparison + ":" + Inventory))) : null;
        var result = Validate(root, files, history, prior, writeNotice);
        var directory = Path.Combine(root, "artifacts/evidence");
        Directory.CreateDirectory(directory);
        var report = new
        {
            result.Result,
            result.Repository,
            result.Files,
            result.ReusedFiles,
            result.Records,
            result.ActiveRecords,
            result.NoticeSha256,
            sourceCommit = (await Git(root, "rev-parse", "HEAD")).Trim(),
            comparisonCommit = comparison,
            dirty = (await Git(root, "status", "--porcelain")).Length != 0
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "provenance.json"), JsonSerializer.Serialize(report, Json) + "\n");
        Console.WriteLine($"Provenance passed: {result.Files} files, {result.ReusedFiles} reused files, {result.Records} immutable records.");
    }

    public static IReadOnlyDictionary<string, byte[]> PackageNotices(string root)
    {
        var inv = Parse(Read(root, Inventory));
        var ids = inv.GetProperty("reused").EnumerateObject().Select(p => Text(p.Value)).Concat(Strings(inv.GetProperty("artifacts"), empty: true)).ToHashSet(StringComparer.Ordinal);
        var records = ids.ToDictionary(id => id, id => Parse(Read(root, Store + id + ".json")), StringComparer.Ordinal);
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["notices/source-provenance.txt"] = Utf8.GetBytes(Notice(records, ids, true)) };
        foreach (var record in records.Values.Where(r => Text(r.GetProperty("notice").GetProperty("distribution")) == "packages"))
            foreach (var target in Array(record.GetProperty("targets"), true))
            {
                var path = Text(target.GetProperty("path"));
                var destination = path == "LICENSE" ? path : path.StartsWith("third-party/", StringComparison.Ordinal) ? "notices/upstream/" + Path.GetFileName(path) : throw new InvalidOperationException("Unassigned distribution notice: " + path);
                var bytes = Read(root, path);
                var normalized = Text(target.GetProperty("normalization")) == "lf" ? Lf(bytes) : bytes;
                Require(Hash(normalized) == Text(target.GetProperty("sha256")), "Changed package notice source: " + path);
                Require(expected.TryAdd(destination, bytes), "Duplicate distribution notice binding.");
            }
        return expected;
    }

    public static void VerifyPackageNotices(IReadOnlyDictionary<string, byte[]> expected, Func<string, byte[]?> read)
    {
        foreach (var item in expected)
            Require(read(item.Key) is { } actual && actual.SequenceEqual(item.Value), "Missing or changed package provenance notice: " + item.Key);
    }
}

public sealed record ProvenanceResult(string Result, string Repository, int Files, int ReusedFiles, int Records, string[] ActiveRecords, string NoticeSha256);
