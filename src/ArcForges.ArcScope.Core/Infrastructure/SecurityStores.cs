// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Execution;
using ArcForges.Persistence.Sqlite;
using ArcForges.Security;
using ArcForges.Security.Approvals;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;

namespace ArcForges.ArcScope.Core.Infrastructure;

/// <summary>The product's durable lease store; coverage and terminal lifecycle are immutable across restarts.</summary>
public sealed class ProductLeaseStore : ILeaseStore
{
    private readonly DurableRegistry registry;
    private readonly HumanPrincipal owner;

    public ProductLeaseStore(IStore store, Guid partitionId, HumanPrincipal owner, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(clock);
        if (partitionId == Guid.Empty) throw new ArgumentException("A partition identity is required.", nameof(partitionId));
        this.owner = owner;
        registry = new(store, "arcscope.security.leases.v1", partitionId, owner.Id, clock);
    }

    public ValueTask<CapabilityLease?> ReadAsync(CapabilityLeaseId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = registry.Read(id.Value);
        return ValueTask.FromResult(entry is null ? null : Decode(entry));
    }

    public ValueTask<bool> TryCreateAsync(CapabilityLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Owner != owner || lease.State != LeaseState.Active || lease.Version != 1) return ValueTask.FromResult(false);
        return registry.TryCreateAsync(new(lease.Id.Value, lease.Version, SecurityCodec.EncodeLease(lease)), cancellationToken);
    }

    public async ValueTask<bool> TryReplaceAsync(CapabilityLeaseId id, long expectedVersion, CapabilityLease replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedVersion <= 0 || expectedVersion == long.MaxValue || replacement.Id != id ||
            replacement.Version != expectedVersion + 1 || replacement.Owner != owner) return false;
        var current = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Version != expectedVersion || !SameCoverage(current, replacement)) return false;
        var legal = current.State == LeaseState.Active
            ? (replacement.State is LeaseState.Revoked or LeaseState.Expired or LeaseState.TaskEnded) && !replacement.EndEventRecorded &&
              (replacement.State != LeaseState.Expired || replacement.EndedAt >= replacement.ExpiresAt)
            : current.State == replacement.State && !current.EndEventRecorded && replacement.EndEventRecorded &&
              current.EndedAt == replacement.EndedAt && current.RevocationReason == replacement.RevocationReason;
        if (!legal) return false;
        return await registry.TryReplaceAsync(new(id.Value, replacement.Version, SecurityCodec.EncodeLease(replacement)),
            expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<CapabilityLease>> ListUnsettledAsync(TaskId? task, Instant? dueAt, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        var records = registry.ReadAll().Select(Decode)
            .Where(lease => (task is null || lease.Task == task.Value) &&
                (lease.State == LeaseState.Active ? dueAt is null || lease.ExpiresAt <= dueAt.Value : !lease.EndEventRecorded))
            .OrderBy(lease => lease.Id.Value).Take(limit).ToArray();
        return ValueTask.FromResult<IReadOnlyList<CapabilityLease>>(records);
    }

    private CapabilityLease Decode(DurableRegistry.Entry entry)
    {
        var lease = SecurityCodec.DecodeLease(entry.Payload);
        if (lease.Owner != owner || lease.Id.Value != entry.Id || lease.Version != entry.Version)
            throw new InvalidDataException("The lease record does not match its owner, identity or version.");
        if (lease.Version != (lease.State == LeaseState.Active ? 1 : lease.EndEventRecorded ? 3 : 2))
            throw new InvalidDataException("The lease version does not match its closed lifecycle.");
        if (lease.State == LeaseState.Expired && lease.EndedAt < lease.ExpiresAt)
            throw new InvalidDataException("An expired lease cannot carry an end timestamp before its expiry.");
        return lease;
    }

    private static bool SameCoverage(CapabilityLease left, CapabilityLease right) =>
        left.Owner == right.Owner && left.Scope == right.Scope && left.Task == right.Task && left.Holder == right.Holder &&
        left.CapabilityKey == right.CapabilityKey && left.ResourceIds.SequenceEqual(right.ResourceIds, StringComparer.Ordinal) &&
        left.EffectiveRisk == right.EffectiveRisk && left.Origin == right.Origin && left.Basis == right.Basis &&
        left.IssuedAt == right.IssuedAt && left.ExpiresAt == right.ExpiresAt &&
        ActorChainSnapshot.Encode(left.IssuedBy).AsSpan().SequenceEqual(ActorChainSnapshot.Encode(right.IssuedBy));
}

/// <summary>Product-owned approvals preserve their exact action binding and commit legal resolutions once.</summary>
public sealed class ProductApprovalStore : IApprovalStore
{
    private readonly DurableRegistry registry;
    private readonly HumanPrincipal owner;

    public ProductApprovalStore(IStore store, Guid partitionId, HumanPrincipal owner, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(clock);
        if (partitionId == Guid.Empty) throw new ArgumentException("A partition identity is required.", nameof(partitionId));
        this.owner = owner;
        registry = new(store, "arcscope.security.approvals.v1", partitionId, owner.Id, clock);
    }

    public ValueTask<ApprovalSnapshot?> ReadAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = registry.Read(approvalId);
        if (entry is null) return ValueTask.FromResult<ApprovalSnapshot?>(null);
        var snapshot = SecurityCodec.DecodeApproval(entry.Payload);
        if (snapshot.Owner != owner || snapshot.ApprovalId != entry.Id || snapshot.Version != entry.Version)
            throw new InvalidDataException("The approval record does not match its owner, identity or version.");
        if (snapshot.Version != (snapshot.State == ApprovalState.Pending ? 1 : 2) ||
            snapshot.Decision is { DecidedBy: var human } && human != owner)
            throw new InvalidDataException("The approval lifecycle or recorded decision owner is invalid.");
        return ValueTask.FromResult<ApprovalSnapshot?>(snapshot);
    }

    public ValueTask<bool> TryCreatePendingAsync(ApprovalSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Owner != owner || snapshot.State != ApprovalState.Pending || snapshot.Version != 1)
            return ValueTask.FromResult(false);
        return registry.TryCreateAsync(new(snapshot.ApprovalId, snapshot.Version, SecurityCodec.EncodeApproval(snapshot)), cancellationToken);
    }

    public async ValueTask<bool> TryResolveAsync(Guid approvalId, long expectedVersion, ApprovalSnapshot resolved,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(resolved);
        if (expectedVersion <= 0 || expectedVersion == long.MaxValue || resolved.ApprovalId != approvalId ||
            resolved.Version != expectedVersion + 1 || resolved.Owner != owner ||
            resolved.State is not (ApprovalState.Approved or ApprovalState.Denied or ApprovalState.Cancelled or ApprovalState.Expired) ||
            resolved.Decision is { DecidedBy: var human } && human != owner) return false;
        var current = await ReadAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Version != expectedVersion || current.State != ApprovalState.Pending ||
            current.CommandId != resolved.CommandId || current.Owner != resolved.Owner ||
            current.OperationId != resolved.OperationId || current.TargetResourceId != resolved.TargetResourceId ||
            current.TargetRevision != resolved.TargetRevision || current.EffectSha256 != resolved.EffectSha256 ||
            current.EffectiveRisk != resolved.EffectiveRisk || current.RequestedAt != resolved.RequestedAt ||
            current.ExpiresAt != resolved.ExpiresAt) return false;
        return await registry.TryReplaceAsync(new(approvalId, resolved.Version, SecurityCodec.EncodeApproval(resolved)),
            expectedVersion, cancellationToken).ConfigureAwait(false);
    }
}

internal static class SecurityCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaximumBytes = 65536;

    internal static byte[] EncodeLease(CapabilityLease lease) => Encode(writer =>
    {
        Text(writer, "ArcForges.ArcScope.Lease.v1");
        GuidValue(writer, lease.Id.Value);
        Principal(writer, lease.Owner);
        GuidValue(writer, lease.Scope.Realm.Value);
        writer.Write(lease.Scope.Workspace is not null);
        if (lease.Scope.Workspace is { } workspace) GuidValue(writer, workspace.Value);
        GuidValue(writer, lease.Task.Value);
        writer.Write((int)lease.Holder.Kind);
        GuidValue(writer, lease.Holder.ActorId);
        Text(writer, lease.CapabilityKey);
        writer.Write(lease.ResourceIds.Count);
        foreach (var resource in lease.ResourceIds) Text(writer, resource);
        writer.Write((int)lease.EffectiveRisk);
        writer.Write((int)lease.Origin);
        writer.Write((int)lease.Basis);
        Bytes(writer, ActorChainSnapshot.Encode(lease.IssuedBy));
        Time(writer, lease.IssuedAt);
        Time(writer, lease.ExpiresAt);
        writer.Write((int)lease.State);
        writer.Write(lease.Version);
        NullableTime(writer, lease.EndedAt);
        writer.Write((int)lease.RevocationReason);
        writer.Write(lease.EndEventRecorded);
    });

    internal static CapabilityLease DecodeLease(byte[] bytes) => Decode(bytes, reader =>
    {
        Header(reader, "ArcForges.ArcScope.Lease.v1");
        var id = new CapabilityLeaseId(GuidValue(reader));
        var owner = Principal(reader);
        var realm = new RealmId(GuidValue(reader));
        var scope = new DecisionScope(realm, reader.ReadBoolean() ? new WorkspaceId(GuidValue(reader)) : null);
        var task = new TaskId(GuidValue(reader));
        var holder = new LeaseHolder((ActorKind)reader.ReadInt32(), GuidValue(reader));
        var capability = Text(reader, 256);
        var count = reader.ReadInt32();
        if (count is < 1 or > 64) throw new InvalidDataException("Invalid lease coverage count.");
        var resources = new string[count];
        for (var i = 0; i < count; i++) resources[i] = Text(reader, 512);
        var risk = (RiskLevel)reader.ReadInt32();
        var origin = (DecisionOrigin)reader.ReadInt32();
        var basis = (LeaseIssueBasis)reader.ReadInt32();
        var chain = ActorChainSnapshot.Decode(Bytes(reader, MaximumBytes)).Chain;
        var issued = Time(reader);
        var expires = Time(reader);
        var state = (LeaseState)reader.ReadInt32();
        var version = reader.ReadInt64();
        var ended = NullableTime(reader);
        var reason = (LeaseRevocationReason)reader.ReadInt32();
        var recorded = reader.ReadBoolean();
        return new(id, owner, scope, task, holder, capability, resources, risk, origin, basis, chain,
            issued, expires, state, version, ended, reason, recorded);
    });

    internal static byte[] EncodeApproval(ApprovalSnapshot snapshot) => Encode(writer =>
    {
        Text(writer, "ArcForges.ArcScope.Approval.v1");
        GuidValue(writer, snapshot.ApprovalId);
        GuidValue(writer, snapshot.CommandId.Value);
        Principal(writer, snapshot.Owner);
        Text(writer, snapshot.OperationId);
        Text(writer, snapshot.TargetResourceId);
        Text(writer, snapshot.TargetRevision);
        Text(writer, snapshot.EffectSha256);
        writer.Write((int)snapshot.EffectiveRisk);
        Time(writer, snapshot.RequestedAt);
        Time(writer, snapshot.ExpiresAt);
        writer.Write((int)snapshot.State);
        writer.Write(snapshot.Version);
        NullableTime(writer, snapshot.ResolvedAt);
        writer.Write(snapshot.Decision is not null);
        if (snapshot.Decision is { } decision)
        {
            GuidValue(writer, decision.DecisionId);
            writer.Write((int)decision.Kind);
            Principal(writer, decision.DecidedBy);
            GuidValue(writer, decision.Device.Value);
            writer.Write((int)decision.Origin);
            Time(writer, decision.DecidedAt);
            writer.Write(decision.Reason is not null);
            if (decision.Reason is not null) Text(writer, decision.Reason);
        }
        writer.Write(snapshot.CancelledBy is not null);
        if (snapshot.CancelledBy is not null) Principal(writer, snapshot.CancelledBy);
    });

    internal static ApprovalSnapshot DecodeApproval(byte[] bytes) => Decode(bytes, reader =>
    {
        Header(reader, "ArcForges.ArcScope.Approval.v1");
        var id = GuidValue(reader);
        var command = new CommandId(GuidValue(reader));
        var owner = Principal(reader);
        var operation = Text(reader, 128);
        var resource = Text(reader, 512);
        var revision = Text(reader, 256);
        var effect = Text(reader, 64);
        var risk = (RiskLevel)reader.ReadInt32();
        var requested = Time(reader);
        var expires = Time(reader);
        var state = (ApprovalState)reader.ReadInt32();
        var version = reader.ReadInt64();
        var resolved = NullableTime(reader);
        ApprovalDecision? decision = null;
        if (reader.ReadBoolean())
        {
            var decisionId = GuidValue(reader);
            var decisionKind = (ApprovalDecisionKind)reader.ReadInt32();
            var human = Principal(reader);
            var device = new DeviceId(GuidValue(reader));
            var origin = (ApprovalOrigin)reader.ReadInt32();
            var at = Time(reader);
            var reason = reader.ReadBoolean() ? Text(reader, 512) : null;
            decision = new(decisionId, decisionKind, human, device, origin, at, reason);
        }
        var cancelled = reader.ReadBoolean() ? Principal(reader) : null;
        return new(id, command, owner, operation, resource, revision, effect, risk, requested, expires,
            state, version, resolved, decision, cancelled);
    });

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true)) write(writer);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Security snapshot exceeds its byte budget.");
        return stream.ToArray();
    }

    private static T Decode<T>(byte[] bytes, Func<BinaryReader, T> read)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid security snapshot byte budget.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Utf8);
        var value = read(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing security snapshot data.");
        return value;
    }

    private static void Header(BinaryReader reader, string expected)
    {
        if (Text(reader, 64) != expected) throw new InvalidDataException("Unsupported security snapshot format.");
    }
    private static void GuidValue(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static Guid GuidValue(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        return new(bytes);
    }
    private static void Time(BinaryWriter writer, Instant value) { writer.Write(value.UnixSeconds); writer.Write(value.Nanoseconds); }
    private static Instant Time(BinaryReader reader) => new(reader.ReadInt64(), reader.ReadUInt32());
    private static void NullableTime(BinaryWriter writer, Instant? value) { writer.Write(value is not null); if (value is { } time) Time(writer, time); }
    private static Instant? NullableTime(BinaryReader reader) => reader.ReadBoolean() ? Time(reader) : null;
    private static void Principal(BinaryWriter writer, HumanPrincipal value)
    {
        GuidValue(writer, value.Realm.Value); GuidValue(writer, value.Id.Value); writer.Write((int)value.Kind);
    }
    private static HumanPrincipal Principal(BinaryReader reader) => new(new RealmId(GuidValue(reader)), new UserId(GuidValue(reader)), (HumanIdentityKind)reader.ReadInt32());
    private static void Text(BinaryWriter writer, string value) => Bytes(writer, Utf8.GetBytes(value));
    private static string Text(BinaryReader reader, int maximumBytes) => Utf8.GetString(Bytes(reader, maximumBytes));
    private static void Bytes(BinaryWriter writer, byte[] value) { writer.Write(value.Length); writer.Write(value); }
    private static byte[] Bytes(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid security snapshot field byte budget.");
        return reader.ReadBytes(length);
    }
}
