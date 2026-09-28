// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Domain.Time;

namespace ArcForges.ArcScope.Domain.Channels;

/// <summary>Describes a measured stream independently of any captured sample sequence.</summary>
public sealed record Channel<T> where T : notnull
{
    public Guid Id { get; }
    public string Name { get; }
    public string? DisplayUnit { get; }
    public ExactRate NominalSampleRate { get; }

    public Channel(Guid id, string name, string? displayUnit, ExactRate nominalSampleRate)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A channel needs a stable non-empty identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A channel name is required.", nameof(name));
        Id = id;
        Name = name;
        DisplayUnit = displayUnit;
        NominalSampleRate = nominalSampleRate ?? throw new ArgumentNullException(nameof(nominalSampleRate));
    }
}
