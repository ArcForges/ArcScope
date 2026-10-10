// SPDX-License-Identifier: AGPL-3.0-only
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using ArcForges.ArcScope.Application.Annotations;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Foundation.Execution;
using Instant = ArcForges.Foundation.Instant;

namespace ArcForges.ArcScope.Infrastructure.Annotations;

/// <summary>
/// The versioned canonical serialization of one annotation (profile <c>arcscope.annotation.v1</c>) and its content-origin
/// record. The payload is compact UTF-8 JSON with ASCII property names in ordinal order, exact integers as decimal strings,
/// lowercase UUIDs and the text preserved exactly; it carries the complete append-only history. Decoding is strict: the
/// bytes must re-encode to themselves, so a reordered, duplicated, extra, missing or non-canonical field is refused.
/// </summary>
internal static class AnnotationPayload
{
    internal const string Profile = "arcscope.annotation.v1";
    internal const string OriginProfile = "arcforges.content-origin.v1";

    /// <summary>The largest payload decode accepts: the 256 KiB text bound with worst-case JSON escaping plus history metadata.</summary>
    internal const int MaxPayloadBytes = 4 * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    internal static byte[] Encode(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("annotation", Uuid(annotation.Id.Value));
            writer.WriteStartArray("entries");
            foreach (var entry in annotation.Entries)
            {
                writer.WriteStartObject();
                writer.WriteStartObject("actor");
                if (entry.Actor.Invocation is { } invocation) writer.WriteString("invocation", Uuid(invocation.Value));
                else writer.WriteNull("invocation");
                writer.WriteString("kind", entry.Actor.Kind == AnnotationActorKind.Human ? "human" : "productHandler");
                writer.WriteString("user", Uuid(entry.Actor.User.Value));
                writer.WriteEndObject();
                writer.WriteString("command", Uuid(entry.Command.Value));
                writer.WriteStartObject("committedAt");
                writer.WriteString("nanoseconds", entry.CommittedAt.Nanoseconds.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("unixSeconds", entry.CommittedAt.UnixSeconds.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                writer.WriteString("contentKind", AnnotationContentKinds.Key(entry.ContentKind));
                writer.WriteString("fingerprint", entry.CommandFingerprint);
                writer.WriteString("revision", entry.Revision.Value.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("text", entry.Text.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("profile", Profile);
            writer.WriteStartObject("range");
            writer.WriteString("count", annotation.Range.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("from", annotation.Range.From.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteString("session", Uuid(annotation.Session.Value));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Decodes a stored payload, refusing anything that is not exactly the canonical encoding of a valid annotation.</summary>
    internal static Annotation Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaxPayloadBytes) throw new InvalidDataException("A stored annotation payload is empty or oversized.");
        Annotation annotation;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), ReaderOptions);
            var root = Object(document.RootElement);
            if (!string.Equals(Text(root, "profile"), Profile, StringComparison.Ordinal))
                throw new InvalidDataException("A stored annotation payload has an unsupported profile.");
            var range = Object(Property(root, "range"));
            var entries = new List<AnnotationEntry>();
            var history = Property(root, "entries");
            if (history.ValueKind != JsonValueKind.Array) throw new InvalidDataException("A stored annotation history is not an array.");
            foreach (var item in history.EnumerateArray()) entries.Add(DecodeEntry(Object(item)));
            annotation = new Annotation(new AnnotationId(ReadUuid(root, "annotation")), new SessionTargetId(ReadUuid(root, "session")),
                new SampleRange(ReadUnsigned(range, "from"), ReadUnsigned(range, "count")), entries);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException
            or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("A stored annotation payload is not a valid annotation.", exception);
        }

        if (!payload.SequenceEqual(Encode(annotation)))
            throw new InvalidDataException("A stored annotation payload is not in its canonical encoding.");
        return annotation;
    }

    /// <summary>
    /// The content-origin record of one payload version: the kind union over the whole history, the exact payload hash, the
    /// producer of the newest entry, its commit instant and the previous version's origin as the only parent.
    /// </summary>
    internal static ContentOrigin CreateOrigin(Annotation next, ReadOnlySpan<byte> payload, ContentOrigin? previous)
    {
        ArgumentNullException.ThrowIfNull(next);
        var entry = next.Entries[^1];
        var origin = new ContentOrigin
        {
            Profile = OriginProfile,
            OriginId = new ContentOriginId(Guid.NewGuid()).ToWire(),
            ContentUnitId = new ContentUnitId(next.Id.Value).ToWire(),
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
            ProducerKind = ProducerKind(entry),
            CreatedAt = WireValues.ToWire(entry.CommittedAt),
            OmittedParentCount = 0,
        };
        origin.Kinds.AddRange(next.OriginKinds.Select(AnnotationContentKinds.Key));
        if (previous is not null) origin.ParentOriginIds.Add(previous.OriginId.Clone());
        return origin;
    }

    /// <summary>
    /// Checks a stored origin against its payload. A supported origin must name this annotation as its content unit, the
    /// exact payload hash and the complete kind union; an unsupported origin profile leaves the annotation readable but
    /// read-only (returns false), because a writer that cannot preserve it must not replace it.
    /// </summary>
    internal static bool VerifyOrigin(Annotation annotation, ReadOnlySpan<byte> payload, ContentOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!string.Equals(origin.Profile, OriginProfile, StringComparison.Ordinal)) return false;
        try
        {
            if (ContentUnitId.FromWire(origin.ContentUnitId).Value != annotation.Id.Value)
                throw new InvalidDataException("A stored annotation origin names another content unit.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A stored annotation origin has an invalid content unit.", exception);
        }

        if (!string.Equals(origin.PayloadSha256, Convert.ToHexStringLower(SHA256.HashData(payload)), StringComparison.Ordinal))
            throw new InvalidDataException("A stored annotation origin does not identify its payload bytes.");
        if (!origin.Kinds.SequenceEqual(annotation.OriginKinds.Select(AnnotationContentKinds.Key), StringComparer.Ordinal))
            throw new InvalidDataException("A stored annotation origin does not carry the history's kind union.");
        return true;
    }

    /// <summary>
    /// Who produced a payload version: the person (<c>human</c>), or for ArcScope's own product handler the model whose output
    /// it recorded (<c>model</c>, for AI-generated or AI-manipulated text) or the handler itself (<c>deterministic</c>).
    /// The producer never replaces the inherited kinds.
    /// </summary>
    internal static string ProducerKind(AnnotationEntry entry) => entry.Actor.Kind switch
    {
        AnnotationActorKind.Human => "human",
        AnnotationActorKind.ProductHandler when entry.ContentKind is AnnotationContentKind.AiGenerated or AnnotationContentKind.AiManipulated => "model",
        AnnotationActorKind.ProductHandler => "deterministic",
        _ => throw new ArgumentOutOfRangeException(nameof(entry), "A known actor is required."),
    };

    private static AnnotationEntry DecodeEntry(JsonElement entry)
    {
        var actorElement = Object(Property(entry, "actor"));
        var user = new UserId(ReadUuid(actorElement, "user"));
        var invocation = Property(actorElement, "invocation");
        AnnotationActor actor = Text(actorElement, "kind") switch
        {
            "human" when invocation.ValueKind == JsonValueKind.Null => AnnotationActor.Human(user),
            "productHandler" when invocation.ValueKind == JsonValueKind.String =>
                AnnotationActor.ProductHandler(user, new InvocationId(ParseUuid(invocation.GetString()))),
            _ => throw new InvalidDataException("A stored annotation actor is not a known actor."),
        };
        var committed = Object(Property(entry, "committedAt"));
        var committedAt = new Instant(long.Parse(Text(committed, "unixSeconds"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            uint.Parse(Text(committed, "nanoseconds"), NumberStyles.None, CultureInfo.InvariantCulture));
        var kind = Text(entry, "contentKind") switch
        {
            "aiGenerated" => AnnotationContentKind.AiGenerated,
            "aiManipulated" => AnnotationContentKind.AiManipulated,
            "nonAi" => AnnotationContentKind.NonAi,
            "unknown" => AnnotationContentKind.Unknown,
            _ => throw new InvalidDataException("A stored annotation entry has an unknown content kind."),
        };
        return new AnnotationEntry(new NativeRevision(ReadUnsigned(entry, "revision")), AnnotationText.Create(Text(entry, "text")), actor,
            kind, committedAt, new CommandId(ReadUuid(entry, "command")), Text(entry, "fingerprint"));
    }

    private static JsonElement Object(JsonElement element) => element.ValueKind == JsonValueKind.Object
        ? element
        : throw new InvalidDataException("A stored annotation payload field is not an object.");

    private static JsonElement Property(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value
        : throw new InvalidDataException("A stored annotation payload lacks the field " + name + ".");

    private static string Text(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException("A stored annotation payload field is not text: " + name + ".");
    }

    private static ulong ReadUnsigned(JsonElement element, string name) =>
        ulong.Parse(Text(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

    private static Guid ReadUuid(JsonElement element, string name) => ParseUuid(Text(element, name));

    private static Guid ParseUuid(string? text) => text is not null && Guid.TryParseExact(text, "D", out var value)
        ? value
        : throw new InvalidDataException("A stored annotation identity is not a UUID.");

    private static string Uuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
}
