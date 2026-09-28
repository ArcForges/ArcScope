// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using ArcForges.ArcScope.Domain.Time;

namespace ArcForges.ArcScope.Domain.Channels;

/// <summary>One timestamped value in a signal. Values are retained without unit conversion.</summary>
public sealed record SignalSample<T> where T : notnull
{
    public TimePoint Time { get; }
    public T Value { get; }

    public SignalSample(TimePoint time, T value)
    {
        Time = time ?? throw new ArgumentNullException(nameof(time));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>A typed, ordered sample sequence for a channel; irregular spacing is preserved.</summary>
public sealed class Signal<T> where T : notnull
{
    private readonly ReadOnlyCollection<SignalSample<T>> _samples;

    public Channel<T> Channel { get; }
    public TimeDomain Domain { get; }
    public IReadOnlyList<SignalSample<T>> Samples => _samples;

    public Signal(Channel<T> channel, TimeDomain domain, IEnumerable<SignalSample<T>> samples)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        ArgumentNullException.ThrowIfNull(samples);

        var owned = samples.ToArray();
        for (var index = 0; index < owned.Length; index++)
        {
            var sample = owned[index] ?? throw new ArgumentException("A signal cannot contain a null sample.", nameof(samples));
            if (sample.Time.Domain != Domain)
                throw new ArgumentException("Every signal sample must use the signal's time domain.", nameof(samples));
            if (index > 0 && owned[index - 1].Time.CompareTo(sample.Time) >= 0)
                throw new ArgumentException("Signal sample times must be strictly increasing; gaps and irregular spacing are allowed.", nameof(samples));
        }

        _samples = Array.AsReadOnly(owned);
    }
}
