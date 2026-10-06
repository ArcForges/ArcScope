// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Application;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.LocalRpc.Scope.Shapes;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Foundation.Execution;
using ArcForges.Foundation.Errors;
using ArcForges.Persistence.Sqlite;
using ArcForges.Sdk.Contracts.V1;
using ArcForges.Security;
using ArcForges.Security.Approvals;
using ArcForges.Security.Audit;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;
using Google.Protobuf;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class ProductCompositionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static T Value<T>(Outcome<T> outcome)
    { Assert.True(outcome.TryGetValue(out var value), outcome.TryGetFailure(out var failure) ? failure?.Code : outcome.Kind.ToString()); return value!; }

    [Fact]
    public async Task ActualGatedAppendReplaysOriginalCommandAndPreservesOwnerHistoryAcrossRestart()
    {
        using var fixture = new Fixture();
        var session = Guid.NewGuid();
        var options = Options();
        var request = Append(session, options, 1, "真实注释");
        await using (var host = fixture.Host())
        {
            var endpoint = fixture.Bind(host);
            var configuration = Configuration();
            Assert.Equal(configuration, Value(await endpoint.BootstrapAsync(session, "Replay", configuration, Cancellation)).Configuration);
            Assert.Equal(2UL, Value(await endpoint.CreateAnnotationAsync(request, options, Cancellation)).Value.Revision.Value);
            // Original native precondition remains 1. Fresh authority is checked before durable replay.
            Assert.Equal(2UL, Value(await endpoint.CreateAnnotationAsync(request, options, Cancellation)).Value.Revision.Value);
            var changed = request.Clone(); changed.Text = "changed";
            Assert.Equal(OutcomeKind.Failure, (await endpoint.CreateAnnotationAsync(changed, options, Cancellation)).Kind);
            var projection = Value(await endpoint.GetAnnotationHistoryAsync(session, 2, Options(), Cancellation));
            Assert.Equal("真实注释", Assert.Single(projection.Annotations).Text);
            projection.Annotations[0].Text = "changed caller clone";
            Assert.Equal("真实注释", Assert.Single(projection.Annotations).Text);
            Assert.Equal(OutcomeKind.Failure, (await endpoint.GetAnnotationHistoryAsync(session, 1, Options(), Cancellation)).Kind);
        }
        fixture.ReopenOwnerStore();
        await using (var reopened = fixture.Host())
        {
            var endpoint = fixture.Bind(reopened);
            Assert.Equal(2UL, Value(await endpoint.CreateAnnotationAsync(request, options, Cancellation)).Value.Revision.Value);
            var read = Value(await endpoint.GetSessionAsync(Read(session, 2), Options(), Cancellation));
            Assert.Equal("Replay", read.Value.Session.Name);
            Assert.Empty(read.Value.Session.Captures);
            Assert.Single(Value(await endpoint.GetAnnotationHistoryAsync(session, 2, Options(), Cancellation)).Annotations);
        }
        Assert.Contains(fixture.Audit.Query(new(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), 100)),
            item => item.Event.EventType == AuditEventType.InvocationResult && item.Event.ActorChain.Owner == fixture.Owner);
    }

    [Fact]
    public async Task RevokedHostCannotReplayCommittedOutcomeOrBootstrapMoreData()
    {
        using var fixture = new Fixture();
        await using var host = fixture.Host();
        var endpoint = fixture.Bind(host);
        var session = Guid.NewGuid();
        Value(await endpoint.BootstrapAsync(session, "Replay", Configuration(), Cancellation));
        var options = Options(); var request = Append(session, options, 1, "once");
        Value(await endpoint.CreateAnnotationAsync(request, options, Cancellation));
        fixture.Session.Dispose();
        Assert.Equal(OutcomeKind.Failure, (await endpoint.CreateAnnotationAsync(request, options, Cancellation)).Kind);
        Assert.Equal(OutcomeKind.Failure, (await endpoint.GetAnnotationHistoryAsync(session, 2, Options(), Cancellation)).Kind);
        Assert.Equal(OutcomeKind.Failure, (await endpoint.BootstrapAsync(Guid.NewGuid(), "denied", Configuration(), Cancellation)).Kind);
        Assert.Single(fixture.Repository.Read(session)!.Metadata.Annotations);
    }

    [Fact]
    public async Task SharedAssistantUsesItsOwnActorAndRealRevocableLeaseWithoutHumanBootstrapFallback()
    {
        using var fixture = new Fixture(); await using var host = fixture.Host();
        var human = fixture.Bind(host); var sessionId = Guid.NewGuid();
        Value(await human.BootstrapAsync(sessionId, "Replay", Configuration(), Cancellation));
        var current = (await fixture.Session.ReadCurrentAsync(Cancellation))!;
        var actor = new DelegatedActor(ActorKind.Agent, Guid.NewGuid(), new InstanceId(Guid.NewGuid()), "arcscope.assistant/1");
        var chain = new ActorChain(current.Actors.Owner, current.Actors.Device, current.Actors.Installation,
            current.Actors.Session, current.Actors.CallerInstance, [actor]);
        using var agentSession = new ProductHostActorSession(new(chain, current.Scope, current.Origin, current.Transport,
            1, current.ValidFrom, current.ExpiresAt, current.Trust, current.Permissions, current.EnabledCapabilities));
        var agent = fixture.Bind(host, actorSource: agentSession);
        Assert.Equal(OutcomeKind.Failure, (await agent.BootstrapAsync(Guid.NewGuid(), "agent must not create", Configuration(), Cancellation)).Kind);
        var missing = Options();
        Assert.Equal(OutcomeKind.Failure, (await agent.CreateAnnotationAsync(Append(sessionId, missing, 1, "without lease"), missing, Cancellation)).Kind);
        var issued = await fixture.Leases.IssueAsync(new(current.Actors, fixture.Scope, new TaskId(Guid.NewGuid()),
            new(ActorKind.Agent, actor.ActorId), "IScopeOperations.CreateAnnotation", ["session:" + sessionId.ToString("D")],
            RiskLevel.R1, DecisionOrigin.Local, LeaseIssueBasis.PolicyAllowed, TimeSpan.FromMinutes(1)), Cancellation);
        Assert.True(issued.Issued, issued.RegisteredCode);
        var lease = issued.Lease!;
        var options = new ProductInvocationOptions(Guid.NewGuid(), Guid.NewGuid(),
            new FrozenContext { ProfileVersion = "profile.1", PermissionsVersion = "permissions.1" }, leaseId: lease.Id.Value);
        var request = Append(sessionId, options, 1, "assistant draft");
        Assert.Equal(2UL, Value(await agent.CreateAnnotationAsync(request, options, Cancellation)).Value.Revision.Value);
        Assert.Equal("unknown", Assert.Single(Assert.Single(fixture.Repository.Read(sessionId)!.Metadata.Annotations).Origin.Kinds));
        Value(await fixture.Leases.RevokeAsync(lease.Id, fixture.Owner, LeaseRevocationReason.OwnerRevoked, Cancellation));
        Assert.Equal(OutcomeKind.Failure, (await agent.CreateAnnotationAsync(request, options, Cancellation)).Kind);
        var records = fixture.Audit.Query(new(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), 100));
        Assert.Contains(records, item => item.Event.EventType == AuditEventType.InvocationResult &&
            item.Event.ActorChain.Actors.Count == 1 && item.Event.ActorChain.Actors[0].ActorId == actor.ActorId &&
            item.Event.SoftwareIdentity.Value == actor.SoftwareIdentity);
        Assert.Single(fixture.Repository.Read(sessionId)!.Metadata.Annotations);
    }

    [Fact]
    public async Task CompetingCommandsCannotBothAppendTheSameOwnerVersion()
    {
        using var fixture = new Fixture();
        await using var host = fixture.Host();
        var endpoint = fixture.Bind(host); var session = Guid.NewGuid();
        Value(await endpoint.BootstrapAsync(session, "Replay", Configuration(), Cancellation));
        var left = Options(); var right = Options();
        var outcomes = await Task.WhenAll(endpoint.CreateAnnotationAsync(Append(session, left, 1, "left"), left, Cancellation),
            endpoint.CreateAnnotationAsync(Append(session, right, 1, "right"), right, Cancellation));
        Assert.Single(outcomes, result => result.Kind == OutcomeKind.Success);
        Assert.Single(fixture.Repository.Read(session)!.Metadata.Annotations);
        Assert.Equal(2, fixture.Repository.Read(session)!.Version);
    }

    [Fact]
    public async Task InvalidConfigurationAndCancelledBootstrapHaveNoDurableOwnerEffect()
    {
        using var fixture = new Fixture(); await using var host = fixture.Host(); var endpoint = fixture.Bind(host);
        var invalid = Guid.NewGuid();
        Assert.Equal(OutcomeKind.Failure, (await endpoint.BootstrapAsync(invalid, "bad", new(), Cancellation)).Kind);
        Assert.Null(fixture.Repository.Read(invalid));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var id = Guid.NewGuid();
        var cancellation = await endpoint.BootstrapAsync(id, "cancelled", Configuration(), cancelled.Token);
        Assert.Equal(OutcomeKind.Cancelled, cancellation.Kind);
        Assert.Equal(ArcForges.Contracts.Foundation.V1.EffectCertainty.DidNotHappen, cancellation.CancellationEffect);
        Assert.Null(fixture.Repository.Read(id));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-device")]
    [InlineData("unknown-constraint")]
    [InlineData("duplicate")]
    public async Task BootstrapRequiresCurrentEnabledUnambiguousOwnerWriteGrant(string refusal)
    {
        using var fixture = new Fixture(); await using var host = fixture.Host(); var endpoint = fixture.Bind(host);
        var current = (await fixture.Session.ReadCurrentAsync(Cancellation))!;
        const string capability = "IScopeOperations.CreateAnnotation";
        var original = Assert.Single(current.Permissions, grant => grant.CapabilityKey == capability);
        var constraints = refusal switch
        {
            "wrong-device" => new[] { PermissionConstraints.DeviceBound(new DeviceId(Guid.NewGuid())) },
            "unknown-constraint" => ["unknown:constraint"],
            _ => Array.Empty<string>(),
        };
        var grant = new PermissionGrantRecord(original.IssuerKey, original.PrincipalKey, capability, original.ScopeKey,
            refusal == "denied" ? PermissionGrantState.ExplicitlyDenied : PermissionGrantState.Granted, constraints,
            refusal == "future" ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow.AddMinutes(-5),
            refusal == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(10), "2");
        var permissions = refusal == "missing" ? Array.Empty<PermissionGrantRecord>() :
            refusal == "duplicate" ? [grant, grant] : new[] { grant };
        fixture.Session.Refresh(new(current.Actors, current.Scope, current.Origin, current.Transport, 2,
            current.ValidFrom, current.ExpiresAt, current.Trust, permissions,
            refusal == "disabled" ? [] : current.EnabledCapabilities));
        var id = Guid.NewGuid();
        Assert.Equal(OutcomeKind.Failure, (await endpoint.BootstrapAsync(id, "denied", Configuration(), Cancellation)).Kind);
        Assert.Null(fixture.Repository.Read(id));
    }

    [Fact]
    public async Task GeneratedValidBootstrapMustFitTheActualReadableGatewayBeforeOwnerCommit()
    {
        using var fixture = new Fixture(); await using var host = fixture.Host(); var endpoint = fixture.Bind(host);
        var id = Guid.NewGuid(); var configuration = Configuration();
        for (var index = 1; index <= 200; index++)
        {
            var channel = configuration.Channels[0].Clone(); channel.ChannelId = UuidBoundary.ToWire(Guid.NewGuid());
            configuration.Channels.Add(channel);
        }
        var response = new ScopeOperationsServiceGetSessionResponse
        {
            Meta = new() { CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()), RecoveryGeneration = 1 },
            Value = new() { Session = new() { SessionId = UuidBoundary.ToWire(id), Name = "Replay", Configuration = configuration, Revision = new() { Value = 1 } } },
        };
        Assert.True(ContractShapeValidation.IsValid(response));
        Assert.Equal(OutcomeKind.Failure, (await endpoint.BootstrapAsync(id, "Replay", configuration, Cancellation)).Kind);
        Assert.Null(fixture.Repository.Read(id));
        var valid = Guid.NewGuid(); var normal = Configuration(); Value(await endpoint.BootstrapAsync(valid, "Replay", normal, Cancellation));
        var read = await endpoint.GetSessionAsync(Read(valid, 1), Options(), Cancellation);
        Assert.True(read.TryGetValue(out var actual), string.Join("; ", fixture.Audit.Query(new(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), 100))
            .Select(item => item.Event.DecisionDetail switch
            {
                AuditSecurityDecisionDetail detail => $"{detail.Record.Kind}/{detail.Record.Point}/{detail.Record.FailedStep}/{detail.Record.ReasonCode}/{detail.Record.RegisteredCode}",
                AuditDecisionResultDetail detail => $"result:{detail.Record.Result}/{detail.Record.FailureCode}",
                _ => item.Event.EventType.ToString(),
            })));
        Assert.Equal(normal, actual!.Value.Session.Configuration);
    }

    [Fact]
    public async Task ShutdownCancelsPendingContextAndNeverDisposesBorrowedOwnerStore()
    {
        using var fixture = new Fixture(); var host = fixture.Host(); var provider = new BlockedContext(fixture.Instance);
        var endpoint = fixture.Bind(host, [provider]); var session = Guid.NewGuid();
        Value(await endpoint.BootstrapAsync(session, "Replay", Configuration(), Cancellation));
        var pending = endpoint.GetSessionAsync(Read(session, 1), Options(), Cancellation);
        await provider.Entered.Task.WaitAsync(Cancellation);
        var first = host.DisposeAsync().AsTask(); var second = host.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        Assert.NotEqual(OutcomeKind.Success, (await pending).Kind);
        Assert.True(provider.HostLifetime.IsCancellationRequested);
        Assert.NotNull(fixture.Repository.Read(session));
        Assert.Equal(OutcomeKind.Failure, (await endpoint.GetSessionAsync(Read(session, 1), Options(), Cancellation)).Kind);
        provider.Release.TrySetResult([]); // The unavailable external callback is owned by the fake and explicitly released.
    }

    private static ProductInvocationOptions Options() => new(Guid.NewGuid(), Guid.NewGuid(),
        new FrozenContext { ProfileVersion = "profile.1", PermissionsVersion = "permissions.1" });
    private static ScopeOperationsServiceGetSessionRequest Read(Guid session, ulong version) => new()
    { SessionId = UuidBoundary.ToWire(session), Meta = new() { CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()), ExpectedNative = new() { Value = version } } };
    private static ScopeOperationsServiceCreateAnnotationRequest Append(Guid session, ProductInvocationOptions options, ulong version, string text) => new()
    {
        SessionId = UuidBoundary.ToWire(session), AnnotationId = UuidBoundary.ToWire(Guid.NewGuid()),
        Range = new() { From = 0, Count = 0 }, Text = text,
        Meta = new() { CommandId = UuidBoundary.ToWire(options.CommandId), CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()), ExpectedNative = new() { Value = version } },
    };
    private static ScopeConfiguration Configuration()
    {
        var channel = new ChannelDefinition { ChannelId = UuidBoundary.ToWire(Guid.NewGuid()), Name = "Voltage", Unit = "V",
            SampleType = "binary64", Rate = new() { Numerator = 1, Denominator = 1 }, Calibration = new() { Scale = 1, Offset = 0, Unit = "V" } };
        var configuration = new ScopeConfiguration { ConfigurationId = UuidBoundary.ToWire(Guid.NewGuid()), ParserProfile = "v1",
            Revision = new() { Value = 1 }, Framing = new() { Kind = "canonicalReplay", Start = ByteString.Empty, End = ByteString.Empty, Header = false, ByteOrder = "little" } };
        configuration.Channels.Add(channel); return configuration;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "app02-product-" + Guid.NewGuid().ToString("N"));
        private readonly Guid partition = Guid.NewGuid();
        private readonly Guid storeId = Guid.NewGuid();
        private SqliteStore store;
        private CapabilityLeaseManager leases;
        private ApprovalCoordinator approvals;
        private readonly StepUpCoordinator stepUp;
        internal HumanPrincipal Owner { get; } = new(new RealmId(Guid.NewGuid()), new UserId(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        internal InstanceIdentity Instance { get; } = new(new InstallationIdentity(AppIdentity.ArcScope, new DeviceId(Guid.NewGuid()), new InstallationId(Guid.NewGuid())), new InstanceId(Guid.NewGuid()), 1);
        internal DecisionScope Scope { get; }
        internal ProductHostActorSession Session { get; }
        internal AnnotationSessionRepository Repository { get; private set; }
        internal AuditStore Audit { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(directory); Scope = new(Owner.Realm, null);
            store = new(Path.Combine(directory, "owner.db"), storeId, new HostWriteAuthorization());
            Repository = new(store, partition, Owner.Id, Clock.System);
            Audit = new(Path.Combine(directory, "audit.db"), Owner.Realm, Owner.Id, new(Guid.NewGuid(), 30));
            var chain = new ActorChain(Owner, Instance.Installation.DeviceId, Instance.Installation.InstallationId,
                new SessionId(Guid.NewGuid()), Instance.InstanceId, []);
            var now = Clock.System.GetCurrentInstant();
            var keys = new[] { "IScopeOperations.GetSession", "IScopeOperations.CreateAnnotation" };
            var grants = keys.Select(key => new PermissionGrantRecord("owner.local", $"principal:{Owner.Realm.Value:N}/{Owner.Id.Value:N}", key,
                Scope.Key, PermissionGrantState.Granted, [], DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10), "1"));
            Session = new(new(chain, Scope, DecisionOrigin.Local, TransportSessions.InProcess, 1,
                new(now.UnixSeconds - 60, now.Nanoseconds), new(now.UnixSeconds + 600, now.Nanoseconds), TrustVerdict.Verified, grants, keys));
            var authority = new ProductAuthority(Repository, Instance, Owner, Scope, Clock.System, Session);
            leases = new(Clock.System, new ProductLeaseStore(store, partition, Owner, Clock.System),
                new CapabilityLeaseEventAuditSink(new CapabilityLeaseAuditAdapter(Audit), new("arcscope.desktop/1")), authority);
            approvals = new(Clock.System, new ProductApprovalStore(store, partition, Owner, Clock.System));
            stepUp = new(Clock.System, new NoSensitiveAuthentication(), new NoLocalPresence());
        }
        internal void ReopenOwnerStore()
        {
            store.Dispose();
            store = new(Path.Combine(directory, "owner.db"), storeId, new HostWriteAuthorization());
            Repository = new(store, partition, Owner.Id, Clock.System);
            var authority = new ProductAuthority(Repository, Instance, Owner, Scope, Clock.System, Session);
            leases = new(Clock.System, new ProductLeaseStore(store, partition, Owner, Clock.System),
                new CapabilityLeaseEventAuditSink(new CapabilityLeaseAuditAdapter(Audit), new("arcscope.desktop/1")), authority);
            approvals = new(Clock.System, new ProductApprovalStore(store, partition, Owner, Clock.System));
        }
        internal CapabilityLeaseManager Leases => leases;
        internal ProductComposition Host() => new(store, partition, Instance, Owner, Scope, Clock.System, 1,
            approvals, stepUp, leases, new SecurityDecisionAuditSink(Audit, new("arcscope.desktop/1")), new LocalTrace());
        internal ProductComposition.ProductEndpoint Bind(ProductComposition host, IReadOnlyList<IContextProvider>? context = null,
            IProductHostActorSource? actorSource = null) =>
            host.BindHostSession(actorSource ?? Session, new UiAvailability(), context ?? [],
                (actors, scope, origin) => new DecisionResultRecorder(Audit, actors,
                    new(actors.Actors.Count == 0 ? "arcscope.desktop/1" : actors.Actors[^1].SoftwareIdentity), scope, origin));
        public void Dispose() { Session.Dispose(); Audit.Dispose(); store.Dispose(); Directory.Delete(directory, recursive: true); }
    }
    // Actual UI/OS identity, availability and presence are unavailable in this ordinary component test.
    // All security decision, approval/lease core, audit and owner/journal persistence are real production components.
    private sealed class HostWriteAuthorization : IStoreAuthorization { public bool CanWrite(WriteCommand command) => true; }
    private sealed class UiAvailability : ICapabilityProvider
    {
        public bool IsBoundToCapability(ActionKey action, string capability) => action.Value == capability && capability is "IScopeOperations.GetSession" or "IScopeOperations.CreateAnnotation";
        public ValueTask<Outcome<AvailabilityResult>> EvaluateAvailabilityAsync(ActionKey action, FrozenContextSnapshot context, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Outcome.Success(AvailabilityResult.Available)); }
    }
    private sealed class LocalTrace : IInvocationTraceSink
    { public ValueTask<Outcome<bool>> WriteAsync(InvocationTraceRecord record, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Outcome.Success(true)); } }
    private sealed class NoSensitiveAuthentication : IStepUpAuthenticator
    { public ValueTask<StepUpAuthentication?> AuthenticateAsync(StepUpChallenge challenge, CancellationToken token = default) => throw new InvalidOperationException("These operations never require an identity step-up."); }
    private sealed class NoLocalPresence : ILocalPresenceVerifier
    { public ValueTask<bool> ConfirmLocalPresenceAsync(StepUpChallenge challenge, CancellationToken token = default) => throw new InvalidOperationException("These operations never require an OS presence assertion."); }
    private sealed class BlockedContext(InstanceIdentity owner) : IContextProvider
    {
        public InstanceIdentity Owner => owner;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyList<IMessage>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken HostLifetime { get; private set; }
        public ValueTask<IReadOnlyList<IMessage>> ProvideMessagesAsync(InstanceIdentity expectedOwner, CancellationToken token = default)
        { HostLifetime = token; Entered.TrySetResult(); return new(Release.Task); }
    }
}
