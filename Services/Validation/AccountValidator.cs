using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services.Validation;

public sealed class AccountValidator
{
    private readonly TicketDb _ticketDb;

    public AccountValidator(TicketDb ticketDb)
    {
        _ticketDb = ticketDb;
    }

    public async Task ValidateAsync(TicketValidateRequest request, TicketValidateResponse response, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.UserId) || !string.IsNullOrWhiteSpace(request.Username))
        {
            var account = await _ticketDb.AccountExistsAsync(request.UserId, request.Username, cancellationToken);
            AddProbeResult(response, account, "account", "account_not_found", "account_schema_unknown", "The supplied account or user was not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.ShopCode))
        {
            var shop = await _ticketDb.ShopExistsAsync(request.ShopCode, cancellationToken);
            AddProbeResult(response, shop, "shopCode", "shop_not_found", "shop_schema_unknown", "The supplied branch or shop was not found.");
        }
    }

    private static void AddProbeResult(
        TicketValidateResponse response,
        ProbeResult result,
        string field,
        string notFoundCode,
        string unknownCode,
        string notFoundMessage)
    {
        response.Checks[field] = result.IsUnknown ? "unknown" : result.IsFound ? "found" : "not_found";
        if (result.IsFound)
        {
            return;
        }

        response.Errors.Add(new TicketValidationError
        {
            Code = result.IsUnknown ? unknownCode : notFoundCode,
            Field = field,
            Message = result.IsUnknown ? result.Detail ?? "The database schema could not be verified." : notFoundMessage
        });
    }
}
