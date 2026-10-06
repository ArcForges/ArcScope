// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.Shapes;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Persistence.Sqlite;
using ArcForges.Sdk.Contracts.V1;
using ArcForges.Security;
using ArcForges.Security.Approvals;
using ArcForges.Security.CapabilityEnforcement;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;

namespace ArcForges.ArcScope.Core.Application;

/// <summary>Exact invocation identity and frozen semantic context, retained unchanged for a same-command retry.</summary>
public sealed class ProductInvocationOptions
{
    private readonly FrozenContext context;
    public ProductInvocationOptions(Guid invocationId, Guid commandId, FrozenContext context,
        Guid? approvalId = null, Guid? leaseId = null)
    {
        if (invocationId == Guid.Empty || commandId == Guid.Empty || approvalId == Guid.Empty || leaseId == Guid.Empty)
            throw new ArgumentException("Invocation, command and optional authority references must be nonempty.");
        ArgumentNullException.ThrowIfNull(context);
        InvocationId = invocationId; CommandId = commandId; this.context = context.Clone();
        ApprovalId = approvalId; LeaseId = leaseId;
    }
    public Guid InvocationId { get; }
    public Guid CommandId { get; }
    public Guid? ApprovalId { get; }
    public Guid? LeaseId { get; }
    public FrozenContext Context => context.Clone();
}

/// <summary>An authorized owner projection; callers receive clones, never a repository or store.</summary>
public sealed class AnnotationHistory
{
    private readonly ScopeAnnotation[] annotations;
    internal AnnotationHistory(ulong revision, IEnumerable<ScopeAnnotation> annotations)
    { Revision = revision; this.annotations = annotations.Select(item => item.Clone()).ToArray(); }
    public ulong Revision { get; }
    public IReadOnlyList<ScopeAnnotation> Annotations => Array.AsReadOnly(annotations.Select(item => item.Clone()).ToArray());
}

/// <summary>
/// Private durable owner composition. The trusted host binds a separate endpoint for each live human or delegated
/// session. Every operation uses the shared invocation journal and real service/final-owner enforcement.
/// </summary>
public sealed class ProductComposition : IAsyncDisposable
{
    private const int MaximumConcurrentOperations = 8;
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim slots = new(MaximumConcurrentOperations, MaximumConcurrentOperations);
    private readonly CancellationTokenSource shutdown = new();
    private readonly IStore store;
    private readonly Guid partition;
    private readonly InstanceIdentity instance;
    private readonly HumanPrincipal owner;
    private readonly DecisionScope scope;
    private readonly IClock clock;
    private readonly ulong recoveryGeneration;
    private readonly AnnotationSessionRepository repository;
    private readonly CapabilityRegistry catalogue = CapabilityRegistry.CreateInitial();
    private readonly ApprovalCoordinator approvals;
    private readonly StepUpCoordinator stepUp;
    private readonly ILeaseUseValidator leases;
    private readonly ISecurityAuditSink audit;
    private readonly IInvocationTraceSink trace;
    private ProductInvocationRecordStore? journal;
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool closing;
    private Task? disposal;

    public ProductComposition(IStore store, Guid partitionId, InstanceIdentity instance, HumanPrincipal owner,
        DecisionScope scope, IClock clock, ulong recoveryGeneration, ApprovalCoordinator approvals,
        StepUpCoordinator stepUp, ILeaseUseValidator leases,
        ISecurityAuditSink audit, IInvocationTraceSink trace)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        if (partitionId == Guid.Empty) throw new ArgumentException("An owned durable partition is required.", nameof(partitionId));
        this.instance = instance ?? throw new ArgumentNullException(nameof(instance));
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.stepUp = stepUp ?? throw new ArgumentNullException(nameof(stepUp));
        this.leases = leases ?? throw new ArgumentNullException(nameof(leases));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.trace = trace ?? throw new ArgumentNullException(nameof(trace));
        if (instance.Installation.App != AppIdentity.ArcScope || owner.Realm != scope.Realm)
            throw new ArgumentException("Composition requires this ArcScope instance and its exact owner realm.");
        partition = partitionId; this.recoveryGeneration = recoveryGeneration;
        repository = new(store, partition, owner.Id, clock);
    }

    /// <summary>
    /// Host-session registration only, never an RPC argument. Availability and context providers must be the host's
    /// real current projections. Assistant registrations use their own actor source and current lease references.
    /// </summary>
    public ProductEndpoint BindHostSession(IProductHostActorSource actorSource, ICapabilityProvider availability,
        IReadOnlyList<IContextProvider> contextProviders,
        Func<ActorChain, DecisionScope, DecisionOrigin, IDecisionRecorder> actorRecorderFactory)
    {
        ArgumentNullException.ThrowIfNull(actorSource);
        ArgumentNullException.ThrowIfNull(actorRecorderFactory);
        if (actorSource.BoundActors.Owner != owner || actorSource.BoundOrigin is not (DecisionOrigin.Local or DecisionOrigin.Remote))
            throw new ArgumentException("Host registration must preserve this exact owner and explicit entry origin.", nameof(actorSource));
        // A real DecisionResultRecorder is actor/scope/origin-bound. Construct one from this exact registration,
        // never reuse the human sink for delegated endpoints; no foreign factory callback is held under our lifecycle lock.
        var actorBoundRecorder = actorRecorderFactory(actorSource.BoundActors, scope, actorSource.BoundOrigin)
            ?? throw new ArgumentException("A durable actor-bound decision recorder is required.", nameof(actorRecorderFactory));
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(contextProviders);
        if (contextProviders.Count > 50 || contextProviders.Any(item => item is null || item.Owner != instance))
            throw new ArgumentException("Context providers must belong to this exact product instance.", nameof(contextProviders));
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            var authority = new ProductAuthority(repository, instance, owner, scope, clock, actorSource);
            var security = new SecurityDecisionPipeline(new(clock, new RegistryCapabilityCatalogue(catalogue),
                authority, authority, authority, authority, authority, authority, authority,
                approvals, stepUp, authority, authority, actorBoundRecorder, audit, leases));
            var gate = new CapabilityEnforcementGate(security, authority);
            var operation = new AnnotationOwnerOperations(repository, instance, scope, owner, leases, clock, recoveryGeneration);
            var history = new ConcurrentDictionary<Guid, AnnotationHistory>();
            var historyRequests = new ConcurrentDictionary<Guid, byte>();
            async ValueTask<Outcome<ScopeOperationsServiceGetSessionResponse>> ReadWithProjection(AuthorizedExecution ticket,
                CapabilityTarget target, Invocation invocation, ScopeOperationsServiceGetSessionRequest request,
                FrozenContextSnapshot context, CancellationToken token)
            {
                var result = await operation.GetSessionAsync(ticket, target, invocation, request, context, token).ConfigureAwait(false);
                var command = UuidBoundary.FromWire(invocation.CommandId);
                if (!historyRequests.ContainsKey(command) || !result.TryGetValue(out var response)) return result;
                if (await authority.CurrentAsync(token).ConfigureAwait(false) is null ||
                    ticket.Lease is not null && await leases.ValidateAsync(ticket.Lease, token).ConfigureAwait(false) != LeaseUseVerdict.Valid)
                    return Failure<ScopeOperationsServiceGetSessionResponse>("perm.capability_denied");
                var stored = repository.Read(UuidBoundary.FromWire(request.SessionId));
                if (stored is null || checked((ulong)stored.Version) != response!.Value.Session.Revision.Value ||
                    ticket.Resource.Revision != AnnotationOwnerOperations.Revision(stored.Version))
                    return Failure<ScopeOperationsServiceGetSessionResponse>("conflict.revision_mismatch");
                history[command] = new(checked((ulong)stored.Version), stored.Metadata.Annotations);
                return result;
            }
            CapabilityInvocationBinding[] bindings =
            [
                new CapabilityInvocationBinding<ScopeOperationsServiceGetSessionRequest, ScopeOperationsServiceGetSessionResponse>(
                    catalogue, "IScopeOperations.GetSession", InvocationResultVersionKind.NativeContentRev,
                    arguments => Decode(() => AnnotationOperationCodec.DecodeGetSession(arguments)),
                    gate.Enforce<ScopeOperationsServiceGetSessionRequest, ScopeOperationsServiceGetSessionResponse>(ReadWithProjection),
                    result => Encode(() => AnnotationOperationCodec.Encode(result)),
                    result => InvocationResultVersion.FromNativeContentRev(result.Value.Session.Revision)),
                new CapabilityInvocationBinding<ScopeOperationsServiceCreateAnnotationRequest, ScopeOperationsServiceCreateAnnotationResponse>(
                    catalogue, "IScopeOperations.CreateAnnotation", InvocationResultVersionKind.NativeContentRev,
                    arguments => Decode(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments)),
                    gate.Enforce<ScopeOperationsServiceCreateAnnotationRequest, ScopeOperationsServiceCreateAnnotationResponse>(operation.CreateAnnotationAsync),
                    result => Encode(() => AnnotationOperationCodec.Encode(result)),
                    result => InvocationResultVersion.FromNativeContentRev(result.Value.Revision)),
            ];
            journal ??= new(store, partition, owner.Id, clock, catalogue, bindings);
            var pipeline = new CapabilityInvocationPipeline(catalogue, availability, bindings, gate.AuthorizeAsync, journal, trace);
            return new(this, authority, pipeline, contextProviders.Select(provider => (IContextProvider)new BoundedContextProvider(provider)).ToArray(), historyRequests, history);
        }
    }

    private static Outcome<T> Decode<T>(Func<T> decode)
    {
        try { return Outcome.Success(decode()); }
        catch (ArgumentException) { return Failure<T>("validation.invalid_request"); }
    }
    private static Outcome<CapabilityResult> Encode(Func<CapabilityResult> encode) => Decode(encode);
    private static Outcome<T> Failure<T>(string code) => Outcome.Failure<T>(TypedFailure.Create(code));

    [SuppressMessage("Usage", "CA1031:Do not catch general exception types", Justification = "The public product boundary maps unavailable owner/storage/host exceptions to closed failure without exposing exception text.")]
    private Task<Outcome<T>> RunAsync<T>(Func<CancellationToken, Task<Outcome<T>>> operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Outcome.Cancelled<T>(EffectCertainty.DidNotHappen));
        // No unbounded queue and no SQLite work on the UI thread, including synchronous reads before the first await.
        lock (lifecycle)
        {
            if (closing || !slots.Wait(0)) return Task.FromResult(Failure<T>("resource.unavailable"));
            active++;
        }
        return Task.Run(async () =>
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            try { return await operation(bound.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Outcome.Cancelled<T>(EffectCertainty.Unknown); }
            catch (ArgumentException) { return Failure<T>("validation.invalid_request"); }
            catch (Exception) { return Failure<T>("internal.unexpected"); }
            finally
            {
                slots.Release();
                lock (lifecycle) { active--; if (closing && active == 0) drained.TrySetResult(); }
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is not null) return new(disposal);
            closing = true;
            if (active == 0) drained.TrySetResult();
            // Cancellation callbacks are foreign code and must never run under our lifecycle gate.
            disposal = Task.Run(StopAsync);
            return new(disposal);
        }
    }
    private async Task StopAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        await drained.Task.ConfigureAwait(false);
        journal?.Dispose(); // The host's borrowed IStore remains host-owned.
        slots.Dispose(); shutdown.Dispose();
    }

    // The trusted host may use asynchronous context adapters backed by unavailable external systems.
    // An uncooperative adapter cannot hold product shutdown forever or accumulate abandoned reads.
    private sealed class BoundedContextProvider(IContextProvider source) : IContextProvider
    {
        private readonly object gate = new();
        private Task<IReadOnlyList<IMessage>>? pending;
        public InstanceIdentity Owner => source.Owner;
        public async ValueTask<IReadOnlyList<IMessage>> ProvideMessagesAsync(InstanceIdentity expectedOwner,
            CancellationToken cancellationToken = default)
        {
            if (expectedOwner != Owner) throw new ArgumentException("Foreign product context owner.", nameof(expectedOwner));
            cancellationToken.ThrowIfCancellationRequested();
            Task<IReadOnlyList<IMessage>> current;
            lock (gate)
            {
                if (pending is null || pending.IsCompleted)
                {
                    pending = Task.Run(async () => await source.ProvideMessagesAsync(expectedOwner, CancellationToken.None).ConfigureAwait(false));
                    _ = pending.ContinueWith(static task => _ = task.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                current = pending;
            }
            // Snapshot freezing still validates generated messages and the authored count/byte budget.
            return await current.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Actor-bound endpoints carry no caller-supplied actor, grant or repository authority.</summary>
    public sealed class ProductEndpoint
    {
        private readonly ProductComposition host;
        private readonly ProductAuthority authority;
        private readonly CapabilityInvocationPipeline pipeline;
        private readonly IReadOnlyList<IContextProvider> contexts;
        private readonly ConcurrentDictionary<Guid, byte> historyRequests;
        private readonly ConcurrentDictionary<Guid, AnnotationHistory> history;
        internal ProductEndpoint(ProductComposition host, ProductAuthority authority,
            CapabilityInvocationPipeline pipeline, IReadOnlyList<IContextProvider> contexts,
            ConcurrentDictionary<Guid, byte> historyRequests, ConcurrentDictionary<Guid, AnnotationHistory> history)
        { this.host = host; this.authority = authority; this.pipeline = pipeline; this.contexts = contexts; this.historyRequests = historyRequests; this.history = history; }

        public Task<Outcome<ScopeOperationsServiceGetSessionResponse>> GetSessionAsync(
            ScopeOperationsServiceGetSessionRequest request, ProductInvocationOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(options);
            var copy = request.Clone();
            return host.RunAsync(async token => Convert(await InvokeAsync("IScopeOperations.GetSession",
                AnnotationOperationCodec.Encode(copy), copy.Meta, options, token).ConfigureAwait(false),
                AnnotationOperationCodec.DecodeGetSessionResponse), cancellationToken);
        }
        public Task<Outcome<ScopeOperationsServiceCreateAnnotationResponse>> CreateAnnotationAsync(
            ScopeOperationsServiceCreateAnnotationRequest request, ProductInvocationOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(options);
            var copy = request.Clone();
            return host.RunAsync(async token => Convert(await InvokeAsync("IScopeOperations.CreateAnnotation",
                AnnotationOperationCodec.Encode(copy), copy.Meta, options, token).ConfigureAwait(false),
                AnnotationOperationCodec.DecodeCreateAnnotationResponse), cancellationToken);
        }
        private Task<InvocationOutcome> InvokeAsync(string key, CapabilityArguments arguments, RequestMeta? metadata,
            ProductInvocationOptions options, CancellationToken token)
        {
            var invocation = new Invocation
            {
                InvocationId = UuidBoundary.ToWire(options.InvocationId), CommandId = UuidBoundary.ToWire(options.CommandId),
                Capability = key, Arguments = arguments, Context = options.Context,
                ExpectedNative = metadata?.ExpectedNative?.Clone(), ExpectedRev = metadata?.ExpectedRev?.Clone(),
                ApprovalId = options.ApprovalId is { } approval ? UuidBoundary.ToWire(approval) : null,
                LeaseId = options.LeaseId is { } lease ? UuidBoundary.ToWire(lease) : null,
            };
            var registration = host.catalogue.Find(key)!;
            var target = new CapabilityTarget(host.instance, registration.Descriptor, InstanceHealth.Ready, true);
            // This instance is actual in-process composition; RunAsync admits work only while it is running.
            return pipeline.InvokeAsync(invocation, new ActionKey(key),
                FrozenContextSnapshot.Freeze(host.instance, [], ContextSnapshotBudget.Default), [target], contexts,
                capturedTarget: host.instance, cancellationToken: token).AsTask();
        }
        private static Outcome<T> Convert<T>(InvocationOutcome result, Func<CapabilityResult, T> decode) => result.Kind switch
        {
            OutcomeKind.Success when result.Value is not null => Decode(() => decode(result.Value.Result)),
            OutcomeKind.Failure when result.Failure is not null => Outcome.Failure<T>(result.Failure),
            OutcomeKind.Cancelled => Outcome.Cancelled<T>(result.CancellationEffect),
            _ => Failure<T>("internal.unexpected"),
        };

        /// <summary>Intentional trusted-human creation; supplied configuration is validated and stored, with no capture proof.</summary>
        public Task<Outcome<ScopeSession>> BootstrapAsync(Guid sessionId, string name, ScopeConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var copy = configuration.Clone();
            return host.RunAsync(async token =>
            {
                var current = await authority.CurrentAsync(token).ConfigureAwait(false);
                if (current is null || current.Origin != DecisionOrigin.Local || current.Actors.Actors.Count != 0 ||
                    current.Trust != TrustVerdict.Verified || current.Actors.Owner.Kind != HumanIdentityKind.LocalHuman) return Failure<ScopeSession>("perm.capability_denied");
                if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(name)) return Failure<ScopeSession>("validation.invalid_request");
                var session = new ScopeSession { SessionId = UuidBoundary.ToWire(sessionId), Name = name, Configuration = copy, Revision = new() { Value = 1 } };
                var shape = new ScopeOperationsServiceGetSessionResponse
                {
                    Meta = new() { CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()), RecoveryGeneration = host.recoveryGeneration },
                    Value = new() { Session = session },
                };
                if (!ContractShapeValidation.IsValid(shape)) return Failure<ScopeSession>("validation.invalid_request");
                var metadata = new ScopeMetadata { SessionId = session.SessionId.Clone(), Name = name, Configuration = copy };
                if (!await host.repository.TryCreateAsync(sessionId, metadata, token).ConfigureAwait(false))
                    return Failure<ScopeSession>("conflict.duplicate_identifier");
                return Outcome.Success(session.Clone());
            }, cancellationToken);
        }

        /// <summary>Fresh GetSession-gated internal projection; never authorized by an old command's journal replay.</summary>
        public Task<Outcome<AnnotationHistory>> GetAnnotationHistoryAsync(Guid sessionId, ulong expectedVersion,
            ProductInvocationOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return host.RunAsync(async token =>
            {
                if (sessionId == Guid.Empty || expectedVersion == 0) return Failure<AnnotationHistory>("validation.invalid_request");
                var fresh = new ProductInvocationOptions(Guid.NewGuid(), Guid.NewGuid(), options.Context, options.ApprovalId, options.LeaseId);
                var request = new ScopeOperationsServiceGetSessionRequest
                {
                    SessionId = UuidBoundary.ToWire(sessionId),
                    Meta = new() { CommandId = UuidBoundary.ToWire(fresh.CommandId), CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()), ExpectedNative = new() { Value = expectedVersion } },
                };
                if (!historyRequests.TryAdd(fresh.CommandId, 0)) return Failure<AnnotationHistory>("internal.unexpected");
                try
                {
                    var result = Convert(await InvokeAsync("IScopeOperations.GetSession", AnnotationOperationCodec.Encode(request),
                        request.Meta, fresh, token).ConfigureAwait(false), AnnotationOperationCodec.DecodeGetSessionResponse);
                    if (!result.TryGetValue(out _))
                        return result.Kind == OutcomeKind.Cancelled ? Outcome.Cancelled<AnnotationHistory>(result.CancellationEffect)
                            : result.TryGetFailure(out var failure) && failure is not null ? Outcome.Failure<AnnotationHistory>(failure)
                            : Failure<AnnotationHistory>("internal.unexpected");
                    // Only the gated owner callback can populate this exact fresh command projection. A journal replay cannot.
                    return history.TryRemove(fresh.CommandId, out var projection)
                        ? Outcome.Success(projection) : Failure<AnnotationHistory>("internal.unexpected");
                }
                finally { historyRequests.TryRemove(fresh.CommandId, out _); history.TryRemove(fresh.CommandId, out _); }
            }, cancellationToken);
        }
    }
}
