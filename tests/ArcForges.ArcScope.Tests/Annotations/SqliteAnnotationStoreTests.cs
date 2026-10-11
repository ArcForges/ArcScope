// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.ArcScope.Infrastructure.Annotations;
using ArcForges.ArcScope.Tests.Storage;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Foundation.Execution;
using Google.Protobuf;
using Xunit;
using Instant = ArcForges.Foundation.Instant;

namespace ArcForges.ArcScope.Tests.Annotations;

public sealed class SqliteAnnotationStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAppendAndReadRoundTripThroughTheRealStoreAndSurviveARestart()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var invocation = new InvocationId(Guid.NewGuid());
        var create = fixture.Create(text: "ringing after the edge µs — \U0001F50D\ttabbed\nline");
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        var append = fixture.Append(create.Annotation, 1, text: "suggested cause: ground bounce",
            actor: AnnotationActor.ProductHandler(fixture.Person, invocation), kind: AnnotationContentKind.AiGenerated);
        var receipt = OutcomeAssert.Success(await fixture.WritePath.AppendAsync(append, Token));
        Assert.Equal(new AnnotationWriteReceipt(append.Command, create.Annotation, new NativeRevision(2), Replayed: false), receipt);

        var before = OutcomeAssert.Success(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token));
        fixture.Reopen();
        var after = OutcomeAssert.Success(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token));

        Assert.Equal(AnnotationPayload.Encode(before), AnnotationPayload.Encode(after));
        Assert.Equal(create.Session, after.Session);
        Assert.Equal(create.Range, after.Range);
        Assert.Equal(new NativeRevision(2), after.Revision);
        Assert.Equal(create.Text, after.Entries[0].Text);
        Assert.Equal(AnnotationActor.Human(fixture.Person), after.Entries[0].Actor);
        Assert.Equal(Instant.FromDateTimeOffset(fixture.Start), after.Entries[0].CommittedAt);
        Assert.Equal(AnnotationActor.ProductHandler(fixture.Person, invocation), after.Entries[1].Actor);
        Assert.Equal(append.Fingerprint(), after.Entries[1].CommandFingerprint);
        Assert.Equal(new[] { AnnotationContentKind.AiGenerated, AnnotationContentKind.NonAi }, after.OriginKinds);

        // Each commit went through the shared mechanism's journal with its typed native revision and command identity.
        var journal = fixture.Journal().Where(entry => entry.AggregateKind == SqliteAnnotationStore.AggregateKind).ToArray();
        Assert.Equal(new[] { create.Command, append.Command }, journal.Select(entry => entry.CommandId));
        Assert.Equal(new[] { AnnotationOperations.Create, AnnotationOperations.Append }, journal.Select(entry => entry.Operation));
        Assert.Equal(invocation.Value, journal[1].CorrelationId);
    }

    [Fact]
    public async Task TheStoredOriginCarriesTheKindUnionTheExactPayloadHashTheProducerAndItsLineage()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var first = Assert.IsType<StoredAnnotation>(SqliteAnnotationStore.Load(fixture.Store.Shared, create.Annotation.Value));
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1,
            actor: AnnotationActor.ProductHandler(fixture.Person, new InvocationId(Guid.NewGuid())), kind: AnnotationContentKind.AiManipulated), Token));
        var second = Assert.IsType<StoredAnnotation>(SqliteAnnotationStore.Load(fixture.Store.Shared, create.Annotation.Value));

        Assert.Equal("arcforges.content-origin.v1", first.Origin.Profile);
        Assert.Equal(new[] { "nonAi" }, first.Origin.Kinds);
        Assert.Equal("human", first.Origin.ProducerKind);
        Assert.Empty(first.Origin.ParentOriginIds);
        Assert.Equal(create.Annotation.Value, ContentUnitId.FromWire(first.Origin.ContentUnitId).Value);
        Assert.Equal(new[] { "aiManipulated", "nonAi" }, second.Origin.Kinds);
        Assert.Equal("model", second.Origin.ProducerKind);
        Assert.Equal(first.Origin.OriginId, Assert.Single(second.Origin.ParentOriginIds));
        Assert.NotEqual(first.Origin.OriginId, second.Origin.OriginId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(AnnotationPayload.Encode(second.Annotation))), second.Origin.PayloadSha256);
        Assert.True(second.Writable);
    }

    [Fact]
    public async Task RetryingACommittedCommandAfterARestartReplaysWithoutASecondEffect()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        fixture.Reopen();
        int journalBefore = fixture.Journal().Count;

        var replay = OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var direct = OutcomeAssert.Success(await fixture.Store.Annotations.CommitAsync(CommitOf(create, fixture), Token));

        Assert.True(replay.Replayed);
        Assert.True(direct.Replayed);
        Assert.Equal(new NativeRevision(1), direct.Revision);
        Assert.Equal(journalBefore, fixture.Journal().Count);
    }

    [Fact]
    public async Task TheStoreItselfRefusesDuplicatesStaleRevisionsReuseAndMissingAnnotations()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var store = fixture.Store.Annotations;
        var create = fixture.Create();
        OutcomeAssert.Success(await store.CommitAsync(CommitOf(create, fixture), Token));

        // Another command naming the same annotation.
        OutcomeAssert.Failure(await store.CommitAsync(CommitOf(fixture.Create(id: create.Annotation), fixture), Token), AnnotationFailures.DuplicateIdentifier);
        // The same command identity with different content.
        OutcomeAssert.Failure(await store.CommitAsync(CommitOf(fixture.Create(id: create.Annotation, command: create.Command, text: "other"), fixture), Token),
            AnnotationFailures.ReusedCommand);

        var stored = (await store.ReadAsync(create.Annotation, Token))!;
        var append = fixture.Append(create.Annotation, 1);
        OutcomeAssert.Success(await store.CommitAsync(AppendOf(append, stored, fixture), Token));
        // A stale append built on revision 1 after revision 2 committed.
        OutcomeAssert.Failure(await store.CommitAsync(AppendOf(fixture.Append(create.Annotation, 1), stored, fixture), Token), AnnotationFailures.RevisionMismatch);
        // An append to an annotation that does not exist.
        var ghost = new Annotation(new AnnotationId(Guid.NewGuid()), fixture.Session, new SampleRange(1, 1), stored.Entries.Take(1));
        OutcomeAssert.Failure(await store.CommitAsync(AppendOf(fixture.Append(ghost.Id, 1), ghost, fixture), Token), AnnotationFailures.RevisionMismatch);

        Assert.Equal(new NativeRevision(2), (await store.ReadAsync(create.Annotation, Token))!.Revision);
    }

    [Fact]
    public async Task AnAppendThatRewritesEarlierHistoryIsRefusedWithoutEffect()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create(text: "original");
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var stored = (await fixture.Store.Annotations.ReadAsync(create.Annotation, Token))!;
        var original = stored.Entries[0];
        var rewritten = new Annotation(stored.Id, stored.Session, stored.Range, [new AnnotationEntry(original.Revision,
            AnnotationText.Create("rewritten"), original.Actor, original.ContentKind, original.CommittedAt, original.Command, original.CommandFingerprint)]);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Store.Annotations.CommitAsync(AppendOf(fixture.Append(create.Annotation, 1), rewritten, fixture), Token));

        var after = (await fixture.Store.Annotations.ReadAsync(create.Annotation, Token))!;
        Assert.Equal("original", Assert.Single(after.Entries).Text.Value);
    }

    [Fact]
    public async Task ACommandIdentityRecordedForAnotherAnnotationRefusesAndLeavesNoVisibleAnnotation()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var first = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(first, Token));
        var reuse = fixture.Create(command: first.Command, from: 7, count: 1, text: "another annotation");

        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(reuse, Token), AnnotationFailures.ReusedCommand);

        Assert.Null(await fixture.Store.Annotations.ReadAsync(reuse.Annotation, Token));
        var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token));
        Assert.Equal(new[] { first.Annotation }, page.Items.Select(item => item.Id));
        Assert.Equal(1, fixture.RawCount("SELECT COUNT(*) FROM arcscope_annotation_index WHERE annotation_id=$id", ("$id", reuse.Annotation.Value.ToString("D"))));

        var report = fixture.Reopen();
        Assert.Equal(1, report.OrphanIndexRowsRemoved);
        Assert.Equal(0, fixture.RawCount("SELECT COUNT(*) FROM arcscope_annotation_index WHERE annotation_id=$id", ("$id", reuse.Annotation.Value.ToString("D"))));
    }

    [Fact]
    public async Task SessionPagesFollowTheSessionOrderAndNeverTheInsertionOrder()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync(ulong.MaxValue);
        var other = new SessionTargetId(Guid.NewGuid());
        await fixture.WithSessionAsync(1_000, other);
        (ulong From, ulong Count)[] ranges = [(500, 10), (5, 3), (500, 2), (9_000_000_000_000_000_000, 1), (5, 3), (0, 1), (500, 2), (42, 8)];
        var created = new List<CreateAnnotation>();
        foreach (var (from, count) in ranges)
        {
            var command = fixture.Create(from: from, count: count);
            created.Add(command);
            OutcomeAssert.Success(await fixture.WritePath.CreateAsync(command, Token));
        }

        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(fixture.Create(session: other, from: 0, count: 1), Token));
        var expected = created.Select(command => new AnnotationPageCursor(command.Range.From, command.Range.Count, command.Annotation.Value))
            .Order(Comparer<AnnotationPageCursor>.Create(AnnotationPageCursor.Compare)).Select(cursor => cursor.Annotation).ToArray();

        var seen = new List<Guid>();
        AnnotationPageCursor? cursor = null;
        int pages = 0;
        do
        {
            var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 3, cursor), Token));
            seen.AddRange(page.Items.Select(item => item.Id.Value));
            cursor = page.Next;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task ACreateInterruptedBeforeItsCommitIsInvisibleRetryableAndCleanedUpOnReopen()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var interrupted = fixture.Create(from: 1, count: 1);
        var abandoned = fixture.Create(from: 2, count: 1);
        fixture.Store.Annotations.BeforeCanonicalWrite = _ => throw new IOException("simulated stop before the commit");

        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(interrupted, Token), AnnotationFailures.Unexpected, EffectCertainty.Unknown);
        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(abandoned, Token), AnnotationFailures.Unexpected, EffectCertainty.Unknown);
        fixture.Store.Annotations.BeforeCanonicalWrite = null;

        Assert.Empty(OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token)).Items);
        var retried = OutcomeAssert.Success(await fixture.WritePath.CreateAsync(interrupted, Token));
        Assert.False(retried.Replayed);
        var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token));
        Assert.Equal(new[] { interrupted.Annotation }, page.Items.Select(item => item.Id));

        var report = fixture.Reopen();
        Assert.Equal(1, report.OrphanIndexRowsRemoved);
        Assert.Equal(1, fixture.RawCount("SELECT COUNT(*) FROM arcscope_annotation_index"));
    }

    [Fact]
    public async Task AFailedCanonicalCommitRollsBackEveryPartAndTheSameCommandCanBeRetried()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create();
        int journalBefore = fixture.Journal().Count;
        fixture.Raw("CREATE TRIGGER test_receipt_failure BEFORE INSERT ON command_log BEGIN SELECT RAISE(ABORT, 'simulated receipt failure'); END;");

        OutcomeAssert.Failure(await fixture.WritePath.CreateAsync(create, Token), AnnotationFailures.Unexpected, EffectCertainty.Unknown);

        Assert.Equal(0, fixture.RawCount("SELECT COUNT(*) FROM store_content WHERE kind=$kind", ("$kind", SqliteAnnotationStore.AggregateKind)));
        Assert.Equal(0, fixture.RawCount("SELECT COUNT(*) FROM store_history"));
        Assert.Equal(0, fixture.RawCount("SELECT COUNT(*) FROM sync_outbox"));
        Assert.Equal(journalBefore, fixture.Journal().Count);
        Assert.Null(await fixture.Store.Annotations.ReadAsync(create.Annotation, Token));

        fixture.Raw("DROP TRIGGER test_receipt_failure");
        var receipt = OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        Assert.Equal(new NativeRevision(1), receipt.Revision);
        Assert.Equal(1, fixture.RawCount("SELECT COUNT(*) FROM command_log WHERE command_id=$id", ("$id", create.Command.Value.ToString("D"))));
    }

    [Fact]
    public async Task ATamperedOrNonCanonicalPayloadFailsClosedForReadsAndWrites()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create(text: "measured 3.3 V");
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var stored = (await fixture.Store.Annotations.ReadAsync(create.Annotation, Token))!;
        byte[] canonical = AnnotationPayload.Encode(stored);

        // Valid canonical JSON of different text: the origin no longer identifies the payload bytes.
        byte[] altered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonical).Replace("3.3 V", "5.0 V", StringComparison.Ordinal));
        fixture.Raw("UPDATE store_content SET payload=$payload WHERE id=$id", ("$payload", altered), ("$id", create.Annotation.Value.ToString("D")));
        OutcomeAssert.Failure(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token), AnnotationFailures.IntegrityFailed);
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.IntegrityFailed);
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token), AnnotationFailures.IntegrityFailed);

        // The same annotation with insignificant whitespace is not the canonical encoding.
        byte[] spaced = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(canonical));
        fixture.Raw("UPDATE store_content SET payload=$payload WHERE id=$id", ("$payload", spaced), ("$id", create.Annotation.Value.ToString("D")));
        OutcomeAssert.Failure(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token), AnnotationFailures.IntegrityFailed);

        var report = fixture.Reopen();
        Assert.Equal(1, report.UnreadableAnnotations);
        Assert.Equal(0, report.OrphanIndexRowsRemoved);
    }

    [Fact]
    public async Task AnUnsupportedOriginProfileLeavesTheAnnotationReadableButReadOnly()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        var stored = SqliteAnnotationStore.Load(fixture.Store.Shared, create.Annotation.Value)!;
        var future = stored.Origin.Clone();
        future.Profile = "arcforges.content-origin.v2";
        fixture.Raw("UPDATE store_content SET origin=$origin WHERE id=$id", ("$origin", future.ToByteArray()), ("$id", create.Annotation.Value.ToString("D")));

        var read = OutcomeAssert.Success(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token));
        Assert.Equal(create.Text, read.Entries[0].Text);
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1), Token), AnnotationFailures.IntegrityFailed);
        Assert.Equal(new NativeRevision(1), (await fixture.Store.Annotations.ReadAsync(create.Annotation, Token))!.Revision);
    }

    [Fact]
    public async Task AnIndexRowThatDisagreesFailsClosedAndTheNextOpenRepairsItFromTheCanonicalAnnotation()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create(from: 10, count: 5);
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        fixture.Raw("UPDATE arcscope_annotation_index SET range_from='00000000000000000011'");

        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token), AnnotationFailures.IntegrityFailed);

        var report = fixture.Reopen();
        Assert.Equal(1, report.IndexRowsRepaired);
        Assert.Equal("00000000000000000010", fixture.Raw("SELECT range_from FROM arcscope_annotation_index"));
        var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token));
        Assert.Equal(new[] { create.Annotation }, page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ASnapshotRestoreRecreatesTheIndexRowsOfTheReplayedCommitsAndOpensReadFirst()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var kept = fixture.Create(from: 1, count: 1);
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(kept, Token));
        var snapshot = fixture.Store.Shared.CreateSnapshot();
        Assert.NotNull(snapshot);
        var replayed = fixture.Create(from: 2, count: 1);
        var lost = fixture.Create(from: 3, count: 1);
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(replayed, Token));
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(lost, Token));
        fixture.Store.Dispose();
        // Damage the newest journal entry: the mechanism restores the snapshot and replays the verified entry after it.
        fixture.Raw("UPDATE journal SET checksum=zeroblob(32) WHERE sequence=(SELECT MAX(sequence) FROM journal)");

        var report = fixture.Reopen();

        Assert.False(report.WritesEnabled);
        Assert.True(report.Recovery.RecoveredThrough > report.Recovery.VerifiedThrough);
        Assert.Equal(1, report.IndexRowsRestored);
        var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token));
        Assert.Equal(new[] { kept.Annotation, replayed.Annotation }, page.Items.Select(item => item.Id));
        Assert.Null(await fixture.Store.Annotations.ReadAsync(lost.Annotation, Token));
        Assert.Equal(0, fixture.RawCount("SELECT COUNT(*) FROM arcscope_store_state"));
        // Read-first safe start: commits refuse until the recovery evidence is resolved.
        OutcomeAssert.Failure(await fixture.WritePath.AppendAsync(fixture.Append(kept.Annotation, 1), Token), AnnotationFailures.IntegrityFailed);
    }

    [Fact]
    public async Task AnInterruptedIndexCatchUpResumesOnTheNextOpen()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var create = fixture.Create();
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        // The state an open leaves when it stops after recording the catch-up marker and before recreating the rows.
        fixture.Raw("DELETE FROM arcscope_annotation_index; INSERT INTO arcscope_store_state(key,value) VALUES('index_catch_up_after','00000000000000000000');");

        var report = fixture.Reopen();

        Assert.True(report.WritesEnabled);
        Assert.Equal(1, report.IndexRowsRestored);
        Assert.Equal(0, fixture.RawCount("SELECT COUNT(*) FROM arcscope_store_state"));
        var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 10), Token));
        Assert.Equal(new[] { create.Annotation }, page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ConcurrentCommandsCommitIndependentlyAndOneStaleRaceWinsExactlyOnce()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        var creates = Enumerable.Range(0, 16).Select(index => fixture.Create(from: (ulong)index, count: 1)).ToArray();
        var created = await Task.WhenAll(creates.Select(command => fixture.WritePath.CreateAsync(command, Token).AsTask()));
        Assert.All(created, outcome => OutcomeAssert.Success(outcome));

        var target = creates[0].Annotation;
        var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.WritePath.AppendAsync(fixture.Append(target, 1), Token).AsTask()));

        Assert.Equal(1, racing.Count(outcome => outcome.Kind == OutcomeKind.Success));
        Assert.All(racing.Where(outcome => outcome.Kind != OutcomeKind.Success),
            outcome => OutcomeAssert.Failure(outcome, AnnotationFailures.RevisionMismatch));
        Assert.Equal(new NativeRevision(2), (await fixture.Store.Annotations.ReadAsync(target, Token))!.Revision);
        Assert.Equal(16, OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 50), Token)).Items.Count);
    }

    [Fact]
    public async Task TheUserInterfaceSurfaceWritesThroughTheSameStore()
    {
        using var fixture = await new SqliteStoreFixture().WithSessionAsync();
        Guid command = Guid.NewGuid(), annotation = Guid.NewGuid();

        OutcomeAssert.Success(await fixture.Surface.CreateAsync(command, annotation, fixture.Session.Value, 3, 4, "noted from the view", Token));
        fixture.Reopen();

        var read = OutcomeAssert.Success(await fixture.Queries.ReadAsync(new ReadAnnotation(new AnnotationId(annotation)), Token));
        Assert.Equal("noted from the view", read.Entries[0].Text.Value);
        Assert.Equal(new CommandId(command), read.Entries[0].Command);
    }

    [Fact]
    public void ThePayloadCodecRoundTripsAndRefusesEveryNonCanonicalForm()
    {
        var person = new UserId(Guid.NewGuid());
        var first = new AnnotationEntry(new NativeRevision(1), AnnotationText.Create("a \"quoted\" \\ back\u0001slash é\U0001F600"),
            AnnotationActor.Human(person), AnnotationContentKind.Unknown, new Instant(-5, 999_999_999), new CommandId(Guid.NewGuid()), new string('a', 64));
        var second = new AnnotationEntry(new NativeRevision(2), AnnotationText.Create("model text"),
            AnnotationActor.ProductHandler(person, new InvocationId(Guid.NewGuid())), AnnotationContentKind.AiGenerated,
            new Instant(1_790_000_000, 0), new CommandId(Guid.NewGuid()), new string('b', 64));
        var annotation = new Annotation(new AnnotationId(Guid.NewGuid()), new SessionTargetId(Guid.NewGuid()),
            new SampleRange(ulong.MaxValue - 1, 1), [first, second]);

        byte[] payload = AnnotationPayload.Encode(annotation);
        var decoded = AnnotationPayload.Decode(payload);

        Assert.Equal(payload, AnnotationPayload.Encode(decoded));
        Assert.Equal(annotation.Range, decoded.Range);
        Assert.Equal(first.Text, decoded.Entries[0].Text);
        Assert.Equal(first.CommittedAt, decoded.Entries[0].CommittedAt);
        Assert.Equal(second.Actor, decoded.Entries[1].Actor);
        string json = Encoding.UTF8.GetString(payload);
        Assert.StartsWith("{\"annotation\":\"", json, StringComparison.Ordinal);
        Assert.Contains("\"profile\":\"arcscope.annotation.v1\"", json, StringComparison.Ordinal);

        foreach (string broken in new[]
        {
            json.Replace("arcscope.annotation.v1", "arcscope.annotation.v2", StringComparison.Ordinal),
            json.Replace("\"revision\":\"2\"", "\"revision\":\"02\"", StringComparison.Ordinal),
            json.Replace("\"revision\":\"2\"", "\"revision\":\"3\"", StringComparison.Ordinal),
            json.Replace("\"kind\":\"human\"", "\"kind\":\"agent\"", StringComparison.Ordinal),
            json.Replace("\"contentKind\":\"unknown\"", "\"contentKind\":\"other\"", StringComparison.Ordinal),
            json.Replace("{\"annotation\"", "{\"extra\":1,\"annotation\"", StringComparison.Ordinal),
            json.Replace("\"session\"", "\"session\":\"x\",\"session\"", StringComparison.Ordinal),
            json + " ",
            "[]",
            "{",
        })
        {
            Assert.Throws<InvalidDataException>(() => AnnotationPayload.Decode(Encoding.UTF8.GetBytes(broken)));
        }

        Assert.Throws<InvalidDataException>(() => AnnotationPayload.Decode(ReadOnlySpan<byte>.Empty));
        Assert.Throws<InvalidDataException>(() => AnnotationPayload.Decode(new byte[AnnotationPayload.MaxPayloadBytes + 1]));
    }

    [Fact]
    public void TheProducerOfAPayloadVersionFollowsItsNewestEntry()
    {
        var person = new UserId(Guid.NewGuid());
        var invocation = new InvocationId(Guid.NewGuid());
        AnnotationEntry Entry(AnnotationActor actor, AnnotationContentKind kind) =>
            new(new NativeRevision(1), AnnotationText.Create("x"), actor, kind, new Instant(0, 0), new CommandId(Guid.NewGuid()), new string('c', 64));

        Assert.Equal("human", AnnotationPayload.ProducerKind(Entry(AnnotationActor.Human(person), AnnotationContentKind.AiGenerated)));
        Assert.Equal("model", AnnotationPayload.ProducerKind(Entry(AnnotationActor.ProductHandler(person, invocation), AnnotationContentKind.AiGenerated)));
        Assert.Equal("model", AnnotationPayload.ProducerKind(Entry(AnnotationActor.ProductHandler(person, invocation), AnnotationContentKind.AiManipulated)));
        Assert.Equal("deterministic", AnnotationPayload.ProducerKind(Entry(AnnotationActor.ProductHandler(person, invocation), AnnotationContentKind.NonAi)));
        Assert.Equal("deterministic", AnnotationPayload.ProducerKind(Entry(AnnotationActor.ProductHandler(person, invocation), AnnotationContentKind.Unknown)));
    }

    private static AnnotationCommit CommitOf(CreateAnnotation command, SqliteStoreFixture fixture)
    {
        string fingerprint = command.Fingerprint();
        var entry = new AnnotationEntry(new NativeRevision(1), command.Text, command.Actor, command.ContentKind,
            Instant.FromDateTimeOffset(fixture.Time.GetUtcNow()), command.Command, fingerprint);
        return new AnnotationCommit(AnnotationOperations.Create, command.Command, fingerprint, new NativeRevision(0),
            new Annotation(command.Annotation, command.Session, command.Range, [entry]));
    }

    private static AnnotationCommit AppendOf(AppendAnnotation command, Annotation basis, SqliteStoreFixture fixture)
    {
        string fingerprint = command.Fingerprint();
        var entry = new AnnotationEntry(new NativeRevision(basis.Revision.Value + 1), command.Text, command.Actor, command.ContentKind,
            Instant.FromDateTimeOffset(fixture.Time.GetUtcNow()), command.Command, fingerprint);
        return new AnnotationCommit(AnnotationOperations.Append, command.Command, fingerprint, basis.Revision,
            new Annotation(basis.Id, basis.Session, basis.Range, basis.Entries.Append(entry)));
    }
}
