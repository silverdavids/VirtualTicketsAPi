using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services.Validation;

public sealed class SetValidator
{
    private readonly TicketDb _ticketDb;

    public SetValidator(TicketDb ticketDb)
    {
        _ticketDb = ticketDb;
    }

    public async Task ValidateAsync(TicketValidateResponse response, CancellationToken cancellationToken)
    {
        var result = await _ticketDb.GetActiveSetAsync(cancellationToken);
        response.Checks["activeSet"] = result.IsFound ? "found" : "not_found";

        if (result.IsFound)
        {
            response.ActiveSetNo = result.SetNo;
            return;
        }

        response.Errors.Add(new TicketValidationError
        {
            Code = "active_set_not_found",
            Field = "activeSet",
            Message = "No active set exists."
        });
    }
}
