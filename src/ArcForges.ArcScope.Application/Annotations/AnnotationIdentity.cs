// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The identity of one annotation (resource kind <c>arcscope.annotation</c>). The client allocates it, so a create can be
/// replayed with the same identity; it is never interchangeable with a session or command identity.
/// </summary>
public readonly record struct AnnotationId
{
    public AnnotationId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("An annotation identity cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static bool TryCreate(Guid value, out AnnotationId id)
    {
        id = value == Guid.Empty ? default : new AnnotationId(value);
        return value != Guid.Empty;
    }

    public static AnnotationId FromWire(Id value) => new(UuidBoundary.FromWire(value));

    public Id ToWire() => UuidBoundary.ToWire(Value);
}

/// <summary>
/// The identity of the capture session an annotation targets (the <c>sessionId</c> of the Contracts annotation shapes).
/// Sessions are owned by the session owner, which registers them in the <see cref="ISessionTargetCatalog"/>.
/// </summary>
public readonly record struct SessionTargetId
{
    public SessionTargetId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A session identity cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static bool TryCreate(Guid value, out SessionTargetId id)
    {
        id = value == Guid.Empty ? default : new SessionTargetId(value);
        return value != Guid.Empty;
    }

    public static SessionTargetId FromWire(Id value) => new(UuidBoundary.FromWire(value));

    public Id ToWire() => UuidBoundary.ToWire(Value);
}

/// <summary>
/// A half-open, checked range of recorded sample indices <c>[From, From + Count)</c> (the Contracts <c>SampleRange</c>).
/// An annotation covers at least one sample, and the end never overflows.
/// </summary>
public readonly record struct SampleRange
{
    public SampleRange(ulong from, ulong count)
    {
        if (!IsValid(from, count))
            throw new ArgumentOutOfRangeException(nameof(count), "A sample range covers at least one sample and its end cannot overflow.");
        From = from;
        Count = count;
    }

    public ulong From { get; }
    public ulong Count { get; }

    /// <summary>The exclusive end index.</summary>
    public ulong End => From + Count;

    public static bool TryCreate(ulong from, ulong count, out SampleRange range)
    {
        bool valid = IsValid(from, count);
        range = valid ? new SampleRange(from, count) : default;
        return valid;
    }

    /// <summary>False for the uninitialized default value, which covers no sample.</summary>
    public bool IsInitialized => Count != 0;

    /// <summary>True when the range lies entirely inside a session holding <paramref name="sampleCount"/> recorded samples.</summary>
    public bool IsWithin(ulong sampleCount) => End <= sampleCount;

    private static bool IsValid(ulong from, ulong count) => count != 0 && count <= ulong.MaxValue - from;
}
