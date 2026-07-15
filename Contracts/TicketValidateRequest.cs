namespace VirtualTickets.Api.Contracts;

public sealed class TicketValidateRequest
{
    public string? Source { get; set; }
    public string? Provider { get; set; }
    public string? ProviderEventId { get; set; }
    public string? ExternalTicketId { get; set; }
    public string? SourceDisplayId { get; set; }
    public string? ShopCode { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public decimal Stake { get; set; }
    public List<TicketSelectionRequest> Selections { get; set; } = [];
}
