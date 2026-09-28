// SPDX-License-Identifier: AGPL-3.0-only
using System.Numerics;

namespace ArcForges.ArcScope.Domain.Time;

/// <summary>
/// A caller-recorded anchor pair mapping one source clock onto a reference clock.
/// It applies a constant offset only; it does not estimate drift or interpolate samples.
/// </summary>
public sealed record SourceAlignment
{
    public Guid Id { get; }
    public string SourceId { get; }
    public string ReferenceSourceId { get; }
    public TimePoint SourceAnchor { get; }
    public TimePoint ReferenceAnchor { get; }
    public DateTimeOffset RecordedAtUtc { get; }

    public SourceAlignment(
        Guid id,
        string sourceId,
        string referenceSourceId,
        TimePoint sourceAnchor,
        TimePoint referenceAnchor,
        DateTimeOffset recordedAtUtc)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An alignment needs a stable non-empty identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A source identity is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(referenceSourceId))
            throw new ArgumentException("A reference source identity is required.", nameof(referenceSourceId));
        if (string.Equals(sourceId, referenceSourceId, StringComparison.Ordinal))
            throw new ArgumentException("Source and reference identities must be distinct.", nameof(referenceSourceId));
        if (recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The alignment record timestamp must be UTC.", nameof(recordedAtUtc));

        Id = id;
        SourceId = sourceId;
        ReferenceSourceId = referenceSourceId;
        SourceAnchor = sourceAnchor ?? throw new ArgumentNullException(nameof(sourceAnchor));
        ReferenceAnchor = referenceAnchor ?? throw new ArgumentNullException(nameof(referenceAnchor));
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Maps a source time using the recorded anchors and exact elapsed-time conversion.</summary>
    public TimePoint MapToReference(TimePoint sourceTime)
    {
        ArgumentNullException.ThrowIfNull(sourceTime);
        if (sourceTime.Domain != SourceAnchor.Domain)
            throw new InvalidOperationException("The source time is outside the recorded alignment domain.");

        var sourceDelta = new BigInteger(sourceTime.Ticks) - SourceAnchor.Ticks;
        var referenceDelta = ExactRate.ConvertDeltaExactly(
            sourceDelta,
            SourceAnchor.Domain.TicksPerSecond,
            ReferenceAnchor.Domain.TicksPerSecond);
        return new TimePoint(ReferenceAnchor.Domain, checked(ReferenceAnchor.Ticks + referenceDelta));
    }
}
