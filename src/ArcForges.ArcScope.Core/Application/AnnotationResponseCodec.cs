// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;
using Google.Protobuf;
using ArcForges.Contracts.LocalRpc.Scope.Shapes;

namespace ArcForges.ArcScope.Core.Application;

internal static partial class AnnotationOperationCodec
{
    internal const string GetSessionResponseSchema = "arcforges.local.scope.v1.ScopeOperationsServiceGetSessionResponse";
    internal const string CreateAnnotationResponseSchema = "arcforges.local.scope.v1.ScopeOperationsServiceCreateAnnotationResponse";

    internal static CapabilityResult Encode(ScopeOperationsServiceGetSessionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.OutcomeCase != ScopeOperationsServiceGetSessionResponse.OutcomeOneofCase.Value || response.Value.Session is null)
            throw new ArgumentException("Only the exact typed owner's successful session response is encoded.");
        var fields = new List<(string Key, StructuredValue Value)>
        {
            ("value", Record([("session", EncodeScopeSession(response.Value.Session))])),
        };
        if (response.Meta is not null) fields.Add(("meta", EncodeResponseMeta(response.Meta)));
        var result = new CapabilityResult { SchemaId = GetSessionResponseSchema, Value = Record(fields) };
        _ = DecodeGetSessionResponse(result);
        return result;
    }

    internal static ScopeOperationsServiceGetSessionResponse DecodeGetSessionResponse(CapabilityResult result)
    {
        RequireResult(result, GetSessionResponseSchema);
        var fields = Record(result.Value, "meta", "value");
        var payload = Record(Required(fields, "value"), "session");
        var session = DecodeScopeSession(Required(payload, "session"));
        if (session.SessionId is null || session.Revision is not { HasValue: true, Value: > 0 })
            throw new ArgumentException("An owned session requires its exact identity and committed native revision.");
        var response = new ScopeOperationsServiceGetSessionResponse
        {
            Value = new() { Session = session },
            Meta = fields.TryGetValue("meta", out var metadata) ? DecodeResponseMeta(metadata) : null,
        };
        if (!ContractShapeValidation.IsValid(response)) throw new ArgumentException("The owned session response violates the published shape contract.");
        return response;
    }

    internal static CapabilityResult Encode(ScopeOperationsServiceCreateAnnotationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.OutcomeCase != ScopeOperationsServiceCreateAnnotationResponse.OutcomeOneofCase.Value || response.Value.Revision is null)
            throw new ArgumentException("Only the exact typed owner's successful mutation response is encoded.");
        var fields = new List<(string Key, StructuredValue Value)>
        {
            ("value", Record([("revision", Native(response.Value.Revision))])),
        };
        if (response.Meta is not null) fields.Add(("meta", EncodeResponseMeta(response.Meta)));
        var result = new CapabilityResult { SchemaId = CreateAnnotationResponseSchema, Value = Record(fields) };
        _ = DecodeCreateAnnotationResponse(result);
        return result;
    }

    internal static ScopeOperationsServiceCreateAnnotationResponse DecodeCreateAnnotationResponse(CapabilityResult result)
    {
        RequireResult(result, CreateAnnotationResponseSchema);
        var fields = Record(result.Value, "meta", "value");
        var payload = Record(Required(fields, "value"), "revision");
        var response = new ScopeOperationsServiceCreateAnnotationResponse
        {
            Value = new() { Revision = new() { Value = PositiveUnsigned(Required(payload, "revision")) } },
            Meta = fields.TryGetValue("meta", out var metadata) ? DecodeResponseMeta(metadata) : null,
        };
        if (!ContractShapeValidation.IsValid(response)) throw new ArgumentException("The annotation result violates the published shape contract.");
        return response;
    }

    internal static void ValidateResult(CapabilityResult result)
        => _ = NativeVersionOfResult(result);

    internal static ulong NativeVersionOfResult(CapabilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SchemaId == GetSessionResponseSchema) return DecodeGetSessionResponse(result).Value.Session.Revision.Value;
        if (result.SchemaId == CreateAnnotationResponseSchema) return DecodeCreateAnnotationResponse(result).Value.Revision.Value;
        throw new ArgumentException("Unknown annotation response schema.");
    }

    private static void RequireResult(CapabilityResult result, string schema)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasSchemaId || result.SchemaId != schema) throw new ArgumentException("Unexpected response schema.");
        ValidateTree(result.Value);
    }

    private static StructuredValue Native(NativeContentRev value) => value is { HasValue: true, Value: > 0 }
        ? Unsigned(value.Value) : throw new ArgumentException("An explicit positive native revision is required.");
    private static StructuredValue CloudRevision(Revision value) => value is { HasValue: true, Value: > 0 }
        ? new() { Integer = value.Value } : throw new ArgumentException("An explicit positive cloud revision is required.");
    private static long Signed(StructuredValue value) => value.ValueCase == StructuredValue.ValueOneofCase.Integer
        ? value.Integer : throw new ArgumentException("An exact signed integer is required.");
    private static long PositiveSigned(StructuredValue value) => Signed(value) is var number && number > 0
        ? number : throw new ArgumentException("A committed revision must be positive.");
    private static uint UInt32(StructuredValue value) => Signed(value) is >= 0 and <= uint.MaxValue
        ? (uint)value.Integer : throw new ArgumentException("An exact uint32 integer is required.");
    private static bool Boolean(StructuredValue value) => value.ValueCase == StructuredValue.ValueOneofCase.Boolean
        ? value.Boolean : throw new ArgumentException("An explicit boolean is required.");
    private static StructuredValue Finite(double value) => double.IsFinite(value) ? new() { Number = value }
        : throw new ArgumentException("A declared binary64 field must be finite.");
    private static double Finite(StructuredValue value) => value.ValueCase == StructuredValue.ValueOneofCase.Number && double.IsFinite(value.Number)
        ? value.Number : throw new ArgumentException("A declared finite binary64 value is required.");
    private static StructuredValue Availability(ResourceAvailability value) => Enum.IsDefined(value)
        ? new() { Integer = (int)value } : throw new ArgumentException("Unknown resource availability.");
    private static ResourceAvailability Availability(StructuredValue value)
    {
        var number = Signed(value);
        if (number is < int.MinValue or > int.MaxValue || !Enum.IsDefined((ResourceAvailability)(int)number))
            throw new ArgumentException("Unknown resource availability.");
        return (ResourceAvailability)(int)number;
    }
    private static ByteString Binary(StructuredValue value)
    {
        var text = Text(value, 65536, allowEmpty: true);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException exception) { throw new ArgumentException("A canonical base64 byte field is required.", nameof(value), exception); }
        if (bytes.Length > 49152 || Convert.ToBase64String(bytes) != text)
            throw new ArgumentException("Byte fields require exact canonical padded base64 within their byte budget.");
        return ByteString.CopyFrom(bytes);
    }
    private static StructuredValue List(IEnumerable<StructuredValue> values)
    {
        var items = values.Take(201).ToArray();
        if (items.Length > 200) throw new ArgumentException("A response list exceeds its item budget.");
        var result = new StructuredValue { List = new() };
        result.List.Items.Add(items);
        return result;
    }
    private static IEnumerable<StructuredValue> List(StructuredValue value) => value.ValueCase == StructuredValue.ValueOneofCase.List && value.List.Items.Count <= 200
        ? value.List.Items : throw new ArgumentException("A bounded response list is required.");

    private static StructuredValue EncodeScopeSession(ScopeSession value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.SessionId is not null) fields.Add(("sessionId", IdValue(value.SessionId)));
        if (value.HasName) fields.Add(("name", new StructuredValue { Text = value.Name }));
        if (value.Revision is not null) fields.Add(("revision", Native(value.Revision)));
        if (value.Configuration is not null) fields.Add(("configuration", EncodeScopeConfiguration(value.Configuration)));
        fields.Add(("captures", List(value.Captures.Select(item => EncodeCaptureMetadata(item)))));
        return Record(fields);
    }

    private static ScopeSession DecodeScopeSession(StructuredValue value)
    {
        var fields = Record(value, "sessionId", "name", "revision", "configuration", "captures");
        var result = new ScopeSession();
        if (fields.TryGetValue("sessionId", out var fieldSessionId)) result.SessionId = IdValue(fieldSessionId);
        if (fields.TryGetValue("name", out var fieldName)) result.Name = Text(fieldName, 4096, allowEmpty: true);
        if (fields.TryGetValue("revision", out var fieldRevision)) result.Revision = new NativeContentRev { Value = PositiveUnsigned(fieldRevision) };
        if (fields.TryGetValue("configuration", out var fieldConfiguration)) result.Configuration = DecodeScopeConfiguration(fieldConfiguration);
        if (fields.TryGetValue("captures", out var fieldCaptures)) result.Captures.Add(List(fieldCaptures).Select(item => DecodeCaptureMetadata(item)));
        return result;
    }

    private static StructuredValue EncodeScopeConfiguration(ScopeConfiguration value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.ConfigurationId is not null) fields.Add(("configurationId", IdValue(value.ConfigurationId)));
        fields.Add(("channels", List(value.Channels.Select(item => EncodeChannelDefinition(item)))));
        if (value.HasParserProfile) fields.Add(("parserProfile", new StructuredValue { Text = value.ParserProfile }));
        if (value.Revision is not null) fields.Add(("revision", Native(value.Revision)));
        if (value.Framing is not null) fields.Add(("framing", EncodeFrameConfiguration(value.Framing)));
        if (value.Trigger is not null) fields.Add(("trigger", EncodeTriggerConfiguration(value.Trigger)));
        return Record(fields);
    }

    private static ScopeConfiguration DecodeScopeConfiguration(StructuredValue value)
    {
        var fields = Record(value, "configurationId", "channels", "parserProfile", "revision", "framing", "trigger");
        var result = new ScopeConfiguration();
        if (fields.TryGetValue("configurationId", out var fieldConfigurationId)) result.ConfigurationId = IdValue(fieldConfigurationId);
        if (fields.TryGetValue("channels", out var fieldChannels)) result.Channels.Add(List(fieldChannels).Select(item => DecodeChannelDefinition(item)));
        if (fields.TryGetValue("parserProfile", out var fieldParserProfile)) result.ParserProfile = Text(fieldParserProfile, 4096, allowEmpty: true);
        if (fields.TryGetValue("revision", out var fieldRevision)) result.Revision = new NativeContentRev { Value = PositiveUnsigned(fieldRevision) };
        if (fields.TryGetValue("framing", out var fieldFraming)) result.Framing = DecodeFrameConfiguration(fieldFraming);
        if (fields.TryGetValue("trigger", out var fieldTrigger)) result.Trigger = DecodeTriggerConfiguration(fieldTrigger);
        return result;
    }

    private static StructuredValue EncodeCaptureMetadata(CaptureMetadata value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.CaptureId is not null) fields.Add(("captureId", IdValue(value.CaptureId)));
        if (value.HasSampleCount) fields.Add(("sampleCount", Unsigned(value.SampleCount)));
        fields.Add(("channels", List(value.Channels.Select(item => EncodeChannelDefinition(item)))));
        if (value.HasContentHash) fields.Add(("contentHash", new StructuredValue { Text = value.ContentHash }));
        if (value.Resource is not null) fields.Add(("resource", EncodeResourceRef(value.Resource)));
        return Record(fields);
    }

    private static CaptureMetadata DecodeCaptureMetadata(StructuredValue value)
    {
        var fields = Record(value, "captureId", "sampleCount", "channels", "contentHash", "resource");
        var result = new CaptureMetadata();
        if (fields.TryGetValue("captureId", out var fieldCaptureId)) result.CaptureId = IdValue(fieldCaptureId);
        if (fields.TryGetValue("sampleCount", out var fieldSampleCount)) result.SampleCount = Unsigned(fieldSampleCount);
        if (fields.TryGetValue("channels", out var fieldChannels)) result.Channels.Add(List(fieldChannels).Select(item => DecodeChannelDefinition(item)));
        if (fields.TryGetValue("contentHash", out var fieldContentHash)) result.ContentHash = Text(fieldContentHash, 4096, allowEmpty: true);
        if (fields.TryGetValue("resource", out var fieldResource)) result.Resource = DecodeResourceRef(fieldResource);
        return result;
    }

    private static StructuredValue EncodeChannelDefinition(ChannelDefinition value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.ChannelId is not null) fields.Add(("channelId", IdValue(value.ChannelId)));
        if (value.HasName) fields.Add(("name", new StructuredValue { Text = value.Name }));
        if (value.HasUnit) fields.Add(("unit", new StructuredValue { Text = value.Unit }));
        if (value.HasSampleType) fields.Add(("sampleType", new StructuredValue { Text = value.SampleType }));
        if (value.Rate is not null) fields.Add(("rate", EncodeRational(value.Rate)));
        if (value.Calibration is not null) fields.Add(("calibration", EncodeCalibration(value.Calibration)));
        return Record(fields);
    }

    private static ChannelDefinition DecodeChannelDefinition(StructuredValue value)
    {
        var fields = Record(value, "channelId", "name", "unit", "sampleType", "rate", "calibration");
        var result = new ChannelDefinition();
        if (fields.TryGetValue("channelId", out var fieldChannelId)) result.ChannelId = IdValue(fieldChannelId);
        if (fields.TryGetValue("name", out var fieldName)) result.Name = Text(fieldName, 4096, allowEmpty: true);
        if (fields.TryGetValue("unit", out var fieldUnit)) result.Unit = Text(fieldUnit, 4096, allowEmpty: true);
        if (fields.TryGetValue("sampleType", out var fieldSampleType)) result.SampleType = Text(fieldSampleType, 4096, allowEmpty: true);
        if (fields.TryGetValue("rate", out var fieldRate)) result.Rate = DecodeRational(fieldRate);
        if (fields.TryGetValue("calibration", out var fieldCalibration)) result.Calibration = DecodeCalibration(fieldCalibration);
        return result;
    }

    private static StructuredValue EncodeCalibration(Calibration value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasScale) fields.Add(("scale", Finite(value.Scale)));
        if (value.HasOffset) fields.Add(("offset", Finite(value.Offset)));
        if (value.HasUnit) fields.Add(("unit", new StructuredValue { Text = value.Unit }));
        return Record(fields);
    }

    private static Calibration DecodeCalibration(StructuredValue value)
    {
        var fields = Record(value, "scale", "offset", "unit");
        var result = new Calibration();
        if (fields.TryGetValue("scale", out var fieldScale)) result.Scale = Finite(fieldScale);
        if (fields.TryGetValue("offset", out var fieldOffset)) result.Offset = Finite(fieldOffset);
        if (fields.TryGetValue("unit", out var fieldUnit)) result.Unit = Text(fieldUnit, 4096, allowEmpty: true);
        return result;
    }

    private static StructuredValue EncodeRational(Rational value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasNumerator) fields.Add(("numerator", new StructuredValue { Integer = value.Numerator }));
        if (value.HasDenominator) fields.Add(("denominator", Unsigned(value.Denominator)));
        return Record(fields);
    }

    private static Rational DecodeRational(StructuredValue value)
    {
        var fields = Record(value, "numerator", "denominator");
        var result = new Rational();
        if (fields.TryGetValue("numerator", out var fieldNumerator)) result.Numerator = Signed(fieldNumerator);
        if (fields.TryGetValue("denominator", out var fieldDenominator)) result.Denominator = Unsigned(fieldDenominator);
        return result;
    }

    private static StructuredValue EncodeFrameConfiguration(FrameConfiguration value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasKind) fields.Add(("kind", new StructuredValue { Text = value.Kind }));
        if (value.HasStart) fields.Add(("start", new StructuredValue { Text = Convert.ToBase64String(value.Start.Span) }));
        if (value.HasEnd) fields.Add(("end", new StructuredValue { Text = Convert.ToBase64String(value.End.Span) }));
        if (value.HasEscapeByte) fields.Add(("escapeByte", new StructuredValue { Integer = value.EscapeByte }));
        if (value.HasDelimiter) fields.Add(("delimiter", new StructuredValue { Integer = value.Delimiter }));
        if (value.HasHeader) fields.Add(("header", new StructuredValue { Boolean = value.Header }));
        if (value.HasFrameBytes) fields.Add(("frameBytes", new StructuredValue { Integer = value.FrameBytes }));
        if (value.HasByteOrder) fields.Add(("byteOrder", new StructuredValue { Text = value.ByteOrder }));
        fields.Add(("fields", List(value.Fields.Select(item => EncodeFrameField(item)))));
        if (value.Checksum is not null) fields.Add(("checksum", EncodeChecksumSpec(value.Checksum)));
        return Record(fields);
    }

    private static FrameConfiguration DecodeFrameConfiguration(StructuredValue value)
    {
        var fields = Record(value, "kind", "start", "end", "escapeByte", "delimiter", "header", "frameBytes", "byteOrder", "fields", "checksum");
        var result = new FrameConfiguration();
        if (fields.TryGetValue("kind", out var fieldKind)) result.Kind = Text(fieldKind, 4096, allowEmpty: true);
        if (fields.TryGetValue("start", out var fieldStart)) result.Start = Binary(fieldStart);
        if (fields.TryGetValue("end", out var fieldEnd)) result.End = Binary(fieldEnd);
        if (fields.TryGetValue("escapeByte", out var fieldEscapeByte)) result.EscapeByte = UInt32(fieldEscapeByte);
        if (fields.TryGetValue("delimiter", out var fieldDelimiter)) result.Delimiter = UInt32(fieldDelimiter);
        if (fields.TryGetValue("header", out var fieldHeader)) result.Header = Boolean(fieldHeader);
        if (fields.TryGetValue("frameBytes", out var fieldFrameBytes)) result.FrameBytes = UInt32(fieldFrameBytes);
        if (fields.TryGetValue("byteOrder", out var fieldByteOrder)) result.ByteOrder = Text(fieldByteOrder, 4096, allowEmpty: true);
        if (fields.TryGetValue("fields", out var fieldFields)) result.Fields.Add(List(fieldFields).Select(item => DecodeFrameField(item)));
        if (fields.TryGetValue("checksum", out var fieldChecksum)) result.Checksum = DecodeChecksumSpec(fieldChecksum);
        return result;
    }

    private static StructuredValue EncodeFrameField(FrameField value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.ChannelId is not null) fields.Add(("channelId", IdValue(value.ChannelId)));
        if (value.HasColumn) fields.Add(("column", new StructuredValue { Integer = value.Column }));
        fields.Add(("jsonPath", List(value.JsonPath.Select(item => new StructuredValue { Text = item }))));
        if (value.HasOffset) fields.Add(("offset", new StructuredValue { Integer = value.Offset }));
        if (value.HasScalarType) fields.Add(("scalarType", new StructuredValue { Text = value.ScalarType }));
        if (value.HasRequired) fields.Add(("required", new StructuredValue { Boolean = value.Required }));
        return Record(fields);
    }

    private static FrameField DecodeFrameField(StructuredValue value)
    {
        var fields = Record(value, "channelId", "column", "jsonPath", "offset", "scalarType", "required");
        var result = new FrameField();
        if (fields.TryGetValue("channelId", out var fieldChannelId)) result.ChannelId = IdValue(fieldChannelId);
        if (fields.TryGetValue("column", out var fieldColumn)) result.Column = UInt32(fieldColumn);
        if (fields.TryGetValue("jsonPath", out var fieldJsonPath)) result.JsonPath.Add(List(fieldJsonPath).Select(item => Text(item, 4096, allowEmpty: true)));
        if (fields.TryGetValue("offset", out var fieldOffset)) result.Offset = UInt32(fieldOffset);
        if (fields.TryGetValue("scalarType", out var fieldScalarType)) result.ScalarType = Text(fieldScalarType, 4096, allowEmpty: true);
        if (fields.TryGetValue("required", out var fieldRequired)) result.Required = Boolean(fieldRequired);
        return result;
    }

    private static StructuredValue EncodeChecksumSpec(ChecksumSpec value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasAlgorithm) fields.Add(("algorithm", new StructuredValue { Text = value.Algorithm }));
        if (value.Input is not null) fields.Add(("input", EncodeByteRange(value.Input)));
        if (value.HasOffset) fields.Add(("offset", new StructuredValue { Integer = value.Offset }));
        if (value.HasByteOrder) fields.Add(("byteOrder", new StructuredValue { Text = value.ByteOrder }));
        return Record(fields);
    }

    private static ChecksumSpec DecodeChecksumSpec(StructuredValue value)
    {
        var fields = Record(value, "algorithm", "input", "offset", "byteOrder");
        var result = new ChecksumSpec();
        if (fields.TryGetValue("algorithm", out var fieldAlgorithm)) result.Algorithm = Text(fieldAlgorithm, 4096, allowEmpty: true);
        if (fields.TryGetValue("input", out var fieldInput)) result.Input = DecodeByteRange(fieldInput);
        if (fields.TryGetValue("offset", out var fieldOffset)) result.Offset = UInt32(fieldOffset);
        if (fields.TryGetValue("byteOrder", out var fieldByteOrder)) result.ByteOrder = Text(fieldByteOrder, 4096, allowEmpty: true);
        return result;
    }

    private static StructuredValue EncodeByteRange(ByteRange value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasOffset) fields.Add(("offset", Unsigned(value.Offset)));
        if (value.HasLength) fields.Add(("length", Unsigned(value.Length)));
        return Record(fields);
    }

    private static ByteRange DecodeByteRange(StructuredValue value)
    {
        var fields = Record(value, "offset", "length");
        var result = new ByteRange();
        if (fields.TryGetValue("offset", out var fieldOffset)) result.Offset = Unsigned(fieldOffset);
        if (fields.TryGetValue("length", out var fieldLength)) result.Length = Unsigned(fieldLength);
        return result;
    }

    private static StructuredValue EncodeTriggerConfiguration(TriggerConfiguration value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasKind) fields.Add(("kind", new StructuredValue { Text = value.Kind }));
        if (value.ChannelId is not null) fields.Add(("channelId", IdValue(value.ChannelId)));
        if (value.HasThreshold) fields.Add(("threshold", Finite(value.Threshold)));
        if (value.HasDirection) fields.Add(("direction", new StructuredValue { Text = value.Direction }));
        if (value.HasHysteresis) fields.Add(("hysteresis", Finite(value.Hysteresis)));
        if (value.Holdoff is not null) fields.Add(("holdoff", EncodeScopeTime(value.Holdoff)));
        if (value.Pre is not null) fields.Add(("pre", EncodeScopeTime(value.Pre)));
        if (value.Post is not null) fields.Add(("post", EncodeScopeTime(value.Post)));
        if (value.HasRepeated) fields.Add(("repeated", new StructuredValue { Boolean = value.Repeated }));
        if (value.HasMaxOccurrences) fields.Add(("maxOccurrences", new StructuredValue { Integer = value.MaxOccurrences }));
        return Record(fields);
    }

    private static TriggerConfiguration DecodeTriggerConfiguration(StructuredValue value)
    {
        var fields = Record(value, "kind", "channelId", "threshold", "direction", "hysteresis", "holdoff", "pre", "post", "repeated", "maxOccurrences");
        var result = new TriggerConfiguration();
        if (fields.TryGetValue("kind", out var fieldKind)) result.Kind = Text(fieldKind, 4096, allowEmpty: true);
        if (fields.TryGetValue("channelId", out var fieldChannelId)) result.ChannelId = IdValue(fieldChannelId);
        if (fields.TryGetValue("threshold", out var fieldThreshold)) result.Threshold = Finite(fieldThreshold);
        if (fields.TryGetValue("direction", out var fieldDirection)) result.Direction = Text(fieldDirection, 4096, allowEmpty: true);
        if (fields.TryGetValue("hysteresis", out var fieldHysteresis)) result.Hysteresis = Finite(fieldHysteresis);
        if (fields.TryGetValue("holdoff", out var fieldHoldoff)) result.Holdoff = DecodeScopeTime(fieldHoldoff);
        if (fields.TryGetValue("pre", out var fieldPre)) result.Pre = DecodeScopeTime(fieldPre);
        if (fields.TryGetValue("post", out var fieldPost)) result.Post = DecodeScopeTime(fieldPost);
        if (fields.TryGetValue("repeated", out var fieldRepeated)) result.Repeated = Boolean(fieldRepeated);
        if (fields.TryGetValue("maxOccurrences", out var fieldMaxOccurrences)) result.MaxOccurrences = UInt32(fieldMaxOccurrences);
        return result;
    }

    private static StructuredValue EncodeScopeTime(ScopeTime value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.HasTicks) fields.Add(("ticks", new StructuredValue { Integer = value.Ticks }));
        if (value.Rate is not null) fields.Add(("rate", EncodeRational(value.Rate)));
        return Record(fields);
    }

    private static ScopeTime DecodeScopeTime(StructuredValue value)
    {
        var fields = Record(value, "ticks", "rate");
        var result = new ScopeTime();
        if (fields.TryGetValue("ticks", out var fieldTicks)) result.Ticks = Signed(fieldTicks);
        if (fields.TryGetValue("rate", out var fieldRate)) result.Rate = DecodeRational(fieldRate);
        return result;
    }

    private static StructuredValue EncodeResourceRef(ResourceRef value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.RealmId is not null) fields.Add(("realmId", IdValue(value.RealmId)));
        if (value.WorkspaceId is not null) fields.Add(("workspaceId", IdValue(value.WorkspaceId)));
        if (value.HasOwnerAppId) fields.Add(("ownerAppId", new StructuredValue { Text = value.OwnerAppId }));
        if (value.HasResourceKind) fields.Add(("resourceKind", new StructuredValue { Text = value.ResourceKind }));
        if (value.ResourceId is not null) fields.Add(("resourceId", IdValue(value.ResourceId)));
        if (value.HasDisplayHint) fields.Add(("displayHint", new StructuredValue { Text = value.DisplayHint }));
        if (value.HasAvailability) fields.Add(("availability", Availability(value.Availability)));
        if (value.HoldingDeviceId is not null) fields.Add(("holdingDeviceId", IdValue(value.HoldingDeviceId)));
        return Record(fields);
    }

    private static ResourceRef DecodeResourceRef(StructuredValue value)
    {
        var fields = Record(value, "realmId", "workspaceId", "ownerAppId", "resourceKind", "resourceId", "displayHint", "availability", "holdingDeviceId");
        var result = new ResourceRef();
        if (fields.TryGetValue("realmId", out var fieldRealmId)) result.RealmId = IdValue(fieldRealmId);
        if (fields.TryGetValue("workspaceId", out var fieldWorkspaceId)) result.WorkspaceId = IdValue(fieldWorkspaceId);
        if (fields.TryGetValue("ownerAppId", out var fieldOwnerAppId)) result.OwnerAppId = Text(fieldOwnerAppId, 4096, allowEmpty: true);
        if (fields.TryGetValue("resourceKind", out var fieldResourceKind)) result.ResourceKind = Text(fieldResourceKind, 4096, allowEmpty: true);
        if (fields.TryGetValue("resourceId", out var fieldResourceId)) result.ResourceId = IdValue(fieldResourceId);
        if (fields.TryGetValue("displayHint", out var fieldDisplayHint)) result.DisplayHint = Text(fieldDisplayHint, 4096, allowEmpty: true);
        if (fields.TryGetValue("availability", out var fieldAvailability)) result.Availability = Availability(fieldAvailability);
        if (fields.TryGetValue("holdingDeviceId", out var fieldHoldingDeviceId)) result.HoldingDeviceId = IdValue(fieldHoldingDeviceId);
        return result;
    }

    private static StructuredValue EncodeResponseMeta(ResponseMeta value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.ResultRev is not null) fields.Add(("resultRev", CloudRevision(value.ResultRev)));
        if (value.HasEntitlementVersion) fields.Add(("entitlementVersion", new StructuredValue { Integer = value.EntitlementVersion }));
        fields.Add(("warnings", List(value.Warnings.Select(item => new StructuredValue { Text = item }))));
        if (value.CorrelationId is not null) fields.Add(("correlationId", IdValue(value.CorrelationId)));
        if (value.HasRecoveryGeneration) fields.Add(("recoveryGeneration", Unsigned(value.RecoveryGeneration)));
        return Record(fields);
    }

    private static ResponseMeta DecodeResponseMeta(StructuredValue value)
    {
        var fields = Record(value, "resultRev", "entitlementVersion", "warnings", "correlationId", "recoveryGeneration");
        var result = new ResponseMeta();
        if (fields.TryGetValue("resultRev", out var fieldResultRev)) result.ResultRev = new Revision { Value = PositiveSigned(fieldResultRev) };
        if (fields.TryGetValue("entitlementVersion", out var fieldEntitlementVersion)) result.EntitlementVersion = Signed(fieldEntitlementVersion);
        if (fields.TryGetValue("warnings", out var fieldWarnings)) result.Warnings.Add(List(fieldWarnings).Select(item => Text(item, 4096, allowEmpty: true)));
        if (fields.TryGetValue("correlationId", out var fieldCorrelationId)) result.CorrelationId = IdValue(fieldCorrelationId);
        if (fields.TryGetValue("recoveryGeneration", out var fieldRecoveryGeneration)) result.RecoveryGeneration = Unsigned(fieldRecoveryGeneration);
        return result;
    }

}
