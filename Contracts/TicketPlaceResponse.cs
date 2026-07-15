namespace VirtualTickets.Api.Contracts;

public sealed class TicketPlaceResponse
{
    public bool IsPlaced { get; set; }
    public int? ReceiptId { get; set; }
    public Guid? Serial { get; set; }
    public long? ActiveSetNo { get; set; }
    public List<PlacedBetResponse> Bets { get; set; } = [];
    public List<TicketValidationError> Errors { get; set; } = [];
    public List<TicketValidationWarning> Warnings { get; set; } = [];
    public Dictionary<string, string> Checks { get; set; } = [];
}

public sealed class PlacedBetResponse
{
    public int BetId { get; set; }
    public long MatchId { get; set; }
    public decimal Odd { get; set; }
}
