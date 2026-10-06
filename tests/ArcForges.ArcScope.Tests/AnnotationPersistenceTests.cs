// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Persistence.Sqlite;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class AnnotationPersistenceTests
{
    [Fact]
    public async Task AtomicAppendSurvivesReopenWithMetadataAndReceipt()
    {
        using var fixture = new Database();
        var session = Guid.NewGuid();
        var command = Guid.NewGuid();
        var annotation = Guid.NewGuid();
        using (var store = fixture.Open())
        {
            var repository = fixture.Repository(store);
            var metadata = new ScopeMetadata { SessionId = UuidBoundary.ToWire(session), Name = "真实笔记" };
            metadata.Captures.Add(new CaptureMetadata { CaptureId = UuidBoundary.ToWire(Guid.NewGuid()), SampleCount = ulong.MaxValue });
            Assert.True(await repository.TryCreateAsync(session, metadata, TestContext.Current.CancellationToken));
            var note = Note(annotation, "persist");
            Assert.True(await repository.TryAppendAsync(repository.Read(session)!, note, command, new byte[32], TestContext.Current.CancellationToken));
            note.Text = "changed caller copy";
        }
        using var reopened = fixture.Open();
        var actual = fixture.Repository(reopened).Read(session)!;
        Assert.Equal(2, actual.Version);
        Assert.Equal("真实笔记", actual.Metadata.Name);
        Assert.Equal(ulong.MaxValue, Assert.Single(actual.Metadata.Captures).SampleCount);
        Assert.Equal("persist", Assert.Single(actual.Metadata.Annotations).Text);
        Assert.Equal(command, Assert.Single(actual.Receipts).CommandId);
        Assert.Equal(2, actual.Receipts[0].CommittedVersion);
        Assert.Equal(2, reopened.ReadJournal(null, 100).Count);
    }

    [Fact]
    public async Task ConcurrentOwnersCannotBothCommitTheSameSessionRevision()
    {
        using var fixture = new Database();
        using var first = fixture.Open();
        using var second = fixture.Open();
        var left = fixture.Repository(first);
        var right = fixture.Repository(second);
        var session = Guid.NewGuid();
        Assert.True(await left.TryCreateAsync(session, new() { SessionId = UuidBoundary.ToWire(session) }, TestContext.Current.CancellationToken));
        var beforeLeft = left.Read(session)!;
        var beforeRight = right.Read(session)!;
        var outcomes = await Task.WhenAll(
            Task.Run(async () => await left.TryAppendAsync(beforeLeft, Note(Guid.NewGuid(), "left"), Guid.NewGuid(), new byte[32], TestContext.Current.CancellationToken)),
            Task.Run(async () => await right.TryAppendAsync(beforeRight, Note(Guid.NewGuid(), "right"), Guid.NewGuid(), new byte[32], TestContext.Current.CancellationToken)));
        Assert.Single(outcomes, value => value);
        Assert.Single(left.Read(session)!.Metadata.Annotations);
        Assert.Single(left.Read(session)!.Receipts);
    }

    [Fact]
    public async Task PostCommitNotificationFailureReconcilesTheExactCommandWithoutRepeatingEffect()
    {
        using var fixture = new Database();
        using var store = fixture.Open();
        store.Committed += (_, _) => throw new InvalidOperationException("Notification failed after SQLite commit");
        var repository = fixture.Repository(store);
        var session = Guid.NewGuid();
        Assert.True(await repository.TryCreateAsync(session, new() { SessionId = UuidBoundary.ToWire(session) }, TestContext.Current.CancellationToken));
        Assert.Equal(1, repository.Read(session)!.Version);
        Assert.Single(store.ReadJournal(null, 100));
    }

    [Fact]
    public async Task CancellationAfterCommitReportsTheDurableMutation()
    {
        using var fixture = new Database();
        using var store = fixture.Open();
        using var cancellation = new CancellationTokenSource();
        store.Committed += (_, _) => cancellation.Cancel();
        var repository = fixture.Repository(store);
        var session = Guid.NewGuid();
        Assert.True(await repository.TryCreateAsync(session, new() { SessionId = UuidBoundary.ToWire(session) }, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotNull(repository.Read(session));
        Assert.Single(store.ReadJournal(null, 100));
    }

    [Fact]
    public async Task UnknownOriginRemainsInheritedAfterALaterHumanAppend()
    {
        using var fixture = new Database();
        using var store = fixture.Open();
        var repository = fixture.Repository(store);
        var session = Guid.NewGuid();
        var metadata = new ScopeMetadata { SessionId = UuidBoundary.ToWire(session) };
        var unknown = Note(Guid.NewGuid(), "uncertain imported content");
        unknown.Origin.Kinds.Clear();
        unknown.Origin.Kinds.Add("unknown");
        unknown.Origin.ProducerKind = "import";
        metadata.Annotations.Add(unknown);
        Assert.True(await repository.TryCreateAsync(session, metadata, TestContext.Current.CancellationToken));
        Assert.True(await repository.TryAppendAsync(repository.Read(session)!, Note(Guid.NewGuid(), "human"), Guid.NewGuid(), new byte[32], TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "nonAi", "unknown" }, store.Read("arcscope.annotation.sessions.v1", fixture.Partition)!.Origin.Kinds);
        Assert.Equal("unknown", repository.Read(session)!.Metadata.Annotations[0].Origin.Kinds.Single());
    }

    [Fact]
    public async Task CancellationBeforeWriteLeavesNoPartialMetadataOrReceipt()
    {
        using var fixture = new Database();
        using var store = fixture.Open();
        var repository = fixture.Repository(store);
        var session = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.TryCreateAsync(session, new() { SessionId = UuidBoundary.ToWire(session) }, cancellation.Token));
        Assert.Null(repository.Read(session));
        Assert.Empty(store.ReadJournal(null, 100));
    }

    [Fact]
    public async Task CapacityRefusalPreservesAllExistingAnnotationsAndReceipts()
    {
        using var fixture = new Database();
        using var store = fixture.Open();
        var repository = fixture.Repository(store);
        var session = Guid.NewGuid();
        var metadata = new ScopeMetadata { SessionId = UuidBoundary.ToWire(session) };
        for (var index = 0; index < 128; index++) metadata.Annotations.Add(Note(Guid.NewGuid(), "retained-" + index));
        Assert.True(await repository.TryCreateAsync(session, metadata, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.TryAppendAsync(repository.Read(session)!, Note(Guid.NewGuid(), "overflow"), Guid.NewGuid(), new byte[32], TestContext.Current.CancellationToken));
        Assert.Equal(128, repository.Read(session)!.Metadata.Annotations.Count);
        Assert.Equal(1, repository.Read(session)!.Version);
        Assert.Empty(repository.Read(session)!.Receipts);
    }

    [Fact]
    public async Task SharedPartitionHasDistinctImmutableContentUnitsForEachRegistry()
    {
        using var fixture = new Database();
        using var store = fixture.Open();
        var first = new DurableRegistry(store, "first", fixture.Partition, fixture.Actor, Clock.System);
        var second = new DurableRegistry(store, "second", fixture.Partition, fixture.Actor, Clock.System);
        Assert.True(await first.TryCreateAsync(new(Guid.NewGuid(), 1, [1]), TestContext.Current.CancellationToken));
        Assert.True(await second.TryCreateAsync(new(Guid.NewGuid(), 1, [2]), TestContext.Current.CancellationToken));
        Assert.NotEqual(store.Read("first", fixture.Partition)!.Origin.ContentUnitId,
            store.Read("second", fixture.Partition)!.Origin.ContentUnitId);
    }

    private static ScopeAnnotation Note(Guid id, string text)
    {
        var origin = new ArcForges.Contracts.Foundation.V1.ContentOrigin
        {
            Profile = "arcforges.content-origin.v1", OriginId = new ContentOriginId(Guid.NewGuid()).ToWire(),
            ContentUnitId = new ContentUnitId(id).ToWire(), ProducerKind = "human", OmittedParentCount = 0,
            PayloadSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),
        };
        origin.Kinds.Add("nonAi");
        return new() { AnnotationId = UuidBoundary.ToWire(id), Text = text, Range = new() { From = 0, Count = 0 }, Origin = origin };
    }

    private sealed class Database : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "af-app02-" + Guid.NewGuid().ToString("N"));
        private readonly Guid storeId = Guid.NewGuid();
        internal Guid Partition { get; } = Guid.NewGuid();
        internal UserId Actor { get; } = new(Guid.NewGuid());
        internal Database() => Directory.CreateDirectory(directory);
        internal SqliteStore Open() => new(Path.Combine(directory, "owner.db"), storeId, new TestAuthorization());
        internal AnnotationSessionRepository Repository(IStore store) => new(store, Partition, Actor, Clock.System);
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
    // Only the host authority dependency is unavailable in this storage component test; the storage is actual SQLite.
    private sealed class TestAuthorization : IStoreAuthorization { public bool CanWrite(WriteCommand command) => true; }
}
