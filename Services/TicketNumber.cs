using System.Security.Cryptography;

namespace VirtualTickets.Api.Services;

public static class TicketNumber
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int RandomLength = 16;

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[RandomLength];
        RandomNumberGenerator.Fill(bytes);
        Span<char> result = stackalloc char[3 + RandomLength];
        result[0] = 'V';
        result[1] = 'T';
        result[2] = '-';

        for (var index = 0; index < bytes.Length; index++)
        {
            result[index + 3] = Alphabet[bytes[index] % Alphabet.Length];
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
        return normalized is { Length: 19 }
            && normalized.StartsWith("VT-", StringComparison.Ordinal)
            && normalized.AsSpan(3).IndexOfAnyExcept(Alphabet) < 0;
    }
}
