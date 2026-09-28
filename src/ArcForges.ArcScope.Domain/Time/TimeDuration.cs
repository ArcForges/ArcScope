// SPDX-License-Identifier: AGPL-3.0-only
namespace ArcForges.ArcScope.Domain.Time;

/// <summary>A signed exact duration represented as integer ticks at an exact rate.</summary>
public sealed record TimeDuration
{
    public long Ticks { get; }
    public ExactRate Rate { get; }

    public TimeDuration(long ticks, ExactRate rate)
    {
        Ticks = ticks;
        Rate = rate ?? throw new ArgumentNullException(nameof(rate));
    }

    /// <summary>Converts units without rounding; a fractional target tick is rejected.</summary>
    public TimeDuration ConvertTo(ExactRate targetRate) =>
        new(ExactRate.ConvertTicksExactly(Ticks, Rate, targetRate), targetRate);
}
