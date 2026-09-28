// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Domain.Time;

namespace ArcForges.ArcScope.Domain.Channels;

/// <summary>A discrete event at one exact time, modelled separately from sampled signals.</summary>
public sealed record EventRecord<T> where T : notnull
{
    public Guid Id { get; }
    public string Kind { get; }
    public TimePoint Time { get; }
    public T Payload { get; }

    public EventRecord(Guid id, string kind, TimePoint time, T payload)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An event needs a stable non-empty identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("An event kind is required.", nameof(kind));
        Id = id;
        Kind = kind;
        Time = time ?? throw new ArgumentNullException(nameof(time));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }
}
