// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using Xunit;
using Instant = ArcForges.Foundation.Instant;

namespace ArcForges.ArcScope.Tests.Annotations;

public sealed class AnnotationQueryAndSurfaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadingReturnsTheFullHistoryOrRefusesAMissingAnnotation()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var create = fixture.Create(text: "first");
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(create, Token));
        OutcomeAssert.Success(await fixture.WritePath.AppendAsync(fixture.Append(create.Annotation, 1, "second"), Token));

        var annotation = OutcomeAssert.Success(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token));
        Assert.Equal(new[] { "first", "second" }, annotation.Entries.Select(entry => entry.Text.Value));
        OutcomeAssert.Failure(await fixture.Queries.ReadAsync(new ReadAnnotation(new AnnotationId(Guid.NewGuid())), Token), AnnotationFailures.NotFound);

        fixture.Store.ReadFault = new InvalidDataException("corrupt");
        OutcomeAssert.Failure(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token), AnnotationFailures.IntegrityFailed);
        fixture.Store.ReadFault = new IOException("locked");
        OutcomeAssert.Failure(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), Token), AnnotationFailures.Unexpected,
            EffectCertainty.DidNotHappen);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        OutcomeAssert.Cancelled(await fixture.Queries.ReadAsync(new ReadAnnotation(create.Annotation), cancelled.Token), EffectCertainty.DidNotHappen);
        OutcomeAssert.Cancelled(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 5), cancelled.Token),
            EffectCertainty.DidNotHappen);
    }

    [Fact]
    public async Task SessionPagesFollowTheDeterministicOrderWhateverTheInsertionOrder()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var shapes = new (ulong From, ulong Count)[] { (500, 10), (5, 100), (5, 1), (70, 3), (5, 1), (9_000, 1_000), (70, 3) };
        var created = new List<CreateAnnotation>();
        foreach (var (from, count) in Enumerable.Reverse(shapes))
        {
            var command = fixture.Create(from: from, count: count);
            OutcomeAssert.Success(await fixture.WritePath.CreateAsync(command, Token));
            created.Add(command);
        }

        var otherSession = new SessionTargetId(Guid.NewGuid());
        OutcomeAssert.Success(await fixture.Sessions.RegisterAsync(new SessionTarget(otherSession, 100), Token));
        OutcomeAssert.Success(await fixture.WritePath.CreateAsync(new CreateAnnotation(new CommandId(Guid.NewGuid()), new AnnotationId(Guid.NewGuid()),
            otherSession, new SampleRange(0, 1), AnnotationText.Create("elsewhere"), AnnotationActor.Human(fixture.Person), AnnotationContentKind.NonAi), Token));

        var expected = created.Select(command => new AnnotationPageCursor(command.Range.From, command.Range.Count, command.Annotation.Value))
            .Order(Comparer<AnnotationPageCursor>.Create(AnnotationPageCursor.Compare)).ToArray();
        var seen = new List<AnnotationPageCursor>();
        AnnotationPageCursor? cursor = null;
        int pages = 0;
        do
        {
            var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 3, cursor), Token));
            Assert.InRange(page.Items.Count, 1, 3);
            seen.AddRange(page.Items.Select(AnnotationPageCursor.After));
            cursor = page.Next;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
        var all = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 7), Token));
        Assert.Equal(7, all.Items.Count);
        Assert.Null(all.Next);
    }

    [Fact]
    public async Task ASessionReadRefusesAnUnregisteredSessionAndAnUntrustworthyPage()
    {
        var fixture = new AnnotationFixture();
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 5), Token), AnnotationFailures.SessionUnavailable);
        Assert.Equal(0, fixture.Store.SessionReadCalls);

        await fixture.WithSessionAsync();
        var first = Make(fixture.Session, 1);
        var second = Make(fixture.Session, 2);
        var foreign = Make(new SessionTargetId(Guid.NewGuid()), 3);

        fixture.Store.SessionResult = _ => [second, first];
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 5), Token), AnnotationFailures.IntegrityFailed);
        fixture.Store.SessionResult = _ => [first, first];
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 5), Token), AnnotationFailures.IntegrityFailed);
        fixture.Store.SessionResult = _ => [first, foreign];
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 5), Token), AnnotationFailures.IntegrityFailed);
        fixture.Store.SessionResult = _ => [first, second, Make(fixture.Session, 4)];
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 1), Token), AnnotationFailures.IntegrityFailed);
        fixture.Store.SessionResult = _ => [first];
        OutcomeAssert.Failure(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 5, AnnotationPageCursor.After(second)), Token),
            AnnotationFailures.IntegrityFailed);
        fixture.Store.SessionResult = _ => [first, second];
        var page = OutcomeAssert.Success(await fixture.Queries.ReadSessionAsync(new ReadSessionAnnotations(fixture.Session, 1), Token));
        Assert.Same(first, Assert.Single(page.Items));
        Assert.Equal(AnnotationPageCursor.After(first), page.Next);
    }

    [Fact]
    public async Task TheUserInterfaceSurfaceRefusesInvalidInputBeforeAnyStoreAccess()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var surface = fixture.Surface;
        var session = fixture.Session.Value;
        var refusals = new[]
        {
            await surface.CreateAsync(Guid.Empty, Guid.NewGuid(), session, 0, 1, "note", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.Empty, session, 0, 1, "note", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 0, 1, "note", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), session, 0, 0, "note", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), session, ulong.MaxValue, 1, "note", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), session, 0, 1, "  ", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), session, 0, 1, null, Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), session, 0, 1, "nul\0", Token),
            await surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), session, 0, 1, "\udfff", Token),
            await surface.AppendAsync(Guid.NewGuid(), Guid.NewGuid(), 0, "note", Token),
            await surface.AppendAsync(Guid.Empty, Guid.NewGuid(), 1, "note", Token),
            await surface.AppendAsync(Guid.NewGuid(), Guid.Empty, 1, "note", Token),
            await surface.AppendAsync(Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', AnnotationText.MaxUtf8Bytes + 1), Token),
        };
        Assert.All(refusals, refusal => OutcomeAssert.Failure(refusal, AnnotationFailures.InvalidRequest));
        OutcomeAssert.Failure(await surface.ReadAsync(Guid.Empty, Token), AnnotationFailures.InvalidRequest);
        OutcomeAssert.Failure(await surface.ReadSessionAsync(Guid.Empty, 5, null, Token), AnnotationFailures.InvalidRequest);
        OutcomeAssert.Failure(await surface.ReadSessionAsync(session, 0, null, Token), AnnotationFailures.InvalidRequest);
        OutcomeAssert.Failure(await surface.ReadSessionAsync(session, ReadSessionAnnotations.MaxPageSize + 1, null, Token), AnnotationFailures.InvalidRequest);
        OutcomeAssert.Failure(await surface.ReadSessionAsync(session, 5, new AnnotationPageCursor(1, 0, Guid.NewGuid()), Token), AnnotationFailures.InvalidRequest);
        Assert.Equal(0, fixture.Store.ReadCalls);
        Assert.Equal(0, fixture.Store.SessionReadCalls);
        Assert.Empty(fixture.Store.Commits);
    }

    [Fact]
    public async Task TheUserInterfaceSurfaceRunsTheSameWritePathAsATypedCommand()
    {
        var fixture = await new AnnotationFixture().WithSessionAsync();
        var commandId = Guid.NewGuid();
        var annotationId = Guid.NewGuid();

        var created = OutcomeAssert.Success(await fixture.Surface.CreateAsync(commandId, annotationId, fixture.Session.Value, 40, 2, "spike", Token));
        Assert.Equal(new NativeRevision(1), created.Revision);
        var commit = Assert.Single(fixture.Store.Commits);
        var typed = new CreateAnnotation(new CommandId(commandId), new AnnotationId(annotationId), fixture.Session, new SampleRange(40, 2),
            AnnotationText.Create("spike"), AnnotationActor.Human(fixture.Person), AnnotationContentKind.NonAi);
        Assert.Equal(typed.Fingerprint(), commit.Fingerprint);
        Assert.Equal(AnnotationActor.Human(fixture.Person), commit.Entry.Actor);
        Assert.Equal(AnnotationContentKind.NonAi, commit.Entry.ContentKind);

        // The typed command through the write path is recognised as the same command: a replay, not a second effect.
        Assert.True(OutcomeAssert.Success(await fixture.WritePath.CreateAsync(typed, Token)).Replayed);
        Assert.True(OutcomeAssert.Success(await fixture.Surface.CreateAsync(commandId, annotationId, fixture.Session.Value, 40, 2, "spike", Token)).Replayed);
        Assert.Single(fixture.Store.Commits);

        var appended = OutcomeAssert.Success(await fixture.Surface.AppendAsync(Guid.NewGuid(), annotationId, 1, "and its echo", Token));
        Assert.Equal(new NativeRevision(2), appended.Revision);
        OutcomeAssert.Failure(await fixture.Surface.AppendAsync(Guid.NewGuid(), annotationId, 1, "stale view", Token), AnnotationFailures.RevisionMismatch);
        var read = OutcomeAssert.Success(await fixture.Surface.ReadAsync(annotationId, Token));
        Assert.Equal(new[] { "spike", "and its echo" }, read.Entries.Select(entry => entry.Text.Value));
        var page = OutcomeAssert.Success(await fixture.Surface.ReadSessionAsync(fixture.Session.Value, 10, null, Token));
        Assert.Equal(annotationId, Assert.Single(page.Items).Id.Value);
        OutcomeAssert.Failure(await fixture.Surface.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, 1, "no session", Token),
            AnnotationFailures.SessionUnavailable);
    }

    private static Annotation Make(SessionTargetId session, ulong from) =>
        new(new AnnotationId(Guid.NewGuid()), session, new SampleRange(from, 1),
            [new AnnotationEntry(new NativeRevision(1), AnnotationText.Create("n"), AnnotationActor.Human(new UserId(Guid.NewGuid())),
                AnnotationContentKind.NonAi, new Instant(0, 0), new CommandId(Guid.NewGuid()), new string('c', 64))]);
}
