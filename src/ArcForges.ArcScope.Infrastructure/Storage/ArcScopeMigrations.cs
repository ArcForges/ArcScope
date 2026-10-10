// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using ArcForges.Foundation.Versions;
using ArcForges.Persistence.Sqlite.Migrations;

namespace ArcForges.ArcScope.Infrastructure.Storage;

/// <summary>One owner-authored migration file: its file name (<c>NNNN_lower_snake_name.sql</c>) and its SQL text.</summary>
public sealed record MigrationSource
{
    public MigrationSource(string fileName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(text);
        FileName = fileName;
        Text = text;
    }

    public string FileName { get; }
    public string Text { get; }
}

/// <summary>
/// ArcScope's complete, immutable storage migration history. The steps are embedded resources named after their files and
/// are ordered by file name with <see cref="StringComparer.Ordinal"/>, never by the order a file system, MSBuild glob or
/// resource table enumerates them; the numbers must run contiguously from 0001. Line endings are normalized to LF before the
/// step checksum is computed, so a checkout's line-ending policy cannot change an applied migration's identity.
/// </summary>
public static class ArcScopeMigrations
{
    /// <summary>The manifest resource name prefix of every embedded migration file.</summary>
    public const string ResourcePrefix = "ArcForges.ArcScope.Infrastructure.Storage.Migrations.";

    /// <summary>The line that starts each statement of a migration file.</summary>
    public const string StatementMarker = "-- @statement";

    private const int NumberDigits = 4;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<MigrationPlan> Embedded = new(() => Build(LoadEmbedded()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The plan built from the migration files embedded in this assembly.</summary>
    public static MigrationPlan Plan => Embedded.Value;

    /// <summary>The storage schema version the embedded plan produces.</summary>
    public static StorageSchemaVersion LatestVersion => Plan.LatestVersion;

    /// <summary>
    /// Builds the plan from migration files given in any order. The files are sorted ordinally by name; a malformed name, a
    /// duplicate, a gap or a file without statements refuses the whole plan.
    /// </summary>
    public static MigrationPlan Build(IEnumerable<MigrationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources.Select(source => source ?? throw new ArgumentException("A migration source cannot be null.", nameof(sources)))
            .OrderBy(source => source.FileName, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0) throw new ArgumentException("ArcScope has at least one storage migration.", nameof(sources));
        var steps = new List<MigrationStep>(ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            var source = ordered[index];
            if (!TryParseName(source.FileName, out uint number, out string id))
                throw new ArgumentException("A migration file is named NNNN_lower_snake_name.sql: " + source.FileName, nameof(sources));
            if (number != (uint)index + 1)
                throw new ArgumentException("Migration numbers run contiguously from 0001 without duplicates: " + source.FileName, nameof(sources));
            steps.Add(new MigrationStep(new StorageSchemaVersion(number), id, SplitStatements(source)));
        }

        return new MigrationPlan(steps);
    }

    /// <summary>Reads the embedded migration files, in the order the resource table lists them.</summary>
    internal static IReadOnlyList<MigrationSource> LoadEmbedded()
    {
        var assembly = typeof(ArcScopeMigrations).Assembly;
        var sources = new List<MigrationSource>();
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".sql", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("An embedded migration resource cannot be read: " + name);
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false);
            sources.Add(new MigrationSource(name[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return sources;
    }

    /// <summary>Parses <c>NNNN_lower_snake_name.sql</c>; the step identity is the file stem.</summary>
    internal static bool TryParseName(string fileName, out uint number, out string id)
    {
        number = 0;
        id = string.Empty;
        const string Extension = ".sql";
        if (fileName.Length <= NumberDigits + 1 + Extension.Length || !fileName.EndsWith(Extension, StringComparison.Ordinal)
            || fileName[NumberDigits] != '_')
        {
            return false;
        }

        string digits = fileName[..NumberDigits];
        string name = fileName[(NumberDigits + 1)..^Extension.Length];
        if (!digits.All(char.IsAsciiDigit) || !uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number == 0)
        {
            number = 0;
            return false;
        }

        bool wellFormed = name.Length > 0 && name[0] != '_' && name[^1] != '_' && !name.Contains("__", StringComparison.Ordinal)
            && name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
        if (!wellFormed)
        {
            number = 0;
            return false;
        }

        id = fileName[..^Extension.Length];
        return true;
    }

    /// <summary>
    /// Splits a file at its statement markers. Text before the first marker may hold only blank lines and <c>--</c> comments;
    /// every statement must hold SQL.
    /// </summary>
    internal static IReadOnlyList<string> SplitStatements(MigrationSource source)
    {
        string text = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Contains('\r', StringComparison.Ordinal)) throw new ArgumentException("A migration file uses LF or CRLF line endings: " + source.FileName);
        var statements = new List<string>();
        StringBuilder? current = null;
        foreach (string line in text.Split('\n'))
        {
            if (string.Equals(line.TrimEnd(), StatementMarker, StringComparison.Ordinal))
            {
                if (current is not null) statements.Add(Finish(current, source));
                current = new StringBuilder();
                continue;
            }

            if (current is null)
            {
                string header = line.Trim();
                if (header.Length > 0 && !header.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("SQL before the first statement marker: " + source.FileName);
                continue;
            }

            current.Append(line).Append('\n');
        }

        if (current is null) throw new ArgumentException("A migration file has at least one statement: " + source.FileName);
        statements.Add(Finish(current, source));
        return statements;
    }

    private static string Finish(StringBuilder statement, MigrationSource source)
    {
        string sql = statement.ToString().Trim();
        if (sql.Length == 0) throw new ArgumentException("A migration statement holds SQL: " + source.FileName);
        return sql;
    }
}
