// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The annotation persistence port. Reads return complete, validated annotations and throw
/// <see cref="InvalidDataException"/> when stored state cannot be trusted. <see cref="CommitAsync"/> is the only
/// mutation, and only <see cref="AnnotationWritePath"/> can construct the <see cref="AnnotationCommit"/> it takes, so every
/// annotation change, from the user interface or an own-app capability invocation, passes through that one write path.
/// </summary>
public interface IAnnotationStore
{
    /// <summary>Reads one annotation with its full history, or null when it does not exist.</summary>
    ValueTask<Annotation?> ReadAsync(AnnotationId annotation, CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <paramref name="limit"/> annotations of one session, strictly after <paramref name="after"/> when present,
    /// in the order of <see cref="AnnotationPageCursor.Compare"/>.
    /// </summary>
    ValueTask<IReadOnlyList<Annotation>> ReadSessionAsync(SessionTargetId session, AnnotationPageCursor? after, int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically commits <see cref="AnnotationCommit.Next"/> and the durable receipt of its command, provided the stored
    /// revision still equals <see cref="AnnotationCommit.ExpectedRevision"/> (zero: the annotation must not exist). The
    /// store refuses with registered codes only: <c>conflict.revision_mismatch</c> for a stale revision,
    /// <c>conflict.duplicate_identifier</c> when a create finds the annotation already present,
    /// <c>command.reused_identifier</c> when the command identity already committed different content, and
    /// <c>resource.integrity_failed</c> when stored state cannot be trusted. A command identity that already committed the
    /// same content returns its recorded receipt with <see cref="AnnotationCommitReceipt.Replayed"/> set and no new effect.
    /// </summary>
    ValueTask<Outcome<AnnotationCommitReceipt>> CommitAsync(AnnotationCommit commit, CancellationToken cancellationToken);
}

/// <summary>One validated, serialized annotation change, ready for a single atomic commit.</summary>
public sealed class AnnotationCommit
{
    internal AnnotationCommit(string operation, CommandId command, string fingerprint, NativeRevision expectedRevision, Annotation next)
    {
        Operation = operation;
        Command = command;
        Fingerprint = fingerprint;
        ExpectedRevision = expectedRevision;
        Next = next;
    }

    /// <summary>The operation key: <see cref="AnnotationOperations.Create"/> or <see cref="AnnotationOperations.Append"/>.</summary>
    public string Operation { get; }

    public CommandId Command { get; }

    /// <summary>The canonical semantic fingerprint of the command (also recorded on the new entry).</summary>
    public string Fingerprint { get; }

    /// <summary>The revision the store must still hold; zero means the annotation must not exist yet.</summary>
    public NativeRevision ExpectedRevision { get; }

    /// <summary>The complete next state; its last entry is the one this command appends.</summary>
    public Annotation Next { get; }

    /// <summary>The entry this command appends.</summary>
    public AnnotationEntry Entry => Next.Entries[^1];
}

/// <summary>The store's durable record of a committed (or replayed) annotation command.</summary>
public sealed record AnnotationCommitReceipt(CommandId Command, AnnotationId Annotation, NativeRevision Revision, bool Replayed);
