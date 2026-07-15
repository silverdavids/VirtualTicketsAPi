namespace VirtualTickets.Api.Contracts;

public sealed class TicketValidateResponse
{
    public bool IsValid => Errors.Count == 0;
    public long? ActiveSetNo { get; set; }
    public List<TicketValidationError> Errors { get; set; } = [];
    public List<TicketValidationWarning> Warnings { get; set; } = [];
    public Dictionary<string, string> Checks { get; set; } = [];
}

public sealed class TicketValidationError
{
    public required string Code { get; set; }
    public required string Field { get; set; }
    public required string Message { get; set; }
}

public sealed class TicketValidationWarning
{
    public required string Code { get; set; }
    public required string Field { get; set; }
    public required string Message { get; set; }
}
