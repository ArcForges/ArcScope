// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation.Execution;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>The closed set of actors that may author an annotation change (the CreateAnnotation actors).</summary>
public enum AnnotationActorKind
{
    /// <summary>The local person acting through the ArcScope user interface.</summary>
    Human = 1,

    /// <summary>ArcScope's own product handler acting for that person through an own-app capability invocation.</summary>
    ProductHandler = 2,
}

/// <summary>
/// Who authored one annotation change. A human change carries no invocation; a product-handler change always carries the
/// invocation that requested it, so the history shows which capability call produced each entry.
/// </summary>
public sealed record AnnotationActor
{
    private AnnotationActor(AnnotationActorKind kind, UserId user, InvocationId? invocation)
    {
        Kind = kind;
        User = user;
        Invocation = invocation;
    }

    public AnnotationActorKind Kind { get; }

    /// <summary>The person the change is made by or for.</summary>
    public UserId User { get; }

    /// <summary>The own-app capability invocation, present exactly for <see cref="AnnotationActorKind.ProductHandler"/>.</summary>
    public InvocationId? Invocation { get; }

    public static AnnotationActor Human(UserId user)
    {
        _ = user.ToWire();
        return new AnnotationActor(AnnotationActorKind.Human, user, null);
    }

    public static AnnotationActor ProductHandler(UserId onBehalfOf, InvocationId invocation)
    {
        _ = onBehalfOf.ToWire();
        _ = invocation.ToWire();
        return new AnnotationActor(AnnotationActorKind.ProductHandler, onBehalfOf, invocation);
    }
}

/// <summary>
/// The content-origin kind of one entry's text, in the canonical order of the shared content-origin carrier. There is no
/// default: every change declares its kind, and an annotation's origin is the union over its history.
/// </summary>
public enum AnnotationContentKind
{
    AiGenerated = 1,
    AiManipulated = 2,
    NonAi = 3,
    Unknown = 4,
}

/// <summary>Canonical keys of <see cref="AnnotationContentKind"/>, matching the content-origin carrier profile.</summary>
public static class AnnotationContentKinds
{
    public static string Key(AnnotationContentKind kind) => kind switch
    {
        AnnotationContentKind.AiGenerated => "aiGenerated",
        AnnotationContentKind.AiManipulated => "aiManipulated",
        AnnotationContentKind.NonAi => "nonAi",
        AnnotationContentKind.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "A known content-origin kind is required."),
    };

    public static bool IsDefined(AnnotationContentKind kind) => kind is AnnotationContentKind.AiGenerated
        or AnnotationContentKind.AiManipulated or AnnotationContentKind.NonAi or AnnotationContentKind.Unknown;
}
