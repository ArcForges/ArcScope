// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;

namespace ArcForges.ArcScope.Infrastructure.Storage;

/// <summary>
/// The exact text forms of ArcScope's product columns. An unsigned 64-bit value is 20 decimal digits, zero padded, so text
/// order equals numeric order for the whole range; an identity is canonical lowercase UUID text. Reading anything else is
/// stored state that cannot be trusted.
/// </summary>
internal static class StorageText
{
    internal const int UnsignedDigits = 20;

    internal static string Unsigned(ulong value) => value.ToString("D20", CultureInfo.InvariantCulture);

    internal static ulong ReadUnsigned(string? text)
    {
        if (text is not { Length: UnsignedDigits } || !text.All(char.IsAsciiDigit)
            || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
        {
            throw new InvalidDataException("A stored unsigned value is not 20 decimal digits.");
        }

        return value;
    }

    internal static string Identity(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    internal static Guid ReadIdentity(string? text)
    {
        if (text is null || !Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty
            || !string.Equals(Identity(value), text, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A stored identity is not a canonical non-empty lowercase UUID.");
        }

        return value;
    }
}
