// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.ArcScope.Infrastructure.Storage;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Foundation.Errors;
using ArcForges.Foundation.Versions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcForges.ArcScope.Tests.Storage;

public sealed class ArcScopeStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OpeningCreatesTheArcScopeSchemaAndRecordsItsVersion()
    {
        using var fixture = new SqliteStoreFixture();

        var report = fixture.Store.OpenReport;

        Assert.Equal(new StorageSchemaVersion(1), report.SchemaVersion);
        Assert.True(report.WritesEnabled);
        Assert.Equal((0, 0, 0, 0), (report.IndexRowsRestored, report.IndexRowsRepaired, report.OrphanIndexRowsRemoved, report.UnreadableAnnotations));
        Assert.Equal("1", fixture.Raw("SELECT value FROM sys_meta WHERE key='storageSchemaVersion'"));
        Assert.Equal(1, fixture.RawCount("SELECT COUNT(*) FROM __arcforges_migration_history"));
        foreach (string name in new[] { "arcscope_session_target", "arcscope_annotation_index", "arcscope_annotation_index_by_session",
            "arcscope_session_target_only_grows", "arcscope_session_target_is_never_deleted", "arcscope_store_state" })
        {
            Assert.Equal(1, fixture.RawCount("SELECT COUNT(*) FROM sqlite_master WHERE name=$name", ("$name", name)));
        }

        var reopened = fixture.Reopen();
        Assert.Equal(new StorageSchemaVersion(1), reopened.SchemaVersion);
        Assert.Equal(1, fixture.RawCount("SELECT COUNT(*) FROM __arcforges_migration_history"));
    }

    [Fact]
    public void ASecondOwnerOfTheSameFileIsRefusedUntilTheFirstCloses()
    {
        using var fixture = new SqliteStoreFixture();

        Assert.ThrowsAny<IOException>(() => ArcScopeStore.Open(fixture.DatabasePath, fixture.StoreId));

        fixture.Store.Dispose();
        using var second = ArcScopeStore.Open(fixture.DatabasePath, fixture.StoreId);
        Assert.Equal(new StorageSchemaVersion(1), second.OpenReport.SchemaVersion);
    }

    [Fact]
    public void AFileOfAnotherStoreIdentityIsRefused()
    {
        using var fixture = new SqliteStoreFixture();
        fixture.Store.Dispose();

        Assert.Throws<InvalidOperationException>(() => ArcScopeStore.Open(fixture.DatabasePath, Guid.NewGuid()));

        // The refusal released the owner lock.
        using var owner = ArcScopeStore.Open(fixture.DatabasePath, fixture.StoreId);
        Assert.True(owner.OpenReport.WritesEnabled);
    }

    [Fact]
    public void ADatabaseFromANewerSchemaRefusesToOpenWithoutChanges()
    {
        using var fixture = new SqliteStoreFixture();
        fixture.Store.Dispose();
        fixture.Raw("INSERT INTO __arcforges_migration_history(version,step_id,checksum) VALUES(2,'0002_future','00'); " +
            "UPDATE sys_meta SET value='2' WHERE key='storageSchemaVersion';");

        Assert.Throws<InvalidOperationException>(() => ArcScopeStore.Open(fixture.DatabasePath, fixture.StoreId));
        Assert.Equal("2", fixture.Raw("SELECT value FROM sys_meta WHERE key='storageSchemaVersion'"));
        Assert.Equal(2, fixture.RawCount("SELECT COUNT(*) FROM __arcforges_migration_history"));
    }

    [Fact]
    public void AChangedAppliedMigrationRefusesToOpen()
    {
        using var fixture = new SqliteStoreFixture();
        fixture.Store.Dispose();
        fixture.Raw("UPDATE __arcforges_migration_history SET checksum='0000' WHERE version=1");

        Assert.Throws<InvalidOperationException>(() => ArcScopeStore.Open(fixture.DatabasePath, fixture.StoreId));
    }

    [Theory]
    [InlineData("DELETE FROM store_content")]
    [InlineData("UPDATE command_log SET version='x'")]
    [InlineData("INSERT INTO sync_outbox(sequence,command_id,payload) VALUES(1,'x',x'00')")]
    [InlineData("DELETE FROM journal")]
    [InlineData("UPDATE sys_meta SET value='9'")]
    [InlineData("DELETE FROM __arcforges_migration_history")]
    [InlineData("CREATE TABLE arcscope_extra(a INTEGER)")]
    [InlineData("DROP TABLE arcscope_annotation_index")]
    [InlineData("DROP TRIGGER arcscope_session_target_only_grows")]
    [InlineData("PRAGMA foreign_keys=OFF")]
    [InlineData("ATTACH DATABASE ':memory:' AS other")]
    public void ProductSqlCannotTouchTheSharedMechanismSchemaOrPragmas(string sql)
    {
        using var fixture = new SqliteStoreFixture();

        var refused = Assert.Throws<SqliteException>(() => fixture.Store.Product.Write((connection, transaction) =>
        {
            using var command = ProductDatabase.Command(connection, transaction, sql);
            return command.ExecuteNonQuery();
        }));

        Assert.Equal(23, refused.SqliteErrorCode); // SQLITE_AUTH
    }

    [Fact]
    public void ProductSqlCannotReadTheSharedMechanismTablesButReadsItsOwn()
    {
        using var fixture = new SqliteStoreFixture();

        var refused = Assert.Throws<SqliteException>(() => fixture.Store.Product.Read((connection, transaction) =>
        {
            using var command = ProductDatabase.Command(connection, transaction, "SELECT COUNT(*) FROM command_log");
            return command.ExecuteScalar();
        }));
        Assert.Equal(23, refused.SqliteErrorCode);

        long own = fixture.Store.Product.Read((connection, transaction) =>
        {
            using var command = ProductDatabase.Command(connection, transaction, "SELECT COUNT(*) FROM arcscope_session_target");
            return (long)command.ExecuteScalar()!;
        });
        Assert.Equal(0, own);
    }

    [Fact]
    public async Task ADisposedStoreRefusesEveryOperation()
    {
        using var fixture = new SqliteStoreFixture();
        var annotations = fixture.Store.Annotations;
        var sessions = fixture.Store.SessionTargets;
        fixture.Store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await annotations.ReadAsync(new AnnotationId(Guid.NewGuid()), Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sessions.FindAsync(fixture.Session, Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await sessions.RegisterAsync(new SessionTarget(fixture.Session, 1), Token));
        fixture.Store.Dispose();
    }

    [Fact]
    public async Task SessionTargetsRegisterGrowAndSurviveARestartButNeverShrink()
    {
        using var fixture = new SqliteStoreFixture();
        var sessions = fixture.Store.SessionTargets;

        Assert.Null(await sessions.FindAsync(fixture.Session, Token));
        Assert.Equal(100UL, OutcomeAssertSuccess(await sessions.RegisterAsync(new SessionTarget(fixture.Session, 100), Token)).SampleCount);
        Assert.Equal(100UL, OutcomeAssertSuccess(await sessions.RegisterAsync(new SessionTarget(fixture.Session, 100), Token)).SampleCount);
        Assert.Equal(ulong.MaxValue, OutcomeAssertSuccess(await sessions.RegisterAsync(new SessionTarget(fixture.Session, ulong.MaxValue), Token)).SampleCount);
        var shrink = await sessions.RegisterAsync(new SessionTarget(fixture.Session, 5), Token);
        Assert.True(shrink.TryGetFailure(out var failure));
        Assert.Equal(AnnotationFailures.InvalidTransition, failure.Code);

        fixture.Reopen();
        var found = await fixture.Store.SessionTargets.FindAsync(fixture.Session, Token);
        Assert.Equal(new SessionTarget(fixture.Session, ulong.MaxValue), found);
        Assert.Equal("18446744073709551615", fixture.Raw("SELECT sample_count FROM arcscope_session_target"));
    }

    [Fact]
    public async Task TheSessionTableRefusesAShrinkAnIdentityChangeOrADeleteFromAnyWriter()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync(500);
        string id = fixture.Session.Value.ToString("D");

        Assert.Contains("only grow", Assert.Throws<SqliteException>(() =>
            fixture.Raw("UPDATE arcscope_session_target SET sample_count='00000000000000000001'")).Message, StringComparison.Ordinal);
        Assert.Contains("only grow", Assert.Throws<SqliteException>(() =>
            fixture.Raw("UPDATE arcscope_session_target SET session_id=$id", ("$id", Guid.NewGuid().ToString("D")))).Message, StringComparison.Ordinal);
        Assert.Contains("never deleted", Assert.Throws<SqliteException>(() =>
            fixture.Raw("DELETE FROM arcscope_session_target")).Message, StringComparison.Ordinal);
        Assert.Throws<SqliteException>(() => fixture.Raw("INSERT INTO arcscope_session_target VALUES($id,'12')", ("$id", Guid.NewGuid().ToString("D"))));
        Assert.Throws<SqliteException>(() => fixture.Raw("INSERT INTO arcscope_session_target VALUES('NOT-A-UUID-NOT-A-UUID-NOT-A-UUID-XXX','00000000000000000001')"));
        Assert.Equal("00000000000000000500", fixture.Raw("SELECT sample_count FROM arcscope_session_target WHERE session_id=$id", ("$id", id)));
    }

    [Fact]
    public async Task ACorruptSessionRowFailsClosed()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync(500);
        fixture.Raw("PRAGMA ignore_check_constraints=ON; UPDATE arcscope_session_target SET sample_count='0000000000000000050x';");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.SessionTargets.FindAsync(fixture.Session, Token));
        var create = await fixture.WritePath.CreateAsync(fixture.Create(), Token);
        Assert.True(create.TryGetFailure(out var failure));
        Assert.Equal(AnnotationFailures.Unexpected, failure.Code);
        Assert.Equal(EffectCertainty.DidNotHappen, failure.Effect);
    }

    private static SessionTarget OutcomeAssertSuccess(Outcome<SessionTarget> outcome)
    {
        Assert.True(outcome.TryGetValue(out var value));
        return value!;
    }
}
