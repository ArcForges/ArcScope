// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// A capture session that annotations may target, with the number of samples recorded so far. Annotation ranges must lie
/// inside the recorded samples.
/// </summary>
public sealed record SessionTarget
{
    public SessionTarget(SessionTargetId id, ulong sampleCount)
    {
        _ = id.ToWire();
        Id = id;
        SampleCount = sampleCount;
    }

    public SessionTargetId Id { get; }

    /// <summary>The recorded sample count; it only grows, because recorded evidence is never removed.</summary>
    public ulong SampleCount { get; }
}

/// <summary>
/// The sessions annotations may target. The session owner registers a session and its growth; no annotation command or
/// capability creates a session. Annotating an unregistered session refuses with <c>resource.unavailable</c>.
/// </summary>
public interface ISessionTargetCatalog
{
    ValueTask<SessionTarget?> FindAsync(SessionTargetId id, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a session or records that it grew. A registration that would shrink the recorded sample count refuses with
    /// <c>state.invalid_transition</c>; re-registering the same count is an idempotent success.
    /// </summary>
    ValueTask<Outcome<SessionTarget>> RegisterAsync(SessionTarget target, CancellationToken cancellationToken);
}
