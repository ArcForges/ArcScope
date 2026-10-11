// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Foundation.Execution;
using Xunit;
using Instant = ArcForges.Foundation.Instant;

namespace ArcForges.ArcScope.Tests.Annotations;

public sealed class AnnotationWritePathTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACreateCommitsOnceThroughTheStoreAndRecordsTheEntry()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var command = fixture.Create(text: "ringing after the edge");

        var receipt = OutcomeAssert.Success(await fixture.WritePath.CreateAsync(command, Token));

        Assert.Equal(new AnnotationWriteReceipt(command.Command, command.Annotation, new NativeRevision(1), Replayed: false), receipt);
        var commit = Assert.Single(fixture.Store.Commits);
        Assert.Equal(AnnotationOperations.Create, commit.Operation);
        Assert.Equal(new NativeRevision(0), commit.ExpectedRevision);
        Assert.Equal(command.Fingerprint(), commit.Fingerprint);
        var stored = fixture.Store.Get(command.Annotation)!;
        Assert.Equal(command.Session, stored.Session);
        Assert.Equal(command.Range, stored.Range);
        var entry = Assert.Single(stored.Entries);
        Assert.Equal(command.Text, entry.Text);
        Assert.Equal(command.Actor, entry.Actor);
        Assert.Equal(AnnotationContentKind.NonAi, entry.ContentKind);
        Assert.Equal(command.Command, entry.Command);
        Assert.Equal(command.Fingerprint(), entry.CommandFingerprint);
        Assert.Equal(Instant.FromDateTimeOffset(AnnotationFixture.Start), entry.CommittedAt);
    }

    [Fact]
    public async Task RetryingTheSameCreateReplaysTheReceiptWithoutASecondEffect()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var command = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(command, Token));
        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        var replay = OutcomeAssert.Success(await fixture.WritePath.CreateAsync(command, Token));

        Assert.True(replay.Replayed);
        Assert.Equal(new NativeRevision(1), replay.Revision);
        Assert.Single(fixture.Store.Commits);
        Assert.Equal(Instant.FromDateTimeOffset(AnnotationFixture.Start), fixture.Store.Get(command.Annotation)!.Entries[0].CommittedAt);
    }

    [Fact]
    public async Task ReusingACommandIdentityForDifferentContentRefuses()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var command = fixture.Create(text: "first wording");
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(command, Token));

        var changed = fixture.Create(id: command.Annotation, command: command.Command, text: "second wording");
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(changed, Token), AnnotationFailures.ReusedCommand);

        // The same command identity on another annotation reaches the store, whose durable command log refuses it.
        var elsewhere = fixture.Create(command: command.Command);
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(elsewhere, Token), AnnotationFailures.ReusedCommand);
        Assert.Null(fixture.Store.Get(elsewhere.Annotation));
        Assert.Equal(2, fixture.Store.CommitCalls);
        Assert.Equal("first wording", fixture.Store.Get(command.Annotation)!.Entries[0].Text.Value);
    }

    [Fact]
    public async Task ASecondCreateOfAnExistingAnnotationRefusesAsADuplicate()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var first = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(first, Token));

        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(fixture.Create(id: first.Annotation), Token), AnnotationFailures.DuplicateIdentifier);
        Assert.Single(fixture.Store.Commits);
    }

    [Fact]
    public async Task ACreateOnAnUnregisteredSessionOrOutsideTheRecordedSamplesRefuses()
    {
        var unregistered = new AnnotationFixture();
        OutcomeAssert.Failure(await unregistered.WritePath.CreateAsync(unregistered.Create(), Token), AnnotationFailures.SessionUnavailable);
        Assert.Empty(unregistered.Store.Commits);

        var fixture = await new AnnotationFixture().WithSessionAsync(samples: 1_000);
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(fixture.Create(from: 990, count: 11), Token), AnnotationFailures.InvalidRequest);
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(fixture.Create(from: 1_000, count: 1), Token), AnnotationFailures.InvalidRequest);
        Assert.Empty(fixture.Store.Commits);
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(fixture.Create(from: 990, count: 10), Token));

        // A session that grows admits ranges over the newly recorded samples.
        OutcomeAssert.Success(await fixture.Sessions.RegisterAsync(new SessionTarget(fixture.Session, 2_000), Token));
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(fixture.Create(from: 1_500, count: 500), Token));
        OutcomeAssert.Failure(await fixture.Sessions.RegisterAsync(new SessionTarget(fixture.Session, 1_999), Token), AnnotationFailures.InvalidTransition);
    }

    [Fact]
    public async Task AnAppendAddsOneEntryAndNeverRewritesHistory()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create(text: "first");
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var before = fixture.Store.Get(create.Annotation)!.Entries[0];
        fixture.Time.Advance(TimeSpan.FromSeconds(30));

        var append = fixture.Append(create.Annotation, expected: 1, text: "second");
        var receipt = OutcomeAssert.Success(await fixture.WritePath.AppendAsync(append, Token));

        Assert.Equal(new NativeRevision(2), receipt.Revision);
        Assert.False(receipt.Replayed);
        var commit = fixture.Store.Commits[^1];
        Assert.Equal(AnnotationOperations.Append, commit.Operation);
        Assert.Equal(new NativeRevision(1), commit.ExpectedRevision);
        var stored = fixture.Store.Get(create.Annotation)!;
        Assert.Equal(2, stored.Entries.Count);
        Assert.Same(before, stored.Entries[0]);
        Assert.Equal("second", stored.Entries[1].Text.Value);
        Assert.Equal(Instant.FromDateTimeOffset(AnnotationFixture.Start.AddSeconds(30)), stored.Entries[1].CommittedAt);
        Assert.Equal(create.Range, stored.Range);
    }

    [Fact]
    public async Task AStaleOrMissingAppendRefusesBeforeAnyCommit()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token));

        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.RevisionMismatch);
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 3), Token), AnnotationFailures.RevisionMismatch);
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(new AnnotationId(Guid.NewGuid()), 1), Token), AnnotationFailures.NotFound);
        Assert.Equal(2, fixture.Store.CommitCalls);
    }

    [Fact]
    public async Task RetryingAnAppendReplaysItEvenAfterLaterAppends()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var append = fixture.Append(create.Annotation, 1);
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(append, Token));
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 2), Token));

        var replay = OutcomeAssert.Success(await fixture.WritePath.AppendAsync(append, Token));
        Assert.True(replay.Replayed);
        Assert.Equal(new NativeRevision(2), replay.Revision);
        Assert.Equal(3, fixture.Store.CommitCalls);

        var reworded = fixture.Append(create.Annotation, 1, text: "other words", command: append.Command);
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(reworded, Token), AnnotationFailures.ReusedCommand);
        // A create command identity cannot be reused as an append either.
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 3, command: create.Command), Token),
            AnnotationFailures.ReusedCommand);
        Assert.Equal(3, fixture.Store.CommitCalls);
    }

    [Fact]
    public async Task AnAppendBeyondTheHistoryOrTextBoundRefuses()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var longest = Seeded(fixture, Enumerable.Range(1, Annotation.MaxEntries).Select(_ => "n").ToArray());
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(longest.Id, Annotation.MaxEntries, "one more"), Token),
            AnnotationFailures.InvalidRequest);

        var large = Seeded(fixture, new string('x', AnnotationText.MaxUtf8Bytes - 4));
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(large.Id, 1, "12345"), Token), AnnotationFailures.InvalidRequest);
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(large.Id, 1, "1234"), Token));
        Assert.Single(fixture.Store.Commits);
    }

    [Fact]
    public async Task AProductHandlerChangeRecordsItsInvocationAndOriginKind()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var invocation = new InvocationId(Guid.NewGuid());
        var create = fixture.Create(actor: AnnotationActor.ProductHandler(fixture.Person, invocation), kind: AnnotationContentKind.AiGenerated);
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token));

        var stored = fixture.Store.Get(create.Annotation)!;
        Assert.Equal(invocation, stored.Entries[0].Actor.Invocation);
        Assert.Equal(AnnotationActorKind.ProductHandler, stored.Entries[0].Actor.Kind);
        Assert.Equal(AnnotationActorKind.Human, stored.Entries[1].Actor.Kind);
        Assert.Equal(new[] { AnnotationContentKind.AiGenerated, AnnotationContentKind.NonAi }, stored.OriginKinds);
    }

    [Fact]
    public async Task ConcurrentAppendsWithTheSameExpectedRevisionCommitExactlyOnce()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        fixture.Store.BeforeCommit = (_, _) => Task.Delay(5, Token);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1, "writer " + index), Token).AsTask()));

        Assert.Single(results, result => result.Kind == OutcomeKind.Success);
        Assert.All(results.Where(result => result.Kind != OutcomeKind.Success),
            result => OutcomeAssert.Failure(result, AnnotationFailures.RevisionMismatch));
        Assert.Equal(2, fixture.Store.CommitCalls);
        Assert.Equal(1, fixture.Store.MaxConcurrentCommitsPerAnnotation);
        Assert.Equal(new NativeRevision(2), fixture.Store.Get(create.Annotation)!.Revision);
    }

    [Fact]
    public async Task WritesSerializePerAnnotationButNotAcrossAnnotations()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var held = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(held, Token));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.BeforeCommit = async (commit, _) =>
        {
            if (commit.Next.Id == held.Annotation && commit.Next.Revision.Value == 2)
            {
                entered.TrySetResult();
                await gate.Task;
            }
        };

        var blocked = fixture.WritePath.AppendAsync(fixture.Append(held.Annotation, 1, "slow"), Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var queued = fixture.WritePath.AppendAsync(fixture.Append(held.Annotation, 2, "queued"), Token).AsTask();

        // Another annotation commits while the first one's commit is still in flight.
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(fixture.Create(), Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.False(queued.IsCompleted);
        Assert.Equal(3, fixture.Store.CommitCalls);

        gate.SetResult();
        Assert.Equal(new NativeRevision(2), OutcomeAssert.Success(await blocked).Revision);
        Assert.Equal(new NativeRevision(3), OutcomeAssert.Success(await queued).Revision);
        Assert.Equal(1, fixture.Store.MaxConcurrentCommitsPerAnnotation);
    }

    [Fact]
    public async Task CancellationBeforeDispatchProvesNoEffect()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        OutcomeAssert.Cancelled(await fixture.WritePath.CreateAsync(fixture.Create(), cancelled.Token), EffectCertainty.DidNotHappen);
        OutcomeAssert.Cancelled(await fixture.WritePath.AppendAsync(fixture.Append(new AnnotationId(Guid.NewGuid()), 1), cancelled.Token),
            EffectCertainty.DidNotHappen);
        Assert.Equal(0, fixture.Store.ReadCalls);
        Assert.Empty(fixture.Store.Commits);
    }

    [Fact]
    public async Task CancellingAWaiterLeavesNoLockAndNoEffect()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.BeforeCommit = async (_, _) =>
        {
            entered.TrySetResult();
            await gate.Task;
        };
        var owner = fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        using var waiterCancel = new CancellationTokenSource();
        var waiter = fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 2), waiterCancel.Token).AsTask();
        await waiterCancel.CancelAsync();
        OutcomeAssert.Cancelled(await waiter.WaitAsync(TimeSpan.FromSeconds(10), Token), EffectCertainty.DidNotHappen);

        gate.SetResult();
        OutcomeAssert.Success(await owner);
        Assert.Equal(2, fixture.Store.CommitCalls);
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 2), Token));
    }

    [Fact]
    public async Task CancellationDuringTheCommitLeavesTheEffectUnknown()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        using var cancel = new CancellationTokenSource();
        fixture.Store.BeforeCommit = async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
        };

        OutcomeAssert.Cancelled(await fixture.WritePath.CreateAsync(fixture.Create(), cancel.Token), EffectCertainty.Unknown);
    }

    [Fact]
    public async Task StoreFailuresMapToClosedCodesWithTheProvableEffect()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));

        fixture.Store.BeforeCommit = (_, _) => throw new IOException("disk full");
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.Unexpected,
            EffectCertainty.Unknown);
        fixture.Store.BeforeCommit = null;

        fixture.Store.CommitResult = _ => Outcome.Failure<AnnotationCommitReceipt>(TypedFailure.Create("perm.capability_denied"));
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.Unexpected,
            EffectCertainty.Unknown);

        foreach (var refusal in AnnotationFailures.CommitRefusals)
        {
            fixture.Store.CommitResult = _ => Outcome.Failure<AnnotationCommitReceipt>(TypedFailure.Create(refusal));
            OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), refusal);
        }

        fixture.Store.CommitResult = commit => Outcome.Success(new AnnotationCommitReceipt(commit.Command, commit.Next.Id, new NativeRevision(7), false));
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.Unexpected,
            EffectCertainty.Unknown);
        fixture.Store.CommitResult = commit => Outcome.Success(new AnnotationCommitReceipt(commit.Command, new AnnotationId(Guid.NewGuid()), commit.Next.Revision, false));
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.Unexpected,
            EffectCertainty.Unknown);
        fixture.Store.CommitResult = commit => Outcome.Success(new AnnotationCommitReceipt(new CommandId(Guid.NewGuid()), commit.Next.Id, commit.Next.Revision, false));
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.Unexpected,
            EffectCertainty.Unknown);

        fixture.Store.CommitResult = _ => Outcome.Cancelled<AnnotationCommitReceipt>(EffectCertainty.DidNotHappen);
        OutcomeAssert.Cancelled(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), EffectCertainty.DidNotHappen);

        // A concurrent writer in another process committed the same command first: the store's durable log replays it.
        fixture.Store.CommitResult = commit => Outcome.Success(new AnnotationCommitReceipt(commit.Command, commit.Next.Id, new NativeRevision(2), true));
        var replay = OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token));
        Assert.True(replay.Replayed);
        Assert.Equal(new NativeRevision(1), fixture.Store.Get(create.Annotation)!.Revision);
    }

    [Fact]
    public async Task UntrustworthyStoredStateRefusesWithoutACommit()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        fixture.Store.ReadFault = new InvalidDataException("corrupt row");
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(fixture.Create(), Token), AnnotationFailures.IntegrityFailed);
        fixture.Store.ReadFault = new IOException("locked");
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(new AnnotationId(Guid.NewGuid()), 1), Token),
            AnnotationFailures.Unexpected, EffectCertainty.DidNotHappen);
        fixture.Store.ReadFault = null;

        fixture.Sessions.FindFault = new IOException("catalog unavailable");
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(fixture.Create(), Token), AnnotationFailures.Unexpected, EffectCertainty.DidNotHappen);
        Assert.Empty(fixture.Store.Commits);
    }

    [Fact]
    public async Task AStoreAnswerForAnotherAnnotationIsAnIntegrityFailure()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var other = Seeded(fixture, "foreign");
        var misdirected = new MisdirectedStore(fixture.Store, other);
        var writePath = new AnnotationWritePath(misdirected, fixture.Sessions, new Clock(fixture.Time));

        OutcomeAssert.Failure(await writePath.AppendAsync(fixture.Append(new AnnotationId(Guid.NewGuid()), 1), Token), AnnotationFailures.IntegrityFailed);
        Assert.Empty(fixture.Store.Commits);
    }

    [Fact]
    public void OnlyTheWritePathCanBuildACommitAndTheStoreHasOneMutation()
    {
        Assert.Empty(typeof(AnnotationCommit).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(typeof(AnnotationCommit).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance), constructor => Assert.True(constructor.IsAssembly));
        var methods = typeof(IAnnotationStore).GetMethods();
        Assert.Equal(new[] { "CommitAsync", "ReadAsync", "ReadSessionAsync" }, methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Single(methods, method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(AnnotationCommit)));
        var creators = typeof(AnnotationWritePath).Assembly.GetTypes()
            .Where(type => type != typeof(AnnotationCommit) && type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(method => method.ReturnType == typeof(AnnotationCommit)))
            .ToArray();
        Assert.Empty(creators);
    }

    private static Annotation Seeded(AnnotationFixture fixture, params string[] texts)
    {
        var annotation = new Annotation(new AnnotationId(Guid.NewGuid()), fixture.Session, new SampleRange(0, 10),
            texts.Select((text, index) => new AnnotationEntry(new NativeRevision((ulong)index + 1), AnnotationText.Create(text),
                AnnotationActor.Human(fixture.Person), AnnotationContentKind.NonAi, Instant.FromDateTimeOffset(AnnotationFixture.Start),
                new CommandId(Guid.NewGuid()), new string('b', 64))));
        fixture.Store.Seed(annotation);
        return annotation;
    }

    private sealed class MisdirectedStore(InMemoryAnnotationStore inner, Annotation answer) : IAnnotationStore
    {
        public ValueTask<Annotation?> ReadAsync(AnnotationId annotation, CancellationToken cancellationToken) => ValueTask.FromResult<Annotation?>(answer);

        public ValueTask<IReadOnlyList<Annotation>> ReadSessionAsync(SessionTargetId session, AnnotationPageCursor? after, int limit,
            CancellationToken cancellationToken) => inner.ReadSessionAsync(session, after, limit, cancellationToken);

        public ValueTask<Outcome<AnnotationCommitReceipt>> CommitAsync(AnnotationCommit commit, CancellationToken cancellationToken) =>
            inner.CommitAsync(commit, cancellationToken);
    }
}
