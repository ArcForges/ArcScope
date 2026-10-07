// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Sdk.Contracts.V1;
using ArcForges.Security;
using ArcForges.Security.Approvals;
using ArcForges.Security.CapabilityEnforcement;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;

namespace ArcForges.ArcScope.Core.Application;

/// <summary>Trusted product-host boundary, never populated from invocation arguments or actor claims.</summary>
public interface IProductHostActorSource
{
    /// <summary>Monotonic host admission generation; changes on revocation, rebind, trust or permission change.</summary>
    ulong Generation { get; }
    /// <summary>Immutable actor identity bound by trusted host-session registration; never changes into another actor.</summary>
    ActorChain BoundActors { get; }
    /// <summary>Immutable host entry origin, independent of caller arguments.</summary>
    DecisionOrigin BoundOrigin { get; }
    /// <summary>Reload the current authenticated host session, revocation, exact policy/grants and software trust.</summary>
    ValueTask<ProductHostActorSnapshot?> ReadCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>Immutable current facts supplied by the trusted local profile or authenticated orchestration host.</summary>
public sealed class ProductHostActorSnapshot
{
    public ProductHostActorSnapshot(ActorChain actors, DecisionScope scope, DecisionOrigin origin,
        ITransportSession transport, ulong generation, Instant validFrom, Instant expiresAt, TrustVerdict trust,
        IEnumerable<PermissionGrantRecord> permissions, IEnumerable<string> enabledCapabilities)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(enabledCapabilities);
        if (generation == 0 || expiresAt <= validFrom || origin is not (DecisionOrigin.Local or DecisionOrigin.Remote) ||
            trust is not (TrustVerdict.Verified or TrustVerdict.Unverified or TrustVerdict.Revoked))
            throw new ArgumentException("Host facts require an explicit valid lifetime, origin and trust verdict.");
        Actors = actors; Scope = scope; Origin = origin; Transport = transport;
        Generation = generation; ValidFrom = validFrom; ExpiresAt = expiresAt; Trust = trust;
        Permissions = Array.AsReadOnly(permissions.ToArray());
        EnabledCapabilities = Array.AsReadOnly(enabledCapabilities.Distinct(StringComparer.Ordinal).ToArray());
        if (Permissions.Count > 2 || EnabledCapabilities.Count > 2 || Permissions.Any(p => p is null) ||
            EnabledCapabilities.Any(p => !ProductAuthority.IsOwnedCapability(p)))
            throw new ArgumentException("The minimal product host has only its exact two registered operations.");
    }
    public ActorChain Actors { get; }
    public DecisionScope Scope { get; }
    public DecisionOrigin Origin { get; }
    public ITransportSession Transport { get; }
    public ulong Generation { get; }
    public Instant ValidFrom { get; }
    public Instant ExpiresAt { get; }
    public TrustVerdict Trust { get; }
    public IReadOnlyList<PermissionGrantRecord> Permissions { get; }
    public IReadOnlyList<string> EnabledCapabilities { get; }
}

/// <summary>
/// The actual in-process host-session adapter. Trusted local-profile or orchestration registration supplies
/// the initial authenticated facts; this object preserves their actor identity and permanently fences revocation.
/// It makes no Cloud authentication or OS isolation claim.
/// </summary>
public sealed class ProductHostActorSession : IProductHostActorSource, IDisposable
{
    private readonly object sync = new();
    private ProductHostActorSnapshot snapshot;
    private ulong generation;
    private bool revoked;

    public ProductHostActorSession(ProductHostActorSnapshot initial)
    {
        snapshot = initial ?? throw new ArgumentNullException(nameof(initial));
        BoundActors = initial.Actors;
        BoundOrigin = initial.Origin;
        generation = initial.Generation;
    }

    public ActorChain BoundActors { get; }
    public DecisionOrigin BoundOrigin { get; }
    public ulong Generation { get { lock (sync) return generation; } }

    public ValueTask<ProductHostActorSnapshot?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return ValueTask.FromResult(revoked ? null : snapshot);
    }

    /// <summary>Trusted host policy refresh only. Actor, transport, origin and owner scope cannot change.</summary>
    public void Refresh(ProductHostActorSnapshot updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(revoked, this);
            if (updated.Generation != checked(generation + 1) ||
                !CryptographicOperations.FixedTimeEquals(ActorChainSnapshot.Encode(updated.Actors), ActorChainSnapshot.Encode(BoundActors)) ||
                updated.Scope != snapshot.Scope || updated.Origin != snapshot.Origin ||
                !ReferenceEquals(updated.Transport, snapshot.Transport))
                throw new ArgumentException("A policy refresh must preserve this exact host-session identity and advance its generation once.", nameof(updated));
            snapshot = updated;
            generation = updated.Generation;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (revoked) return;
            revoked = true;
            if (generation < ulong.MaxValue) generation++;
        }
    }
}

// Authority never accepts an ActorChain, grant or version from generic request data. Every security boundary
// reloads the host source. Resource revision is fresh and distinct from the original command precondition,
// allowing an already committed command to replay only after renewed service authorization.
internal sealed class ProductAuthority(AnnotationSessionRepository repository, InstanceIdentity instance,
    HumanPrincipal owner, DecisionScope scope, IClock clock, IProductHostActorSource actors, AnnotationOwnerOperations? committedOwner = null, CancellationToken lifetime = default)
    : ICapabilityEvidenceSource, IProductPolicy, IActorIdentityVerifier, IScopeAuthority, ITrustEvaluator,
      IPermissionSource, IResourceAuthorizer, IOwnerValidator, IDataBoundaryAuthorizer,
      ISensitiveOperationSource, ILeaseCeilingSource
{
    private readonly object actorReadGate = new();
    private Task<ProductHostActorSnapshot?>? actorRead;
    private readonly AsyncLocal<InvocationFrame?> invocationFrame = new();

    // This bounded execution-flow capture is installed only by the private typed endpoint, never by request JSON.
    // It is data proof for original command replay; all actor/grant/trust/approval/lease queries remain fresh.
    internal IDisposable EnterInvocation(Invocation invocation, CapabilityTarget target)
    {
        var previous = invocationFrame.Value;
        invocationFrame.Value = new(invocation.Clone(), target);
        return new InvocationScope(invocationFrame, previous);
    }

    private sealed record InvocationFrame(Invocation Invocation, CapabilityTarget Target);
    private sealed class InvocationScope(AsyncLocal<InvocationFrame?> capture, InvocationFrame? previous) : IDisposable
    {
        public void Dispose() => capture.Value = previous;
    }

    private AnnotationOwnerOperations.CommittedAnnotationProof? MatchCommitted(Invocation invocation, CapabilityTarget target)
    {
        if (committedOwner is null || invocation.Capability != "IScopeOperations.CreateAnnotation" || invocation.Arguments is null)
            return null;
        try { return committedOwner.TryMatchCommittedAnnotation(target, invocation,
            AnnotationOperationCodec.DecodeCreateAnnotation(invocation.Arguments)); }
        catch (ArgumentException) { return null; }
    }

    internal bool CurrentResource(ResourceReference resource) => ResourceIsCurrent(resource);


    internal static bool IsOwnedCapability(string key) =>
        key is "IScopeOperations.GetSession" or "IScopeOperations.CreateAnnotation";

    internal async ValueTask<ProductHostActorSnapshot?> CurrentAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<ProductHostActorSnapshot?> pending;
        lock (actorReadGate)
        {
            // One outstanding host query even if an external identity adapter ignores cancellation.
            // Caller cancellation only stops this waiter, never another invocation's current-authority read.
            if (actorRead is null || actorRead.IsCompleted)
            {
                actorRead = Task.Run(async () => await actors.ReadCurrentAsync(lifetime).ConfigureAwait(false));
                _ = actorRead.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            pending = actorRead;
        }
        ProductHostActorSnapshot? current;
        try { current = await pending.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
        var now = clock.GetCurrentInstant();
        return current is not null && current.Generation == actors.Generation && current.ValidFrom <= now && now < current.ExpiresAt &&
            current.Origin == actors.BoundOrigin && SameActors(current.Actors, actors.BoundActors) && current.Actors.Owner == owner && current.Actors.Owner.Realm == scope.Realm && current.Scope == scope &&
            instance.Installation.App == AppIdentity.ArcScope &&
            (current.Actors.Actors.Count != 0 ||
             current.Actors.Device == instance.Installation.DeviceId &&
             current.Actors.Installation == instance.Installation.InstallationId &&
             current.Actors.CallerInstance == instance.InstanceId)
            ? current : null;
    }

    private static bool SameActors(ActorChain left, ActorChain right) =>
        CryptographicOperations.FixedTimeEquals(ActorChainSnapshot.Encode(left), ActorChainSnapshot.Encode(right));

    internal async ValueTask<bool> CanBootstrapAsync(CancellationToken token)
    {
        const string capability = "IScopeOperations.CreateAnnotation";
        var current = await CurrentAsync(token).ConfigureAwait(false);
        if (current is null || current.Origin != DecisionOrigin.Local || current.Actors.Actors.Count != 0 ||
            current.Trust != TrustVerdict.Verified || current.Actors.Owner.Kind != HumanIdentityKind.LocalHuman ||
            !ReferenceEquals(current.Transport, TransportSessions.InProcess) ||
            !current.EnabledCapabilities.Contains(capability, StringComparer.Ordinal)) return false;
        var principal = FormattableString.Invariant($"principal:{owner.Realm.Value:N}/{owner.Id.Value:N}");
        var grant = FindGrant(current, principal, capability, scope.Key);
        var now = clock.GetCurrentInstant();
        return grant is { State: PermissionGrantState.Granted } &&
            Instant.FromDateTimeOffset(grant.ValidFromUtc) <= now && now < Instant.FromDateTimeOffset(grant.ValidUntilUtc) &&
            grant.Constraints.All(constraint => constraint == PermissionConstraints.LocalOriginOnly ||
                constraint == PermissionConstraints.DeviceBound(current.Actors.Device)) &&
            current.Generation == actors.Generation;
    }

    private async ValueTask<ProductHostActorSnapshot?> ForRequestAsync(DecisionRequest request, CancellationToken token)
    {
        var current = await CurrentAsync(token).ConfigureAwait(false);
        return current is not null && IsOwnedCapability(request.CapabilityKey) && request.Scope == scope &&
            request.Origin == current.Origin && ReferenceEquals(request.Transport, current.Transport) &&
            SameActors(current.Actors, request.Actors) ? current : null;
    }

    public async ValueTask<CapabilityEvidence?> DescribeAsync(CapabilityRegistration capability, Invocation invocation,
        CapabilityTarget target, FrozenContextSnapshot context, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || target.Identity != instance || capability.Key != invocation.Capability ||
            !IsOwnedCapability(capability.Key) || invocation.Arguments is null) return null;
        Guid session;
        try
        {
            session = capability.Key == "IScopeOperations.GetSession"
                ? UuidBoundary.FromWire(AnnotationOperationCodec.DecodeGetSession(invocation.Arguments).SessionId)
                : UuidBoundary.FromWire(AnnotationOperationCodec.DecodeCreateAnnotation(invocation.Arguments).SessionId);
        }
        catch (ArgumentException) { return null; }
        var stored = repository.Read(session);
        if (stored is null) return null;
        var committed = MatchCommitted(invocation, target);
        var resourceRevision = committed is null
            ? AnnotationOwnerOperations.Revision(stored.Version)
            : committed.OriginalExpectedNative.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(current.Actors, scope,
            new(AnnotationOwnerOperations.ResourceId(session), resourceRevision),
            current.Origin, current.Transport, RiskFacts.None);
    }

    public async ValueTask<PolicyVerdict> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken) =>
        await ForRequestAsync(request, cancellationToken).ConfigureAwait(false) is { } current &&
        current.EnabledCapabilities.Contains(request.CapabilityKey, StringComparer.Ordinal)
            ? PolicyVerdict.Enabled : PolicyVerdict.Disabled;
    public async ValueTask<ActorIdentityVerdict> VerifyAsync(DecisionRequest request, CancellationToken cancellationToken) =>
        await ForRequestAsync(request, cancellationToken).ConfigureAwait(false) is not null
            ? ActorIdentityVerdict.Authenticated : ActorIdentityVerdict.Unauthenticated;
    public async ValueTask<ScopeVerdict> ValidateAsync(DecisionRequest request, CancellationToken cancellationToken) =>
        await ForRequestAsync(request, cancellationToken).ConfigureAwait(false) is not null ? ScopeVerdict.Valid : ScopeVerdict.Invalid;
    public async ValueTask<TrustVerdict> EvaluateAsync(DecisionRequest request, TransportBinding? transport,
        CancellationToken cancellationToken) =>
        await ForRequestAsync(request, cancellationToken).ConfigureAwait(false) is { } current && transport is not null
            ? current.Trust : TrustVerdict.Unknown;
    public async ValueTask<PermissionGrantRecord?> FindAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        var current = await ForRequestAsync(request, cancellationToken).ConfigureAwait(false);
        return FindGrant(current, request.PrincipalKey, request.CapabilityKey, request.ScopeKey);
    }
    public async ValueTask<PermissionGrantRecord?> FindAsync(string principalKey, string capabilityKey, string scopeKey,
        CancellationToken cancellationToken) => FindGrant(await CurrentAsync(cancellationToken).ConfigureAwait(false),
            principalKey, capabilityKey, scopeKey);
    private static PermissionGrantRecord? FindGrant(ProductHostActorSnapshot? current, string principal, string capability, string scopeKey)
    {
        if (current is null || !IsOwnedCapability(capability)) return null;
        var matching = current.Permissions.Where(g => g.PrincipalKey == principal && g.CapabilityKey == capability && g.ScopeKey == scopeKey).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private bool ResourceIsCurrent(ResourceReference resource)
    {
        const string prefix = "session:";
        if (!resource.Id.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(resource.Id[prefix.Length..], "D", out var id) ||
            resource.Id != AnnotationOwnerOperations.ResourceId(id)) return false;
        var session = repository.Read(id);
        return session is not null && resource.Revision == AnnotationOwnerOperations.Revision(session.Version);
    }
    private bool ResourceIsCurrentOrCommitted(DecisionRequest request)
    {
        if (ResourceIsCurrent(request.Resource)) return true;
        var frame = invocationFrame.Value;
        if (frame is null || frame.Invocation.Capability != request.CapabilityKey ||
            UuidBoundary.FromWire(frame.Invocation.CommandId) != request.CommandId.Value ||
            CapabilityEnforcementGate.ComputeEffectSha256(request.CapabilityKey, frame.Invocation, frame.Target) != request.EffectSha256)
            return false;
        var proof = MatchCommitted(frame.Invocation, frame.Target);
        return proof is not null && request.Resource.Id == AnnotationOwnerOperations.ResourceId(proof.SessionId) &&
            request.Resource.Revision == proof.OriginalExpectedNative.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public async ValueTask<ResourceVerdict?> AuthorizeAsync(DecisionRequest request, CancellationToken cancellationToken) =>
        new(await ForRequestAsync(request, cancellationToken).ConfigureAwait(false) is not null && ResourceIsCurrentOrCommitted(request)
            ? ResourceDisposition.Authorized : ResourceDisposition.Denied, RiskFacts.None);
    public async ValueTask<OwnerVerdict> ValidateAsync(OwnerValidationRequest request, CancellationToken cancellationToken)
    {
        if (await ForRequestAsync(request.Request, cancellationToken).ConfigureAwait(false) is null) return OwnerVerdict.Refused;
        return ResourceIsCurrentOrCommitted(request.Request) ? OwnerVerdict.Valid : OwnerVerdict.RevisionChanged;
    }
    public ValueTask<BoundaryVerdict> AuthorizeSecretUseAsync(DecisionRequest request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(BoundaryVerdict.Denied); }
    public ValueTask<BoundaryVerdict> AuthorizeEgressAsync(DecisionRequest request, string destination, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(BoundaryVerdict.Denied); }
    public ValueTask<SensitiveOperation> FindAsync(string capabilityKey, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(SensitiveOperation.None); }
}
