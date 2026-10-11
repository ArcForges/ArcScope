// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ArcForges.ArcScope.Infrastructure.Storage;

/// <summary>
/// Connections to ArcScope's own tables in the store file. Every connection carries a SQLite authorizer that admits only
/// reads and row changes of <c>arcscope_*</c> tables, transactions and scalar functions: product SQL can never read or change
/// the shared mechanism's tables (content, origins, journal, command log, outbox, migration history), create or drop schema,
/// attach another database or change a pragma. Schema changes happen only through the numbered migrations.
/// </summary>
internal sealed class ProductDatabase
{
    internal const string TablePrefix = "arcscope_";

    // Kept in a static field so the native callback can never outlive its delegate.
    private static readonly strdelegate_authorizer Authorizer = Authorize;

    private readonly string connectionString;

    internal ProductDatabase(string fullPath)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Runs <paramref name="action"/> in a read-only snapshot transaction.</summary>
    internal T Read<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = Open(readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return action(connection, transaction);
    }

    /// <summary>Runs <paramref name="action"/> in an immediate write transaction and commits it when it returns.</summary>
    internal T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = Open(readOnly: false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = action(connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>Creates a command on the transaction. Every caller passes constant SQL with parameter-bound values.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Internal sink: every caller passes a constant SQL literal and binds all values as parameters; the authorizer also confines it to ArcScope tables.")]
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static bool IsProductTable(string? table) =>
        table is not null && table.StartsWith(TablePrefix, StringComparison.OrdinalIgnoreCase);

    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = readOnly
                    ? "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA query_only=ON;"
                    : "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
                pragmas.ExecuteNonQuery();
            }

            int result = raw.sqlite3_set_authorizer(connection.Handle, Authorizer, null);
            if (result != raw.SQLITE_OK) throw new InvalidOperationException("The product connection guard could not be installed.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static int Authorize(object state, int action, string first, string second, string database, string source)
    {
        if (action == raw.SQLITE_READ || action == raw.SQLITE_INSERT || action == raw.SQLITE_UPDATE || action == raw.SQLITE_DELETE)
        {
            return IsProductTable(first) ? raw.SQLITE_OK : raw.SQLITE_DENY;
        }

        return action == raw.SQLITE_SELECT || action == raw.SQLITE_FUNCTION || action == raw.SQLITE_TRANSACTION
            ? raw.SQLITE_OK
            : raw.SQLITE_DENY;
    }
}
