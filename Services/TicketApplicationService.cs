using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;
using VirtualTickets.Api.Services.Validation;

namespace VirtualTickets.Api.Services;

public sealed class TicketApplicationService
{
    private readonly TicketDb _ticketDb;
    private readonly StakeValidator _stakeValidator;
    private readonly AccountValidator _accountValidator;
    private readonly OddsValidator _oddsValidator;
    private readonly SetValidator _setValidator;

    public TicketApplicationService(
        TicketDb ticketDb,
        StakeValidator stakeValidator,
        AccountValidator accountValidator,
        OddsValidator oddsValidator,
        SetValidator setValidator)
    {
        _ticketDb = ticketDb;
        _stakeValidator = stakeValidator;
        _accountValidator = accountValidator;
        _oddsValidator = oddsValidator;
        _setValidator = setValidator;
    }

    public async Task<TicketValidateResponse> ValidateAsync(TicketValidateRequest request, CancellationToken cancellationToken)
    {
        var response = new TicketValidateResponse();

        Required(response, request.Source, "source", "source_required", "Source is required.");
        Required(response, request.Provider, "provider", "provider_required", "Provider is required.");
        Required(response, request.ExternalTicketId, "externalTicketId", "external_ticket_id_required", "External ticket id is required.");

        _stakeValidator.Validate(request, response);
        ValidateSelections(request, response);

        var connectionResult = await _ticketDb.CanConnectAsync(cancellationToken);
        response.Checks["database"] = connectionResult.CanConnect ? "reachable" : "unreachable";
        if (!connectionResult.CanConnect)
        {
            response.Errors.Add(new TicketValidationError
            {
                Code = connectionResult.Code ?? "database_unreachable",
                Field = "database",
                Message = connectionResult.Message ?? "The database could not be reached."
            });

            return response;
        }

        var referenceDataState = await _ticketDb.GetReferenceDataStateAsync(cancellationToken);
        response.Checks["schema"] = referenceDataState.SchemaValid ? "valid" : "invalid";
        response.Checks["referenceData"] = referenceDataState.IsEmpty ? "empty" : "available";
        if (!referenceDataState.SchemaValid)
        {
            response.Errors.Add(new TicketValidationError
            {
                Code = "schema_invalid",
                Field = "schema",
                Message = $"Required business schema is missing: {string.Join(", ", referenceDataState.MissingTables)}."
            });

            return response;
        }

        if (referenceDataState.IsEmpty)
        {
            response.Warnings.Add(new TicketValidationWarning
            {
                Code = "reference_data_empty",
                Field = "referenceData",
                Message = "Development database contains no reference data."
            });

            return response;
        }

        await _setValidator.ValidateAsync(response, cancellationToken);
        await _accountValidator.ValidateAsync(request, response, cancellationToken);
        await _oddsValidator.ValidateAsync(request, response, cancellationToken);

        return response;
    }

    public async Task<TicketPlaceResponse> PlaceAsync(TicketValidateRequest request, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(request, cancellationToken);
        var response = new TicketPlaceResponse
        {
            ActiveSetNo = validation.ActiveSetNo,
            Checks = new Dictionary<string, string>(validation.Checks),
            Errors = [.. validation.Errors],
            Warnings = [.. validation.Warnings]
        };

        if (validation.Errors.Count > 0)
        {
            response.Checks["place"] = "blocked_by_validation";
            return response;
        }

        if (validation.Checks.TryGetValue("referenceData", out var referenceData) && referenceData == "empty")
        {
            response.Errors.Add(new TicketValidationError
            {
                Code = "reference_data_empty",
                Field = "referenceData",
                Message = "Ticket placement requires seeded business reference data."
            });
            response.Checks["place"] = "blocked_by_reference_data";
            return response;
        }

        if (!validation.ActiveSetNo.HasValue)
        {
            response.Errors.Add(new TicketValidationError
            {
                Code = "active_set_required",
                Field = "activeSet",
                Message = "Ticket placement requires an active set."
            });
            response.Checks["place"] = "blocked_by_active_set";
            return response;
        }

        var placeResult = await _ticketDb.PlaceTicketAsync(request, validation.ActiveSetNo.Value, cancellationToken);
        if (!placeResult.IsPlaced)
        {
            response.Errors.AddRange(placeResult.Errors);
            response.Checks["place"] = "failed";
            return response;
        }

        response.IsPlaced = true;
        response.ReceiptId = placeResult.ReceiptId;
        response.Serial = placeResult.Serial;
        response.Bets = placeResult.Bets;
        response.Checks["place"] = "placed";

        return response;
    }

    private static void ValidateSelections(TicketValidateRequest request, TicketValidateResponse response)
    {
        if (request.Selections.Count == 0)
        {
            response.Errors.Add(new TicketValidationError
            {
                Code = "selection_required",
                Field = "selections",
                Message = "At least one selection is required."
            });
        }

        if (request.Selections.Count > 25)
        {
            response.Errors.Add(new TicketValidationError
            {
                Code = "selection_limit_exceeded",
                Field = "selections",
                Message = "A maximum of 25 selections is allowed."
            });
        }
    }

    private static void Required(
        TicketValidateResponse response,
        string? value,
        string field,
        string code,
        string message)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        response.Errors.Add(new TicketValidationError
        {
            Code = code,
            Field = field,
            Message = message
        });
    }
}
