using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Services;

namespace VirtualTickets.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/tickets")]
public sealed class TicketsController : ControllerBase
{
    private readonly TicketApplicationService _ticketApplicationService;
    private readonly TicketPayoutService _ticketPayoutService;

    public TicketsController(
        TicketApplicationService ticketApplicationService,
        TicketPayoutService ticketPayoutService)
    {
        _ticketApplicationService = ticketApplicationService;
        _ticketPayoutService = ticketPayoutService;
    }

    [HttpPost("validate")]
    public async Task<ActionResult<TicketValidateResponse>> Validate(
        [FromBody] TicketValidateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _ticketApplicationService.ValidateAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("place")]
    public async Task<ActionResult<TicketPlaceResponse>> Place(
        [FromBody] TicketValidateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _ticketApplicationService.PlaceAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("payout/lookup")]
    public async Task<ActionResult<TicketPayoutLookupResponse>> LookupPayout(
        [FromBody] TicketPayoutLookupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _ticketPayoutService.LookupAsync(request, cancellationToken));
        }
        catch (TicketPayoutException exception)
        {
            return StatusCode(exception.StatusCode, new TicketPayoutError(
                exception.Code, exception.Message, exception.TicketNumber));
        }
    }

    [HttpPost("payout")]
    public async Task<ActionResult<TicketPayoutResponse>> Payout(
        [FromBody] TicketPayoutRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _ticketPayoutService.PayoutAsync(request, cancellationToken));
        }
        catch (TicketPayoutException exception)
        {
            return StatusCode(exception.StatusCode, new TicketPayoutError(
                exception.Code, exception.Message, exception.TicketNumber));
        }
    }
}
