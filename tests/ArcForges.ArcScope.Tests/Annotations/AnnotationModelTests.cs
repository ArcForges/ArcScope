// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Execution;
using Xunit;

namespace ArcForges.ArcScope.Tests.Annotations;

public sealed class AnnotationModelTests
{
    private static readonly UserId Person = new(Guid.Parse("6f2b1c1e-8d4a-4c55-9b0e-0123456789ab"));

    [Fact]
    public void IdentitiesRefuseTheEmptyValueAndRoundTripTheCanonicalWireOrder()
    {
        Assert.Throws<ArgumentException>(() => new AnnotationId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SessionTargetId(Guid.Empty));
        Assert.False(AnnotationId.TryCreate(Guid.Empty, out _));
        Assert.False(SessionTargetId.TryCreate(Guid.Empty, out _));
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.True(AnnotationId.TryCreate(id, out var annotation));
        Assert.Equal(annotation, AnnotationId.FromWire(annotation.ToWire()));
        Assert.Equal(Convert.FromHexString("00112233445566778899aabbccddeeff"), annotation.ToWire().Value.ToByteArray());
        Assert.True(SessionTargetId.TryCreate(id, out var session));
        Assert.Equal(session, SessionTargetId.FromWire(session.ToWire()));
        Assert.Throws<ArgumentException>(() => default(AnnotationId).ToWire());
    }

    [Fact]
    public void SampleRangesAreHalfOpenNonEmptyAndOverflowChecked()
    {
        var range = new SampleRange(10, 5);
        Assert.Equal(15UL, range.End);
        Assert.True(range.IsWithin(15));
        Assert.False(range.IsWithin(14));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleRange(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleRange(ulong.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleRange(2, ulong.MaxValue - 1));
        Assert.True(SampleRange.TryCreate(ulong.MaxValue - 1, 1, out var last));
        Assert.Equal(ulong.MaxValue, last.End);
        Assert.False(SampleRange.TryCreate(0, 0, out _));
        Assert.False(default(SampleRange).IsInitialized);
    }

    [Fact]
    public void TextIsPreservedExactlyAndRefusesBlankNulMalformedOrOversizedInput()
    {
        const string decomposed = "é at 3.2 kHz";
        var text = AnnotationText.Create(decomposed);
        Assert.Equal(decomposed, text.Value);
        Assert.NotEqual(AnnotationText.Create("é at 3.2 kHz"), text);
        Assert.Equal(Encoding.UTF8.GetByteCount(decomposed), text.Utf8ByteCount);

        foreach (var invalid in new[] { null, "", "   \t\n", "a\0b", "lone \ud800 high", "lone \udc00 low", "end \ud83d" })
            Assert.False(AnnotationText.TryCreate(invalid, out _));
        Assert.Throws<ArgumentException>(() => AnnotationText.Create("\ud800"));

        Assert.True(AnnotationText.TryCreate(new string('a', AnnotationText.MaxUtf8Bytes), out var largest));
        Assert.Equal(AnnotationText.MaxUtf8Bytes, largest.Utf8ByteCount);
        Assert.False(AnnotationText.TryCreate(new string('a', AnnotationText.MaxUtf8Bytes + 1), out _));
        // 87 382 three-byte characters are 262 146 UTF-8 bytes: under the UTF-16 length bound, over the byte bound.
        Assert.False(AnnotationText.TryCreate(new string('€', 87_382), out _));
        Assert.True(AnnotationText.TryCreate("😀 pair", out _));
    }

    [Fact]
    public void TextHasNoLoggingRepresentation()
    {
        var text = AnnotationText.Create("secret probe note");
        Assert.DoesNotContain("secret", text.ToString(), StringComparison.Ordinal);
        var command = new CreateAnnotation(new CommandId(Guid.NewGuid()), new AnnotationId(Guid.NewGuid()), new SessionTargetId(Guid.NewGuid()),
            new SampleRange(0, 1), text, AnnotationActor.Human(Person), AnnotationContentKind.NonAi);
        Assert.DoesNotContain("secret", command.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ActorsAreClosedAndAProductHandlerAlwaysNamesItsInvocation()
    {
        var human = AnnotationActor.Human(Person);
        Assert.Equal(AnnotationActorKind.Human, human.Kind);
        Assert.Null(human.Invocation);
        var invocation = new InvocationId(Guid.NewGuid());
        var handler = AnnotationActor.ProductHandler(Person, invocation);
        Assert.Equal(AnnotationActorKind.ProductHandler, handler.Kind);
        Assert.Equal(invocation, handler.Invocation);
        Assert.NotEqual(human, handler);
        Assert.Throws<ArgumentException>(() => AnnotationActor.Human(default));
        Assert.Throws<ArgumentException>(() => AnnotationActor.ProductHandler(Person, default));
    }

    [Fact]
    public void ContentKindsUseTheCarrierKeysAndHaveNoDefault()
    {
        Assert.Equal(new[] { "aiGenerated", "aiManipulated", "nonAi", "unknown" },
            Enum.GetValues<AnnotationContentKind>().Order().Select(AnnotationContentKinds.Key));
        Assert.False(AnnotationContentKinds.IsDefined(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnnotationContentKinds.Key(default));
    }

    [Fact]
    public void AnAnnotationHistoryIsContiguousUniqueAndBounded()
    {
        var id = new AnnotationId(Guid.NewGuid());
        var session = new SessionTargetId(Guid.NewGuid());
        var range = new SampleRange(0, 10);
        var first = Entry(1, "first");
        var second = Entry(2, "second", AnnotationContentKind.AiGenerated);
        var annotation = new Annotation(id, session, range, [first, second]);
        Assert.Equal(new NativeRevision(2), annotation.Revision);
        Assert.Equal(new[] { AnnotationContentKind.AiGenerated, AnnotationContentKind.NonAi }, annotation.OriginKinds);
        Assert.Same(second, annotation.FindEntry(second.Command));
        Assert.Null(annotation.FindEntry(new CommandId(Guid.NewGuid())));
        Assert.Equal(11, annotation.TextUtf8ByteCount);

        Assert.Throws<ArgumentException>(() => new Annotation(id, session, range, []));
        Assert.Throws<ArgumentException>(() => new Annotation(id, session, range, [Entry(2, "gap")]));
        Assert.Throws<ArgumentException>(() => new Annotation(id, session, range, [first, Entry(3, "skip")]));
        Assert.Throws<ArgumentException>(() => new Annotation(id, session, range, [first, Entry(2, "again", command: first.Command)]));
        Assert.Throws<ArgumentException>(() => new Annotation(id, session, default, [first]));
        Assert.Throws<ArgumentException>(() => new Annotation(default, session, range, [first]));
        Assert.Throws<ArgumentException>(() => new Annotation(id, session, range,
            Enumerable.Range(1, Annotation.MaxEntries + 1).Select(revision => Entry((ulong)revision, "n"))));
        var half = new string('x', AnnotationText.MaxUtf8Bytes / 2);
        Assert.Throws<ArgumentException>(() => new Annotation(id, session, range, [Entry(1, half), Entry(2, half), Entry(3, "x")]));

        var full = new Annotation(id, session, range, [Entry(1, half), Entry(2, half)]);
        Assert.False(full.CanAppend(1));
        Assert.True(annotation.CanAppend(AnnotationText.MaxUtf8Bytes - 11));
        Assert.False(annotation.CanAppend(AnnotationText.MaxUtf8Bytes - 10));
        var longest = new Annotation(id, session, range, Enumerable.Range(1, Annotation.MaxEntries).Select(revision => Entry((ulong)revision, "n")));
        Assert.False(longest.CanAppend(1));
    }

    [Fact]
    public void EntriesRefuseUncommittedOrUnidentifiedChanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Entry(0, "zero"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Entry(1, "kind", (AnnotationContentKind)9));
        Assert.Throws<ArgumentException>(() => new AnnotationEntry(new NativeRevision(1), AnnotationText.Create("x"), AnnotationActor.Human(Person),
            AnnotationContentKind.NonAi, new Instant(0, 0), default, new string('a', 64)));
        foreach (var fingerprint in new[] { "", new string('A', 64), new string('a', 63), new string('g', 64) })
            Assert.Throws<ArgumentException>(() => new AnnotationEntry(new NativeRevision(1), AnnotationText.Create("x"),
                AnnotationActor.Human(Person), AnnotationContentKind.NonAi, new Instant(0, 0), new CommandId(Guid.NewGuid()), fingerprint));
    }

    [Fact]
    public void CommandsAndQueriesRefuseUninitializedOrOutOfBoundsFields()
    {
        var text = AnnotationText.Create("note");
        var human = AnnotationActor.Human(Person);
        var annotation = new AnnotationId(Guid.NewGuid());
        var session = new SessionTargetId(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new CreateAnnotation(default, annotation, session, new SampleRange(0, 1), text, human, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentException>(() => new CreateAnnotation(new CommandId(Guid.NewGuid()), default, session, new SampleRange(0, 1), text, human, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentException>(() => new CreateAnnotation(new CommandId(Guid.NewGuid()), annotation, default, new SampleRange(0, 1), text, human, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentException>(() => new CreateAnnotation(new CommandId(Guid.NewGuid()), annotation, session, default, text, human, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreateAnnotation(new CommandId(Guid.NewGuid()), annotation, session, new SampleRange(0, 1), text, human, default));
        Assert.Throws<ArgumentNullException>(() => new CreateAnnotation(new CommandId(Guid.NewGuid()), annotation, session, new SampleRange(0, 1), null!, human, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppendAnnotation(new CommandId(Guid.NewGuid()), annotation, new NativeRevision(0), text, human, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentNullException>(() => new AppendAnnotation(new CommandId(Guid.NewGuid()), annotation, new NativeRevision(1), text, null!, AnnotationContentKind.NonAi));
        Assert.Throws<ArgumentException>(() => new ReadAnnotation(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadSessionAnnotations(session, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadSessionAnnotations(session, ReadSessionAnnotations.MaxPageSize + 1));
        Assert.Throws<ArgumentException>(() => new ReadSessionAnnotations(session, 10, new AnnotationPageCursor(0, 0, Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => new ReadSessionAnnotations(session, 10, new AnnotationPageCursor(0, 1, Guid.Empty)));
        Assert.Equal(ReadSessionAnnotations.MaxPageSize, new ReadSessionAnnotations(session, ReadSessionAnnotations.MaxPageSize).Limit);
    }

    [Fact]
    public void TheSessionOrderIsRangeThenLengthThenCanonicalIdentityText()
    {
        var low = Guid.Parse("0fffffff-ffff-ffff-ffff-ffffffffffff");
        var high = Guid.Parse("f0000000-0000-0000-0000-000000000000");
        Assert.True(AnnotationPageCursor.Compare(new(1, 9, high), new(2, 1, low)) < 0);
        Assert.True(AnnotationPageCursor.Compare(new(1, 1, high), new(1, 2, low)) < 0);
        Assert.True(AnnotationPageCursor.Compare(new(1, 1, low), new(1, 1, high)) < 0);
        Assert.Equal(0, AnnotationPageCursor.Compare(new(1, 1, low), new(1, 1, low)));
        // Guid.CompareTo is not the canonical byte order for every value; the session order never uses it.
        var a = Guid.Parse("00000000-0000-0001-0000-000000000000");
        var b = Guid.Parse("00000000-0000-0000-0100-000000000000");
        Assert.Equal(Math.Sign(string.CompareOrdinal(a.ToString("D"), b.ToString("D"))), Math.Sign(AnnotationPageCursor.Compare(new(0, 1, a), new(0, 1, b))));
    }

    [Fact]
    public void TheCommandFingerprintIsTheSha256OfTheCanonicalCommandJson()
    {
        var command = new CreateAnnotation(new CommandId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new AnnotationId(Guid.Parse("22222222-2222-2222-2222-222222222222")), new SessionTargetId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            new SampleRange(18_446_744_073_709_551_000, 7), AnnotationText.Create("Peak \"A\" – café"), AnnotationActor.Human(Person), AnnotationContentKind.NonAi);
        const string canonical = "{\"actor\":{\"invocation\":null,\"kind\":\"human\",\"user\":\"6f2b1c1e-8d4a-4c55-9b0e-0123456789ab\"},"
            + "\"annotation\":\"22222222-2222-2222-2222-222222222222\",\"contentKind\":\"nonAi\",\"expectedRevision\":\"0\","
            + "\"operation\":\"arcscope.annotation.create\",\"profile\":\"arcscope.annotation-command.v1\","
            + "\"range\":{\"count\":\"7\",\"from\":\"18446744073709551000\"},\"session\":\"33333333-3333-3333-3333-333333333333\","
            + "\"text\":\"Peak \\u0022A\\u0022 \\u2013 caf\\u00E9\"}";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), command.Fingerprint());

        var invocation = new InvocationId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var append = new AppendAnnotation(new CommandId(Guid.NewGuid()), command.Annotation, new NativeRevision(3), AnnotationText.Create("more"),
            AnnotationActor.ProductHandler(Person, invocation), AnnotationContentKind.AiGenerated);
        const string appendCanonical = "{\"actor\":{\"invocation\":\"44444444-4444-4444-4444-444444444444\",\"kind\":\"productHandler\","
            + "\"user\":\"6f2b1c1e-8d4a-4c55-9b0e-0123456789ab\"},\"annotation\":\"22222222-2222-2222-2222-222222222222\","
            + "\"contentKind\":\"aiGenerated\",\"expectedRevision\":\"3\",\"operation\":\"arcscope.annotation.append\","
            + "\"profile\":\"arcscope.annotation-command.v1\",\"text\":\"more\"}";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(appendCanonical))), append.Fingerprint());
    }

    [Fact]
    public void TheFingerprintCoversEverySemanticFieldButNotTheCommandIdentity()
    {
        var annotation = new AnnotationId(Guid.NewGuid());
        var session = new SessionTargetId(Guid.NewGuid());
        CreateAnnotation Make(Guid? command = null, AnnotationId? id = null, SessionTargetId? target = null, ulong from = 1, ulong count = 2,
            string text = "t", AnnotationActor? actor = null, AnnotationContentKind kind = AnnotationContentKind.NonAi) =>
            new(new CommandId(command ?? Guid.NewGuid()), id ?? annotation, target ?? session, new SampleRange(from, count), AnnotationText.Create(text),
                actor ?? AnnotationActor.Human(Person), kind);

        string baseline = Make().Fingerprint();
        Assert.True(AnnotationCommandFingerprint.IsWellFormed(baseline));
        Assert.Equal(baseline, Make().Fingerprint());
        var variants = new[]
        {
            Make(id: new AnnotationId(Guid.NewGuid())), Make(target: new SessionTargetId(Guid.NewGuid())), Make(from: 2), Make(count: 3),
            Make(text: "T"), Make(text: "t "), Make(actor: AnnotationActor.Human(new UserId(Guid.NewGuid()))),
            Make(actor: AnnotationActor.ProductHandler(Person, new InvocationId(Guid.NewGuid()))), Make(kind: AnnotationContentKind.AiGenerated),
        };
        Assert.All(variants, variant => Assert.NotEqual(baseline, variant.Fingerprint()));
        Assert.Equal(variants.Length, variants.Select(variant => variant.Fingerprint()).Distinct(StringComparer.Ordinal).Count());
    }

    private static AnnotationEntry Entry(ulong revision, string text, AnnotationContentKind kind = AnnotationContentKind.NonAi, CommandId? command = null) =>
        new(new NativeRevision(revision), AnnotationText.Create(text), AnnotationActor.Human(Person), kind, new Instant(1_791_000_000, 0),
            command ?? new CommandId(Guid.NewGuid()), new string('a', 64));
}
