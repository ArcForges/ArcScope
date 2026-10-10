// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.ArcScope.Infrastructure.Storage;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation.Errors;
using ArcForges.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace ArcForges.ArcScope.Infrastructure.Annotations;

/// <summary>
/// The annotation store over ArcScope's store file. Each annotation is one aggregate of the shared store mechanism (kind
/// <c>arcscope.annotation</c>, native content revision = annotation revision) whose payload is the canonical annotation with
/// its full history; every commit writes that payload, its content-origin record, the journal entry, the next revision and
/// the durable command receipt in one transaction of the mechanism's single authorized write path.
/// <para>
/// The session index (<c>arcscope_annotation_index</c>) is derived. A create writes its index row before the canonical commit,
/// so every committed annotation has a row even if the process stops right after the commit; a row whose annotation never
/// committed is skipped by reads and removed when the store next opens, and the open also restores rows from the retained
/// journal after a snapshot recovery. Commits are serialized within the owning <see cref="ArcScopeStore"/>, which holds the
/// file's exclusive owner lock, so the checks below cannot race another writer.
/// </para>
/// </summary>
public sealed class SqliteAnnotationStore : IAnnotationStore
{
    /// <summary>The aggregate kind of an annotation in the shared store (the resource kind <c>arcscope.annotation</c>).</summary>
    public const string AggregateKind = "arcscope.annotation";

    private const int IndexBatch = 64;

    private readonly ArcScopeStore owner;
    private readonly SemaphoreSlim commits = new(1, 1);

    internal SqliteAnnotationStore(ArcScopeStore owner) => this.owner = owner;

    /// <summary>Test seam: runs after the index intent is written and before the canonical commit.</summary>
    internal Action<AnnotationCommit>? BeforeCanonicalWrite { get; set; }

    public async ValueTask<Annotation?> ReadAsync(AnnotationId annotation, CancellationToken cancellationToken)
    {
        var store = owner.Shared;
        return await Task.Run(() => Load(store, annotation.Value)?.Annotation, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Annotation>> ReadSessionAsync(SessionTargetId session, AnnotationPageCursor? after, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var store = owner.Shared;
        var product = owner.Product;
        return await Task.Run(() => ReadSession(store, product, session, after, limit, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Outcome<AnnotationCommitReceipt>> CommitAsync(AnnotationCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateShape(commit);
        if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled<AnnotationCommitReceipt>(EffectCertainty.DidNotHappen);
        try
        {
            await commits.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled<AnnotationCommitReceipt>(EffectCertainty.DidNotHappen);
        }

        try
        {
            // Once dispatched the commit runs to its end; cancellation can no longer prove that it did not happen.
            return await Task.Run(() => Commit(commit), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            commits.Release();
        }
    }

    private Outcome<AnnotationCommitReceipt> Commit(AnnotationCommit commit)
    {
        var store = owner.Shared;
        if (store.Recovery.RequiresSafeStart) return Refuse(AnnotationFailures.IntegrityFailed);
        StoredAnnotation? current;
        try
        {
            current = Load(store, commit.Next.Id.Value);
        }
        catch (InvalidDataException)
        {
            return Refuse(AnnotationFailures.IntegrityFailed);
        }

        if (current is not null && current.Annotation.FindEntry(commit.Command) is { } recorded)
        {
            return string.Equals(recorded.CommandFingerprint, commit.Fingerprint, StringComparison.Ordinal)
                ? Outcome.Success(new AnnotationCommitReceipt(commit.Command, current.Annotation.Id, recorded.Revision, Replayed: true))
                : Refuse(AnnotationFailures.ReusedCommand);
        }

        if (commit.ExpectedRevision.Value == 0)
        {
            if (current is not null) return Refuse(AnnotationFailures.DuplicateIdentifier);
        }
        else
        {
            if (current is null || current.Annotation.Revision != commit.ExpectedRevision) return Refuse(AnnotationFailures.RevisionMismatch);
            RequireAppendOnly(current.Annotation, commit.Next);
        }

        // A writer that cannot preserve an unsupported origin profile must not replace it.
        if (current is { Writable: false }) return Refuse(AnnotationFailures.IntegrityFailed);

        if (current is null) owner.Product.Write((connection, transaction) => UpsertIndex(connection, transaction, commit.Next));
        BeforeCanonicalWrite?.Invoke(commit);

        byte[] payload = AnnotationPayload.Encode(commit.Next);
        var origin = AnnotationPayload.CreateOrigin(commit.Next, payload, current?.Origin);
        var entry = commit.Entry;
        var write = new WriteCommand(commit.Command, AggregateKind, commit.Next.Id.Value,
            current is null ? StoreVersion.NewRoot : StoreVersion.Native(current.Annotation.Revision),
            new StoredContent(StoreVersion.Native(commit.Next.Revision), payload, origin), commit.Operation, entry.Actor.User,
            entry.Actor.Invocation?.Value ?? commit.Command.Value, entry.CommittedAt);

        CommitReceipt receipt;
        owner.Authorization.Permit(write);
        try
        {
            receipt = store.Write(write);
        }
        catch (InvalidOperationException exception) when (exception.GetType() == typeof(InvalidOperationException))
        {
            // The owner lock and the commit gate exclude every other writer; safe start, the revision, the origin profile, the
            // kind union and a fresh origin identity were all checked above. The mechanism's remaining plain refusal is a command
            // identity it already recorded for another aggregate. Its storage-pressure refusal is a derived exception type and
            // propagates as an unexpected failure. Confirm the state did not move.
            var after = Load(store, commit.Next.Id.Value);
            return (after?.Annotation.Revision.Value ?? 0) == commit.ExpectedRevision.Value
                ? Refuse(AnnotationFailures.ReusedCommand)
                : Refuse(AnnotationFailures.RevisionMismatch);
        }
        finally
        {
            owner.Authorization.Revoke();
        }

        if (receipt.Replayed || receipt.CommandId != commit.Command || receipt.Version != StoreVersion.Native(commit.Next.Revision))
        {
            // The mechanism answered for a different effect than this commit; stored state cannot be trusted.
            return Refuse(AnnotationFailures.IntegrityFailed);
        }

        return Outcome.Success(new AnnotationCommitReceipt(commit.Command, commit.Next.Id, commit.Next.Revision, Replayed: false));
    }

    /// <summary>Reads and fully validates one stored annotation, or null when it does not exist.</summary>
    internal static StoredAnnotation? Load(SqliteStore store, Guid annotation)
    {
        StoredContent? content;
        try
        {
            content = store.Read(AggregateKind, annotation);
        }
        catch (Exception exception) when (exception is Google.Protobuf.InvalidProtocolBufferException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("A stored annotation row cannot be read.", exception);
        }

        if (content is null) return null;
        byte[] payload = content.Payload.ToArray();
        var decoded = AnnotationPayload.Decode(payload);
        if (decoded.Id.Value != annotation) throw new InvalidDataException("A stored annotation payload names another annotation.");
        if (content.Version != StoreVersion.Native(decoded.Revision))
            throw new InvalidDataException("A stored annotation's revision differs from its history.");
        var origin = content.Origin;
        bool writable = AnnotationPayload.VerifyOrigin(decoded, payload, origin);
        return new StoredAnnotation(decoded, origin, writable);
    }

    /// <summary>Writes or replaces the derived index row of an annotation; its session and range never change after create.</summary>
    internal static int UpsertIndex(SqliteConnection connection, SqliteTransaction transaction, Annotation annotation)
    {
        using var upsert = ProductDatabase.Command(connection, transaction, """
            INSERT INTO arcscope_annotation_index(annotation_id,session_id,range_from,range_count) VALUES($annotation,$session,$from,$count)
            ON CONFLICT(annotation_id) DO UPDATE SET session_id=excluded.session_id,range_from=excluded.range_from,range_count=excluded.range_count
            WHERE session_id<>excluded.session_id OR range_from<>excluded.range_from OR range_count<>excluded.range_count
            """);
        upsert.Parameters.AddWithValue("$annotation", StorageText.Identity(annotation.Id.Value));
        upsert.Parameters.AddWithValue("$session", StorageText.Identity(annotation.Session.Value));
        upsert.Parameters.AddWithValue("$from", StorageText.Unsigned(annotation.Range.From));
        upsert.Parameters.AddWithValue("$count", StorageText.Unsigned(annotation.Range.Count));
        return upsert.ExecuteNonQuery();
    }

    private static IReadOnlyList<Annotation> ReadSession(SqliteStore store, ProductDatabase product, SessionTargetId session,
        AnnotationPageCursor? after, int limit, CancellationToken cancellationToken)
    {
        var page = new List<Annotation>(Math.Min(limit, IndexBatch));
        var position = after;
        while (page.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = product.Read((connection, transaction) => ReadIndex(connection, transaction, session, position, IndexBatch));
            foreach (var row in rows)
            {
                position = row.Position;
                var stored = Load(store, row.Annotation);
                if (stored is null) continue; // an index intent whose create never committed
                var annotation = stored.Annotation;
                if (annotation.Session != session || AnnotationPageCursor.Compare(AnnotationPageCursor.After(annotation), row.Position) != 0)
                    throw new InvalidDataException("The session index disagrees with the stored annotation.");
                page.Add(annotation);
                if (page.Count == limit) break;
            }

            if (rows.Count < IndexBatch) break;
        }

        return page;
    }

    private static List<IndexRow> ReadIndex(SqliteConnection connection, SqliteTransaction transaction, SessionTargetId session,
        AnnotationPageCursor? after, int batch)
    {
        using var query = after is null
            ? ProductDatabase.Command(connection, transaction, """
                SELECT annotation_id,range_from,range_count FROM arcscope_annotation_index WHERE session_id=$session
                ORDER BY range_from,range_count,annotation_id LIMIT $limit
                """)
            : ProductDatabase.Command(connection, transaction, """
                SELECT annotation_id,range_from,range_count FROM arcscope_annotation_index
                WHERE session_id=$session AND (range_from,range_count,annotation_id)>($from,$count,$annotation)
                ORDER BY range_from,range_count,annotation_id LIMIT $limit
                """);
        query.Parameters.AddWithValue("$session", StorageText.Identity(session.Value));
        query.Parameters.AddWithValue("$limit", batch);
        if (after is { } cursor)
        {
            query.Parameters.AddWithValue("$from", StorageText.Unsigned(cursor.From));
            query.Parameters.AddWithValue("$count", StorageText.Unsigned(cursor.Count));
            query.Parameters.AddWithValue("$annotation", StorageText.Identity(cursor.Annotation));
        }

        var rows = new List<IndexRow>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) throw new InvalidDataException("A session index row has a missing column.");
            var id = StorageText.ReadIdentity(reader.GetString(0));
            rows.Add(new IndexRow(id, new AnnotationPageCursor(StorageText.ReadUnsigned(reader.GetString(1)),
                StorageText.ReadUnsigned(reader.GetString(2)), id)));
        }

        return rows;
    }

    private static void ValidateShape(AnnotationCommit commit)
    {
        var entry = commit.Entry;
        bool create = string.Equals(commit.Operation, AnnotationOperations.Create, StringComparison.Ordinal);
        bool append = string.Equals(commit.Operation, AnnotationOperations.Append, StringComparison.Ordinal);
        bool valid = (create && commit.ExpectedRevision.Value == 0 && commit.Next.Entries.Count == 1)
            || (append && commit.ExpectedRevision.Value != 0 && commit.Next.Revision.Value == commit.ExpectedRevision.Value + 1);
        if (!valid || entry.Command != commit.Command || !string.Equals(entry.CommandFingerprint, commit.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("An annotation commit must be one create or one append of exactly one entry by its command.", nameof(commit));
    }

    private static void RequireAppendOnly(Annotation current, Annotation next)
    {
        bool extends = next.Id == current.Id && next.Session == current.Session && next.Range == current.Range
            && next.Entries.Count == current.Entries.Count + 1
            && current.Entries.Select((entry, index) => SameEntry(entry, next.Entries[index])).All(same => same);
        if (!extends) throw new ArgumentException("An append extends the stored history by exactly one entry and never rewrites it.", nameof(next));
    }

    private static bool SameEntry(AnnotationEntry left, AnnotationEntry right) =>
        left.Revision == right.Revision && left.Command == right.Command
        && string.Equals(left.CommandFingerprint, right.CommandFingerprint, StringComparison.Ordinal)
        && left.Text.Equals(right.Text) && left.Actor == right.Actor && left.ContentKind == right.ContentKind
        && left.CommittedAt == right.CommittedAt;

    private static Outcome<AnnotationCommitReceipt> Refuse(string code) => AnnotationFailures.Refuse<AnnotationCommitReceipt>(code);

    private sealed record IndexRow(Guid Annotation, AnnotationPageCursor Position);
}

/// <summary>One stored annotation with its origin; <see cref="Writable"/> is false for an unsupported origin profile.</summary>
internal sealed record StoredAnnotation(Annotation Annotation, ContentOrigin Origin, bool Writable);
