using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services;

public static class TicketCancellationEligibility
{
    public static string? GetCannotCancelReason(
        string? paymentSource,
        int receiptBranchId,
        int terminalBranchId,
        bool isCanceled,
        ReceiptStatus status,
        bool noSelectionsSettled,
        bool eventStarted)
    {
        if (!string.Equals(paymentSource, "VirtualDisplay", StringComparison.OrdinalIgnoreCase))
            return "NotVirtualTicket";
        if (receiptBranchId != terminalBranchId)
            return "WrongBranch";
        if (isCanceled || status == ReceiptStatus.Cancelled)
            return "AlreadyCancelled";
        if (status == ReceiptStatus.Paid)
            return "AlreadyPaid";
        if (status == ReceiptStatus.Won)
            return "CannotCancelWonTicket";
        if (status == ReceiptStatus.Lost)
            return "CannotCancelSettledTicket";
        if (status == ReceiptStatus.Blocked)
            return "Blocked";
        if (status != ReceiptStatus.Pending || !noSelectionsSettled)
            return "CannotCancelSettledTicket";
        if (eventStarted)
            return "EventStarted";
        return null;
    }
}
