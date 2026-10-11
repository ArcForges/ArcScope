// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation.Errors;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The presentation-agnostic annotation command surface of the ArcScope user interface. It turns untrusted user input into
/// typed commands for the local person (a human actor whose text is <see cref="AnnotationContentKind.NonAi"/>) and runs
/// them through the same <see cref="AnnotationWritePath"/> and <see cref="AnnotationQueries"/> that own-app capability
/// invocation uses. Invalid input refuses with <c>validation.invalid_request</c> before any store access. The view keeps
/// one command identity per user action and reuses it when the person retries that action.
/// </summary>
public sealed class AnnotationCommandSurface
{
    private readonly AnnotationWritePath writePath;
    private readonly AnnotationQueries queries;
    private readonly AnnotationActor person;

    public AnnotationCommandSurface(AnnotationWritePath writePath, AnnotationQueries queries, UserId localPerson)
    {
        ArgumentNullException.ThrowIfNull(writePath);
        ArgumentNullException.ThrowIfNull(queries);
        this.writePath = writePath;
        this.queries = queries;
        person = AnnotationActor.Human(localPerson);
    }

    public ValueTask<Outcome<AnnotationWriteReceipt>> CreateAsync(Guid commandId, Guid annotationId, Guid sessionId,
        ulong rangeFrom, ulong rangeCount, string? text, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty || !AnnotationId.TryCreate(annotationId, out var annotation)
            || !SessionTargetId.TryCreate(sessionId, out var session) || !SampleRange.TryCreate(rangeFrom, rangeCount, out var range)
            || !AnnotationText.TryCreate(text, out var body))
        {
            return Invalid<AnnotationWriteReceipt>();
        }

        return writePath.CreateAsync(new CreateAnnotation(new CommandId(commandId), annotation, session, range, body, person,
            AnnotationContentKind.NonAi), cancellationToken);
    }

    public ValueTask<Outcome<AnnotationWriteReceipt>> AppendAsync(Guid commandId, Guid annotationId, ulong expectedRevision,
        string? text, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty || !AnnotationId.TryCreate(annotationId, out var annotation) || expectedRevision == 0
            || !AnnotationText.TryCreate(text, out var body))
        {
            return Invalid<AnnotationWriteReceipt>();
        }

        return writePath.AppendAsync(new AppendAnnotation(new CommandId(commandId), annotation, new NativeRevision(expectedRevision),
            body, person, AnnotationContentKind.NonAi), cancellationToken);
    }

    public ValueTask<Outcome<Annotation>> ReadAsync(Guid annotationId, CancellationToken cancellationToken) =>
        AnnotationId.TryCreate(annotationId, out var annotation)
            ? queries.ReadAsync(new ReadAnnotation(annotation), cancellationToken)
            : Invalid<Annotation>();

    public ValueTask<Outcome<AnnotationPage>> ReadSessionAsync(Guid sessionId, int limit, AnnotationPageCursor? after,
        CancellationToken cancellationToken)
    {
        if (!SessionTargetId.TryCreate(sessionId, out var session) || limit is < 1 or > ReadSessionAnnotations.MaxPageSize
            || after is { } cursor && (cursor.Count == 0 || cursor.Annotation == Guid.Empty))
        {
            return Invalid<AnnotationPage>();
        }

        return queries.ReadSessionAsync(new ReadSessionAnnotations(session, limit, after), cancellationToken);
    }

    private static ValueTask<Outcome<T>> Invalid<T>() =>
        ValueTask.FromResult(AnnotationFailures.Refuse<T>(AnnotationFailures.InvalidRequest));
}
