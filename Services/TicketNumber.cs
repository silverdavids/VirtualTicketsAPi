using System.Security.Cryptography;

namespace VirtualTickets.Api.Services;

public static class TicketNumber
{
    private const string LegacyAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int NumericLength = 12;

    public static string Generate()
    {
        Span<char> result = stackalloc char[NumericLength];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(result);
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Concat(value.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
    }

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return (normalized is { Length: NumericLength }
            && normalized.All(char.IsAsciiDigit))
            || IsValidLegacy(normalized);
    }

    private static bool IsValidLegacy(string? value) =>
        value is { Length: 19 }
        && value.StartsWith("VT-", StringComparison.Ordinal)
        && value.AsSpan(3).IndexOfAnyExcept(LegacyAlphabet) < 0;
}
