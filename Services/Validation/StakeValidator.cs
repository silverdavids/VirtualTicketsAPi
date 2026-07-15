using VirtualTickets.Api.Contracts;

namespace VirtualTickets.Api.Services.Validation;

public sealed class StakeValidator
{
    public void Validate(TicketValidateRequest request, TicketValidateResponse response)
    {
        if (request.Stake > 0)
        {
            return;
        }

        response.Errors.Add(new TicketValidationError
        {
            Code = "stake_must_be_positive",
            Field = "stake",
            Message = "Stake must be greater than zero."
        });
    }
}
