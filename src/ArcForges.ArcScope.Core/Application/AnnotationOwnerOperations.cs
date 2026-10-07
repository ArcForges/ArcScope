// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.Shapes;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Sdk.Contracts.V1;
using ArcForges.Security;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;
using Google.Protobuf;
using ActorChain = ArcForges.Security.ActorChain;

namespace ArcForges.ArcScope.Core.Application;

/// <summary>Internal typed owners: composition exposes these exclusively through the invocation pipeline and enforcement gate.</summary>
internal sealed class AnnotationOwnerOperations(
    AnnotationSessionRepository repository, InstanceIdentity instance, DecisionScope scope, HumanPrincipal owner,
    ILeaseUseValidator leases, IClock clock, ulong recoveryGeneration)
{
    internal static string ResourceId(Guid session) => "session:" + session.ToString("D");
    internal static string Revision(long version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Exact persisted data proof only; this does not establish an actor, permission, lease or approval.</summary>
    internal sealed class CommittedAnnotationProof
    {
        internal CommittedAnnotationProof(Guid sessionId, Guid commandId, ulong originalExpectedNative,
            long committedVersion, long currentVersion, ByteString normalizedArgumentFingerprint)
        {
            SessionId = sessionId;
            CommandId = commandId;
            OriginalExpectedNative = originalExpectedNative;
            CommittedVersion = committedVersion;
            CurrentVersion = currentVersion;
            NormalizedArgumentFingerprint = normalizedArgumentFingerprint;
        }

        internal Guid SessionId { get; }
        internal Guid CommandId { get; }
        internal ulong OriginalExpectedNative { get; }
        internal long CommittedVersion { get; }
        internal long CurrentVersion { get; }
        internal ByteString NormalizedArgumentFingerprint { get; }
    }

    internal CommittedAnnotationProof? TryMatchCommittedAnnotation(CapabilityTarget target, Invocation invocation,
        ScopeOperationsServiceCreateAnnotationRequest rawRequest)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(rawRequest);
        if (invocation.Capability != "IScopeOperations.CreateAnnotation" || target.Descriptor.Key != invocation.Capability)
            return null;
        var request = rawRequest.Clone();
        try
        {
            // Refuse unknown facts before deriving the same metadata the actual owner hashed at commit.
            if (!AnnotationOperationCodec.Encode(request).Equals(invocation.Arguments)) return null;
            _ = UuidBoundary.FromWire(invocation.CommandId);
            if (!ValidateMetadata(request.Meta, invocation, target, write: true) || !ContractShapeValidation.IsValid(request))
                return null;
        }
        catch (ArgumentException) { return null; }
        var id = UuidBoundary.FromWire(request.SessionId);
        var command = UuidBoundary.FromWire(invocation.CommandId);
        var current = repository.Read(id);
        if (current is null) return null;
        var receipt = current.Receipts.SingleOrDefault(item => item.CommandId == command);
        var original = invocation.ExpectedNative!.Value;
        if (receipt is null || original >= long.MaxValue || receipt.CommittedVersion != checked((long)original + 1) ||
            receipt.CommittedVersion > current.Version) return null;
        var fingerprint = SHA256.HashData(request.ToByteArray());
        return CryptographicOperations.FixedTimeEquals(receipt.Fingerprint, fingerprint)
            ? new(id, command, original, receipt.CommittedVersion, current.Version, ByteString.CopyFrom(fingerprint))
            : null;
    }

    internal async ValueTask<Outcome<ScopeOperationsServiceGetSessionResponse>> GetSessionAsync(
        AuthorizedExecution ticket, CapabilityTarget target, Invocation invocation, ScopeOperationsServiceGetSessionRequest arguments,
        FrozenContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidateMetadata(arguments.Meta, invocation, target, write: false) || !ContractShapeValidation.IsValid(arguments))
            return Failure<ScopeOperationsServiceGetSessionResponse>("validation.invalid_request");
        var id = UuidBoundary.FromWire(arguments.SessionId);
        var session = repository.Read(id);
        if (session is null) return Failure<ScopeOperationsServiceGetSessionResponse>("state.not_found");
        if (!Matches(ticket, id, session.Version, "IScopeOperations.GetSession"))
            return Failure<ScopeOperationsServiceGetSessionResponse>("perm.resource_denied");
        if (!await LeaseIsCurrentAsync(ticket, cancellationToken).ConfigureAwait(false))
            return Failure<ScopeOperationsServiceGetSessionResponse>("perm.capability_denied");
        if (invocation.ExpectedNative is not null && invocation.ExpectedNative.Value != (ulong)session.Version)
            return Failure<ScopeOperationsServiceGetSessionResponse>("conflict.revision_mismatch");
        var value = new ScopeSession { SessionId = session.Metadata.SessionId.Clone(), Revision = new() { Value = (ulong)session.Version } };
        if (session.Metadata.HasName) value.Name = session.Metadata.Name;
        value.Configuration = session.Metadata.Configuration?.Clone();
        value.Captures.Add(session.Metadata.Captures.Select(capture => capture.Clone()));
        var response = new ScopeOperationsServiceGetSessionResponse { Meta = ResponseMetadata(arguments.Meta!), Value = new() { Session = value } };
        if (!ContractShapeValidation.IsValid(response)) return Failure<ScopeOperationsServiceGetSessionResponse>("internal.unexpected");
        return Outcome.Success(response);
    }

    internal async ValueTask<Outcome<ScopeOperationsServiceCreateAnnotationResponse>> CreateAnnotationAsync(
        AuthorizedExecution ticket, CapabilityTarget target, Invocation invocation, ScopeOperationsServiceCreateAnnotationRequest arguments,
        FrozenContextSnapshot context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidateMetadata(arguments.Meta, invocation, target, write: true) || !ContractShapeValidation.IsValid(arguments))
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("validation.invalid_request");
        var id = UuidBoundary.FromWire(arguments.SessionId);
        var command = UuidBoundary.FromWire(invocation.CommandId);
        var annotationId = UuidBoundary.FromWire(arguments.AnnotationId);
        var fingerprint = SHA256.HashData(arguments.ToByteArray());
        var current = repository.Read(id);
        if (current is null) return Failure<ScopeOperationsServiceCreateAnnotationResponse>("state.not_found");
        // The final owner never relies solely on the earlier resource verdict or caller-supplied metadata.
        if (!Matches(ticket, id, current.Version, "IScopeOperations.CreateAnnotation"))
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("conflict.revision_mismatch");
        if (!await LeaseIsCurrentAsync(ticket, cancellationToken).ConfigureAwait(false))
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("perm.capability_denied");
        var receipt = current.Receipts.SingleOrDefault(receipt => receipt.CommandId == command);
        if (receipt is not null)
            return CryptographicOperations.FixedTimeEquals(receipt.Fingerprint, fingerprint)
                ? Outcome.Success(MutationResponse(arguments.Meta!, receipt.CommittedVersion))
                : Failure<ScopeOperationsServiceCreateAnnotationResponse>("command.reused_identifier");
        if (invocation.ExpectedNative!.Value != (ulong)current.Version)
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("conflict.revision_mismatch");
        if (current.Metadata.Annotations.Any(annotation => UuidBoundary.FromWire(annotation.AnnotationId) == annotationId))
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("conflict.duplicate_identifier");
        var range = arguments.Range;
        var count = current.Metadata.Captures.Select(capture => capture.HasSampleCount ? capture.SampleCount : 0).DefaultIfEmpty(0UL).Max();
        if (range.From > count || range.Count > count - range.From)
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("validation.invalid_request");
        if (current.Metadata.Annotations.Count >= 128 || current.Receipts.Count >= 256)
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("resource.unavailable");
        var annotation = new ScopeAnnotation
        {
            AnnotationId = arguments.AnnotationId.Clone(),
            Range = range.Clone(),
            Text = arguments.Text,
            Origin = Origin(annotationId, arguments.Text, ticket.Actors),
        };
        if (!await repository.TryAppendAsync(current, annotation, command, fingerprint, cancellationToken).ConfigureAwait(false))
            return Failure<ScopeOperationsServiceCreateAnnotationResponse>("conflict.revision_mismatch");
        return Outcome.Success(MutationResponse(arguments.Meta!, checked(current.Version + 1)));
    }

    private bool ValidateMetadata(RequestMeta? metadata, Invocation invocation, CapabilityTarget target, bool write)
    {
        if (metadata is null || target.Identity != instance || instance.Installation.App != AppIdentity.ArcScope ||
            metadata.ExpectedRev is not null || invocation.ExpectedRev is not null ||
            metadata.CommandId is not null && !metadata.CommandId.Equals(invocation.CommandId) ||
            metadata.WorkspaceId is not null && (scope.Workspace is null || !metadata.WorkspaceId.Equals(scope.Workspace.Value.ToWire())) ||
            metadata.ApplicationScope is not null && !metadata.ApplicationScope.Equals(instance.Installation.ToApplicationScope()) ||
            metadata.HasRecoveryGeneration && metadata.RecoveryGeneration != recoveryGeneration ||
            !Equals(metadata.ExpectedNative, invocation.ExpectedNative) ||
            write && invocation.ExpectedNative is not { HasValue: true, Value: > 0 }) return false;
        // Command identity is supplied by the validated invocation, never chosen from untrusted operation arguments.
        metadata.CommandId = invocation.CommandId.Clone();
        metadata.WorkspaceId = scope.Workspace?.ToWire();
        metadata.ApplicationScope = instance.Installation.ToApplicationScope();
        metadata.RecoveryGeneration = recoveryGeneration;
        return true;
    }

    private bool Matches(AuthorizedExecution ticket, Guid session, long version, string key) =>
        ticket.Decision.Allowed && ticket.CapabilityKey == key && ticket.Actors.Owner == owner && ticket.Actors.Owner.Realm == scope.Realm &&
        ticket.Resource.Id == ResourceId(session) && ticket.Resource.Revision == Revision(version);

    private async ValueTask<bool> LeaseIsCurrentAsync(AuthorizedExecution ticket, CancellationToken cancellationToken)
    {
        if (ticket.Lease is null) return !ticket.Actors.Actors.Any(actor => actor.Kind is ActorKind.Agent or ActorKind.Extension);
        return await leases.ValidateAsync(ticket.Lease, cancellationToken).ConfigureAwait(false) == LeaseUseVerdict.Valid;
    }

    private ResponseMeta ResponseMetadata(RequestMeta request) => new()
    { CorrelationId = request.CorrelationId.Clone(), RecoveryGeneration = recoveryGeneration };
    private ScopeOperationsServiceCreateAnnotationResponse MutationResponse(RequestMeta request, long version) => new()
    { Meta = ResponseMetadata(request), Value = new() { Revision = new() { Value = checked((ulong)version) } } };

    private ContentOrigin Origin(Guid annotation, string text, ActorChain actors)
    {
        var delegated = actors.Actors.Count != 0;
        var origin = new ContentOrigin
        {
            Profile = "arcforges.content-origin.v1",
            OriginId = new ContentOriginId(Guid.NewGuid()).ToWire(),
            ContentUnitId = new ContentUnitId(annotation).ToWire(),
            ProducerKind = delegated ? "import" : "human",
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            CreatedAt = WireValues.ToWire(clock.GetCurrentInstant()),
            OmittedParentCount = 0,
        };
        // The request carries no model provenance. Preserve that uncertainty rather than label delegated text non-AI.
        origin.Kinds.Add(delegated ? "unknown" : "nonAi");
        return origin;
    }
    private static Outcome<T> Failure<T>(string code) => Outcome.Failure<T>(TypedFailure.Create(code));
}
