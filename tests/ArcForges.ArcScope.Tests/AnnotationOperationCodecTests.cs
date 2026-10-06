// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Application;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;
using Google.Protobuf;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class AnnotationOperationCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownWireFieldsInTheCapabilityEnvelopeOrMetadataAreRefused(bool nestedMetadata)
    {
        var arguments = AnnotationOperationCodec.Encode(Request());
        byte[] unknown = [0xa0, 0x06, 0x01];
        if (nestedMetadata)
        {
            var metadata = Entry(arguments.Value, "meta");
            metadata.Value = StructuredValue.Parser.ParseFrom(metadata.Value.ToByteArray().Concat(unknown).ToArray());
        }
        else
            arguments = CapabilityArguments.Parser.ParseFrom(arguments.ToByteArray().Concat(unknown).ToArray());
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownWireFieldsCannotBeSilentlyRemovedFromAnOperation(bool nestedMetadata)
    {
        var request = Request();
        // Field 100 is outside the published request and RequestMeta schemas.
        byte[] unknown = [0xa0, 0x06, 0x01];
        if (nestedMetadata)
            request.Meta = RequestMeta.Parser.ParseFrom(request.Meta.ToByteArray().Concat(unknown).ToArray());
        else
            request = ScopeOperationsServiceCreateAnnotationRequest.Parser.ParseFrom(request.ToByteArray().Concat(unknown).ToArray());
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.Encode(request));
    }

    [Fact]
    public void UnsignedMaximumAndExplicitZeroRoundTripWithoutFloatingPoint()
    {
        var request = Request();
        request.Range.From = ulong.MaxValue;
        request.Range.Count = 0;
        request.Meta.ExpectedNative.Value = ulong.MaxValue;
        request.Meta.RecoveryGeneration = 0;
        var encoded = AnnotationOperationCodec.Encode(request);
        var decoded = AnnotationOperationCodec.DecodeCreateAnnotation(encoded);
        Assert.Equal(ulong.MaxValue, decoded.Range.From);
        Assert.True(decoded.Range.HasCount);
        Assert.Equal(0UL, decoded.Range.Count);
        Assert.Equal(ulong.MaxValue, decoded.Meta.ExpectedNative.Value);
        Assert.True(decoded.Meta.HasRecoveryGeneration);
        Assert.Equal(0UL, decoded.Meta.RecoveryGeneration);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    [InlineData("18446744073709551616")]
    public void NonCanonicalOrOverflowingUnsignedNumbersAreRefused(string text)
    {
        var arguments = AnnotationOperationCodec.Encode(Request());
        Entry(Entry(arguments.Value, "range").Value, "from").Value = new() { Text = text };
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments));
    }

    [Fact]
    public void SignedIntegerAndBinary64CannotReplaceAnUnsignedDecimalText()
    {
        foreach (var number in new[] { new StructuredValue { Integer = 1 }, new StructuredValue { Number = 1 } })
        {
            var arguments = AnnotationOperationCodec.Encode(Request());
            Entry(Entry(arguments.Value, "range").Value, "from").Value = number;
            Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments));
        }
    }

    [Fact]
    public void MissingRangeCountDoesNotBecomeAnExplicitZero()
    {
        var arguments = AnnotationOperationCodec.Encode(Request());
        var range = Entry(arguments.Value, "range").Value;
        range.Record.Entries.Remove(Entry(range, "count"));
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments));
    }

    [Fact]
    public void SignedCloudRevisionUsesItsAuthoredIntegerArmAndPreservesMaximum()
    {
        var request = Request();
        request.Meta.ExpectedRev = new() { Value = long.MaxValue };
        var encoded = AnnotationOperationCodec.Encode(request);
        var field = Entry(Entry(encoded.Value, "meta").Value, "expectedRev");
        Assert.Equal(StructuredValue.ValueOneofCase.Integer, field.Value.ValueCase);
        Assert.Equal(long.MaxValue, AnnotationOperationCodec.DecodeCreateAnnotation(encoded).Meta.ExpectedRev.Value);
        field.Value = new() { Text = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(encoded));
    }

    [Fact]
    public void AbsentMetadataScalarRemainsAbsent()
    {
        var request = Request();
        Assert.False(request.Meta.HasRecoveryGeneration);
        var decoded = AnnotationOperationCodec.DecodeCreateAnnotation(AnnotationOperationCodec.Encode(request));
        Assert.False(decoded.Meta.HasRecoveryGeneration);
        request.Meta.RecoveryGeneration = 0;
        decoded = AnnotationOperationCodec.DecodeCreateAnnotation(AnnotationOperationCodec.Encode(request));
        Assert.True(decoded.Meta.HasRecoveryGeneration);
    }

    [Fact]
    public void UnknownAuthorizationKeysAndRepeatedOrUnsortedKeysAreRefused()
    {
        var unknown = AnnotationOperationCodec.Encode(Request());
        unknown.Value.Record.Entries.Add(new ValueEntry { Name = "trust", Value = new() { Boolean = true } });
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(unknown));

        var repeated = AnnotationOperationCodec.Encode(Request());
        repeated.Value.Record.Entries.Insert(1, repeated.Value.Record.Entries[0].Clone());
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(repeated));

        var unsorted = AnnotationOperationCodec.Encode(Request());
        var first = unsorted.Value.Record.Entries[0];
        unsorted.Value.Record.Entries.RemoveAt(0);
        unsorted.Value.Record.Entries.Add(first);
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(unsorted));
    }

    [Fact]
    public void NestedDuplicateKeysAndActorClaimsAreRefused()
    {
        var duplicate = AnnotationOperationCodec.Encode(Request());
        var range = Entry(duplicate.Value, "range").Value;
        range.Record.Entries.Insert(1, range.Record.Entries[0].Clone());
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(duplicate));

        var actor = AnnotationOperationCodec.Encode(Request());
        var metadata = Entry(actor.Value, "meta").Value;
        metadata.Record.Entries.Insert(0, new() { Name = "actor", Value = new() { Text = "administrator" } });
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(actor));
    }

    [Fact]
    public void InProcessCyclesAreRefusedBeforeRecursiveProtobufClone()
    {
        var cycle = new StructuredValue { Record = new() };
        cycle.Record.Entries.Add(new ValueEntry { Name = "sessionId", Value = cycle });
        var arguments = new CapabilityArguments { SchemaId = AnnotationOperationCodec.GetSessionSchema, Value = cycle };
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeGetSession(arguments));
    }

    [Fact]
    public void GetSessionPreservesExactIdentityAndAuthorityMetadataForLaterOwnerValidation()
    {
        var create = Request();
        var read = new ScopeOperationsServiceGetSessionRequest { SessionId = create.SessionId.Clone(), Meta = create.Meta.Clone() };
        var decoded = AnnotationOperationCodec.DecodeGetSession(AnnotationOperationCodec.Encode(read));
        Assert.Equal(read.SessionId, decoded.SessionId);
        Assert.Equal(read.Meta, decoded.Meta);
    }

    [Fact]
    public void GuidCaseAndEmptyIdentityAreRejected()
    {
        foreach (var id in new[] { "00000000-0000-0000-0000-000000000000", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", "not-an-id" })
        {
            var arguments = AnnotationOperationCodec.Encode(Request());
            Entry(arguments.Value, "sessionId").Value = new() { Text = id };
            Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments));
        }
    }

    [Fact]
    public void InvalidUtf8AndOversizedUnicodeTextAreRefused()
    {
        foreach (var text in new[] { "\uD800", new string('界', 1366) })
        {
            var arguments = AnnotationOperationCodec.Encode(Request());
            Entry(arguments.Value, "text").Value = new() { Text = text };
            Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeCreateAnnotation(arguments));
        }
    }

    private static ValueEntry Entry(StructuredValue value, string key) => value.Record.Entries.Single(entry => entry.Name == key);
    private static ScopeOperationsServiceCreateAnnotationRequest Request() => new()
    {
        AnnotationId = UuidBoundary.ToWire(Guid.NewGuid()),
        SessionId = UuidBoundary.ToWire(Guid.NewGuid()),
        Range = new() { From = 0, Count = 0 },
        Text = "A real annotation",
        Meta = new()
        {
            CommandId = UuidBoundary.ToWire(Guid.NewGuid()),
            CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()),
            ExpectedNative = new() { Value = 1 },
            ApplicationScope = new() { ProductId = "arcscope", InstallationId = UuidBoundary.ToWire(Guid.NewGuid()) },
        },
    };
}
