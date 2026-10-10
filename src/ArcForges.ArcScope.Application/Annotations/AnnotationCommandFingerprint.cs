// SPDX-License-Identifier: AGPL-3.0-only
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// The canonical semantic fingerprint of an annotation write command: SHA-256 (lowercase hex) of a versioned UTF-8 JSON
/// object with ASCII property names in ordinal order, exact integers as decimal strings, lowercase UUIDs and the text
/// preserved exactly. It covers the operation and every semantic field, including the actor; it excludes the command
/// identity itself, timestamps and transport metadata. The same command content always yields the same fingerprint.
/// </summary>
public static class AnnotationCommandFingerprint
{
    public const string Profile = "arcscope.annotation-command.v1";

    public static string ForCreate(CreateAnnotation command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Hash(writer =>
        {
            WriteActor(writer, command.Actor);
            writer.WriteString("annotation", Uuid(command.Annotation.Value));
            writer.WriteString("contentKind", AnnotationContentKinds.Key(command.ContentKind));
            writer.WriteString("expectedRevision", "0");
            writer.WriteString("operation", AnnotationOperations.Create);
            writer.WriteString("profile", Profile);
            writer.WriteStartObject("range");
            writer.WriteString("count", Integer(command.Range.Count));
            writer.WriteString("from", Integer(command.Range.From));
            writer.WriteEndObject();
            writer.WriteString("session", Uuid(command.Session.Value));
            writer.WriteString("text", command.Text.Value);
        });
    }

    public static string ForAppend(AppendAnnotation command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Hash(writer =>
        {
            WriteActor(writer, command.Actor);
            writer.WriteString("annotation", Uuid(command.Annotation.Value));
            writer.WriteString("contentKind", AnnotationContentKinds.Key(command.ContentKind));
            writer.WriteString("expectedRevision", Integer(command.ExpectedRevision.Value));
            writer.WriteString("operation", AnnotationOperations.Append);
            writer.WriteString("profile", Profile);
            writer.WriteString("text", command.Text.Value);
        });
    }

    /// <summary>True for exactly 64 lowercase hexadecimal characters.</summary>
    public static bool IsWellFormed(string? fingerprint) =>
        fingerprint is { Length: 64 } && fingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteActor(Utf8JsonWriter writer, AnnotationActor actor)
    {
        writer.WriteStartObject("actor");
        if (actor.Invocation is { } invocation) writer.WriteString("invocation", Uuid(invocation.Value));
        else writer.WriteNull("invocation");
        writer.WriteString("kind", actor.Kind == AnnotationActorKind.Human ? "human" : "productHandler");
        writer.WriteString("user", Uuid(actor.User.Value));
        writer.WriteEndObject();
    }

    private static string Hash(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static string Uuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Integer(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
