// SPDX-License-Identifier: AGPL-3.0-only
using System.Numerics;

namespace ArcForges.ArcScope.Domain.Time;

/// <summary>A positive, reduced count-per-second ratio using Foundation Rational bounds.</summary>
public sealed record ExactRate
{
    public long Numerator { get; }
    public ulong Denominator { get; }

    public ExactRate(long numerator, ulong denominator)
    {
        if (numerator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "A rate must be positive.");
        if (denominator == 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "A rate denominator must be positive.");

        var divisor = GreatestCommonDivisor((ulong)numerator, denominator);
        Numerator = checked((long)((ulong)numerator / divisor));
        Denominator = denominator / divisor;
    }

    internal static long ConvertTicksExactly(long ticks, ExactRate from, ExactRate to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var numerator = new BigInteger(ticks) * from.Denominator * to.Numerator;
        var denominator = new BigInteger(from.Numerator) * to.Denominator;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (!remainder.IsZero)
            throw new InvalidOperationException("The time conversion is not exact at the requested rate.");
        if (quotient < long.MinValue || quotient > long.MaxValue)
            throw new OverflowException("The converted tick count is outside the supported range.");
        return (long)quotient;
    }

    internal static long ConvertDeltaExactly(BigInteger ticks, ExactRate from, ExactRate to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var numerator = ticks * from.Denominator * to.Numerator;
        var denominator = new BigInteger(from.Numerator) * to.Denominator;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (!remainder.IsZero)
            throw new InvalidOperationException("The time conversion is not exact at the requested rate.");
        if (quotient < long.MinValue || quotient > long.MaxValue)
            throw new OverflowException("The converted tick count is outside the supported range.");
        return (long)quotient;
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return left;
    }
}
