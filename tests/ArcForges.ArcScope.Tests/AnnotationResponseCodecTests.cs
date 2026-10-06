// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Core.Application;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;
using Google.Protobuf;
using Xunit;

namespace ArcForges.ArcScope.Tests;

public sealed class AnnotationResponseCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownWireFieldsInTheCapabilityEnvelopeOrConfigurationAreRefused(bool nestedConfiguration)
    {
        var result = AnnotationOperationCodec.Encode(CompleteResponse());
        byte[] unknown = [0xa0, 0x06, 0x01];
        if (nestedConfiguration)
        {
            var session = Field(Field(result.Value, "value"), "session");
            var configuration = session.Record.Entries.Single(entry => entry.Name == "configuration");
            configuration.Value = StructuredValue.Parser.ParseFrom(configuration.Value.ToByteArray().Concat(unknown).ToArray());
        }
        else
            result = CapabilityResult.Parser.ParseFrom(result.ToByteArray().Concat(unknown).ToArray());
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.DecodeGetSessionResponse(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownWireFieldsCannotBeSilentlyRemovedFromACompleteOwnerResponse(bool nestedConfiguration)
    {
        var response = CompleteResponse();
        byte[] unknown = [0xa0, 0x06, 0x01];
        if (nestedConfiguration)
            response.Value.Session.Configuration = ScopeConfiguration.Parser.ParseFrom(
                response.Value.Session.Configuration.ToByteArray().Concat(unknown).ToArray());
        else
            response = ScopeOperationsServiceGetSessionResponse.Parser.ParseFrom(response.ToByteArray().Concat(unknown).ToArray());
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.Encode(response));
    }

    [Fact]
    public void CompleteSessionConfigurationCapturesAndExplicitDefaultsRoundTrip()
    {
        var response = CompleteResponse();
        var encoded = AnnotationOperationCodec.Encode(response);
        var decoded = AnnotationOperationCodec.DecodeGetSessionResponse(encoded);
        Assert.Equal(response, decoded);
        Assert.True(decoded.Value.Session.Configuration.Framing.HasStart);
        Assert.Equal(ByteString.Empty, decoded.Value.Session.Configuration.Framing.Start);
        Assert.True(decoded.Value.Session.Configuration.Framing.HasHeader);
        Assert.False(decoded.Value.Session.Configuration.Framing.Header);
        Assert.Equal(ulong.MaxValue, decoded.Value.Session.Captures[0].SampleCount);
        Assert.Equal(long.MinValue, decoded.Meta.EntitlementVersion);
        Assert.Equal(ulong.MaxValue, decoded.Value.Session.Configuration.Trigger.Pre.Rate.Denominator);
    }

    [Theory]
    [InlineData("AQ")]
    [InlineData("AQ== ")]
    [InlineData("AR==")]
    [InlineData("-_==")]
    public void NonCanonicalByteEncodingCannotBecomeAStoredResponse(string text)
    {
        var encoded = AnnotationOperationCodec.Encode(CompleteResponse());
        Field(Field(Field(Field(encoded.Value, "value"), "session"), "configuration"), "framing").Record.Entries
            .Single(entry => entry.Name == "end").Value = new() { Text = text };
        Assert.ThrowsAny<ArgumentException>(() => AnnotationOperationCodec.DecodeGetSessionResponse(encoded));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteDeclaredMeasurementsAreRefused(double value)
    {
        var response = CompleteResponse();
        response.Value.Session.Configuration.Trigger.Threshold = value;
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.Encode(response));
    }

    [Fact]
    public void FailureEnvelopeAndMissingOwnerVersionCannotMasqueradeAsSuccess()
    {
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.Encode(new ScopeOperationsServiceCreateAnnotationResponse
        { Error = new() { Code = "perm.resource_denied" } }));
        Assert.Throws<ArgumentException>(() => AnnotationOperationCodec.Encode(new ScopeOperationsServiceCreateAnnotationResponse
        { Value = new() { Revision = new() } }));
        var response = new ScopeOperationsServiceCreateAnnotationResponse { Value = new() { Revision = new() { Value = ulong.MaxValue } }, Meta = new() { CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()) } };
        Assert.Equal(response, AnnotationOperationCodec.DecodeCreateAnnotationResponse(AnnotationOperationCodec.Encode(response)));
    }

    private static StructuredValue Field(StructuredValue record, string name) => record.Record.Entries.Single(entry => entry.Name == name).Value;

    private static ScopeOperationsServiceGetSessionResponse CompleteResponse()
    {
        var channel = new ChannelDefinition
        {
            ChannelId = UuidBoundary.ToWire(Guid.NewGuid()), Name = "电压", Unit = "V", SampleType = "binary64",
            Rate = new() { Numerator = long.MaxValue, Denominator = ulong.MaxValue },
            Calibration = new() { Scale = 0, Offset = -0.125, Unit = "V" },
        };
        var frame = new FrameConfiguration
        {
            Kind = "jsonLine", Start = ByteString.Empty, End = ByteString.CopyFrom([10]),
            Header = false, ByteOrder = "little",
        };
        var field = new FrameField { ChannelId = channel.ChannelId.Clone(), ScalarType = "f64", Required = false };
        field.JsonPath.Add(["samples", "0"]);
        frame.Fields.Add(field);
        var configuration = new ScopeConfiguration
        {
            ConfigurationId = UuidBoundary.ToWire(Guid.NewGuid()), ParserProfile = "v1", Revision = new() { Value = 1 }, Framing = frame,
            Trigger = new()
            {
                Kind = "edge", ChannelId = channel.ChannelId.Clone(), Threshold = -1, Direction = "rising", Hysteresis = 0,
                Holdoff = new() { Ticks = 0, Rate = new() { Numerator = 1, Denominator = 1 } },
                Pre = new() { Ticks = 0, Rate = new() { Numerator = long.MaxValue, Denominator = ulong.MaxValue } },
                Post = new() { Ticks = long.MaxValue, Rate = new() { Numerator = long.MaxValue, Denominator = 1 } }, Repeated = false, MaxOccurrences = 1,
            },
        };
        configuration.Channels.Add(channel);
        var capture = new CaptureMetadata
        {
            CaptureId = UuidBoundary.ToWire(Guid.NewGuid()), SampleCount = ulong.MaxValue, ContentHash = new string('a', 64),
            Resource = new()
            {
                RealmId = UuidBoundary.ToWire(Guid.NewGuid()), WorkspaceId = UuidBoundary.ToWire(Guid.NewGuid()),
                OwnerAppId = "arcscope", ResourceKind = "capture", ResourceId = UuidBoundary.ToWire(Guid.NewGuid()), DisplayHint = "",
                Availability = ResourceAvailability.AvailableOffline, HoldingDeviceId = UuidBoundary.ToWire(Guid.NewGuid()),
            },
        };
        capture.Channels.Add(channel.Clone());
        var session = new ScopeSession { SessionId = UuidBoundary.ToWire(Guid.NewGuid()), Name = "", Revision = new() { Value = ulong.MaxValue }, Configuration = configuration };
        session.Captures.Add(capture);
        var response = new ScopeOperationsServiceGetSessionResponse
        {
            Value = new() { Session = session },
            Meta = new() { ResultRev = new() { Value = long.MaxValue }, EntitlementVersion = long.MinValue, RecoveryGeneration = 0, CorrelationId = UuidBoundary.ToWire(Guid.NewGuid()) },
        };
        response.Meta.Warnings.Add("retained");
        return response;
    }
}
