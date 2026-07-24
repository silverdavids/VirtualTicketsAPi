namespace VirtualTickets.Api.Contracts;

public sealed record TicketPayoutLookupRequest(string? TicketNumber);

public sealed record TicketPayoutRequest(
    string? TicketNumber,
    string? ConfirmationReference = null);

public sealed record TicketPayoutLookupResponse(
    long ReceiptId,
    string TicketNumber,
    DateTimeOffset PlacedAt,
    decimal Stake,
    decimal TotalOdds,
    decimal PossibleWin,
    decimal PayableAmount,
    string Currency,
    string Status,
    bool CanPayout,
    string? CannotPayoutReason,
    DateTimeOffset? PaidAt,
    string? PayoutReference,
    bool CanCancel,
    string? CannotCancelReason);

public sealed record TicketPayoutResponse(
    long ReceiptId,
    string TicketNumber,
    decimal PaidAmount,
    string Currency,
    DateTimeOffset PaidAt,
    string PayoutReference,
    string Status);

public sealed record TicketPayoutError(
    string Code,
    string Message,
    string? TicketNumber = null);
