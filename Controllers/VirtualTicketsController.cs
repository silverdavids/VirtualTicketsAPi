using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;
using VirtualTickets.Api.Services;
using System.Security.Claims;

namespace VirtualTickets.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/virtual-tickets")]
public sealed class VirtualTicketsController : ControllerBase
{
    private readonly VirtualTicketDb _virtualTicketDb;
    private readonly ILogger<VirtualTicketsController> _logger;

    public VirtualTicketsController(
        VirtualTicketDb virtualTicketDb,
        ILogger<VirtualTicketsController> logger)
    {
        _virtualTicketDb = virtualTicketDb;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<VirtualTicketListItem>>> List(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? status,
        [FromQuery] string? userId,
        [FromQuery] int? branchId,
        CancellationToken cancellationToken)
    {
        try
        {
            var scope = await ResolveTerminalScopeAsync(cancellationToken);
            if (IsDisplayTerminal() && scope is null)
            {
                return Forbid();
            }

            var queryScope = VirtualTicketAccessScopePolicy.Apply(scope, userId, branchId);

            var tickets = await _virtualTicketDb.GetTicketsAsync(
                from,
                to,
                status,
                queryScope.UserId,
                queryScope.BranchId,
                cancellationToken);

            return Ok(tickets);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            _logger.LogError(exception, "[virtual-tickets-api] Failed to list virtual tickets.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Virtual tickets could not be loaded."
            });
        }
    }

    [HttpGet("{receiptId:long}")]
    public async Task<ActionResult<VirtualTicketDetailsResponse>> Details(
        [FromRoute] long receiptId,
        CancellationToken cancellationToken)
    {
        try
        {
            var scope = await ResolveTerminalScopeAsync(cancellationToken);
            if (IsDisplayTerminal() && scope is null)
            {
                return Forbid();
            }

            var details = await _virtualTicketDb.GetTicketDetailsAsync(
                receiptId, scope?.UserId, scope?.BranchId, cancellationToken);
            return details is null
                ? NotFound()
                : Ok(details);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            _logger.LogError(exception, "[virtual-tickets-api] Failed to load virtual ticket {ReceiptId}.", receiptId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Virtual ticket details could not be loaded."
            });
        }
    }

    private bool IsDisplayTerminal() =>
        User.FindFirstValue("auth_type") == "display_terminal";

    private async Task<TerminalVirtualTicketScope?> ResolveTerminalScopeAsync(
        CancellationToken cancellationToken)
    {
        if (!IsDisplayTerminal())
        {
            return null;
        }

        if (!int.TryParse(User.FindFirstValue("terminal_id"), out var terminalId)
            || !int.TryParse(User.FindFirstValue("branch_id"), out var branchId)
            || string.IsNullOrWhiteSpace(User.FindFirstValue("terminal_code")))
        {
            return null;
        }

        return await _virtualTicketDb.ResolveTerminalScopeAsync(
            new TerminalTicketIdentity(terminalId, User.FindFirstValue("terminal_code")!, branchId),
            cancellationToken);
    }
}
