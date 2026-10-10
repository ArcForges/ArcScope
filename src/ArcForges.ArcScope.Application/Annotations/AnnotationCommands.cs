// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.Values;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>The stable operation keys of the annotation write commands.</summary>
public static class AnnotationOperations
{
    /// <summary>The CreateAnnotation operation (capability identity <c>arcscope.annotation.create</c>).</summary>
    public const string Create = "arcscope.annotation.create";

    /// <summary>The append command. It is an Application command only; it has no Contracts operation.</summary>
    public const string Append = "arcscope.annotation.append";
}

/// <summary>
/// Creates an annotation with a client-allocated identity. Retrying the same command (same command identity and content)
/// replays the recorded result; reusing the command identity for different content refuses.
/// </summary>
public sealed record CreateAnnotation
{
    public CreateAnnotation(CommandId command, AnnotationId annotation, SessionTargetId session, SampleRange range,
        AnnotationText text, AnnotationActor actor, AnnotationContentKind contentKind)
    {
        _ = command.ToWire();
        _ = annotation.ToWire();
        _ = session.ToWire();
        if (!range.IsInitialized) throw new ArgumentException("An annotation covers an initialized sample range.", nameof(range));
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(actor);
        if (!AnnotationContentKinds.IsDefined(contentKind)) throw new ArgumentOutOfRangeException(nameof(contentKind));
        Command = command;
        Annotation = annotation;
        Session = session;
        Range = range;
        Text = text;
        Actor = actor;
        ContentKind = contentKind;
    }

    public CommandId Command { get; }
    public AnnotationId Annotation { get; }
    public SessionTargetId Session { get; }
    public SampleRange Range { get; }
    public AnnotationText Text { get; }
    public AnnotationActor Actor { get; }
    public AnnotationContentKind ContentKind { get; }

    /// <summary>The canonical semantic fingerprint used for command-identity replay and reuse detection.</summary>
    public string Fingerprint() => AnnotationCommandFingerprint.ForCreate(this);
}

/// <summary>
/// Appends one entry to an existing annotation, guarded by the revision the author last saw. A stale revision refuses
/// with <c>conflict.revision_mismatch</c>; earlier entries are never rewritten.
/// </summary>
public sealed record AppendAnnotation
{
    public AppendAnnotation(CommandId command, AnnotationId annotation, NativeRevision expectedRevision, AnnotationText text,
        AnnotationActor actor, AnnotationContentKind contentKind)
    {
        _ = command.ToWire();
        _ = annotation.ToWire();
        if (expectedRevision.Value == 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision), "An append expects an existing revision.");
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(actor);
        if (!AnnotationContentKinds.IsDefined(contentKind)) throw new ArgumentOutOfRangeException(nameof(contentKind));
        Command = command;
        Annotation = annotation;
        ExpectedRevision = expectedRevision;
        Text = text;
        Actor = actor;
        ContentKind = contentKind;
    }

    public CommandId Command { get; }
    public AnnotationId Annotation { get; }
    public NativeRevision ExpectedRevision { get; }
    public AnnotationText Text { get; }
    public AnnotationActor Actor { get; }
    public AnnotationContentKind ContentKind { get; }

    /// <summary>The canonical semantic fingerprint used for command-identity replay and reuse detection.</summary>
    public string Fingerprint() => AnnotationCommandFingerprint.ForAppend(this);
}

/// <summary>Reads one annotation with its full history.</summary>
public sealed record ReadAnnotation
{
    public ReadAnnotation(AnnotationId annotation)
    {
        _ = annotation.ToWire();
        Annotation = annotation;
    }

    public AnnotationId Annotation { get; }
}

/// <summary>The position after which a session page continues, in the deterministic session order.</summary>
public readonly record struct AnnotationPageCursor(ulong From, ulong Count, Guid Annotation)
{
    public static AnnotationPageCursor After(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return new AnnotationPageCursor(annotation.Range.From, annotation.Range.Count, annotation.Id.Value);
    }

    /// <summary>
    /// The session order: range start, then range length, then the annotation identity's canonical lowercase UUID text
    /// compared ordinally (which is canonical UUID byte order). It never depends on storage or enumeration order.
    /// </summary>
    public static int Compare(AnnotationPageCursor left, AnnotationPageCursor right)
    {
        int order = left.From.CompareTo(right.From);
        if (order == 0) order = left.Count.CompareTo(right.Count);
        return order != 0 ? order : string.CompareOrdinal(left.Annotation.ToString("D"), right.Annotation.ToString("D"));
    }
}

/// <summary>
/// Reads one page of a session's annotations in the order of <see cref="AnnotationPageCursor.Compare"/>, continuing after
/// <see cref="After"/> when it is present.
/// </summary>
public sealed record ReadSessionAnnotations
{
    public const int MaxPageSize = 200;

    public ReadSessionAnnotations(SessionTargetId session, int limit, AnnotationPageCursor? after = null)
    {
        _ = session.ToWire();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        if (after is { } cursor && (cursor.Count == 0 || cursor.Annotation == Guid.Empty))
            throw new ArgumentException("A page cursor names an annotation position.", nameof(after));
        Session = session;
        Limit = limit;
        After = after;
    }

    public SessionTargetId Session { get; }
    public int Limit { get; }
    public AnnotationPageCursor? After { get; }
}

/// <summary>One page of a session's annotations; <see cref="Next"/> is present only when more annotations follow.</summary>
public sealed record AnnotationPage(IReadOnlyList<Annotation> Items, AnnotationPageCursor? Next);

/// <summary>The result of a committed or replayed annotation write.</summary>
public sealed record AnnotationWriteReceipt(CommandId Command, AnnotationId Annotation, NativeRevision Revision, bool Replayed);
