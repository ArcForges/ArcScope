// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using ArcForges.ArcScope.Infrastructure.Storage;
using ArcForges.Foundation.Versions;
using ArcForges.Persistence.Sqlite.Migrations;
using Xunit;

namespace ArcForges.ArcScope.Tests.Storage;

public sealed class ArcScopeMigrationsTests
{
    private static readonly string[] SyntheticNames = Enumerable.Range(1, 12)
        .Select(number => string.Create(CultureInfo.InvariantCulture, $"{number:D4}_step_{(char)('a' + number - 1)}.sql")).ToArray();

    [Fact]
    public void TheEmbeddedPlanIsTheContiguousOrdinalSequenceOfTheMigrationFiles()
    {
        var sources = ArcScopeMigrations.LoadEmbedded();
        var plan = ArcScopeMigrations.Plan;

        Assert.NotEmpty(sources);
        Assert.Equal(sources.Count, plan.Steps.Count);
        Assert.Equal(sources.Select(source => source.FileName).Order(StringComparer.Ordinal).Select(name => name[..^4]),
            plan.Steps.Select(step => step.Id));
        Assert.Equal(Enumerable.Range(1, plan.Steps.Count).Select(number => (uint)number), plan.Steps.Select(step => step.Version.Number));
        Assert.Equal(new StorageSchemaVersion((uint)plan.Steps.Count), ArcScopeMigrations.LatestVersion);
        Assert.Equal("0001_annotation_store", plan.Steps[0].Id);
        Assert.Equal(6, plan.Steps[0].Statements.Count);
    }

    [Fact]
    public void ThePlanDoesNotDependOnTheOrderTheFilesAreGiven()
    {
        var sources = SyntheticNames.Select(Synthetic).ToArray();
        var expected = Describe(ArcScopeMigrations.Build(sources));

        Assert.Equal(SyntheticNames.Select(name => name[..^4]), expected.Select(step => step.Id));
        Assert.Equal(expected, Describe(ArcScopeMigrations.Build(sources.Reverse())));
        for (int seed = 0; seed < 8; seed++)
        {
            var shuffled = sources.ToArray();
            new Random(seed).Shuffle(shuffled);
            Assert.Equal(expected, Describe(ArcScopeMigrations.Build(shuffled)));
        }
    }

    [Fact]
    public void FileSystemEnumerationOrderDoesNotChangeThePlan()
    {
        // ext4 and other file systems enumerate a directory in hash or creation order, not by name: create the files in a
        // scrambled order and build the plan from whatever order the file system returns.
        string directory = Path.Combine(Path.GetTempPath(), "arcscope-migration-order", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var scrambled = SyntheticNames.ToArray();
            new Random(20261010).Shuffle(scrambled);
            foreach (string name in scrambled) File.WriteAllText(Path.Combine(directory, name), Synthetic(name).Text);
            var enumerated = Directory.EnumerateFiles(directory, "*.sql")
                .Select(path => new MigrationSource(Path.GetFileName(path), File.ReadAllText(path))).ToArray();

            Assert.Equal(SyntheticNames.Length, enumerated.Length);
            Assert.Equal(Describe(ArcScopeMigrations.Build(SyntheticNames.Select(Synthetic))), Describe(ArcScopeMigrations.Build(enumerated)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LineEndingsDoNotChangeAStepIdentity()
    {
        var lf = Synthetic("0001_step_a.sql");
        var crlf = new MigrationSource(lf.FileName, lf.Text.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(Describe(ArcScopeMigrations.Build([lf])), Describe(ArcScopeMigrations.Build([crlf])));
    }

    [Fact]
    public void StatementsAreSplitOnlyAtMarkersSoTriggerBodiesStayWhole()
    {
        const string Text = """
            -- header comment
            -- @statement
            CREATE TABLE arcscope_t(a INTEGER);
            -- @statement
            CREATE TRIGGER arcscope_t_guard BEFORE DELETE ON arcscope_t
            BEGIN
                SELECT RAISE(ABORT, 'no');
            END;
            """;

        var statements = ArcScopeMigrations.SplitStatements(new MigrationSource("0001_t.sql", Text));

        Assert.Equal(2, statements.Count);
        Assert.Equal("CREATE TABLE arcscope_t(a INTEGER);", statements[0]);
        Assert.StartsWith("CREATE TRIGGER arcscope_t_guard", statements[1], StringComparison.Ordinal);
        Assert.EndsWith("END;", statements[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0001_a.sql", "0003_c.sql")]
    [InlineData("0002_b.sql", "0003_c.sql")]
    [InlineData("0001_a.sql", "0001_b.sql")]
    [InlineData("0001_Upper.sql")]
    [InlineData("0001-a.sql")]
    [InlineData("00001_a.sql")]
    [InlineData("0000_a.sql")]
    [InlineData("0001_.sql")]
    [InlineData("0001_a__b.sql")]
    [InlineData("0001_a.txt")]
    [InlineData("abcd_a.sql")]
    public void MalformedNamesGapsAndDuplicatesRefuseThePlan(params string[] names)
    {
        Assert.Throws<ArgumentException>(() => ArcScopeMigrations.Build(names.Select(Synthetic)));
    }

    [Fact]
    public void AFileWithoutStatementsOrWithSqlBeforeTheFirstMarkerRefuses()
    {
        Assert.Throws<ArgumentException>(() => ArcScopeMigrations.Build([new MigrationSource("0001_a.sql", "-- only a comment\n")]));
        Assert.Throws<ArgumentException>(() => ArcScopeMigrations.Build([new MigrationSource("0001_a.sql", "-- @statement\n\n")]));
        Assert.Throws<ArgumentException>(() =>
            ArcScopeMigrations.Build([new MigrationSource("0001_a.sql", "CREATE TABLE arcscope_x(a);\n-- @statement\nSELECT 1;\n")]));
        Assert.Throws<ArgumentException>(() => ArcScopeMigrations.Build([]));
    }

    private static MigrationSource Synthetic(string name) =>
        new(name, "-- synthetic\n-- @statement\nCREATE TABLE arcscope_" + name.Replace('.', '_').Replace('-', '_') + "(a INTEGER);\n");

    private static (string Id, uint Number, string Checksum)[] Describe(MigrationPlan plan) =>
        plan.Steps.Select(step => (step.Id, step.Version.Number, step.Checksum)).ToArray();
}
