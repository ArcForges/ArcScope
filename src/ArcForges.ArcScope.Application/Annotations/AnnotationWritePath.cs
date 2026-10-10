// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The single annotation write path. Every annotation change, whether it comes from the user interface or from an own-app
/// capability invocation, is executed here: the command is validated against the current state and the session target,
/// serialized per annotation (TH-05), and committed through exactly one <see cref="IAnnotationStore.CommitAsync"/> call
/// together with its durable command receipt. Failures are registered codes from <see cref="AnnotationFailures"/>; the
/// write path never retries.
/// </summary>
public sealed class AnnotationWritePath
{
    private readonly IAnnotationStore store;
    private readonly ISessionTargetCatalog sessions;
    private readonly IClock clock;
    private readonly KeyedAsyncLock<AnnotationId> locks = new();

    public AnnotationWritePath(IAnnotationStore store, ISessionTargetCatalog sessions, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(clock);
        this.store = store;
        this.sessions = sessions;
        this.clock = clock;
    }

    /// <summary>
    /// Creates an annotation. The same command replays its recorded receipt; the same command identity with different
    /// content refuses with <c>command.reused_identifier</c>; another command naming an existing annotation refuses with
    /// <c>conflict.duplicate_identifier</c>; an unregistered session refuses with <c>resource.unavailable</c>; a range
    /// outside the recorded samples refuses with <c>validation.invalid_request</c>.
    /// </summary>
    public async ValueTask<Outcome<AnnotationWriteReceipt>> CreateAsync(CreateAnnotation command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string fingerprint = command.Fingerprint();
        if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled<AnnotationWriteReceipt>(EffectCertainty.DidNotHappen);
        KeyedAsyncLock<AnnotationId>.Releaser owner;
        try
        {
            owner = await locks.AcquireAsync(command.Annotation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled<AnnotationWriteReceipt>(EffectCertainty.DidNotHappen);
        }

        using (owner)
        {
            var read = await ReadCurrentAsync(command.Annotation, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var current)) return Forward(read);
            if (current.Annotation is { } existing)
            {
                return ReplayOrRefuse(existing, command.Command, fingerprint, AnnotationFailures.DuplicateIdentifier);
            }

            SessionTarget? session;
            try
            {
                session = await sessions.FindAsync(command.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Outcome.Cancelled<AnnotationWriteReceipt>(EffectCertainty.DidNotHappen);
            }
            catch (Exception)
            {
                return AnnotationFailures.UnexpectedFailure<AnnotationWriteReceipt>(EffectCertainty.DidNotHappen);
            }

            if (session is null || session.Id != command.Session) return AnnotationFailures.Refuse<AnnotationWriteReceipt>(AnnotationFailures.SessionUnavailable);
            if (!command.Range.IsWithin(session.SampleCount)) return AnnotationFailures.Refuse<AnnotationWriteReceipt>(AnnotationFailures.InvalidRequest);

            var entry = new AnnotationEntry(new NativeRevision(1), command.Text, command.Actor, command.ContentKind,
                clock.GetCurrentInstant(), command.Command, fingerprint);
            var next = new Annotation(command.Annotation, command.Session, command.Range, [entry]);
            return await CommitAsync(new AnnotationCommit(AnnotationOperations.Create, command.Command, fingerprint,
                new NativeRevision(0), next), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends one entry. The same command replays its recorded receipt even after later appends; a missing annotation
    /// refuses with <c>state.not_found</c>; a stale expected revision refuses with <c>conflict.revision_mismatch</c>; an
    /// entry that would exceed the history or text bound refuses with <c>validation.invalid_request</c>.
    /// </summary>
    public async ValueTask<Outcome<AnnotationWriteReceipt>> AppendAsync(AppendAnnotation command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string fingerprint = command.Fingerprint();
        if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled<AnnotationWriteReceipt>(EffectCertainty.DidNotHappen);
        KeyedAsyncLock<AnnotationId>.Releaser owner;
        try
        {
            owner = await locks.AcquireAsync(command.Annotation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled<AnnotationWriteReceipt>(EffectCertainty.DidNotHappen);
        }

        using (owner)
        {
            var read = await ReadCurrentAsync(command.Annotation, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var current)) return Forward(read);
            if (current.Annotation is not { } existing) return AnnotationFailures.Refuse<AnnotationWriteReceipt>(AnnotationFailures.NotFound);
            if (existing.FindEntry(command.Command) is not null)
            {
                return ReplayOrRefuse(existing, command.Command, fingerprint, AnnotationFailures.ReusedCommand);
            }

            if (existing.Revision != command.ExpectedRevision) return AnnotationFailures.Refuse<AnnotationWriteReceipt>(AnnotationFailures.RevisionMismatch);
            if (!existing.CanAppend(command.Text.Utf8ByteCount)) return AnnotationFailures.Refuse<AnnotationWriteReceipt>(AnnotationFailures.InvalidRequest);

            var entry = new AnnotationEntry(new NativeRevision(checked(existing.Revision.Value + 1)), command.Text, command.Actor,
                command.ContentKind, clock.GetCurrentInstant(), command.Command, fingerprint);
            return await CommitAsync(new AnnotationCommit(AnnotationOperations.Append, command.Command, fingerprint,
                existing.Revision, existing.Append(entry)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Outcome<AnnotationWriteReceipt> ReplayOrRefuse(Annotation existing, CommandId command, string fingerprint, string otherwise)
    {
        var recorded = existing.FindEntry(command);
        if (recorded is null) return AnnotationFailures.Refuse<AnnotationWriteReceipt>(otherwise);
        return string.Equals(recorded.CommandFingerprint, fingerprint, StringComparison.Ordinal)
            ? Outcome.Success(new AnnotationWriteReceipt(command, existing.Id, recorded.Revision, Replayed: true))
            : AnnotationFailures.Refuse<AnnotationWriteReceipt>(AnnotationFailures.ReusedCommand);
    }

    private async ValueTask<Outcome<CurrentState>> ReadCurrentAsync(AnnotationId annotation, CancellationToken cancellationToken)
    {
        try
        {
            var current = await store.ReadAsync(annotation, cancellationToken).ConfigureAwait(false);
            if (current is not null && current.Id != annotation) return AnnotationFailures.Refuse<CurrentState>(AnnotationFailures.IntegrityFailed);
            return Outcome.Success(new CurrentState(current));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled<CurrentState>(EffectCertainty.DidNotHappen);
        }
        catch (InvalidDataException)
        {
            return AnnotationFailures.Refuse<CurrentState>(AnnotationFailures.IntegrityFailed);
        }
        catch (Exception)
        {
            return AnnotationFailures.UnexpectedFailure<CurrentState>(EffectCertainty.DidNotHappen);
        }
    }

    private async ValueTask<Outcome<AnnotationWriteReceipt>> CommitAsync(AnnotationCommit commit, CancellationToken cancellationToken)
    {
        Outcome<AnnotationCommitReceipt> result;
        try
        {
            result = await store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The commit was dispatched: cancellation cannot prove that it did not happen. Reconcile with the same command.
            return Outcome.Cancelled<AnnotationWriteReceipt>(EffectCertainty.Unknown);
        }
        catch (Exception)
        {
            return AnnotationFailures.UnexpectedFailure<AnnotationWriteReceipt>(EffectCertainty.Unknown);
        }

        switch (result.Kind)
        {
            case OutcomeKind.Success when result.TryGetValue(out var receipt):
                bool matches = receipt is not null && receipt.Command == commit.Command && receipt.Annotation == commit.Next.Id
                    && (receipt.Replayed ? receipt.Revision.Value != 0 : receipt.Revision == commit.Next.Revision);
                return matches
                    ? Outcome.Success(new AnnotationWriteReceipt(receipt!.Command, receipt.Annotation, receipt.Revision, receipt.Replayed))
                    : AnnotationFailures.UnexpectedFailure<AnnotationWriteReceipt>(EffectCertainty.Unknown);
            case OutcomeKind.Failure when result.TryGetFailure(out var failure):
                return AnnotationFailures.CommitRefusals.Contains(failure.Code)
                    ? AnnotationFailures.Refuse<AnnotationWriteReceipt>(failure.Code)
                    : AnnotationFailures.UnexpectedFailure<AnnotationWriteReceipt>(EffectCertainty.Unknown);
            case OutcomeKind.Cancelled:
                return Outcome.Cancelled<AnnotationWriteReceipt>(result.CancellationEffect);
            default:
                return AnnotationFailures.UnexpectedFailure<AnnotationWriteReceipt>(EffectCertainty.Unknown);
        }
    }

    private static Outcome<AnnotationWriteReceipt> Forward(Outcome<CurrentState> read) => read.Map<AnnotationWriteReceipt>(
        _ => throw new InvalidOperationException("Only a non-success read is forwarded."));

    private sealed record CurrentState(Annotation? Annotation);
}
