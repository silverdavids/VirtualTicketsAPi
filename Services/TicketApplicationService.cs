using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;
using VirtualTickets.Api.Services.Validation;
using System.Security.Claims;

namespace VirtualTickets.Api.Services;

public sealed class TicketApplicationService
{
    private const int ExternalTicketIdMaxLength = 100;
    private readonly TicketDb _ticketDb;
    private readonly StakeValidator _stakeValidator;
    private readonly AccountValidator _accountValidator;
    private readonly OddsValidator _oddsValidator;
    private readonly SetValidator _setValidator;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TicketApplicationService(
        TicketDb ticketDb,
        StakeValidator stakeValidator,
        AccountValidator accountValidator,
        OddsValidator oddsValidator,
        SetValidator setValidator,
        IHttpContextAccessor httpContextAccessor)
    {
        _ticketDb = ticketDb;
        _stakeValidator = stakeValidator;
        _accountValidator = accountValidator;
        _oddsValidator = oddsValidator;
        _setValidator = setValidator;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<TicketValidateResponse> ValidateAsync(TicketValidateRequest request, CancellationToken cancellationToken)
    {
        var response = new TicketValidateResponse();
        var terminalIdentity = ResolveTerminalIdentity();

        Required(response, request.Source, "source", "source_required", "Source is required.");
        Required(response, request.Provider, "provider", "provider_required", "Provider is required.");
        if (terminalIdentity is not null)
        {
            Required(response, request.ExternalTicketId, "externalTicketId", "external_ticket_id_required", "External ticket id is required.");
            if (request.ExternalTicketId?.Length > ExternalTicketIdMaxLength)
            {
                response.Errors.Add(new TicketValidationError
                {
                    Code = "external_ticket_id_too_long",
                    Field = "externalTicketId",
                    Message = $"External ticket id cannot exceed {ExternalTicketIdMaxLength} characters."
                });
            }
        }

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
        if (terminalIdentity is null)
        {
            await _accountValidator.ValidateAsync(request, response, cancellationToken);
        }
        if (terminalIdentity is null)
        {
            await _oddsValidator.ValidateAsync(request, response, cancellationToken);
        }
        else
        {
            response.Checks["odds"] = "deferred_to_authoritative_placement_transaction";
        }

        return response;
    }

    public async Task<TicketPlaceResponse> PlaceAsync(TicketValidateRequest request, CancellationToken cancellationToken)
    {
        var terminalIdentity = ResolveTerminalIdentity();
        if (terminalIdentity is not null
            && !string.IsNullOrWhiteSpace(request.ExternalTicketId)
            && request.ExternalTicketId.Length <= ExternalTicketIdMaxLength)
        {
            var existing = await _ticketDb.FindExistingTerminalPlacementAsync(
                terminalIdentity, request.ExternalTicketId, cancellationToken);
            if (existing is not null)
            {
                return ToRetryResponse(existing);
            }
        }

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

        var placeResult = await _ticketDb.PlaceTicketAsync(
            request,
            validation.ActiveSetNo.Value,
            terminalIdentity,
            cancellationToken);
        if (!placeResult.IsPlaced)
        {
            response.Errors.AddRange(placeResult.Errors);
            response.Checks["place"] = "failed";
            return response;
        }

        response.IsPlaced = true;
        response.ReceiptId = placeResult.ReceiptId;
        response.InternalSerial = placeResult.Serial;
        response.TicketNumber = placeResult.TicketNumber;
        response.ShopDisplayName = placeResult.ShopDisplayName;
        response.BookedAtUtc = placeResult.BookedAtUtc;
        response.Bets = placeResult.Bets;
        response.Checks["place"] = "placed";

        return response;
    }

    private static TicketPlaceResponse ToRetryResponse(TicketPlaceResult existing) => new()
    {
        IsPlaced = true,
        ReceiptId = existing.ReceiptId,
        InternalSerial = existing.Serial,
        TicketNumber = existing.TicketNumber,
        ShopDisplayName = existing.ShopDisplayName,
        BookedAtUtc = existing.BookedAtUtc,
        ActiveSetNo = existing.ActiveSetNo,
        Bets = existing.Bets,
        Checks = new Dictionary<string, string> { ["place"] = "idempotent_retry" }
    };

    public static bool IsConflictError(string code) => code is
        "board_changed" or "board_expired" or "selection_not_available" or "odds_changed";

    private TerminalTicketIdentity? ResolveTerminalIdentity()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.FindFirstValue("auth_type") != "display_terminal")
        {
            return null;
        }

        return int.TryParse(user.FindFirstValue("terminal_id"), out var terminalId)
            && int.TryParse(user.FindFirstValue("branch_id"), out var branchId)
            && !string.IsNullOrWhiteSpace(user.FindFirstValue("terminal_code"))
            ? new TerminalTicketIdentity(terminalId, user.FindFirstValue("terminal_code")!, branchId)
            : throw new InvalidOperationException("Authenticated display terminal claims are incomplete.");
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
