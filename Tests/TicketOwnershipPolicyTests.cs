using VirtualTickets.Api.Services;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class TicketOwnershipPolicyTests
{
    [Fact]
    public void Resolves_designated_account_for_terminal_branch()
    {
        var result = TicketOwnershipPolicy.Resolve(2, 2, "shop2-user-id", "shop2-user-id");

        Assert.True(result.IsResolved);
        Assert.Equal(2, result.BranchId);
        Assert.Equal("shop2-user-id", result.UserId);
    }

    [Fact]
    public void Rejects_branch_without_designated_account()
    {
        var result = TicketOwnershipPolicy.Resolve(7, 7, null, null);

        Assert.False(result.IsResolved);
        Assert.Equal("shop_ticket_account_not_configured", result.ErrorCode);
    }

    [Fact]
    public void Rejects_designated_account_that_does_not_verify_in_branch()
    {
        var result = TicketOwnershipPolicy.Resolve(7, 7, "branch-2-user", null);

        Assert.False(result.IsResolved);
        Assert.Equal("shop_ticket_account_invalid", result.ErrorCode);
    }

    [Fact]
    public void Resolves_new_branch_to_its_own_designated_account()
    {
        const string branch7UserId = "branch-7-user";

        var result = TicketOwnershipPolicy.Resolve(7, 7, branch7UserId, branch7UserId);

        Assert.True(result.IsResolved);
        Assert.Equal(7, result.BranchId);
        Assert.Equal(branch7UserId, result.UserId);
        Assert.NotEqual("shop2-user-id", result.UserId);
    }

    [Fact]
    public void Rejects_stale_terminal_branch_claim()
    {
        var result = TicketOwnershipPolicy.Resolve(2, 7, "branch-7-user", "branch-7-user");

        Assert.False(result.IsResolved);
        Assert.Equal("terminal_identity_invalid", result.ErrorCode);
    }
}
