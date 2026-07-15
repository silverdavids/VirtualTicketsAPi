namespace VirtualTickets.Api.Contracts;

public sealed class TicketSelectionRequest
{
    public string? ProviderMatchId { get; set; }
    public long? MatchId { get; set; }
    public long? MatchOddId { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public string? Market { get; set; }
    public string? Option { get; set; }
    public decimal? Line { get; set; }
    public decimal Odd { get; set; }
    public string? ShortCode { get; set; }
}
