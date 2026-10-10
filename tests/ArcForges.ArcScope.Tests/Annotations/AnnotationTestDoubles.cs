// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using Xunit;

namespace ArcForges.ArcScope.Tests.Annotations;

/// <summary>
/// An in-memory implementation of the annotation store port with the port's full commit contract (durable command log,
/// fingerprint replay and reuse refusal, atomic expected-revision check). It is the stand-in for the SQLite store at the
/// persistence port only; it counts every call and can inject faults, and it records the highest number of commits that
/// were ever in flight for one annotation at the same time.
/// </summary>
internal sealed class InMemoryAnnotationStore : IAnnotationStore
{
    private readonly Lock sync = new();
    private readonly Dictionary<AnnotationId, Annotation> rows = [];
    private readonly Dictionary<CommandId, (string Fingerprint, AnnotationCommitReceipt Receipt)> log = [];
    private readonly Dictionary<AnnotationId, int> inFlight = [];
    private readonly List<AnnotationCommit> commits = [];
    private int readCalls;
    private int sessionReadCalls;

    /// <summary>Runs inside each commit before it applies; it may block, throw or observe cancellation.</summary>
    public Func<AnnotationCommit, CancellationToken, Task>? BeforeCommit { get; set; }

    /// <summary>Replaces the commit result entirely (an injected store response).</summary>
    public Func<AnnotationCommit, Outcome<AnnotationCommitReceipt>>? CommitResult { get; set; }

    /// <summary>Thrown by every read.</summary>
    public Exception? ReadFault { get; set; }

    /// <summary>Replaces the session read result (an injected store response).</summary>
    public Func<SessionTargetId, IReadOnlyList<Annotation>>? SessionResult { get; set; }

    public int ReadCalls => Volatile.Read(ref readCalls);
    public int SessionReadCalls => Volatile.Read(ref sessionReadCalls);
    public int MaxConcurrentCommitsPerAnnotation { get; private set; }

    public IReadOnlyList<AnnotationCommit> Commits
    {
        get
        {
            lock (sync) return commits.ToArray();
        }
    }

    public int CommitCalls => Commits.Count;

    public void Seed(Annotation annotation)
    {
        lock (sync)
        {
            rows[annotation.Id] = annotation;
            foreach (var entry in annotation.Entries)
                log[entry.Command] = (entry.CommandFingerprint, new AnnotationCommitReceipt(entry.Command, annotation.Id, entry.Revision, false));
        }
    }

    public Annotation? Get(AnnotationId id)
    {
        lock (sync) return rows.GetValueOrDefault(id);
    }

    public ValueTask<Annotation?> ReadAsync(AnnotationId annotation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref readCalls);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFault is { } fault) throw fault;
        lock (sync) return ValueTask.FromResult(rows.GetValueOrDefault(annotation));
    }

    public ValueTask<IReadOnlyList<Annotation>> ReadSessionAsync(SessionTargetId session, AnnotationPageCursor? after, int limit,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref sessionReadCalls);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFault is { } fault) throw fault;
        if (SessionResult is { } injected) return ValueTask.FromResult(injected(session));
        lock (sync)
        {
            IReadOnlyList<Annotation> page = rows.Values.Where(item => item.Session == session)
                .Where(item => after is not { } cursor || AnnotationPageCursor.Compare(cursor, AnnotationPageCursor.After(item)) < 0)
                .Order(Comparer<Annotation>.Create((left, right) => AnnotationPageCursor.Compare(AnnotationPageCursor.After(left), AnnotationPageCursor.After(right))))
                .Take(limit).ToArray();
            return ValueTask.FromResult(page);
        }
    }

    public async ValueTask<Outcome<AnnotationCommitReceipt>> CommitAsync(AnnotationCommit commit, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            commits.Add(commit);
            int now = inFlight.GetValueOrDefault(commit.Next.Id) + 1;
            inFlight[commit.Next.Id] = now;
            MaxConcurrentCommitsPerAnnotation = Math.Max(MaxConcurrentCommitsPerAnnotation, now);
        }

        try
        {
            if (BeforeCommit is { } before) await before(commit, cancellationToken);
            if (CommitResult is { } injected) return injected(commit);
            lock (sync)
            {
                if (log.TryGetValue(commit.Command, out var recorded))
                {
                    return recorded.Fingerprint == commit.Fingerprint
                        ? Outcome.Success(recorded.Receipt with { Replayed = true })
                        : Outcome.Failure<AnnotationCommitReceipt>(TypedFailure.Create(AnnotationFailures.ReusedCommand));
                }

                var current = rows.GetValueOrDefault(commit.Next.Id);
                if (commit.ExpectedRevision.Value == 0 && current is not null)
                    return Outcome.Failure<AnnotationCommitReceipt>(TypedFailure.Create(AnnotationFailures.DuplicateIdentifier));
                if ((current?.Revision.Value ?? 0) != commit.ExpectedRevision.Value)
                    return Outcome.Failure<AnnotationCommitReceipt>(TypedFailure.Create(AnnotationFailures.RevisionMismatch));
                rows[commit.Next.Id] = commit.Next;
                var receipt = new AnnotationCommitReceipt(commit.Command, commit.Next.Id, commit.Next.Revision, false);
                log[commit.Command] = (commit.Fingerprint, receipt);
                return Outcome.Success(receipt);
            }
        }
        finally
        {
            lock (sync) inFlight[commit.Next.Id]--;
        }
    }
}

/// <summary>An in-memory session catalog with the port's registration rule (sessions only grow).</summary>
internal sealed class InMemorySessionTargetCatalog : ISessionTargetCatalog
{
    private readonly Lock sync = new();
    private readonly Dictionary<SessionTargetId, SessionTarget> sessions = [];

    public Exception? FindFault { get; set; }

    public ValueTask<SessionTarget?> FindAsync(SessionTargetId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FindFault is { } fault) throw fault;
        lock (sync) return ValueTask.FromResult(sessions.GetValueOrDefault(id));
    }

    public ValueTask<Outcome<SessionTarget>> RegisterAsync(SessionTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (sessions.TryGetValue(target.Id, out var existing) && existing.SampleCount > target.SampleCount)
                return ValueTask.FromResult(Outcome.Failure<SessionTarget>(TypedFailure.Create(AnnotationFailures.InvalidTransition)));
            sessions[target.Id] = target;
            return ValueTask.FromResult(Outcome.Success(target));
        }
    }
}

/// <summary>A deterministic wall clock for committed-at instants.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>Builds a fully wired write path over the in-memory ports.</summary>
internal sealed class AnnotationFixture
{
    public static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public AnnotationFixture()
    {
        Time = new ManualTimeProvider(Start);
        WritePath = new AnnotationWritePath(Store, Sessions, new Clock(Time));
        Queries = new AnnotationQueries(Store, Sessions);
        Surface = new AnnotationCommandSurface(WritePath, Queries, Person);
    }

    public InMemoryAnnotationStore Store { get; } = new();
    public InMemorySessionTargetCatalog Sessions { get; } = new();
    public ManualTimeProvider Time { get; }
    public AnnotationWritePath WritePath { get; }
    public AnnotationQueries Queries { get; }
    public AnnotationCommandSurface Surface { get; }
    public UserId Person { get; } = new(Guid.NewGuid());
    public SessionTargetId Session { get; } = new(Guid.NewGuid());

    public async Task<AnnotationFixture> WithSessionAsync(ulong samples = 10_000)
    {
        var registered = await Sessions.RegisterAsync(new SessionTarget(Session, samples), CancellationToken.None);
        Assert.Equal(OutcomeKind.Success, registered.Kind);
        return this;
    }

    public CreateAnnotation Create(AnnotationId? id = null, string text = "rising edge glitch", ulong from = 100, ulong count = 50,
        CommandId? command = null, AnnotationActor? actor = null, AnnotationContentKind kind = AnnotationContentKind.NonAi) =>
        new(command ?? new CommandId(Guid.NewGuid()), id ?? new AnnotationId(Guid.NewGuid()), Session, new SampleRange(from, count),
            AnnotationText.Create(text), actor ?? AnnotationActor.Human(Person), kind);

    public AppendAnnotation Append(AnnotationId id, ulong expected, string text = "confirmed on the second capture",
        CommandId? command = null, AnnotationActor? actor = null, AnnotationContentKind kind = AnnotationContentKind.NonAi) =>
        new(command ?? new CommandId(Guid.NewGuid()), id, new NativeRevision(expected), AnnotationText.Create(text),
            actor ?? AnnotationActor.Human(Person), kind);
}

internal static class OutcomeAssert
{
    public static T Success<T>(Outcome<T> outcome)
    {
        if (outcome.TryGetFailure(out var failure)) Assert.Fail("Expected success, got " + failure.Code);
        Assert.Equal(OutcomeKind.Success, outcome.Kind);
        Assert.True(outcome.TryGetValue(out var value));
        return value!;
    }

    /// <summary>Asserts a registered, closed-set refusal with its registered effect certainty.</summary>
    public static TypedFailure Failure<T>(Outcome<T> outcome, string code, EffectCertainty effect = EffectCertainty.DidNotHappen)
    {
        Assert.Equal(OutcomeKind.Failure, outcome.Kind);
        Assert.True(outcome.TryGetFailure(out var failure));
        Assert.Equal(code, failure.Code);
        Assert.Contains(failure.Code, AnnotationFailures.All);
        Assert.True(failure.IsKnownCode);
        Assert.Equal(effect, failure.Effect);
        Assert.Equal(RetryMode.Never, failure.Retry);
        return failure;
    }

    public static void Cancelled<T>(Outcome<T> outcome, EffectCertainty effect)
    {
        Assert.Equal(OutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(effect, outcome.CancellationEffect);
    }
}
