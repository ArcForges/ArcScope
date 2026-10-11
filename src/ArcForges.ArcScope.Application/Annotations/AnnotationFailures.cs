// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The closed set of registered failure codes the annotation services produce. No other code leaves the Application layer:
/// an unexpected store result becomes <c>internal.unexpected</c> with the effect certainty the write path can prove.
/// </summary>
public static class AnnotationFailures
{
    public const string InvalidRequest = "validation.invalid_request";
    public const string RevisionMismatch = "conflict.revision_mismatch";
    public const string DuplicateIdentifier = "conflict.duplicate_identifier";
    public const string ReusedCommand = "command.reused_identifier";
    public const string NotFound = "state.not_found";
    public const string InvalidTransition = "state.invalid_transition";
    public const string SessionUnavailable = "resource.unavailable";
    public const string IntegrityFailed = "resource.integrity_failed";
    public const string Unexpected = "internal.unexpected";

    /// <summary>The refusals a store commit may report; each occurs before any effect.</summary>
    public static IReadOnlySet<string> CommitRefusals { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RevisionMismatch, DuplicateIdentifier, ReusedCommand, IntegrityFailed,
    };

    /// <summary>Every code the annotation services can return.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        InvalidRequest, RevisionMismatch, DuplicateIdentifier, ReusedCommand, NotFound, InvalidTransition,
        SessionUnavailable, IntegrityFailed, Unexpected,
    };

    public static Outcome<T> Refuse<T>(string code) => Outcome.Failure<T>(TypedFailure.Create(code));

    /// <summary>An unexpected failure; <paramref name="effect"/> states what the write path can prove about the effect.</summary>
    public static Outcome<T> UnexpectedFailure<T>(EffectCertainty effect) =>
        Outcome.Failure<T>(TypedFailure.Create(Unexpected, effect: effect));
}
