// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Application;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Persistence.Sqlite;
using Google.Protobuf;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class ProductInvocationRecordStoreTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static ByteString Fingerprint(byte value = 0) => ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    [Fact]
    public async Task ExactSuccessSurvivesRestartAndNeverInvokesAnyOwnerDuringReplay()
    {
        using var database = new Database();
        var command = UuidBoundary.ToWire(Guid.NewGuid());
        var response = new ScopeOperationsServiceCreateAnnotationResponse
        {
            Value = new() { Revision = new() { Value = ulong.MaxValue } },
            Meta = new() { CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()) },
        };
        var encoded = AnnotationOperationCodec.Encode(response);
        using (var sqlite = database.Open())
        using (var store = database.Journal(sqlite))
        {
            var begun = Value(await store.BeginAsync(command, Fingerprint(), Cancellation));
            Assert.Equal(InvocationRecordClaimKind.Execute, begun.Kind);
            var value = Value(database.Binding.RestoreResult(encoded, InvocationResultVersion.FromNativeContentRev(response.Value.Revision)));
            var outcome = Value(await store.CompleteAsync(begun, InvocationOutcome.Success(value), Cancellation));
            Assert.Equal(encoded, outcome.Value!.Result);
            response.Value.Revision.Value = 1;
        }
        using var reopened = database.Open();
        using var actual = database.Journal(reopened);
        var replay = Value(await actual.BeginAsync(command, Fingerprint(), Cancellation));
        Assert.Equal(InvocationRecordClaimKind.Replay, replay.Kind);
        Assert.Equal(encoded, replay.ReplayOutcome!.Value!.Result);
        Assert.Equal(ulong.MaxValue, replay.ReplayOutcome.Value.Version.NativeContentRev!.Value);
        Assert.Equal(2, reopened.ReadJournal(null, 100).Count);
    }

    [Fact]
    public async Task PendingCommandAfterRestartHasUnknownEffectAndCannotExecuteAgain()
    {
        using var database = new Database();
        var command = UuidBoundary.ToWire(Guid.NewGuid());
        using (var sqlite = database.Open())
        using (var journal = database.Journal(sqlite))
            Assert.Equal(InvocationRecordClaimKind.Execute, Value(await journal.BeginAsync(command, Fingerprint(), Cancellation)).Kind);
        using var reopened = database.Open();
        using var actual = database.Journal(reopened);
        var replay = Value(await actual.BeginAsync(command, Fingerprint(), Cancellation));
        Assert.Equal(InvocationRecordClaimKind.Replay, replay.Kind);
        Assert.Equal(OutcomeKind.Cancelled, replay.ReplayOutcome!.Kind);
        Assert.Equal(EffectCertainty.Unknown, replay.ReplayOutcome.CancellationEffect);
        Assert.Single(reopened.ReadJournal(null, 100));
    }

    [Fact]
    public async Task LocalConcurrentDuplicateWaitsForOneCommittedFailureAndChangedFingerprintIsRefused()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        using var journal = database.Journal(sqlite);
        var command = UuidBoundary.ToWire(Guid.NewGuid());
        var claim = Value(await journal.BeginAsync(command, Fingerprint(), Cancellation));
        var duplicate = journal.BeginAsync(command, Fingerprint(), Cancellation).AsTask();
        Assert.False(duplicate.IsCompleted);
        var changed = await journal.BeginAsync(command, Fingerprint(1), Cancellation);
        Assert.Equal(OutcomeKind.Failure, changed.Kind);
        Assert.True(changed.TryGetFailure(out var changedFailure));
        Assert.Equal("command.reused_identifier", changedFailure!.ToWire().Code);
        var failure = InvocationOutcome.FailureResult(TypedFailure.Create("perm.resource_denied"));
        Assert.Equal(OutcomeKind.Failure, Value(await journal.CompleteAsync(claim, failure, Cancellation)).Kind);
        var replay = Value(await duplicate);
        Assert.Equal(InvocationRecordClaimKind.Replay, replay.Kind);
        Assert.Equal(failure.Failure!.ToWire(), replay.ReplayOutcome!.Failure!.ToWire());
        Assert.Equal(2, sqlite.ReadJournal(null, 100).Count);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelExecutingReservationAndDisposeReleasesOtherWaiters()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        var journal = database.Journal(sqlite);
        var command = UuidBoundary.ToWire(Guid.NewGuid());
        _ = Value(await journal.BeginAsync(command, Fingerprint(), Cancellation));
        using var cancelled = new CancellationTokenSource();
        var waiting = journal.BeginAsync(command, Fingerprint(), cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var other = journal.BeginAsync(command, Fingerprint(), Cancellation).AsTask();
        journal.Dispose();
        var replay = Value(await other);
        Assert.Equal(EffectCertainty.Unknown, replay.ReplayOutcome!.CancellationEffect);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await journal.BeginAsync(command, Fingerprint(), Cancellation));
        Assert.Single(sqlite.ReadJournal(null, 100));
        Assert.NotNull(sqlite.Read("arcscope.capability.invocations.v1", database.Partition));
    }

    [Fact]
    public async Task CompletionIsDurableIdempotentAndDifferentTerminalOutcomeIsRefused()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        using var journal = database.Journal(sqlite);
        var command = UuidBoundary.ToWire(Guid.NewGuid());
        var claim = Value(await journal.BeginAsync(command, Fingerprint(), Cancellation));
        var outcome = InvocationOutcome.Cancelled(EffectCertainty.Happened);
        sqlite.Committed += (_, _) => throw new IOException("Notification after durable commit");
        Assert.Equal(EffectCertainty.Happened, Value(await journal.CompleteAsync(claim, outcome, Cancellation)).CancellationEffect);
        Assert.Equal(EffectCertainty.Happened, Value(await journal.CompleteAsync(claim, outcome, Cancellation)).CancellationEffect);
        Assert.Equal(OutcomeKind.Failure, (await journal.CompleteAsync(claim, InvocationOutcome.Cancelled(EffectCertainty.DidNotHappen), Cancellation)).Kind);
        Assert.Equal(2, sqlite.ReadJournal(null, 100).Count);
    }

    [Fact]
    public async Task InvalidFingerprintAndCancellationCannotReserve()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        using var journal = database.Journal(sqlite);
        Assert.Equal(OutcomeKind.Failure, (await journal.BeginAsync(UuidBoundary.ToWire(Guid.NewGuid()), ByteString.Empty, Cancellation)).Kind);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await journal.BeginAsync(UuidBoundary.ToWire(Guid.NewGuid()), Fingerprint(), cancelled.Token));
        Assert.Empty(sqlite.ReadJournal(null, 100));
    }

    private static T Value<T>(Outcome<T> outcome) where T : class
    {
        Assert.True(outcome.TryGetValue(out var value));
        Assert.NotNull(value);
        return value;
    }

    private sealed class Database : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "af-journal-" + Guid.NewGuid().ToString("N"));
        private readonly Guid storeId = Guid.NewGuid();
        private readonly UserId actor = new(Guid.NewGuid());
        private readonly CapabilityRegistry catalogue = CapabilityRegistry.CreateInitial();
        internal Guid Partition { get; } = Guid.NewGuid();
        internal CapabilityInvocationBinding Binding { get; }
        internal Database()
        {
            Directory.CreateDirectory(directory);
            Binding = new CapabilityInvocationBinding<string, string>(catalogue, "IScopeOperations.CreateAnnotation", InvocationResultVersionKind.NativeContentRev,
                _ => throw new InvalidOperationException("Journal replay must not decode a request."),
                (_, _, _, _, _) => throw new InvalidOperationException("Journal replay must not execute an owner."),
                _ => throw new InvalidOperationException("Journal replay must not encode a new response."),
                _ => throw new InvalidOperationException("Journal replay must not derive a new version."));
        }
        internal SqliteStore Open() => new(Path.Combine(directory, "owner.db"), storeId, new TestAuthorization());
        internal ProductInvocationRecordStore Journal(IStore store) => new(store, Partition, actor, Clock.System, catalogue, [Binding]);
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
    // Only unavailable product authority is faked. Real SQLite and exact published binding validate stored data.
    private sealed class TestAuthorization : IStoreAuthorization { public bool CanWrite(WriteCommand command) => true; }
}
