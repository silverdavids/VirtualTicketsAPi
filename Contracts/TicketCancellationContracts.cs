namespace VirtualTickets.Api.Contracts;

public sealed record TicketCancelRequest(
    string? TicketNumber,
    string? ConfirmationReference = null,
    string? Reason = null);

public sealed record TicketCancelResponse(
    long ReceiptId,
    string TicketNumber,
    string Status,
    DateTimeOffset CancelledAt,
    string CancelReference);
