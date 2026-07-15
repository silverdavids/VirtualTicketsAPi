namespace VirtualTickets.Api.Contracts;

public sealed record VirtualTicketDetailsResponse(
    VirtualTicketReceiptDetail Receipt,
    IReadOnlyCollection<VirtualTicketBetDetail> Bets);

public sealed record VirtualTicketReceiptDetail
{
    public long ReceiptId { get; init; }
    public string? SerialCode { get; init; }
    public DateTime? ReceiptDate { get; init; }
    public decimal? Stake { get; init; }
    public decimal? TotalOdds { get; init; }
    public decimal? PossibleWin { get; init; }
    public int? ReceiptStatus { get; init; }
    public int? SetSize { get; init; }
    public int? SubmitedSize { get; init; }
    public int? WonSize { get; init; }
    public decimal? AmountPaid { get; init; }
    public DateTime? DateSettled { get; init; }
    public string? PaymentSource { get; init; }
}

public sealed record VirtualTicketBetDetail
{
    public long BetId { get; init; }
    public long? MatchId { get; init; }
    public string? HomeTeam { get; init; }
    public string? AwayTeam { get; init; }
    public string? League { get; init; }
    public DateTime? StartTime { get; init; }
    public string? Market { get; init; }
    public string? Option { get; init; }
    public string? Line { get; init; }
    public decimal? BetOdd { get; init; }
    public int? GameBetStatus { get; init; }
    public int? HomeScore { get; init; }
    public int? AwayScore { get; init; }
    public string? MatchStatus { get; init; }
}
