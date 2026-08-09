namespace VirtualTickets.Api.Services;

public sealed record TerminalTicketIdentity(int TerminalId, string TerminalCode, int BranchId);

public sealed record TerminalTicketOwnership(
    bool IsResolved,
    int? BranchId,
    string? UserId,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static TerminalTicketOwnership Resolved(int branchId, string userId) =>
        new(true, branchId, userId, null, null);

    public static TerminalTicketOwnership InvalidTerminal() =>
        new(false, null, null, "terminal_identity_invalid", "Authenticated terminal is no longer valid.");

    public static TerminalTicketOwnership ConfigurationMissing() =>
        new(false, null, null, "shop_ticket_account_not_configured", "Shop has no ticket sales account configured.");

    public static TerminalTicketOwnership ConfigurationInvalid() =>
        new(false, null, null, "shop_ticket_account_invalid", "Shop ticket sales account is invalid or belongs to another branch.");
}

public static class TicketOwnershipPolicy
{
    public static TerminalTicketOwnership Resolve(
        int claimedBranchId,
        int databaseBranchId,
        string? configuredUserId,
        string? verifiedUserId)
    {
        if (claimedBranchId != databaseBranchId)
        {
            return TerminalTicketOwnership.InvalidTerminal();
        }

        if (string.IsNullOrWhiteSpace(configuredUserId))
        {
            return TerminalTicketOwnership.ConfigurationMissing();
        }

        if (string.IsNullOrWhiteSpace(verifiedUserId)
            || !string.Equals(configuredUserId, verifiedUserId, StringComparison.OrdinalIgnoreCase))
        {
            return TerminalTicketOwnership.ConfigurationInvalid();
        }

        return TerminalTicketOwnership.Resolved(databaseBranchId, verifiedUserId);
    }
}
