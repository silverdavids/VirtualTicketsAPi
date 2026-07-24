using VirtualTickets.Api.Services;
using VirtualTickets.Api.Data;
using System.Text;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class PayoutAuthorizationTests
{
    [Fact]
    public void Valid_configured_user_authorizes_payout_without_shift()
    {
        const string configuredUserId = "A31807FD-D127-4558-A2E7-592C98F9C482";
        var userId = PayoutUserAuthorization.RequireConfigured(Guid.Parse(configuredUserId));

        Assert.Equal(configuredUserId.ToLowerInvariant(), PayoutUserAuthorization.RequireActive(userId, true));
    }

    [Fact]
    public void Missing_payout_user_returns_stable_error()
    {
        var error = Assert.Throws<TicketPayoutException>(
            () => PayoutUserAuthorization.RequireConfigured(null));

        Assert.Equal("PayoutUserNotConfigured", error.Code);
        Assert.Equal("No valid virtual-ticket payout user is configured.", error.Message);
        Assert.Equal(500, error.StatusCode);
    }

    [Fact]
    public void Empty_payout_user_is_rejected()
    {
        var error = Assert.Throws<TicketPayoutException>(
            () => PayoutUserAuthorization.RequireConfigured(Guid.Empty));

        Assert.Equal("PayoutUserNotConfigured", error.Code);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("A31807FD-D127-4558-A2E7")]
    public void Malformed_payout_user_configuration_is_rejected(string configuredUserId)
    {
        var parsedUserId = VirtualTicketPayoutOptions.ParsePayoutUserId(configuredUserId);
        var error = Assert.Throws<TicketPayoutException>(
            () => PayoutUserAuthorization.RequireConfigured(parsedUserId));

        Assert.Equal("PayoutUserNotConfigured", error.Code);
    }

    [Fact]
    public void Nonexistent_or_inactive_payout_user_is_rejected()
    {
        var error = Assert.Throws<TicketPayoutException>(
            () => PayoutUserAuthorization.RequireActive(
                "a31807fd-d127-4558-a2e7-592c98f9c482",
                false));

        Assert.Equal("PayoutUserNotConfigured", error.Code);
    }

    [Fact]
    public void Branch_mismatch_remains_rejected()
    {
        var reason = TicketPayoutEligibility.GetCannotPayoutReason(
            "VirtualDisplay", 10, 20, false, ReceiptStatus.Won, true, true);

        Assert.Equal("WrongBranch", reason);
    }

    [Fact]
    public void Paid_ticket_remains_already_paid()
    {
        var reason = TicketPayoutEligibility.GetCannotPayoutReason(
            "VirtualDisplay", 10, 10, false, ReceiptStatus.Paid, true, true);

        Assert.Equal("AlreadyPaid", reason);
    }

    [Theory]
    [InlineData(ReceiptStatus.Pending, false, false, "Pending")]
    [InlineData(ReceiptStatus.Lost, true, false, "Lost")]
    public void Pending_and_lost_tickets_remain_rejected(
        ReceiptStatus status,
        bool allSelectionsSettled,
        bool allSelectionsWon,
        string expectedReason)
    {
        var reason = TicketPayoutEligibility.GetCannotPayoutReason(
            "VirtualDisplay", 10, 10, false, status, allSelectionsSettled, allSelectionsWon);

        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Winning_status_still_requires_every_bet_to_be_won()
    {
        var reason = TicketPayoutEligibility.GetCannotPayoutReason(
            "VirtualDisplay", 10, 10, false, ReceiptStatus.Won, true, false);

        Assert.Equal("Blocked", reason);
    }

    [Fact]
    public void Compiled_payout_implementation_contains_no_shift_query_or_failure_message()
    {
        var assemblyBytes = File.ReadAllBytes(typeof(TicketPayoutService).Assembly.Location);
        var compiledStrings = Encoding.Unicode.GetString(assemblyBytes);

        Assert.DoesNotContain("dbo.Shifts", compiledStrings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active user shift", compiledStrings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shift user not found", compiledStrings, StringComparison.OrdinalIgnoreCase);
    }
}
