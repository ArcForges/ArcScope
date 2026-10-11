// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.ArcScope.Infrastructure.Storage;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Infrastructure.Sessions;

/// <summary>
/// The session-target catalog over the <c>arcscope_session_target</c> table. A registration inserts a session or records
/// that it grew, in one immediate transaction; the table's triggers also refuse any shrink, identity change or delete, so
/// the grow-only rule holds even for a writer that bypassed this class.
/// </summary>
public sealed class SqliteSessionTargetCatalog : ISessionTargetCatalog
{
    private readonly ArcScopeStore owner;

    internal SqliteSessionTargetCatalog(ArcScopeStore owner) => this.owner = owner;

    public async ValueTask<SessionTarget?> FindAsync(SessionTargetId id, CancellationToken cancellationToken)
    {
        string key = StorageText.Identity(id.Value);
        var product = owner.Product;
        return await Task.Run(() => product.Read((connection, transaction) => Find(connection, transaction, key)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Outcome<SessionTarget>> RegisterAsync(SessionTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled<SessionTarget>(EffectCertainty.DidNotHappen);
        string key = StorageText.Identity(target.Id.Value);
        var product = owner.Product;
        // Once dispatched the transaction runs to its end: its effect is decided by its commit, not by cancellation.
        return await Task.Run(() => product.Write((connection, transaction) =>
        {
            var existing = Find(connection, transaction, key);
            if (existing is not null)
            {
                if (existing.SampleCount > target.SampleCount) return AnnotationFailures.Refuse<SessionTarget>(AnnotationFailures.InvalidTransition);
                if (existing.SampleCount == target.SampleCount) return Outcome.Success(existing);
                using var grow = ProductDatabase.Command(connection, transaction,
                    "UPDATE arcscope_session_target SET sample_count=$count WHERE session_id=$session");
                grow.Parameters.AddWithValue("$session", key);
                grow.Parameters.AddWithValue("$count", StorageText.Unsigned(target.SampleCount));
                if (grow.ExecuteNonQuery() != 1) throw new InvalidDataException("A session target registration must change exactly one row.");
                return Outcome.Success(target);
            }

            using var insert = ProductDatabase.Command(connection, transaction,
                "INSERT INTO arcscope_session_target(session_id,sample_count) VALUES($session,$count)");
            insert.Parameters.AddWithValue("$session", key);
            insert.Parameters.AddWithValue("$count", StorageText.Unsigned(target.SampleCount));
            insert.ExecuteNonQuery();
            return Outcome.Success(target);
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private static SessionTarget? Find(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string key)
    {
        using var query = ProductDatabase.Command(connection, transaction,
            "SELECT session_id,sample_count FROM arcscope_session_target WHERE session_id=$session");
        query.Parameters.AddWithValue("$session", key);
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        if (row.IsDBNull(0) || row.IsDBNull(1)) throw new InvalidDataException("A stored session target has a missing column.");
        var id = new SessionTargetId(StorageText.ReadIdentity(row.GetString(0)));
        if (!string.Equals(StorageText.Identity(id.Value), key, StringComparison.Ordinal))
            throw new InvalidDataException("A stored session target answered for another session.");
        return new SessionTarget(id, StorageText.ReadUnsigned(row.GetString(1)));
    }
}
