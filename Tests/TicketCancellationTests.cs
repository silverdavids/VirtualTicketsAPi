using VirtualTickets.Api.Data;
using VirtualTickets.Api.Services;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class TicketCancellationTests
{
    [Fact]
    public void Pending_unsettled_ticket_before_event_can_be_cancelled()
    {
        var reason = Evaluate(ReceiptStatus.Pending, noSelectionsSettled: true, eventStarted: false);

        Assert.Null(reason);
    }

    [Theory]
    [InlineData(ReceiptStatus.Cancelled, "AlreadyCancelled")]
    [InlineData(ReceiptStatus.Paid, "AlreadyPaid")]
    [InlineData(ReceiptStatus.Won, "CannotCancelWonTicket")]
    [InlineData(ReceiptStatus.Lost, "CannotCancelSettledTicket")]
    [InlineData(ReceiptStatus.Blocked, "Blocked")]
    public void Final_or_blocked_status_cannot_be_cancelled(
        ReceiptStatus status,
        string expectedReason)
    {
        Assert.Equal(expectedReason, Evaluate(status, true, false));
    }

    [Fact]
    public void Started_event_cannot_be_cancelled()
    {
        Assert.Equal("EventStarted", Evaluate(ReceiptStatus.Pending, true, true));
    }

    [Fact]
    public void Partially_settled_ticket_cannot_be_cancelled()
    {
        Assert.Equal(
            "CannotCancelSettledTicket",
            Evaluate(ReceiptStatus.Pending, noSelectionsSettled: false, eventStarted: false));
    }

    [Fact]
    public void Receipt_branch_must_match_terminal_branch()
    {
        var reason = TicketCancellationEligibility.GetCannotCancelReason(
            "VirtualDisplay",
            receiptBranchId: 10,
            terminalBranchId: 20,
            isCanceled: false,
            ReceiptStatus.Pending,
            noSelectionsSettled: true,
            eventStarted: false);

        Assert.Equal("WrongBranch", reason);
    }

    [Fact]
    public void Cancellation_is_limited_to_virtual_display_tickets()
    {
        var reason = TicketCancellationEligibility.GetCannotCancelReason(
            "Retail",
            receiptBranchId: 10,
            terminalBranchId: 10,
            isCanceled: false,
            ReceiptStatus.Pending,
            noSelectionsSettled: true,
            eventStarted: false);

        Assert.Equal("NotVirtualTicket", reason);
    }

    [Fact]
    public void IsCanceled_flag_prevents_duplicate_cancellation()
    {
        var reason = TicketCancellationEligibility.GetCannotCancelReason(
            "VirtualDisplay",
            receiptBranchId: 10,
            terminalBranchId: 10,
            isCanceled: true,
            ReceiptStatus.Pending,
            noSelectionsSettled: true,
            eventStarted: false);

        Assert.Equal("AlreadyCancelled", reason);
    }

    private static string? Evaluate(
        ReceiptStatus status,
        bool noSelectionsSettled,
        bool eventStarted) =>
        TicketCancellationEligibility.GetCannotCancelReason(
            "VirtualDisplay",
            receiptBranchId: 10,
            terminalBranchId: 10,
            isCanceled: false,
            status,
            noSelectionsSettled,
            eventStarted);
}
