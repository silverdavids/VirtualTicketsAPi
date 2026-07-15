namespace VirtualTickets.Api.Contracts;

public sealed record VirtualTicketListItem
{
    public long ReceiptId { get; init; }
    public string? SerialCode { get; init; }
    public DateTime? ReceiptDate { get; init; }
    public string? UserId { get; init; }
    public int? BranchId { get; init; }
    public decimal? Stake { get; init; }
    public decimal? TotalOdds { get; init; }
    public decimal? PossibleWin { get; init; }
    public int? ReceiptStatus { get; init; }
    public int? SetSize { get; init; }
    public int? SubmitedSize { get; init; }
    public int? WonSize { get; init; }
    public bool? IsCanceled { get; init; }
    public bool? IsLive { get; init; }
    public string? PaymentSource { get; init; }
}
