// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Execution;
using ArcForges.Persistence.Sqlite;
using ArcForges.Security;
using ArcForges.Security.Approvals;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class ProductSecurityStoreTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LeaseEndAndOwedAuditFactSurviveRestartWithoutResurrection()
    {
        using var database = new Database();
        var lease = database.Lease();
        using (var sqlite = database.Open())
        {
            var store = database.Leases(sqlite);
            Assert.True(await store.TryCreateAsync(lease, Cancellation));
            Assert.False(await store.TryCreateAsync(lease, Cancellation));
            Assert.True(await store.TryReplaceAsync(lease.Id, 1, End(lease, LeaseState.Expired, lease.ExpiresAt), Cancellation));
        }
        using var reopened = database.Open();
        var actual = database.Leases(reopened);
        var ended = await actual.ReadAsync(lease.Id, Cancellation);
        Assert.NotNull(ended);
        Assert.Equal(LeaseState.Expired, ended.State);
        Assert.Equal(2, ended.Version);
        Assert.Equal(lease.ResourceIds, ended.ResourceIds);
        Assert.Equal(lease.Holder, ended.Holder);
        Assert.Single(await actual.ListUnsettledAsync(lease.Task, lease.ExpiresAt, 100, Cancellation));
        var recorded = Copy(ended, 3, LeaseState.Expired, ended.EndedAt, recorded: true);
        Assert.True(await actual.TryReplaceAsync(lease.Id, 2, recorded, Cancellation));
        Assert.Empty(await actual.ListUnsettledAsync(lease.Task, lease.ExpiresAt, 100, Cancellation));
        Assert.False(await actual.TryReplaceAsync(lease.Id, 3, Copy(lease, 4), Cancellation));
    }

    [Fact]
    public async Task LeaseCoverageCannotWidenAndPrematureExpiryCannotCommit()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        var store = database.Leases(sqlite);
        var lease = database.Lease();
        Assert.True(await store.TryCreateAsync(lease, Cancellation));
        var ended = End(lease, LeaseState.TaskEnded, new(101, 0));
        Assert.False(await store.TryReplaceAsync(lease.Id, 1, Copy(ended, 2, ended.State, ended.EndedAt,
            resources: ["session:other"]), Cancellation));
        Assert.False(await store.TryReplaceAsync(lease.Id, 1, End(lease, LeaseState.Expired, new(101, 0)), Cancellation));
        Assert.Equal(LeaseState.Active, (await store.ReadAsync(lease.Id, Cancellation))!.State);
        Assert.Single(sqlite.ReadJournal(null, 100));
    }

    [Fact]
    public async Task TwoLeaseOwnersHaveExactlyOneTerminalTransition()
    {
        using var database = new Database();
        using var first = database.Open();
        using var second = database.Open();
        var left = database.Leases(first);
        var right = database.Leases(second);
        var lease = database.Lease();
        Assert.True(await left.TryCreateAsync(lease, Cancellation));
        var results = await Task.WhenAll(
            Task.Run(async () => await left.TryReplaceAsync(lease.Id, 1, End(lease, LeaseState.TaskEnded, new(101, 0)), Cancellation)),
            Task.Run(async () => await right.TryReplaceAsync(lease.Id, 1, End(lease, LeaseState.Revoked, new(101, 0)), Cancellation)));
        Assert.Single(results, value => value);
        Assert.Equal(2, (await left.ReadAsync(lease.Id, Cancellation))!.Version);
        Assert.Equal(2, first.ReadJournal(null, 100).Count);
    }

    [Fact]
    public async Task ApprovalCompetingDecisionsCommitOnceAndPreserveActionBindingOnReopen()
    {
        using var database = new Database();
        var pending = database.Approval();
        using (var first = database.Open())
        using (var second = database.Open())
        {
            var left = database.Approvals(first);
            var right = database.Approvals(second);
            Assert.True(await left.TryCreatePendingAsync(pending, Cancellation));
            var results = await Task.WhenAll(
                Task.Run(async () => await left.TryResolveAsync(pending.ApprovalId, 1, Resolve(pending, database.Owner, ApprovalDecisionKind.Approve), Cancellation)),
                Task.Run(async () => await right.TryResolveAsync(pending.ApprovalId, 1, Resolve(pending, database.Owner, ApprovalDecisionKind.Deny), Cancellation)));
            Assert.Single(results, value => value);
        }
        using var reopened = database.Open();
        var store = database.Approvals(reopened);
        var actual = await store.ReadAsync(pending.ApprovalId, Cancellation);
        Assert.NotNull(actual);
        Assert.Equal(2, actual.Version);
        Assert.Equal(pending.CommandId, actual.CommandId);
        Assert.Equal(pending.TargetRevision, actual.TargetRevision);
        Assert.Equal(pending.EffectSha256, actual.EffectSha256);
        Assert.Equal(database.Owner, actual.Decision!.DecidedBy);
        Assert.False(await store.TryResolveAsync(pending.ApprovalId, 2, Resolve(pending, database.Owner, ApprovalDecisionKind.Approve, version: 3), Cancellation));
    }

    [Fact]
    public async Task ForeignApprovalDecisionAndChangedActionAreRefusedWithoutMutation()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        var store = database.Approvals(sqlite);
        var pending = database.Approval();
        Assert.True(await store.TryCreatePendingAsync(pending, Cancellation));
        var foreign = new HumanPrincipal(database.Owner.Realm, new(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        Assert.False(await store.TryResolveAsync(pending.ApprovalId, 1, Resolve(pending, foreign, ApprovalDecisionKind.Approve), Cancellation));
        Assert.False(await store.TryResolveAsync(pending.ApprovalId, 1, Resolve(pending, database.Owner, ApprovalDecisionKind.Approve,
            resource: "session:substituted"), Cancellation));
        Assert.Equal(ApprovalState.Pending, (await store.ReadAsync(pending.ApprovalId, Cancellation))!.State);
        Assert.Single(sqlite.ReadJournal(null, 100));
    }

    [Fact]
    public async Task CorruptedStoredForeignDecisionCannotBeReadAsAnOwnerApproval()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        var pending = database.Approval();
        var raw = new DurableRegistry(sqlite, "arcscope.security.approvals.v1", database.Partition, database.Owner.Id, Clock.System);
        Assert.True(await raw.TryCreateAsync(new(pending.ApprovalId, 1, SecurityCodec.EncodeApproval(pending)), Cancellation));
        var foreign = new HumanPrincipal(database.Owner.Realm, new(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        var corrupt = Resolve(pending, foreign, ApprovalDecisionKind.Approve);
        Assert.True(await raw.TryReplaceAsync(new(pending.ApprovalId, 2, SecurityCodec.EncodeApproval(corrupt)), 1, Cancellation));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await database.Approvals(sqlite).ReadAsync(pending.ApprovalId, Cancellation));
    }

    [Fact]
    public async Task CancellationAndOwnerMismatchCannotCreateSecurityRecords()
    {
        using var database = new Database();
        using var sqlite = database.Open();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await database.Leases(sqlite).TryCreateAsync(database.Lease(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await database.Approvals(sqlite).TryCreatePendingAsync(database.Approval(), cancelled.Token));
        var foreign = new HumanPrincipal(database.Owner.Realm, new(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        Assert.False(await new ProductLeaseStore(sqlite, database.Partition, foreign, Clock.System).TryCreateAsync(database.Lease(), Cancellation));
        Assert.False(await new ProductApprovalStore(sqlite, database.Partition, foreign, Clock.System).TryCreatePendingAsync(database.Approval(), Cancellation));
        Assert.Empty(sqlite.ReadJournal(null, 100));
    }

    private static CapabilityLease End(CapabilityLease lease, LeaseState state, Instant at) =>
        Copy(lease, 2, state, at);

    private static CapabilityLease Copy(CapabilityLease lease, long version, LeaseState state = LeaseState.Active,
        Instant? ended = null, bool recorded = false, string[]? resources = null) => new(
        lease.Id, lease.Owner, lease.Scope, lease.Task, lease.Holder, lease.CapabilityKey, resources ?? [.. lease.ResourceIds],
        lease.EffectiveRisk, lease.Origin, lease.Basis, lease.IssuedBy, lease.IssuedAt, lease.ExpiresAt, state, version,
        ended, state == LeaseState.Revoked ? LeaseRevocationReason.OwnerRevoked : LeaseRevocationReason.None, recorded);

    private static ApprovalSnapshot Resolve(ApprovalSnapshot pending, HumanPrincipal human, ApprovalDecisionKind kind,
        long version = 2, string? resource = null)
    {
        var at = new Instant(101, 0);
        var decision = new ApprovalDecision(Guid.NewGuid(), kind, human, new(Guid.NewGuid()), ApprovalOrigin.Local, at);
        return new(pending.ApprovalId, pending.CommandId, pending.Owner, pending.OperationId,
            resource ?? pending.TargetResourceId, pending.TargetRevision, pending.EffectSha256, pending.EffectiveRisk,
            pending.RequestedAt, pending.ExpiresAt, kind == ApprovalDecisionKind.Approve ? ApprovalState.Approved : ApprovalState.Denied,
            version, at, decision);
    }

    private sealed class Database : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "af-security-" + Guid.NewGuid().ToString("N"));
        private readonly Guid storeId = Guid.NewGuid();
        internal Guid Partition { get; } = Guid.NewGuid();
        internal HumanPrincipal Owner { get; } = new(new(Guid.NewGuid()), new(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        internal Database() => Directory.CreateDirectory(directory);
        internal SqliteStore Open() => new(Path.Combine(directory, "owner.db"), storeId, new TestAuthorization());
        internal ProductLeaseStore Leases(IStore store) => new(store, Partition, Owner, Clock.System);
        internal ProductApprovalStore Approvals(IStore store) => new(store, Partition, Owner, Clock.System);
        internal CapabilityLease Lease()
        {
            var chain = new ActorChain(Owner, new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid()), []);
            return new(CapabilityLeaseId.New(), Owner, new DecisionScope(Owner.Realm, null), new TaskId(Guid.NewGuid()),
                new LeaseHolder(ActorKind.Agent, Guid.NewGuid()), "IScopeOperations.CreateAnnotation", ["session:owned"],
                RiskLevel.R1, DecisionOrigin.Local, LeaseIssueBasis.PolicyAllowed, chain, new(100, 0), new(200, 0), LeaseState.Active, 1);
        }
        internal ApprovalSnapshot Approval() => new(Guid.NewGuid(), new(Guid.NewGuid()), Owner,
            "IScopeOperations.CreateAnnotation", "session:owned", "1", new string('A', 64), RiskLevel.R1,
            new(100, 0), new(200, 0), ApprovalState.Pending, 1);
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
    // Storage is actual SQLite. Authentication is tested at the product composition boundary separately.
    private sealed class TestAuthorization : IStoreAuthorization { public bool CanWrite(WriteCommand command) => true; }
}
