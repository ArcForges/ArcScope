// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The annotation read handlers shared by the user interface and own-app capability invocation. Reads never mutate, and
/// a store result that breaks the read contract (wrong owner, wrong session, unordered or oversized page) is refused as
/// <c>resource.integrity_failed</c> rather than shown.
/// </summary>
public sealed class AnnotationQueries
{
    private readonly IAnnotationStore store;
    private readonly ISessionTargetCatalog sessions;

    public AnnotationQueries(IAnnotationStore store, ISessionTargetCatalog sessions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        this.store = store;
        this.sessions = sessions;
    }

    /// <summary>Reads one annotation with its history; a missing annotation refuses with <c>state.not_found</c>.</summary>
    public async ValueTask<Outcome<Annotation>> ReadAsync(ReadAnnotation query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled<Annotation>(EffectCertainty.DidNotHappen);
        try
        {
            var annotation = await store.ReadAsync(query.Annotation, cancellationToken).ConfigureAwait(false);
            if (annotation is null) return AnnotationFailures.Refuse<Annotation>(AnnotationFailures.NotFound);
            return annotation.Id == query.Annotation
                ? Outcome.Success(annotation)
                : AnnotationFailures.Refuse<Annotation>(AnnotationFailures.IntegrityFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled<Annotation>(EffectCertainty.DidNotHappen);
        }
        catch (InvalidDataException)
        {
            return AnnotationFailures.Refuse<Annotation>(AnnotationFailures.IntegrityFailed);
        }
        catch (Exception)
        {
            return AnnotationFailures.UnexpectedFailure<Annotation>(EffectCertainty.DidNotHappen);
        }
    }

    /// <summary>
    /// Reads one page of a session's annotations in the deterministic session order. An unregistered session refuses with
    /// <c>resource.unavailable</c>.
    /// </summary>
    public async ValueTask<Outcome<AnnotationPage>> ReadSessionAsync(ReadSessionAnnotations query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled<AnnotationPage>(EffectCertainty.DidNotHappen);
        try
        {
            var session = await sessions.FindAsync(query.Session, cancellationToken).ConfigureAwait(false);
            if (session is null || session.Id != query.Session) return AnnotationFailures.Refuse<AnnotationPage>(AnnotationFailures.SessionUnavailable);
            var items = await store.ReadSessionAsync(query.Session, query.After, query.Limit + 1, cancellationToken).ConfigureAwait(false);
            if (items is null || items.Count > query.Limit + 1) return AnnotationFailures.Refuse<AnnotationPage>(AnnotationFailures.IntegrityFailed);
            AnnotationPageCursor? previous = query.After;
            foreach (var item in items)
            {
                if (item is null || item.Session != query.Session) return AnnotationFailures.Refuse<AnnotationPage>(AnnotationFailures.IntegrityFailed);
                var position = AnnotationPageCursor.After(item);
                if (previous is { } before && AnnotationPageCursor.Compare(before, position) >= 0)
                    return AnnotationFailures.Refuse<AnnotationPage>(AnnotationFailures.IntegrityFailed);
                previous = position;
            }

            bool more = items.Count > query.Limit;
            var page = more ? items.Take(query.Limit).ToArray() : items.ToArray();
            return Outcome.Success(new AnnotationPage(page, more ? AnnotationPageCursor.After(page[^1]) : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Outcome.Cancelled<AnnotationPage>(EffectCertainty.DidNotHappen);
        }
        catch (InvalidDataException)
        {
            return AnnotationFailures.Refuse<AnnotationPage>(AnnotationFailures.IntegrityFailed);
        }
        catch (Exception)
        {
            return AnnotationFailures.UnexpectedFailure<AnnotationPage>(EffectCertainty.DidNotHappen);
        }
    }
}
