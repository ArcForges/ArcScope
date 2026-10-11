// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.ArcScope.Infrastructure.Annotations;
using ArcForges.ArcScope.Infrastructure.Sessions;
using ArcForges.Foundation.Versions;
using ArcForges.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace ArcForges.ArcScope.Infrastructure.Storage;

/// <summary>What opening the ArcScope store found and did.</summary>
/// <param name="Recovery">The shared mechanism's recovery report for this open.</param>
/// <param name="SchemaVersion">The storage schema version after the ArcScope migrations ran.</param>
/// <param name="IndexRowsRestored">Session index rows recreated for annotations the retained journal names.</param>
/// <param name="IndexRowsRepaired">Session index rows rewritten to match their canonical annotation.</param>
/// <param name="OrphanIndexRowsRemoved">Index rows removed because their create never committed.</param>
/// <param name="UnreadableAnnotations">Annotations whose stored state failed validation; reading them fails closed.</param>
public sealed record ArcScopeStoreOpenReport(StoreRecoveryReport Recovery, StorageSchemaVersion SchemaVersion, int IndexRowsRestored,
    int IndexRowsRepaired, int OrphanIndexRowsRemoved, int UnreadableAnnotations)
{
    /// <summary>False in the mechanism's read-first safe start: annotation commits refuse until the evidence is resolved.</summary>
    public bool WritesEnabled => !Recovery.RequiresSafeStart;
}

/// <summary>
/// ArcScope's local store: one SQLite file owned by one process. Opening takes the file's exclusive owner lock (a second
/// owner, in this or another process, is refused), opens the shared store mechanism under the installation's store identity,
/// applies ArcScope's numbered migrations and reconciles the derived session index. The annotation store and the session
/// catalog it exposes are the persistence ports of the ArcScope application layer.
/// </summary>
public sealed class ArcScopeStore : IDisposable
{
    /// <summary>The suffix of the owner lock file kept beside the database.</summary>
    public const string OwnerLockSuffix = ".arcscope-owner.lock";

    private const int JournalPage = 64;
    private const int IndexPage = 256;

    private readonly FileStream ownerLock;
    private readonly SqliteStore shared;
    private readonly ProductDatabase product;
    private readonly Guid storeId;
    private int disposed;

    private ArcScopeStore(string databasePath, Guid storeId, FileStream ownerLock, SqliteStore shared, AnnotationWriteAuthorization authorization)
    {
        DatabasePath = databasePath;
        this.storeId = storeId;
        this.ownerLock = ownerLock;
        this.shared = shared;
        Authorization = authorization;
        product = new ProductDatabase(databasePath);
        Annotations = new SqliteAnnotationStore(this);
        SessionTargets = new SqliteSessionTargetCatalog(this);
        OpenReport = new ArcScopeStoreOpenReport(shared.Recovery, new StorageSchemaVersion(0), 0, 0, 0, 0);
    }

    /// <summary>The full path of the store file.</summary>
    public string DatabasePath { get; }

    public SqliteAnnotationStore Annotations { get; }

    public SqliteSessionTargetCatalog SessionTargets { get; }

    public ArcScopeStoreOpenReport OpenReport { get; private set; }

    internal SqliteStore Shared
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return shared;
        }
    }

    internal ProductDatabase Product
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return product;
        }
    }

    internal AnnotationWriteAuthorization Authorization { get; }

    /// <summary>
    /// Opens (or creates) the store at <paramref name="databasePath"/> for the installation's durable
    /// <paramref name="storeId"/>. A file that belongs to another store identity, a database written by a newer ArcScope
    /// schema, a changed applied migration and an owner lock held elsewhere all refuse without changes. A store the
    /// mechanism recovered opens read-first (<see cref="ArcScopeStoreOpenReport.WritesEnabled"/> is false); if it would also
    /// need a migration it refuses to open until its evidence is resolved.
    /// </summary>
    public static ArcScopeStore Open(string databasePath, Guid storeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (storeId == Guid.Empty) throw new ArgumentException("A durable store identity is required.", nameof(storeId));
        string fullPath = Path.GetFullPath(databasePath);
        var ownerLock = new FileStream(fullPath + OwnerLockSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        SqliteStore? shared = null;
        try
        {
            var authorization = new AnnotationWriteAuthorization();
            shared = new SqliteStore(fullPath, storeId, authorization);
            var plan = ArcScopeMigrations.Plan;
            StorageSchemaVersion version;
            if (shared.Recovery.RequiresSafeStart)
            {
                var current = shared.Migrations.CurrentVersion;
                if (current is not { } applied || applied != plan.LatestVersion)
                    throw new InvalidOperationException("The recovered ArcScope store needs a schema migration; resolve its recovery evidence before migrating.");
                version = applied;
            }
            else
            {
                version = shared.Migrations.MigrateTo(plan, plan.LatestVersion);
            }

            var store = new ArcScopeStore(fullPath, storeId, ownerLock, shared, authorization);
            store.OpenReport = store.Reconcile(version);
            return store;
        }
        catch
        {
            shared?.Dispose();
            ownerLock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shared.Dispose();
        ownerLock.Dispose();
    }

    /// <summary>
    /// Brings the derived session index in line with the canonical annotations. Every create writes its index row before its
    /// canonical commit, so in a file the mechanism did not restore every committed annotation already has a row. When the
    /// mechanism restored a snapshot and replayed journal entries after it (its report recovers through a later sequence than
    /// it verified), the replayed commits have no row in the restored file: their rows are recreated from the journal entries
    /// after the snapshot boundary. A durable marker records that catch-up
    /// before it starts and is removed after it ends, so an open interrupted half way resumes it on the next open. Then every
    /// index row is checked against its canonical annotation: a row whose create never committed is removed and a row that
    /// disagrees is rewritten from the canonical annotation.
    /// </summary>
    private ArcScopeStoreOpenReport Reconcile(StorageSchemaVersion version)
    {
        int restored = 0, repaired = 0, removed = 0;
        var unreadable = new HashSet<Guid>();
        long? catchUpAfter = product.Read(ReadCatchUpMarker);
        if (shared.Recovery.RecoveredThrough > shared.Recovery.VerifiedThrough)
        {
            long boundary = shared.Recovery.VerifiedThrough;
            long marker = catchUpAfter is { } pending ? Math.Min(pending, boundary) : boundary;
            product.Write((connection, transaction) => WriteCatchUpMarker(connection, transaction, marker));
            catchUpAfter = marker;
        }

        if (catchUpAfter is { } start)
        {
            foreach (var id in AnnotationsJournaledAfter(start).OrderBy(StorageText.Identity, StringComparer.Ordinal))
            {
                var stored = TryLoad(id, unreadable);
                if (stored is null) continue;
                switch (product.Write((connection, transaction) => Align(connection, transaction, stored.Annotation)))
                {
                    case IndexChange.Inserted: restored++; break;
                    case IndexChange.Updated: repaired++; break;
                }
            }

            product.Write(DeleteCatchUpMarker);
        }

        string? position = null;
        while (true)
        {
            var rows = product.Read((connection, transaction) => ReadIndexIdentities(connection, transaction, position));
            foreach (string row in rows)
            {
                position = row;
                var id = StorageText.ReadIdentity(row);
                var stored = TryLoad(id, unreadable);
                if (stored is null)
                {
                    if (unreadable.Contains(id)) continue;
                    removed += product.Write((connection, transaction) => DeleteIndexRow(connection, transaction, row));
                    continue;
                }

                if (product.Write((connection, transaction) => Align(connection, transaction, stored.Annotation)) == IndexChange.Updated) repaired++;
            }

            if (rows.Count < IndexPage) break;
        }

        return new ArcScopeStoreOpenReport(shared.Recovery, version, restored, repaired, removed, unreadable.Count);
    }

    /// <summary>The annotations named by the retained journal entries after <paramref name="after"/>.</summary>
    private HashSet<Guid> AnnotationsJournaledAfter(long after)
    {
        var named = new HashSet<Guid>();
        JournalSequence? position = after == 0 ? null : new JournalSequence(storeId, after);
        while (true)
        {
            IReadOnlyList<JournalEntry> entries;
            try
            {
                entries = shared.ReadJournal(position, JournalPage);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException("The session index cannot be rebuilt: the journal no longer holds the entries after the restored snapshot.", exception);
            }

            foreach (var entry in entries)
            {
                position = entry.Sequence;
                if (string.Equals(entry.AggregateKind, SqliteAnnotationStore.AggregateKind, StringComparison.Ordinal)) named.Add(entry.AggregateId);
            }

            if (entries.Count < JournalPage) return named;
        }
    }

    private StoredAnnotation? TryLoad(Guid id, HashSet<Guid> unreadable)
    {
        try
        {
            return SqliteAnnotationStore.Load(shared, id);
        }
        catch (InvalidDataException)
        {
            unreadable.Add(id);
            return null;
        }
    }

    private static long? ReadCatchUpMarker(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = ProductDatabase.Command(connection, transaction,
            "SELECT value FROM arcscope_store_state WHERE key='index_catch_up_after'");
        return query.ExecuteScalar() is string value ? checked((long)StorageText.ReadUnsigned(value)) : null;
    }

    private static int WriteCatchUpMarker(SqliteConnection connection, SqliteTransaction transaction, long after)
    {
        using var upsert = ProductDatabase.Command(connection, transaction,
            "INSERT INTO arcscope_store_state(key,value) VALUES('index_catch_up_after',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value");
        upsert.Parameters.AddWithValue("$value", StorageText.Unsigned(checked((ulong)after)));
        return upsert.ExecuteNonQuery();
    }

    private static int DeleteCatchUpMarker(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var delete = ProductDatabase.Command(connection, transaction, "DELETE FROM arcscope_store_state WHERE key='index_catch_up_after'");
        return delete.ExecuteNonQuery();
    }

    private static IndexChange Align(SqliteConnection connection, SqliteTransaction transaction, Annotation annotation)
    {
        using var exists = ProductDatabase.Command(connection, transaction,
            "SELECT COUNT(*) FROM arcscope_annotation_index WHERE annotation_id=$annotation");
        exists.Parameters.AddWithValue("$annotation", StorageText.Identity(annotation.Id.Value));
        bool present = Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        int changed = SqliteAnnotationStore.UpsertIndex(connection, transaction, annotation);
        return changed == 0 ? IndexChange.Unchanged : present ? IndexChange.Updated : IndexChange.Inserted;
    }

    private static List<string> ReadIndexIdentities(SqliteConnection connection, SqliteTransaction transaction, string? after)
    {
        using var query = after is null
            ? ProductDatabase.Command(connection, transaction,
                "SELECT annotation_id FROM arcscope_annotation_index ORDER BY annotation_id LIMIT $limit")
            : ProductDatabase.Command(connection, transaction,
                "SELECT annotation_id FROM arcscope_annotation_index WHERE annotation_id>$after ORDER BY annotation_id LIMIT $limit");
        query.Parameters.AddWithValue("$limit", IndexPage);
        if (after is not null) query.Parameters.AddWithValue("$after", after);
        var rows = new List<string>();
        using var reader = query.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static int DeleteIndexRow(SqliteConnection connection, SqliteTransaction transaction, string annotation)
    {
        using var delete = ProductDatabase.Command(connection, transaction, "DELETE FROM arcscope_annotation_index WHERE annotation_id=$annotation");
        delete.Parameters.AddWithValue("$annotation", annotation);
        return delete.ExecuteNonQuery();
    }

    private enum IndexChange
    {
        Unchanged,
        Inserted,
        Updated,
    }
}

/// <summary>
/// The owner's write authorization for the shared mechanism: it admits exactly the one annotation write command the
/// annotation store is committing at that moment, and nothing else.
/// </summary>
internal sealed class AnnotationWriteAuthorization : IStoreAuthorization
{
    private WriteCommand? permitted;

    public bool CanWrite(WriteCommand command) =>
        command is not null && ReferenceEquals(Volatile.Read(ref permitted), command)
        && string.Equals(command.AggregateKind, SqliteAnnotationStore.AggregateKind, StringComparison.Ordinal)
        && (string.Equals(command.Operation, AnnotationOperations.Create, StringComparison.Ordinal)
            || string.Equals(command.Operation, AnnotationOperations.Append, StringComparison.Ordinal));

    internal void Permit(WriteCommand command) => Volatile.Write(ref permitted, command);

    internal void Revoke() => Volatile.Write(ref permitted, null);
}
