namespace VirtualTickets.Api.Services;

public sealed class TicketPayoutException : Exception
{
    public TicketPayoutException(int statusCode, string code, string message, string? ticketNumber = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        TicketNumber = ticketNumber;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public string? TicketNumber { get; }
}
