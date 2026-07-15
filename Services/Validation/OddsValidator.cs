using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services.Validation;

public sealed class OddsValidator
{
    private readonly TicketDb _ticketDb;

    public OddsValidator(TicketDb ticketDb)
    {
        _ticketDb = ticketDb;
    }

    public async Task ValidateAsync(TicketValidateRequest request, TicketValidateResponse response, CancellationToken cancellationToken)
    {
        for (var index = 0; index < request.Selections.Count; index++)
        {
            var selection = request.Selections[index];
         if (!selection.MatchOddId.HasValue || selection.MatchOddId.Value <= 0)
       {
    continue;
     }
            var result = await _ticketDb.MatchOddMatchesAsync(selection.MatchOddId.Value, selection.MatchId, selection.Odd, cancellationToken);
            var field = $"selections[{index}].matchOddId";
            response.Checks[field] = result.IsUnknown ? "unknown" : result.IsFound ? "matched" : "not_matched";

            if (result.IsFound)
            {
                continue;
            }

            response.Errors.Add(new TicketValidationError
            {
                Code = result.IsUnknown ? "odds_schema_unknown" : "odds_mismatch",
                Field = field,
                Message = result.IsUnknown
                    ? result.Detail ?? "The odds schema could not be verified."
                    : "The supplied match odd id, match id, or odd did not match current database values."
            });
        }
    }
}
