// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.LocalRpc.Scope.V1;
using ArcForges.Contracts.PublicApi.V1;

namespace ArcForges.ArcScope.Core.Application;

/// <summary>Explicit operation-local gateway mapping, without reflection or a generic protobuf JSON profile.</summary>
internal static partial class AnnotationOperationCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const string GetSessionSchema = "arcforges.local.scope.v1.ScopeOperationsServiceGetSessionRequest";
    internal const string CreateAnnotationSchema = "arcforges.local.scope.v1.ScopeOperationsServiceCreateAnnotationRequest";

    internal static ScopeOperationsServiceGetSessionRequest DecodeGetSession(CapabilityArguments arguments)
    {
        RequireSchema(arguments, GetSessionSchema);
        var fields = Record(arguments.Value, "meta", "sessionId");
        var request = new ScopeOperationsServiceGetSessionRequest
        {
            SessionId = IdValue(Required(fields, "sessionId")),
            Meta = fields.TryGetValue("meta", out var metadata) ? RequestMetadata(metadata) : null,
        };
        if (!BuildArguments(request).Equals(arguments))
            throw new ArgumentException("The arguments contain fields outside the closed operation profile.", nameof(arguments));
        return request;
    }

    internal static ScopeOperationsServiceCreateAnnotationRequest DecodeCreateAnnotation(CapabilityArguments arguments)
    {
        RequireSchema(arguments, CreateAnnotationSchema);
        var fields = Record(arguments.Value, "annotationId", "meta", "range", "sessionId", "text");
        var range = Record(Required(fields, "range"), "count", "from");
        var first = Unsigned(Required(range, "from"));
        var count = Unsigned(Required(range, "count"));
        if (ulong.MaxValue - first < count) throw new ArgumentException("The half-open sample range overflows.");
        var request = new ScopeOperationsServiceCreateAnnotationRequest
        {
            AnnotationId = IdValue(Required(fields, "annotationId")),
            SessionId = IdValue(Required(fields, "sessionId")),
            Range = new() { From = first, Count = count },
            Text = Text(Required(fields, "text"), 4096, allowEmpty: false),
            Meta = fields.TryGetValue("meta", out var metadata) ? RequestMetadata(metadata) : null,
        };
        if (!BuildArguments(request).Equals(arguments))
            throw new ArgumentException("The arguments contain fields outside the closed operation profile.", nameof(arguments));
        return request;
    }

    internal static CapabilityArguments Encode(ScopeOperationsServiceGetSessionRequest request)
    {
        var result = BuildArguments(request);
        if (!DecodeGetSession(result).Equals(request))
            throw new ArgumentException("The request contains fields outside the closed operation profile.", nameof(request));
        return result;
    }

    private static CapabilityArguments BuildArguments(ScopeOperationsServiceGetSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new List<(string Key, StructuredValue Value)> { ("sessionId", IdValue(request.SessionId)) };
        if (request.Meta is not null) fields.Add(("meta", RequestMetadata(request.Meta)));
        return new() { SchemaId = GetSessionSchema, Value = Record(fields) };
    }

    internal static CapabilityArguments Encode(ScopeOperationsServiceCreateAnnotationRequest request)
    {
        var result = BuildArguments(request);
        if (!DecodeCreateAnnotation(result).Equals(request))
            throw new ArgumentException("The request contains fields outside the closed operation profile.", nameof(request));
        return result;
    }

    private static CapabilityArguments BuildArguments(ScopeOperationsServiceCreateAnnotationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Range is not { HasFrom: true, HasCount: true } || !request.HasText)
            throw new ArgumentException("Annotation range and text must be explicit.", nameof(request));
        var fields = new List<(string Key, StructuredValue Value)>
        {
            ("annotationId", IdValue(request.AnnotationId)),
            ("range", Record([("count", Unsigned(request.Range.Count)), ("from", Unsigned(request.Range.From))])),
            ("sessionId", IdValue(request.SessionId)),
            ("text", new() { Text = request.Text }),
        };
        if (request.Meta is not null) fields.Add(("meta", RequestMetadata(request.Meta)));
        return new() { SchemaId = CreateAnnotationSchema, Value = Record(fields) };
    }

    private static RequestMeta RequestMetadata(StructuredValue value)
    {
        var fields = Record(value, "applicationScope", "commandId", "correlationId", "expectedNative", "expectedRev", "recoveryGeneration", "workspaceId");
        var result = new RequestMeta();
        if (fields.TryGetValue("commandId", out var command)) result.CommandId = IdValue(command);
        if (fields.TryGetValue("correlationId", out var correlation)) result.CorrelationId = IdValue(correlation);
        if (fields.TryGetValue("workspaceId", out var workspace)) result.WorkspaceId = IdValue(workspace);
        if (fields.TryGetValue("expectedNative", out var native)) result.ExpectedNative = new() { Value = PositiveUnsigned(native) };
        if (fields.TryGetValue("expectedRev", out var cloud)) result.ExpectedRev = new() { Value = PositiveSigned(cloud) };
        if (fields.TryGetValue("recoveryGeneration", out var recovery)) result.RecoveryGeneration = Unsigned(recovery);
        if (fields.TryGetValue("applicationScope", out var scope))
        {
            var application = Record(scope, "installationId", "productId");
            result.ApplicationScope = new()
            {
                InstallationId = IdValue(Required(application, "installationId")),
                ProductId = Text(Required(application, "productId"), 128, allowEmpty: false),
            };
        }
        return result;
    }

    private static StructuredValue RequestMetadata(RequestMeta value)
    {
        var fields = new List<(string Key, StructuredValue Value)>();
        if (value.CommandId is not null) fields.Add(("commandId", IdValue(value.CommandId)));
        if (value.CorrelationId is not null) fields.Add(("correlationId", IdValue(value.CorrelationId)));
        if (value.WorkspaceId is not null) fields.Add(("workspaceId", IdValue(value.WorkspaceId)));
        if (value.ExpectedNative is not null)
        {
            if (!value.ExpectedNative.HasValue || value.ExpectedNative.Value == 0) throw new ArgumentException("An expected native revision must be explicitly positive.");
            fields.Add(("expectedNative", Unsigned(value.ExpectedNative.Value)));
        }
        if (value.ExpectedRev is not null)
        {
            if (!value.ExpectedRev.HasValue || value.ExpectedRev.Value <= 0) throw new ArgumentException("An expected cloud revision must be explicitly positive.");
            fields.Add(("expectedRev", CloudRevision(value.ExpectedRev)));
        }
        if (value.HasRecoveryGeneration) fields.Add(("recoveryGeneration", Unsigned(value.RecoveryGeneration)));
        if (value.ApplicationScope is not null)
        {
            if (!value.ApplicationScope.HasProductId) throw new ArgumentException("Application product identity must be explicit.");
            fields.Add(("applicationScope", Record([
                ("installationId", IdValue(value.ApplicationScope.InstallationId)),
                ("productId", new StructuredValue { Text = value.ApplicationScope.ProductId }),
            ])));
        }
        var result = Record(fields);
        _ = RequestMetadata(result);
        return result;
    }

    private static IReadOnlyDictionary<string, StructuredValue> Record(StructuredValue? value, params string[] allowed)
    {
        if (value?.ValueCase != StructuredValue.ValueOneofCase.Record || value.Record.Entries.Count > 200)
            throw new ArgumentException("A bounded record is required.");
        var fields = new Dictionary<string, StructuredValue>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var entry in value.Record.Entries)
        {
            if (!entry.HasName || string.IsNullOrEmpty(entry.Name) || entry.Name.Any(character => character is < '!' or > '~') ||
                previous is not null && StringComparer.Ordinal.Compare(previous, entry.Name) >= 0 ||
                !allowed.Contains(entry.Name, StringComparer.Ordinal) || entry.Value is null ||
                entry.Value.ValueCase == StructuredValue.ValueOneofCase.None)
                throw new ArgumentException("Record keys must be known, unique, ASCII and sorted ordinal.");
            fields.Add(entry.Name, entry.Value.Clone());
            previous = entry.Name;
        }
        return fields;
    }

    private static StructuredValue Record(IEnumerable<(string Key, StructuredValue Value)> fields)
    {
        var value = new StructuredValue { Record = new() };
        foreach (var field in fields.OrderBy(field => field.Key, StringComparer.Ordinal))
            value.Record.Entries.Add(new ValueEntry { Name = field.Key, Value = field.Value.Clone() });
        return value;
    }

    private static StructuredValue Required(IReadOnlyDictionary<string, StructuredValue> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing required operation field: " + name);

    private static string Text(StructuredValue value, int maximumBytes, bool allowEmpty)
    {
        if (value.ValueCase != StructuredValue.ValueOneofCase.Text || !allowEmpty && string.IsNullOrWhiteSpace(value.Text) ||
            value.Text.Length > maximumBytes || StrictUtf8.GetByteCount(value.Text) > maximumBytes)
            throw new ArgumentException("An explicit bounded text value is required.");
        return value.Text;
    }

    private static ulong Unsigned(StructuredValue value)
    {
        var text = Text(value, 20, allowEmpty: false);
        if (text.Length > 1 && text[0] == '0' || text.Any(character => character is < '0' or > '9') ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("An exact canonical unsigned decimal text value is required.");
        return number;
    }
    private static ulong PositiveUnsigned(StructuredValue value) => Unsigned(value) is var number && number != 0
        ? number : throw new ArgumentException("A committed revision must be positive.");
    private static StructuredValue Unsigned(ulong value) => new() { Text = value.ToString(CultureInfo.InvariantCulture) };
    private static Id IdValue(StructuredValue value)
    {
        var text = Text(value, 36, allowEmpty: false);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw new ArgumentException("An exact nonempty canonical GUID-D value is required.");
        return UuidBoundary.ToWire(id);
    }
    private static StructuredValue IdValue(Id? value) => new() { Text = UuidBoundary.FromWire(value!).ToString("D") };
    private static void RequireSchema(CapabilityArguments arguments, string expected)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.HasSchemaId || arguments.SchemaId != expected) throw new ArgumentException("Unexpected operation schema.");
        ValidateTree(arguments.Value);
    }

    internal static void ValidateTree(StructuredValue? root)
    {
        if (root is null) throw new ArgumentException("A structured operation payload is required.");
        var pending = new Stack<(StructuredValue Value, int Depth)>();
        pending.Push((root, 1));
        var textBytes = 0;
        var nodeCount = 0;
        while (pending.TryPop(out var node))
        {
            if (node.Depth > 16 || ++nodeCount > 8192)
                throw new ArgumentException("The operation payload exceeds its depth or node budget.");
            if (node.Value.ValueCase == StructuredValue.ValueOneofCase.None)
                throw new ArgumentException("An explicit structured value is required.");
            if (node.Value.ValueCase == StructuredValue.ValueOneofCase.Text)
            {
                textBytes = checked(textBytes + StrictUtf8.GetByteCount(node.Value.Text));
                if (textBytes > 65536) throw new ArgumentException("Operation text exceeds its byte budget.");
            }
            if (node.Value.ValueCase == StructuredValue.ValueOneofCase.List)
            {
                if (node.Value.List.Items.Count > 200) throw new ArgumentException("Operation list exceeds its item budget.");
                foreach (var child in node.Value.List.Items) pending.Push((child, node.Depth + 1));
            }
            if (node.Value.ValueCase == StructuredValue.ValueOneofCase.Record)
            {
                if (node.Value.Record.Entries.Count > 200) throw new ArgumentException("Operation record exceeds its field budget.");
                string? previous = null;
                foreach (var entry in node.Value.Record.Entries)
                {
                    if (!entry.HasName || string.IsNullOrEmpty(entry.Name) || entry.Name.Length > 128 ||
                        entry.Name.Any(character => character is < '!' or > '~') || entry.Value is null ||
                        previous is not null && StringComparer.Ordinal.Compare(previous, entry.Name) >= 0)
                        throw new ArgumentException("Operation record keys must be sorted, unique ASCII text.");
                    previous = entry.Name;
                    textBytes = checked(textBytes + entry.Name.Length);
                    if (textBytes > 65536) throw new ArgumentException("Operation record keys exceed their byte budget.");
                    pending.Push((entry.Value, node.Depth + 1));
                }
            }
        }
    }
}
