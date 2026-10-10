// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics.CodeAnalysis;
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.ArcScope.Infrastructure.Storage;
using ArcForges.ArcScope.Tests.Annotations;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcForges.ArcScope.Tests.Storage;

/// <summary>
/// A real ArcScope store file in its own temporary directory, with the application write path, queries and user-interface
/// surface wired over the real SQLite annotation store and session catalog. Only the wall clock is a test double.
/// </summary>
internal sealed class SqliteStoreFixture : IDisposable
{
    private readonly string directory;

    public SqliteStoreFixture()
    {
        directory = Path.Combine(Path.GetTempPath(), "arcscope-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "arcscope.db");
        // The wall clock starts at the real time so the mechanism's journal-age snapshot policy behaves as in use.
        Start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Time = new ManualTimeProvider(Start);
        Store = ArcScopeStore.Open(DatabasePath, StoreId);
        Wire();
    }

    public string DatabasePath { get; }
    public DateTimeOffset Start { get; }
    public Guid StoreId { get; } = Guid.NewGuid();
    public ManualTimeProvider Time { get; }
    public ArcScopeStore Store { get; private set; }
    public AnnotationWritePath WritePath { get; private set; } = null!;
    public AnnotationQueries Queries { get; private set; } = null!;
    public AnnotationCommandSurface Surface { get; private set; } = null!;
    public UserId Person { get; } = new(Guid.NewGuid());
    public SessionTargetId Session { get; } = new(Guid.NewGuid());

    /// <summary>Closes the store and opens the same file again, as a restart does.</summary>
    public ArcScopeStoreOpenReport Reopen()
    {
        Store.Dispose();
        Store = ArcScopeStore.Open(DatabasePath, StoreId);
        Wire();
        return Store.OpenReport;
    }

    public async Task<SqliteStoreFixture> WithSessionAsync(ulong samples = 10_000, SessionTargetId? session = null)
    {
        var registered = await Store.SessionTargets.RegisterAsync(new SessionTarget(session ?? Session, samples), TestContext.Current.CancellationToken);
        Assert.Equal(OutcomeKind.Success, registered.Kind);
        return this;
    }

    public CreateAnnotation Create(AnnotationId? id = null, string text = "rising edge glitch", ulong from = 100, ulong count = 50,
        CommandId? command = null, AnnotationActor? actor = null, AnnotationContentKind kind = AnnotationContentKind.NonAi,
        SessionTargetId? session = null) =>
        new(command ?? new CommandId(Guid.NewGuid()), id ?? new AnnotationId(Guid.NewGuid()), session ?? Session,
            new SampleRange(from, count), AnnotationText.Create(text), actor ?? AnnotationActor.Human(Person), kind);

    public AppendAnnotation Append(AnnotationId id, ulong expected, string text = "confirmed on the second capture",
        CommandId? command = null, AnnotationActor? actor = null, AnnotationContentKind kind = AnnotationContentKind.NonAi) =>
        new(command ?? new CommandId(Guid.NewGuid()), id, new NativeRevision(expected), AnnotationText.Create(text),
            actor ?? AnnotationActor.Human(Person), kind);

    /// <summary>Every retained journal entry of the shared store.</summary>
    public IReadOnlyList<JournalEntry> Journal()
    {
        var all = new List<JournalEntry>();
        JournalSequence? after = null;
        while (true)
        {
            var page = Store.Shared.ReadJournal(after, 256);
            all.AddRange(page);
            if (page.Count < 256) return all;
            after = page[^1].Sequence;
        }
    }

    /// <summary>Runs SQL on the file directly, outside every ArcScope guard, as damage or another tool would.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only raw access with constant SQL and bound parameters.")]
    public object? Raw(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    public long RawCount(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(Raw(sql, parameters), System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless; the next run uses a fresh one.
        }
    }

    private void Wire()
    {
        WritePath = new AnnotationWritePath(Store.Annotations, Store.SessionTargets, new Clock(Time));
        Queries = new AnnotationQueries(Store.Annotations, Store.SessionTargets);
        Surface = new AnnotationCommandSurface(WritePath, Queries, Person);
    }
}
