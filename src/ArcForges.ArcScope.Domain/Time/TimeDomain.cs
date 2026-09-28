// SPDX-License-Identifier: AGPL-3.0-only
namespace ArcForges.ArcScope.Domain.Time;

/// <summary>Identifies one source clock and its exact number of ticks per second.</summary>
public sealed record TimeDomain
{
    public Guid Id { get; }
    public ExactRate TicksPerSecond { get; }

    public TimeDomain(Guid id, ExactRate ticksPerSecond)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A time domain needs a stable non-empty identity.", nameof(id));
        Id = id;
        TicksPerSecond = ticksPerSecond ?? throw new ArgumentNullException(nameof(ticksPerSecond));
    }
}
