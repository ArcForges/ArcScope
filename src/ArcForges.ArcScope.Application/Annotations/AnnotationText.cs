// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// Authored annotation text, preserved exactly as written (no normalization). It is well-formed Unicode, not blank, holds
/// no NUL and fits the Contracts <c>Text</c> bound of 256 KiB of UTF-8. Annotation text is user content: it has no logging
/// representation, so <see cref="ToString"/> never discloses it.
/// </summary>
public sealed class AnnotationText : IEquatable<AnnotationText>
{
    /// <summary>The Contracts <c>Text</c> alias bound: at most 256 KiB of UTF-8.</summary>
    public const int MaxUtf8Bytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private AnnotationText(string value, int utf8Bytes)
    {
        Value = value;
        Utf8ByteCount = utf8Bytes;
    }

    public string Value { get; }

    public int Utf8ByteCount { get; }

    public static AnnotationText Create(string value) =>
        TryCreate(value, out var text) ? text : throw new ArgumentException("Annotation text must be non-blank, well-formed Unicode without NUL, within 256 KiB of UTF-8.", nameof(value));

    public static bool TryCreate(string? value, [NotNullWhen(true)] out AnnotationText? text)
    {
        text = null;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal)) return false;
        // A cheap refusal first: every UTF-16 unit encodes to at least one UTF-8 byte.
        if (value.Length > MaxUtf8Bytes) return false;
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        if (bytes > MaxUtf8Bytes) return false;
        text = new AnnotationText(value, bytes);
        return true;
    }

    public bool Equals(AnnotationText? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as AnnotationText);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>A redacted description: annotation content never reaches diagnostics.</summary>
    public override string ToString() => $"AnnotationText({Utf8ByteCount} UTF-8 bytes)";
}
