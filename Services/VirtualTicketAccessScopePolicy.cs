using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services;

public static class VirtualTicketAccessScopePolicy
{
    public static VirtualTicketQueryScope Apply(
        TerminalVirtualTicketScope? terminalScope,
        string? requestedUserId,
        int? requestedBranchId) =>
        terminalScope is null
            ? new(requestedUserId, requestedBranchId)
            : new(terminalScope.UserId, terminalScope.BranchId);
}

public sealed record VirtualTicketQueryScope(string? UserId, int? BranchId);
