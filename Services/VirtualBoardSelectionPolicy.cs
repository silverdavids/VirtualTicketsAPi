using System.Globalization;
using System.Text.RegularExpressions;
using VirtualTickets.Api.Contracts;

namespace VirtualTickets.Api.Services;

public static class VirtualBoardSelectionPolicy
{
    public static bool OddsMatch(decimal submittedOdd, decimal authoritativeOdd) =>
        submittedOdd == authoritativeOdd;

    public static bool IsSameSelection(
        TicketSelectionRequest submitted,
        string? candidateMarket,
        string? candidateOption,
        string? candidateLine)
    {
        var submittedMarket = NormalizeMarket(submitted.Market);
        var snapshotMarket = NormalizeMarket(candidateMarket);
        if (submittedMarket is null || snapshotMarket is null || submittedMarket != snapshotMarket)
        {
            return false;
        }

        var submittedOption = NormalizeOption(submitted.Option, submitted.Line);
        var snapshotOption = NormalizeOption(candidateOption, TryParseLine(candidateLine));
        if (submittedOption is null || snapshotOption is null || submittedOption != snapshotOption)
        {
            return false;
        }

        var submittedLine = submitted.Line;
        var snapshotLine = TryParseLine(candidateLine) ?? ExtractLineFromLineOption(candidateOption);
        if (submittedLine.HasValue || snapshotLine.HasValue)
        {
            return submittedLine.HasValue
                && snapshotLine.HasValue
                && submittedLine.Value == snapshotLine.Value;
        }

        return true;
    }

    public static string? NormalizeMarket(string? market)
    {
        var value = NormalizeToken(market);
        return value switch
        {
            "O/U" or "OVERUNDER" or "OVER_UNDER" or "TOTAL" or "TOTALS" => "OU",
            _ => value
        };
    }

    public static string? NormalizeOption(string? option, decimal? line)
    {
        var value = NormalizeToken(option);
        if (value is null)
        {
            return null;
        }

        var extractedLine = ExtractLineFromLineOption(option) ?? line;
        if (IsOver(value))
        {
            return extractedLine.HasValue
                ? $"OVER_{FormatLine(extractedLine.Value)}"
                : "OVER";
        }

        if (IsUnder(value))
        {
            return extractedLine.HasValue
                ? $"UNDER_{FormatLine(extractedLine.Value)}"
                : "UNDER";
        }

        return value;
    }

    public static decimal? TryParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return decimal.TryParse(line.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static decimal? ExtractLine(string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return null;
        }

        var match = Regex.Match(option, @"(?<!\d)(\d+(?:\.\d+)?)(?!\d)", RegexOptions.CultureInvariant);
        return match.Success
            && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static decimal? ExtractLineFromLineOption(string? option)
    {
        var value = NormalizeToken(option);
        return value is not null && (IsOver(value) || IsUnder(value))
            ? ExtractLine(option)
            : null;
    }

    private static bool IsOver(string value) =>
        value == "OV" || value.StartsWith("OV", StringComparison.Ordinal) || value.StartsWith("OVER", StringComparison.Ordinal);

    private static bool IsUnder(string value) =>
        value == "UN" || value.StartsWith("UN", StringComparison.Ordinal) || value.StartsWith("UNDER", StringComparison.Ordinal);

    private static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim()
            .ToUpperInvariant()
            .Replace(" ", "_", StringComparison.Ordinal);
    }

    private static string FormatLine(decimal line) =>
        line.ToString("0.######", CultureInfo.InvariantCulture);
}
