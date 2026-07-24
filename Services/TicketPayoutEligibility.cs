using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services;

public static class TicketPayoutEligibility
{
    public static string? GetCannotPayoutReason(
        string? paymentSource,
        int receiptBranchId,
        int terminalBranchId,
        bool isCanceled,
        ReceiptStatus status,
        bool allSelectionsSettled,
        bool allSelectionsWon)
    {
        if (!string.Equals(paymentSource, "VirtualDisplay", StringComparison.OrdinalIgnoreCase))
            return "NotVirtualTicket";
        if (receiptBranchId != terminalBranchId)
            return "WrongBranch";
        if (isCanceled || status == ReceiptStatus.Cancelled)
            return "Cancelled";
        if (status == ReceiptStatus.Blocked)
            return "Blocked";
        if (status == ReceiptStatus.Paid)
            return "AlreadyPaid";
        if (!allSelectionsSettled || status is ReceiptStatus.Pending or ReceiptStatus.Inactive)
            return "Pending";
        if (status == ReceiptStatus.Lost)
            return "Lost";
        if (status == ReceiptStatus.Won && !allSelectionsWon)
            return "Blocked";
        return status == ReceiptStatus.Won ? null : "Blocked";
    }
}
