namespace VirtualTickets.Api.Data;

public enum ReceiptStatus
{
    Blocked = -2,
    Cancelled = -1,
    Inactive = 0,
    Pending = 1,
    Lost = 2,
    Won = 3,
    Paid = 4
}
