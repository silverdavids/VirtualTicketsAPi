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

    public TicketsController(TicketApplicationService ticketApplicationService)
    {
        _ticketApplicationService = ticketApplicationService;
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
}
