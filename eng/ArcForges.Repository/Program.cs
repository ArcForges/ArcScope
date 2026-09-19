// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArcForges.Repository;

public static partial class Program
{
    public static readonly string[] Rids = ["win-x64", "win-arm64", "linux-x64", "osx-x64", "osx-arm64"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["hooks"]:
                    await Run("git", ["config", "extensions.worktreeConfig", "true"]);
                    await Run("git", ["config", "--worktree", "core.hooksPath", ".githooks"]);
                    break;
                case ["check"]: await Check(); break;
                case ["provenance-notice"]: await ProvenancePolicy.Check(Directory.GetCurrentDirectory(), writeNotice: true); break;
                case ["version"]:
                    var generatedVersion = Version(Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? "0", Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "1");
                    Console.WriteLine(generatedVersion);
                    if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { } output)
                        await File.AppendAllTextAsync(output, $"version={generatedVersion}\n");
                    break;
                case ["prepare", var rid, var version]: await Prepare(rid, version); break;
                case ["smoke", var rid]:
                    ValidateRid(rid);
                    Directory.CreateDirectory("artifacts/evidence");
                    await Run(Executable(rid), ["--smoke-live", "--evidence", Path.GetFullPath($"artifacts/evidence/{rid}.json")]);
                    break;
                case ["pack", var rid, var version, var commit]: await Pack(rid, version, commit); break;
                case ["verify", var path, var version, var commit]:
                    var manifests = Directory.GetFiles(path, "manifest.json", SearchOption.AllDirectories);
                    if (manifests.Length != Rids.Length) throw new InvalidOperationException("Expected all five native candidates.");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var manifest in manifests)
                    {
                        var rid = VerifyCandidate(manifest, version, commit);
                        if (!seen.Add(rid)) throw new InvalidOperationException("Duplicate candidate RID.");
                    }
                    Console.WriteLine("Verified all five immutable native candidates.");
                    break;
                default: throw new ArgumentException("Use hooks, check, provenance-notice, version, prepare RID VERSION, smoke RID, pack RID VERSION COMMIT, or verify DIRECTORY VERSION COMMIT.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    public static string Version(string run, string attempt)
    {
        if (!int.TryParse(run, out var n) || n < 0 || !int.TryParse(attempt, out var a) || a is < 1 or > 99)
            throw new ArgumentException("Invalid CI version coordinates.");
        return $"0.1.0-ci.{n}.{a}";
    }

    private static void ValidateRid(string rid)
    {
        if (!Rids.Contains(rid, StringComparer.Ordinal)) throw new ArgumentException("Unsupported RID.");
    }
    private static void ValidateVersion(string version)
    {
        if (!VersionPattern().IsMatch(version)) throw new ArgumentException("Invalid release version.");
    }
    [GeneratedRegex(@"^0\.1\.0-ci\.[0-9]+\.[0-9]+$")]
    private static partial Regex VersionPattern();
    private static string Stage(string rid) => Path.GetFullPath($"artifacts/stage/{rid}");
    private static string Executable(string rid) => Path.Combine(Stage(rid), rid.StartsWith("osx-", StringComparison.Ordinal)
        ? "ArcScope.app/Contents/MacOS/ArcScope" : rid.StartsWith("win-", StringComparison.Ordinal) ? "ArcScope.exe" : "ArcScope");

    private static async Task Prepare(string rid, string version)
    {
        ValidateRid(rid);
        ValidateVersion(version);
        await CheckSourceForDistribution();
        var source = Path.GetFullPath($"artifacts/publish/{rid}");
        var stage = Stage(rid);
        if (Directory.Exists(stage)) throw new InvalidOperationException("Staging already exists; use a fresh worktree/output directory.");
        var binaryRoot = rid.StartsWith("osx-", StringComparison.Ordinal) ? Path.Combine(stage, "ArcScope.app/Contents/MacOS") : stage;
        Directory.CreateDirectory(binaryRoot);
        foreach (var sourceFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(binaryRoot, Path.GetRelativePath(source, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(sourceFile));
        }
        foreach (var name in new[] { "LICENSE", "THIRD_PARTY_NOTICES.md", "README.md" }) File.Copy(name, Path.Combine(stage, name));
        WriteDependencyNotices(stage);
        var requiredNotices = ProvenancePolicy.PackageNotices(Directory.GetCurrentDirectory());
        File.WriteAllBytes(Path.Combine(stage, "notices/source-provenance.txt"), requiredNotices["notices/source-provenance.txt"]);
        ProvenancePolicy.VerifyPackageNotices(requiredNotices, path => File.Exists(Path.Combine(stage, path)) ? File.ReadAllBytes(Path.Combine(stage, path)) : null);
        File.Copy("artifacts/evidence/provenance.json", Path.Combine(stage, "notices/provenance-source.json"));
        if (!File.Exists(Executable(rid))) throw new InvalidOperationException("Published executable is missing.");
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            var parts = version.Split('.');
            await File.WriteAllTextAsync(Path.Combine(stage, "ArcScope.app/Contents/Info.plist"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                <key>CFBundleExecutable</key><string>ArcScope</string>
                <key>CFBundleIdentifier</key><string>com.arcforges.arcscope</string>
                <key>CFBundleName</key><string>ArcScope</string>
                <key>CFBundlePackageType</key><string>APPL</string>
                <key>CFBundleShortVersionString</key><string>0.1.0</string>
                <key>CFBundleVersion</key><string>{parts[^2]}.{parts[^1]}</string>
                <key>NSHighResolutionCapable</key><true/>
                </dict></plist>
                """);
            await Run("codesign", ["--force", "--deep", "--sign", "-", Path.Combine(stage, "ArcScope.app")]);
            await Run("codesign", ["--verify", "--deep", "--strict", Path.Combine(stage, "ArcScope.app")]);
        }
    }

    private static void WriteDependencyNotices(string stage)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText("src/ArcForges.ArcScope/obj/project.assets.json"));
        var roots = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
        var inventory = new List<object>();
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            var relative = library.Value.GetProperty("path").GetString()!;
            var package = roots.Select(root => Path.Combine(root, relative)).First(Directory.Exists);
            var nuspec = XDocument.Load(Directory.GetFiles(package, "*.nuspec").Single());
            string Field(string name) => nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
            inventory.Add(new { package = library.Name, licence = Field("license"), licenceUrl = Field("licenseUrl"), projectUrl = Field("projectUrl") });
            foreach (var entry in library.Value.GetProperty("files").EnumerateArray())
            {
                var file = entry.GetString()!;
                var name = Path.GetFileName(file);
                if (!name.StartsWith("license", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("notice", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("thirdparty", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("third-party", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(stage, "notices", relative, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(package, file), destination);
            }
        }
        File.WriteAllText(Path.Combine(stage, "dependencies.json"), JsonSerializer.Serialize(inventory, Json));
        Directory.CreateDirectory(Path.Combine(stage, "notices/upstream"));
        foreach (var file in Directory.GetFiles("third-party"))
            File.Copy(file, Path.Combine(stage, "notices/upstream", Path.GetFileName(file)));
    }

    private static async Task CheckSourceForDistribution()
    {
        await ProvenancePolicy.Check(Directory.GetCurrentDirectory());
        if ((await Capture("git", ["status", "--porcelain"])).Length != 0)
            throw new InvalidOperationException("Commit reviewed source before producing a source-bound portable candidate.");
    }

    private static async Task Pack(string rid, string version, string commit)
    {
        ValidateRid(rid);
        ValidateVersion(version);
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit)) throw new ArgumentException("Expected a full Git commit.");
        await CheckSourceForDistribution();
        if ((await Capture("git", ["rev-parse", "HEAD"])).Trim() != commit)
            throw new InvalidOperationException("Pack source differs from the candidate revision.");
        var evidence = $"artifacts/evidence/{rid}.json";
        using var smoke = JsonDocument.Parse(File.ReadAllText(evidence));
        ValidateSmoke(smoke.RootElement, rid, version, commit);
        var folder = $"artifacts/candidate/{rid}";
        Directory.CreateDirectory(folder);
        var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var name = $"arcscope-{version}-{rid}{extension}";
        var archive = Path.Combine(folder, name);
        if (File.Exists(archive)) throw new InvalidOperationException("Never overwrite a native candidate archive.");
        if (extension == ".zip") ZipFile.CreateFromDirectory(Stage(rid), archive, CompressionLevel.Optimal, false);
        else
        {
            using var file = File.Create(archive);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            TarFile.CreateFromDirectory(Stage(rid), gzip, false);
        }
        VerifyArchiveNotices(archive, commit, Directory.GetCurrentDirectory());
        File.Copy(evidence, Path.Combine(folder, "smoke.json"));
        File.Copy(Path.ChangeExtension(evidence, ".png"), Path.Combine(folder, "screen.png"));
        var manifest = new Candidate(rid, version, commit, name, Hash(archive), Hash(evidence));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        File.WriteAllText(archive + ".sha256", $"{manifest.ArchiveSha256}  {name}\n");
        Console.WriteLine($"Packed verified {rid}: {name}");
    }

    public static string VerifyCandidate(string manifestPath, string version, string commit, string? sourceRoot = null)
    {
        var manifest = JsonSerializer.Deserialize<Candidate>(File.ReadAllText(manifestPath), Json) ?? throw new InvalidOperationException("Missing manifest.");
        ValidateRid(manifest.Rid);
        ValidateVersion(version);
        if (manifest.Version != version || manifest.SourceRevision != commit) throw new InvalidOperationException("Candidate identity mismatch.");
        var expected = $"arcscope-{version}-{manifest.Rid}" + (manifest.Rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
        if (manifest.Archive != expected) throw new InvalidOperationException("Invalid archive path.");
        var folder = Path.GetDirectoryName(manifestPath)!;
        var archive = Path.Combine(folder, manifest.Archive);
        var smoke = Path.Combine(folder, "smoke.json");
        if (Hash(archive) != manifest.ArchiveSha256 || Hash(smoke) != manifest.SmokeSha256)
            throw new InvalidOperationException("Candidate hash mismatch.");
        if (File.ReadAllText(archive + ".sha256") != $"{manifest.ArchiveSha256}  {manifest.Archive}\n")
            throw new InvalidOperationException("Download checksum mismatch.");
        using var evidence = JsonDocument.Parse(File.ReadAllText(smoke));
        ValidateSmoke(evidence.RootElement, manifest.Rid, version, commit);
        VerifyArchiveNotices(archive, commit, sourceRoot ?? Directory.GetCurrentDirectory());
        return manifest.Rid;
    }

    private static void VerifyArchiveNotices(string archive, string commit, string sourceRoot)
    {
        var expected = ProvenancePolicy.PackageNotices(sourceRoot);
        const string receiptName = "notices/provenance-source.json";
        var selected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Retain(string path, Stream? stream)
        {
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            path = path.TrimEnd('/');
            if (path.Length == 0 || path == ".") return;
            if (path.StartsWith('/') || path.IndexOfAny(['\\', ':', '\0']) >= 0 || path.Split('/').Any(p => p is "" or "." or ".."))
                throw new InvalidOperationException("Escaping archive member: " + path);
            if (!members.Add(path)) throw new InvalidOperationException("Duplicate or case-colliding archive member: " + path);
            if (!expected.ContainsKey(path) && path != receiptName) return;
            if (stream is null) throw new InvalidOperationException("Required notice is not a regular archive file: " + path);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            if (!selected.TryAdd(path, memory.ToArray())) throw new InvalidOperationException("Duplicate archive notice: " + path);
        }
        if (archive.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidOperationException("Linked ZIP member: " + entry.FullName);
                using var stream = entry.Open();
                Retain(entry.FullName, stream);
            }
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    throw new InvalidOperationException("Linked tar member: " + entry.Name);
                Retain(entry.Name, entry.DataStream);
            }
        }
        ProvenancePolicy.VerifyPackageNotices(expected, path => selected.GetValueOrDefault(path));
        if (!selected.TryGetValue(receiptName, out var receipt)) throw new InvalidOperationException("Missing candidate source provenance.");
        using var report = JsonDocument.Parse(receipt);
        if (report.RootElement.GetProperty("sourceCommit").GetString() != commit ||
            report.RootElement.GetProperty("dirty").GetBoolean() || report.RootElement.GetProperty("result").GetString() != "passed")
            throw new InvalidOperationException("Candidate provenance does not match clean reviewed source.");
    }

    private static void ValidateSmoke(JsonElement smoke, string rid, string version, string commit)
    {
        if (!smoke.GetProperty("success").GetBoolean() || !smoke.GetProperty("nativeAot").GetBoolean() ||
            smoke.GetProperty("rid").GetString() != rid || smoke.GetProperty("sourceRevision").GetString() != commit ||
            smoke.GetProperty("version").GetString() != version || smoke.GetProperty("uiGreeting").GetString() != "Hello, ArcScope!" ||
            !smoke.GetProperty("cloud").GetProperty("nativeAot").GetBoolean())
            throw new InvalidOperationException("Native UI/live evidence does not match the candidate.");
        var expected = new[] { "native-ui-live-action", "unicode-whitespace-boundary", "InvalidArgument", "ResourceExhausted" };
        if (!smoke.GetProperty("checks").EnumerateArray().Select(c => c.GetString()).SequenceEqual(expected))
            throw new InvalidOperationException("Incomplete native UI/live checks.");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static async Task Check()
    {
        await Run("git", ["diff", "--check"]);
        await ProvenancePolicy.Check(Directory.GetCurrentDirectory());
        using var upstream = JsonDocument.Parse(File.ReadAllText("third-party/sources.json"));
        foreach (var entry in upstream.RootElement.EnumerateArray())
        {
            var path = entry.GetProperty("File").GetString()!;
            if (Hash(path) != entry.GetProperty("Sha256").GetString())
                throw new InvalidOperationException($"Upstream licence text has changed: {path}");
        }
        var files = (await Capture("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"])).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in files)
        {
            if (!File.Exists(path)) continue;
            if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
            }
            if (Path.GetExtension(path) is ".csproj" or ".props" or ".slnx" or ".manifest" || path == "NuGet.Config")
                XDocument.Load(path);
            if (Path.GetExtension(path) is ".cs" or ".md" or ".yml" or ".json" or ".props" or ".csproj" or ".slnx")
            {
                var text = File.ReadAllText(path, new UTF8Encoding(false, true));
                // NuGet owns lock formatting and may omit a final newline when regenerating it.
                if (!text.EndsWith('\n') && Path.GetFileName(path) != "packages.lock.json")
                    throw new InvalidOperationException($"Missing final newline: {path}");
                if (path.EndsWith(".cs", StringComparison.Ordinal) && !text.StartsWith("// SPDX-License-Identifier: AGPL-3.0-only\n", StringComparison.Ordinal) && !text.StartsWith("// SPDX-License-Identifier: AGPL-3.0-only\r\n", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Missing source licence: {path}");
            }
        }
        Console.WriteLine("Repository text, structured inputs and whitespace checks passed.");
        var projects = LicencePolicy.Validate(Directory.GetCurrentDirectory(), files);
        var evaluated = new List<object>();
        foreach (var project in projects)
        {
            using var report = JsonDocument.Parse(await Capture("dotnet", ["msbuild", project,
                "-t:ArcForgesVerifyLicenceBoundary", "-getProperty:PackageLicenseExpression,LicenceBoundary",
                "-getItem:ProjectReference", "-verbosity:quiet"]));
            var properties = report.RootElement.GetProperty("Properties");
            evaluated.Add(new
            {
                path = project,
                spdxLicense = properties.GetProperty("PackageLicenseExpression").GetString(),
                licenceBoundary = properties.GetProperty("LicenceBoundary").GetString(),
                projectReferences = report.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
                    .Select(item => Path.GetRelativePath(Directory.GetCurrentDirectory(), item.GetProperty("FullPath").GetString()!).Replace('\\', '/')).ToArray()
            });
        }
        Directory.CreateDirectory("artifacts/evidence");
        await File.WriteAllTextAsync("artifacts/evidence/licence-boundary.json", JsonSerializer.Serialize(new
        {
            result = "passed",
            repository = "ArcScope",
            commit = (await Capture("git", ["rev-parse", "HEAD"])).Trim(),
            dirty = (await Capture("git", ["status", "--porcelain"])).Trim().Length != 0,
            spdxLicense = "AGPL-3.0-only",
            licenceBoundary = "AGPL",
            projects = evaluated
        }, Json));
        Console.WriteLine($"Verified {projects.Length} project licence declarations and references.");
    }

    private static async Task<string> Capture(string command, string[] args)
    {
        var info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {command}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException($"{command} exceeded its 120-second bound."); }
        var text = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{command} exited {process.ExitCode}: {text}\n{stderr}");
        return text;
    }
    private static async Task Run(string command, string[] args) => Console.Write(await Capture(command, args));
}

public sealed record Candidate(string Rid, string Version, string SourceRevision, string Archive, string ArchiveSha256, string SmokeSha256);
