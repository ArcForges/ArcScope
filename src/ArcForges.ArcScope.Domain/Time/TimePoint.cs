// SPDX-License-Identifier: AGPL-3.0-only
namespace ArcForges.ArcScope.Domain.Time;

/// <summary>An exact position in one identified clock domain.</summary>
public sealed record TimePoint
{
    public TimeDomain Domain { get; }
    public long Ticks { get; }

    public TimePoint(TimeDomain domain, long ticks)
    {
        Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        Ticks = ticks;
    }

    public TimePoint Add(TimeDuration duration)
    {
        ArgumentNullException.ThrowIfNull(duration);
        var converted = duration.ConvertTo(Domain.TicksPerSecond);
        return new TimePoint(Domain, checked(Ticks + converted.Ticks));
    }

    /// <summary>Returns a signed duration; points from different clocks need an alignment first.</summary>
    public TimeDuration DifferenceFrom(TimePoint earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        EnsureSameDomain(earlier);
        return new TimeDuration(checked(Ticks - earlier.Ticks), Domain.TicksPerSecond);
    }

    public int CompareTo(TimePoint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameDomain(other);
        return Ticks.CompareTo(other.Ticks);
    }

    internal void EnsureSameDomain(TimePoint other)
    {
        if (Domain != other.Domain)
            throw new InvalidOperationException("Time points from different domains require an explicit recorded alignment.");
    }
}
