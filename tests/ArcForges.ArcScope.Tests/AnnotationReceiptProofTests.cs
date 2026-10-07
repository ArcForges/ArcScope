// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using ArcForges.ArcScope.Core.Application;
using ArcForges.ArcScope.Core.Infrastructure;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Persistence.Sqlite;
using ArcForges.Sdk.Contracts.V1;
using ArcForges.Security;
using ArcForges.Security.Decisions;
using ArcForges.Security.Leases;
using Google.Protobuf;
using Xunit;
using InstanceHealth = ArcForges.Contracts.Foundation.V1.InstanceHealth;

namespace ArcForges.ArcScope.Tests;

public sealed class AnnotationReceiptProofTests
{
    [Fact]
    public async Task ExactCommittedDataProofSurvivesReopenWithoutMutatingCallerOrGrantingAuthority()
    {
        using var fixture = new Fixture();
        await fixture.CommitAsync();
        fixture.Reopen();
        var proof = Assert.IsType<AnnotationOwnerOperations.CommittedAnnotationProof>(
            fixture.Owner.TryMatchCommittedAnnotation(fixture.Target, fixture.Invocation, fixture.Request));
        Assert.Equal(fixture.Session, proof.SessionId);
        Assert.Equal(UuidBoundary.FromWire(fixture.Invocation.CommandId), proof.CommandId);
        Assert.Equal(1UL, proof.OriginalExpectedNative);
        Assert.Equal(2, proof.CommittedVersion);
        Assert.Equal(2, proof.CurrentVersion);
        Assert.Null(fixture.Request.Meta.ApplicationScope);
        Assert.False(fixture.Request.Meta.HasRecoveryGeneration);
        var copy = proof.NormalizedArgumentFingerprint.ToByteArray();
        copy[0] ^= 255;
        Assert.NotEqual(copy, proof.NormalizedArgumentFingerprint.ToByteArray());
        Assert.Single(fixture.Repository.Read(fixture.Session)!.Metadata.Annotations);
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("text")]
    [InlineData("command")]
    [InlineData("version")]
    [InlineData("recovery")]
    [InlineData("unknown-wire")]
    [InlineData("target")]
    public async Task AStoredReceiptCannotProveDifferentOrMalformedInvocationFacts(string substitution)
    {
        using var fixture = new Fixture();
        await fixture.CommitAsync();
        var raw = fixture.Request.Clone();
        var invocation = fixture.Invocation.Clone();
        var target = fixture.Target;
        switch (substitution)
        {
            case "arguments": raw.Text = "other proposal"; break;
            case "text": raw.Text = "other proposal"; invocation.Arguments = AnnotationOperationCodec.Encode(raw); break;
            case "command": invocation.CommandId = UuidBoundary.ToWire(Guid.NewGuid()); break;
            case "version": raw.Meta.ExpectedNative.Value = 2; invocation.ExpectedNative = raw.Meta.ExpectedNative.Clone(); invocation.Arguments = AnnotationOperationCodec.Encode(raw); break;
            case "recovery": raw.Meta.RecoveryGeneration = 2; invocation.Arguments = AnnotationOperationCodec.Encode(raw); break;
            case "unknown-wire": raw = ScopeOperationsServiceCreateAnnotationRequest.Parser.ParseFrom(raw.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray()); break;
            case "target": target = new(new(fixture.Instance.Installation, new InstanceId(Guid.NewGuid()), 1), fixture.Target.Descriptor, InstanceHealth.Ready, true); break;
        }
        Assert.Null(fixture.Owner.TryMatchCommittedAnnotation(target, invocation, raw));
        Assert.Equal(2, fixture.Repository.Read(fixture.Session)!.Version);
        Assert.Single(fixture.Repository.Read(fixture.Session)!.Receipts);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "app02-receipt-proof-" + Guid.NewGuid().ToString("N"));
        private readonly Guid storeId = Guid.NewGuid();
        private readonly Guid partition = Guid.NewGuid();
        private readonly HumanPrincipal principal = new(new RealmId(Guid.NewGuid()), new UserId(Guid.NewGuid()), HumanIdentityKind.LocalHuman);
        private SqliteStore store;
        internal Guid Session { get; } = Guid.NewGuid();
        internal InstanceIdentity Instance { get; } = new(new(AppIdentity.ArcScope, new DeviceId(Guid.NewGuid()), new InstallationId(Guid.NewGuid())), new InstanceId(Guid.NewGuid()), 1);
        internal CapabilityTarget Target { get; }
        internal ScopeOperationsServiceCreateAnnotationRequest Request { get; }
        internal Invocation Invocation { get; }
        internal AnnotationSessionRepository Repository => new(store, partition, principal.Id, Clock.System);
        internal AnnotationOwnerOperations Owner => new(Repository, Instance, new(principal.Realm, null), principal, new NoAuthority(), Clock.System, 1);
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            store = Open();
            Target = new(Instance, CapabilityRegistry.CreateInitial().Find("IScopeOperations.CreateAnnotation")!.Descriptor, InstanceHealth.Ready, true);
            Request = new()
            {
                SessionId = UuidBoundary.ToWire(Session),
                AnnotationId = UuidBoundary.ToWire(Guid.NewGuid()),
                Range = new() { From = 0, Count = 0 },
                Text = "actual committed data",
                Meta = new() { CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()), ExpectedNative = new() { Value = 1 } }
            };
            Invocation = new()
            {
                CommandId = UuidBoundary.ToWire(Guid.NewGuid()),
                Capability = "IScopeOperations.CreateAnnotation",
                Arguments = AnnotationOperationCodec.Encode(Request),
                ExpectedNative = Request.Meta.ExpectedNative.Clone()
            };
        }
        private SqliteStore Open() => new(Path.Combine(directory, "owner.db"), storeId, new HostWriteAuthorization());
        internal void Reopen() { store.Dispose(); store = Open(); }
        internal async Task CommitAsync()
        {
            Assert.True(await Repository.TryCreateAsync(Session, new() { SessionId = Request.SessionId.Clone() }, TestContext.Current.CancellationToken));
            var normalized = Request.Clone();
            normalized.Meta.CommandId = Invocation.CommandId.Clone();
            normalized.Meta.ApplicationScope = Instance.Installation.ToApplicationScope();
            normalized.Meta.RecoveryGeneration = 1;
            var origin = new ArcForges.Contracts.Foundation.V1.ContentOrigin
            {
                Profile = "arcforges.content-origin.v1",
                OriginId = new ContentOriginId(Guid.NewGuid()).ToWire(),
                ContentUnitId = new ContentUnitId(UuidBoundary.FromWire(Request.AnnotationId)).ToWire(),
                ProducerKind = "human",
                OmittedParentCount = 0,
                PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Request.Text)))
            };
            origin.Kinds.Add("nonAi");
            var note = new ScopeAnnotation { AnnotationId = Request.AnnotationId.Clone(), Range = Request.Range.Clone(), Text = Request.Text, Origin = origin };
            Assert.True(await Repository.TryAppendAsync(Repository.Read(Session)!, note, UuidBoundary.FromWire(Invocation.CommandId),
                SHA256.HashData(normalized.ToByteArray()), TestContext.Current.CancellationToken));
        }
        public void Dispose() { store.Dispose(); Directory.Delete(directory, recursive: true); }
    }

    // The proof reads real SQLite facts. Unavailable host write authority is the only admitted fake;
    // attempting to use the unrelated lease port proves this data matcher must not authorize anything.
    private sealed class HostWriteAuthorization : IStoreAuthorization { public bool CanWrite(WriteCommand command) => true; }
    private sealed class NoAuthority : ILeaseUseValidator
    { public ValueTask<LeaseUseVerdict> ValidateAsync(LeaseUse use, CancellationToken cancellationToken) => throw new InvalidOperationException("Data proof is not authorization."); }
}
