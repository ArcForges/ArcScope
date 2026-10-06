// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Application;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Persistence.Sqlite;
using ArcForges.Security;
using ArcForges.Security.Decisions;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class ProductAuthorityTests
{
    [Fact]
    public async Task SessionRefreshAndTerminalRevocationCannotResurrectAuthority()
    {
        using var fixture = new Fixture();
        var first = fixture.Snapshot(1);
        using var session = new ProductHostActorSession(first);
        var authority = fixture.Authority(session);
        Assert.NotNull(await authority.CurrentAsync(TestContext.Current.CancellationToken));
        session.Refresh(fixture.Snapshot(2, enabled: []));
        Assert.Equal(2UL, session.Generation);
        Assert.Empty((await authority.CurrentAsync(TestContext.Current.CancellationToken))!.EnabledCapabilities);
        session.Dispose();
        Assert.Equal(3UL, session.Generation);
        Assert.Null(await authority.CurrentAsync(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => session.Refresh(fixture.Snapshot(4)));
    }

    [Fact]
    public async Task ExpiryAndFutureLifetimeFailClosedOnActualHostSession()
    {
        using var fixture = new Fixture();
        using var expired = new ProductHostActorSession(fixture.Snapshot(1, from: 0, until: 100));
        Assert.Null(await fixture.Authority(expired).CurrentAsync(TestContext.Current.CancellationToken));
        using var future = new ProductHostActorSession(fixture.Snapshot(1, from: 101, until: 200));
        Assert.Null(await fixture.Authority(future).CurrentAsync(TestContext.Current.CancellationToken));
        using var valid = new ProductHostActorSession(fixture.Snapshot(1, from: 100, until: 101));
        Assert.NotNull(await fixture.Authority(valid).CurrentAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefreshRejectsIdentityScopeOriginAndGenerationSubstitution()
    {
        using var fixture = new Fixture();
        using var session = new ProductHostActorSession(fixture.Snapshot(1));
        Assert.Throws<ArgumentException>(() => session.Refresh(fixture.Snapshot(1)));
        var other = new ActorChain(fixture.Owner, fixture.Instance.Installation.DeviceId,
            new InstallationId(Guid.NewGuid()), new SessionId(Guid.NewGuid()), fixture.Instance.InstanceId, []);
        Assert.Throws<ArgumentException>(() => session.Refresh(new(other, fixture.Scope, DecisionOrigin.Local,
            TransportSessions.InProcess, 2, new(0, 0), new(200, 0), TrustVerdict.Verified, [], [])));
        Assert.Throws<ArgumentException>(() => session.Refresh(new(fixture.Chain,
            new DecisionScope(fixture.Owner.Realm, new WorkspaceId(Guid.NewGuid())), DecisionOrigin.Local,
            TransportSessions.InProcess, 2, new(0, 0), new(200, 0), TrustVerdict.Verified, [], [])));
        Assert.Throws<ArgumentException>(() => session.Refresh(new(fixture.Chain, fixture.Scope, DecisionOrigin.Remote,
            TransportSessions.InProcess, 2, new(0, 0), new(200, 0), TrustVerdict.Verified, [], [])));
        Assert.Equal(1UL, session.Generation);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelAnotherWaiterOrMultiplyHungHostReads()
    {
        using var fixture = new Fixture();
        var source = new PendingSource(fixture.Chain);
        var authority = fixture.Authority(source);
        using var cancelled = new CancellationTokenSource();
        var first = authority.CurrentAsync(cancelled.Token).AsTask();
        var second = authority.CurrentAsync(TestContext.Current.CancellationToken).AsTask();
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, source.Reads);
        source.Completion.SetResult(fixture.Snapshot(1));
        Assert.NotNull(await second);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task SupersededReplyCannotBecomeCurrentAuthority()
    {
        using var fixture = new Fixture();
        var source = new PendingSource(fixture.Chain);
        var pending = fixture.Authority(source).CurrentAsync(TestContext.Current.CancellationToken).AsTask();
        source.Generation = 2;
        source.Completion.SetResult(fixture.Snapshot(1));
        Assert.Null(await pending);
    }

    [Fact]
    public async Task ReplyFromAnotherBoundActorCannotBecomeCurrentAuthority()
    {
        using var fixture = new Fixture();
        var foreign = new ActorChain(fixture.Owner, fixture.Instance.Installation.DeviceId,
            new InstallationId(Guid.NewGuid()), new SessionId(Guid.NewGuid()), fixture.Instance.InstanceId, []);
        var source = new PendingSource(foreign);
        var pending = fixture.Authority(source).CurrentAsync(TestContext.Current.CancellationToken).AsTask();
        source.Completion.SetResult(fixture.Snapshot(1));
        Assert.Null(await pending);
    }

    [Fact]
    public void SnapshotRejectsForeignCapabilityAndPreservesImmutableCollections()
    {
        using var fixture = new Fixture();
        var enabled = new List<string> { "IScopeOperations.GetSession" };
        var snapshot = fixture.Snapshot(1, enabled: enabled);
        enabled.Clear();
        Assert.Single(snapshot.EnabledCapabilities);
        Assert.Throws<ArgumentException>(() => fixture.Snapshot(1, enabled: ["IScopeOperations.DeleteSession"]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "app02-authority-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteStore store;
        private readonly AnnotationSessionRepository repository;
        internal HumanPrincipal Owner { get; } = new(new RealmId(Guid.NewGuid()), new UserId(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        internal InstanceIdentity Instance { get; } = new(new InstallationIdentity(AppIdentity.ArcScope,
            new DeviceId(Guid.NewGuid()), new InstallationId(Guid.NewGuid())), new InstanceId(Guid.NewGuid()), 1);
        internal DecisionScope Scope { get; }
        internal ActorChain Chain { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            store = new(Path.Combine(directory, "authority.db"), Guid.NewGuid(), new StoreAuthorization());
            repository = new(store, Guid.NewGuid(), Owner.Id, new FixedClock());
            Scope = new(Owner.Realm, null);
            Chain = new(Owner, Instance.Installation.DeviceId, Instance.Installation.InstallationId,
                new SessionId(Guid.NewGuid()), Instance.InstanceId, []);
        }
        internal ProductHostActorSnapshot Snapshot(ulong generation, long from = 0, long until = 200,
            IEnumerable<string>? enabled = null) => new(Chain, Scope, DecisionOrigin.Local, TransportSessions.InProcess,
                generation, new(from, 0), new(until, 0), TrustVerdict.Verified, [], enabled ?? ["IScopeOperations.GetSession"]);
        internal ProductAuthority Authority(IProductHostActorSource source) => new(repository, Instance, Owner, Scope, new FixedClock(), source);
        public void Dispose() { store.Dispose(); Directory.Delete(directory, recursive: true); }
    }
    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => new(100, 0);
        public MonotonicTimestamp GetTimestamp() => Clock.System.GetTimestamp();
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => Clock.System.GetElapsedTime(start, finish);
    }
    // Only the unavailable host write authorization is faked; durability is actual SQLite.
    private sealed class StoreAuthorization : IStoreAuthorization { public bool CanWrite(WriteCommand command) => true; }
    private sealed class PendingSource(ActorChain actors) : IProductHostActorSource
    {
        internal TaskCompletionSource<ProductHostActorSnapshot?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        public ulong Generation { get; set; } = 1;
        public ActorChain BoundActors => actors;
        public DecisionOrigin BoundOrigin => DecisionOrigin.Local;
        public ValueTask<ProductHostActorSnapshot?> ReadCurrentAsync(CancellationToken cancellationToken)
        { Reads++; Entered.TrySetResult(); return new(Completion.Task); }
    }
}
